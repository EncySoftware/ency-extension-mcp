using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace EncyExtensionMcp;

/// <summary>
/// Publishing what the author built ON THEIR OWN MACHINE: a ready `.nupkg` or a build-output folder.
/// No git, no GitHub, no second repository holding a copy of the sources.
///
/// <para>Why a route of its own. `publish_extension` tags the repository and waits for GitHub
/// Actions, `publish_folder` creates a repository through the store's app — both need a repository.
/// An author who keeps the sources at home and builds the package there was left uploading the
/// `.nupkg` through the website for every version (asked for on 15.09.2026). The server has taken
/// this route all along — it is how the website publishes; only the client was missing.</para>
///
/// <para>Nothing is built here: building is the author's business and their environment's. What is
/// already on disk is what goes.</para>
/// </summary>
[McpServerToolType]
public class LocalPublishTools
{
    private readonly IStoreClient store;
    private readonly IStoreAuth auth;
    private readonly Action<string> log;
    private readonly IUpdateCheck updates;

    public LocalPublishTools(IStoreClient store, IStoreAuth auth, Action<string> log, IUpdateCheck? updates = null)
    {
        this.store = store;
        this.auth = auth;
        this.log = log;
        this.updates = updates ?? new NoUpdateCheck();
    }

    /** The version note, when there is one, under a result worth reading to the end. */
    private async Task<string> WithUpdateNote(string text)
    {
        string? note = await updates.Note();
        return note == null ? text : text.TrimEnd() + "\n\n" + note;
    }

    /**
     * Give a build-output folder the manifest the store's packer needs. When the output carries a
     * manifest with the marker, nothing happens. Otherwise the project is looked for above the
     * folder; its manifest is written or given the marker, and replaces (or joins) the output's copy
     * in the upload. Returns the reason when it cannot be done.
     */
    private static string? AdoptOutput(string outputDir, List<string> files, StringBuilder sb)
    {
        int at = files.FindIndex(f => Path.GetFileName(f).Equals("package.info.json", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && ProjectLayout.HasMarker(File.ReadAllText(files[at]))) return null;

        var project = ProjectLayout.LocateAbove(outputDir);
        if (project == null)
            return at < 0
                ? "no package.info.json in the build output and no csproj above it — the store cannot make a package without the manifest. "
                  + "Point at the build output of the project (bin/Release/<tfm>), or write src/package.info.json (see the template)."
                : null;   // a manifest without the marker still packs: the store stamps the marker itself

        var notes = new List<string>();
        ProjectLayout.Adopt(project, notes);
        if (notes.Count == 0) return null;
        foreach (var n in notes) sb.AppendLine("- adopted: " + n);
        // The project's manifest is the one that goes up: the output's copy, if any, predates the edit.
        if (at >= 0) files[at] = project.Manifest; else files.Add(project.Manifest);
        return null;
    }

    /** What the store's packer needs in a build folder: the extension's manifest and its assembly. */
    private const string ManifestSuffix = ".settings.json";

    [McpServerTool(Name = "publish_package"), Description(
        "Publish an extension BUILT ON THIS MACHINE straight to the ENCY store — no git, no GitHub, " +
        "no repository. Give it a ready .nupkg, or a build-output folder (the flat one with " +
        "<Name>.dll + <Name>.settings.json + package.info.json, produced by `dotnet build`, not " +
        "`dotnet publish`): a folder is uploaded file by file and the STORE packs it, exactly as it " +
        "does for CI. Publishes under the author's own store account (browser sign-in, once). A new " +
        "name waits for a moderator; a new version of an extension that is already in the catalogue " +
        "appears at once. Use publish_folder instead when the author WANTS the store to keep the " +
        "sources in a GitHub repository.")]
    public async Task<string> PublishPackage(
        [Description("A .nupkg file, or the build-output folder. Default: current directory")] string? path = null,
        [Description("Version to stamp while packing a FOLDER (a ready .nupkg already carries its own). Empty = the one in package.info.json")] string? version = null,
        [Description("Store category id for the card, e.g. operation, analyzer. Empty = keep what the extension already has")] string? category = null)
    {
        string full = Path.GetFullPath(path ?? ".");
        bool isFile = File.Exists(full);
        bool isDir = Directory.Exists(full);
        if (!isFile && !isDir)
            return $"ERROR: there is no file or folder at {full} — give the path to the .nupkg or to the build output.";

        // A folder holding a ready package counts as the package: `dotnet pack` drops the .nupkg
        // next to the build, and there is no point making the author name the file.
        string? nupkg = isFile ? full : NewestNupkg(full);
        if (isFile && !full.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            return $"ERROR: {Path.GetFileName(full)} is not a .nupkg — give the package, or the folder with the build output.";

        // Read locally first: what the store would refuse is said before the sign-in and the upload,
        // and what only a bare card would have shown (no pictures, no readme) is said at all. The SDK
        // is left to the server below — it holds the release map, and one warning is enough.
        IReadOnlyList<Finding> checks = Array.Empty<Finding>();
        if (nupkg != null && PackageCheck.TryRead(nupkg) is { } facts)
        {
            checks = PackageCheck.Findings(facts, await CategoriesOrNone(), recommendedSdk: null);
            if (checks.Any(f => f.Blocking))
                return "ERROR: " + string.Join(" ", checks.Where(f => f.Blocking).Select(f => f.Text));
        }

        var sb = new StringBuilder();
        foreach (var f in checks) sb.AppendLine("- check: " + f.Text);
        string? token = await TokenOrBrowserLogin(sb);
        if (token == null)
            return "ERROR: not signed in to the store. The browser sign-in did not complete — ask the author to "
                 + "finish it in the browser window that opened (or run `ency-extension-mcp login` in a terminal), "
                 + "then call publish_package again.";

        StagedPackage staged;
        try
        {
            if (nupkg != null)
            {
                sb.AppendLine($"- package: {Path.GetFileName(nupkg)} ({new FileInfo(nupkg).Length / 1024} KB)");
                staged = await store.StageNupkg(Path.GetFileName(nupkg), await File.ReadAllBytesAsync(nupkg), token);
            }
            else
            {
                var files = BuildOutput(full);
                string? missing = WhatIsMissing(files);
                if (missing != null) return "ERROR: " + missing;

                // The output of a project not made from the template: no package.info.json in it,
                // or one without the marker. The csproj is a few folders up; the manifest is written
                // next to it (that is where it belongs, and where the next build copies it from) and
                // rides along with this upload.
                string? adoptError = AdoptOutput(full, files, sb);
                if (adoptError != null) return "ERROR: " + adoptError;

                sb.AppendLine($"- build output: {files.Count} files from {full}");
                var ids = new List<string>();
                foreach (var f in files)
                    ids.Add(await store.UploadFile(Path.GetFileName(f), await File.ReadAllBytesAsync(f), token));
                staged = await store.PackFolder(ids, version, token);
                sb.AppendLine("- packed by the store");
            }
        }
        catch (StoreApiException e)
        {
            return $"ERROR: the store refused ({e.Status}): {e.Message}";
        }

        // The marker tag is how the catalogue recognises an extension at all: without it the package
        // publishes and shows up nowhere. Refused here, not after the publish.
        if (!staged.HasMarker)
            return $"ERROR: {staged.PackageId} {staged.Version} carries no `ency-extension` tag, so the catalogue "
                 + "would never pick it up. Add it to the package tags (the ENCY pack tool and our template do it "
                 + "for you) and build again.";

        if (staged.SdkNewerThanAnyRelease && staged.SdkVersion != null)
            sb.AppendLine($"- warning: built against SDK {staged.SdkVersion}, newer than any released ENCY — it will "
                        + "install for nobody until that release ships");

        PublishedCard card;
        try { card = await store.PublishStaged(staged, category, token); }
        catch (StoreApiException e) { return $"ERROR: the store refused to publish ({e.Status}): {e.Message}"; }

        sb.AppendLine($"- published {card.PackageId} {card.LatestVersion ?? staged.Version}");
        sb.AppendLine(card.Approved
            ? $"- in the catalogue: {store.StoreBaseUrl}/extension/{card.Slug}"
            : $"- waiting for a moderator; the card already opens by its link: {store.StoreBaseUrl}/extension/{card.Slug}");
        return await WithUpdateNote(sb.ToString().TrimEnd());
    }

    [McpServerTool(Name = "check_package"), Description(
        "Check a ready .nupkg before publishing, without signing in or uploading anything: the marker " +
        "tag the catalogue needs, the manifest and assembly the store requires, the category tag, " +
        "screenshots, readme, icon, and whether the SDK it was built against has shipped in a released " +
        "ENCY. publish_package runs the same checks and refuses on the ones the store would refuse too. " +
        "For a source folder use check_extension instead.")]
    public async Task<string> CheckPackage(
        [Description("A .nupkg file, or the folder holding it (the newest one is taken). Default: current directory")] string? path = null)
    {
        string full = Path.GetFullPath(path ?? ".");
        string? nupkg = File.Exists(full) ? full : Directory.Exists(full) ? NewestNupkg(full) : null;
        if (nupkg == null)
            return Directory.Exists(full)
                ? $"ERROR: no .nupkg in {full} — build the package first, or use check_extension for a source folder."
                : $"ERROR: there is no file or folder at {full}.";
        if (!nupkg.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            return $"ERROR: {Path.GetFileName(nupkg)} is not a .nupkg.";
        var facts = PackageCheck.TryRead(nupkg);
        if (facts == null) return $"ERROR: {Path.GetFileName(nupkg)} is not a valid package — not a zip archive.";

        var findings = PackageCheck.Findings(facts, await CategoriesOrNone(), await RecommendedSdkOrNull());
        string head = $"{facts.PackageId ?? "?"} {facts.Version ?? "?"} ({Path.GetFileName(nupkg)})";
        if (findings.Count == 0) return head + ": nothing to fix before publishing.";
        return head + " — before publishing:" + Environment.NewLine + string.Join(Environment.NewLine,
            findings.Select(f => (f.Blocking ? "  - STOPS THE PUBLISH: " : "  - ") + f.Text));
    }

    /** The store's categories, or none when it cannot be asked — a local check must not fail on the network. */
    private async Task<IReadOnlyList<StoreCategory>> CategoriesOrNone()
    {
        try { return await store.GetCategories(); }
        catch (Exception e) { log($"could not read the store's categories ({e.Message})"); return Array.Empty<StoreCategory>(); }
    }

    private async Task<string?> RecommendedSdkOrNull()
    {
        try { return await store.GetRecommendedSdk(); }
        catch (Exception e) { log($"could not ask the store which SDK has shipped ({e.Message})"); return null; }
    }

    /** The newest top-level .nupkg in the folder; null when there is none. */
    private static string? NewestNupkg(string dir) =>
        Directory.EnumerateFiles(dir, "*.nupkg", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    /**
     * What goes to the packer: the TOP-LEVEL files of the build folder. Subfolders are left out on
     * purpose — the store's packer takes a flat list, and a `dotnet publish` output with every
     * runtime dll in it is not what it wants (a known trap of its own; see the `folder` input of
     * the GitHub action).
     */
    private static List<string> BuildOutput(string dir)
    {
        var all = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList();
        // Symbols and the compiler's XML doc files ride along with every build and serve nobody at
        // run time; the doc file is told from a real XML resource by the assembly standing next to it.
        var assemblies = all.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all
            .Where(f => !f.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .Where(f => !(f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                          && assemblies.Contains(Path.GetFileNameWithoutExtension(f))))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /** The refusal before the upload: what the store would say, without the round trip. */
    private static string? WhatIsMissing(List<string> files)
    {
        if (files.Count == 0) return "the folder is empty — build the project first (dotnet build -c Release).";
        bool manifest = files.Any(f => f.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase));
        bool dll = files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (!manifest && !dll) return "this folder holds no <Name>.dll and no <Name>.settings.json — point at the build output (bin/Release/<tfm>) or at the .nupkg.";
        if (!manifest) return "no <Name>.settings.json in the folder — the store cannot tell what the extension registers.";
        if (!dll) return "no .dll in the folder — nothing to publish.";
        return null;
    }

    /** The stored token, else a browser sign-in — the same as the tool's other commands. */
    private async Task<string?> TokenOrBrowserLogin(StringBuilder sb)
    {
        string? token = null;
        try { token = await auth.GetAccessToken(); }
        catch (InvalidOperationException e) { log(e.Message); }
        if (token != null) return token;

        log("Signing in to the ENCY store — the browser will open.");
        if (!await auth.LoginBrowser(log)) return null;
        sb.AppendLine("- signed in to the store");
        try { return await auth.GetAccessToken(); }
        catch (InvalidOperationException) { return null; }
    }
}
