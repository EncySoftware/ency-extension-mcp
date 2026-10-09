using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EncyExtensionMcp;

/** One PackagePath changed: the line of its item in the csproj, the item, and the value before and after. */
public sealed record PackagePathEdit(int Line, string Item, string Before, string After);

/** The csproj text with the edits made, the edits, and the items a text edit cannot put right. */
public sealed record PackagePathPlan(string Text, IReadOnlyList<PackagePathEdit> Edits, IReadOnlyList<string> Problems);

/// <summary>
/// Moves a project's runtime files out of contentFiles/ in its package. ENCY's store installer keeps
/// contentFiles/ as a folder of its own (see InstallLayout), so every file a csproj packs there has to
/// land under build/ instead, in the same folder it has in the Release build output — the folder the
/// extension reads it from when it runs.
///
/// <para>Where NuGet puts a packed item: the PackagePath, then the folder its wildcard matched
/// (RecursiveDir), then the file name. So the new value is <c>build\</c> plus the item's folder in the
/// build output WITHOUT the part NuGet appends by itself. That part is what doubled
/// <c>SmartHintImages\SmartHintImages\</c> in a real package (08.10.2026): an Update of the SDK's own
/// <c>**/*</c> item already carries its folder, so the right value for it is plain <c>build\</c>.</para>
///
/// <para>A text edit, never a rewrite: only the value between the quotes changes, so the byte order
/// mark, the line endings and everything else in the author's file stay as they were. An item whose
/// placement cannot be read off the text (MSBuild properties, a Link that renames, files of one item
/// in several folders) is reported, not guessed at.</para>
/// </summary>
public static class PackagePathFix
{
    private static readonly char[] Wildcards = { '*', '?' };

    public static PackagePathPlan Plan(string text, string projectDir)
    {
        var doc = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        var lineStarts = LineStarts(text);
        var splices = new List<(int Start, int Length, string Value)>();
        var edits = new List<PackagePathEdit>();
        var problems = new List<string>();
        bool defaultNoneOff = IsFalse(Property(doc, "EnableDefaultItems")) || IsFalse(Property(doc, "EnableDefaultNoneItems"));
        // NuGet's default for an item packed without a PackagePath: both content/ and contentFiles/.
        string targets = Property(doc, "ContentTargetFolders") ?? "content;contentFiles";
        bool defaultHasContentFiles = Split(targets).Any(IsContentFiles);
        var items = doc.Root!.Descendants().Where(e => e.Parent?.Name.LocalName.Equals("ItemGroup", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var specsWithPath = new HashSet<string>(items.Where(e => PackagePathOf(e).Value != null)
            .Select(e => Attr(e, "Include") ?? Attr(e, "Update") ?? ""), StringComparer.OrdinalIgnoreCase);

        foreach (var el in items)
        {
            string kind = el.Name.LocalName;
            string? include = Attr(el, "Include"), update = Attr(el, "Update");
            if (include == null && update == null) continue;   // a Remove
            string item = include ?? update!;
            int line = ((IXmlLineInfo)el).LineNumber;
            string where = $"line {line}, {kind} {item}";
            var (value, holder, misspelt) = PackagePathOf(el);

            if (misspelt is { } wrong && Split(wrong.Value).Any(IsContentFiles))
                problems.Add($"{where}: the metadata is spelled `{wrong.Name}`, and NuGet reads only `PackagePath` - rename it, "
                             + "then call connect_to_store again.");

            if (value == null)
            {
                // Packed with no PackagePath at all: NuGet's default puts it under content/ AND contentFiles/.
                bool packed = IsTrue(Meta(el, "Pack")) || (kind.Equals("Content", StringComparison.OrdinalIgnoreCase) && !IsFalse(Meta(el, "Pack")));
                if (packed && defaultHasContentFiles && misspelt == null && !specsWithPath.Contains(item) && IsPayload(kind))
                {
                    string? t = Target(el, projectDir, defaultNoneOff, out _);
                    problems.Add($"{where}: packed without a PackagePath, so NuGet puts it under content/ and contentFiles/ both"
                                 + (t != null ? $" - give it PackagePath=\"build\\{t}\"" : " - give it a PackagePath under build\\") + ".");
                }
                continue;
            }

            var parts = Split(value);
            if (!parts.Any(IsContentFiles) || IsFalse(Meta(el, "Pack"))) continue;
            if (!IsPayload(kind))
            {
                problems.Add($"{where}: a {kind} item is not a file the build copies next to the dll, so where it belongs cannot "
                             + "be read off the build - set its PackagePath under build\\ by hand, or stop packing it.");
                continue;
            }
            string? target = Target(el, projectDir, defaultNoneOff, out string why);
            if (target == null)
            {
                problems.Add($"{where}: {why}.");
                continue;
            }

            char sep = value.Contains('\\') || !value.Contains('/') ? '\\' : '/';
            string computed = "build" + sep + target.Replace('\\', sep);
            var kept = parts.Where(p => !IsContentFiles(p)).ToList();
            if (!kept.Any(p => SameFolder(p, computed))) kept.Add(computed);
            string after = string.Join(";", kept);
            if (after.IndexOfAny(new[] { '"', '\'', '&', '<', '>', '%', '$', '@' }) >= 0)
            {
                problems.Add($"{where}: the folder it needs ({after}) holds characters MSBuild or XML treat specially - set it by hand.");
                continue;
            }
            if (Locate(text, lineStarts, el, holder!) is not { } span)
            {
                problems.Add($"{where}: its PackagePath could not be found in the text as written (an entity or a line break in it?) - set it to {after} by hand.");
                continue;
            }
            splices.Add((span.Start, span.Length, after));
            edits.Add(new PackagePathEdit(line, item, value, after));
        }

        var sb = new StringBuilder(text);
        foreach (var (start, length, v) in splices.OrderByDescending(s => s.Start))
            sb.Remove(start, length).Insert(start, v);
        return new PackagePathPlan(sb.ToString(), edits, problems);
    }

    /**
     * Where the item's files go in the package, after "build\": "" for the root, "Dir\" for a folder,
     * or "Dir\name.ext" for one file under a name of its own. Null — with the reason — when that cannot
     * be read off the text.
     */
    private static string? Target(XElement el, string projectDir, bool defaultNoneOff, out string why)
    {
        string kind = el.Name.LocalName;
        string? include = Attr(el, "Include");
        string spec = include ?? Attr(el, "Update")!;
        bool isUpdate = include == null;
        string? link = Meta(el, "Link"), linkBase = Meta(el, "LinkBase");
        why = "";
        if (NonLiteral(spec))
        {
            why = $"`{spec}` uses MSBuild properties or metadata, so where its files land cannot be read off the text - set PackagePath by hand";
            return null;
        }
        var patterns = Split(spec);
        if (patterns.Count == 0) { why = "the item names no files"; return null; }
        if (link != null && patterns.Count > 1) { why = "several files share one Link - split the item"; return null; }

        string? target = null;
        foreach (var pattern in patterns)
        {
            string? t = link != null
                ? LinkTarget(pattern, link, isUpdate, out why)
                : PatternTarget(pattern, isUpdate, kind, linkBase, projectDir, defaultNoneOff, out why);
            if (t == null) return null;
            if (target != null && !target.Equals(t, StringComparison.OrdinalIgnoreCase))
            {
                why = $"its files land in different folders ({Shown(target)} and {Shown(t)}) - split the item, one folder each";
                return null;
            }
            target = t;
        }
        return target;
    }

    private static string Shown(string target) => target.Length == 0 ? "the root" : target;

    /** The folder of a file the item names by itself (no Link). */
    private static string? PatternTarget(string pattern, bool isUpdate, string kind, string? linkBase, string projectDir,
                                         bool defaultNoneOff, out string why)
    {
        why = "";
        var segs = pattern.Replace('/', '\\').Split('\\');
        int wild = Array.FindIndex(segs, s => s.IndexOfAny(Wildcards) >= 0);
        string fixedDir = string.Join('\\', segs.Take(wild >= 0 ? wild : segs.Length - 1));
        bool rooted = pattern.StartsWith('\\') || pattern.StartsWith('/') || Regex.IsMatch(pattern, "^[A-Za-z]:");
        string abs = Path.GetFullPath(rooted ? Os(fixedDir) : Path.Combine(projectDir, Os(fixedDir)));
        bool inside = IsUnder(abs, projectDir);

        if (isUpdate)
        {
            // An Update changes an item that exists already. For None that is the SDK's own `**/*`, whose
            // folder below the project NuGet appends by itself — so the value is the root of build\.
            if (!kind.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                why = $"an Update of a {kind} item changes one included elsewhere, so the folder NuGet appends to it is not known here - set PackagePath by hand";
                return null;
            }
            if (defaultNoneOff)
            {
                why = "the project turns the SDK's default items off, so this Update changes an item included elsewhere and the folder NuGet appends to it is not known here - set PackagePath by hand";
                return null;
            }
            if (!inside)
            {
                why = $"`{pattern}` is outside the project folder, where the SDK's default items do not reach - set PackagePath by hand";
                return null;
            }
            return "";
        }

        if (!inside)
        {
            // Outside the project the SDK links the file into the output under LinkBase, if any, plus
            // the folder its wildcard matched — which NuGet appends by itself.
            if (linkBase == null) return "";
            if (NonLiteral(linkBase)) { why = $"its LinkBase `{linkBase}` uses MSBuild properties or metadata - set PackagePath by hand"; return null; }
            return Folder(linkBase);
        }
        string rel = Path.GetRelativePath(projectDir, abs);
        return rel == "." ? "" : Folder(rel);
    }

    /** The folder (or the one file) a Link puts the item's file at in the build output. */
    private static string? LinkTarget(string pattern, string link, bool isUpdate, out string why)
    {
        why = "";
        var m = Regex.Match(link, @"^(?<pre>[^$@%]*)%\(RecursiveDir\)%\(Filename\)%\(Extension\)$", RegexOptions.IgnoreCase);
        if (m.Success) return Folder(m.Groups["pre"].Value);

        var segs = pattern.Replace('/', '\\').Split('\\');
        bool wildDir = segs.Take(segs.Length - 1).Any(s => s.IndexOfAny(Wildcards) >= 0);
        m = Regex.Match(link, @"^(?<pre>[^$@%]*)%\(Filename\)%\(Extension\)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            if (isUpdate || wildDir)
            {
                why = "its Link leaves out the folder NuGet appends to the file - set PackagePath by hand";
                return null;
            }
            return Folder(m.Groups["pre"].Value);
        }
        if (NonLiteral(link)) { why = $"its Link `{link}` uses MSBuild properties or metadata - set PackagePath by hand"; return null; }
        if (pattern.IndexOfAny(Wildcards) >= 0) { why = "several files share one Link - split the item"; return null; }

        // One file under a name of its own: its full path in the package, which NuGet takes as it is
        // when the extension is the file's own.
        string ext = Path.GetExtension(segs[^1]);
        if (ext.Length == 0 || !Path.GetExtension(link).Equals(ext, StringComparison.OrdinalIgnoreCase))
        {
            why = "its Link changes the file's extension - set PackagePath by hand";
            return null;
        }
        return link.Replace('/', '\\').TrimStart('\\');
    }

    // ---- reading the csproj

    /** The PackagePath of an item as written — attribute or child element, and the XML node holding it. */
    private static (string? Value, XObject? Holder, (string Name, string Value)? Misspelt) PackagePathOf(XElement el)
    {
        if (el.Attribute("PackagePath") is { } a) return (a.Value, a, null);
        var child = el.Elements().FirstOrDefault(c => c.Name.LocalName == "PackagePath");
        if (child != null) return (child.Value, child, null);
        // NuGet looks the name up as written (an ordinal compare): `packagepath` is ignored altogether.
        var other = el.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals("PackagePath", StringComparison.OrdinalIgnoreCase));
        if (other != null) return (null, null, (other.Name.LocalName, other.Value));
        var otherChild = el.Elements().FirstOrDefault(c => c.Name.LocalName.Equals("PackagePath", StringComparison.OrdinalIgnoreCase));
        return otherChild != null ? (null, null, (otherChild.Name.LocalName, otherChild.Value)) : (null, null, null);
    }

    private static string? Attr(XElement el, string name) => el.Attribute(name)?.Value;

    /** Item metadata: an attribute, or a child element of that name. */
    private static string? Meta(XElement el, string name) =>
        el.Attribute(name)?.Value ?? el.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value;

    /** A property of the project, the first one written — conditions are not evaluated. */
    private static string? Property(XDocument doc, string name) =>
        doc.Root!.Elements().Where(e => e.Name.LocalName == "PropertyGroup")
            .SelectMany(g => g.Elements()).FirstOrDefault(p => p.Name.LocalName == name)?.Value.Trim();

    private static bool IsTrue(string? v) => string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    private static bool IsFalse(string? v) => string.Equals(v?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /** Only these are copied next to the dll; anything else packed as a file is not the extension's payload. */
    private static bool IsPayload(string kind) =>
        kind.Equals("None", StringComparison.OrdinalIgnoreCase) || kind.Equals("Content", StringComparison.OrdinalIgnoreCase);

    private static bool NonLiteral(string v) =>
        v.Contains("$(") || v.Contains("@(") || v.Contains("%(") || Regex.IsMatch(v, "%[0-9A-Fa-f]{2}");

    private static List<string> Split(string value) =>
        value.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /** contentFiles/ as a whole first segment, in any case and with either slash. */
    public static bool IsContentFiles(string value) =>
        Regex.IsMatch(value.Replace('/', '\\').TrimStart('\\'), @"^contentFiles(\\|$)", RegexOptions.IgnoreCase);

    /** Whether two values put a file in the same installed folder (content\ and build\ both land at the root). */
    private static bool SameFolder(string a, string b)
    {
        static string? Dir(string v)
        {
            string? rel = InstallLayout.PayloadRelativePath(v.Replace('\\', '/').Trim('/') + "/x");
            if (rel == null) return null;
            int slash = rel.LastIndexOf('/');
            return slash < 0 ? "" : rel[..slash];
        }
        return Dir(a) is { } da && Dir(b) is { } db && da.Equals(db, StringComparison.OrdinalIgnoreCase);
    }

    private static string Folder(string path)
    {
        string f = path.Replace('/', '\\').Trim('\\');
        return f.Length == 0 ? "" : f + "\\";
    }

    private static string Os(string path) => path.Replace('\\', Path.DirectorySeparatorChar);

    private static bool IsUnder(string path, string dir)
    {
        string d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return p.Equals(d, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // ---- finding the value in the text, so that nothing else changes

    /** Where each line starts, counted the way the XML reader counts lines (\r\n, \r and \n). */
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n') starts.Add(i + 1);
        }
        return starts;
    }

    private static int Offset(List<int> lineStarts, IXmlLineInfo info) =>
        info.HasLineInfo() && info.LineNumber >= 1 && info.LineNumber <= lineStarts.Count
            ? lineStarts[info.LineNumber - 1] + info.LinePosition - 1 : -1;

    /**
     * The span of the value as written: between the quotes of the attribute, or the trimmed text of the
     * child element. Null when the text there is not what the XML reader read (an entity, say) — then
     * a splice would not be a faithful edit.
     */
    private static (int Start, int Length)? Locate(string text, List<int> lineStarts, XElement el, XObject holder)
    {
        if (holder is XAttribute attr)
        {
            int at = Offset(lineStarts, attr);
            if (at < 0 || !text.AsSpan(at).StartsWith(attr.Name.LocalName, StringComparison.Ordinal)) return null;
            int i = at + attr.Name.LocalName.Length;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '=') return null;
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || (text[i] != '"' && text[i] != '\'')) return null;
            char quote = text[i];
            int start = i + 1, end = text.IndexOf(quote, start);
            if (end < 0 || text[start..end] != attr.Value) return null;
            return (start, end - start);
        }
        if (holder is XElement child)
        {
            int at = Offset(lineStarts, child);
            if (at < 1 || text[at - 1] != '<' || !text.AsSpan(at).StartsWith(child.Name.LocalName, StringComparison.Ordinal)) return null;
            int close = text.IndexOf('>', at);
            if (close < 0 || text[close - 1] == '/') return null;
            int contentStart = close + 1, contentEnd = text.IndexOf('<', contentStart);
            if (contentEnd < 0) return null;
            string raw = text[contentStart..contentEnd];
            string trimmed = raw.Trim();
            if (trimmed != child.Value.Trim() || trimmed.Length == 0) return null;
            return (contentStart + (raw.Length - raw.TrimStart().Length), trimmed.Length);
        }
        return null;
    }
}
