using System.Reflection;
using System.Text.Json;

namespace EncyExtensionMcp;

/** One line for the end of a result when a newer tool is on nuget.org; null when there is nothing to say. */
public interface IUpdateCheck
{
    Task<string?> Note();
}

/** Tests and the terminal commands that must stay quiet. */
public sealed class NoUpdateCheck : IUpdateCheck
{
    public Task<string?> Note() => Task.FromResult<string?>(null);
}

/**
 * Whether a newer tool exists, asked of nuget.org at most once a day and answered from a cache file
 * in between. The tool used to have no idea what version it was, so an assistant asked to "update
 * the tool" had nothing to go on but the author's memory, and the flat index of nuget.org lags a
 * fresh release by up to an hour — hence the `--no-cache` in the command it hands out (16.09.2026).
 * Everything here fails quiet: a publish must not wait on, or break over, a version check.
 */
public sealed class UpdateCheck : IUpdateCheck
{
    public const string PackageId = "EncySoftware.ExtensionStoreMcp";
    public const string IndexUrl = "https://api.nuget.org/v3-flatcontainer/encysoftware.extensionstoremcp/index.json";
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /** What is running now, as the package was built: "0.2.13". */
    public static string Current =>
        (typeof(UpdateCheck).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(UpdateCheck).Assembly.GetName().Version?.ToString(3) ?? "0.0.0").Split('+')[0];

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ency-extension-mcp", "update-check.json");

    private readonly Func<Task<string?>> fetchLatest;
    private readonly string cachePath;
    private readonly Func<DateTimeOffset> now;
    private readonly string current;

    public UpdateCheck(Func<Task<string?>>? fetchLatest = null, string? cachePath = null,
                       Func<DateTimeOffset>? now = null, string? current = null)
    {
        this.fetchLatest = fetchLatest ?? LatestOnNuget;
        this.cachePath = cachePath ?? CachePath;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.current = current ?? Current;
    }

    public async Task<string?> Note()
    {
        string? latest = await Latest();
        return latest != null && IsNewer(latest, current)
            ? $"Tool {current}; {latest} is available: dotnet tool update -g {PackageId} --no-cache"
            : null;
    }

    /** The newest stable version on nuget.org, from the cache when it was asked within the day. */
    public async Task<string?> Latest()
    {
        var cached = ReadCache();
        if (cached.CheckedAt != null && now() - cached.CheckedAt < CheckEvery) return cached.Latest;
        string? latest = null;
        try { latest = await fetchLatest(); }
        catch (Exception) { /* offline, blocked, or nuget.org down: the cache still gets a stamp, so this is not retried on every call */ }
        WriteCache(latest);
        return latest;
    }

    public static async Task<string?> LatestOnNuget() => LatestOf(await Http.GetStringAsync(IndexUrl));

    /** The highest stable version in a flat-container index; pre-releases are not offered as updates. */
    public static string? LatestOf(string indexJson)
    {
        using var doc = JsonDocument.Parse(indexJson);
        string? best = null;
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            string s = v.GetString() ?? "";
            if (s.Contains('-')) continue;
            if (best == null || IsNewer(s, best)) best = s;
        }
        return best;
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate.Split('-')[0], out var a) && Version.TryParse(current.Split('-')[0], out var b) && a > b;

    private (DateTimeOffset? CheckedAt, string? Latest) ReadCache()
    {
        try
        {
            if (!File.Exists(cachePath)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));
            var root = doc.RootElement;
            DateTimeOffset? at = root.TryGetProperty("checkedAt", out var c) && c.TryGetDateTimeOffset(out var dto) ? dto : null;
            string? latest = root.TryGetProperty("latest", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            return (at, latest);
        }
        catch (Exception) { return (null, null); }
    }

    private void WriteCache(string? latest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(new { checkedAt = now(), latest }));
        }
        catch (Exception) { /* a cache that cannot be written just means asking again next time */ }
    }
}
