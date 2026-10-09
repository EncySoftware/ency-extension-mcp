using EncyExtensionMcp;
using Xunit;

/**
 * A repository connected with connect_to_store, as the other tools meet it: publish_extension and
 * publish_status follow its publish_store.yml, and the template's route (update_extension,
 * publish_folder) refuses to plant its files there. Before, publish_extension knew only src/ and
 * publish.yml, so it refused a connected project at the root or lost its run, and update_extension
 * added a second workflow and Directory.Build files to it (review, 09.10.2026).
 */
public class ConnectedRepositoryTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "mcp-connected-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _log = new();

    private string Workflows => Path.Combine(_repo, ".github", "workflows");

    public ConnectedRepositoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Remotes(("origin", "https://github.com/acme/MyExt.git"));
    }

    public void Dispose() { try { Directory.Delete(_repo, true); } catch { } }

    private void Remotes(params (string Name, string Url)[] remotes) =>
        File.WriteAllText(Path.Combine(_repo, ".git", "config"),
            "[core]\n\tbare = false\n" + string.Concat(remotes.Select(r => $"[remote \"{r.Name}\"]\n\turl = {r.Url}\n")));

    /** The project at `rel` and, when `connected`, connect_to_store's workflow packing it. */
    private string Project(string rel, bool connected, string packageId = "Acme.MyExt")
    {
        string csproj = Path.Combine(_repo, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(csproj)!);
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0-windows</TargetFramework>\n"
                                  + $"    <PackageId>{packageId}</PackageId>\n  </PropertyGroup>\n  <ItemGroup>\n"
                                  + "    <PackageReference Include=\"EncySoftware.CAMAPI.SDK.Net\" Version=\"3.0.15\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(csproj)!, "MyExt.settings.json"), "{}");
        if (connected)
        {
            Directory.CreateDirectory(Workflows);
            File.WriteAllText(Path.Combine(Workflows, StoreWorkflow.FileName), StoreWorkflow.Render(rel, "10.0.x"));
        }
        return csproj;
    }

    private static ExtensionStoreTools Store(FakeProcessRunner proc, FakeStoreClient? store = null) =>
        new(proc, store ?? new FakeStoreClient(), new FakeStoreAuth { Token = null });

    private static FakeProcessRunner Push(string tag = "v1.0.0") => new FakeProcessRunner()
        .On("git status --porcelain", stdout: "")
        .On("git rev-parse", exit: 1)
        .On("git tag")
        .On("git push origin HEAD")
        .On($"git push origin {tag}")
        .On("gh run list", stdout: "[]");

    // ---- publish_extension and publish_status

    /** A connected project at the repository root: it used to be refused with "move it under src/, or connect_to_store". */
    [Fact]
    public async Task Publish_extension_tags_a_connected_repository_and_follows_its_workflow()
    {
        Project("MyExt.csproj", connected: true);
        var proc = Push();

        string answer = await Store(proc).PublishExtension("1.0.0", repoDir: _repo);

        Assert.StartsWith("Pushed v1.0.0", answer);
        Assert.Contains(proc.Calls, c => c == "git tag v1.0.0");
        Assert.Contains(proc.Calls, c => c.StartsWith("gh run list --workflow publish_store.yml "));
        Assert.DoesNotContain(proc.Calls, c => c.Contains("--workflow publish.yml"));
        // Nothing written into a project connect_to_store packs as it is.
        Assert.False(File.Exists(Path.Combine(_repo, "package.info.json")));
        Assert.False(File.Exists(Path.Combine(_repo, "src", "package.info.json")));
        Assert.DoesNotContain(proc.Calls, c => c.StartsWith("git add") || c.StartsWith("git commit"));
        Assert.DoesNotContain("names no category", answer);
    }

    [Fact]
    public async Task Publish_extension_in_a_subfolder_of_a_connected_repository_follows_its_workflow_too()
    {
        Project("project/main/MyExt.csproj", connected: true);
        var proc = Push();

        string answer = await Store(proc).PublishExtension("1.0.0", repoDir: Path.Combine(_repo, "project", "main"));

        Assert.StartsWith("Pushed v1.0.0", answer);
        Assert.Contains(proc.Calls, c => c.StartsWith("gh run list --workflow publish_store.yml "));
    }

    /** The category is a field of the template's manifest; here it is a tag in the csproj — said, not half-done. */
    [Fact]
    public async Task A_category_for_a_connected_repository_is_explained_and_nothing_happens()
    {
        Project("MyExt.csproj", connected: true);
        var proc = Push();

        string answer = await Store(proc).PublishExtension("1.0.0", repoDir: _repo, category: "Operation");

        Assert.StartsWith("ERROR: this repository publishes through .github/workflows/publish_store.yml", answer);
        Assert.Contains("category:operation to <PackageTags>", answer);
        Assert.Contains("Known: other, analyzer, operation.", answer);
        Assert.Empty(proc.Calls);
    }

    [Fact]
    public async Task Publish_status_of_a_connected_repository_reads_its_workflow_and_its_csproj()
    {
        Project("project/main/MyExt.csproj", connected: true);
        var none = new FakeProcessRunner().On("gh run list", stdout: "[]");

        string waiting = await Store(none).PublishStatus(_repo);

        Assert.StartsWith("No run of publish_store.yml found yet", waiting);
        Assert.Contains(none.Calls, c => c.StartsWith("gh run list --workflow publish_store.yml "));

        var done = new FakeProcessRunner().On("gh run list",
            stdout: "[{\"databaseId\":9,\"status\":\"completed\",\"conclusion\":\"success\",\"url\":\"https://gh/run/9\"}]");
        var store = new FakeStoreClient { Card = new StoreCard("acme-myext", Approved: true, Unlisted: false, "1.0.0") };

        string published = await Store(done, store).PublishStatus(_repo);

        // The package id comes from the csproj the workflow packs — there is no package.info.json here.
        Assert.Contains("Published and approved: Acme.MyExt 1.0.0", published);
        Assert.Contains("https://store.test/extension/acme-myext", published);
    }

    // ---- no A -> B -> A: each tool sends the author only where the case is taken

    /**
     * No workflow here publishes: publish_status used to say "did you call publish_extension", whose tag
     * starts nothing here, after which publish_extension said "call publish_status" — a circle. Now it
     * names the tools that add a workflow, and connect_to_store does take such a repository.
     */
    [Fact]
    public async Task Publish_status_where_no_workflow_publishes_names_what_adds_one_and_that_route_takes_it()
    {
        Project("MyExt.csproj", connected: false);
        var none = new FakeProcessRunner().On("gh run list", stdout: "[]");

        string answer = await Store(none).PublishStatus(_repo);

        Assert.StartsWith("No publish workflow runs found, and no workflow in this repository publishes to the store", answer);
        Assert.Contains("connect_to_store", answer);
        Assert.Contains("update_extension", answer);
        Assert.DoesNotContain("publish_extension", answer);

        // Followed: connect_to_store takes it (its check pack faked, as a real one would pass).
        var dotnet = new FakeProcessRunner()
            .On("dotnet pack", args =>
            {
                string output = System.Text.RegularExpressions.Regex.Match(args, "-o \"([^\"]+)\"").Groups[1].Value;
                DcoFixture.WritePackage(Path.Combine(output, "Acme.MyExt.0.0.0-storecheck.nupkg"), "build");
                return new ProcResult(0, "", "");
            })
            .On("dotnet msbuild", _ =>
            {
                string bin = Path.Combine(_repo, "bin", "Release", "net10.0-windows");
                DcoFixture.WriteBuildOutput(bin);
                return new ProcResult(0, System.Text.Json.JsonSerializer.Serialize(new { Properties = new { TargetDir = bin } }), "");
            });
        string connected = await new ConnectTools(dotnet, new FakeStoreClient { RecommendedSdk = "3.0.15" }, new FakeUpdateCheck(null), _log.Add)
            .ConnectToStore(_repo);
        Assert.StartsWith("Connected MyExt.csproj", connected);
    }

    /** A workflow of the author's own that uses the publish action: followed by its own name, and not via publish_extension. */
    [Fact]
    public async Task Publish_status_follows_a_workflow_of_the_authors_own()
    {
        Project("MyExt.csproj", connected: false);
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Path.Combine(Workflows, "release.yml"), "on: workflow_dispatch\njobs:\n  p:\n    steps:\n      - uses: EncySoftware/publish-extension-action@v1\n");
        var none = new FakeProcessRunner().On("gh run list", stdout: "[]");

        string answer = await Store(none).PublishStatus(_repo);

        Assert.Contains(none.Calls, c => c.StartsWith("gh run list --workflow release.yml "));
        Assert.StartsWith("No run of release.yml found yet — this repository publishes through it", answer);
        Assert.DoesNotContain("publish_extension", answer);
    }

    /** The template's workflow keeps its answer: publish_extension's tag starts it. */
    [Fact]
    public async Task Publish_status_of_a_template_repository_still_sends_to_publish_extension()
    {
        Project("src/MyExt.csproj", connected: false);
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Path.Combine(Workflows, "publish.yml"), ConnectToolTests.TemplatesWorkflow);
        var none = new FakeProcessRunner().On("gh run list", stdout: "[]");

        string answer = await Store(none).PublishStatus(_repo);

        Assert.Contains(none.Calls, c => c.StartsWith("gh run list --workflow publish.yml "));
        Assert.Equal("No publish workflow runs found — did you call publish_extension (push a v* tag)?", answer);
    }

    /**
     * A project outside src/ in a repository that publishes through a workflow of the author's own: not
     * sent to connect_to_store, which has nothing to connect there, nor told to move into src/.
     */
    [Fact]
    public async Task Publish_extension_outside_src_with_a_workflow_of_the_authors_own_names_that_workflow()
    {
        Project("MyExt.csproj", connected: false);
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Path.Combine(Workflows, "release.yml"), "on: push\njobs:\n  p:\n    steps:\n      - uses: EncySoftware/publish-extension-action@v1\n");
        var proc = Push();

        string answer = await Store(proc).PublishExtension("1.0.0", repoDir: _repo);

        Assert.StartsWith($"ERROR: the project is at {_repo}, and this repository publishes through .github/workflows/release.yml, "
                          + "not through the template's workflow", answer);
        Assert.Contains("Publish by starting release.yml the way it runs", answer);
        Assert.DoesNotContain("connect_to_store", answer);
        Assert.DoesNotContain(proc.Calls, c => c.StartsWith("git tag") || c.StartsWith("git push"));
    }

    /** Where publish_extension does tag, it follows the run of the workflow that publishes — the one publish_status follows. */
    [Fact]
    public async Task Publish_extension_follows_the_workflow_that_publishes()
    {
        Project("src/MyExt.csproj", connected: false);
        File.WriteAllText(Path.Combine(_repo, "src", "package.info.json"), "{\n  \"packageId\": \"MyExt\",\n  \"tags\": \"ency-extension\",\n  \"category\": \"analyzer\"\n}\n");
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Path.Combine(Workflows, "release.yml"), "on: push\njobs:\n  p:\n    steps:\n      - uses: EncySoftware/publish-extension-action@v1\n");
        var proc = Push();

        string answer = await Store(proc).PublishExtension("1.0.0", repoDir: _repo);

        Assert.StartsWith("Pushed v1.0.0", answer);
        Assert.Contains(proc.Calls, c => c.StartsWith("gh run list --workflow release.yml "));
    }

    // ---- the template's route refuses

    [Fact]
    public async Task Update_extension_refuses_a_connected_repository_and_writes_nothing()
    {
        Project("project/main/MyExt.csproj", connected: true);
        // The note connect_to_store gives about Schedule A leads to a package.info.json: with it,
        // update_extension used to find a project and plant the template's files at the root.
        File.WriteAllText(Path.Combine(_repo, "project", "main", "package.info.json"), "{\n  \"reservedDomains\": []\n}\n");
        int fetched = 0;
        var tools = Folder(() => { fetched++; return Task.FromResult(Array.Empty<byte>()); });
        var before = Files();

        string answer = await tools.UpdateExtension(_repo);

        Assert.StartsWith($"ERROR: {_repo} publishes through .github/workflows/publish_store.yml", answer);
        Assert.Contains("a second one that publishes on every v* tag", answer);
        Assert.Contains("Nothing was changed.", answer);
        Assert.Equal(0, fetched);
        Assert.Equal(before, Files());
    }

    /** A project of its own shape in a repository with no template workflow: connect_to_store's, not this tool's. */
    [Fact]
    public async Task Update_extension_refuses_a_project_outside_src_in_a_repository_with_no_template_workflow()
    {
        Project("project/main/MyExt.csproj", connected: false);
        var tools = Folder(() => Task.FromResult(Array.Empty<byte>()));
        var before = Files();

        string answer = await tools.UpdateExtension(Path.Combine(_repo, "project", "main"));

        Assert.StartsWith("ERROR: project/main/MyExt.csproj is not in the template's src/ layout", answer);
        Assert.Contains("connect_to_store", answer);
        Assert.Equal(before, Files());
    }

    /** The template's own layout is what update_extension is for: no refusal there. */
    [Fact]
    public async Task Update_extension_still_serves_a_repository_in_the_templates_layout()
    {
        Project("src/MyExt.csproj", connected: false);
        var tools = Folder(() => throw new HttpRequestException("offline"));

        string answer = await tools.UpdateExtension(_repo);

        Assert.DoesNotContain("ERROR", answer);
        Assert.True(File.Exists(Path.Combine(_repo, "src", "package.info.json")));
    }

    [Fact]
    public async Task Publish_folder_refuses_a_GitHub_repository_of_its_own_before_signing_in()
    {
        Project("project/main/MyExt.csproj", connected: false);
        var auth = new FakeStoreAuth { Token = null };
        var store = new FakeStoreClient();
        var tools = new FolderPublishTools(store, auth, _ => Task.CompletedTask, _ => Task.CompletedTask, _log.Add,
            () => Task.FromResult(Array.Empty<byte>()));
        var before = Files();

        string answer = await tools.PublishFolder("MyExt", Path.Combine(_repo, "project", "main"));

        Assert.StartsWith("ERROR: project/main/MyExt.csproj is not in the template's src/ layout", answer);
        Assert.Contains("publish_folder would make a second repository for it from the template, besides acme/MyExt", answer);
        Assert.Equal(0, auth.LoginCalls);
        Assert.Empty(store.Uploads);
        Assert.Equal(before, Files());   // not even the manifest it writes for a foreign project
    }

    [Fact]
    public async Task Publish_folder_refuses_a_connected_repository()
    {
        Project("MyExt.csproj", connected: true);
        var auth = new FakeStoreAuth { Token = null };
        var tools = new FolderPublishTools(new FakeStoreClient(), auth, _ => Task.CompletedTask, _ => Task.CompletedTask, _log.Add,
            () => Task.FromResult(Array.Empty<byte>()));

        string answer = await tools.PublishFolder("MyExt", _repo);

        Assert.StartsWith($"ERROR: {_repo} publishes through .github/workflows/publish_store.yml", answer);
        Assert.Equal(0, auth.LoginCalls);
    }

    /** A folder in a repository that is not on GitHub is what publish_folder is for. */
    [Fact]
    public async Task Publish_folder_goes_ahead_in_a_repository_that_is_not_on_GitHub()
    {
        Remotes(("origin", "ssh://dev@gerrit.example.com:29418/ext"));
        Project("project/main/MyExt.csproj", connected: false, packageId: "MyExt");
        var store = new FakeStoreClient();
        var tools = new FolderPublishTools(store, new FakeStoreAuth(), _ => Task.CompletedTask, _ => Task.CompletedTask, _log.Add,
            () => Task.FromResult(Array.Empty<byte>()));

        string answer = await tools.PublishFolder("MyExt", Path.Combine(_repo, "project", "main"), waitForResult: false);

        Assert.DoesNotContain("ERROR", answer);
        Assert.Single(store.Uploads);
    }

    private FolderPublishTools Folder(Func<Task<byte[]>> zip) =>
        new(new FakeStoreClient { RecommendedSdk = "3.0.15" }, new FakeStoreAuth(), _ => Task.CompletedTask, _ => Task.CompletedTask,
            _log.Add, zip);

    /** Every file under the repository with its bytes, to show nothing was written. */
    private Dictionary<string, string> Files() =>
        Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(_repo, f), f => Convert.ToBase64String(File.ReadAllBytes(f)));

    // ---- reading the repository

    [Theory]
    [InlineData("https://github.com/EncySoftware/DirectCladdingOperation.git", "EncySoftware/DirectCladdingOperation")]
    [InlineData("https://github.com/acme/My.Ext", "acme/My.Ext")]
    [InlineData("git@github.com:acme/MyExt.git", "acme/MyExt")]
    [InlineData("ssh://git@github.com/acme/MyExt.git", "acme/MyExt")]
    [InlineData("https://user@github.com/acme/MyExt/", "acme/MyExt")]
    [InlineData("https://gitlab.com/acme/MyExt.git", null)]
    [InlineData("https://notgithub.com/acme/MyExt.git", null)]
    [InlineData("https://github.com.example.org/acme/MyExt.git", null)]
    [InlineData("ssh://dev@gerrit.example.com:29418/ext", null)]
    public void A_GitHub_remote_is_told_from_any_other(string url, string? expected) =>
        Assert.Equal(expected, GitRepository.GitHubRepository(url));

    [Fact]
    public void Remotes_are_read_from_the_config_and_origin_on_GitHub_comes_first()
    {
        Remotes(("upstream", "https://github.com/other/MyExt.git"), ("origin", "https://github.com/acme/MyExt.git"));

        var remotes = GitRepository.Remotes(_repo);

        Assert.Equal(new[] { "upstream", "origin" }, remotes.Select(r => r.Name).ToArray());
        Assert.Equal("acme/MyExt", GitRepository.GitHubRemote(remotes)!.GitHub);
        Assert.Equal(_repo, GitRepository.Root(Path.Combine(_repo, "a", "b")));
    }

    /** A worktree's .git is a file; its config lives in the repository its commondir names. */
    [Fact]
    public void A_worktree_reads_the_remotes_of_its_repository()
    {
        string main = Path.Combine(_repo, "main-repo"), tree = Path.Combine(_repo, "tree");
        string gitDir = Path.Combine(main, ".git", "worktrees", "tree");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(main, ".git", "config"), "[remote \"origin\"]\n\turl = git@github.com:acme/Tree.git\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(tree, ".git"), $"gitdir: {gitDir}\n");

        Assert.Equal(tree, GitRepository.Root(tree));
        Assert.Equal("acme/Tree", GitRepository.GitHubRemote(GitRepository.Remotes(tree))!.GitHub);
    }

    [Fact]
    public void A_workflow_names_the_projects_it_packs()
    {
        Assert.Equal(new[] { "project/main/X.csproj" },
            StoreWorkflow.PackedProjects("# dotnet pack \"commented/Out.csproj\"\nrun: dotnet pack \"project/main/X.csproj\" -c Release\n"));
        Assert.Equal(new[] { "src/A.csproj", "B.csproj" },
            StoreWorkflow.PackedProjects("run: |\n  dotnet build ./src/A.csproj\n  dotnet pack 'B.csproj'\n"));
        Assert.Empty(StoreWorkflow.PackedProjects("run: dotnet pack -c Release -o out\n"));

        Assert.True(StoreWorkflow.SameProject("project\\main\\X.csproj", "project/main/X.csproj"));
        Assert.True(StoreWorkflow.SameProject("X.csproj", "project/main/X.csproj"));   // a step that runs in project/main
        Assert.False(StoreWorkflow.SameProject("project/old/X.csproj", "project/main/X.csproj"));
    }

    [Fact]
    public void The_workflow_is_found_from_any_folder_of_the_repository_and_names_the_package()
    {
        Project("project/main/MyExt.csproj", connected: true);

        string? found = StoreWorkflow.Find(Path.Combine(_repo, "project", "main"));

        Assert.Equal(Path.Combine(Workflows, StoreWorkflow.FileName), found);
        Assert.Equal("Acme.MyExt", StoreWorkflow.PackageIdOf(found!));
        Assert.Null(StoreWorkflow.Find(Path.Combine(Path.GetTempPath(), "mcp-nowhere-" + Guid.NewGuid().ToString("N"))));
    }
}
