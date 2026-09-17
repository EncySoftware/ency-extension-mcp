using System.Text;
using System.Text.RegularExpressions;

namespace EncyExtensionMcp;

/** An extension project as found on disk: where the csproj is and where its manifest belongs. */
public sealed record Project(string Dir, string Csproj)
{
    /** `package.info.json` next to the csproj — where the template keeps it and where the packer looks. */
    public string Manifest => Path.Combine(Dir, "package.info.json");
}

/**
 * An extension does not have to come from our template: people already have projects, built by
 * hand or from older samples, and asking them to rebuild the template's layout around their code
 * just to publish is work nobody should be asked for (17.09.2026). This is everything that makes
 * such a project publishable: where it is, what it lacks, and how to fill the gap from its csproj.
 *
 * <para>The one thing that cannot be filled in is <c>&lt;Name&gt;.settings.json</c>: that is the
 * extension's own manifest of entry points, and ENCY registers nothing without it.</para>
 */
public static class ProjectLayout
{
    public const string SdkPackageId = "EncySoftware.CAMAPI.SDK.Net";

    /**
     * The project is a folder with one csproj: `src/` first (the template's layout), then the folder
     * itself. Two csproj files in one place is a question for the author, not a coin toss — the
     * reason comes back in `error`.
     */
    public static Project? Locate(string repoDir, out string? error)
    {
        error = null;
        foreach (var dir in new[] { Path.Combine(repoDir, "src"), repoDir })
        {
            if (!Directory.Exists(dir)) continue;
            var found = Directory.GetFiles(dir, "*.csproj");
            if (found.Length == 0) continue;
            if (found.Length > 1)
            {
                error = $"{dir} holds several projects ({string.Join(", ", found.Select(Path.GetFileName))}) — "
                      + "keep one csproj in the extension folder, or point at that project's folder.";
                return null;
            }
            return new Project(dir, found[0]);
        }
        return null;
    }

    /**
     * The project a build-output folder came from: the csproj is a few levels up
     * (bin/Release/&lt;tfm&gt;/ is three). Stops at the first folder holding exactly one csproj.
     */
    public static Project? LocateAbove(string outputDir, int levels = 4)
    {
        string? dir = Path.GetFullPath(outputDir);
        for (int i = 0; i <= levels && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            var found = Directory.GetFiles(dir, "*.csproj");
            if (found.Length == 1) return new Project(dir, found[0]);
            if (found.Length > 1) return null;
        }
        return null;
    }

    /** A property out of the csproj — the first occurrence, conditions ignored. Enough for a name and a version. */
    public static string? CsprojProperty(string xml, string name)
    {
        var m = Regex.Match(xml, $"<{name}>\\s*([^<]*?)\\s*</{name}>", RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
    }

    /**
     * A manifest for a project that has none, in the template's shape: the package name from
     * AssemblyName or the csproj file, the version from the csproj or 0.1.0, the SDK from the
     * package reference. The store's marker tag is there from the start — without it the catalogue
     * never sees the package. The category is not guessed: the card sits under `other` until the
     * author names one (the `category` argument of every publish).
     */
    public static string ManifestSkeleton(string csprojXml, string csprojPath)
    {
        string id = CsprojProperty(csprojXml, "AssemblyName") ?? CsprojProperty(csprojXml, "PackageId")
                    ?? Path.GetFileNameWithoutExtension(csprojPath);
        string version = CsprojProperty(csprojXml, "Version") ?? "0.1.0";
        string? sdk = SdkPin.ReadCsproj(csprojXml);
        string description = CsprojProperty(csprojXml, "Description") ?? "";
        string tfm = CsprojProperty(csprojXml, "TargetFramework") ?? "";
        if (tfm.EndsWith("-windows", StringComparison.OrdinalIgnoreCase)) tfm = tfm[..^"-windows".Length];

        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"packageId\": \"{Json(id)}\",\n");
        sb.Append($"  \"version\": \"{Json(version)}\",\n");
        if (tfm.Length > 0) sb.Append($"  \"targetFramework\": \"{Json(tfm)}\",\n");
        if (sdk != null)
        {
            sb.Append($"  \"sdkVersion\": \"{Json(sdk)}\",\n");
            sb.Append("  \"dependencies\": [\n");
            sb.Append($"    {{ \"id\": \"{SdkPackageId}\", \"version\": \"{Json(sdk)}\" }}\n");
            sb.Append("  ],\n");
        }
        sb.Append($"  \"description\": \"{Json(description)}\",\n");
        sb.Append("  \"author\": \"\",\n");
        sb.Append("  \"category\": \"other\",\n");
        sb.Append($"  \"tags\": \"{PackageCheck.MarkerTag}\",\n");
        sb.Append("  \"requiresRestart\": false\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /** Whether the store's marker is among the manifest's tags (comma, space or semicolon separated, any case). */
    public static bool HasMarker(string manifestJson)
    {
        var m = Regex.Match(manifestJson, "\"tags\"\\s*:\\s*\"([^\"]*)\"");
        if (!m.Success) return false;
        return m.Groups[1].Value.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(t => t.Equals(PackageCheck.MarkerTag, StringComparison.OrdinalIgnoreCase));
    }

    /**
     * The manifest with the marker: appended to the tags it has, or added as a line of its own
     * under `packageId`. A text edit, not a re-serialise — the author's file changes by one line,
     * not by a reflow of every line (the same rule PackageInfo.WithCategory follows).
     */
    public static string WithMarker(string manifestJson)
    {
        if (HasMarker(manifestJson)) return manifestJson;
        var tags = new Regex("(\"tags\"\\s*:\\s*\")([^\"]*)(\")");
        var m = tags.Match(manifestJson);
        if (m.Success)
        {
            string value = m.Groups[2].Value.Trim();
            string joined = value.Length == 0 ? PackageCheck.MarkerTag : value + ", " + PackageCheck.MarkerTag;
            return manifestJson[..m.Groups[2].Index] + joined + manifestJson[(m.Groups[2].Index + m.Groups[2].Length)..];
        }
        var anchor = new Regex("(?<indent>[ \\t]*)\"packageId\"\\s*:\\s*\"[^\"]*\",?(?<eol>\\r?\\n)");
        var a = anchor.Match(manifestJson);
        if (!a.Success) return manifestJson;
        string line = $"{a.Groups["indent"].Value}\"tags\": \"{PackageCheck.MarkerTag}\",{a.Groups["eol"].Value}";
        return manifestJson[..(a.Index + a.Length)] + line + manifestJson[(a.Index + a.Length)..];
    }

    /**
     * Give the project what the store's packer cannot do without: a manifest. The marker tag is
     * added to an existing manifest when the file's shape allows it — the store's packer stamps the
     * marker itself, so this only matters for a package the author packs locally, and a manifest of
     * an unfamiliar shape is left alone rather than mangled. Every edit to the author's files comes
     * back as a line for the answer — a file changed in silence is the worst kind of help.
     */
    public static void Adopt(Project project, List<string> notes)
    {
        if (!File.Exists(project.Manifest))
        {
            File.WriteAllText(project.Manifest, ManifestSkeleton(File.ReadAllText(project.Csproj), project.Csproj));
            notes.Add("package.info.json written from the csproj (name, version, SDK) — check the description and pick a category");
            return;
        }
        string json = File.ReadAllText(project.Manifest);
        if (HasMarker(json)) return;
        string withMarker = WithMarker(json);
        if (withMarker == json) return;
        File.WriteAllText(project.Manifest, withMarker);
        notes.Add($"`{PackageCheck.MarkerTag}` added to the tags of package.info.json — a package packed locally needs it to be seen by the catalogue");
    }

    private static string Json(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
