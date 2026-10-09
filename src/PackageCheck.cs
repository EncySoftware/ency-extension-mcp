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
    string? LayoutProblem = null,
    /**
     * The package.info.json the store reads — the first entry whose name ends in it, wherever it sits
     * (build/package.info.json, say); null when the package carries none.
     */
    string? ManifestEntry = null);

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
        string? manifestEntry = null;
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
            // package.info.json at all), and takes the higher of the two. Of several groups naming
            // it, the highest — the store's NuspecReader.sdkDependency.
            foreach (var dep in doc.Descendants().Where(x => x.Name.LocalName == "dependency"
                         && string.Equals((string?)x.Attribute("id"), ProjectLayout.SdkPackageId, StringComparison.OrdinalIgnoreCase)))
                if (LowerBound((string?)dep.Attribute("version")) is { } low
                    && (nuspecSdk == null || CompareVersions(low, nuspecSdk) > 0)) nuspecSdk = low;
        }

        foreach (var e in archive.Entries)
        {
            string name = Path.GetFileName(e.FullName);
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            if (name.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase)) manifest = true;
            else if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)) assembly = true;
            else if (name.Equals("readme.md", StringComparison.OrdinalIgnoreCase)) readme = true;
            // The store's own pick: the first entry whose name ends in package.info.json, wherever it sits.
            else if (e.FullName.EndsWith("package.info.json", StringComparison.OrdinalIgnoreCase) && manifestEntry == null)
            {
                manifestEntry = e.FullName.Replace('\\', '/');
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
                                SdkOf(sdk, nuspecSdk), declaration, layout, manifestEntry);
    }

    // ---- the SDK the package needs, read exactly as the store reads it (backend PackageSdk, SemVers)

    private static readonly System.Text.RegularExpressions.Regex Core = new(
        @"^([0-9]+)(?:\.([0-9]+))?(?:\.([0-9]+))?(?:\.([0-9]+))?(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$");

    /**
     * PackageSdk.Reading.version(): both sides cleaned, then package.info.json's unless the nuspec's
     * ranks higher. A release outranks its own prerelease, so 3.0.15-dev.3 beside 3.0.15 reads as 3.0.15.
     */
    internal static string? SdkOf(string? manifest, string? nuspec)
    {
        string? a = CleanSdk(manifest), b = CleanSdk(nuspec);
        if (a == null) return b;
        if (b == null) return a;
        return CompareVersions(a, b) >= 0 ? a : b;
    }

    /** PackageSdk.clean: the lower bound, spelled the NuGet way; null when that is no version or longer than 64. */
    internal static string? CleanSdk(string? raw)
    {
        string? low = LowerBound(raw);
        if (low == null) return null;
        string v = Normalize(low);
        return Core.IsMatch(v) && v.Length <= 64 ? v : null;
    }

    /** NuspecReader.lowerBound: `[3.0.15]`, `3.0.15`, `[3.0.15, )` → 3.0.15; null when there is no lower bound. */
    private static string? LowerBound(string? range)
    {
        if (range == null) return null;
        string s = range.Trim();
        if (s.StartsWith('[') || s.StartsWith('(')) s = s[1..];
        int comma = s.IndexOf(',');
        string low = (comma >= 0 ? s[..comma] : s).Replace("]", "").Replace(")", "").Trim();
        int plus = low.IndexOf('+');
        if (plus >= 0) low = low[..plus];
        return low.Length == 0 || !char.IsDigit(low[0]) ? null : low;
    }

    /** SemVers.normalize: three numeric parts, no leading zeros, a fourth only when it is not zero. */
    private static string Normalize(string v)
    {
        string t = v.Trim();
        var m = Core.Match(t);
        if (!m.Success) return t;
        static string Digits(System.Text.RegularExpressions.Group g)
        {
            if (!g.Success) return "0";
            string s = g.Value.TrimStart('0');
            return s.Length == 0 ? "0" : s;
        }
        var sb = new System.Text.StringBuilder(Digits(m.Groups[1])).Append('.').Append(Digits(m.Groups[2])).Append('.').Append(Digits(m.Groups[3]));
        if (m.Groups[4].Success && Digits(m.Groups[4]) != "0") sb.Append('.').Append(Digits(m.Groups[4]));
        return sb.Append(m.Groups[5].Value).Append(m.Groups[6].Value).ToString();
    }

    /** SemVers.compare: numeric parts as numbers of any length, then a release above its prereleases, then the labels. */
    internal static int CompareVersions(string a, string b)
    {
        static (string Core, string Pre) Split(string v)
        {
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v[..plus];
            int dash = v.IndexOf('-');
            return dash >= 0 ? (v[..dash], v[(dash + 1)..]) : (v, "");
        }
        var (ac, ap) = Split(a);
        var (bc, bp) = Split(b);
        string[] pa = ac.Split('.'), pb = bc.Split('.');
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int c = CompareDigits(i < pa.Length ? pa[i] : "0", i < pb.Length ? pb[i] : "0");
            if (c != 0) return c;
        }
        if ((ap.Length > 0) != (bp.Length > 0)) return ap.Length > 0 ? -1 : 1;
        if (ap.Length == 0) return 0;
        string[] ai = ap.Split('.'), bi = bp.Split('.');
        for (int i = 0; i < Math.Max(ai.Length, bi.Length); i++)
        {
            if (i >= ai.Length) return -1;
            if (i >= bi.Length) return 1;
            bool xn = IsNumeric(ai[i]), yn = IsNumeric(bi[i]);
            int c = xn && yn ? CompareDigits(ai[i], bi[i])
                  : xn ? -1
                  : yn ? 1
                  : Math.Sign(string.Compare(ai[i], bi[i], StringComparison.OrdinalIgnoreCase));
            if (c != 0) return c;
        }
        return 0;
    }

    private static int CompareDigits(string x, string y)
    {
        string a = IsNumeric(x) ? x.TrimStart('0') : "", b = IsNumeric(y) ? y.TrimStart('0') : "";
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : Math.Sign(string.CompareOrdinal(a, b));
    }

    private static bool IsNumeric(string s) => s.Length > 0 && s.All(c => c is >= '0' and <= '9');

    /** The two things the manifest is read for, in one pass over it. */
    private static (string? Sdk, ScheduleADeclaration? Declaration) ManifestOf(ZipArchiveEntry e)
    {
        try
        {
            using var s = e.Open();
            using var doc = JsonDocument.Parse(s);
            var root = doc.RootElement;
            // Jackson's asText: a number reads as its text; null, missing or blank as nothing.
            string? sdk = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sdkVersion", out var v)
                ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
                : null;
            return (string.IsNullOrWhiteSpace(sdk) ? null : sdk, ScheduleA.Read(root));
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
            // Said of the package, not of the template's layout: connect_to_store lists it for a csproj of its own.
            found.Add(new Finding(false, "no screenshots — the card gets no cover picture (images packed under a screenshots/ "
                                       + "folder, such as build/screenshots/, become one)."));
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
