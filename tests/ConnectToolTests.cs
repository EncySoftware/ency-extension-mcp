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
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "mcp-connect-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _log = new();

    private string ProjectDir => Path.Combine(_repo, "project", "main");
    private string Csproj => Path.Combine(ProjectDir, DcoFixture.CsprojName);
    private string Bin => Path.Combine(ProjectDir, "bin", "Release", "net10.0-windows");
    private string Workflows => Path.Combine(_repo, ".github", "workflows");
    private string Workflow => Path.Combine(Workflows, StoreWorkflow.FileName);

    public ConnectToolTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(ProjectDir);
        File.WriteAllBytes(Csproj, DcoFixture.Bytes);
        DcoFixture.WriteBuildOutput(Bin);
    }

    public void Dispose() { try { Directory.Delete(_repo, true); } catch { } }

    /** `dotnet pack` writes a package laid out as `layout` into its -o folder; MSBuild says the build went to Bin. */
    private FakeProcessRunner Dotnet(string layout = "build", List<string>? outputs = null, string version = ConnectTools.ProbeVersion,
                                     string tags = "ency-extension operationsolver") =>
        new FakeProcessRunner()
            .On("dotnet pack", args =>
            {
                string output = Regex.Match(args, "-o \"([^\"]+)\"").Groups[1].Value;
                outputs?.Add(output);
                DcoFixture.WritePackage(Path.Combine(output, $"EncySoftware.DirectCladdingOperation.{version}.nupkg"), layout, version, tags);
                return new ProcResult(0, "Successfully created package.", "");
            })
            .On("dotnet msbuild", _ => new ProcResult(0, JsonSerializer.Serialize(new
            {
                Properties = new { TargetDir = Bin + Path.DirectorySeparatorChar, TargetFileName = DcoFixture.Dll },
            }), ""));

    private ConnectTools Tools(FakeProcessRunner proc, DateOnly? today = null) =>
        new(proc, new FakeUpdateCheck(null), _log.Add, () => today ?? new DateOnly(2026, 10, 15));

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

        Assert.StartsWith("Connected project/main/DirectCladdingOperationExtension.csproj (EncySoftware.DirectCladdingOperation)", answer);
        Assert.Contains("line 25 (DirectCladdingOperation.smarthint): PackagePath \"contentFiles\\any\\any\\\" -> \"build\\\"", answer);
        Assert.Contains("line 29 (SmartHintImages\\*.png): PackagePath \"contentFiles\\any\\any\\SmartHintImages\\\" -> \"build\\\"", answer);
        Assert.Contains(".github/workflows/publish_store.yml: added", answer);
        Assert.Contains($"7 files land where the Release build has them (README.md and icon.png at the root", answer);
        Assert.Contains("DirectCladdingOperationExtension.settings.json finds DirectCladdingOperationExtension.dll", answer);

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

        // What only the author can do, and how to publish.
        Assert.Contains("Sign in at https://apps.encycam.com.", answer);
        Assert.Contains("https://apps.encycam.com/publish: the developer registration", answer);
        Assert.Contains("\"Sign in with GitHub\"", answer);
        Assert.Contains("Own the card: EncySoftware.DirectCladdingOperation", answer);
        Assert.Contains("git tag vX.Y.Z, then git push origin vX.Y.Z", answer);
        Assert.Contains("Run workflow", answer);
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

        Assert.Contains("nothing to change - it packs no runtime file under contentFiles/", again);
        Assert.Contains("publish_store.yml: already there as this tool writes it", again);
        Assert.Equal(csproj, File.ReadAllBytes(Csproj));
        Assert.Equal(yaml, File.ReadAllText(Workflow));
    }

    [Fact]
    public async Task An_existing_publish_store_yml_is_never_overwritten()
    {
        Directory.CreateDirectory(Workflows);
        File.WriteAllText(Workflow, "name: mine\n");

        // Started from the project folder: the repository is found above it.
        string answer = await Tools(Dotnet()).ConnectToStore(ProjectDir);

        Assert.StartsWith("Connected", answer);
        Assert.Contains("publish_store.yml: already there and different from what this tool would write - left as it is", answer);
        Assert.Equal("name: mine\n", File.ReadAllText(Workflow));
        Assert.Equal(DcoFixture.FixedBytes, File.ReadAllBytes(Csproj));
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

    [Fact]
    public async Task Outside_a_git_repository_nothing_is_done()
    {
        if (ConnectTools.RepoRoot(Path.GetTempPath()) != null) return;   // a machine whose temp folder sits in a repository
        Directory.Delete(Path.Combine(_repo, ".git"));
        var proc = Dotnet();

        string answer = await Tools(proc).ConnectToStore(_repo);

        Assert.StartsWith("ERROR", answer);
        Assert.Contains("not inside a git repository", answer);
        Assert.Contains("publish_package", answer);
        Assert.Empty(proc.Calls);
    }

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
    }
}
