using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EncyExtensionMcp;

public record StoreCard(string Slug, bool Approved, bool Unlisted, string? LatestVersion)
{
    public string CardUrl(string storeBase) => $"{storeBase}/extension/{Slug}";
}

/** A storefront category as the store publishes it; archived ones never reach here. */
public record StoreCategory(string Id, string Caption);

// ---- the GitHub App route: the store creates the repository, takes the folder, runs the build ----

/** A repository the store created for this person through its GitHub App. */
public record AppRepo(string PackageId, string Repository, string Url);
/** Configured — the store has an App at all; Installations — GitHub accounts this person installed it on. */
public record AppStatus(bool Configured, IReadOnlyList<string> Installations, IReadOnlyList<AppRepo> Repos);
/** generating → bootstrapping → ready. OwnCode — somebody who is not a bot has committed into src/. */
public record RepoState(string Stage, bool OwnCode, string Branch);
/** A file of the extension folder; Path is relative to src/, forward slashes. */
public record SourceFile(string Path, byte[] Bytes);
public record SourcesUploaded(string Repository, string CommitSha, string CommitUrl, int Files);
public record RunStarted(string Repository, string ActionsUrl);
/** The latest build of a repository as the run itself reported it to the store. */
public record BuildReport(string Repository, string? PackageId, string Status, string? Version,
                          string? FailedStep, string? FailureLog, string? RunUrl, string? UpdatedAt);

/** A refusal from the store, in its own words — the `message` of its JSON error, not the JSON. */
public class StoreApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/**
 * A package the store has staged and parsed: what gets published next is THIS one, by
 * {@code NupkgUploadId}. Both routes answer in the same shape — a ready .nupkg and a build folder
 * the store packs itself.
 */
public record StagedPackage(string PackageId, string Version, bool HasMarker, string NupkgUploadId,
                            string? SdkVersion, bool SdkNewerThanAnyRelease);

/** The card as it stands after the publish. */
public record PublishedCard(string Slug, string PackageId, string? LatestVersion, bool Approved, bool Unlisted);

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

    /**
     * What SDK to build against today: the one carried by the newest RELEASED application. Asked
     * rather than remembered - a number written into the template goes stale in silence, and that is
     * exactly how an extension built against an unreleased SDK reached the store (03.09.2026).
     * Null when the store cannot say; a caller that does not know must not rewrite anyone's pin.
     */
    Task<string?> GetRecommendedSdk();

    string StoreBaseUrl { get; }

    // ---- the GitHub App route. Every call carries the person's store token; a refusal throws
    //      StoreApiException with the store's own sentence.
    Task<AppStatus> GetAppStatus(string accessToken);
    Task<string> GetAppInstallUrl(string accessToken);
    Task<AppRepo> CreateRepository(string packageId, string accessToken);
    Task<RepoState> GetRepoState(string packageId, string accessToken);
    Task<SourcesUploaded> UploadSources(string packageId, IReadOnlyList<SourceFile> files, string accessToken);
    Task<RunStarted> StartRun(string packageId, string accessToken);
    Task<IReadOnlyList<BuildReport>> GetMyBuilds(string accessToken);

    // ---- publishing what was built ON THIS machine: neither git nor GitHub takes part. The server
    //      has done this all along — it is how the website publishes; only the client was missing (15.09.2026).

    /** Stage a ready .nupkg and have it parsed: the answer carries the name, the version and the marker. */
    Task<StagedPackage> StageNupkg(string fileName, byte[] nupkg, string accessToken);

    /** Stage one file of a build; returns the id the packer will collect it by. */
    Task<string> UploadFile(string fileName, byte[] bytes, string accessToken);

    /** Pack the staged files ON THE STORE'S SIDE — the same route the CI pipeline takes. */
    Task<StagedPackage> PackFolder(IReadOnlyList<string> fileUploadIds, string? version, string accessToken);

    /** Publish the staged package. A new name then waits for a moderator; a new version does not. */
    Task<PublishedCard> PublishStaged(StagedPackage staged, string? category, string accessToken);
}

/** Reads the public store REST API (no auth needed for cards). */
public class StoreClient : IStoreClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    /** Uploads carry up to 20 MB of sources over a home connection; the 15 s of the rest is not enough. */
    private static readonly HttpClient UploadHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    /** API base; override with ENCY_STORE_API for test stands. */
    private readonly string _apiBase =
        (Environment.GetEnvironmentVariable("ENCY_STORE_API") ?? "https://apps.encycam.com/api").TrimEnd('/');

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

    public async Task<string?> GetRecommendedSdk()
    {
        try
        {
            var resp = await Http.GetAsync($"{_apiBase}/sdk");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            // registrableSdk first: the store answers with two numbers - what it recommends, and what
            // it can actually INSTALL. They differ while the extensions tab in the released
            // application carries older assemblies than ENCY ships, and building to the higher one
            // produces a package nobody can install (04.09.2026). The lower one lives on the server,
            // so raising it later is one setting rather than a release of this tool.
            if (doc.RootElement.TryGetProperty("registrableSdk", out var ceiling)
                && ceiling.ValueKind == JsonValueKind.String && ceiling.GetString() is { Length: > 0 } line)
                return line;
            return doc.RootElement.TryGetProperty("sdkVersion", out var v) ? v.GetString() : null;
        }
        catch (Exception)
        {
            return null;   // "don't know" is no reason to touch somebody's pin
        }
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
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await Http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return null;
        string body = await resp.Content.ReadAsStringAsync();
        return $"{(int)resp.StatusCode} {resp.ReasonPhrase}: {body.Trim()}";
    }

    // ---- the GitHub App route ------------------------------------------------------------------

    public async Task<AppStatus> GetAppStatus(string accessToken)
    {
        using var doc = await Send(Http, HttpMethod.Get, "/github/app", accessToken);
        var r = doc.RootElement;
        var installations = new List<string>();
        if (r.TryGetProperty("installations", out var inst))
            foreach (var i in inst.EnumerateArray())
                installations.Add(i.GetProperty("account").GetString() ?? "");
        var repos = new List<AppRepo>();
        if (r.TryGetProperty("repos", out var rs))
            foreach (var x in rs.EnumerateArray())
                repos.Add(new AppRepo(x.GetProperty("packageId").GetString() ?? "",
                    x.GetProperty("repository").GetString() ?? "", x.GetProperty("url").GetString() ?? ""));
        return new AppStatus(r.TryGetProperty("configured", out var c) && c.GetBoolean(), installations, repos);
    }

    public async Task<string> GetAppInstallUrl(string accessToken)
    {
        using var doc = await Send(Http, HttpMethod.Get, "/github/app/install", accessToken);
        return doc.RootElement.GetProperty("url").GetString() ?? "";
    }

    public async Task<AppRepo> CreateRepository(string packageId, string accessToken)
    {
        using var doc = await Send(Http, HttpMethod.Post, $"/extensions/{Uri.EscapeDataString(packageId)}/repo", accessToken);
        var r = doc.RootElement;
        return new AppRepo(r.GetProperty("packageId").GetString() ?? packageId,
            r.GetProperty("repository").GetString() ?? "", r.GetProperty("url").GetString() ?? "");
    }

    public async Task<RepoState> GetRepoState(string packageId, string accessToken)
    {
        using var doc = await Send(Http, HttpMethod.Get, $"/extensions/{Uri.EscapeDataString(packageId)}/repo", accessToken);
        var r = doc.RootElement;
        return new RepoState(r.GetProperty("stage").GetString() ?? "generating",
            r.TryGetProperty("ownCode", out var o) && o.GetBoolean(),
            r.TryGetProperty("branch", out var b) ? b.GetString() ?? "main" : "main");
    }

    public async Task<SourcesUploaded> UploadSources(string packageId, IReadOnlyList<SourceFile> files, string accessToken)
    {
        // Files as parts, their paths as ONE JSON list in the same order — the store's contract
        // (N+1 parts rather than 2N: Tomcat caps the number of parts).
        var form = new MultipartFormDataContent();
        foreach (var f in files)
        {
            var part = new ByteArrayContent(f.Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", System.IO.Path.GetFileName(f.Path));
        }
        form.Add(new StringContent(JsonSerializer.Serialize(files.Select(f => f.Path).ToList())), "paths");
        using var doc = await Send(UploadHttp, HttpMethod.Post, $"/extensions/{Uri.EscapeDataString(packageId)}/src", accessToken, form);
        var r = doc.RootElement;
        return new SourcesUploaded(r.GetProperty("repository").GetString() ?? "",
            r.GetProperty("commitSha").GetString() ?? "", r.GetProperty("commitUrl").GetString() ?? "",
            r.TryGetProperty("files", out var n) ? n.GetInt32() : files.Count);
    }

    public async Task<RunStarted> StartRun(string packageId, string accessToken)
    {
        using var doc = await Send(Http, HttpMethod.Post, $"/extensions/{Uri.EscapeDataString(packageId)}/run", accessToken);
        var r = doc.RootElement;
        return new RunStarted(r.GetProperty("repository").GetString() ?? "", r.GetProperty("actionsUrl").GetString() ?? "");
    }

    public async Task<IReadOnlyList<BuildReport>> GetMyBuilds(string accessToken)
    {
        using var doc = await Send(Http, HttpMethod.Get, "/builds/mine", accessToken);
        var list = new List<BuildReport>();
        foreach (var b in doc.RootElement.EnumerateArray())
            list.Add(new BuildReport(
                b.GetProperty("repository").GetString() ?? "",
                Str(b, "packageId"), b.GetProperty("status").GetString() ?? "",
                Str(b, "version"), Str(b, "failedStep"), Str(b, "failureLog"), Str(b, "runUrl"), Str(b, "updatedAt")));
        return list;
    }

    public async Task<StagedPackage> StageNupkg(string fileName, byte[] nupkg, string accessToken)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(nupkg);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        using var doc = await Send(UploadHttp, HttpMethod.Post, "/extensions/parse-nupkg", accessToken, form);
        return Staged(doc.RootElement);
    }

    public async Task<string> UploadFile(string fileName, byte[] bytes, string accessToken)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        using var doc = await Send(UploadHttp, HttpMethod.Post, "/storage/upload", accessToken, form);
        return doc.RootElement.GetProperty("uploadId").GetString() ?? "";
    }

    public async Task<StagedPackage> PackFolder(IReadOnlyList<string> fileUploadIds, string? version, string accessToken)
    {
        var body = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["fileUploadIds"] = fileUploadIds,
            ["version"] = string.IsNullOrWhiteSpace(version) ? null : version,
        }), Encoding.UTF8, "application/json");
        using var doc = await Send(UploadHttp, HttpMethod.Post, "/extensions/pack-folder", accessToken, body);
        return Staged(doc.RootElement);
    }

    public async Task<PublishedCard> PublishStaged(StagedPackage staged, string? category, string accessToken)
    {
        // packageId and version are required by the endpoint, but they come FROM THE PARSED package,
        // not from our say-so: what gets published is what the archive holds either way.
        var body = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["packageId"] = staged.PackageId,
            ["version"] = staged.Version,
            ["nupkgUploadId"] = staged.NupkgUploadId,
            ["category"] = string.IsNullOrWhiteSpace(category) ? null : category,
        }), Encoding.UTF8, "application/json");
        using var doc = await Send(UploadHttp, HttpMethod.Post, "/extensions", accessToken, body);
        var r = doc.RootElement;
        return new PublishedCard(r.GetProperty("slug").GetString() ?? "",
            r.GetProperty("packageId").GetString() ?? staged.PackageId,
            Str(r, "latestVersion"),
            r.TryGetProperty("approved", out var a) && a.ValueKind == JsonValueKind.True,
            r.TryGetProperty("unlisted", out var u) && u.ValueKind == JsonValueKind.True);
    }

    private static StagedPackage Staged(JsonElement r) => new(
        r.GetProperty("packageId").GetString() ?? "",
        r.GetProperty("version").GetString() ?? "",
        r.TryGetProperty("hasMarker", out var m) && m.ValueKind == JsonValueKind.True,
        r.GetProperty("nupkgUploadId").GetString() ?? "",
        Str(r, "sdkVersion"),
        r.TryGetProperty("sdkNewerThanAnyRelease", out var n) && n.ValueKind == JsonValueKind.True);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /** Sends with the bearer; a non-2xx answer becomes StoreApiException carrying the store's message. */
    private async Task<JsonDocument> Send(HttpClient http, HttpMethod method, string path, string accessToken, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, _apiBase + path) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new StoreApiException((int)resp.StatusCode, MessageOf(body, resp.ReasonPhrase));
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static string MessageOf(string body, string? fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? fallback ?? body;
        }
        catch (JsonException) { /* not JSON — the body itself is the message */ }
        return string.IsNullOrWhiteSpace(body) ? fallback ?? "" : body.Trim();
    }
}
