using EncyExtensionMcp;
using Xunit;

public class PublishToolTests
{
    private static ExtensionStoreTools Tools(FakeProcessRunner proc, FakeStoreClient? store = null)
        => new(proc, store ?? new FakeStoreClient(), new StoreTokenProvider());

    [Fact]
    public async Task RejectsNonSemver()
    {
        var res = await Tools(new FakeProcessRunner()).PublishExtension("not-a-version");
        Assert.StartsWith("ERROR", res);
        Assert.Contains("semver", res);
    }

    [Fact]
    public async Task RefusesDirtyTreeWithoutCommitAll()
    {
        var proc = new FakeProcessRunner().On("git status --porcelain", stdout: " M src/Extension.cs\n");
        var res = await Tools(proc).PublishExtension("1.0.0");
        Assert.StartsWith("ERROR", res);
        Assert.Contains("uncommitted", res);
        Assert.DoesNotContain(proc.Calls, c => c.StartsWith("git tag"));
    }

    [Fact]
    public async Task CommitAllCommitsThenTags()
    {
        var proc = new FakeProcessRunner()
            .On("git status --porcelain", stdout: " M x\n")
            .On("git add -A")
            .On("git commit")
            .On("git rev-parse", exit: 1) // tag does not exist
            .On("git tag")
            .On("git push origin HEAD")
            .On("git push origin v1.0.0")
            .On("gh run list", stdout: "[]");
        var res = await Tools(proc).PublishExtension("1.0.0", commitAll: true);
        Assert.DoesNotContain("ERROR", res);
        Assert.Contains(proc.Calls, c => c.StartsWith("git commit -m \"Release v1.0.0\""));
        Assert.Contains(proc.Calls, c => c == "git tag v1.0.0");
        Assert.Contains(proc.Calls, c => c == "git push origin v1.0.0");
    }

    [Fact]
    public async Task RefusesExistingTag()
    {
        var proc = new FakeProcessRunner()
            .On("git status --porcelain", stdout: "")
            .On("git rev-parse", exit: 0, stdout: "abc123"); // tag exists
        var res = await Tools(proc).PublishExtension("1.0.0");
        Assert.StartsWith("ERROR", res);
        Assert.Contains("already exists", res);
    }

    [Fact]
    public async Task StatusReportsPendingModeration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcp-st-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "package.info.json"), "{\"packageId\":\"MyExt\"}");
        try
        {
            var proc = new FakeProcessRunner().On("gh run list",
                stdout: "[{\"databaseId\":42,\"status\":\"completed\",\"conclusion\":\"success\",\"url\":\"https://gh/run/42\"}]");
            var store = new FakeStoreClient { Card = new StoreCard("myext", Approved: false, Unlisted: false, "0.1.0") };
            var res = await Tools(proc, store).PublishStatus(dir);
            Assert.Contains("awaiting store moderation", res);
            Assert.Contains("https://store.test/extension/myext", res);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StatusShowsFailedLogTail()
    {
        var proc = new FakeProcessRunner()
            .On("gh run list",
                stdout: "[{\"databaseId\":7,\"status\":\"completed\",\"conclusion\":\"failure\",\"url\":\"https://gh/run/7\"}]")
            .On("gh run view 7 --log-failed", stdout: "boom: pack produced no .nupkg");
        var res = await Tools(proc).PublishStatus(Path.GetTempPath());
        Assert.Contains("failed", res);
        Assert.Contains("pack produced no .nupkg", res);
    }

    /** "publish it" with no version: the tool reads the tags itself instead of asking. */
    [Fact]
    public async Task PublishesTheNextPatchWhenNoVersionIsGiven()
    {
        var proc = new FakeProcessRunner()
            .On("git tag --list", stdout: "v0.1.8\nv0.1.7\n")
            .On("git status --porcelain")
            .On("git rev-parse -q --verify refs/tags/v0.1.9", exit: 1)   // tag is free
            .On("git tag v0.1.9")
            .On("git push")
            .On("gh run list", stdout: "[]");

        var res = await Tools(proc).PublishExtension();

        Assert.Contains("Pushed v0.1.9", res);
        Assert.Contains("no version given", res);
    }

    [Fact]
    public async Task StillRejectsAVersionThatIsNotSemver()
    {
        var res = await Tools(new FakeProcessRunner()).PublishExtension("1.2");
        Assert.StartsWith("ERROR", res);
    }

    /** Authors type "v1.2.3" as often as "1.2.3" — the tag must not become vv1.2.3. */
    [Fact]
    public async Task AcceptsAVersionWrittenWithTheVPrefix()
    {
        var proc = new FakeProcessRunner()
            .On("git status --porcelain")
            .On("git rev-parse -q --verify refs/tags/v2.0.0", exit: 1)
            .On("git tag v2.0.0")
            .On("git push")
            .On("gh run list", stdout: "[]");

        Assert.Contains("Pushed v2.0.0", await Tools(proc).PublishExtension("v2.0.0"));
    }

    [Fact]
    public async Task CreateRejectsBadName()
    {
        var res = await Tools(new FakeProcessRunner()).CreateExtensionRepo("bad name!");
        Assert.StartsWith("ERROR", res);
    }

    /// <summary>
    /// The default bootstrap must be "no credential in GitHub": claiming the name binds the repository,
    /// and the workflow then authenticates with its own OIDC token — including on the first publish,
    /// which previously forced a long-lived store token into the repository secrets.
    /// </summary>
    [Fact]
    public async Task ClaimingTheNameReplacesTheRepositorySecret()
    {
        var store = new FakeStoreClient();
        var proc = new FakeProcessRunner();
        string note = await Tools(proc, store).BootstrapPublisherAuth("MyExt", "acme/MyExt", "tok");

        Assert.Equal(("MyExt", "acme/MyExt"), store.Claims.Single());
        Assert.Contains("no repository secret", note);
        Assert.DoesNotContain("ENCY_STORE_TOKEN", note);
    }

    [Fact]
    public async Task FallsBackToTheSecretWhenTheNameCannotBeClaimed()
    {
        var store = new FakeStoreClient { ClaimFailure = "403 Forbidden: claimed by someone else" };
        var proc = new FakeProcessRunner().On("gh secret set ENCY_STORE_TOKEN");
        string note = await Tools(proc, store).BootstrapPublisherAuth("Taken", "acme/Taken", "tok");

        Assert.Contains("could not claim", note);
        Assert.Contains("ENCY_STORE_TOKEN", note);
        Assert.False(note.StartsWith("!"), "the fallback worked, so this is not an error note");
    }

    [Fact]
    public async Task SaysWhatToDoWhenThereIsNoStoreLogin()
    {
        string note = await Tools(new FakeProcessRunner(), new FakeStoreClient())
            .BootstrapPublisherAuth("MyExt", "acme/MyExt", token: null);
        Assert.StartsWith("!", note);
        Assert.Contains("ency-extension-mcp login", note);
    }

    [Fact]
    public async Task CreateFailsFastWithoutGhAuth()
    {
        var proc = new FakeProcessRunner().On("gh api user", exit: 1, stderr: "not logged in");
        var res = await Tools(proc).CreateExtensionRepo("GoodName",
            targetDir: Path.Combine(Path.GetTempPath(), "mcp-cr-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith("ERROR", res);
        // Отказ обязан вести к пути без консоли РАНЬШЕ, чем к `gh auth login`: человек без консольной
        // привычки читает первое предложение и идёт по нему (Андрей, 02.09.2026).
        Assert.Contains("Use this template", res);
        Assert.Contains("Run workflow", res);
        Assert.True(res.IndexOf("Use this template", StringComparison.Ordinal)
                    < res.IndexOf("gh auth login", StringComparison.Ordinal),
            "путь через браузер должен стоять раньше консольного");
        Assert.Contains("gh auth login", res);
    }
}
