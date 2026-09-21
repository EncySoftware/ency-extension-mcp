using System.Text.Json;

namespace EncyExtensionMcp;

/**
 * The Schedule A declaration as package.info.json carries it: whether the extension works in a
 * Reserved Functionality Domain the ENCY Publishing Policy reserves, which Entitlement it verifies,
 * and the three Policy paragraphs the publisher confirms.
 */
public sealed record ScheduleADeclaration(bool None, string? Domain, string? Entitlement,
                                          IReadOnlyList<string> Confirmations)
{
    /** Nothing answered at all — the shape the template ships, so the author sees where it goes. */
    public bool Unanswered => !None
                              && string.IsNullOrWhiteSpace(Domain)
                              && string.IsNullOrWhiteSpace(Entitlement)
                              && Confirmations.Count == 0;
}

/**
 * What can be said about that declaration here, before a publish rather than after it.
 *
 * <p>The store is the authority: it holds the domain codes and the Entitlement names the licensing
 * system knows, and it decides. This only catches what is visible in the file — no block, or the
 * template's empty one — because that is the case that costs an author a red build for a file we
 * shipped. Until the documents take effect the store publishes such a submission with a warning;
 * from that day it refuses, and from that day so does this.</p>
 */
public static class ScheduleA
{
    /** The manifest key, the same one the store reads out of the package. */
    public const string Key = "reservedFunctionality";

    public const string Url = "https://encycam.com/legal/extension-store/reserved-functionality/";

    /** Policy paragraphs a declaration confirms; all three, or the store calls it incomplete. */
    public static readonly IReadOnlyList<string> Confirmations = new[] { "4.2", "4.9", "4.6" };

    /** The day the documents take effect — the store's own `app.legal.enforce-from`. */
    public static readonly DateOnly RefusedFrom = new(2026, 11, 1);

    /** The block of a package.info.json root element; null when the manifest carries none. */
    public static ScheduleADeclaration? Read(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty(Key, out var block) || block.ValueKind != JsonValueKind.Object)
            return null;
        string? domain = Str(block, "domain");
        // "domain": "none" is the other way of writing the first answer, as the store reads it too.
        bool none = (block.TryGetProperty("none", out var n) && n.ValueKind == JsonValueKind.True)
                    || string.Equals(domain, "none", StringComparison.OrdinalIgnoreCase);
        var confirmations = new List<string>();
        if (block.TryGetProperty("confirmations", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var c in list.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s)
                    confirmations.Add(s.Trim());
        return new ScheduleADeclaration(none, none ? null : domain, Str(block, "entitlement"), confirmations);
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
        string how = "answer it with {\"none\": true, \"confirmations\": [\"4.2\", \"4.9\", \"4.6\"]}, or with a "
                     + "\"domain\" code and the \"entitlement\" it verifies — " + Url;
        // Said to whoever reads this, which is usually an assistant: the declaration is the author's
        // statement about their own extension, and the three confirmations are theirs to make.
        const string whose = "It is the author's statement — ask them, do not answer it for them.";

        if (d == null)
        {
            yield return new Finding(refused, $"package.info.json has no `{Key}` — every submission declares whether the "
                                            + $"extension works in a Reserved Functionality Domain of Schedule A: {how}. "
                                            + $"{deadline}. {whose}");
            yield break;
        }
        if (d.Unanswered)
        {
            yield return new Finding(refused, $"`{Key}` in package.info.json is still unanswered — {how}. {deadline}. {whose}");
            yield break;
        }
        // Answered, however partly: the store has the domain list and the Entitlement names, so it
        // gets the last word on those. These two gaps need neither, and cost a round trip each.
        if (!d.None && string.IsNullOrWhiteSpace(d.Entitlement))
            yield return new Finding(false, $"`{Key}` names domain {d.Domain} and no `entitlement` — the store asks which "
                                          + "Entitlement the extension verifies there, spelled as the licensing system spells it.");
        var missing = Confirmations.Where(c => !d.Confirmations.Contains(c)).ToList();
        if (missing.Count > 0)
            // Both numbers spelled out: a list that carries only the subject leaves the sentence
            // ungrammatical for one of them, and one missing paragraph is the likelier case.
            yield return new Finding(false, $"`{Key}` does not confirm Policy "
                                          + (missing.Count == 1 ? "paragraph " : "paragraphs ")
                                          + string.Join(", ", missing)
                                          + " — the store asks for all three (4.2, 4.9, 4.6).");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s.Trim() : null;
}
