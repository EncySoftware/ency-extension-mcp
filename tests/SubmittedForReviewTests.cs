using System.Net;
using System.Text;
using EncyExtensionMcp;
using Xunit;

/**
 * A store that reviews a publication before it reaches the feed answers the publish with 202 and no
 * card, and the run reports its build as SUBMITTED afterwards. The tool must call that a success,
 * give no card link (there is no card yet), and read the status wherever it reads build reports —
 * and a status it does not know must not break it.
 *
 * The world these tests build is the server's: a first submission claims its name with an EMPTY card
 * row (no version), which the store returns like any card; the version's real state lives in the
 * store's submissions, and the run's report is only the run's last word.
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

    /** The server's 202 sentence for every state but SUBMITTED, FAILED included. */
    private const string SigningSentence = "Accepted and waiting to be signed — it appears in the catalog once it reaches the feed";

    private const string Moderator = Submissions.Moderator;

    /** The empty row a first submission claims its name with: no version, not approved. */
    private static readonly StoreCard Claimed = new("myext", Approved: false, Unlisted: false, LatestVersion: null);

    private static MySubmission Sub(string version, string state, string packageId = "MyExt", string? reason = null,
                                    string? error = null, string createdAt = "2026-10-05T10:00:00Z") =>
        new(packageId, version, state, reason, error, createdAt, createdAt);

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
        var body = "{\"packageId\":\"MyExt\",\"version\":\"0.2.0\",\"message\":\"" + SigningSentence + "\",\"state\":\"PENDING\"}";
        var card = await new StoreClient(new Scripted(HttpStatusCode.Accepted, body), "https://store.test/api")
            .PublishStaged(Staged, null, "tok");

        Assert.True(card.Submitted);
        Assert.False(card.AwaitsReview);
        Assert.True(card.AwaitsSigning);
        Assert.StartsWith("Accepted, not in the catalog yet: MyExt 0.2.0 — Accepted and waiting to be signed",
            LocalPublishTools.SubmittedLine("MyExt 0.2.0", card));
    }

    /**
     * A trusted publisher's submission goes on to signing and the feed in the same call, and can end
     * there in FAILED (the feed already holds other bytes of that version). The 202 still carries the
     * signing sentence, so the state is what tells: no reviewer, no catalog, a new version needed.
     */
    [Fact]
    public async Task A_202_in_FAILED_is_neither_a_review_nor_on_its_way()
    {
        var body = "{\"packageId\":\"MyExt\",\"version\":\"0.2.0\",\"message\":\"" + SigningSentence + "\",\"state\":\"FAILED\"}";
        var card = await new StoreClient(new Scripted(HttpStatusCode.Accepted, body), "https://store.test/api")
            .PublishStaged(Staged, null, "tok");

        Assert.True(card.Submitted);
        Assert.False(card.AwaitsReview);
        Assert.False(card.AwaitsSigning);
        string line = LocalPublishTools.SubmittedLine("MyExt 0.2.0", card);
        Assert.StartsWith("Not published: MyExt 0.2.0 — the store took the upload but reports the version as FAILED", line);
        Assert.Contains("publish again with a new version", line);
        Assert.DoesNotContain("moderator", line);
        Assert.DoesNotContain("Submitted for review", line);
    }

    [Fact]
    public async Task A_202_in_a_state_the_tool_does_not_know_promises_nothing()
    {
        var body = "{\"packageId\":\"MyExt\",\"version\":\"0.2.0\",\"message\":\"Queued\",\"state\":\"QUEUED\"}";
        var card = await new StoreClient(new Scripted(HttpStatusCode.Accepted, body), "https://store.test/api")
            .PublishStaged(Staged, null, "tok");

        string line = LocalPublishTools.SubmittedLine("MyExt 0.2.0", card);
        Assert.False(card.AwaitsReview);
        Assert.StartsWith("Accepted, not in the catalog yet: MyExt 0.2.0 — the store took the upload but reports the version as QUEUED", line);
        Assert.DoesNotContain("moderator", line);
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

    [Fact]
    public async Task Submissions_are_read_when_the_store_has_them_and_null_when_it_does_not()
    {
        var body = "[{\"packageId\":\"MyExt\",\"version\":\"0.2.0\",\"state\":\"REJECTED\",\"rejectionReason\":\"No icon\","
                   + "\"lastError\":null,\"createdAt\":\"2026-10-05T10:00:00.123Z\",\"updatedAt\":\"2026-10-05T11:00:00Z\"},"
                   + "{\"packageId\":null,\"version\":\"0.1.0\",\"state\":\"SUBMITTED\"}]";
        var http = new Scripted(HttpStatusCode.OK, body);

        var list = await new StoreClient(http, "https://store.test/api").GetMySubmissions("tok");

        Assert.Equal((HttpMethod.Get, "/api/extensions/my/submissions"), http.Calls.Single());
        var s = Assert.Single(list!);   // a row without a package id is skipped, not a crash
        Assert.Equal(("MyExt", "0.2.0", "REJECTED", "No icon"), (s.PackageId, s.Version, s.State, s.RejectionReason));

        Assert.Null(await new StoreClient(new Scripted(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"), "https://store.test/api")
            .GetMySubmissions("tok"));
        Assert.Null(await new StoreClient(new Scripted(HttpStatusCode.OK, "<!doctype html><html></html>"), "https://store.test/api")
            .GetMySubmissions("tok"));
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

    [Fact]
    public async Task Publish_package_that_FAILED_on_the_way_says_so_and_promises_no_review()
    {
        var store = new FakeStoreClient { SubmittedState = "FAILED", SubmittedMessage = SigningSentence };
        string answer = await new LocalPublishTools(store, new FakeStoreAuth(), _ => { }).PublishPackage(NupkgIn(TempDir()));

        Assert.Contains("Not published: MyExt 0.1.0 — the store took the upload but reports the version as FAILED", answer);
        Assert.DoesNotContain("Submitted for review", answer);
        Assert.DoesNotContain("moderator", answer);
        Assert.DoesNotContain("/extension/", answer);
    }

    // ---- publish_folder_status --------------------------------------------------------------------

    private static FolderPublishTools FolderTools(FakeStoreClient store) =>
        new(store, new FakeStoreAuth(), _ => Task.CompletedTask, _ => Task.CompletedTask, _ => { },
            () => Task.FromResult(Array.Empty<byte>()));

    private static BuildReport Build(string status, string? version = "0.3.0", string packageId = "EncyNotify",
                                     string? failureLog = null) =>
        new("andrew-l/" + packageId, packageId, status, version, status == "FAILED" ? "Publish" : null, failureLog,
            "https://github.com/andrew-l/run/9", "2026-10-05T10:00:00Z");

    [Fact]
    public async Task Publish_folder_status_reads_a_submitted_build()
    {
        var store = new FakeStoreClient
        {
            BuildsDefault = new[] { Build("SUBMITTED") },
            Card = new StoreCard("encynotify", true, false, "0.2.0"),
            MySubmissions = new[] { Sub("0.3.0", "SUBMITTED", "EncyNotify") },
        };

        string answer = await FolderTools(store).PublishFolderStatus("EncyNotify");

        Assert.StartsWith($"Submitted for review: EncyNotify 0.3.0 — {Moderator}", answer);
        Assert.DoesNotContain("/extension/", answer);
        Assert.DoesNotContain("Still building", answer);
    }

    [Fact]
    public async Task Publish_folder_status_says_a_moderator_refused_what_the_run_reported_as_submitted()
    {
        var store = new FakeStoreClient
        {
            BuildsDefault = new[] { Build("SUBMITTED") },
            Card = new StoreCard("encynotify", true, false, "0.2.0"),
            MySubmissions = new[] { Sub("0.3.0", "REJECTED", "EncyNotify", reason: "The readme is the template's") },
        };

        string answer = await FolderTools(store).PublishFolderStatus("EncyNotify");

        Assert.StartsWith("Rejected by a moderator: EncyNotify 0.3.0 — The readme is the template's.", answer);
        Assert.DoesNotContain(Moderator, answer);
    }

    [Fact]
    public async Task Publish_folder_status_on_a_store_without_submissions_does_not_promise_a_moderator()
    {
        var store = new FakeStoreClient { BuildsDefault = new[] { Build("SUBMITTED") }, Card = Claimed };

        string answer = await FolderTools(store).PublishFolderStatus("EncyNotify");

        Assert.StartsWith("Submitted for review: EncyNotify 0.3.0 — as the run reported it", answer);
        Assert.DoesNotContain(Moderator, answer);
        Assert.DoesNotContain("/extension/", answer);
    }

    [Fact]
    public async Task Publish_folder_status_of_a_202_that_stopped_is_not_a_code_problem()
    {
        var store = new FakeStoreClient
        {
            BuildsDefault = new[] { Build("FAILED", failureLog: "HTTP 202 — the store took EncyNotify 0.3.0 but reports it as FAILED: "
                                                                + SigningSentence) },
        };

        string answer = await FolderTools(store).PublishFolderStatus("EncyNotify");

        Assert.StartsWith("Not published: the store took the package, then stopped it.", answer);
        Assert.Contains("needs a new number", answer);
        Assert.DoesNotContain("Fix the code", answer);
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

    /** The first publication on a reviewing store: an empty claimed row, the run's SUBMITTED, the store's SUBMITTED. */
    [Fact]
    public async Task Publish_status_of_a_first_submission_links_no_empty_card()
    {
        var store = new FakeStoreClient
        {
            Card = Claimed,
            BuildsDefault = new[] { Build("SUBMITTED", "0.1.0", "MyExt") },
            MySubmissions = new[] { Sub("0.1.0", "SUBMITTED") },
        };

        string answer = await PublishStatus(store);

        Assert.Contains($"Submitted for review: MyExt 0.1.0 — {Moderator}.", answer);
        Assert.DoesNotContain("/extension/", answer);
        Assert.DoesNotContain("card still shows", answer);
        Assert.DoesNotContain("awaiting store moderation", answer);
    }

    [Fact]
    public async Task Publish_status_reads_the_submission_even_when_the_run_could_not_report_it()
    {
        // A store that does not know the SUBMITTED report yet refuses it, and the run's row stays RUNNING.
        var store = new FakeStoreClient
        {
            Card = Claimed,
            BuildsDefault = new[] { Build("RUNNING", "0.1.0", "MyExt") },
            MySubmissions = new[] { Sub("0.1.0", "SUBMITTED") },
        };

        Assert.Contains($"Submitted for review: MyExt 0.1.0 — {Moderator}.", await PublishStatus(store));
    }

    [Fact]
    public async Task Publish_status_of_an_empty_card_with_nothing_on_its_way_is_no_card_yet()
    {
        var store = new FakeStoreClient { Card = Claimed };

        string answer = await PublishStatus(store);

        Assert.Contains("the store has no card for MyExt yet", answer);
        Assert.DoesNotContain("/extension/", answer);
    }

    [Fact]
    public async Task Publish_status_on_a_store_without_submissions_reads_the_run_report_without_promising_a_moderator()
    {
        var store = new FakeStoreClient { BuildsDefault = new[] { Build("SUBMITTED", "0.1.0", "MyExt") } };

        string answer = await PublishStatus(store);

        Assert.Contains("Submitted for review: MyExt 0.1.0 — as the run reported it", answer);
        Assert.DoesNotContain(Moderator, answer);
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
            MySubmissions = new[] { Sub("0.1.1", "SUBMITTED") },
        };

        string answer = await PublishStatus(store);

        Assert.Contains($"Submitted for review: MyExt 0.1.1 — {Moderator}.", answer);
        Assert.Contains("The card still shows 0.1.0: https://store.test/extension/myext", answer);
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

    /**
     * The last report that landed said SUBMITTED for 0.1.0; a later run published 0.2.0 with a 200 and
     * its report did not land. A card that is AHEAD of the report is not "still showing" anything.
     */
    [Fact]
    public async Task Publish_status_ignores_a_submitted_report_older_than_the_card()
    {
        var store = new FakeStoreClient
        {
            Card = new StoreCard("myext", Approved: true, Unlisted: false, "0.2.0"),
            BuildsDefault = new[] { Build("SUBMITTED", "0.1.0", "MyExt") },
        };

        string answer = await PublishStatus(store);

        Assert.Contains("Published and approved: MyExt 0.2.0", answer);
        Assert.DoesNotContain("Submitted for review", answer);
    }

    /** The run said SUBMITTED, and nothing rewrites that row when a moderator decides; the store's submission does. */
    [Fact]
    public async Task Publish_status_says_rejected_when_the_store_says_so_whatever_the_run_reported()
    {
        var store = new FakeStoreClient
        {
            Card = new StoreCard("myext", Approved: true, Unlisted: false, "0.1.0"),
            BuildsDefault = new[] { Build("SUBMITTED", "0.2.0", "MyExt") },
            MySubmissions = new[] { Sub("0.2.0", "REJECTED", reason: "Crashes on start.") },
        };

        string answer = await PublishStatus(store);

        Assert.Contains("Rejected by a moderator: MyExt 0.2.0 — Crashes on start. Fix it and publish again", answer);
        Assert.Contains("The card still shows 0.1.0", answer);
        Assert.DoesNotContain(Moderator, answer);
        Assert.DoesNotContain("Submitted for review", answer);
    }

    [Fact]
    public async Task Publish_status_says_withdrawn_and_failed_as_the_store_says_them()
    {
        var withdrawn = new FakeStoreClient
        {
            Card = Claimed,
            BuildsDefault = new[] { Build("SUBMITTED", "0.1.0", "MyExt") },
            MySubmissions = new[] { Sub("0.1.0", "WITHDRAWN") },
        };
        string w = await PublishStatus(withdrawn);
        Assert.Contains("Withdrawn: MyExt 0.1.0", w);
        Assert.DoesNotContain(Moderator, w);

        var failed = new FakeStoreClient
        {
            Card = Claimed,
            BuildsDefault = new[] { Build("SUBMITTED", "0.1.0", "MyExt") },
            MySubmissions = new[] { Sub("0.1.0", "FAILED", error: "Version 0.1.0 of MyExt is already on the feed with different bytes.") },
        };
        string f = await PublishStatus(failed);
        Assert.Contains("Not published: MyExt 0.1.0 — it failed on the way to the feed: Version 0.1.0 of MyExt is already on the feed with different bytes. Publish again with a new version.", f);
        Assert.DoesNotContain(Moderator, f);
    }

    [Fact]
    public async Task Publish_status_picks_the_run_version_among_several_submissions()
    {
        var store = new FakeStoreClient
        {
            Card = new StoreCard("myext", Approved: true, Unlisted: false, "0.1.0"),
            BuildsDefault = new[] { Build("SUBMITTED", "0.2.0", "MyExt") },
            MySubmissions = new[]
            {
                Sub("0.2.0", "SUBMITTED", createdAt: "2026-10-05T09:00:00Z"),
                Sub("0.1.5", "REJECTED", reason: "Old", createdAt: "2026-10-05T11:00:00Z"),
            },
        };

        string answer = await PublishStatus(store);

        Assert.Contains("Submitted for review: MyExt 0.2.0", answer);
        Assert.DoesNotContain("Rejected", answer);
    }

    [Fact]
    public async Task Publish_status_is_unchanged_by_a_status_it_does_not_know_or_by_no_sign_in()
    {
        var unknown = new FakeStoreClient { BuildsDefault = new[] { Build("QUARANTINED", "0.1.0", "MyExt") } };
        Assert.Contains("the store has no card for MyExt yet", await PublishStatus(unknown));

        var submitted = new FakeStoreClient
        {
            BuildsDefault = new[] { Build("SUBMITTED", "0.1.0", "MyExt") },
            MySubmissions = new[] { Sub("0.1.0", "SUBMITTED") },
        };
        Assert.Contains("the store has no card for MyExt yet", await PublishStatus(submitted, new FakeStoreAuth { Token = null }));
        Assert.Contains("the store has no card for MyExt yet", await PublishStatus(submitted, new ExpiredAuth()));
    }

    // ---- my_extensions ----------------------------------------------------------------------------

    private static MyExtension Ext(string id, string? version = "1.0.0", bool approved = true) =>
        new(id.ToLowerInvariant(), id, id, version, approved, false, null, "operation", "2026-10-01T10:00:00Z");

    [Fact]
    public void My_extensions_lists_a_submission_that_has_no_card_yet()
    {
        string text = AccountTools.Render(Array.Empty<MyExtension>(),
            new[] { Build("SUBMITTED", "0.1.0", "NewOne") }, "https://store.test",
            new[] { Sub("0.1.0", "SUBMITTED", "NewOne") });

        Assert.Contains("Submitted, no card yet:", text);
        Assert.Contains($"- Submitted for review: NewOne 0.1.0 — {Moderator}.", text);
        Assert.DoesNotContain("/extension/", text);
    }

    /** The main case: the first publication of a new name leaves an empty, unapproved row behind. */
    [Fact]
    public void My_extensions_puts_the_empty_row_of_a_first_submission_under_no_card_yet()
    {
        string text = AccountTools.Render(new[] { Ext("NewOne", version: null, approved: false) },
            new[] { Build("SUBMITTED", "0.1.0", "NewOne") }, "https://store.test",
            new[] { Sub("0.1.0", "SUBMITTED", "NewOne") });

        Assert.Contains("Submitted, no card yet:", text);
        Assert.Contains($"- Submitted for review: NewOne 0.1.0 — {Moderator}.", text);
        Assert.DoesNotContain("waiting for a moderator (", text);
        Assert.DoesNotContain("/extension/", text);
        Assert.DoesNotContain("Needs attention", text);
    }

    [Fact]
    public void My_extensions_does_so_from_the_run_report_alone_on_a_store_without_submissions()
    {
        string text = AccountTools.Render(new[] { Ext("NewOne", version: null, approved: false) },
            new[] { Build("SUBMITTED", "0.1.0", "NewOne") }, "https://store.test");

        Assert.Contains("- Submitted for review: NewOne 0.1.0 — as the run reported it", text);
        Assert.DoesNotContain("/extension/", text);
    }

    [Fact]
    public void My_extensions_keeps_an_empty_row_with_nothing_on_its_way_as_before()
    {
        string text = AccountTools.Render(new[] { Ext("Held", version: null, approved: false) },
            Array.Empty<BuildReport>(), "https://store.test");

        Assert.Contains("- Held — waiting for a moderator (https://store.test/extension/held)", text);
    }

    [Fact]
    public void My_extensions_marks_a_newer_version_waiting_for_review_on_a_live_card()
    {
        string text = AccountTools.Render(new[] { Ext("Live") },
            new[] { Build("SUBMITTED", "1.0.1", "Live") }, "https://store.test");

        Assert.Contains("- Live 1.0.0 — https://store.test/extension/live (category: operation) — 1.0.1 submitted for review", text);
    }

    [Fact]
    public void My_extensions_does_not_call_an_older_submitted_report_newer()
    {
        string text = AccountTools.Render(new[] { Ext("Live", "1.1.0") },
            new[] { Build("SUBMITTED", "1.0.1", "Live") }, "https://store.test");

        Assert.Contains("- Live 1.1.0 — https://store.test/extension/live (category: operation)", text);
        Assert.DoesNotContain("submitted for review", text);
    }

    [Fact]
    public void My_extensions_puts_a_rejected_newer_version_under_needs_attention()
    {
        string text = AccountTools.Render(new[] { Ext("Live") },
            new[] { Build("SUBMITTED", "1.0.1", "Live") }, "https://store.test",
            new[] { Sub("1.0.1", "REJECTED", "Live", reason: "Wrong category") });

        Assert.StartsWith("Needs attention:", text);
        Assert.Contains("- Live 1.0.0 — https://store.test/extension/live (category: operation) — 1.0.1 rejected: Wrong category", text);
        Assert.DoesNotContain("submitted for review", text);
    }

    [Fact]
    public void My_extensions_takes_a_status_it_does_not_know_in_its_stride()
    {
        string text = AccountTools.Render(new[] { Ext("Live") },
            new[] { Build("QUARANTINED", "1.0.1", "Live"), Build("QUARANTINED", "0.1.0", "Orphan") }, "https://store.test");

        Assert.Contains("- Live 1.0.0 — https://store.test/extension/live (category: operation)", text);
        Assert.DoesNotContain("Orphan", text);
    }

    // ---- version order ----------------------------------------------------------------------------

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("1.0.0", "1.0.0-rc.1", true)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1", true)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("1.0.0-rc.1", "1.0.0", false)]
    [InlineData("not-a-version", "1.0.0", false)]
    public void Newer_is_semver_order(string candidate, string current, bool newer) =>
        Assert.Equal(newer, Submissions.IsNewer(candidate, current));
}
