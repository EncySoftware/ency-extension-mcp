using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using ModelContextProtocol.Server;

namespace EncyExtensionMcp;

/// <summary>
/// An extension that already has a repository of its own and is packed with `dotnet pack` — not made
/// from the template — connected to the store, so that a version tag publishes it.
///
/// <para>Why a tool of its own. Every other route assumed the template: `update_extension` plants the
/// template's workflow (it builds src/) and its PackReady target (it deletes *.xml from the output,
/// and an operation can keep its definition in one), `publish_folder` makes a NEW repository. A real
/// project brought over from nuget.org (08.10.2026) packed its manifest under contentFiles/, which
/// ENCY's store installer keeps as a folder of its own: the store took the package and ENCY never
/// registered it. So this tool changes exactly two things — the PackagePath values that put runtime
/// files where the installer does not look, and a workflow file of its own — and proves both with a
/// real pack before it lets them stay.</para>
///
/// <para>The workflow runs on GitHub Actions only, so it is added only to a repository with a GitHub
/// remote. Anywhere else the csproj fix and the check still happen — every route needs them, and
/// sending such a project to publish_package without them would only send it back here.</para>
///
/// <para>Nothing is written for the author that only the author can say: no package.info.json, no
/// Schedule A answer.</para>
/// </summary>
[McpServerToolType]
public class ConnectTools
{
    /** The version stamped on the check pack: never published, and plainly not a real one. */
    public const string ProbeVersion = "0.0.0-storecheck";
    /** A first restore of the SDK packages on a cold machine takes minutes, not seconds. */
    public const int PackTimeoutSeconds = 900;
    private const string Store = "https://apps.encycam.com";

    private static readonly string NL = Environment.NewLine;
    private static readonly string[] SkippedDirs = { "bin", "obj", "node_modules", "packages" };
    private static readonly Regex SdkReference = new(
        @"<PackageReference\b[^>]*\b(?:Include|Update)\s*=\s*[""']\s*EncySoftware\.CAMAPI\.SDK\.Net\s*[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PacksManifest = new(
        @"<\w+\b(?=[^>]*\b(?:Include|Update)\s*=\s*[""'][^""']*\.settings\.json\s*[""'])(?=[^>]*\b(?:Pack\s*=\s*[""']\s*true|PackagePath\s*=))[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IProcessRunner proc;
    private readonly IStoreClient store;
    private readonly IUpdateCheck updates;
    private readonly Action<string> log;
    private readonly Func<DateOnly> today;

    // Built per call by the MCP SDK from the container (see ServerSetup): everything past the services
    // is optional for that reason. `today` is for the tests — the Schedule A date is a real one.
    public ConnectTools(IProcessRunner proc, IStoreClient store, IUpdateCheck? updates = null, Action<string>? log = null,
                        Func<DateOnly>? today = null)
    {
        this.proc = proc;
        this.store = store;
        this.updates = updates ?? new NoUpdateCheck();
        this.log = log ?? (s => Console.Error.WriteLine(s));
        this.today = today ?? (() => DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [McpServerTool(Name = "connect_to_store"), Description(
        "Connect an EXISTING ENCY extension project to the ENCY store so that its own GitHub repository " +
        "publishes it: the tool for \"publish my existing project to the ENCY store\" when the project was " +
        "NOT made from the store template and is packed with `dotnet pack` (use update_extension only for " +
        "projects made from the template; publish_folder makes a NEW repository). Finds the extension's " +
        "csproj and moves the files it packs where ENCY's store installer does not look (contentFiles/, " +
        "or a folder named twice) to build\\, adds .github/workflows/publish_store.yml when the repository " +
        "is on GitHub (a version tag or Actions -> Run workflow publishes; no secrets; one repository " +
        "publishes one extension), then packs the project for real and lays the package out the way ENCY " +
        "installs it — when that check fails, both changes are taken back. Writes nothing else: no " +
        "package.info.json, no Schedule A answer (ask the author for that). Returns the changes, the " +
        "check, and the one-time steps only the author can do in the browser. Afterwards publish_extension " +
        "pushes the version tag and publish_status follows the run. Needs the .NET SDK the project targets.")]
    public async Task<string> ConnectToStore(
        [Description("The repository root or the project folder. Default: current directory")] string? path = null,
        [Description("The extension's .csproj when the folder holds several projects: absolute, or relative to path")] string? csproj = null)
    {
        // Answered, not thrown: the MCP host replaces an exception's text with "an error occurred". The
        // writes below take back what they did on their own; this catches what fails before them.
        try { return await Connect(path, csproj); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"ERROR: {e.Message}";
        }
    }

    /** Where the workflow would run: the GitHub remote, or why there is none. */
    private sealed record Home(GitRemote? GitHub, string? Missing);

    private async Task<string> Connect(string? path, string? csproj)
    {
        string start = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
        if (File.Exists(start) && start.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(csproj)) csproj = start;
            start = Path.GetDirectoryName(start)!;
        }
        if (!Directory.Exists(start)) return $"ERROR: there is no folder at {start}.";

        // Outside git the folder given stands for the repository: the csproj is still fixed and checked.
        string? git = GitRepository.Root(start);
        string repo = git ?? start;
        var home = HomeOf(git, start);

        string project;
        if (!string.IsNullOrWhiteSpace(csproj))
        {
            string given = Path.GetFullPath(Path.IsPathRooted(csproj) ? csproj : Path.Combine(start, csproj));
            if (!File.Exists(given) && !Path.IsPathRooted(csproj)) given = Path.GetFullPath(Path.Combine(repo, csproj));
            if (!File.Exists(given) || !given.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return $"ERROR: there is no .csproj at {given}.";
            if (!IsUnder(given, repo))
                return $"ERROR: {given} is outside {(git != null ? "the repository " : "")}{repo} - the workflow can build only what the repository holds.";
            project = given;
        }
        else
        {
            var found = FindExtensionProjects(start);
            if (found.Count == 0)
                return $"ERROR: no extension project under {start} - no csproj there references {ProjectLayout.SdkPackageId} "
                     + "or packs a *.settings.json. Point at the project's folder, or name the project with csproj=.";
            if (found.Count > 1)
                return $"ERROR: several extension projects under {start}:" + NL
                     + string.Join(NL, found.Select(f => "  - " + Rel(repo, f.Path) + (f.PacksManifest ? " (packs a *.settings.json)" : "")))
                     + NL + "Call connect_to_store again with csproj=<the one to publish>.";
            project = found[0].Path;
        }
        string relProject = Rel(repo, project);
        string projectDir = Path.GetDirectoryName(project)!;

        // A repository made from the template publishes already; a second workflow would publish twice.
        string workflows = Path.Combine(repo, ".github", "workflows");
        if (StoreWorkflow.OtherStoreWorkflow(repo) is { } publishing)
            return $"ERROR: this repository already publishes to the store through .github/workflows/{publishing}, so it needs "
                 + "no second workflow. A project made from the template publishes with publish_extension, and update_extension "
                 + "brings it in line with the template.";

        byte[] original = File.ReadAllBytes(project);
        if (!TryDecode(original, out string text, out bool bom, out string? decodeError))
            return $"ERROR: {relProject}: {decodeError}";

        var (tfm, tfmError) = TargetFramework(text, project, repo, relProject);
        if (tfm == null) return "ERROR: " + tfmError;
        string? dotnet = StoreWorkflow.DotnetVersion(tfm);
        if (dotnet == null)
            return $"ERROR: {relProject} targets {tfm}, and the workflow builds net8.0, net9.0 or net10.0 projects - the ones "
                 + "ENCY 3 extensions target. Target the framework of the ENCY you build for, then call connect_to_store again.";
        if (home.GitHub != null && !StoreWorkflow.IsSafePath(relProject))
            return $"ERROR: the project's path {relProject} holds a character the workflow's shell would read as its own "
                 + "(a quote, $, ` or \\) - rename the folder, then call connect_to_store again.";

        PackagePathPlan plan;
        try { plan = PackagePathFix.Plan(text, projectDir); }
        catch (XmlException e) { return $"ERROR: {relProject} is not readable as XML ({e.Message})."; }
        if (plan.Problems.Count > 0)
            return $"ERROR: {relProject} packs files where ENCY's store installer does not look, and these need a hand "
                 + "(nothing was changed):" + NL + string.Join(NL, plan.Problems.Select(p => "  - " + p)) + NL
                 + InstallLayout.Rule + " Then call connect_to_store again.";

        string workflowFile = Path.Combine(workflows, StoreWorkflow.FileName);
        string yaml = StoreWorkflow.Render(relProject, dotnet, NewlineOf(workflows));
        bool workflowExisted = File.Exists(workflowFile);
        bool workflowSame = workflowExisted && SameText(File.ReadAllText(workflowFile), yaml);
        // One repository publishes one extension: the store binds the repository to the first package it
        // publishes. A workflow already here that packs something else is that other extension's, and
        // saying "connected" over it would be a claim its first tag disproves (review, 09.10.2026).
        if (home.GitHub != null && workflowExisted && !workflowSame
            && OtherProject(File.ReadAllText(workflowFile), relProject, repo) is { } other)
            return "ERROR: " + other + " Nothing was changed.";

        // ---- write, then prove it; on any failure both go back
        var created = new List<string>();
        bool csprojWritten = false, workflowWritten = false;
        // What was taken back, said as it is — including what could not be.
        string Revert()
        {
            var stuck = new List<string>();
            if (csprojWritten)
                try { WriteAtomically(project, original); }
                catch (Exception e) { stuck.Add($"{relProject} could not be put back ({e.Message}) - `git checkout -- {relProject}` does it"); }
            if (workflowWritten)
                try { File.Delete(workflowFile); }
                catch (Exception e) { stuck.Add($"{StoreWorkflow.RelativePath} could not be removed ({e.Message})"); }
            foreach (var d in created.OrderByDescending(d => d.Length))
                try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { /* an empty folder */ }
            if (stuck.Count > 0) return "NOT everything could be taken back: " + string.Join("; ", stuck) + ".";
            return (csprojWritten, workflowWritten) switch
            {
                (true, true) => "Nothing was changed: the csproj is back as it was and the workflow is removed.",
                (true, false) => "Nothing was changed: the csproj is back as it was.",
                (false, true) => "Nothing was changed: the workflow is removed.",
                _ => "This call changed nothing.",
            };
        }
        try
        {
            if (plan.Edits.Count > 0)
            {
                WriteAtomically(project, Encode(plan.Text, bom));
                csprojWritten = true;
            }
            if (home.GitHub != null && !workflowExisted)
            {
                foreach (var d in new[] { Path.Combine(repo, ".github"), workflows })
                    if (!Directory.Exists(d)) { Directory.CreateDirectory(d); created.Add(d); }
                File.WriteAllText(workflowFile, yaml, new UTF8Encoding(false));
                workflowWritten = true;
            }
        }
        catch (Exception e)
        {
            return $"ERROR: could not write the changes ({e.Message}). " + Revert();
        }

        log($"packing {relProject} the way the workflow will (dotnet pack -c Release) to check where ENCY installs each file; "
            + "a first restore can take a few minutes");
        Check check;
        // Nothing may leave the changes standing unchecked — not even a check that breaks.
        try { check = await Verify(project, projectDir); }
        catch (Exception e) { check = new Check($"the check could not be completed ({e.GetType().Name}: {e.Message})."); }
        if (check.Failure != null)
            return "ERROR: " + check.Failure + NL + Revert();

        // The SDK line the store publishes under; null when it did not answer — then nothing is said.
        string? ceiling = await store.GetRecommendedSdk();
        var outcome = new Outcome(relProject, plan, dotnet, check, workflows, home, workflowExisted, workflowWritten, workflowExisted && workflowSame, ceiling);
        return await WithUpdateNote(Report(outcome));
    }

    /** Where a workflow would run for this repository: its GitHub remote, or a sentence saying why there is none. */
    private static Home HomeOf(string? git, string start)
    {
        if (git == null) return new Home(null, $"{start} is not in a git repository");
        var remotes = GitRepository.Remotes(git);
        if (GitRepository.GitHubRemote(remotes) is { } gitHub) return new Home(gitHub, null);
        if (remotes.Count == 0) return new Home(null, "this repository has no remote");
        return new Home(null, (remotes.Count == 1 ? "its remote" : "its remotes") + " ("
                              + string.Join(", ", remotes.Select(r => $"{r.Name}: {r.Url}"))
                              + (remotes.Count == 1 ? ") is" : ") are") + " not on GitHub");
    }

    /**
     * Why an existing publish_store.yml is not this project's — another csproj, or none named at all;
     * null when it packs this one.
     */
    private static string? OtherProject(string yaml, string relProject, string repo)
    {
        var named = StoreWorkflow.PackedProjects(yaml);
        if (named.Any(n => StoreWorkflow.SameProject(n, relProject))) return null;
        if (named.Count == 0)
            return $"{StoreWorkflow.RelativePath} is already here and names no .csproj, so what it publishes cannot be told. "
                 + $"Have its pack step name the project (dotnet pack \"{relProject}\" ...), or delete the file to have this tool "
                 + "write it, then call connect_to_store again.";
        string first = named[0];
        bool gone = !File.Exists(Path.Combine(repo, first.Replace('/', Path.DirectorySeparatorChar)));
        return $"{StoreWorkflow.RelativePath} already publishes {first}, not {relProject}"
             + (gone ? $" - and there is no {first} in the repository any more (did the project move? then change the path in it, "
                       + "or delete the file and call connect_to_store again)." : ".")
             + " One repository publishes one extension: the store binds the repository to the first extension it publishes"
             + (gone ? "." : $", so {relProject} needs a repository of its own - or change the workflow to pack it, if this "
                             + "repository is to publish it instead.");
    }

    // ---- the answer

    private sealed record Outcome(string RelProject, PackagePathPlan Plan, string Dotnet, Check Check, string Workflows,
                                  Home Home, bool WorkflowExisted, bool WorkflowWritten, bool WorkflowSame, string? Ceiling);

    private string Report(Outcome o)
    {
        var facts = o.Check.Facts;
        var verdict = o.Check.Verdict!;
        string rel = o.RelProject;
        var gitHub = o.Home.GitHub;
        var sb = new StringBuilder();
        string name = facts?.PackageId is { Length: > 0 } id ? id : Path.GetFileNameWithoutExtension(rel);
        sb.AppendLine(gitHub != null
            ? $"Connected {rel} ({name}) to the ENCY Extension Store: the GitHub repository {gitHub.GitHub} publishes it."
            : $"Checked {rel} ({name}) for the ENCY Extension Store - no workflow added: {o.Home.Missing}, and the workflow "
              + "that publishes runs on GitHub Actions only.");

        sb.AppendLine();
        sb.AppendLine("Changes:");
        foreach (var e in o.Plan.Edits)
        {
            sb.AppendLine($"  - {rel}, line {e.Line} ({e.Item}): PackagePath \"{e.Before}\" -> \"{e.After}\"");
            if (e.Unchecked != null) sb.AppendLine("    ! " + e.Unchecked);
        }
        if (o.Plan.Edits.Count == 0)
            sb.AppendLine($"  - {rel}: nothing to change - it packs no runtime file where ENCY's store installer does not look");
        if (gitHub == null)
            sb.AppendLine(o.WorkflowExisted
                ? $"  - {StoreWorkflow.RelativePath}: left as it is, though it does not run - {o.Home.Missing}"
                : $"  - {StoreWorkflow.RelativePath}: not added - {o.Home.Missing}");
        else if (o.WorkflowWritten)
            sb.AppendLine($"  - {StoreWorkflow.RelativePath}: added - packs {rel} with .NET {o.Dotnet} and publishes the .nupkg, "
                        + "signed in with the run's own GitHub token (no secrets)");
        else if (o.WorkflowSame)
            sb.AppendLine($"  - {StoreWorkflow.RelativePath}: already there as this tool writes it");
        else
            sb.AppendLine($"  - {StoreWorkflow.RelativePath}: already there and packs {rel}, though not word for word as this "
                        + "tool would write it - left as it is");
        if (o.Plan.Edits.Any(e => e.Change == PathChange.OutOfContentFiles))
            sb.AppendLine("  Why: ENCY's store installer keeps contentFiles/ as a folder of its own, so what was packed there landed "
                        + "away from the dll - a manifest there never finds it, and the extension never registers."
                        + (o.Plan.Edits.Any(e => e.Change == PathChange.OutOfContentFiles && e.Unchecked == null)
                            ? " Under build/ a file the build copies lands where the Release build has it." : ""));
        if (o.Plan.Edits.Any(e => e.Change == PathChange.WrongFolder))
            sb.AppendLine("  Why: NuGet adds the folder an item's wildcard matched after its PackagePath by itself (for <None Update=...>, "
                        + "the file's folder in the project), so a value that names that folder again put the files one folder too deep.");

        sb.AppendLine();
        string named = verdict.NamedByNuspec.Count == 0 ? ""
            : $" ({string.Join(" and ", verdict.NamedByNuspec)} at the root, where the nuspec names {(verdict.NamedByNuspec.Count == 1 ? "it" : "them")})";
        string manifest = verdict.Module != null ? $"{verdict.Manifest} finds {verdict.Module}"
            : $"the manifest is {verdict.Manifest} (its module_path is not relative to it, so the dll it names was not looked for)";
        sb.AppendLine($"Checked: `dotnet pack -c Release` (nuget.org and the ENCY Nexus as sources, NU1603 as an error) made "
                    + $"{o.Check.Nupkg}. Laid out the way ENCY's store installer lays it out, {verdict.AtBuildPath} "
                    + (verdict.AtBuildPath == 1 ? "file lands where the Release build has it" : "files land where the Release build has them")
                    + $"{named}, {manifest}, and nothing lands under contentFiles/.");
        foreach (var n in verdict.Notes) sb.AppendLine("  - note: " + n);
        if (facts?.Version is { } v && !v.Equals(ProbeVersion, StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"  - note: the package came out as version {v}, not the {ProbeVersion} the pack asked for - the csproj "
                        + "sets the package version itself (<PackageVersion>?), so a tag would not set it. Remove that line.");

        var stops = new List<string>();
        if (facts != null && !facts.HasMarker)
            stops.Add($"the package tags have no `{PackageCheck.MarkerTag}` - the store refuses such a package. Add it to <PackageTags> in {rel}.");
        if (facts?.SdkVersion is { } sdk && sdk.Contains('-'))
            stops.Add($"the package is built against SDK {sdk}, a prerelease - the store refuses that; reference a released "
                    + $"{ProjectLayout.SdkPackageId}.");
        // The store refuses an SDK above its line: newer assemblies do not register in the released ENCY.
        else if (facts?.SdkVersion is { } built && SdkPin.IsFromTheFuture(built, o.Ceiling))
            stops.Add($"the package is built against SDK {built}, newer than the store takes ({o.Ceiling}) - the released ENCY "
                    + $"does not register an extension built against a newer one, so the store refuses it. Reference "
                    + $"{ProjectLayout.SdkPackageId} {o.Ceiling} in {rel}.");
        if (stops.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Before the first publish, in the project:");
            foreach (var s in stops) sb.AppendLine("  - " + s);
        }

        sb.AppendLine();
        sb.AppendLine("Once, in the browser - only the author can do these:");
        sb.AppendLine($"  1. Sign in at {Store}.");
        sb.AppendLine($"  2. {Store}/publish: the developer registration, then accept the terms (I Agree).");
        int step = 3;
        if (gitHub != null)
            // The store's order (PublisherAuth): a bound repository's runs are its binder's, whoever starts
            // them; an unbound one's are whoever starts it, when their GitHub is linked. The first publish
            // binds it — so who starts THAT run decides whose the repository is.
            sb.AppendLine($"  {step++}. Profile -> \"Sign in with GitHub\" links your GitHub account. The first publish binds "
                        + $"{gitHub.GitHub} to {name}: start it as the card's owner, with that GitHub account linked - a run from "
                        + $"a repository not bound yet is credited to whoever starts it. After that every run from {gitHub.GitHub} "
                        + $"is credited to that owner, whoever starts it. To bind it before the first run: Connect on {Store}/account, "
                        + $"or `ency-extension-mcp claim {name} {gitHub.GitHub}`.");
        sb.AppendLine($"  {step}. Own the card: {name} has to belong to that store account - a new name becomes theirs with its first "
                    + "publish; a card someone else owns has to be handed over first.");

        sb.AppendLine();
        if (gitHub != null)
        {
            sb.AppendLine("To publish:");
            sb.AppendLine("  - commit and push the changes above;");
            sb.AppendLine("  - push a version tag: git tag vX.Y.Z, then git push origin vX.Y.Z - the workflow publishes X.Y.Z "
                        + "(publish_extension does both);");
            sb.AppendLine("  - or on GitHub: Actions -> \"Publish to ENCY Extension Store\" -> Run workflow, type the version; tick "
                        + "\"dry run\" to have the store check the package and publish nothing.");
            var alongside = TagWorkflows(o.Workflows);
            if (alongside.Count > 0)
                sb.AppendLine($"  A tag push also starts {string.Join(", ", alongside)} (they have tag triggers of their own); "
                            + "Run workflow starts this one alone.");
            sb.AppendLine($"  To follow a run: publish_status (the run of {StoreWorkflow.FileName}, then the store card), or "
                        + $"my_extensions; on GitHub, the Actions tab of {gitHub.GitHub}.");
        }
        else
        {
            sb.AppendLine("To publish from this machine:");
            sb.AppendLine($"  - pack it the way the check did: dotnet pack \"{rel}\" -c Release -p:Version=X.Y.Z "
                        + $"-p:WarningsAsErrors=NU1603 --source {StoreWorkflow.NuGetOrg} --source {StoreWorkflow.EncyNexus} -o out");
            sb.AppendLine("  - then publish_package out (or `ency-extension-mcp publish-package out`) - it signs in through the browser;");
            sb.AppendLine("  - once the repository has a GitHub remote (a mirror will do), call connect_to_store again: it adds the "
                        + "workflow, and a version tag publishes from then on.");
        }

        sb.AppendLine();
        sb.Append(ScheduleANote(facts, fromCi: gitHub != null));
        return sb.ToString();
    }

    /** What the store asks of every submission from 1 November 2026 — said, never answered here. */
    private string ScheduleANote(PackageFacts? facts, bool fromCi)
    {
        // How the store takes an answer from CI or a tool: the first publish with a new one stops with a link.
        string confirm = fromCi
            ? "A run with a new answer stops once with a link to confirm it in the store; confirm, then run the workflow again."
            : "A publish with a new answer stops once with a link to confirm it in the store; confirm, then publish again.";
        string route = fromCi ? "a publish from CI" : "a publish from this machine";
        var d = facts?.Declaration;
        if (d != null && d.Malformed == null && !d.Legacy
            && d.Domains.All(e => !string.IsNullOrWhiteSpace(e.Domain) && !string.IsNullOrWhiteSpace(e.Entitlement)))
            return "Schedule A: the package carries the answer ("
                 + (d.Domains.Count == 0 ? "none of the Reserved Functionality areas" : string.Join(", ", d.Domains.Select(x => x.Domain)))
                 + "). " + confirm;

        bool refused = today() >= ScheduleA.RefusedFrom;
        string when = refused
            ? $"Schedule A: since {ScheduleA.RefusedFrom:yyyy-MM-dd} the store refuses {route} without the Reserved Functionality answer"
            : $"Schedule A: from {ScheduleA.RefusedFrom:yyyy-MM-dd} the store refuses {route} without the Reserved Functionality answer (until then it publishes and warns)";
        string state = d == null ? "" : " The package's package.info.json has the answer in an earlier or broken form.";
        return when + $" - `{ScheduleA.Key}` in a package.info.json packed into the package (PackagePath=\"build\\\"; give it no "
             + "packageId or version, which the tag sets): [] when the extension provides none of the areas listed at "
             + $"{ScheduleA.Url}, otherwise one entry per area with its licence.{state} The answer is the author's statement "
             + "about their extension - ask them; this tool does not write it. " + confirm;
    }

    private async Task<string> WithUpdateNote(string text)
    {
        string? note = await updates.Note();
        return note == null ? text : text.TrimEnd() + "\n\n" + note;
    }

    // ---- the check pack

    private sealed record Check(string? Failure, LayoutVerdict? Verdict = null, PackageFacts? Facts = null, string? Nupkg = null);

    /**
     * Packs the project as the workflow will, into a folder of its own that is deleted afterwards, and
     * lays the package out the way ENCY's store installer does, against the Release build the pack made.
     */
    private async Task<Check> Verify(string project, string projectDir)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "ency-connect-" + Guid.NewGuid().ToString("N")[..12]);
        string output = Path.Combine(tmp, "pkg");
        // English messages to match on, whatever the machine's language.
        var env = new Dictionary<string, string>
        {
            ["DOTNET_CLI_UI_LANGUAGE"] = "en", ["DOTNET_NOLOGO"] = "1", ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
        };
        try
        {
            Directory.CreateDirectory(output);
            var pack = await proc.Run("dotnet",
                $"pack \"{project}\" -c Release -p:Version={ProbeVersion} -p:WarningsAsErrors=NU1603 "
                + $"--source {StoreWorkflow.NuGetOrg} --source {StoreWorkflow.EncyNexus} -o \"{output}\"",
                projectDir, env, PackTimeoutSeconds);
            if (!pack.Ok) return new Check(PackFailure(pack));

            var nupkgs = Directory.EnumerateFiles(output, "*.nupkg")
                .Where(f => !f.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase)).ToList();
            if (nupkgs.Count != 1)
                return new Check(nupkgs.Count == 0
                    ? "dotnet pack succeeded and wrote no .nupkg - is the project packable (<IsPackable>)?"
                    : $"dotnet pack wrote {nupkgs.Count} packages ({string.Join(", ", nupkgs.Select(Path.GetFileName))}); the workflow publishes one.");

            var where = await proc.Run("dotnet",
                $"msbuild \"{project}\" -nologo -p:Configuration=Release -getProperty:TargetDir -getProperty:TargetFileName",
                projectDir, env);
            string? buildDir = where.Ok ? TargetDir(where.StdOut) : null;
            if (buildDir == null || !Directory.Exists(buildDir))
                return new Check("could not ask MSBuild where the Release build went: " + Tail(where.Output, 5));

            var verdict = InstallLayout.Verify(nupkgs[0], buildDir);
            if (verdict.Failures.Count > 0)
                return new Check("the check pack shows the package would not install right:" + NL
                               + string.Join(NL, Capped(verdict.Failures, 12).Select(f => "  - " + f)) + NL + InstallLayout.Rule);
            return new Check(null, verdict, PackageCheck.TryRead(nupkgs[0]), Path.GetFileName(nupkgs[0]));
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* temp only */ }
        }
    }

    private static IEnumerable<string> Capped(IReadOnlyList<string> lines, int max) =>
        lines.Count <= max ? lines : lines.Take(max).Append($"... and {lines.Count - max} more like these");

    /** What went wrong in the pack, in its own words: the error lines, or the tail of the log. */
    private static string PackFailure(ProcResult r)
    {
        if (r.ExitCode == 127)
            return "dotnet is not installed or not on PATH - install the .NET SDK the project targets (https://dot.net).";
        if (r.ExitCode == -1)
            return $"dotnet pack did not finish in {PackTimeoutSeconds / 60} minutes.";
        string all = r.StdOut + "\n" + r.StdErr;
        var errors = all.Split('\n').Select(l => l.Trim())
            .Where(l => l.Contains(": error ", StringComparison.Ordinal) || l.StartsWith("error ", StringComparison.Ordinal))
            .Distinct().Take(8).ToList();
        string hint =
            all.Contains("NU1603") ? " NuGet found no package of the exact SDK version the csproj asks for on nuget.org or the ENCY "
                                     + "Nexus and would take another one (NU1603) - a build the store would offer to the wrong ENCY. "
                                     + "Reference a version that exists."
            : all.Contains("NETSDK1045") ? " This machine's .NET SDK is older than the project's target framework (NETSDK1045) - "
                                           + "install that SDK from https://dot.net."
            : all.Contains("NU1101") || all.Contains("NU1102") ? " A package the project references is on neither nuget.org nor "
                                           + "the ENCY Nexus - the two sources the workflow restores from, as this check does."
            : "";
        var lines = errors.Count > 0 ? errors : Tail(all, 15).Split('\n').ToList();
        return "dotnet pack failed." + hint + NL + string.Join(NL, lines.Select(l => "    " + l.TrimEnd()));
    }

    private static string Tail(string text, int lines) =>
        string.Join('\n', text.Replace("\r\n", "\n").TrimEnd().Split('\n').TakeLast(lines));

    /** TargetDir out of `dotnet msbuild -getProperty` (JSON once two or more properties are asked for). */
    internal static string? TargetDir(string output)
    {
        int brace = output.IndexOf('{');
        if (brace < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(output[brace..]);
            return doc.RootElement.TryGetProperty("Properties", out var p) && p.TryGetProperty("TargetDir", out var d)
                   && d.GetString() is { Length: > 0 } dir ? dir : null;
        }
        catch (JsonException) { return null; }
    }

    // ---- finding things

    private sealed record Candidate(string Path, bool PacksManifest);

    /**
     * Every csproj under the folder that is an extension: it references the ENCY SDK or packs a
     * *.settings.json. Build output, packages and dot-folders (.git, .vs, a build script's own) are
     * skipped; the walk is bounded, in case it starts somewhere far too wide.
     */
    private static List<Candidate> FindExtensionProjects(string root)
    {
        var found = new List<Candidate>();
        var pending = new Queue<(string Dir, int Depth)>();
        pending.Enqueue((root, 0));
        for (int visited = 0; pending.Count > 0 && visited < 5000; visited++)
        {
            var (dir, depth) = pending.Dequeue();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir, "*.csproj");
                dirs = depth < 8 ? Directory.GetDirectories(dir) : Array.Empty<string>();
            }
            catch (Exception) { continue; }
            foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string text;
                try { text = File.ReadAllText(f); } catch (Exception) { continue; }
                bool packs = PacksManifest.IsMatch(text);
                if (packs || SdkReference.IsMatch(text)) found.Add(new Candidate(f, packs));
            }
            foreach (var d in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(d);
                if (name.StartsWith('.') || SkippedDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                pending.Enqueue((d, depth + 1));
            }
        }
        return found;
    }

    /** The other workflows a tag push starts as well (they have a tag trigger of their own). */
    private static List<string> TagWorkflows(string workflows) =>
        StoreWorkflow.Workflows(workflows).Where(f => Read(f) is { } t && Regex.IsMatch(t, @"(?m)^\s*tags\s*:"))
            .Select(f => Path.GetFileName(f)).ToList();

    /** The line ending the repository's other workflows use, so the new one does not stand out in a diff. */
    private static string NewlineOf(string workflows) =>
        StoreWorkflow.Workflows(workflows).Any(f => Read(f)?.Contains("\r\n") == true) ? "\r\n" : "\n";

    /**
     * The one framework the project builds: its csproj first, then the Directory.Build.props MSBuild
     * would import from the folders above, up to the repository root.
     */
    private static (string? Tfm, string? Error) TargetFramework(string csprojText, string project, string repo, string relProject)
    {
        var sources = new List<(string Text, string From)> { (csprojText, relProject) };
        for (string? d = Path.GetDirectoryName(project); d != null && IsUnder(d, repo); d = Path.GetDirectoryName(d))
        {
            string props = Path.Combine(d, "Directory.Build.props");
            if (File.Exists(props) && Read(props) is { } t) { sources.Add((t, Rel(repo, props))); break; }
        }
        foreach (var (text, from) in sources)
        {
            string? value = ProjectLayout.CsprojProperty(text, "TargetFramework") ?? ProjectLayout.CsprojProperty(text, "TargetFrameworks");
            if (value == null) continue;
            if (value.Contains("$("))
                return (null, $"{from} sets the target framework from an MSBuild property ({value}), so the workflow cannot tell which .NET to install.");
            var tfms = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tfms.Length > 1)
                return (null, $"{from} builds several target frameworks ({value}), and ENCY installs one dll per extension - build "
                            + "the one your ENCY uses (<TargetFramework>), then call connect_to_store again.");
            if (tfms.Length == 1) return (tfms[0], null);
        }
        return (null, $"cannot tell which framework {relProject} targets - it sets no <TargetFramework>.");
    }

    // ---- files

    private static string Rel(string repo, string path) => Path.GetRelativePath(repo, path).Replace('\\', '/');

    internal static bool IsUnder(string path, string dir)
    {
        string d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p.Equals(d, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception) { return null; }
    }

    private static bool SameText(string a, string b) => a.Replace("\r\n", "\n") == b.Replace("\r\n", "\n");

    /** The csproj as text, and whether it starts with a byte order mark — which the edit has to keep. */
    internal static bool TryDecode(byte[] bytes, out string text, out bool bom, out string? error)
    {
        text = "";
        error = null;
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            error = "the file is saved as UTF-16 - save it as UTF-8, then call connect_to_store again.";
            return false;
        }
        try
        {
            int skip = bom ? 3 : 0;
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, skip, bytes.Length - skip);
            return true;
        }
        catch (DecoderFallbackException)
        {
            error = "the file is not UTF-8 - save it as UTF-8, then call connect_to_store again.";
            return false;
        }
    }

    internal static byte[] Encode(string text, bool bom)
    {
        byte[] body = new UTF8Encoding(false).GetBytes(text);
        return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }

    /** Written next to the file and moved over it, so a failure halfway never leaves half a csproj. */
    private static void WriteAtomically(string path, byte[] bytes)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* temp only */ }
        }
    }
}
