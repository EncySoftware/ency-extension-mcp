using System.Text.Json;
using System.Text.RegularExpressions;
using EncyExtensionMcp;
using Xunit;

/**
 * connect_to_store on a project that was not made from the template — DirectCladdingOperation, as it
 * was on 08.10.2026: its csproj gets the PackagePath fix, the repository gets a workflow of its own,
 * and both stay only when a real pack, laid out the way ENCY installs it, says they are right.
 * `dotnet` is faked: the pack writes the package a real one would, the build output is on disk.
 */
public class ConnectToolTests : IDisposable
{
    private const string Origin = "https://github.com/EncySoftware/DirectCladdingOperation.git";
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "mcp-connect-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _log = new();
    private readonly FakeStoreClient _store = new() { RecommendedSdk = "3.0.15" };

    private string ProjectDir => Path.Combine(_repo, "project", "main");
    private string Csproj => Path.Combine(ProjectDir, DcoFixture.CsprojName);
    private string Bin => Path.Combine(ProjectDir, "bin", "Release", "net10.0-windows");
    private string Workflows => Path.Combine(_repo, ".github", "workflows");
    private string Workflow => Path.Combine(Workflows, StoreWorkflow.FileName);

    public ConnectToolTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        SetRemotes(("origin", Origin));
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllBytes(Csproj, DcoFixture.Bytes);
        DcoFixture.WriteBuildOutput(Bin);
    }

    public void Dispose() { try { Directory.Delete(_repo, true); } catch { } }

    /** The repository's remotes, as git writes them into .git/config. */
    private void SetRemotes(params (string Name, string Url)[] remotes) =>
        File.WriteAllText(Path.Combine(_repo, ".git", "config"),
            "[core]\n\trepositoryformatversion = 0\n"
            + string.Concat(remotes.Select(r => $"[remote \"{r.Name}\"]\n\turl = {r.Url}\n\tfetch = +refs/heads/*:refs/remotes/{r.Name}/*\n")));

    /** `dotnet pack` writes a package laid out as `layout` into its -o folder; MSBuild says the build went to Bin. */
    private FakeProcessRunner Dotnet(string layout = "build", List<string>? outputs = null, string version = ConnectTools.ProbeVersion,
                                     string tags = "ency-extension operationsolver", string sdk = "3.0.15") =>
        new FakeProcessRunner()
            .On("dotnet pack", args =>
            {
                string output = Regex.Match(args, "-o \"([^\"]+)\"").Groups[1].Value;
                outputs?.Add(output);
                DcoFixture.WritePackage(Path.Combine(output, $"EncySoftware.DirectCladdingOperation.{version}.nupkg"), layout, version, tags, sdk);
                return new ProcResult(0, "Successfully created package.", "");
            })
            .On("dotnet msbuild", _ => new ProcResult(0, JsonSerializer.Serialize(new
            {
                Properties = new { TargetDir = Bin + Path.DirectorySeparatorChar, TargetFileName = DcoFixture.Dll },
            }), ""));

    private ConnectTools Tools(FakeProcessRunner proc, DateOnly? today = null) =>
        new(proc, _store, new FakeUpdateCheck(null), _log.Add, () => today ?? new DateOnly(2026, 10, 15));

    [Fact]
    public async Task A_contentFiles_project_is_fixed_given_a_workflow_and_checked_by_a_real_pack()
    {
        Directory.CreateDirectory(Workflows);
        string prod = Path.Combine(Workflows, "publish_prod.yml");
        const string prodText = "name: Publish to NuGet\r\n\r\non:\r\n  push:\r\n    tags:\r\n      - 'v*'\r\n";
        File.WriteAllText(prod, prodText);
        var outputs = new List<string>();
        var proc = Dotnet("build", outputs);

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("Connected project/main/DirectCladdingOperationExtension.csproj (EncySoftware.DirectCladdingOperation) "
                          + "to the ENCY Extension Store: the GitHub repository EncySoftware/DirectCladdingOperation publishes it.", answer);
        Assert.Contains("line 25 (DirectCladdingOperation.smarthint): PackagePath \"contentFiles\\any\\any\\\" -> \"build\\\"", answer);
        Assert.Contains("line 29 (SmartHintImages\\*.png): PackagePath \"contentFiles\\any\\any\\SmartHintImages\\\" -> \"build\\\"", answer);
        Assert.DoesNotContain("the build does not copy it", answer);   // all four are copied: the build says where they belong
        Assert.Contains(".github/workflows/publish_store.yml: added", answer);
        Assert.Contains($"7 files land where the Release build has them (README.md and icon.png at the root", answer);
        Assert.Contains("DirectCladdingOperationExtension.settings.json finds DirectCladdingOperationExtension.dll", answer);
        Assert.DoesNotContain("Before the first publish", answer);   // SDK 3.0.15 is the store's own line

        // The csproj: four values, the byte order mark and every CRLF as they were.
        Assert.Equal(DcoFixture.FixedBytes, File.ReadAllBytes(Csproj));
        string yaml = File.ReadAllText(Workflow);
        Assert.Contains("dotnet pack \"project/main/DirectCladdingOperationExtension.csproj\" -c Release", yaml);
        Assert.Contains("dotnet-version: '10.0.x'", yaml);
        Assert.Contains("id-token: write", yaml);
        Assert.Contains("uses: EncySoftware/publish-extension-action@v1", yaml);
        Assert.Contains("\r\n", yaml);   // the line ending of its neighbour
        Assert.Equal(prodText, File.ReadAllText(prod));
        Assert.Contains("A tag push also starts publish_prod.yml", answer);
        Assert.False(File.Exists(Path.Combine(ProjectDir, "package.info.json")));

        // What only the author can do, said the way the store decides it: the first publish binds the
        // repository, and from then on every run is the binder's — not "whoever starts it".
        Assert.Contains("Sign in at https://apps.encycam.com.", answer);
        Assert.Contains("https://apps.encycam.com/publish: the developer registration", answer);
        Assert.Contains("\"Sign in with GitHub\"", answer);
        Assert.Contains("The first publish binds EncySoftware/DirectCladdingOperation to EncySoftware.DirectCladdingOperation: "
                        + "start it as the card's owner", answer);
        Assert.Contains("After that every run from EncySoftware/DirectCladdingOperation is credited to that owner, whoever starts it.", answer);
        Assert.Contains("`ency-extension-mcp claim EncySoftware.DirectCladdingOperation EncySoftware/DirectCladdingOperation`", answer);
        Assert.DoesNotContain("everyone who will run the workflow", answer);
        Assert.Contains("Own the card: EncySoftware.DirectCladdingOperation", answer);
        Assert.Contains("git tag vX.Y.Z, then git push origin vX.Y.Z", answer);
        Assert.Contains("(publish_extension does both)", answer);
        Assert.Contains("Run workflow", answer);
        Assert.Contains("To follow a run: publish_status (the run of publish_store.yml, then the store card)", answer);
        Assert.Contains("from 2026-11-01 the store refuses a publish from CI without the Reserved Functionality answer", answer);
        Assert.Contains("this tool does not write it", answer);

        // The pack ran where the workflow runs it, the way the workflow runs it, in English, with time for
        // a cold restore — into a folder deleted afterwards.
        var pack = proc.Runs.Single(r => r.Call.StartsWith("dotnet pack"));
        Assert.Equal(ProjectDir, pack.WorkingDir);
        Assert.Equal("en", pack.Env!["DOTNET_CLI_UI_LANGUAGE"]);
        Assert.Equal(ConnectTools.PackTimeoutSeconds, pack.Timeout);
        Assert.Contains("-c Release -p:Version=0.0.0-storecheck -p:WarningsAsErrors=NU1603", pack.Call);
        Assert.Contains("--source https://api.nuget.org/v3/index.json --source https://nexus.encycam.com/repository/master/index.json", pack.Call);
        Assert.False(Directory.Exists(Assert.Single(outputs)));
    }

    [Fact]
    public async Task A_check_pack_that_still_lays_files_out_wrong_takes_both_changes_back()
    {
        var outputs = new List<string>();

        string answer = await Tools(Dotnet("contentFiles", outputs)).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: the check pack shows the package would not install right:", answer);
        Assert.Contains("contentFiles/any/any/DirectCladdingOperationExtension.settings.json", answer);
        Assert.Contains("the Release build has it at SmartHintImages/start-point.png", answer);
        Assert.EndsWith("Nothing was changed: the csproj is back as it was and the workflow is removed.", answer);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
        Assert.False(File.Exists(Workflow));
        Assert.False(Directory.Exists(Path.Combine(_repo, ".github")));   // made for the workflow, gone with it
        Assert.False(Directory.Exists(Assert.Single(outputs)));
    }

    [Fact]
    public async Task A_failed_pack_takes_both_changes_back_and_says_why()
    {
        Directory.CreateDirectory(Workflows);
        var proc = new FakeProcessRunner().On("dotnet pack", 1,
            "C:\\r\\project\\main\\X.csproj : error NU1603: Warning As Error: EncySoftware.CAMAPI.SDK.Net 3.0.15 was not found. 3.0.16 was resolved instead.");

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: dotnet pack failed. NuGet found no package of the exact SDK version", answer);
        Assert.Contains("error NU1603: Warning As Error", answer);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
        Assert.False(File.Exists(Workflow));
        Assert.True(Directory.Exists(Workflows));   // it was there before; only what this call made goes
    }

    /** A check that breaks halfway leaves nothing standing unchecked either. */
    [Fact]
    public async Task A_check_that_breaks_takes_both_changes_back_too()
    {
        var proc = new FakeProcessRunner()
            .On("dotnet pack", args =>
            {
                string output = Regex.Match(args, "-o \"([^\"]+)\"").Groups[1].Value;
                File.WriteAllBytes(Path.Combine(output, "broken.nupkg"), new byte[] { 0x50, 0x4B, 3, 4, 9, 9, 9 });
                return new ProcResult(0, "", "");
            })
            .On("dotnet msbuild", _ => new ProcResult(0, JsonSerializer.Serialize(new { Properties = new { TargetDir = Bin, TargetFileName = DcoFixture.Dll } }), ""));

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: the check could not be completed", answer);
        Assert.EndsWith("Nothing was changed: the csproj is back as it was and the workflow is removed.", answer);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
        Assert.False(File.Exists(Workflow));
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        await Tools(Dotnet()).ConnectToStore(_repo);
        byte[] csproj = File.ReadAllBytes(Csproj);
        string yaml = File.ReadAllText(Workflow);

        string again = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("Connected", again);
        Assert.Contains("nothing to change - it packs no runtime file where ENCY's store installer does not look", again);
        Assert.Contains("publish_store.yml: already there as this tool writes it", again);
        Assert.Equal(csproj, File.ReadAllBytes(Csproj));
        Assert.Equal(yaml, File.ReadAllText(Workflow));
    }

    /** The author's own edits to the workflow stay — as long as it still packs this project. */
    [Fact]
    public async Task An_existing_publish_store_yml_that_packs_this_project_is_left_as_it_is()
    {
        Directory.CreateDirectory(Workflows);
        string mine = StoreWorkflow.Render("project/main/DirectCladdingOperationExtension.csproj", "10.0.x")
            .Replace("runs-on: windows-latest", "runs-on: windows-2022");
        File.WriteAllText(Workflow, mine);

        // Started from the project folder: the repository is found above it.
        string answer = await Tools(Dotnet()).ConnectToStore(ProjectDir);

        Assert.StartsWith("Connected", answer);
        Assert.Contains("publish_store.yml: already there and packs project/main/DirectCladdingOperationExtension.csproj, "
                        + "though not word for word as this tool would write it - left as it is", answer);
        Assert.Equal(mine, File.ReadAllText(Workflow));
        Assert.Equal(DcoFixture.FixedBytes, File.ReadAllBytes(Csproj));
    }

    /**
     * One repository publishes one extension. A workflow here that packs another project is that one's —
     * connecting a second project over it would answer "Connected" while every tag published the first
     * (review, 09.10.2026).
     */
    [Fact]
    public async Task An_existing_publish_store_yml_for_another_project_is_not_answered_with_connected()
    {
        Directory.CreateDirectory(Workflows);
        Directory.CreateDirectory(Path.Combine(_repo, "other"));
        File.WriteAllText(Path.Combine(_repo, "other", "Other.csproj"), "<Project />");
        string theirs = StoreWorkflow.Render("other/Other.csproj", "10.0.x");
        File.WriteAllText(Workflow, theirs);
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo, "project/main/DirectCladdingOperationExtension.csproj");

        Assert.StartsWith("ERROR: .github/workflows/publish_store.yml already publishes other/Other.csproj, not "
                          + "project/main/DirectCladdingOperationExtension.csproj.", answer);
        Assert.Contains("One repository publishes one extension", answer);
        Assert.EndsWith("Nothing was changed.", answer);
        Assert.DoesNotContain("Connected", answer);
        Assert.Empty(proc.Calls);
        Assert.Equal(theirs, File.ReadAllText(Workflow));
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
    }

    /** The same false success when the project moved after an earlier connect: CI would fail on the old path. */
    [Fact]
    public async Task A_workflow_for_a_project_that_moved_is_named_as_such()
    {
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Workflow, StoreWorkflow.Render("project/old/DirectCladdingOperationExtension.csproj", "10.0.x"));

        string answer = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: .github/workflows/publish_store.yml already publishes project/old/DirectCladdingOperationExtension.csproj", answer);
        Assert.Contains("there is no project/old/DirectCladdingOperationExtension.csproj in the repository any more (did the project move?", answer);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
    }

    [Fact]
    public async Task A_workflow_that_names_no_csproj_cannot_be_vouched_for()
    {
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Workflow, "name: mine\non: push\njobs:\n  p:\n    steps:\n      - run: dotnet pack -c Release -o out\n");
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: .github/workflows/publish_store.yml is already here and names no .csproj", answer);
        Assert.Contains("dotnet pack \"project/main/DirectCladdingOperationExtension.csproj\"", answer);
        Assert.Empty(proc.Calls);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
    }

    [Fact]
    public async Task Several_extension_projects_and_no_csproj_named_is_a_question_not_a_guess()
    {
        string tests = Path.Combine(_repo, "tests", "Ext.Tests");
        Directory.CreateDirectory(tests);
        File.WriteAllText(Path.Combine(tests, "Ext.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"EncySoftware.CAMAPI.SDK.Net\" Version=\"3.0.15\" /></ItemGroup></Project>");
        // A build script's own project, in a dot-folder, is no candidate at all.
        Directory.CreateDirectory(Path.Combine(_repo, ".stbuild", "build"));
        File.WriteAllText(Path.Combine(_repo, ".stbuild", "build", "stbuild.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"EncySoftware.CAMAPI.SDK.Net\" Version=\"3.0.15\" /></ItemGroup></Project>");
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: several extension projects", answer);
        Assert.Contains("  - project/main/DirectCladdingOperationExtension.csproj (packs a *.settings.json)", answer);
        Assert.Contains("  - tests/Ext.Tests/Ext.Tests.csproj", answer);
        Assert.DoesNotContain("stbuild", answer);
        Assert.Empty(proc.Calls);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));

        string named = await Tools(Dotnet()).ConnectToStore(_repo, "project/main/DirectCladdingOperationExtension.csproj");
        Assert.StartsWith("Connected", named);
    }

    [Fact]
    public async Task A_repository_that_publishes_to_the_store_already_gets_no_second_workflow()
    {
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Path.Combine(Workflows, "publish.yml"), "steps:\n  - uses: EncySoftware/publish-extension-action@v1\n");
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: this repository already publishes to the store through .github/workflows/publish.yml", answer);
        Assert.Contains("update_extension", answer);
        Assert.Empty(proc.Calls);
        Assert.False(File.Exists(Workflow));
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
    }

    [Fact]
    public async Task A_framework_the_workflow_cannot_build_stops_before_anything_is_written()
    {
        File.WriteAllBytes(Csproj, ConnectTools.Encode(DcoFixture.Text.Replace("net10.0-windows", "net48"), bom: true));
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: project/main/DirectCladdingOperationExtension.csproj targets net48", answer);
        Assert.Empty(proc.Calls);
        Assert.False(File.Exists(Workflow));
    }

    // ---- where the workflow would run: GitHub only

    /**
     * Outside git there is no repository for a workflow to run in — but the csproj fix is what every
     * route needs, publish_package included, so it is made and checked all the same.
     */
    [Fact]
    public async Task Outside_a_git_repository_the_csproj_is_fixed_and_no_workflow_is_added()
    {
        if (GitRepository.Root(Path.GetTempPath()) != null) return;   // a machine whose temp folder sits in a repository
        Directory.Delete(Path.Combine(_repo, ".git"), true);

        string answer = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("Checked project/main/DirectCladdingOperationExtension.csproj (EncySoftware.DirectCladdingOperation) for the "
                          + $"ENCY Extension Store - no workflow added: {_repo} is not in a git repository", answer);
        Assert.DoesNotContain("Connected", answer);
        Assert.Equal(DcoFixture.FixedBytes, File.ReadAllBytes(Csproj));
        Assert.False(File.Exists(Workflow));
        Assert.Contains(".github/workflows/publish_store.yml: not added", answer);
        Assert.Contains("dotnet pack \"project/main/DirectCladdingOperationExtension.csproj\" -c Release -p:Version=X.Y.Z", answer);
        Assert.Contains("then publish_package out", answer);
        Assert.DoesNotContain("Sign in with GitHub", answer);
        Assert.Contains("the store refuses a publish from this machine without the Reserved Functionality answer", answer);
    }

    [Fact]
    public async Task A_repository_whose_remote_is_not_on_GitHub_gets_no_workflow()
    {
        SetRemotes(("origin", "ssh://dev@gerrit.example.com:29418/extensions/DirectCladding"));

        string answer = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("Checked project/main/DirectCladdingOperationExtension.csproj", answer);
        Assert.Contains("no workflow added: its remote (origin: ssh://dev@gerrit.example.com:29418/extensions/DirectCladding) is not on "
                        + "GitHub, and the workflow that publishes runs on GitHub Actions only", answer);
        Assert.False(File.Exists(Workflow));
        Assert.False(Directory.Exists(Path.Combine(_repo, ".github")));
        Assert.Equal(DcoFixture.FixedBytes, File.ReadAllBytes(Csproj));
        Assert.Contains("once the repository has a GitHub remote (a mirror will do), call connect_to_store again", answer);
    }

    [Fact]
    public async Task A_repository_with_no_remote_gets_no_workflow()
    {
        SetRemotes();

        string answer = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("Checked", answer);
        Assert.Contains("no workflow added: this repository has no remote", answer);
        Assert.False(File.Exists(Workflow));

        // One left from earlier stays, and is not called "not added".
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Workflow, "name: old\n");
        string again = await Tools(Dotnet()).ConnectToStore(_repo);
        Assert.Contains(".github/workflows/publish_store.yml: left as it is, though it does not run - this repository has no remote", again);
        Assert.Equal("name: old\n", File.ReadAllText(Workflow));
    }

    /** A GitHub mirror next to another origin is where the workflow runs. */
    [Fact]
    public async Task A_GitHub_mirror_is_where_the_workflow_runs()
    {
        SetRemotes(("origin", "ssh://dev@gerrit.example.com:29418/ext"), ("github", "git@github.com:EncySoftware/DirectCladdingOperation.git"));

        string answer = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("Connected", answer);
        Assert.Contains("the GitHub repository EncySoftware/DirectCladdingOperation publishes it", answer);
        Assert.True(File.Exists(Workflow));
    }

    // ---- what would stop the first publish

    [Fact]
    public async Task What_would_stop_the_first_publish_is_named_and_the_connection_stands()
    {
        string answer = await Tools(Dotnet(tags: "operationsolver", version: "3.0.1")).ConnectToStore(_repo);

        Assert.StartsWith("Connected", answer);
        Assert.Contains("Before the first publish, in the project:", answer);
        Assert.Contains("the package tags have no `ency-extension`", answer);
        Assert.Contains("the package came out as version 3.0.1", answer);
        Assert.Equal(DcoFixture.FixedBytes, File.ReadAllBytes(Csproj));
    }

    /** The store refuses an SDK above its line (3.0.16 is on nuget.org, so the pack itself goes through). */
    [Fact]
    public async Task An_SDK_newer_than_the_store_takes_is_named_before_the_first_publish()
    {
        string answer = await Tools(Dotnet(sdk: "3.0.16")).ConnectToStore(_repo);

        Assert.StartsWith("Connected", answer);
        Assert.Contains("Before the first publish, in the project:", answer);
        Assert.Contains("the package is built against SDK 3.0.16, newer than the store takes (3.0.15)", answer);
        Assert.Contains("Reference EncySoftware.CAMAPI.SDK.Net 3.0.15 in project/main/DirectCladdingOperationExtension.csproj", answer);
    }

    [Fact]
    public async Task A_store_that_does_not_answer_about_the_SDK_stops_nothing()
    {
        _store.RecommendedSdk = null;

        string answer = await Tools(Dotnet(sdk: "3.0.16")).ConnectToStore(_repo);

        Assert.StartsWith("Connected", answer);
        Assert.DoesNotContain("Before the first publish", answer);
    }

    /** An item the build does not copy has no place in the build to go by: it keeps its own, and the answer says so. */
    [Fact]
    public async Task An_item_the_build_does_not_copy_keeps_its_place_and_is_named()
    {
        string text = DcoFixture.Text.Replace(
            "    <None Include=\"DirectCladdingOperation_ExtOp.xml\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\">\r\n"
            + "      <CopyToOutputDirectory>Always</CopyToOutputDirectory>\r\n    </None>",
            "    <None Include=\"DirectCladdingOperation_ExtOp.xml\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\" />");
        Assert.NotEqual(DcoFixture.Text, text);
        File.WriteAllBytes(Csproj, ConnectTools.Encode(text, bom: true));

        string answer = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("Connected", answer);
        Assert.Contains("(DirectCladdingOperation_ExtOp.xml): PackagePath \"contentFiles\\any\\any\\\" -> \"build\\\"", answer);
        Assert.Contains("    ! the build does not copy it to its output (no CopyToOutputDirectory), so no build says where the extension "
                        + "reads it: it keeps the place its PackagePath gave it under contentFiles/ and lands in the extension's folder, "
                        + "next to the dll.", answer);
    }

    [Fact]
    public async Task From_1_November_the_Schedule_A_note_says_the_store_refuses()
    {
        string answer = await Tools(Dotnet(), new DateOnly(2026, 11, 2)).ConnectToStore(_repo);
        Assert.Contains("since 2026-11-01 the store refuses a publish from CI", answer);
    }

    [Theory]
    [InlineData("net10.0-windows", "10.0.x")]
    [InlineData("net9.0-windows7.0", "9.0.x")]
    [InlineData("net8.0", "8.0.x")]
    [InlineData("net7.0", null)]
    [InlineData("net48", null)]
    [InlineData("netstandard2.0", null)]
    public void The_dotnet_version_follows_the_target_framework(string tfm, string? expected) =>
        Assert.Equal(expected, StoreWorkflow.DotnetVersion(tfm));

    [Fact]
    public void The_workflow_builds_the_csproj_by_its_path_in_the_repository()
    {
        string yaml = StoreWorkflow.Render("src/My Ext/MyExt.csproj", "8.0.x");

        Assert.Contains("dotnet pack \"src/My Ext/MyExt.csproj\" -c Release", yaml);
        Assert.Contains("dotnet-version: '8.0.x'", yaml);
        Assert.Contains("-p:WarningsAsErrors=NU1603", yaml);
        Assert.Contains("--source https://nexus.encycam.com/repository/master/index.json", yaml);
        Assert.Contains("nupkg: out/*.nupkg", yaml);
        Assert.DoesNotContain("__", yaml);
        Assert.DoesNotContain("\r", yaml);
        Assert.Contains("\r\n", StoreWorkflow.Render("a.csproj", "10.0.x", "\r\n"));
        Assert.False(StoreWorkflow.IsSafePath("a\"b/c.csproj"));
        // The header says who a run is credited to the way the store decides it.
        Assert.Contains("The first publish\n# binds this repository to the extension", yaml);
        Assert.Contains("every run from this repository is credited to that owner, whoever starts it", yaml);
        Assert.DoesNotContain("The store credits the", yaml);
        Assert.Equal(new[] { "src/My Ext/MyExt.csproj" }, StoreWorkflow.PackedProjects(yaml));
    }
}
