using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using ModelContextProtocol.Server;

namespace EncyExtensionMcp;

/// <summary>
/// The route with no git and no gh on the machine: the store creates the repository in the author's
/// GitHub account (through its GitHub App), takes the folder, commits it into src/, runs the GitHub
/// build and reports the result. The person is needed twice, both times in a browser: the store
/// sign-in and, once, the app's consent page. This tool opens both and waits.
///
/// <para>Born from the first attempt of somebody who is not a developer (02.09.2026): the console
/// route stalled on `gh auth login`'s Y/n prompt, which swallowed the next pasted command. Nothing
/// here asks a question in a terminal.</para>
///
/// <para>`create_extension_folder` is the other end of the same idea: the project comes from the
/// template's public zip, renamed on this machine — no GitHub account is needed to START, only to
/// publish, and that one is the store's job.</para>
///
/// <para>Waits are counted in polls, not in wall-clock time, so a test with an instant delay ends.</para>
/// </summary>
[McpServerToolType]
public class FolderPublishTools
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan BuildPollEvery = TimeSpan.FromSeconds(5);
    /** 5 minutes for the consent page, 2 for GitHub to register a fresh repository's workflow, 15 for the build. */
    public const int InstallPolls = 100, ReadyPolls = 40, BuildPolls = 180;
    public const string TemplateZipUrl = "https://github.com/EncySoftware/ency-extension-template/archive/refs/heads/main.zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly IStoreClient store;
    private readonly IStoreAuth auth;
    private readonly Func<string, Task> openBrowser;
    private readonly Func<TimeSpan, Task> delay;
    private readonly Action<string> log;
    private readonly Func<Task<byte[]>> fetchTemplateZip;

    public FolderPublishTools(IStoreClient store, IStoreAuth auth, Func<string, Task> openBrowser,
                              Func<TimeSpan, Task> delay, Action<string> log, Func<Task<byte[]>>? fetchTemplateZip = null)
    {
        this.store = store;
        this.auth = auth;
        this.openBrowser = openBrowser;
        this.delay = delay;
        this.log = log;
        this.fetchTemplateZip = fetchTemplateZip ?? (() => Http.GetByteArrayAsync(TemplateZipUrl));
    }

    public static Task OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Could not open the browser (" + e.Message + ") — open the address by hand: " + url);
        }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- create_extension_folder

    [McpServerTool(Name = "create_extension_folder"), Description(
        "Make a new ENCY extension project on this machine from the official template — no GitHub " +
        "account, no gh, no git: a folder named after the extension with the sample code in src/, the " +
        "rules for the assistant (AGENTS.md, .cursor/rules) and the MCP registration inside. Use it when " +
        "the author starts from nothing. Then write the code in src/ (start at Extension.cs), fill " +
        "src/readme.md and src/package.info.json, and publish_folder(name, thatFolder) publishes it.")]
    public async Task<string> CreateExtensionFolder(
        [Description("Extension name in PascalCase, e.g. MyToolpathHelper — also the store name")] string name,
        [Description("Where to put it: the project lands in <targetDir>/<name>. Default: current directory")] string? targetDir = null,
        [Description("Store category id for the card, e.g. operation, analyzer. Leave empty for 'other'")] string? category = null)
    {
        if (!TemplateRenamer.IsValidName(name))
            return $"ERROR: '{name}' is not a valid extension name — PascalCase letters/digits/dots, starting with a letter.";
        var parent = Path.GetFullPath(targetDir ?? ".");
        var target = Path.Combine(parent, name);
        if (Directory.Exists(target))
            return $"ERROR: {target} already exists — pick another name or directory.";

        string? wantedCategory = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            wantedCategory = category.Trim().ToLowerInvariant();
            var known = await store.GetCategories();
            if (known.Count > 0 && known.All(k => k.Id != wantedCategory))
                return $"ERROR: the store has no category '{wantedCategory}'. Known: " + string.Join(", ", known.Select(k => k.Id)) + ".";
        }

        byte[] zip;
        try { zip = await fetchTemplateZip(); }
        catch (Exception e)
        {
            return $"ERROR: could not download the template ({e.Message}). Is GitHub reachable? The same zip by hand: {TemplateZipUrl}";
        }

        var tmp = Path.Combine(Path.GetTempPath(), "ency-tpl-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var ms = new MemoryStream(zip))
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                archive.ExtractToDirectory(tmp);
            // GitHub wraps the tree in one folder named after the repository and the branch.
            var root = Directory.GetDirectories(tmp).Length == 1 && Directory.GetFiles(tmp).Length == 0
                ? Directory.GetDirectories(tmp)[0] : tmp;
            if (!Directory.Exists(Path.Combine(root, "src")))
                return "ERROR: the downloaded zip has no src/ — that is not the extension template.";
            CopyTree(root, target);   // a copy, not a move: temp and target may sit on different drives
        }
        catch (Exception e)
        {
            return $"ERROR: could not unpack the template: {e.Message}";
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { /* temp only */ }
        }

        int touched = TemplateRenamer.Rename(target, name);
        string categoryNote = "";
        if (wantedCategory != null)
        {
            PackageInfo.SetCategory(target, wantedCategory);
            categoryNote = $", category {wantedCategory}";
        }
        return $"""
            Created {target} from the ENCY extension template ({touched} files renamed to {name}{categoryNote}).

            - write the code in src/ (start at src/Extension.cs; keep the id in src/{name}.settings.json in sync with ExtensionFactory)
            - fill src/readme.md (the store card) and description/author in src/package.info.json
            - AGENTS.md and .cursor/rules/ explain the API and the rules; .mcp.json registers this MCP server for the editor
            - publish with publish_folder("{name}", "{target}") — no git, no gh; the author only signs in and approves the store app in the browser
            """;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    // ---------------------------------------------------------------- publish_folder

    [McpServerTool(Name = "publish_folder"), Description(
        "Publish an ENCY extension from a local folder with NO git and NO gh on this machine — the " +
        "preferred way to publish. The store creates the repository in the author's GitHub account " +
        "(through the store's GitHub App), commits the folder into src/, runs the GitHub build and " +
        "publishes. The author is needed twice, in the browser only: the ENCY store sign-in and, once, " +
        "the app's consent page — this tool opens both and waits; nothing is asked in a terminal. " +
        "Never run `gh auth login` for the author instead of this. The folder is the project: the one " +
        "holding <Name>.csproj, package.info.json and <Name>.settings.json, or the whole project with " +
        "src/ inside. Waits for the build by default and returns the result: the version and the card " +
        "link, or the failing step and its log. The next version is the same call.")]
    public async Task<string> PublishFolder(
        [Description("Extension name in PascalCase, e.g. MyToolpathHelper — the store name and the repository name")] string name,
        [Description("The extension folder. Default: current directory")] string? folder = null,
        [Description("false = return right after the run starts; follow it with publish_folder_status")] bool waitForResult = true)
    {
        if (!TemplateRenamer.IsValidName(name))
            return $"ERROR: '{name}' is not a valid extension name — PascalCase letters/digits/dots, starting with a letter.";
        FolderPlan plan;
        try { plan = FolderPlanner.Plan(Path.GetFullPath(folder ?? ".")); }
        catch (FolderPlanException e) { return "ERROR: " + e.Message; }
        log($"{name}: {plan.Files.Count} files from {plan.Root} ({plan.Bytes / 1024} KB)");

        var sb = new StringBuilder();
        string? token = await TokenOrBrowserLogin(sb);
        if (token == null)
            return "ERROR: not signed in to the store. The browser sign-in did not complete — ask the author to finish "
                 + "it in the browser window that opened (or run `ency-extension-mcp login` in a terminal), then call publish_folder again.";

        AppStatus app;
        try { app = await store.GetAppStatus(token); }
        catch (StoreApiException e) { return $"ERROR: the store refused ({e.Status}): {e.Message}"; }
        if (!app.Configured)
            return "ERROR: this store has no GitHub App, so it cannot create repositories. Use the browser: "
                 + $"{store.StoreBaseUrl}/publish -> 'A repository on GitHub'.";
        if (app.Installations.Count == 0)
        {
            string url = await store.GetAppInstallUrl(token);
            log("The store app needs the author's consent on GitHub, once. Opening " + url);
            log("Tell the author: on that page choose Only select repositories and leave the list empty - the store then sees only the repositories it creates; GitHub adds each new one to the list itself.");
            await openBrowser(url);
            for (int i = 0; i < InstallPolls && app.Installations.Count == 0; i++)
            {
                await delay(PollEvery);
                app = await store.GetAppStatus(token);
            }
            if (app.Installations.Count == 0)
                return "WAITING: the store app is not installed yet. Ask the author to approve it in the browser window "
                     + $"that opened ({url}), then call publish_folder again.";
            sb.AppendLine($"- store app installed on {app.Installations[0]}");
        }

        AppRepo? repo = app.Repos.FirstOrDefault(r => r.PackageId.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (repo == null)
        {
            try { repo = await store.CreateRepository(name, token); }
            catch (StoreApiException e) { return $"ERROR: the store could not create the repository ({e.Status}): {e.Message}"; }
            sb.AppendLine($"- repository {repo.Repository} created from the template");
            log($"repository {repo.Repository} created; waiting for GitHub to prepare it");
        }
        else
        {
            sb.AppendLine($"- repository {repo.Repository} (already there)");
            log($"repository {repo.Repository} already exists");
        }

        // GitHub registers a fresh repository's workflow seconds after creating its files; a run
        // dispatched before that gets 404, and the template's bootstrap commit is still landing.
        RepoState st = await store.GetRepoState(name, token);
        for (int i = 0; i < ReadyPolls && st.Stage != "ready"; i++)
        {
            await delay(PollEvery);
            st = await store.GetRepoState(name, token);
        }
        if (st.Stage != "ready") log("GitHub is slow to prepare the repository; going on anyway.");

        // The previous run's report is the baseline, not news — only a report that changes after
        // this run starts is this run's.
        string? baseline = (await FindBuild(repo.Repository, token)) is { } before && before.Status != "RUNNING"
            ? before.UpdatedAt : null;

        log($"uploading {plan.Files.Count} files");
        SourcesUploaded up;
        try { up = await store.UploadSources(name, plan.Files, token); }
        catch (StoreApiException e) { return $"ERROR: the store did not take the folder ({e.Status}): {e.Message}"; }
        sb.AppendLine($"- {up.Files} files from {plan.Root} committed as {Short(up.CommitSha)} — {up.CommitUrl}");
        foreach (var w in plan.Warnings) sb.AppendLine("- warning: " + w);

        RunStarted run;
        try { run = await store.StartRun(name, token); }
        catch (StoreApiException e)
        {
            return sb + $"ERROR: the files are in the repository, but the run did not start ({e.Status}): {e.Message}";
        }
        sb.AppendLine($"- publish run started: {run.ActionsUrl}");
        log($"committed as {Short(up.CommitSha)}; publish run started, waiting for the build (usually 1-2 minutes)");

        if (!waitForResult)
            return sb + "\nThe build is running on GitHub — call publish_folder_status with the same name to follow it.";

        BuildReport? report = null;
        for (int i = 0; i < BuildPolls; i++)
        {
            await delay(BuildPollEvery);
            var b = await FindBuild(repo.Repository, token);
            if (i % 6 == 5) log($"still building - {(i + 1) * (int)BuildPollEvery.TotalSeconds}s");
            if (b == null || b.UpdatedAt == baseline) continue;
            report = b;
            if (b.Status != "RUNNING") break;
        }
        log(report == null ? "no report from GitHub yet" : $"build {report.Status.ToLowerInvariant()}");
        sb.AppendLine();
        sb.Append(await Describe(name, report, run.ActionsUrl));
        return sb.ToString();
    }

    [McpServerTool(Name = "publish_folder_status"), Description(
        "Result of the latest publish of an extension published with publish_folder: building, " +
        "published (with the version and the card link), or failed (with the failing step and its log).")]
    public async Task<string> PublishFolderStatus(
        [Description("Extension name, as given to publish_folder")] string name)
    {
        string? token;
        try { token = await auth.GetAccessToken(); }
        catch (InvalidOperationException e) { return "ERROR: " + e.Message; }
        if (token == null) return "ERROR: not signed in to the store — call publish_folder, it signs in through the browser.";
        IReadOnlyList<BuildReport> builds;
        try { builds = await store.GetMyBuilds(token); }
        catch (StoreApiException e) { return $"ERROR: the store refused ({e.Status}): {e.Message}"; }
        var b = builds.FirstOrDefault(x => string.Equals(x.PackageId, name, StringComparison.OrdinalIgnoreCase));
        if (b == null) return $"No publish run of {name} is known to the store yet.";
        return await Describe(name, b, b.RunUrl);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<string?> TokenOrBrowserLogin(StringBuilder sb)
    {
        string? token = null;
        try { token = await auth.GetAccessToken(); }
        catch (InvalidOperationException e) { log(e.Message); }
        if (token != null) return token;
        log("The ENCY store sign-in page is opening in the browser — the author signs in there; nothing to type here.");
        if (!await auth.LoginBrowser(log)) return null;
        token = await auth.GetAccessToken();
        if (token != null) sb.AppendLine("- signed in to the store through the browser");
        return token;
    }

    private async Task<BuildReport?> FindBuild(string repository, string token)
    {
        try
        {
            return (await store.GetMyBuilds(token))
                .FirstOrDefault(b => b.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase));
        }
        catch (StoreApiException) { return null; }
    }

    private async Task<string> Describe(string name, BuildReport? b, string? actionsUrl)
    {
        if (b == null)
            return $"GitHub has not reported on the run yet — {actionsUrl}. Call publish_folder_status with the same name in a minute.";
        switch (b.Status)
        {
            case "PUBLISHED":
            {
                var card = await store.GetCard(name);
                var sb = new StringBuilder();
                sb.Append($"Published {name}{(b.Version != null ? " " + b.Version : "")}.");
                if (card == null) sb.Append(" The card is not indexed yet — try the store in a minute.");
                else if (!card.Approved) sb.Append($" It awaits a store moderator; the card already works by direct link: {card.CardUrl(store.StoreBaseUrl)}");
                else sb.Append($" Card: {card.CardUrl(store.StoreBaseUrl)}");
                sb.Append(" The next version is the same publish_folder call.");
                return sb.ToString();
            }
            case "FAILED":
            {
                var sb = new StringBuilder();
                sb.AppendLine($"The run failed{(b.FailedStep != null ? " at " + b.FailedStep : "")}. {b.RunUrl}");
                if (!string.IsNullOrWhiteSpace(b.FailureLog))
                {
                    sb.AppendLine("```");
                    sb.AppendLine(string.Join('\n', b.FailureLog.Split('\n').TakeLast(40)).Trim());
                    sb.AppendLine("```");
                }
                sb.Append("Fix the code and call publish_folder again.");
                return sb.ToString();
            }
            default:
                return $"Still building on GitHub — {b.RunUrl ?? actionsUrl}. Call publish_folder_status with the same name in a minute.";
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
