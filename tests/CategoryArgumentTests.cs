using EncyExtensionMcp;
using Xunit;

/// <summary>
/// The category an author names in Cursor.
///
/// <para>It is written into src/package.info.json rather than passed to the publish, because a tag
/// push carries nothing but a name — the server packer turns that field into the `category:` tag the
/// store reads. These cases hold the two things that make it safe: the author's file is edited by a
/// single line, and a refused publish leaves nothing behind.</para>
/// </summary>
public class CategoryArgumentTests
{
    private const string Manifest = """
        {
          "packageId": "MyExt",
          "version": "0.1.0",
          "category": "other",
          "tags": "Utility, ency-extension"
        }
        """;

    [Fact]
    public void ReplacesTheValueAndTouchesNothingElse()
    {
        var after = PackageInfo.WithCategory(Manifest, "analyzer");
        Assert.Contains("\"category\": \"analyzer\"", after);
        Assert.Equal(Manifest.Replace("\"other\"", "\"analyzer\""), after);
    }

    [Fact]
    public void SameValueIsNotAChange()
        => Assert.Equal(Manifest, PackageInfo.WithCategory(Manifest, "other"));

    [Fact]
    public void InsertsUnderPackageIdWhenTheKeyIsMissing()
    {
        var without = """
            {
              "packageId": "MyExt",
              "version": "0.1.0"
            }
            """;
        var after = PackageInfo.WithCategory(without, "machine");
        Assert.Contains("  \"category\": \"machine\",", after);
        // Under packageId, not at the end: that is where the template keeps it, and a key where the
        // author expects it reads as an edit rather than as damage.
        Assert.True(after.IndexOf("\"category\"") > after.IndexOf("\"packageId\""));
        Assert.True(after.IndexOf("\"category\"") < after.IndexOf("\"version\""));
    }

    [Fact]
    public void AShapeWeDoNotRecogniseIsLeftAlone()
    {
        const string odd = "{ \"weird\": true }";
        Assert.Equal(odd, PackageInfo.WithCategory(odd, "analyzer"));
    }

    [Fact]
    public async Task UnknownCategoryIsRefusedBeforeAnythingIsWritten()
    {
        var dir = TempRepo();
        var proc = new FakeProcessRunner().On("git status --porcelain");
        var res = await new ExtensionStoreTools(proc, new FakeStoreClient(), new StoreTokenProvider())
            .PublishExtension("1.0.0", dir, category: "milling");

        Assert.StartsWith("ERROR", res);
        Assert.Contains("no category 'milling'", res);
        Assert.Contains("analyzer", res);   // the answer names what IS allowed
        Assert.Contains("\"category\": \"other\"", File.ReadAllText(PackageInfo.PathIn(dir)));
        Assert.DoesNotContain(proc.Calls, c => c.StartsWith("git tag"));
    }

    [Fact]
    public async Task ARefusedPublishLeavesTheManifestUntouched()
    {
        var dir = TempRepo();
        // Dirty tree without commitAll: the publish stops before the category is written, so the
        // author is not left with an edit they never asked to keep.
        var proc = new FakeProcessRunner().On("git status --porcelain", stdout: " M src/Extension.cs\n");
        var res = await new ExtensionStoreTools(proc, new FakeStoreClient(), new StoreTokenProvider())
            .PublishExtension("1.0.0", dir, category: "analyzer");

        Assert.StartsWith("ERROR", res);
        Assert.Contains("\"category\": \"other\"", File.ReadAllText(PackageInfo.PathIn(dir)));
    }

    [Fact]
    public async Task WritesAndCommitsTheCategoryBeforeTagging()
    {
        var dir = TempRepo();
        var proc = new FakeProcessRunner()
            .On("git status --porcelain")
            .On("git add src/package.info.json")
            .On("git commit")
            .On("git rev-parse", exit: 1)
            .On("git tag")
            .On("git push")
            .On("gh run list", stdout: "[]");

        var res = await new ExtensionStoreTools(proc, new FakeStoreClient(), new StoreTokenProvider())
            .PublishExtension("1.0.0", dir, category: "Analyzer");   // case is the author's business

        Assert.DoesNotContain("ERROR", res);
        Assert.Contains("\"category\": \"analyzer\"", File.ReadAllText(PackageInfo.PathIn(dir)));
        Assert.Contains(proc.Calls, c => c.StartsWith("git commit -m \"Set the store category to analyzer\""));
        var commit = proc.Calls.FindIndex(c => c.StartsWith("git commit -m \"Set the store category"));
        var tag = proc.Calls.FindIndex(c => c.StartsWith("git tag"));
        Assert.True(commit < tag, "the category must be in the commit the tag points at");
    }

    private static string TempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ency-cat-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(PackageInfo.PathIn(dir), Manifest);
        return dir;
    }
}
