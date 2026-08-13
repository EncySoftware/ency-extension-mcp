using System.Text.Json;
using System.Text.RegularExpressions;

namespace EncyExtensionMcp;

public static class PackageInfo
{
    /** Where the manifest lives in an extension repo, relative to its root. */
    public static string PathIn(string repoDir) => Path.Combine(repoDir, "src", "package.info.json");

    /** packageId from src/package.info.json of an extension repo; null when absent/unreadable. */
    public static string? ReadPackageId(string repoDir) => ReadString(repoDir, "packageId");

    /**
     * The store category the repo publishes into.
     *
     * <p>It is a field in the manifest rather than an argument of the publish, and that is the whole
     * design: a tag push carries nothing but a name, so a per-publish flag would have to travel as a
     * workflow input and would be re-typed at every release. The server packer turns this field into
     * the `category:<id>` tag every .nupkg can carry, and the store reads that as a hint — it fills an
     * empty category and never overrules a person. Written once, honoured by every later publish.
     */
    public static string? ReadCategory(string repoDir) => ReadString(repoDir, "category");

    private static string? ReadString(string repoDir, string property)
    {
        var path = PathIn(repoDir);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(property, out var v) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /**
     * Write the category into the repo's manifest. Returns true when the file changed.
     *
     * <p>A text edit, not a re-serialise: the manifest is the author's file, kept in their repo and
     * read by them, and rewriting it through a JSON writer would reflow every line — a one-word
     * change arriving as a whole-file diff is how a tool loses the author's trust.
     */
    public static bool SetCategory(string repoDir, string category)
    {
        var path = PathIn(repoDir);
        if (!File.Exists(path)) throw new FileNotFoundException(
            "src/package.info.json not found — is this an ENCY extension repo?", path);
        var before = File.ReadAllText(path);
        var after = WithCategory(before, category);
        if (after == before) return false;
        File.WriteAllText(path, after);
        return true;
    }

    /**
     * The manifest with `category` set — replacing the value when the key is there, inserting the
     * line after `packageId` when it is not (that is where the template keeps it, and a key placed
     * where the author expects it reads as an edit rather than as damage).
     */
    public static string WithCategory(string json, string category)
    {
        var existing = new Regex("(\"category\"\\s*:\\s*\")([^\"]*)(\")");
        var m = existing.Match(json);
        if (m.Success)
            return m.Groups[2].Value == category
                ? json
                : json[..m.Groups[2].Index] + category + json[(m.Groups[2].Index + m.Groups[2].Length)..];

        // No key yet: put it under packageId, copying that line's own indentation and line ending.
        var anchor = new Regex("(?<indent>[ \\t]*)\"packageId\"\\s*:\\s*\"[^\"]*\",?(?<eol>\\r?\\n)");
        var a = anchor.Match(json);
        if (a.Success)
        {
            var line = $"{a.Groups["indent"].Value}\"category\": \"{category}\",{a.Groups["eol"].Value}";
            return json[..(a.Index + a.Length)] + line + json[(a.Index + a.Length)..];
        }

        // Not the shape we know. Leave the file alone rather than guess: the publish still works,
        // the category simply stays unset, and the author's file is not mangled by a tool.
        return json;
    }
}
