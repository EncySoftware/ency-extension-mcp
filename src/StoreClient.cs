using System.Net;
using System.Text.Json;

namespace EncyExtensionMcp;

public record StoreCard(string Slug, bool Approved, bool Unlisted, string? LatestVersion)
{
    public string CardUrl(string storeBase) => $"{storeBase}/extension/{Slug}";
}

/** A storefront category as the store publishes it; archived ones never reach here. */
public record StoreCategory(string Id, string Caption);

public interface IStoreClient
{
    /** Card by slug or packageId; null when the store has no such extension (yet). */
    Task<StoreCard?> GetCard(string slugOrPackageId);

    /**
     * The categories an extension may be sorted into. Read live rather than hardcoded: the list is
     * rows in the store's database precisely so it can grow without a release, and a tool shipping
     * its own copy would start refusing categories that exist.
     */
    Task<IReadOnlyList<StoreCategory>> GetCategories();

    /**
     * Bind the repository to the package name up front, so the first CI publish authenticates with
     * GitHub's OIDC token and the repository needs no store secret at all. Returns null on success,
     * otherwise the reason (shown to the author, who can then fall back to a repo secret).
     */
    Task<string?> ClaimPackage(string packageId, string repository, string accessToken);

    string StoreBaseUrl { get; }
}

/** Reads the public store REST API (no auth needed for cards). */
public class StoreClient : IStoreClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /** API base; override with ENCY_STORE_API for test stands. */
    private readonly string _apiBase =
        (Environment.GetEnvironmentVariable("ENCY_STORE_API") ?? "https://dmc.encycam.com/store/api").TrimEnd('/');

    public string StoreBaseUrl => _apiBase.EndsWith("/api") ? _apiBase[..^4] : _apiBase;

    public async Task<StoreCard?> GetCard(string slugOrPackageId)
    {
        var resp = await Http.GetAsync($"{_apiBase}/extensions/{Uri.EscapeDataString(slugOrPackageId)}");
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        return new StoreCard(
            r.GetProperty("slug").GetString()!,
            r.TryGetProperty("approved", out var a) && a.GetBoolean(),
            r.TryGetProperty("unlisted", out var u) && u.GetBoolean(),
            r.TryGetProperty("latestVersion", out var v) ? v.GetString() : null);
    }

    public async Task<IReadOnlyList<StoreCategory>> GetCategories()
    {
        var resp = await Http.GetAsync($"{_apiBase}/categories");
        if (!resp.IsSuccessStatusCode) return Array.Empty<StoreCategory>();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var list = new List<StoreCategory>();
        foreach (var c in doc.RootElement.GetProperty("categories").EnumerateArray())
        {
            if (c.TryGetProperty("archived", out var arch) && arch.GetBoolean()) continue;
            var id = c.GetProperty("id").GetString();
            if (id is null) continue;
            list.Add(new StoreCategory(id,
                c.TryGetProperty("caption", out var cap) ? cap.GetString() ?? id : id));
        }
        return list;
    }

    public async Task<string?> ClaimPackage(string packageId, string repository, string accessToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Put,
            $"{_apiBase}/publishers/{Uri.EscapeDataString(packageId)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { repository }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await Http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return null;
        string body = await resp.Content.ReadAsStringAsync();
        return $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body.Trim()}";
    }
}
