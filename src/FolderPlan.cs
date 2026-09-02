namespace EncyExtensionMcp;

/**
 * What of a local folder goes into src/ of the repository — the same rules the store page applies
 * in the browser. The project lives either in the folder itself (<Name>.csproj at the top) or in
 * its src/ (the whole downloaded project); build output and editor junk never travel.
 */
public record FolderPlan(IReadOnlyList<SourceFile> Files, string Project, long Bytes, int Skipped,
                         IReadOnlyList<string> Warnings, string Root);

public class FolderPlanException(string message) : Exception(message);

public static class FolderPlanner
{
    public const int MaxFiles = 300;
    public const long MaxBytes = 20L * 1024 * 1024;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", ".github", "testresults" };
    private static readonly HashSet<string> SkipFiles = new(StringComparer.OrdinalIgnoreCase)
        { ".DS_Store", "Thumbs.db", "desktop.ini" };

    public static FolderPlan Plan(string folder)
    {
        if (!Directory.Exists(folder))
            throw new FolderPlanException($"{folder} does not exist.");
        string root = folder;
        bool atTop = Directory.EnumerateFiles(folder, "*.csproj").Any();
        string src = Path.Combine(folder, "src");
        if (!atTop)
        {
            if (Directory.Exists(src) && Directory.EnumerateFiles(src, "*.csproj").Any()) root = src;
            else throw new FolderPlanException(
                "No .csproj found at the top of the folder or in its src/. Pick the extension project folder — "
                + "the one holding <Name>.csproj, package.info.json and <Name>.settings.json.");
        }

        var files = new List<SourceFile>();
        int skipped = 0;
        long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            var parts = rel.Split('/');
            var name = parts[^1];
            if (parts.Take(parts.Length - 1).Any(SkipDirs.Contains) || SkipFiles.Contains(name)
                || name.EndsWith(".user", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }
            var data = File.ReadAllBytes(f);
            bytes += data.Length;
            files.Add(new SourceFile(rel, data));
            if (files.Count > MaxFiles)
                throw new FolderPlanException($"Too many files (over {MaxFiles}) — sources only, no bin/, obj/ or packages.");
            if (bytes > MaxBytes)
                throw new FolderPlanException($"The folder is too big (over {MaxBytes / 1048576} MB) — sources only, no build output.");
        }

        var top = files.Where(x => !x.Path.Contains('/')).Select(x => x.Path).ToList();
        var project = top.First(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var warnings = new List<string>();
        if (!top.Any(p => p.Equals("package.info.json", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("No package.info.json — the card will have no description, author or category.");
        if (!top.Any(p => p.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("No <Name>.settings.json — ENCY cannot register the extension without it; the run will fail while packing.");
        return new FolderPlan(files, project, bytes, skipped, warnings, root);
    }
}
