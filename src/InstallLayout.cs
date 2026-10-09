using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace EncyExtensionMcp;

/** One package entry and the path it lands at in the extension's folder ('/'-separated, relative). */
public sealed record InstalledFile(string Entry, string Installed);

/**
 * What a package looks like once installed, compared with the Release build it came from. Failures
 * are what would leave the extension broken; notes are worth reading and break nothing. AtBuildPath
 * counts the files that land where the build has them; NamedByNuspec are the root files the nuspec
 * itself names (readme, icon, licence), which no build output holds.
 */
public sealed record LayoutVerdict(IReadOnlyList<string> Failures, IReadOnlyList<string> Notes,
                                   int Payload, int AtBuildPath, string? Manifest, string? Module,
                                   IReadOnlyList<string> NamedByNuspec);

/// <summary>
/// Where ENCY's store installer puts each file of a package. A package is not unpacked the NuGet way:
/// build/, content/ and lib/&lt;tfm&gt;/ are merged into one flat folder and every other folder is
/// kept as it is — contentFiles/ included. The manifest's <c>module_path</c> is
/// <c>${extensionJsonFolder}/&lt;Name&gt;.dll</c>, resolved against the folder the manifest lands in,
/// so a manifest packed under contentFiles/any/any/ looks for its dll there: the extension installs as
/// files and never registers. A package made with <c>PackagePath="contentFiles\any\any\"</c> did
/// exactly that in ENCY 3.1.6 (08.10.2026), after passing every check the store and this tool had.
/// </summary>
public static class InstallLayout
{
    /** The installer's own records, which a package does not get to write (InstalledExtensionsStore.RecordFileName). */
    private const string RecordFileName = ".store.json";
    private const string PendingDeleteMarker = ".pending-delete";
    private const string ModuleToken = "${extensionJsonFolder}";

    /** Why a broken layout breaks, said once for every answer that reports one. */
    public const string Rule = "ENCY's store installer strips build/, content/ and lib/<tfm>/ and keeps any other folder "
                             + "as it is, contentFiles/ included, so a runtime file belongs under build/ (PackagePath=\"build\\\"). "
                             + "NuGet adds the folder an item's wildcard matched after the PackagePath by itself (for <None Update=...>, "
                             + "the file's folder in the project), so the PackagePath names only what comes before it.";

    // ---- copied from the installer: CAMStartPage.ExtensionStore/Services/NupkgInstaller.cs,
    //      PayloadRelativePath, IsOurBookkeeping and IsPackagingMetadata (ENC3 5f83953d).
    //      Keep it a copy: the point is to answer exactly as the installer does.

    /// <summary>Maps a package entry onto the flat installed folder; null when it is not payload.</summary>
    public static string? PayloadRelativePath(string full)
    {
        if (IsPackagingMetadata(full) || IsOurBookkeeping(full)) return null;
        if (full.StartsWith("content/", StringComparison.OrdinalIgnoreCase))
            return full["content/".Length..];
        if (full.StartsWith("build/", StringComparison.OrdinalIgnoreCase))
        {
            string rel = full["build/".Length..];
            // NuGet build plumbing, not extension payload
            return rel.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ? null : rel;
        }
        if (full.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
        {
            int slash = full.IndexOf('/', "lib/".Length); // drop the tfm segment (lib/net8.0/…)
            return slash < 0 ? null : full[(slash + 1)..];
        }
        return full; // .dext / hand-rolled zips: files at the root, keep their structure
    }

    private static bool IsOurBookkeeping(string full)
    {
        string name = full[(full.LastIndexOf('/') + 1)..];
        return name.Equals(RecordFileName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(PendingDeleteMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPackagingMetadata(string full) =>
        full.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
        || full.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase)
        || full.StartsWith("package/", StringComparison.OrdinalIgnoreCase)
        || full.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase);

    // ---- the rest walks the archive the way the installer does (PayloadTargets)

    /**
     * Every payload entry with the path it lands at, in archive order. Like the installer: names are
     * read with '\' as '/', directory entries are skipped, and a path that would leave the folder is
     * dropped (its zip-slip guard).
     */
    public static List<InstalledFile> Place(IEnumerable<string> entryNames)
    {
        var placed = new List<InstalledFile>();
        foreach (var raw in entryNames)
        {
            string full = raw.Replace('\\', '/');
            if (full.Length == 0 || full.EndsWith('/')) continue;
            string? rel = PayloadRelativePath(full);
            if (string.IsNullOrEmpty(rel)) continue;
            if (Reduce(rel) is { } installed) placed.Add(new InstalledFile(full, installed));
        }
        return placed;
    }

    /**
     * The manifest the installer registers: the first *.settings.json of a recursive search of the
     * folder, which on .NET walks breadth-first — the shallowest one wins, so a stray copy deeper down
     * is harmless and a lone one deeper down is what breaks.
     */
    public static InstalledFile? RegisteredManifest(IEnumerable<InstalledFile> placed) =>
        placed.Where(p => p.Installed.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase))
              .OrderBy(p => p.Installed.Count(c => c == '/'))
              .ThenBy(p => p.Installed, StringComparer.OrdinalIgnoreCase)
              .FirstOrDefault();

    /**
     * Where a module_path points once its manifest is installed — the native manager replaces
     * ${extensionJsonFolder} with the manifest's folder and reduces '..'. Null when the path does not
     * start with that token: then it is not relative to the package, and nothing here can tell.
     */
    public static string? ModuleTarget(string manifestInstalled, string? modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath)) return null;
        string mp = modulePath.Trim();
        if (!mp.StartsWith(ModuleToken, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = mp[ModuleToken.Length..].Replace('\\', '/').TrimStart('/');
        int slash = manifestInstalled.LastIndexOf('/');
        string dir = slash < 0 ? "" : manifestInstalled[..slash];
        return Reduce(dir.Length == 0 ? rest : dir + "/" + rest);
    }

    /**
     * Why the installed manifest would not find its dll, as a fact naming where each lands; null when
     * it would, or when that cannot be told (no manifest in the package — a finding of its own — or a
     * module_path that does not start with ${extensionJsonFolder}).
     */
    public static string? ManifestProblem(IReadOnlyList<InstalledFile> placed, IEnumerable<string> entryNames,
                                          Func<string, string?> readEntry)
    {
        var manifest = RegisteredManifest(placed);
        if (manifest == null)
        {
            string? dropped = entryNames.Select(n => n.Replace('\\', '/'))
                .FirstOrDefault(n => n.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase));
            return dropped == null ? null
                : $"the manifest {dropped} is not unpacked by ENCY's store installer, so no manifest reaches the "
                  + "extension's folder and ENCY would register nothing.";
        }

        string? target = ModuleTarget(manifest.Installed, ModulePath(readEntry(manifest.Entry)));
        if (target == null || placed.Any(p => p.Installed.Equals(target, StringComparison.OrdinalIgnoreCase)))
            return null;

        string dllName = FileName(target);
        var dll = placed.FirstOrDefault(p => FileName(p.Installed).Equals(dllName, StringComparison.OrdinalIgnoreCase));
        return "the manifest would not find its dll after installation — the manifest lands at " + Where(manifest)
             + $" and its module_path points at {target}, "
             + (dll != null ? "while the dll lands at " + Where(dll) : $"while the package installs no {dllName} at all")
             + ", so ENCY would not register the extension.";
    }

    /** The module_path a manifest names; null when it is not JSON or names none. */
    public static string? ModulePath(string? manifestJson)
    {
        if (manifestJson == null) return null;
        try { return Preflight.Str(JsonDocument.Parse(manifestJson).RootElement, "module_path"); }
        catch (JsonException) { return null; }
    }

    /**
     * The package laid out the way the installer lays it out, against the Release build it came from:
     * the manifest has to find its dll, every file has to land where the build put it, nothing may land
     * under contentFiles/, and no two entries may land on one path. A file the build does not have
     * (the readme and icon the nuspec names, say) is not a failure: it is not a runtime file.
     */
    public static LayoutVerdict Verify(string nupkgPath, string buildDir)
    {
        using var archive = ZipFile.OpenRead(nupkgPath);
        var entries = archive.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var e in entries) byName.TryAdd(e.FullName.Replace('\\', '/'), e);
        var names = entries.Select(e => e.FullName).ToList();
        var placed = Place(names);
        var failures = new List<string>();
        var notes = new List<string>();

        foreach (var g in placed.GroupBy(p => p.Installed, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            failures.Add($"{g.Count()} entries land at {g.Key} ({string.Join(", ", g.Select(p => p.Entry))}) — one overwrites the other.");

        var manifest = RegisteredManifest(placed);
        string? module = null;
        if (manifest == null)
            failures.Add(ManifestProblem(placed, names, _ => null)
                         ?? "no *.settings.json lands in the extension's folder — ENCY registers nothing without the manifest.");
        else
        {
            module = ModuleTarget(manifest.Installed, ModulePath(Read(byName, manifest.Entry)));
            if (ManifestProblem(placed, names, n => Read(byName, n)) is { } problem) failures.Add(problem);
        }

        var build = new BuildOutput(buildDir);
        var meta = NuspecFiles(archive);
        var named = new List<string>();
        int atBuild = 0;
        foreach (var p in placed)
        {
            var entry = byName[p.Entry];
            byte[] hash;
            using (var s = entry.Open()) hash = SHA256.HashData(s);
            bool contentFiles = p.Installed.StartsWith("contentFiles/", StringComparison.OrdinalIgnoreCase);
            bool exists = build.Has(p.Installed);
            bool same = exists && build.Same(p.Installed, entry.Length, hash);
            if (same && !contentFiles) { atBuild++; continue; }
            // The icon, readme or licence the nuspec names sits at the root because NuGet wants it there,
            // whatever copy of the same bytes the build keeps elsewhere (an icon copied into Assets/, say).
            if (!contentFiles && p.Installed.IndexOf('/') < 0 && meta.Contains(p.Installed)) { named.Add(p.Installed); continue; }

            string? elsewhere = same ? null : build.Find(entry.Length, hash, FileName(p.Installed));
            if (contentFiles)
                failures.Add($"{p.Entry} lands at {p.Installed} — the installer keeps contentFiles/ as a folder of its own"
                             + (elsewhere != null ? $"; the Release build has it at {elsewhere}" : "") + ".");
            else if (elsewhere != null)
                failures.Add($"{p.Entry} lands at {p.Installed}, but the Release build has it at {elsewhere}." + Doubled(p.Installed, elsewhere));
            else if (exists)
                notes.Add($"{p.Entry} lands at {p.Installed}, where the Release build has a different file — does the build change it?");
            else
                notes.Add($"{p.Entry} lands at {p.Installed}; the Release build has no such file.");
        }

        // What the build has and the package does not carry: the installer puts nothing else in the folder.
        var installed = new HashSet<string>(placed.Select(p => p.Installed), StringComparer.OrdinalIgnoreCase);
        var leftOut = build.Paths.Where(k => k.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !installed.Contains(k))
                                 .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        if (leftOut.Count > 0)
            notes.Add($"the Release build also has {Some(leftOut)}, which the package does not carry — ENCY installs only what "
                      + "is in the package, so if the extension loads them at run time, pack them too.");
        foreach (var dep in NuspecDependencies(archive).Where(d => !d.Equals(ProjectLayout.SdkPackageId, StringComparison.OrdinalIgnoreCase)))
            notes.Add($"the package depends on {dep} — ENCY's store installer does not install dependencies, so the "
                      + "extension has to carry what it needs.");

        return new LayoutVerdict(failures, notes, placed.Count, atBuild, manifest?.Installed, module, named);
    }

    /**
     * When a file lands one or more folders deeper than the build has it, and the extra part repeats a
     * folder of the build path — Hints/Hints/a.png against Hints/a.png — the PackagePath named a folder
     * NuGet appends by itself. Said, since that value is otherwise left as it is on every rerun.
     */
    internal static string Doubled(string installed, string atBuild)
    {
        if (!installed.EndsWith("/" + atBuild, StringComparison.OrdinalIgnoreCase)) return "";
        var extra = installed[..^(atBuild.Length + 1)].Split('/');
        var folder = atBuild.Split('/').SkipLast(1).ToArray();
        // The extra part ends with what the build path begins with: Hints + Hints/a.png, Data + Data/Hints/a.png.
        for (int k = Math.Min(extra.Length, folder.Length); k >= 1; k--)
            if (extra.Skip(extra.Length - k).SequenceEqual(folder.Take(k), StringComparer.OrdinalIgnoreCase))
                return $" {string.Join('/', folder.Take(k))}/ is there twice: NuGet adds the item's own folder after its PackagePath "
                     + "by itself (for <None Update=...>, the file's folder in the project), so the PackagePath has to stop above "
                     + "it - plain build\\ for a file in the project's own folders.";
        return "";
    }

    /** "a.dll, b.dll and 3 more" — a list that stays one line. */
    internal static string Some(IReadOnlyList<string> items, int max = 4) =>
        items.Count <= max ? string.Join(", ", items) : string.Join(", ", items.Take(max)) + $" and {items.Count - max} more";

    private static string Where(InstalledFile f) =>
        f.Entry.Equals(f.Installed, StringComparison.Ordinal) ? $"{f.Installed} (kept as it is)" : $"{f.Installed} (from {f.Entry})";

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    /**
     * A relative path with '.' and '..' resolved, as Path.GetFullPath resolves it under the
     * extension's folder; null when it would leave the folder, is rooted, or is empty.
     */
    internal static string? Reduce(string rel)
    {
        if (rel.StartsWith('/') || rel.Contains(':')) return null;
        var stack = new List<string>();
        foreach (var seg in rel.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (stack.Count == 0) return null;
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }
        return stack.Count == 0 ? null : string.Join('/', stack);
    }

    private static string? Read(Dictionary<string, ZipArchiveEntry> byName, string name)
    {
        if (!byName.TryGetValue(name, out var e)) return null;
        try
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        }
        catch (Exception) { return null; }
    }

    /** The files of a build output by their '/'-separated relative paths, each hashed at most once. */
    private sealed class BuildOutput
    {
        private readonly Dictionary<string, FileInfo> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> hashes = new(StringComparer.OrdinalIgnoreCase);

        public BuildOutput(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                files[Path.GetRelativePath(dir, f).Replace('\\', '/')] = new FileInfo(f);
        }

        public IEnumerable<string> Paths => files.Keys;

        public bool Has(string rel) => files.ContainsKey(rel);

        public bool Same(string rel, long length, byte[] hash) =>
            files.TryGetValue(rel, out var f) && f.Length == length && HashOf(rel, f).AsSpan().SequenceEqual(hash);

        /** Where in the build the same bytes sit — a file of the same name first, then the shallowest. */
        public string? Find(long length, byte[] hash, string name) =>
            files.Where(kv => kv.Value.Length == length && HashOf(kv.Key, kv.Value).AsSpan().SequenceEqual(hash))
                 .Select(kv => kv.Key)
                 .OrderBy(k => FileName(k).Equals(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                 .ThenBy(k => k.Count(c => c == '/'))
                 .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                 .FirstOrDefault();

        private byte[] HashOf(string rel, FileInfo f)
        {
            if (hashes.TryGetValue(rel, out var h)) return h;
            using var s = f.OpenRead();
            return hashes[rel] = SHA256.HashData(s);
        }
    }

    /** The files the nuspec itself names — icon, readme, licence — which sit at the root on purpose. */
    private static HashSet<string> NuspecFiles(ZipArchive archive)
    {
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Nuspec(archive) is not { } doc) return named;
        foreach (var x in doc.Descendants())
        {
            string local = x.Name.LocalName;
            bool file = local is "icon" or "readme"
                        || (local == "license" && string.Equals((string?)x.Attribute("type"), "file", StringComparison.OrdinalIgnoreCase));
            if (file && x.Value.Trim() is { Length: > 0 } v) named.Add(v.Replace('\\', '/').TrimStart('/'));
        }
        return named;
    }

    private static IEnumerable<string> NuspecDependencies(ZipArchive archive) =>
        Nuspec(archive)?.Descendants().Where(x => x.Name.LocalName == "dependency")
            .Select(x => (string?)x.Attribute("id")).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();

    private static XDocument? Nuspec(ZipArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (entry == null) return null;
        try
        {
            using var s = entry.Open();
            return XDocument.Load(s);
        }
        catch (Exception) { return null; }
    }
}
