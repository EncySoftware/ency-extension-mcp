using System.Net;
using System.Text;
using EncyExtensionMcp;
using Xunit;

/**
 * A store that reviews a publication before it reaches the feed answers the publish with 202 and no
 * card, and the run reports its build as SUBMITTED afterwards. The tool must call that a success,
 * give no card link (there is no card yet), and read the status wherever it reads build reports —
 * and a status it does not know must not break it.
 */
public class SubmittedForReviewTests
{
    /** The store's HTTP answer, scripted: one code, one body, an optional warning header. */
    private sealed class Scripted(HttpStatusCode code, string body, string? warning = null) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath));
            var resp = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (warning != null) resp.Headers.TryAddWithoutValidation(StoreClient.WarningHeader, warning);
            return Task.FromResult(resp);
        }
    }

    /** Sign-in that has expired: the token provider throws, as the real one does. */
    private sealed class ExpiredAuth : IStoreAuth
    {
        public Task<string?> GetAccessToken() => throw new InvalidOperationException("Store login expired");
        public Task<bool> LoginBrowser(Action<string> log) => Task.FromResult(false);
    }

    private static readonly StagedPackage Staged = new("MyExt", "0.2.0", true, "up-1", "3.0.8", false);

    /** The body as the server writes it (ExtensionController.PublishAccepted). */
    private const string Accepted202 =
        "{\"packageId\":\"MyExt\",\"version\":\"0.2.0\",\"message\":\"Submitted for moderation — it appears in the catalog "
        + "once a moderator approves it and it reaches the feed\",\"submissionId\":\"6f1c2a9e-0000-4000-8000-000000000001\","
        + "\"state\":\"SUBMITTED\"}";

    private const string Moderator = "a moderator approves it before it appears in the catalog";

    // ---- the store client reads the answer --------------------------------------------------------

    [Fact]
    public async Task A_202_is_a_submission_with_no_card()
    {
        var http = new Scripted(HttpStatusCode.Accepted, Accepted202, warning: "Add reservedDomains before 1 November");

        var card = await new StoreClient(http, "https://store.test/api").PublishStaged(Staged, null, "tok");

        Assert.True(card.Submitted);
        Assert.True(card.AwaitsReview);
        Assert.Equal("", card.Slug);
        Assert.Equal("MyExt", card.PackageId);
        Assert.Equal("0.2.0", card.LatestVersion);
        Assert.Equal("SUBMITTED", card.State);
        Assert.StartsWith("Submitted for moderation", card.Message);
        Assert.Equal(new[] { "Add reservedDomains before 1 November" }, card.Warnings);
        Assert.Equal((HttpMethod.Post, "/api/extensions"), http.Calls.Single());
    }

    [Fact]
    public async Task A_202_with_no_body_is_still_a_submission_of_the_staged_package()
    {
        var card = await new StoreClient(new Scripted(HttpStatusCode.Accepted, ""), "https://store.test/api")
            .PublishStaged(Staged, null, "tok");

        Assert.True(card.Submitted);
        Assert.True(card.AwaitsReview);   // an unnamed wait counts as review
        Assert.Equal("MyExt", card.PackageId);
        Assert.Equal("0.2.0", card.LatestVersion);
    }

    [Fact]
    public async Task Waiting_to_be_signed_is_not_called_a_review()
    {
        var body = "{\"packageId\":\"MyExt\",\"version\":\"0.2.0\",\"message\":\"Accepted and waiting to be signed — it appears "
                   + "in the catalog once it reaches the feed\",\"state\":\"PENDING\"}";
        var card = await new StoreClient(new Scripted(HttpStatusCode.Accepted, body), "https://store.test/api")
            .PublishStaged(Staged, null, "tok");

        Assert.True(card.Submitted);
        Assert.False(card.AwaitsReview);
        Assert.StartsWith("Accepted, not in the catalog yet: MyExt 0.2.0 — Accepted and waiting to be signed",
            LocalPublishTools.SubmittedLine("MyExt 0.2.0", card));
    }

    [Fact]
    public async Task A_200_is_the_card_as_before()
    {
        var body = "{\"slug\":\"myext\",\"packageId\":\"MyExt\",\"latestVersion\":\"0.2.0\",\"approved\":true,\"unlisted\":false}";
        var card = await new StoreClient(new Scripted(HttpStatusCode.OK, body), "https://store.test/api")
            .PublishStaged(Staged, null, "tok");

        Assert.False(card.Submitted);
        Assert.Equal("myext", card.Slug);
        Assert.True(card.Approved);
    }

    // ---- publish_package --------------------------------------------------------------------------

    private static string NupkgIn(string dir)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "MyExt.0.1.0.nupkg");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 3, 4, 9 });   // the store parses the content, not the tool
        return path;
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "mcp-sub-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Publish_package_says_submitted_for_review_and_links_no_card()
    {
        var store = new FakeStoreClient { SubmittedState = "SUBMITTED", SubmittedMessage = "Submitted for moderation" };
        string nupkg = NupkgIn(TempDir());

        string answer = await new LocalPublishTools(store, new FakeStoreAuth(), _ => { }).PublishPackage(nupkg);

        Assert.Contains($"Submitted for review: MyExt 0.1.0 — {Moderator}", answer);
        Assert.DoesNotContain("/extension/", answer);
        Assert.DoesNotContain("ERROR", answer);
        Assert.Single(store.Published);
    }

    [Fact]
    public async Task Publish_package_of_a_package_waiting_to_be_signed_repeats_the_store()
    {
        var store = new FakeStoreClient { SubmittedState = "PENDING", SubmittedMessage = "Accepted and waiting to be signed" };
        string answer = await new LocalPublishTools(store, new FakeStoreAuth(), _ => { }).PublishPackage(NupkgIn(TempDir()));

        Assert.Contains("Accepted, not in the catalog yet: MyExt 0.1.0 — Accepted and waiting to be signed", answer);
        Assert.DoesNotContain("moderator", answer);
        Assert.DoesNotContain("/extension/", answer);
    }

    // ---- publish_folder_status --------------------------------------------------------------------

    private static FolderPublishTools FolderTools(FakeStoreClient store) =>
        new(store, new FakeStoreAuth(), _ => Task.CompletedTask, _ => Task.CompletedTask, _ => { },
            () => Task.FromResult(Array.Empty<byte>()));

    private static BuildReport Build(string status, string? version = "0.3.0", string packageId = "EncyNotify") =>
        new("andrew-l/" + packageId, packageId, status, version, null, null, "https://github.com/andrew-l/run/9", "2026-10-05T10:00:00Z");

    [Fact]
    public async Task Publish_folder_status_reads_a_submitted_build()
    {
        var store = new FakeStoreClient
        {
            BuildsDefault = new[] { Build("SUBMITTED") },
            Card = new StoreCard("encynotify", true, false, "0.2.0"),
        };

        string answer = await FolderTools(store).PublishFolderStatus("EncyNotify");

        Assert.StartsWith($"Submitted for review: EncyNotify 0.3.0 — {Moderator}", answer);
        Assert.DoesNotContain("/extension/", answer);
        Assert.DoesNotContain("Still building", answer);
    }

    [Fact]
    public async Task Publish_folder_status_names_a_status_it_does_not_know()
    {
        var store = new FakeStoreClient { BuildsDefault = new[] { Build("QUARANTINED") } };

        string answer = await FolderTools(store).PublishFolderStatus("EncyNotify");

        Assert.Contains("QUARANTINED", answer);
        Assert.DoesNotContain("Still building", answer);
    }

    // ---- publish_status ---------------------------------------------------------------------------

    private static async Task<string> PublishStatus(FakeStoreClient store, IStoreAuth? auth = null)
    {
        string dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "package.info.json"), "{\"packageId\":\"MyExt\"}");
        try
        {
            var proc = new FakeProcessRunner().On("gh run list",
                stdout: "[{\"databaseId\":42,\"status\":\"completed\",\"conclusion\":\"success\",\"url\":\"https://gh/run/42\"}]");
            return await new ExtensionStoreTools(proc, store, auth ?? new FakeStoreAuth()).PublishStatus(dir);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Publish_status_reads_a_submitted_build_when_there_is_no_card()
    {
        var store = new FakeStoreClient { BuildsDefault = new[] { Build("SUBMITTED", "0.1.0", "MyExt") } };

        string answer = await PublishStatus(store);

        Assert.Contains($"Submitted for review: MyExt 0.1.0 — {Moderator}.", answer);
        Assert.DoesNotContain("/extension/", answer);
        Assert.DoesNotContain("no card", answer);
    }

    [Fact]
    public async Task Publish_status_says_the_card_still_shows_the_previous_version()
    {
        var store = new FakeStoreClient
        {
            Card = new StoreCard("myext", Approved: true, Unlisted: false, "0.1.0"),
            BuildsDefault = new[] { Build("SUBMITTED", "0.1.1", "MyExt") },
        };

        string answer = await PublishStatus(store);

        Assert.Contains("Submitted for review: MyExt 0.1.1", answer);
        Assert.Contains("Until then the card shows 0.1.0: https://store.test/extension/myext", answer);
        Assert.DoesNotContain("Published and approved", answer);
    }

    [Fact]
    public async Task Publish_status_trusts_the_card_once_it_shows_the_submitted_version()
    {
        var store = new FakeStoreClient
        {
            Card = new StoreCard("myext", Approved: true, Unlisted: false, "0.1.1"),
            BuildsDefault = new[] { Build("SUBMITTED", "0.1.1", "MyExt") },
        };

        Assert.Contains("Published and approved: MyExt 0.1.1", await PublishStatus(store));
    }

    [Fact]
    public async Task Publish_status_is_unchanged_by_a_status_it_does_not_know_or_by_no_sign_in()
    {
        var unknown = new FakeStoreClient { BuildsDefault = new[] { Build("QUARANTINED", "0.1.0", "MyExt") } };
        Assert.Contains("the store has no card for MyExt yet", await PublishStatus(unknown));

        var submitted = new FakeStoreClient { BuildsDefault = new[] { Build("SUBMITTED", "0.1.0", "MyExt") } };
        Assert.Contains("the store has no card for MyExt yet", await PublishStatus(submitted, new FakeStoreAuth { Token = null }));
        Assert.Contains("the store has no card for MyExt yet", await PublishStatus(submitted, new ExpiredAuth()));
    }

    // ---- my_extensions ----------------------------------------------------------------------------

    private static MyExtension Ext(string id, string version = "1.0.0") =>
        new(id.ToLowerInvariant(), id, id, version, true, false, null, "operation", "2026-10-01T10:00:00Z");

    [Fact]
    public void My_extensions_lists_a_submission_that_has_no_card_yet()
    {
        string text = AccountTools.Render(Array.Empty<MyExtension>(),
            new[] { Build("SUBMITTED", "0.1.0", "NewOne") }, "https://store.test");

        Assert.Contains("Submitted for review, no card yet:", text);
        Assert.Contains($"- NewOne 0.1.0 — {Moderator}", text);
        Assert.DoesNotContain("/extension/", text);
    }

    [Fact]
    public void My_extensions_marks_a_newer_version_waiting_for_review_on_a_live_card()
    {
        string text = AccountTools.Render(new[] { Ext("Live") },
            new[] { Build("SUBMITTED", "1.0.1", "Live") }, "https://store.test");

        Assert.Contains("- Live 1.0.0 — https://store.test/extension/live (category: operation) — 1.0.1 submitted for review", text);
    }

    [Fact]
    public void My_extensions_takes_a_status_it_does_not_know_in_its_stride()
    {
        string text = AccountTools.Render(new[] { Ext("Live") },
            new[] { Build("QUARANTINED", "1.0.1", "Live"), Build("QUARANTINED", "0.1.0", "Orphan") }, "https://store.test");

        Assert.Contains("- Live 1.0.0 — https://store.test/extension/live (category: operation)", text);
        Assert.DoesNotContain("Orphan", text);
    }
}
