using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace EncyExtensionMcp;

/** What a ready .nupkg says about itself — read off the archive, nothing uploaded. */
public sealed record PackageFacts(
    string? PackageId, string? Version, bool HasMarker, string? CategoryTag,
    bool HasManifest, bool HasAssembly, bool HasReadme, bool HasIcon, int Screenshots, string? SdkVersion,
    /** The Schedule A declaration the manifest carries; null when it carries none. */
    ScheduleADeclaration? Declaration = null,
    /** Why the manifest would not find its dll once ENCY has installed the package; null when it would. */
    string? LayoutProblem = null);

/**
 * The checks a ready package gets before it is published — the counterpart of Preflight, which reads
 * a source folder. Everything here is read locally: no sign-in, no upload, no staged copy left on
 * the server. The store re-checks the marker and the manifest on its side (16.09.2026).
 */
public static class PackageCheck
{
    /** The tag the catalogue looks for; a package without it is published and never seen. */
    public const string MarkerTag = "ency-extension";

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    /** Null when the file is not a zip at all — then the server gets to say what it is. */
    public static PackageFacts? TryRead(string nupkgPath)
    {
        try
        {
            using var stream = File.OpenRead(nupkgPath);
            return Read(stream);
        }
        catch (InvalidDataException) { return null; }
    }

    public static PackageFacts Read(Stream zip)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        string? id = null, version = null, category = null, sdk = null;
        bool marker = false, manifest = false, assembly = false, readme = false, icon = false;
        int screenshots = 0;
        bool manifestRead = false;
        ScheduleADeclaration? declaration = null;

        var nuspec = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        string? nuspecSdk = null;
        if (nuspec != null)
        {
            using var s = nuspec.Open();
            var doc = XDocument.Load(s);
            // The nuspec namespace differs between NuGet versions; match by local name only.
            string? Elem(string name) => doc.Descendants().FirstOrDefault(x => x.Name.LocalName == name)?.Value;
            id = Elem("id");
            version = Elem("version");
            if (Elem("icon") is { Length: > 0 }) icon = true;
            if (Elem("readme") is { Length: > 0 }) readme = true;
            foreach (var tag in (Elem("tags") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (tag.Equals(MarkerTag, StringComparison.OrdinalIgnoreCase)) marker = true;
                else if (tag.StartsWith("category:", StringComparison.OrdinalIgnoreCase) && tag.Length > "category:".Length)
                    category = tag["category:".Length..].ToLowerInvariant();
            }
            // The store reads the SDK from here too (a package made with `dotnet pack` has no
            // package.info.json at all), and takes the higher of the two.
            nuspecSdk = doc.Descendants()
                .Where(x => x.Name.LocalName == "dependency"
                            && string.Equals((string?)x.Attribute("id"), ProjectLayout.SdkPackageId, StringComparison.OrdinalIgnoreCase))
                .Select(x => LowerBound((string?)x.Attribute("version")))
                .FirstOrDefault(v => v != null);
        }

        foreach (var e in archive.Entries)
        {
            string name = Path.GetFileName(e.FullName);
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            if (name.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase)) manifest = true;
            else if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) assembly = true;
            else if (name.Equals("readme.md", StringComparison.OrdinalIgnoreCase)) readme = true;
            else if (name.Equals("package.info.json", StringComparison.OrdinalIgnoreCase) && !manifestRead)
            {
                manifestRead = true;
                (sdk, declaration) = ManifestOf(e);
            }

            bool image = ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
            bool iconName = stem.Equals("icon", StringComparison.OrdinalIgnoreCase) || stem.Equals("logo", StringComparison.OrdinalIgnoreCase);
            if (image && iconName) icon = true;
            // The store never takes the icon as a cover, so a screenshot is an image under a
            // screenshots folder that is not called icon or logo.
            else if (image && e.FullName.Contains("screenshots/", StringComparison.OrdinalIgnoreCase)) screenshots++;
        }

        // Where the manifest and the dll land once installed: having both inside is not enough.
        var names = archive.Entries.Select(e => e.FullName).ToList();
        string? layout = InstallLayout.ManifestProblem(InstallLayout.Place(names), names, name =>
        {
            var e = archive.Entries.FirstOrDefault(x => x.FullName.Replace('\\', '/') == name);
            if (e == null) return null;
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
        return new PackageFacts(id, version, marker, category, manifest, assembly, readme, icon, screenshots,
                                Higher(sdk, nuspecSdk), declaration, layout);
    }

    /** `[3.0.15]`, `3.0.15` or `[3.0.15, )` → 3.0.15; null when there is no lower bound to read. */
    private static string? LowerBound(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        string v = range.Trim().TrimStart('[', '(').Split(',')[0].Trim().TrimEnd(']', ')').Trim();
        return v.Length > 0 && v.Length <= 64 ? v : null;
    }

    /** The higher of two SDK versions, as the store takes it; either one when the other is missing or unreadable. */
    private static string? Higher(string? a, string? b)
    {
        Version? na = Numeric(a), nb = Numeric(b);
        if (na == null) return nb != null ? b : a ?? b;
        if (nb == null) return a;
        return nb > na ? b : a;
    }

    /** The two things the manifest is read for, in one pass over it. */
    private static (string? Sdk, ScheduleADeclaration? Declaration) ManifestOf(ZipArchiveEntry e)
    {
        try
        {
            using var s = e.Open();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            string? sdk = root.TryGetProperty("sdkVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (sdk, ScheduleA.Read(root));
        }
        catch (JsonException) { return (null, null); }
    }

    /**
     * What to say about the package. Blocking findings are the ones the store would refuse anyway;
     * the rest is what the author would otherwise learn from a bare card in the catalogue.
     *
     * @param today for the tests: the Schedule A deadline is a real date and this code outlives it.
     */
    public static IReadOnlyList<Finding> Findings(PackageFacts f, IReadOnlyList<StoreCategory> categories,
                                                  string? recommendedSdk, DateOnly? today = null)
    {
        var found = new List<Finding>();
        if (!f.HasMarker)
            found.Add(new Finding(true, $"no `{MarkerTag}` tag among the package tags — the catalogue would never pick it up. "
                                      + "The ENCY pack tool and the template add it; a plain `dotnet pack` does not."));
        if (!f.HasManifest)
            found.Add(new Finding(true, "no <Name>.settings.json inside — the store refuses a package without the manifest ENCY registers."));
        if (!f.HasAssembly)
            found.Add(new Finding(true, "no .dll inside — nothing for ENCY to load."));
        // The store takes such a package, and ENCY installs it as files that never register: both have
        // a manifest and a dll inside, which is all anybody checked (a package laid out under
        // contentFiles/, 08.10.2026).
        if (f.LayoutProblem != null)
            found.Add(new Finding(true, f.LayoutProblem + " " + InstallLayout.Rule
                                      + " connect_to_store makes that change in the project's csproj."));

        if (f.CategoryTag == null)
            found.Add(new Finding(false, "no `category:<id>` tag, so the card shows under 'other' until one is set — "
                                       + "pass category=<id> when publishing or add the tag."
                                       + (categories.Count > 0 ? " Known: " + string.Join(", ", categories.Select(c => c.Id)) + "." : "")));
        else if (categories.Count > 0 && categories.All(c => c.Id != f.CategoryTag))
            found.Add(new Finding(false, $"category '{f.CategoryTag}' is not one the store knows, so it is ignored. Known: "
                                       + string.Join(", ", categories.Select(c => c.Id)) + "."));

        if (f.Screenshots == 0)
            found.Add(new Finding(false, "no screenshots — the card gets no cover picture (images under src/screenshots become one)."));
        if (!f.HasReadme)
            found.Add(new Finding(false, "no readme — the card page shows the one-line description and nothing else."));
        if (!f.HasIcon)
            found.Add(new Finding(false, "no icon — the card shows a letter tile instead."));

        if (f.SdkVersion == null)
            found.Add(new Finding(false, "the package names no SDK version — neither `sdkVersion` in package.info.json nor a dependency on "
                                       + $"{ProjectLayout.SdkPackageId} in the nuspec — so the store cannot tell which ENCY release it fits."));
        else if (Numeric(f.SdkVersion) is { } built && Numeric(recommendedSdk) is { } shipped && built > shipped)
            found.Add(new Finding(false, $"built against SDK {f.SdkVersion}, newer than the newest released ENCY carries ({recommendedSdk}) — "
                                       + "it installs for nobody until that release ships."));

        found.AddRange(ScheduleA.Findings(f.Declaration, today));
        return found;
    }

    /** 3.0.8-dev.2 → 3.0.8; null when there is no version to read. */
    private static Version? Numeric(string? sdk)
    {
        if (string.IsNullOrWhiteSpace(sdk)) return null;
        int cut = sdk.IndexOfAny(new[] { '-', '+' });
        return Version.TryParse(cut > 0 ? sdk[..cut] : sdk, out var v) ? v : null;
    }
}
