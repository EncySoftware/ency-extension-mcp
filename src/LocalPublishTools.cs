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

    public LocalPublishTools(IStoreClient store, IStoreAuth auth, Action<string> log)
    {
        this.store = store;
        this.auth = auth;
        this.log = log;
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

        var sb = new StringBuilder();
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
        return sb.ToString().TrimEnd();
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
    private static List<string> BuildOutput(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

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
