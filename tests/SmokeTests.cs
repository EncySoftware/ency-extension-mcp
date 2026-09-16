using System.Net;
using System.Text.Json;
using EncyExtensionMcp;
using Xunit;

/**
 * Live, read-only checks against the production store and its Keycloak — the shapes this tool
 * parses and the sign-in contract it relies on. The unit tests run against fakes and could not
 * notice the day the server changed an answer; the author of an extension noticed instead.
 *
 * <para>They run only with ENCY_SMOKE=1 (the smoke workflow sets it) and pass silently otherwise, so
 * `dotnet test` stays offline on a developer's machine. Nothing here signs in or writes.</para>
 */
[Trait("Category", "Smoke")]
public class SmokeTests
{
    private const string Store = "https://apps.encycam.com";
    private const string Keycloak = "https://webservices.encycam.com/keycloak/realms/licsys/protocol/openid-connect";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };

    private static bool Off => Environment.GetEnvironmentVariable("ENCY_SMOKE") != "1";

    [Fact]
    public async Task The_categories_answer_has_the_fields_the_tool_reads()
    {
        if (Off) return;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Store + "/api/categories"));
        var list = doc.RootElement.GetProperty("categories").EnumerateArray().ToList();
        Assert.True(list.Count >= 10, $"only {list.Count} categories");
        Assert.All(list, c => Assert.Equal(JsonValueKind.String, c.GetProperty("id").ValueKind));
        Assert.Contains(list, c => c.GetProperty("id").GetString() == "other");
    }

    [Fact]
    public async Task The_sdk_answer_names_a_version()
    {
        if (Off) return;
        string? sdk = await new StoreClient().GetRecommendedSdk();
        Assert.NotNull(sdk);
        Assert.Matches(@"^\d+\.\d+\.\d+", sdk);
    }

    [Fact]
    public async Task A_catalogue_card_has_the_fields_the_tool_reads()
    {
        if (Off) return;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(Store + "/api/extensions?size=1"));
        var root = doc.RootElement;
        var first = (root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("content")).EnumerateArray().First();
        foreach (var field in new[] { "slug", "packageId", "approved", "unlisted" })
            Assert.True(first.TryGetProperty(field, out _), $"card lacks '{field}'");
    }

    /** The sign-in contract set up on 2026-09-16: our client, our loopback callback, PKCE required, no password grant. */
    [Fact]
    public async Task The_keycloak_client_takes_the_loopback_callback_and_insists_on_pkce()
    {
        if (Off) return;
        string cb = Uri.EscapeDataString($"http://localhost:{BrowserLogin.LoopbackPorts[0]}/callback");
        string challenge = "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";
        string auth = $"{Keycloak}/auth?client_id=extension-store&response_type=code&scope=openid&redirect_uri={cb}";

        var page = await Http.GetAsync(auth + challenge);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("login-actions/authenticate", await page.Content.ReadAsStringAsync());

        var noPkce = await Http.GetAsync(auth);
        Assert.Equal(HttpStatusCode.Found, noPkce.StatusCode);
        Assert.Contains("code_challenge", noPkce.Headers.Location?.ToString() ?? "");

        var password = await Http.PostAsync($"{Keycloak}/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = "extension-store",
            ["username"] = "nobody@example.com", ["password"] = "x",
        }));
        Assert.Contains("unauthorized_client", await password.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nuget_org_lists_this_package()
    {
        if (Off) return;
        string? latest = UpdateCheck.LatestOf(await Http.GetStringAsync(UpdateCheck.IndexUrl));
        Assert.NotNull(latest);
        Assert.False(UpdateCheck.IsNewer("0.2.13", latest), $"nuget.org says {latest}, older than a version that shipped");
    }
}
