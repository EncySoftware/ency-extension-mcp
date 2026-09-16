using EncyExtensionMcp;
using Xunit;

/** my_extensions: the author's cards in one answer, what needs them first. */
public class AccountToolsTests
{
    private static MyExtension Ext(string id, bool approved = true, bool unlisted = false, string? reason = null, string? category = "operation") =>
        new(id.ToLowerInvariant(), id, id, "1.0.0", approved, unlisted, reason, category, "2026-09-16T10:00:00Z");

    [Fact]
    public void Attention_comes_first_in_the_order_a_person_would_act()
    {
        var mine = new[]
        {
            Ext("Live"),
            Ext("Waiting", approved: false),
            Ext("Rejected", approved: false, reason: "The README is the template's."),
            Ext("Hidden", unlisted: true),
            Ext("Broken"),
        };
        var builds = new[]
        {
            new BuildReport("someone/Broken", "Broken", "FAILED", "1.0.1", "build", "error CS1513: } expected\nat Foo.cs:12\n", "https://github.com/someone/Broken/actions/runs/1", "2026-09-16T11:00:00Z"),
        };

        string text = AccountTools.Render(mine, builds, "https://store.test");
        var lines = text.Split('\n').Select(l => l.TrimEnd()).ToList();

        int Idx(string s) => lines.FindIndex(l => l.Contains(s));
        Assert.True(Idx("Broken 1.0.0 — build FAILED at build: https://github.com/someone/Broken/actions/runs/1") < Idx("Rejected"));
        Assert.True(Idx("Rejected 1.0.0 — rejected: The README is the template's.") < Idx("Waiting"));
        Assert.True(Idx("Waiting 1.0.0 — waiting for a moderator") < Idx("Live:"));
        Assert.Contains("    error CS1513: } expected", lines);
        Assert.Contains("- Live 1.0.0 — https://store.test/extension/live (category: operation)", lines);
        Assert.True(Idx("Hidden:") > Idx("Live:"));
        Assert.Contains("- Hidden 1.0.0 — hidden from the catalogue (https://store.test/extension/hidden)", lines);
    }

    [Fact]
    public void A_build_that_failed_before_any_card_exists_is_still_listed()
    {
        string text = AccountTools.Render(Array.Empty<MyExtension>(),
            new[] { new BuildReport("someone/NewOne", null, "FAILED", null, "pack", "boom", "https://x/run/2", "2026-09-16T11:00:00Z") },
            "https://store.test");
        Assert.Contains("Failed builds without a card yet:", text);
        Assert.Contains("- someone/NewOne: FAILED at pack — https://x/run/2", text);
    }

    [Fact]
    public async Task Not_signed_in_says_how_to_sign_in_and_reads_nothing()
    {
        var store = new FakeStoreClient();
        string answer = await new AccountTools(store, new FakeStoreAuth { Token = null }).MyExtensions();
        Assert.StartsWith("ERROR", answer);
        Assert.Contains("ency-extension-mcp login", answer);
    }

    [Fact]
    public async Task Nothing_published_yet_points_at_the_first_publish()
    {
        string answer = await new AccountTools(new FakeStoreClient(), new FakeStoreAuth()).MyExtensions();
        Assert.Contains("Nothing published under this account yet", answer);
    }

    [Fact]
    public async Task The_tool_reads_both_lists_with_the_token()
    {
        var store = new FakeStoreClient();
        store.MyExtensions.Add(Ext("Live"));
        string answer = await new AccountTools(store, new FakeStoreAuth()).MyExtensions();
        Assert.Contains("Live:", answer);
        Assert.Contains("https://store.test/extension/live", answer);
    }
}
