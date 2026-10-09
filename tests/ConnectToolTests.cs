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

    /**
     * `dotnet pack` writes a package laid out as `layout` (plus `extra` entries) into its -o folder; MSBuild
     * says the build went to Bin, and the restore's assets file is `assets`.
     */
    private FakeProcessRunner Dotnet(string layout = "build", List<string>? outputs = null, string version = ConnectTools.ProbeVersion,
                                     string tags = "ency-extension operationsolver", string sdk = "3.0.15", string assets = "",
                                     params (string Name, string Text)[] extra) =>
        new FakeProcessRunner()
            .On("dotnet pack", args =>
            {
                string output = Regex.Match(args, "-o \"([^\"]+)\"").Groups[1].Value;
                outputs?.Add(output);
                DcoFixture.WritePackage(Path.Combine(output, $"EncySoftware.DirectCladdingOperation.{version}.nupkg"), layout, version, tags, sdk, extra);
                return new ProcResult(0, "Successfully created package.", "");
            })
            .On("dotnet msbuild", _ => new ProcResult(0, JsonSerializer.Serialize(new
            {
                Properties = new { TargetDir = Bin + Path.DirectorySeparatorChar, TargetFileName = DcoFixture.Dll, ProjectAssetsFile = assets },
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
        Assert.Contains("Why: the store installer in ENCY keeps contentFiles/ as a subfolder away from the dll", answer);
        // What the check proves, and no more: the root files are not among the ones the build has, and the
        // check package's own name (in a temp folder gone before anyone reads this) is not given.
        Assert.Contains("laid out the way the store tab in ENCY installs it: 7 files land where the Release build has them; "
                        + "README.md and icon.png land at the root, where the nuspec names them; "
                        + "DirectCladdingOperationExtension.settings.json finds DirectCladdingOperationExtension.dll; "
                        + "nothing lands under contentFiles/.", answer);
        Assert.DoesNotContain("storecheck.nupkg", answer);
        Assert.Contains("Its restore may have taken packages from this machine's cache; the run on GitHub starts cold, with only those "
                        + "two sources.", answer);
        Assert.Contains("  - commit and push the changes above;", answer);
        Assert.DoesNotContain(" — ", answer);   // the package check's notes in this answer's own style
        Assert.Contains("  - note: no screenshots", answer);   // the package check's note on the card, offline
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
        Assert.Contains("A tag push also starts publish_prod.yml, which has a tag trigger of its own; Run workflow starts this one alone.", answer);
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
        // The store has no transfer an author can start: said as what the author can do about it.
        Assert.Contains("a card someone else owns: ask that owner or the store's moderators to hand it over.", answer);
        Assert.DoesNotContain("handed over first", answer);
        Assert.Contains("git tag vX.Y.Z, then git push origin vX.Y.Z", answer);
        Assert.Contains("(publish_extension does both)", answer);
        Assert.Contains("Run workflow", answer);
        // What an author meets on the first run.
        Assert.Contains("GitHub offers Run workflow only once publish_store.yml is on the default branch.", answer);
        Assert.Contains("A new card stays out of the catalogue until a moderator approves it (its direct link works at once); "
                        + "new versions of an approved card appear at once.", answer);
        Assert.Contains("The workflow passes no category: a new card lands in \"other\" unless <PackageTags> carries category:<id> "
                        + "(ids: https://apps.encycam.com/api/categories); a card that already has a category keeps it.", answer);
        Assert.Contains("To follow a run: publish_status (the run of publish_store.yml, then the store card)", answer);
        // Schedule A in three lines: when, what and where, whose.
        Assert.Contains("Schedule A: from 2026-11-01 the store refuses a publish from CI that carries no Reserved Functionality "
                        + "answer; until then it publishes with a warning." + Environment.NewLine, answer);
        Assert.Contains("  The answer is `reservedDomains` in a package.info.json next to the csproj: [] when the extension provides "
                        + "none of the areas listed at https://encycam.com/legal/extension-store/reserved-functionality/", answer);
        Assert.Contains("Pack it with <None Include=\"package.info.json\" Pack=\"true\" PackagePath=\"build\\\" "
                        + "CopyToOutputDirectory=\"PreserveNewest\" />." + Environment.NewLine, answer);
        Assert.Contains("  This tool does not write it: the answer is the author's to decide. The first run with a new answer stops once "
                        + "with a link to confirm it in the browser; confirm, then run the workflow again.", answer);

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
        // Not "nothing was changed": the check build has left bin/ and obj/, as any build does.
        Assert.EndsWith("The csproj and the workflow were left as they were.", answer);
        Assert.DoesNotContain("Nothing was changed", answer);
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
        Assert.EndsWith("The csproj and the workflow were left as they were.", answer);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
        Assert.False(File.Exists(Workflow));
        Assert.True(Directory.Exists(Workflows));   // it was there before; only what this call made goes
    }

    /**
     * A source that does not answer: NuGet's own lines carry the operating system's reason in the
     * machine's language, so the answer says in plain words which source it was and what to do.
     */
    [Fact]
    public async Task A_source_that_cannot_be_reached_is_named_in_plain_words()
    {
        var proc = new FakeProcessRunner().On("dotnet pack", 1,
            "C:\\r\\project\\main\\X.csproj : error NU1301: Unable to load the service index for source "
            + "https://nexus.encycam.com/repository/master/index.json.\r\n  Этот хост неизвестен. (nexus.encycam.com:443)\r\n");

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: dotnet pack failed. The ENCY Nexus (https://nexus.encycam.com/repository/master/index.json) could not "
                          + "be reached from this machine (NU1301). The check restores from the same two sources as the workflow, "
                          + "nuget.org and the ENCY Nexus; try again when it answers.", answer);
        Assert.Contains("error NU1301", answer);   // NuGet's own words stay below
        Assert.EndsWith("The csproj and the workflow were left as they were.", answer);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
        Assert.False(File.Exists(Workflow));
    }

    /**
     * Restore takes a package out of this machine's cache without asking any source; the run on GitHub
     * starts cold. A package the cache got from elsewhere is named — by its .nupkg.metadata.
     */
    [Fact]
    public async Task Packages_this_machines_cache_had_from_other_sources_are_named()
    {
        string cache = Path.Combine(_repo, "cache"), assets = Path.Combine(_repo, "project.assets.json");
        void Cached(string path, string source)
        {
            Directory.CreateDirectory(Path.Combine(cache, path));
            File.WriteAllText(Path.Combine(cache, path, ".nupkg.metadata"),
                $"{{\"version\": 2, \"contentHash\": \"x\", \"source\": \"{source}\"}}");
        }
        Cached("encysoftware.camapi.sdk.net/3.0.15", "https://nexus.encycam.com/repository/master/index.json");
        Cached("newtonsoft.json/13.0.3", "https://api.nuget.org/v3/index.json");
        Cached("encysoftware.helpers/1.2.0", "https://nexus.hq.encycam.com/repository/dev-feed/index.json");
        File.WriteAllText(assets, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["libraries"] = new Dictionary<string, object>
            {
                ["EncySoftware.CAMAPI.SDK.Net/3.0.15"] = new { type = "package", path = "encysoftware.camapi.sdk.net/3.0.15" },
                ["Newtonsoft.Json/13.0.3"] = new { type = "package", path = "newtonsoft.json/13.0.3" },
                ["EncySoftware.Helpers/1.2.0"] = new { type = "package", path = "encysoftware.helpers/1.2.0" },
                ["Shared/1.0.0"] = new { type = "project", path = "../shared/Shared.csproj" },
            },
            ["packageFolders"] = new Dictionary<string, object> { [cache + Path.DirectorySeparatorChar] = new { } },
        }));

        string answer = await Tools(Dotnet(assets: assets)).ConnectToStore(_repo);

        Assert.StartsWith("Connected", answer);
        Assert.Contains("  - note: this machine's cache had EncySoftware.Helpers 1.2.0 "
                        + "(https://nexus.hq.encycam.com/repository/dev-feed/index.json) from other sources - if neither nuget.org nor "
                        + "the ENCY Nexus has it, the run on GitHub stops at restore (NU1101).", answer);
        Assert.DoesNotContain("Newtonsoft", answer);
        Assert.DoesNotContain("SDK.Net 3.0.15 (", answer);
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", true)]
    [InlineData("https://www.nuget.org/api/v2/", true)]
    [InlineData("https://nexus.encycam.com/repository/master/index.json", true)]
    [InlineData("https://nexus.encycam.com/repository/master/", true)]
    [InlineData("https://nexus.hq.encycam.com/repository/dev-feed/index.json", false)]
    [InlineData("https://nexus.encycam.com/repository/dev-feed/index.json", false)]
    [InlineData("https://apiint.nugettest.org/v3/index.json", false)]
    [InlineData("C:\\feeds\\local", false)]
    public void The_workflows_two_sources_are_told_however_their_address_is_written(string source, bool expected) =>
        Assert.Equal(expected, ConnectTools.IsWorkflowSource(source));

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
        Assert.EndsWith("The csproj and the workflow were left as they were.", answer);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
        Assert.False(File.Exists(Workflow));
    }

    /**
     * The csproj cannot be written back: its original goes to a file of its own, named in the answer.
     * `git checkout` alone would also throw away whatever the author had not committed — and this tool
     * takes a working tree with uncommitted edits.
     */
    [Fact]
    public async Task A_csproj_that_cannot_be_put_back_is_saved_elsewhere_and_named()
    {
        var proc = new FakeProcessRunner().On("dotnet pack", _ =>
        {
            // The csproj's path turns into a folder while the check runs: nothing can be written back there.
            File.Delete(Csproj);
            Directory.CreateDirectory(Csproj);
            return new ProcResult(1, "X.csproj : error NU1603: Warning As Error", "");
        });

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.Contains("NOT everything could be taken back: project/main/DirectCladdingOperationExtension.csproj could not be put back", answer);
        string saved = Regex.Match(answer, "its original is saved at (?<p>.+?) - copy that over it").Groups["p"].Value;
        try
        {
            Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(saved));   // byte for byte: the byte order mark and every CRLF
            Assert.Contains("(`git checkout -- project/main/DirectCladdingOperationExtension.csproj` would also discard any edits "
                            + "to it you have not committed)", answer);
            Assert.False(File.Exists(Workflow));   // what could be taken back was
        }
        finally { File.Delete(saved); }
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        await Tools(Dotnet()).ConnectToStore(_repo);
        byte[] csproj = File.ReadAllBytes(Csproj);
        string yaml = File.ReadAllText(Workflow);

        string again = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("Connected", again);
        Assert.Contains("nothing to change - it packs nothing under contentFiles/ and no folder twice", again);
        Assert.Contains("publish_store.yml: already there as this tool writes it", again);
        // Nothing is "above" this time: what has to be on GitHub is said by name.
        Assert.DoesNotContain("the changes above", again);
        Assert.Contains("  - commit and push project/main/DirectCladdingOperationExtension.csproj and .github/workflows/publish_store.yml "
                        + "if they are not on GitHub as they are here;", again);
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

    /** The template's own workflow, as far as telling it apart goes: publish.yml, building src/. */
    public const string TemplatesWorkflow =
        "name: publish-to-ency-store\non:\n  push:\n    tags: ['v*']\n  workflow_dispatch:\njobs:\n  publish:\n    steps:\n"
        + "      - run: dotnet build src -c Release -t:PackReady\n      - uses: EncySoftware/publish-extension-action@v1\n"
        + "        with:\n          folder: src/bin/Release\n";

    /**
     * A workflow of the author's own that publishes already — hand-made, or the template's renamed: there
     * is nothing to connect, and the template's tools are not named — update_extension would plant the
     * template's workflow beside it (review, 09.10.2026).
     */
    [Theory]
    [InlineData("publish.yml", "steps:\n  - uses: EncySoftware/publish-extension-action@v1\n")]
    [InlineData("release.yml", TemplatesWorkflow)]
    public async Task A_repository_that_publishes_through_a_workflow_of_its_own_has_nothing_to_connect(string file, string yaml)
    {
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Path.Combine(Workflows, file), yaml);
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith($"ERROR: this repository publishes to the store already, through .github/workflows/{file} (it uses "
                          + "publish-extension-action), so there is nothing to connect: a version is published by starting that workflow.", answer);
        Assert.DoesNotContain("update_extension", answer);
        Assert.DoesNotContain("publish_extension", answer);
        Assert.Empty(proc.Calls);
        Assert.False(File.Exists(Workflow));
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
    }

    /** The template's workflow with the project in src/: the template's tools take it from there. */
    [Fact]
    public async Task The_templates_workflow_with_the_project_in_src_is_sent_to_the_templates_tools()
    {
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Path.Combine(Workflows, "publish.yml"), TemplatesWorkflow);
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.Move(Csproj, Path.Combine(_repo, "src", DcoFixture.CsprojName));

        string answer = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: this repository publishes to the store already, through .github/workflows/publish.yml - the "
                          + "template's workflow, which builds src/ - so it needs no second one: publish_extension publishes a version", answer);
        Assert.Contains("update_extension brings the repository in line with the current template", answer);

        // ...and publish_extension does take it: the project is where the template's workflow builds.
        var push = new FakeProcessRunner().On("git status --porcelain").On("git add").On("git commit").On("git rev-parse", exit: 1)
            .On("git tag").On("git push").On("gh run list", stdout: "[]");
        string published = await new ExtensionStoreTools(push, _store, new FakeStoreAuth { Token = null }).PublishExtension("1.0.0", repoDir: _repo);
        Assert.StartsWith("Pushed v1.0.0", published);
        Assert.Contains(push.Calls, c => c.StartsWith("gh run list --workflow publish.yml "));
    }

    /**
     * The template's workflow with the project outside src/: connect_to_store used to send the author to
     * publish_extension, which refused the project outside src/ and sent them back here. Now both name
     * what does take it, and that route is followed to the end.
     */
    [Fact]
    public async Task The_templates_workflow_with_the_project_outside_src_sends_nobody_round_in_a_circle()
    {
        Directory.CreateDirectory(Workflows);
        string templates = Path.Combine(Workflows, "publish.yml");
        File.WriteAllText(templates, TemplatesWorkflow);

        string answer = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: .github/workflows/publish.yml is the template's workflow, which builds src/, and "
                          + "project/main/DirectCladdingOperationExtension.csproj is outside src/.", answer);
        Assert.Contains("Move the project into src/ and publish with publish_extension; or, to publish it where it is, delete "
                        + "publish.yml and call connect_to_store again", answer);

        // publish_extension, asked for the project where it is, names the same way out — not connect_to_store as it stands.
        var push = new FakeProcessRunner().On("git status --porcelain").On("git rev-parse", exit: 1);
        string refused = await new ExtensionStoreTools(push, _store, new FakeStoreAuth { Token = null }).PublishExtension("1.0.0", repoDir: ProjectDir);
        Assert.StartsWith($"ERROR: the project is at {ProjectDir}, but the template's workflow (.github/workflows/publish.yml) builds "
                          + "src/ — move the project under src/; or, to publish it where it is, delete publish.yml and let "
                          + "connect_to_store give this repository a workflow", refused);
        Assert.DoesNotContain(push.Calls, c => c.StartsWith("git tag") || c.StartsWith("git push"));

        // Followed: with the template's workflow gone, connect_to_store takes the project.
        File.Delete(templates);
        Assert.StartsWith("Connected", await Tools(Dotnet()).ConnectToStore(_repo));
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
        Assert.DoesNotContain("A new card stays out of the catalogue", answer);   // publish_package says that itself
        Assert.DoesNotContain("starts cold", answer);   // no run elsewhere: the publish packs on this machine
        Assert.Contains("the store refuses a publish from this machine that carries no Reserved Functionality answer", answer);
        Assert.Contains("The first publish with a new answer stops once with a link to confirm it in the browser; confirm, then publish again.", answer);
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
        Assert.Contains("since 2026-11-01 the store refuses a publish from CI that carries no Reserved Functionality answer.", answer);
        Assert.DoesNotContain("until then", answer);
    }

    /** A package that carries the answer already: said as such, with how the store takes a new one. */
    [Fact]
    public async Task A_package_that_carries_the_Schedule_A_answer_is_told_so()
    {
        string answer = await Tools(Dotnet(extra: ("build/package.info.json", "{\n  \"reservedDomains\": []\n}\n"))).ConnectToStore(_repo);

        Assert.Contains("Schedule A: the package carries the Reserved Functionality answer (none of the areas)." + Environment.NewLine
                        + "  The first run with a new answer stops once with a link to confirm it in the browser; confirm, then run "
                        + "the workflow again.", answer);
        Assert.DoesNotContain("refuses a publish from CI", answer);
        Assert.DoesNotContain("Pack it with", answer);
    }

    /** The earlier form of the answer: the key goes into the file the package carries, not into a second one. */
    [Fact]
    public async Task A_package_with_the_earlier_Schedule_A_block_is_told_to_answer_in_that_file()
    {
        string answer = await Tools(Dotnet(extra: ("build/package.info.json", "{ \"reservedFunctionality\": { \"none\": true } }"))).ConnectToStore(_repo);

        Assert.Contains("answer; until then it publishes with a warning. The package's package.info.json has it in an earlier or broken form."
                        + Environment.NewLine + "  The answer is `reservedDomains` in that file: [] when the extension provides none of", answer);
        Assert.DoesNotContain("Pack it with", answer);
        Assert.Contains("  This tool does not write it", answer);
    }

    /** The store reads the SDK as SemVer: 3.0.15-dev.3 beside 3.0.15 is 3.0.15, a release, and no stop. */
    [Fact]
    public async Task A_prerelease_SDK_in_package_info_json_beside_its_release_in_the_nuspec_stops_nothing()
    {
        string answer = await Tools(Dotnet(extra: ("build/package.info.json", "{ \"sdkVersion\": \"3.0.15-dev.3\", \"reservedDomains\": [] }")))
            .ConnectToStore(_repo);

        Assert.StartsWith("Connected", answer);
        Assert.DoesNotContain("a prerelease", answer);
        Assert.DoesNotContain("Before the first publish", answer);
    }

    // ---- branches of the answer and the refusals

    [Fact]
    public async Task A_project_that_builds_several_target_frameworks_is_refused_before_anything_is_written()
    {
        File.WriteAllBytes(Csproj, ConnectTools.Encode(DcoFixture.Text.Replace("<TargetFramework>net10.0-windows</TargetFramework>",
            "<TargetFrameworks>net8.0-windows;net10.0-windows</TargetFrameworks>"), bom: true));
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: project/main/DirectCladdingOperationExtension.csproj builds several target frameworks "
                          + "(net8.0-windows;net10.0-windows), and ENCY installs one dll per extension", answer);
        Assert.Empty(proc.Calls);
        Assert.False(File.Exists(Workflow));
    }

    /** No framework in the csproj: the Directory.Build.props MSBuild imports from above it says. */
    [Fact]
    public async Task The_target_framework_is_read_from_Directory_Build_props_above_the_project()
    {
        File.WriteAllBytes(Csproj, ConnectTools.Encode(DcoFixture.Text.Replace("    <TargetFramework>net10.0-windows</TargetFramework>\r\n", ""), bom: true));
        File.WriteAllText(Path.Combine(_repo, "Directory.Build.props"),
            "<Project>\n  <PropertyGroup>\n    <TargetFramework>net8.0-windows</TargetFramework>\n  </PropertyGroup>\n</Project>\n");

        string answer = await Tools(Dotnet()).ConnectToStore(_repo);

        Assert.StartsWith("Connected", answer);
        Assert.Contains("dotnet-version: '8.0.x'", File.ReadAllText(Workflow));
    }

    [Fact]
    public async Task A_target_framework_set_from_a_property_is_refused()
    {
        File.WriteAllBytes(Csproj, ConnectTools.Encode(DcoFixture.Text.Replace("net10.0-windows</TargetFramework>", "$(EncyTfm)</TargetFramework>"), bom: true));
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: project/main/DirectCladdingOperationExtension.csproj sets the target framework from an MSBuild "
                          + "property ($(EncyTfm)), so the workflow cannot tell which .NET to install.", answer);
        Assert.Empty(proc.Calls);
    }

    [Fact]
    public async Task A_csproj_outside_the_repository_is_refused()
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), "mcp-elsewhere-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(elsewhere);
        try
        {
            string outside = Path.Combine(elsewhere, DcoFixture.CsprojName);
            File.WriteAllBytes(outside, DcoFixture.Bytes);
            var proc = Dotnet();

            string answer = await Tools(proc).ConnectToStore(_repo, outside);

            Assert.StartsWith($"ERROR: {outside} is outside the repository {_repo} - the workflow can build only what the repository holds.", answer);
            Assert.Empty(proc.Calls);
            Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(outside));
        }
        finally { Directory.Delete(elsewhere, true); }
    }

    /** A folder name the workflow's bash step would read as its own ($, a quote, a backtick or \). */
    [Fact]
    public async Task A_project_path_the_workflows_shell_would_read_is_refused()
    {
        string odd = Path.Combine(_repo, "pro$ject");
        Directory.CreateDirectory(odd);
        File.Move(Csproj, Path.Combine(odd, DcoFixture.CsprojName));
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: the project's path pro$ject/DirectCladdingOperationExtension.csproj holds a character the workflow's "
                          + "shell would read as its own", answer);
        Assert.Empty(proc.Calls);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Path.Combine(odd, DcoFixture.CsprojName)));
    }

    [Fact]
    public async Task A_pack_that_writes_two_packages_is_not_one_the_workflow_can_publish()
    {
        var proc = new FakeProcessRunner().On("dotnet pack", args =>
        {
            string output = Regex.Match(args, "-o \"([^\"]+)\"").Groups[1].Value;
            DcoFixture.WritePackage(Path.Combine(output, "EncySoftware.DirectCladdingOperation.0.0.0-storecheck.nupkg"), "build");
            DcoFixture.WritePackage(Path.Combine(output, "EncySoftware.DirectCladdingOperation.Core.0.0.0-storecheck.nupkg"), "build");
            return new ProcResult(0, "", "");
        });

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR: dotnet pack wrote 2 packages (", answer);
        Assert.Contains("; the workflow publishes one.", answer);
        Assert.EndsWith("The csproj and the workflow were left as they were.", answer);
        Assert.Equal(DcoFixture.Bytes, File.ReadAllBytes(Csproj));
        Assert.False(File.Exists(Workflow));
    }

    /**
     * Found by its own text only, a project is missed when its SDK reference sits in Directory.Build.props
     * (or Directory.Packages.props), or its manifest item does not say Pack or PackagePath in its start
     * tag. Pointed at its folder, the one csproj there is taken; pointed at nothing in particular, the
     * answer asks for csproj= — not for the folder, which used to run the same search again.
     */
    [Fact]
    public async Task A_project_the_search_misses_is_taken_from_the_folder_named_and_otherwise_asked_for_by_csproj()
    {
        File.Delete(Csproj);
        string ext = Path.Combine(_repo, "ext");
        Directory.CreateDirectory(ext);
        File.WriteAllText(Path.Combine(ext, "Ext.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0-windows</TargetFramework>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <None Update=\"Ext.settings.json\">\n      <Pack>true</Pack>\n      <PackagePath>build\\</PackagePath>\n"
            + "      <CopyToOutputDirectory>Always</CopyToOutputDirectory>\n    </None>\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(_repo, "Directory.Build.props"),
            "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"EncySoftware.CAMAPI.SDK.Net\" Version=\"3.0.15\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(ext, "Ext.settings.json"), "{}");
        var none = Dotnet();

        string asked = await Tools(none).ConnectToStore(_repo);

        Assert.StartsWith($"ERROR: no extension project found under {_repo} - no csproj there references EncySoftware.CAMAPI.SDK.Net "
                          + "or packs a *.settings.json. Name the project with csproj=<its .csproj>.", asked);
        Assert.DoesNotContain("Point at", asked);
        Assert.Empty(none.Calls);

        string taken = await Tools(Dotnet()).ConnectToStore(ext);

        Assert.StartsWith("Connected ext/Ext.csproj", taken);
        Assert.Contains("dotnet pack \"ext/Ext.csproj\"", File.ReadAllText(Workflow));
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
