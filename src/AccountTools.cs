using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace EncyExtensionMcp;

/**
 * What the signed-in author has in the store, in one call. publish_status answers for one repository
 * folder at a time, so "which of my extensions needs me" meant walking folders; the site's My published
 * page answers it in one screen, and an assistant deserves the same view (16.09.2026).
 */
[McpServerToolType]
public class AccountTools(IStoreClient store, IStoreAuth auth)
{
    [McpServerTool(Name = "my_extensions"), Description(
        "Everything the signed-in author has published, what needs attention first: a failing build " +
        "(with the step and the run link), a rejected card (with the moderator's reason), one waiting " +
        "for a moderator — then the live ones with their catalogue links and latest versions, then the " +
        "hidden ones. Read-only; needs the store sign-in (`ency-extension-mcp login`).")]
    public async Task<string> MyExtensions()
    {
        string? token;
        try { token = await auth.GetAccessToken(); }
        catch (InvalidOperationException e) { return "ERROR: " + e.Message; }
        if (token == null) return "ERROR: not signed in to the store — run `ency-extension-mcp login` (a browser sign-in), then call my_extensions again.";

        IReadOnlyList<MyExtension> mine;
        IReadOnlyList<BuildReport> builds;
        try
        {
            mine = await store.GetMyExtensions(token);
            builds = await store.GetMyBuilds(token);
        }
        catch (StoreApiException e) { return $"ERROR: the store refused ({e.Status}): {e.Message}"; }
        // Where each submitted version stands; null on a store that does not track them.
        var submissions = await store.GetMySubmissions(token);

        if (mine.Count == 0 && builds.Count == 0 && (submissions == null || submissions.Count == 0))
            return "Nothing published under this account yet. publish_folder or publish_package puts the first extension in.";

        return Render(mine, builds, store.StoreBaseUrl, submissions);
    }

    /**
     * Pure, so the ordering can be tested without a sign-in.
     *
     * @param submissions the store's own word on each submitted version; null when it does not track
     *                    them, and the run's SUBMITTED report is all there is
     */
    public static string Render(IReadOnlyList<MyExtension> mine, IReadOnlyList<BuildReport> builds, string storeBase,
                                IReadOnlyList<MySubmission>? submissions = null)
    {
        var buildOf = builds.Where(b => b.PackageId != null)
            .GroupBy(b => b.PackageId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);

        var all = mine.Select(e =>
        {
            var build = buildOf.GetValueOrDefault(e.PackageId);
            var inFlight = Submissions.Of(e.PackageId, e.LatestVersion, build, submissions);
            return (Ext: e, Build: build, InFlight: inFlight, Rank: Rank(e, build, inFlight));
        }).ToList();
        // A first submission claims its name with an empty row: no version, nothing to open. That is not a
        // card, so it goes under "no card yet" with what the store says about the submission, and no link.
        static bool Unborn((MyExtension Ext, BuildReport? Build, InFlight? InFlight, int Rank) r) =>
            r.Ext.LatestVersion == null && r.InFlight != null && r.Build?.Status != "FAILED";
        var rows = all.Where(r => !Unborn(r))
            .OrderBy(r => r.Rank).ThenBy(r => r.Ext.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder();
        void Section(string title, IEnumerable<(MyExtension Ext, BuildReport? Build, InFlight? InFlight, int Rank)> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(title + ":");
            foreach (var r in list) sb.Append(Line(r.Ext, r.Build, r.InFlight, storeBase));
        }
        Section("Needs attention", rows.Where(r => r.Rank <= 2));
        Section("Live", rows.Where(r => r.Rank == 4));
        Section("Hidden", rows.Where(r => r.Rank == 3));

        bool HasNoCard(string? packageId) =>
            packageId == null || mine.All(e => !e.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));

        // A repository whose build failed before anything was published has no card to hang on.
        var orphans = builds.Where(b => b.Status == "FAILED" && HasNoCard(b.PackageId)).ToList();
        if (orphans.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Failed builds without a card yet:");
            foreach (var b in orphans)
                sb.AppendLine($"- {b.Repository}: FAILED at {b.FailedStep ?? "?"}" + (b.RunUrl != null ? $" — {b.RunUrl}" : ""));
        }

        // Taken before the feed (HTTP 202): there is no card until a moderator approves, so without this
        // the extension would be missing from the list, or listed with a link to an empty page.
        var waiting = all.Where(Unborn).Select(r => r.InFlight!).ToList();
        var named = new HashSet<string>(waiting.Select(f => f.PackageId), StringComparer.OrdinalIgnoreCase);
        var cardless = (submissions ?? Array.Empty<MySubmission>()).Select(s => s.PackageId)
            .Concat(builds.Where(b => b.Status == "SUBMITTED" && b.PackageId != null).Select(b => b.PackageId!))
            .Where(p => HasNoCard(p) && named.Add(p)).ToList();
        foreach (var p in cardless)
            if (Submissions.Of(p, null, buildOf.GetValueOrDefault(p), submissions) is { } f) waiting.Add(f);
        if (waiting.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Submitted, no card yet:");
            foreach (var f in waiting.OrderBy(f => f.PackageId, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine("- " + Submissions.Sentence(f));
        }
        return sb.ToString().TrimEnd();
    }

    /** 0 failing build, 1 rejected (the card, or a newer version of it), 2 waiting for a moderator, 3 hidden, 4 live. */
    private static int Rank(MyExtension e, BuildReport? build, InFlight? inFlight)
    {
        if (build?.Status == "FAILED") return 0;
        if (!e.Approved && !string.IsNullOrWhiteSpace(e.RejectionReason)) return 1;
        if (inFlight != null && Submissions.NeedsAuthor(inFlight)) return 1;
        if (!e.Approved) return 2;
        if (e.Unlisted) return 3;
        return 4;
    }

    private static string Line(MyExtension e, BuildReport? build, InFlight? inFlight, string storeBase)
    {
        var sb = new StringBuilder();
        string version = e.LatestVersion != null ? " " + e.LatestVersion : "";
        string url = $"{storeBase}/extension/{e.Slug}";
        if (build?.Status == "FAILED")
        {
            sb.AppendLine($"- {e.Name}{version} — build FAILED at {build.FailedStep ?? "?"}" + (build.RunUrl != null ? $": {build.RunUrl}" : ""));
            foreach (var l in (build.FailureLog ?? "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(3))
                sb.AppendLine("    " + l);
        }
        else if (!e.Approved && !string.IsNullOrWhiteSpace(e.RejectionReason))
            sb.AppendLine($"- {e.Name}{version} — rejected: {e.RejectionReason.Trim()} Fix it and publish again ({url})");
        else if (!e.Approved)
            sb.AppendLine($"- {e.Name}{version} — waiting for a moderator ({url})" + Newer(inFlight));
        else if (e.Unlisted)
            sb.AppendLine($"- {e.Name}{version} — hidden from the catalogue ({url})" + Newer(inFlight));
        else
            sb.AppendLine($"- {e.Name}{version} — {url}" + (e.Category != null ? $" (category: {e.Category})" : " (category: other)")
                          + (build?.Status == "RUNNING" ? " — building now" : "") + Newer(inFlight));
        return sb.ToString();
    }

    /** A version newer than the card shows, and where it stands; the card keeps the previous one meanwhile. */
    private static string Newer(InFlight? inFlight) => inFlight == null ? "" : " — " + Submissions.Suffix(inFlight);
}
