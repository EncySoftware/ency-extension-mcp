using EncyExtensionMcp;
using Xunit;

/**
 * publish_package: publishing what the author built on their own machine, with no git, no GitHub
 * and no second repository holding a copy of the sources.
 *
 * <para>Asked for by an author on 15.09.2026: the sources live in their own repository and the
 * packages are built on their machine. `publish_extension` tags and waits for GitHub Actions,
 * `publish_folder` creates a repository through the store's app — both routes need a repository,
 * which left uploading the `.nupkg` through the website for every version.</para>
 */
public class LocalPublishToolTests
{
    private readonly List<string> _log = new();

    private LocalPublishTools Tools(FakeStoreClient store, FakeStoreAuth? auth = null) =>
        new(store, auth ?? new FakeStoreAuth(), _log.Add);

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "encylocal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteNupkg(string dir, string name = "MyExt.0.1.0.nupkg")
    {
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 3, 4, 9 });   // the store parses the content, not the tool
        return path;
    }

    [Fact]
    public async Task A_ready_package_is_staged_and_published()
    {
        var store = new FakeStoreClient();
        string dir = TempDir();
        string nupkg = WriteNupkg(dir);

        string answer = await Tools(store).PublishPackage(nupkg);

        Assert.Equal(new[] { "MyExt.0.1.0.nupkg" }, store.StagedNupkgs.Select(s => s.FileName).ToArray());
        Assert.Single(store.Published);
        Assert.Equal("MyExt", store.Published[0].PackageId);
        Assert.Equal("0.1.0", store.Published[0].Version);
        Assert.Contains("https://store.test/extension/myext", answer);
        Assert.DoesNotContain("ERROR", answer);
    }

    /** A build folder: the files go up one by one and the store packs them — the CI route. */
    [Fact]
    public async Task A_build_output_folder_is_packed_by_the_store()
    {
        var store = new FakeStoreClient();
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "MyExt.dll"), "binary");
        File.WriteAllText(Path.Combine(dir, "MyExt.settings.json"), "{\"name\":\"My Ext\"}");
        File.WriteAllText(Path.Combine(dir, "package.info.json"), "{\"packageId\":\"MyExt\",\"version\":\"0.1.0\"}");
        File.WriteAllText(Path.Combine(dir, "readme.md"), "# My Ext");

        string answer = await Tools(store).PublishPackage(dir, version: "0.2.0");

        Assert.Equal(4, store.UploadedFiles.Count);
        Assert.Contains("MyExt.dll", store.UploadedFiles.Select(f => f.FileName));
        Assert.Single(store.Packed);
        Assert.Equal("0.2.0", store.Packed[0].Version);
        Assert.Single(store.Published);
        Assert.DoesNotContain("ERROR", answer);
    }

    /** A build folder has a required minimum; without it the refusal comes BEFORE the upload. */
    [Fact]
    public async Task A_folder_without_a_manifest_is_refused_before_anything_is_uploaded()
    {
        var store = new FakeStoreClient();
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "MyExt.dll"), "binary");

        string answer = await Tools(store).PublishPackage(dir);

        Assert.StartsWith("ERROR", answer);
        Assert.Contains("settings.json", answer);
        Assert.Empty(store.UploadedFiles);
        Assert.Empty(store.Published);
    }

    /** A package without the marker is never picked up by the indexer, so it is not published — said up front. */
    [Fact]
    public async Task A_package_without_the_marker_tag_is_not_published()
    {
        var store = new FakeStoreClient { StagedHasMarker = false };
        string nupkg = WriteNupkg(TempDir());

        string answer = await Tools(store).PublishPackage(nupkg);

        Assert.StartsWith("ERROR", answer);
        Assert.Contains("ency-extension", answer);
        Assert.Empty(store.Published);
    }

    /** The store's refusal is repeated in its own words — that is what the author fixes by. */
    [Fact]
    public async Task The_store_refusal_is_repeated_in_its_own_words()
    {
        var store = new FakeStoreClient { StageFailure = "The package has no *.settings.json manifest or no .dll inside" };
        string nupkg = WriteNupkg(TempDir());

        string answer = await Tools(store).PublishPackage(nupkg);

        Assert.StartsWith("ERROR", answer);
        Assert.Contains("no *.settings.json manifest", answer);
    }

    /** Not signed in — nothing is published, and the fix is named. */
    [Fact]
    public async Task Without_a_store_sign_in_it_says_so_instead_of_failing_obscurely()
    {
        var store = new FakeStoreClient();
        string nupkg = WriteNupkg(TempDir());

        string answer = await Tools(store, new FakeStoreAuth { Token = null, LoginSucceeds = false }).PublishPackage(nupkg);

        Assert.StartsWith("ERROR", answer);
        Assert.Contains("sign", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Published);
    }

    /** Neither a file nor a folder at that path — the answer is about the path, not the store. */
    [Fact]
    public async Task A_path_that_is_neither_a_package_nor_a_folder_is_named()
    {
        var store = new FakeStoreClient();
        string answer = await Tools(store).PublishPackage(Path.Combine(TempDir(), "nothing-here.nupkg"));

        Assert.StartsWith("ERROR", answer);
        Assert.Contains("nothing-here.nupkg", answer);
        Assert.Empty(store.Published);
    }
}
