using System.Text.Json;
using System.Text.RegularExpressions;

namespace EncyExtensionMcp;

/** Something worth saying about a folder before it is published; a blocking one stops the publish. */
public sealed record Finding(bool Blocking, string Text);

/// <summary>
/// What can be told about an extension folder without building it.
///
/// <para>Everything checked here is decided by a file nobody looks at twice - the manifest, the
/// settings, the readme - and every mistake in them is learned late and expensively: a name that
/// does not match reserves a SECOND name in the store (and only an administrator can free it), an
/// identifier the code no longer answers to installs and silently never registers, and the
/// template's own words become the public card.</para>
/// </summary>
public static class Preflight
{
    /// <param name="root">The extension folder.</param>
    /// <param name="name">The name it would be published under, when it is known.</param>
    /// <param name="templateReadme">The template's own readme, to recognise an untouched one.</param>
    /// <param name="templateDescription">The template's own description, for the same reason.</param>
    public static IReadOnlyList<Finding> Check(string root, string? name = null,
                                               string? templateReadme = null, string? templateDescription = null)
    {
        var found = new List<Finding>();
        string? manifestPath = FindFile(root, "package.info.json");
        if (manifestPath == null)
        {
            found.Add(new Finding(true, $"no package.info.json under {root} - the store cannot make a package out of this folder."));
            return found;
        }

        string dir = Path.GetDirectoryName(manifestPath)!;
        JsonElement info;
        try
        {
            info = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
        }
        catch (Exception e)
        {
            found.Add(new Finding(true, $"package.info.json is not readable as JSON ({e.Message}) - the packer reads this file first."));
            return found;
        }

        string? packageId = Str(info, "packageId");
        if (name != null && packageId != null && !name.Equals(packageId, StringComparison.OrdinalIgnoreCase))
            found.Add(new Finding(true,
                $"the name given is '{name}' and package.info.json says '{packageId}'. Publishing under a name of "
                + "its own would create a SECOND extension in the store and reserve that name - only an administrator "
                + $"can free it. Fix packageId, or publish as '{packageId}'."));

        string? description = Str(info, "description");
        if (string.IsNullOrWhiteSpace(description) || Same(description, templateDescription))
            found.Add(new Finding(false, "description in package.info.json is still the template's - it is the line under the name on the store card."));

        if (string.IsNullOrWhiteSpace(Str(info, "author")))
            found.Add(new Finding(false, "author in package.info.json is empty - the card will name nobody."));

        string? category = Str(info, "category");
        if (string.IsNullOrWhiteSpace(category) || category == "other")
            found.Add(new Finding(false, "category is 'other', so the card lands in the catalogue's leftovers - pick one the store knows."));

        // The manifest names the framework of the lib/<tfm> folder inside the package, the project names
        // the one it builds for. They differ by the platform suffix only; anything else and the dll is
        // laid out under a name the manifest never mentions - the package installs as files and nothing runs.
        string? csprojPath = Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault();
        string? csproj = Read(csprojPath);
        string? tfmProject = csproj == null ? null : Group(csproj, "<TargetFramework>([^<]+)</TargetFramework>");
        string? tfmManifest = Str(info, "targetFramework");
        if (tfmProject != null && tfmManifest != null && Platformless(tfmProject) != tfmManifest.Trim())
            found.Add(new Finding(false,
                $"targetFramework in package.info.json is '{tfmManifest}' and the project builds '{tfmProject}' - "
                + "the dll would be packed under a folder the manifest does not name."));

        string? readme = Read(FindFile(dir, "readme.md"));
        if (readme == null)
            found.Add(new Finding(false, "there is no readme.md - the store card would have no text at all."));
        else if (string.IsNullOrWhiteSpace(readme) || Same(readme, templateReadme))
            found.Add(new Finding(false, "readme.md is still the template's - that text becomes the public card."));

        found.AddRange(CheckSettings(dir, csprojPath));
        return found;
    }

    /// <summary>The settings file is the contract with ENCY: it names the library to load and the
    /// identifiers ENCY will ask the factory for. Both are written by hand and neither is checked by
    /// the compiler, so a rename in one place and not the other passes every build and then fails to
    /// register with a message about a factory the author never wrote.</summary>
    private static IEnumerable<Finding> CheckSettings(string dir, string? csprojPath)
    {
        string? settingsPath = Directory.EnumerateFiles(dir, "*.settings.json").FirstOrDefault();
        string? settings = Read(settingsPath);
        if (settings == null) yield break;

        JsonElement root = default;
        string? unreadable = null;
        try { root = JsonDocument.Parse(settings).RootElement; }
        catch (Exception e) { unreadable = e.Message; }
        if (unreadable != null)
        {
            yield return new Finding(true, $"{Path.GetFileName(settingsPath)} is not readable as JSON ({unreadable}) - ENCY reads it to register the library.");
            yield break;
        }

        string expectedDll = Path.GetFileNameWithoutExtension(csprojPath ?? "") + ".dll";
        string? modulePath = Str(root, "module_path");
        if (modulePath != null && csprojPath != null
            && !Path.GetFileName(modulePath.Replace('\\', '/')).Equals(expectedDll, StringComparison.OrdinalIgnoreCase))
            yield return new Finding(false,
                $"module_path points at {Path.GetFileName(modulePath.Replace('\\', '/'))} and the project builds {expectedDll} - "
                + "ENCY would look for a library that is not there.");

        var sources = new List<string>();
        foreach (var cs in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (cs.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || cs.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (Read(cs) is { } text) sources.Add(text);
        }
        if (sources.Count == 0) yield break;

        foreach (string id in Ids(root))
            if (!sources.Any(s => s.Contains("\"" + id + "\"")))
                yield return new Finding(false,
                    $"{Path.GetFileName(settingsPath)} declares the identifier \"{id}\" and no source answers to it - "
                    + "ENCY asks the factory for exactly this string, so the extension would install and never start.");
    }

    /** The identifiers declared under "extensions", whatever kind each entry is. */
    private static IEnumerable<string> Ids(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object
            || !settings.TryGetProperty("extensions", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            foreach (var kind in entry.EnumerateObject())
                if (kind.Value.ValueKind == JsonValueKind.Object && Str(kind.Value, "id") is { Length: > 0 } id)
                    yield return id;
        }
    }

    /** A field of a JSON object, when it is a string. */
    public static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /** The same field straight out of JSON text, for callers holding a file rather than a document. */
    public static string? Field(string json, string name)
    {
        try { return Str(JsonDocument.Parse(json).RootElement, name); }
        catch { return null; }
    }

    private static string Platformless(string tfm) => tfm.Split('-')[0].Trim();

    private static bool Same(string? a, string? b) =>
        a != null && b != null && a.Replace("\r\n", "\n").Trim() == b.Replace("\r\n", "\n").Trim();

    private static string? Read(string? path)
    {
        try { return path != null && File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static string? Group(string text, string pattern) =>
        Regex.Match(text, pattern) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

    /** The first file with this name under root, ignoring build output. */
    private static string? FindFile(string root, string name)
    {
        try
        {
            return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                  && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        }
        catch { return null; }
    }
}
