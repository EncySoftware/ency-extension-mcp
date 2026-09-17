using EncyExtensionMcp;
using Xunit;

/**
 * A project that was not made from the template: a csproj of its own, no package.info.json, no
 * marker tag. Every publish route has to take it as it is and say what it wrote (17.09.2026).
 */
public class AdoptProjectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp-adopt-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _log = new();

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0-windows</TargetFramework>
            <AssemblyName>ToolpathTimer</AssemblyName>
            <Version>1.4.0</Version>
            <Description>Times every toolpath</Description>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="EncySoftware.CAMAPI.Sdk.Net" Version="[3.0.8]" />
          </ItemGroup>
        </Project>
        """;

    public AdoptProjectTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ToolpathTimer.csproj"), Csproj);
        File.WriteAllText(Path.Combine(_dir, "ToolpathTimer.settings.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "Extension.cs"), "class E {}");
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void TheManifestSkeletonComesFromTheCsproj()
    {
        string json = ProjectLayout.ManifestSkeleton(Csproj, Path.Combine(_dir, "ToolpathTimer.csproj"));
        Assert.Contains("\"packageId\": \"ToolpathTimer\"", json);
        Assert.Contains("\"version\": \"1.4.0\"", json);
        Assert.Contains("\"sdkVersion\": \"3.0.8\"", json);
        Assert.Contains("\"targetFramework\": \"net10.0\"", json);
        Assert.Contains("\"description\": \"Times every toolpath\"", json);
        Assert.True(ProjectLayout.HasMarker(json));
        // Readable by the same parser the preflight uses.
        System.Text.Json.JsonDocument.Parse(json);
    }

    [Fact]
    public void TheMarkerJoinsExistingTagsOrGetsALineOfItsOwn()
    {
        Assert.Equal("{\n  \"tags\": \"Utility, ency-extension\"\n}", ProjectLayout.WithMarker("{\n  \"tags\": \"Utility\"\n}"));
        Assert.Equal("{\n  \"packageId\": \"X\",\n  \"tags\": \"ency-extension\",\n  \"version\": \"1\"\n}",
            ProjectLayout.WithMarker("{\n  \"packageId\": \"X\",\n  \"version\": \"1\"\n}"));
        string already = "{\n  \"tags\": \"Ency-Extension\"\n}";
        Assert.Same(already, ProjectLayout.WithMarker(already));
    }

    [Fact]
    public void LocatePrefersSrcThenTheFolderAndRefusesTwoProjects()
    {
        var p = ProjectLayout.Locate(_dir, out var err);
        Assert.NotNull(p);
        Assert.Null(err);
        Assert.Equal(_dir, p!.Dir);

        File.WriteAllText(Path.Combine(_dir, "Second.csproj"), "<Project />");
        Assert.Null(ProjectLayout.Locate(_dir, out err));
        Assert.Contains("several projects", err);
    }

    [Fact]
    public void LocateAboveFindsTheProjectOfABuildOutput()
    {
        string bin = Path.Combine(_dir, "bin", "Release", "net10.0-windows");
        Directory.CreateDirectory(bin);
        var p = ProjectLayout.LocateAbove(bin);
        Assert.NotNull(p);
        Assert.Equal(Path.Combine(_dir, "ToolpathTimer.csproj"), p!.Csproj);
        Assert.Null(ProjectLayout.LocateAbove(Path.GetTempPath(), levels: 0));
    }

    /** publish_package with the build output of such a project: the manifest is written next to the csproj and goes up with the files. */
    [Fact]
    public async Task PublishPackageAdoptsTheBuildOutputOfAForeignProject()
    {
        string bin = Path.Combine(_dir, "bin", "Release", "net10.0-windows");
        Directory.CreateDirectory(bin);
        foreach (var f in new[] { "ToolpathTimer.dll", "ToolpathTimer.settings.json" }) File.WriteAllText(Path.Combine(bin, f), "x");
        var store = new FakeStoreClient();

        string answer = await new LocalPublishTools(store, new FakeStoreAuth(), _log.Add).PublishPackage(bin);

        Assert.DoesNotContain("ERROR", answer);
        Assert.Contains("- adopted: package.info.json written from the csproj", answer);
        Assert.True(File.Exists(Path.Combine(_dir, "package.info.json")));
        var sent = store.UploadedFiles.Select(f => f.FileName).ToList();
        Assert.Contains("package.info.json", sent);
        Assert.Contains("ToolpathTimer.dll", sent);
        Assert.Single(store.Published);
    }

    [Fact]
    public async Task PublishPackageGivesAManifestWithoutTheMarkerTheTag()
    {
        File.WriteAllText(Path.Combine(_dir, "package.info.json"), "{\n  \"packageId\": \"ToolpathTimer\",\n  \"version\": \"2.0.0\",\n  \"tags\": \"Utility\"\n}");
        string bin = Path.Combine(_dir, "bin", "Release", "net10.0-windows");
        Directory.CreateDirectory(bin);
        foreach (var f in new[] { "ToolpathTimer.dll", "ToolpathTimer.settings.json", "package.info.json" }) File.WriteAllText(Path.Combine(bin, f), "{\"tags\": \"Utility\"}");
        var store = new FakeStoreClient();

        string answer = await new LocalPublishTools(store, new FakeStoreAuth(), _log.Add).PublishPackage(bin);

        Assert.DoesNotContain("ERROR", answer);
        Assert.Contains("- adopted: `ency-extension` added", answer);
        Assert.Contains("\"tags\": \"Utility, ency-extension\"", File.ReadAllText(Path.Combine(_dir, "package.info.json")));
        // The project's manifest went up, not the stale copy from the output.
        var manifest = store.UploadedFiles.Single(f => f.FileName == "package.info.json");
        Assert.Contains("ency-extension", System.Text.Encoding.UTF8.GetString(manifest.Bytes));
    }

    [Fact]
    public async Task PublishPackageWithoutAnyProjectAboveSaysSo()
    {
        string bin = Path.Combine(Path.GetTempPath(), "mcp-orphan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(bin);
        foreach (var f in new[] { "Orphan.dll", "Orphan.settings.json" }) File.WriteAllText(Path.Combine(bin, f), "x");
        try
        {
            string answer = await new LocalPublishTools(new FakeStoreClient(), new FakeStoreAuth(), _log.Add).PublishPackage(bin);
            Assert.StartsWith("ERROR", answer);
            Assert.Contains("no csproj above it", answer);
        }
        finally { Directory.Delete(bin, true); }
    }

    /** publish_folder: the folder is adopted before it is read, so the manifest is among the sources that go to the repository. */
    [Fact]
    public async Task PublishFolderAdoptsAForeignProject()
    {
        var store = new FakeStoreClient();
        store.Builds.Enqueue(Array.Empty<BuildReport>());
        store.Builds.Enqueue(new[] { new BuildReport("someone/ToolpathTimer", "ToolpathTimer", "PUBLISHED", "1.4.0", null, null, null, "t2") });
        var tools = new FolderPublishTools(store, new FakeStoreAuth(), _ => Task.CompletedTask, _ => Task.CompletedTask, _log.Add,
            () => Task.FromResult(Array.Empty<byte>()));

        string answer = await tools.PublishFolder("ToolpathTimer", _dir, waitForResult: false);

        Assert.DoesNotContain("ERROR", answer);
        Assert.Contains("- adopted: package.info.json written from the csproj", answer);
        var upload = Assert.Single(store.Uploads);
        Assert.Contains(upload.Files, f => f.Path == "package.info.json");
    }

    /** update_extension on such a project: the manifest appears, and the answer says so. */
    [Fact]
    public async Task UpdateExtensionAdoptsAForeignProject()
    {
        var store = new FakeStoreClient { RecommendedSdk = "3.0.8" };
        var tools = new FolderPublishTools(store, new FakeStoreAuth(), _ => Task.CompletedTask, _ => Task.CompletedTask, _log.Add,
            () => throw new HttpRequestException("offline"));

        string answer = await tools.UpdateExtension(_dir);

        Assert.StartsWith("Updated:", answer);
        Assert.Contains("package.info.json written from the csproj", answer);
        Assert.True(File.Exists(Path.Combine(_dir, "package.info.json")));
    }

    /** publish_extension: the manifest is written under src/ and committed before the tag. */
    [Fact]
    public async Task PublishExtensionAdoptsAndCommitsBeforeTagging()
    {
        string repo = Path.Combine(Path.GetTempPath(), "mcp-adopt-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(repo, "src", "ToolpathTimer.csproj"), Csproj);
        var proc = new FakeProcessRunner()
            .On("git status --porcelain", stdout: "")
            .On("git add src/package.info.json")
            .On("git commit -m \"Add the store manifest\"")
            .On("git rev-parse", exit: 1)
            .On("git tag")
            .On("git push origin HEAD")
            .On("git push origin v1.4.0")
            .On("gh run list", stdout: "[]");
        try
        {
            string answer = await new ExtensionStoreTools(proc, new FakeStoreClient(), new StoreTokenProvider()).PublishExtension("1.4.0", repo);

            Assert.DoesNotContain("ERROR", answer);
            Assert.Contains("package.info.json written from the csproj", answer);
            Assert.True(File.Exists(Path.Combine(repo, "src", "package.info.json")));
            int commit = proc.Calls.FindIndex(c => c.StartsWith("git commit -m \"Add the store manifest\""));
            int tag = proc.Calls.FindIndex(c => c == "git tag v1.4.0");
            Assert.True(commit >= 0 && commit < tag, "the manifest is committed before the tag");
        }
        finally { Directory.Delete(repo, true); }
    }

    [Fact]
    public async Task PublishExtensionRefusesAProjectOutsideSrc()
    {
        var proc = new FakeProcessRunner().On("git status --porcelain", stdout: "");
        string answer = await new ExtensionStoreTools(proc, new FakeStoreClient(), new StoreTokenProvider()).PublishExtension("1.0.0", _dir);
        Assert.StartsWith("ERROR", answer);
        Assert.Contains("src/", answer);
        Assert.DoesNotContain(proc.Calls, c => c.StartsWith("git tag"));
    }
}
