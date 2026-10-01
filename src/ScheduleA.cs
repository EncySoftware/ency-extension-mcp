using System.Text.Json;

namespace EncyExtensionMcp;

/** One entry of `reservedDomains` (Schedule B §B.3.2). */
public sealed record ReservedDomainEntry(string? Domain, string? Entitlement, IReadOnlyList<string> Capabilities);

/**
 * The Reserved Functionality declaration as package.info.json carries it: `reservedDomains`, the
 * Schedule A areas the extension provides (empty = none). The earlier `reservedFunctionality` block is
 * still read, and named as the old form.
 */
public sealed record ScheduleADeclaration(IReadOnlyList<ReservedDomainEntry> Domains, bool Legacy, string? Malformed);

/**
 * What can be said about the declaration here, before a publish rather than after it. The store is the
 * authority — it holds the areas and their licences, and it asks the author to confirm the answer once
 * in the browser. This catches what is visible in the file: no list, a broken one, the old form.
 */
public static class ScheduleA
{
    public const string Key = "reservedDomains";
    public const string LegacyKey = "reservedFunctionality";
    public const string Url = "https://encycam.com/legal/extension-store/reserved-functionality/";

    /** The day the documents take effect — the store's own `app.legal.enforce-from`. */
    public static readonly DateOnly RefusedFrom = new(2026, 11, 1);

    public static ScheduleADeclaration? Read(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object) return null;
        if (manifest.TryGetProperty(Key, out var list) && list.ValueKind != JsonValueKind.Null)
        {
            if (list.ValueKind != JsonValueKind.Array)
                return new ScheduleADeclaration([], false,
                    $"`{Key}` must be a list - [] when the extension provides nothing Schedule A lists");
            var entries = new List<ReservedDomainEntry>();
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object)
                    return new ScheduleADeclaration([], false,
                        $"every entry of `{Key}` is an object with \"domain\", \"entitlement\" and \"capabilities\"");
                var capabilities = new List<string>();
                if (e.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
                    foreach (var x in c.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s)
                            capabilities.Add(s.Trim());
                entries.Add(new ReservedDomainEntry(Str(e, "domain"), Str(e, "entitlement"), capabilities));
            }
            return new ScheduleADeclaration(entries, false, null);
        }
        if (manifest.TryGetProperty(LegacyKey, out var block) && block.ValueKind == JsonValueKind.Object)
        {
            string? domain = Str(block, "domain");
            bool none = (block.TryGetProperty("none", out var n) && n.ValueKind == JsonValueKind.True)
                        || string.Equals(domain, "none", StringComparison.OrdinalIgnoreCase);
            if (none) return new ScheduleADeclaration([], true, null);
            if (domain == null) return null;   // the template's empty block: no answer at all
            return new ScheduleADeclaration([new ReservedDomainEntry(domain, Str(block, "entitlement"), [])], true, null);
        }
        return null;
    }

    /** Same, read straight from the file's text; null when it is not JSON at all. */
    public static ScheduleADeclaration? ReadJson(string manifestJson)
    {
        try { return Read(JsonDocument.Parse(manifestJson).RootElement); }
        catch (JsonException) { return null; }
    }

    /** @param today for the tests; the deadline is a real date and this code outlives it. */
    public static IEnumerable<Finding> Findings(ScheduleADeclaration? d, DateOnly? today = null)
    {
        bool refused = (today ?? DateOnly.FromDateTime(DateTime.UtcNow)) >= RefusedFrom;
        string deadline = refused
            ? "the store refuses a submission without it"
            : $"until {RefusedFrom:yyyy-MM-dd} the store publishes anyway and says so; after that it refuses";
        string how = $"add \"{Key}\": [] when the extension provides nothing Schedule A lists, or one entry "
                     + "{\"domain\", \"entitlement\"} per area it does - " + Url;
        // Said to whoever reads this, which is usually an assistant: the declaration is the author's
        // statement about their own extension, and the store asks the author to confirm it.
        const string whose = "It is the author's statement - ask them, do not answer it for them; "
                             + "the store asks them to confirm it once in the browser.";

        if (d == null)
        {
            yield return new Finding(refused, $"package.info.json has no `{Key}` - {how}. {deadline}. {whose}");
            yield break;
        }
        if (d.Malformed != null)
        {
            yield return new Finding(true, d.Malformed + " - " + Url);
            yield break;
        }
        if (d.Legacy)
            yield return new Finding(refused, $"`{LegacyKey}` is the earlier form of the declaration: write `{Key}` "
                                            + "instead (Schedule B B.3.2). "
                                            + (refused ? "The store no longer reads it." : $"The store reads it until {RefusedFrom.AddDays(-1):yyyy-MM-dd}."));
        foreach (var e in d.Domains)
            if (string.IsNullOrWhiteSpace(e.Domain) || string.IsNullOrWhiteSpace(e.Entitlement))
                yield return new Finding(false, $"an entry of `{Key}` lacks \"domain\" or \"entitlement\" - each area "
                                              + $"comes with the licence Schedule A assigns to it ({Url}).");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s.Trim() : null;
}
