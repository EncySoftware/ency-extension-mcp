using EncyExtensionMcp;
using Xunit;

/**
 * publish_folder: the route with no git and no gh. The store does the GitHub work; the tool signs
 * in through the browser, opens the app's consent page when needed, and waits — counting polls, so
 * these tests run with an instant delay.
 */
public class FolderPublishToolTests
{
    private readonly List<string> _opened = new();
    private readonly List<TimeSpan> _waited = new();
    private readonly List<string> _log = new();

    private FolderPublishTools Tools(FakeStoreClient store, FakeStoreAuth? auth = null) =>
        new(store, auth ?? new FakeStoreAuth(),
            url => { _opened.Add(url); return Task.CompletedTask; },
            t => { _waited.Add(t); return Task.CompletedTask; },
            _log.Add);

    /** A project folder like the template's src/: csproj, manifest, settings, code, and junk to skip. */
    private static string ProjectFolder(string name, bool withBuildOutput = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcp-pf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Ops"));
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(dir, "package.info.json"), "{}");
        File.WriteAllText(Path.Combine(dir, name + ".settings.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "Extension.cs"), "class E {}");
        File.WriteAllText(Path.Combine(dir, "Ops", "Notify.cs"), "class N {}");
        if (withBuildOutput)
        {
            Directory.CreateDirectory(Path.Combine(dir, "bin", "Release"));
            File.WriteAllBytes(Path.Combine(dir, "bin", "Release", name + ".dll"), new byte[10]);
            Directory.CreateDirectory(Path.Combine(dir, "obj"));
            File.WriteAllText(Path.Combine(dir, "obj", "project.assets.json"), "{}");
            File.WriteAllText(Path.Combine(dir, name + ".csproj.user"), "<x/>");
        }
        return dir;
    }

    private static BuildReport Report(string status, string updatedAt, string? version = null,
                                      string? step = null, string? log = null) =>
        new("andrew-l/EncyNotify", "EncyNotify", status, version, step, log,
            "https://github.com/andrew-l/EncyNotify/actions/runs/1", updatedAt);

    // ── the folder ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefusesAFolderThatIsNotAProjectBeforeTouchingTheStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcp-pf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "readme.md"), "hi");
        var store = new FakeStoreClient();
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.StartsWith("ERROR", res);
            Assert.Contains(".csproj", res);
            Assert.Empty(store.Uploads);
            Assert.Empty(store.CreatedRepos);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task UploadsTheSourcesWithoutBuildOutputAndWithForwardSlashes()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient { BuildsDefault = new[] { Report("PUBLISHED", "t1", "0.1.0") } };
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            var (id, files) = Assert.Single(store.Uploads);
            Assert.Equal("EncyNotify", id);
            var paths = files.Select(f => f.Path).OrderBy(p => p).ToList();
            Assert.Equal(new[] { "EncyNotify.csproj", "EncyNotify.settings.json", "Extension.cs", "Ops/Notify.cs", "package.info.json" }, paths);
            Assert.Contains("5 files", res);
            Assert.Equal(new[] { "EncyNotify" }, store.Runs);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TheWholeDownloadedProjectIsAcceptedThroughItsSrc()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-pf-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(root, ".github", "workflows"));
        File.WriteAllText(Path.Combine(root, ".github", "workflows", "publish.yml"), "x");
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "rules");
        File.WriteAllText(Path.Combine(src, "EncyNotify.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(src, "package.info.json"), "{}");
        File.WriteAllText(Path.Combine(src, "EncyNotify.settings.json"), "{}");
        var store = new FakeStoreClient { BuildsDefault = new[] { Report("PUBLISHED", "t1", "0.1.0") } };
        try
        {
            await Tools(store).PublishFolder("EncyNotify", root);
            var (_, files) = Assert.Single(store.Uploads);
            Assert.Equal(3, files.Count);
            Assert.DoesNotContain(files, f => f.Path.Contains("AGENTS") || f.Path.Contains("workflows"));
        }
        finally { Directory.Delete(root, true); }
    }

    // ── sign-in and the app ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignsInThroughTheBrowserWhenThereIsNoToken()
    {
        var dir = ProjectFolder("EncyNotify");
        var auth = new FakeStoreAuth { Token = null };
        var store = new FakeStoreClient { BuildsDefault = new[] { Report("PUBLISHED", "t1", "0.1.0") } };
        try
        {
            var res = await Tools(store, auth).PublishFolder("EncyNotify", dir);
            Assert.Equal(1, auth.LoginCalls);
            Assert.Contains("signed in to the store through the browser", res);
            Assert.Single(store.Uploads);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ABrowserSignInThatDoesNotCompleteIsSaidPlainlyNotAsAConsoleCommandToRun()
    {
        var dir = ProjectFolder("EncyNotify");
        var auth = new FakeStoreAuth { Token = null, LoginSucceeds = false };
        var store = new FakeStoreClient();
        try
        {
            var res = await Tools(store, auth).PublishFolder("EncyNotify", dir);
            Assert.StartsWith("ERROR", res);
            Assert.Contains("browser", res);
            Assert.DoesNotContain("gh auth login", res);
            Assert.Empty(store.Uploads);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task OpensTheConsentPageAndWaitsForTheInstallation()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient { BuildsDefault = new[] { Report("PUBLISHED", "t1", "0.1.0") } };
        var none = new AppStatus(true, new List<string>(), new List<AppRepo>());
        var installed = new AppStatus(true, new List<string> { "andrew-l" }, new List<AppRepo>());
        store.AppStatuses.Enqueue(none);        // the first look
        store.AppStatuses.Enqueue(none);        // still on the consent page
        store.AppStatuses.Enqueue(installed);   // approved
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.Equal(new[] { store.InstallUrl }, _opened);
            Assert.Equal(1, store.InstallUrlAsked);
            Assert.Contains("store app installed on andrew-l", res);
            Assert.Single(store.Uploads);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task WithoutTheConsentItStopsAndSaysWhatToDoNotWhatToType()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient { AppStatusDefault = new AppStatus(true, new List<string>(), new List<AppRepo>()) };
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.StartsWith("WAITING", res);
            Assert.Contains(store.InstallUrl, res);
            Assert.Equal(FolderPublishTools.InstallPolls, _waited.Count);
            Assert.Empty(store.Uploads);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task AStoreWithoutAnAppSendsToTheBrowserRoute()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient { AppStatusDefault = new AppStatus(false, new List<string>(), new List<AppRepo>()) };
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.StartsWith("ERROR", res);
            Assert.Contains("https://store.test/publish", res);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── the repository ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReusesTheRepositoryTheStoreAlreadyMadeForThisName()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient
        {
            AppStatusDefault = new AppStatus(true, new List<string> { "andrew-l" },
                new List<AppRepo> { new("encynotify", "andrew-l/EncyNotify", "https://github.com/andrew-l/EncyNotify") }),
            BuildsDefault = new[] { Report("PUBLISHED", "t1", "0.1.0") },
        };
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.Empty(store.CreatedRepos);
            Assert.Contains("already there", res);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CreatesTheRepositoryAndWaitsUntilGithubHasItReady()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient { BuildsDefault = new[] { Report("PUBLISHED", "t1", "0.1.0") } };
        store.RepoStates.Enqueue(new RepoState("generating", false, "main"));
        store.RepoStates.Enqueue(new RepoState("bootstrapping", false, "main"));
        store.RepoStates.Enqueue(new RepoState("ready", false, "main"));
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.Equal(new[] { "EncyNotify" }, store.CreatedRepos);
            Assert.Contains("created from the template", res);
            Assert.True(_waited.Count(t => t == FolderPublishTools.PollEvery) >= 2, "waited for the repository");
            Assert.Single(store.Uploads);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ANameThatIsTakenIsRefusedInTheStoresWords()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient { CreateRepoFailure = "The name EncyNotify is connected to somebody else's repository" };
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.StartsWith("ERROR", res);
            Assert.Contains("somebody else's repository", res);
            Assert.Empty(store.Uploads);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── the build ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReportsThePublishedVersionAndTheCard()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient { Card = new StoreCard("encynotify", Approved: false, Unlisted: false, "0.1.0") };
        store.Builds.Enqueue(Array.Empty<BuildReport>());                          // baseline: nothing before
        store.Builds.Enqueue(Array.Empty<BuildReport>());                          // GitHub has not started
        store.Builds.Enqueue(new[] { Report("RUNNING", "t1") });
        store.Builds.Enqueue(new[] { Report("PUBLISHED", "t2", "0.1.0") });
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.Contains("Published EncyNotify 0.1.0", res);
            Assert.Contains("awaits a store moderator", res);
            Assert.Contains("https://store.test/extension/encynotify", res);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ReportsTheFailingStepAndItsLog()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient();
        store.Builds.Enqueue(Array.Empty<BuildReport>());
        store.Builds.Enqueue(new[] { Report("FAILED", "t1", step: "Build", log: "error CS1002: ; expected") });
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.Contains("failed at Build", res);
            Assert.Contains("error CS1002", res);
            Assert.Contains("publish_folder again", res);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ThePreviousRunsReportIsNotMistakenForThisOne()
    {
        var dir = ProjectFolder("EncyNotify");
        var old = Report("PUBLISHED", "t0", "0.1.0");
        var store = new FakeStoreClient { Card = new StoreCard("encynotify", Approved: true, Unlisted: false, "0.1.1") };
        store.Builds.Enqueue(new[] { old });                                       // baseline
        store.Builds.Enqueue(new[] { old });                                       // still the old one
        store.Builds.Enqueue(new[] { old });
        store.Builds.Enqueue(new[] { Report("PUBLISHED", "t9", "0.1.1") });
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir);
            Assert.Contains("Published EncyNotify 0.1.1", res);
            Assert.DoesNotContain("0.1.0", res);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task WithoutWaitingItReturnsAfterTheRunStarts()
    {
        var dir = ProjectFolder("EncyNotify");
        var store = new FakeStoreClient();
        try
        {
            var res = await Tools(store).PublishFolder("EncyNotify", dir, waitForResult: false);
            Assert.Contains("publish run started", res);
            Assert.Contains("publish_folder_status", res);
            Assert.DoesNotContain(_waited, t => t == FolderPublishTools.BuildPollEvery);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StatusReadsTheLatestReportByName()
    {
        var store = new FakeStoreClient
        {
            BuildsDefault = new[] { Report("FAILED", "t1", step: "Publish", log: "refused by the virus scanner") },
        };
        var res = await Tools(store).PublishFolderStatus("EncyNotify");
        Assert.Contains("failed at Publish", res);
        Assert.Contains("virus scanner", res);
        Assert.Contains("No publish run", await Tools(new FakeStoreClient()).PublishFolderStatus("Other"));
    }
}
