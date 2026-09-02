using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace EncyExtensionMcp;

/// <summary>
/// MCP tools for the "write an ENCY extension in Cursor, never copy a file by hand" flow:
/// create a repo from the template, push a version tag, watch it land in the store.
/// Thin wrappers over `gh` + `git` + the store REST API — the author's own gh login is the auth.
/// </summary>
[McpServerToolType]
public class ExtensionStoreTools(IProcessRunner proc, IStoreClient store, StoreTokenProvider tokens)
{
    private const string TemplateRepo = "EncySoftware/ency-extension-template";
    private const string WorkflowFile = "publish.yml";

    // ---------------------------------------------------------------- create_extension_repo

    [McpServerTool(Name = "create_extension_repo"), Description(
        "Create a new ENCY extension repository from the official template: makes a GitHub repo, " +
        "waits for the template copy, clones it locally, renames the extension, sets the store " +
        "publish secret and pushes. After this the author just writes code and calls publish_extension. " +
        "This is the CONSOLE route and needs `gh` signed in. The route with no console at all — for " +
        "somebody who is not a developer — is the browser: GitHub 'Use this template' on " +
        "EncySoftware/ency-extension-template, then Connect in the store profile, then " +
        "Actions -> publish-to-ency-store -> Run workflow. Prefer that unless scripting is the point.")]
    public async Task<string> CreateExtensionRepo(
        [Description("Extension name in PascalCase, e.g. MyToolpathHelper (also becomes the packageId)")] string name,
        [Description("Directory to clone into (the repo lands in <targetDir>/<name>). Default: current directory")] string? targetDir = null,
        [Description("GitHub org for the repo; empty = the author's personal account")] string? org = null,
        [Description("Store category id for the card, e.g. operation, analyzer, geometry-io. The "
                     + "template ships 'other', which is where an unsorted extension lands")] string? category = null)
    {
        if (!TemplateRenamer.IsValidName(name))
            return $"ERROR: '{name}' is not a valid extension name — PascalCase letters/digits/dots, starting with a letter.";
        var parent = Path.GetFullPath(targetDir ?? ".");
        var cloneDir = Path.Combine(parent, name);
        if (Directory.Exists(cloneDir))
            return $"ERROR: {cloneDir} already exists — pick another name or directory.";

        // whoami: nice error early when gh is not logged in
        var who = await proc.Run("gh", "api user --jq .login");
        if (!who.Ok)
            // Не «иди в консоль», а дорога: у человека без консольной привычки `gh auth login` кончается
            // диалогом, в который улетает следующая вставленная команда (поймано 02.09.2026 на Андрее).
            // Первым — путь, где GitHub на этой машине не нужен вовсе: страница стора берёт папку и
            // делает остальное сама. Форма GitHub — второй, консоль — тем, кому она нужна.
            return "ERROR: gh CLI is not authenticated, so this tool cannot create the repository.\n" +
                   "No console and no GitHub login on this machine are needed to publish — the store does it:\n" +
                   "  1. open https://apps.encycam.com/publish and pick 'A folder with the extension';\n" +
                   "  2. install the store app on GitHub once (a consent page) and name the extension;\n" +
                   "  3. choose the project folder (the one holding <Name>.csproj, package.info.json and <Name>.settings.json) " +
                   "and press 'Upload and publish' — the store creates the repository, commits the folder, runs the build " +
                   "and shows the result on the same page.\n" +
                   "Prefer GitHub by hand? The 'Use this template' form with the name filled in: " +
                   $"https://github.com/new?template_owner=EncySoftware&template_name=ency-extension-template&name={name} " +
                   "(the first push renames everything inside), then https://apps.encycam.com/account -> Connect once, " +
                   "put the code in src/, Actions -> publish-to-ency-store -> Run workflow.\n" +
                   "To use this tool instead: run `gh auth login` in a terminal and ANSWER ITS QUESTIONS there " +
                   "(it waits on a Y/n prompt; anything pasted meanwhile is taken as the answer), then retry.\n" +
                   who.StdErr.Trim();
        var owner = string.IsNullOrWhiteSpace(org) ? who.StdOut.Trim() : org.Trim();
        var full = $"{owner}/{name}";

        var create = await proc.Run("gh", $"repo create {full} --template {TemplateRepo} --private");
        if (!create.Ok)
            return $"ERROR: could not create {full} from the template:\n{create.StdErr.Trim()}";

        // The template copy is asynchronous on GitHub's side — an immediate clone lands empty.
        // Poll until the generated repo has a commit.
        bool ready = false;
        for (int i = 0; i < 15 && !ready; i++)
        {
            await Task.Delay(2000);
            var commits = await proc.Run("gh", $"api repos/{full}/commits?per_page=1 --jq length");
            ready = commits.Ok && commits.StdOut.Trim() == "1";
        }
        if (!ready)
            return $"ERROR: {full} was created but the template copy did not materialize in 30s — clone it manually.";

        var clone = await proc.Run("gh", $"repo clone {full} \"{cloneDir}\"", parent);
        if (!clone.Ok)
            return $"ERROR: created {full} but the clone failed:\n{clone.StdErr.Trim()}";

        int touched = TemplateRenamer.Rename(cloneDir, name);
        // Sorted from the first commit, not from the first moderation queue: the template ships
        // "other", and an author who knows what they are building should not have to fix that later
        // through a web dialog.
        string? categoryNote = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            var wanted = category.Trim().ToLowerInvariant();
            var known = await store.GetCategories();
            if (known.Count > 0 && !known.Any(k => k.Id == wanted))
                return $"ERROR: the store has no category '{wanted}'. Known: "
                       + string.Join(", ", known.Select(k => k.Id)) + ".";
            PackageInfo.SetCategory(cloneDir, wanted);
            categoryNote = $"category: {wanted}";
        }
        await proc.Run("git", "add -A", cloneDir);
        var commit = await proc.Run("git", $"commit -m \"Rename template extension to {name}\"", cloneDir);
        if (!commit.Ok) return $"ERROR: rename commit failed:\n{commit.Output.Trim()}";
        var push = await proc.Run("git", "push", cloneDir);
        if (!push.Ok) return $"ERROR: push of the rename commit failed:\n{push.StdErr.Trim()}";

        string? token = null;
        string? tokenError = null;
        try { token = await tokens.GetAccessToken(); }
        catch (InvalidOperationException e) { tokenError = "! " + e.Message; }
        string authNote = tokenError ?? await BootstrapPublisherAuth(name, full, token);

        return $"""
            Created {full} from the ENCY extension template.

            - local clone: {cloneDir}
            - renamed EncyExtension -> {name} ({touched} files){(categoryNote != null ? ", " + categoryNote : "")} and pushed
            - {authNote}

            Next steps:
            1. Write the extension code in src/ (start at Extension.cs; keep the id in {name}.settings.json in sync with ExtensionFactory).
            2. Fill src/readme.md (store card) and description/author in src/package.info.json.
            3. Call publish_extension (a version is optional — the next patch is used) — it tags, GitHub Actions builds and publishes to the store.
            """;
    }

    /// <summary>
    /// Prepare the repository to publish. Preferred path: claim the package name for it with the
    /// author's own store login, which leaves NO credential in GitHub — every publish, the first one
    /// included, then authenticates with the workflow's own OIDC token. Planting ENCY_STORE_TOKEN is
    /// the fallback for when the store is unreachable or the name belongs to somebody else.
    /// </summary>
    internal async Task<string> BootstrapPublisherAuth(string name, string full, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "! no store login — run `ency-extension-mcp login` once in a terminal, then create the "
                   + $"repo again (or bind {full} to {name} from the store account page)";

        string? claimFailure = await store.ClaimPackage(name, full, token);
        if (claimFailure == null)
            return $"{full} is registered as the trusted publisher of {name} — no repository secret is "
                   + "needed, CI publishes with its GitHub OIDC token";

        var secret = await proc.Run("gh", $"secret set ENCY_STORE_TOKEN --repo {full} --body \"{token}\"");
        return secret.Ok
            ? $"could not claim the name ({claimFailure}) — fell back to the ENCY_STORE_TOKEN secret, "
              + "which covers the first publish"
            : $"! could not claim the name ({claimFailure}) and could not set the fallback secret: "
              + secret.StdErr.Trim();
    }

    // ---------------------------------------------------------------- publish_extension

    [McpServerTool(Name = "publish_extension"), Description(
        "Publish the extension to the ENCY store: tags the repo vX.Y.Z and pushes — GitHub Actions " +
        "builds, packs and publishes. New extensions land hidden until a store moderator approves. " +
        "Requires a clean working tree unless commitAll is true.")]
    public async Task<string> PublishExtension(
        [Description("Version to publish, semver: 1.2.3. Leave empty for the next patch after the "
                     + "latest tag (0.1.0 for a repo that has never been released)")] string? version = null,
        [Description("Extension repo directory. Default: current directory")] string? repoDir = null,
        [Description("true = commit all pending changes as 'Release v<version>' before tagging")] bool commitAll = false,
        [Description("Store category id for the card, e.g. operation, analyzer, geometry-io. Written "
                     + "into src/package.info.json, so it holds for this and every later publish. "
                     + "Leave empty to keep what the repo already says")] string? category = null)
    {
        var dir = Path.GetFullPath(repoDir ?? ".");
        string? chosenNote = null;
        if (string.IsNullOrWhiteSpace(version))
        {
            var tags = await proc.Run("git", "tag --list v[0-9]* --sort=-v:refname", dir);
            if (!tags.Ok) return $"ERROR: not a git repo? {tags.StdErr.Trim()}";
            version = NextVersion.FromTags(tags.StdOut);
            chosenNote = $"no version given — publishing {version} (next after the latest tag)";
        }
        version = version!.TrimStart('v', 'V');
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+([\-.][0-9A-Za-z.\-]+)?$"))
            return $"ERROR: '{version}' is not a semver version (expected like 1.2.3).";
        var tag = $"v{version}";

        var status = await proc.Run("git", "status --porcelain", dir);
        if (!status.Ok) return $"ERROR: not a git repo? {status.StdErr.Trim()}";
        if (!string.IsNullOrWhiteSpace(status.StdOut))
        {
            if (!commitAll)
                return "ERROR: the working tree has uncommitted changes. Commit them (or call again with commitAll=true):\n"
                       + status.StdOut.Trim();
            await proc.Run("git", "add -A", dir);
            var c = await proc.Run("git", $"commit -m \"Release {tag}\"", dir);
            if (!c.Ok) return $"ERROR: commit failed:\n{c.Output.Trim()}";
        }

        // The category is a field in the manifest, not a flag on this call: a tag push carries only a
        // name, and the server packer turns that field into the `category:<id>` tag the store reads
        // as a hint (it fills an empty category and never overrules a person). Written AFTER the
        // clean-tree check on purpose — a refused publish must not leave an edit behind.
        string? categoryNote = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            var wanted = category.Trim().ToLowerInvariant();
            var known = await store.GetCategories();
            if (known.Count > 0 && known.All(k => k.Id != wanted))
                return $"ERROR: the store has no category '{wanted}'. Known: "
                       + string.Join(", ", known.Select(k => k.Id)) + ".";
            try
            {
                if (PackageInfo.SetCategory(dir, wanted))
                {
                    await proc.Run("git", "add src/package.info.json", dir);
                    var cc = await proc.Run("git", $"commit -m \"Set the store category to {wanted}\"", dir);
                    if (!cc.Ok) return $"ERROR: could not commit the category change:\n{cc.Output.Trim()}";
                    categoryNote = $"category set to {wanted} in src/package.info.json (committed)";
                }
                else categoryNote = $"category was already {wanted}";
            }
            catch (FileNotFoundException e) { return $"ERROR: {e.Message}"; }
        }

        var existing = await proc.Run("git", $"rev-parse -q --verify refs/tags/{tag}", dir);
        if (existing.Ok)
            return $"ERROR: tag {tag} already exists — bump the version.";

        var t = await proc.Run("git", $"tag {tag}", dir);
        if (!t.Ok) return $"ERROR: tagging failed:\n{t.StdErr.Trim()}";
        var pushBranch = await proc.Run("git", "push origin HEAD", dir);
        if (!pushBranch.Ok) return $"ERROR: branch push failed:\n{pushBranch.StdErr.Trim()}";
        var pushTag = await proc.Run("git", $"push origin {tag}", dir);
        if (!pushTag.Ok) return $"ERROR: tag push failed:\n{pushTag.StdErr.Trim()}";

        await Task.Delay(4000); // give Actions a moment to register the run
        var run = await LatestRun(dir);

        return $"""
            Pushed {tag} — GitHub Actions is building and publishing.{(categoryNote != null ? " " + categoryNote + "." : "")}
            {(chosenNote != null ? chosenNote + "\n" : "")}
            {(run != null ? $"workflow run: {run.Value.Url} ({run.Value.Status})" : "the workflow run has not registered yet")}

            Call publish_status to follow it to the store card.
            """;
    }

    // ---------------------------------------------------------------- publish_status

    [McpServerTool(Name = "publish_status"), Description(
        "Status of the latest publish: the GitHub Actions run (with the failure log when red) and, " +
        "once published, the store card link and its moderation state.")]
    public async Task<string> PublishStatus(
        [Description("Extension repo directory. Default: current directory")] string? repoDir = null)
    {
        var dir = Path.GetFullPath(repoDir ?? ".");
        var run = await LatestRun(dir);
        if (run == null)
            return "No publish workflow runs found — did you call publish_extension (push a v* tag)?";

        var sb = new StringBuilder();
        var r = run.Value;
        sb.AppendLine($"Workflow: {r.Status}{(r.Conclusion != null ? $" / {r.Conclusion}" : "")} — {r.Url}");

        if (r.Status != "completed")
        {
            sb.AppendLine("Still running — call publish_status again in a minute.");
            return sb.ToString();
        }
        if (r.Conclusion != "success")
        {
            var log = await proc.Run("gh", $"run view {r.Id} --log-failed", dir);
            var tail = string.Join('\n', log.StdOut.Split('\n').TakeLast(25));
            sb.AppendLine();
            sb.AppendLine("The run failed; tail of the failing step:");
            sb.AppendLine("```");
            sb.AppendLine(tail.Trim());
            sb.AppendLine("```");
            return sb.ToString();
        }

        var packageId = PackageInfo.ReadPackageId(dir);
        if (packageId == null)
        {
            sb.AppendLine("Run succeeded. (src/package.info.json not found, so the card was not checked.)");
            return sb.ToString();
        }
        var card = await store.GetCard(packageId);
        sb.AppendLine();
        if (card == null)
        {
            sb.AppendLine($"Run succeeded, but the store has no card for {packageId} yet — try again shortly.");
        }
        else if (!card.Approved)
        {
            sb.AppendLine($"Published {packageId} {card.LatestVersion} — awaiting store moderation.");
            sb.AppendLine($"The card already works by direct link: {card.CardUrl(store.StoreBaseUrl)}");
            sb.AppendLine("It appears in the public catalog after a moderator approves it.");
        }
        else
        {
            sb.AppendLine($"Published and approved: {packageId} {card.LatestVersion}");
            sb.AppendLine($"Card: {card.CardUrl(store.StoreBaseUrl)}{(card.Unlisted ? " (currently unlisted by the owner)" : "")}");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- helpers

    private record struct RunInfo(long Id, string Status, string? Conclusion, string Url);

    private async Task<RunInfo?> LatestRun(string repoDir)
    {
        var res = await proc.Run("gh",
            $"run list --workflow {WorkflowFile} --limit 1 --json databaseId,status,conclusion,url", repoDir);
        if (!res.Ok) return null;
        try
        {
            using var doc = JsonDocument.Parse(res.StdOut);
            if (doc.RootElement.GetArrayLength() == 0) return null;
            var e = doc.RootElement[0];
            return new RunInfo(
                e.GetProperty("databaseId").GetInt64(),
                e.GetProperty("status").GetString() ?? "unknown",
                e.TryGetProperty("conclusion", out var c) ? c.GetString() : null,
                e.GetProperty("url").GetString() ?? "");
        }
        catch (JsonException) { return null; }
    }
}
