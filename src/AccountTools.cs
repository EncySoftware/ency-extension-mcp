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

        if (mine.Count == 0 && builds.Count == 0)
            return "Nothing published under this account yet. publish_folder or publish_package puts the first extension in.";

        return Render(mine, builds, store.StoreBaseUrl);
    }

    /** Pure, so the ordering can be tested without a sign-in. */
    public static string Render(IReadOnlyList<MyExtension> mine, IReadOnlyList<BuildReport> builds, string storeBase)
    {
        var buildOf = builds.Where(b => b.PackageId != null)
            .GroupBy(b => b.PackageId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);

        var rows = mine.Select(e => (Ext: e, Build: buildOf.GetValueOrDefault(e.PackageId), Rank: Rank(e, buildOf.GetValueOrDefault(e.PackageId))))
            .OrderBy(r => r.Rank).ThenBy(r => r.Ext.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var sb = new StringBuilder();
        void Section(string title, IEnumerable<(MyExtension Ext, BuildReport? Build, int Rank)> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(title + ":");
            foreach (var r in list) sb.Append(Line(r.Ext, r.Build, storeBase));
        }
        Section("Needs attention", rows.Where(r => r.Rank <= 2));
        Section("Live", rows.Where(r => r.Rank == 4));
        Section("Hidden", rows.Where(r => r.Rank == 3));

        // A repository whose build failed before anything was published has no card to hang on.
        var orphans = builds.Where(b => b.Status == "FAILED" && (b.PackageId == null || mine.All(e => !e.PackageId.Equals(b.PackageId, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (orphans.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Failed builds without a card yet:");
            foreach (var b in orphans)
                sb.AppendLine($"- {b.Repository}: FAILED at {b.FailedStep ?? "?"}" + (b.RunUrl != null ? $" — {b.RunUrl}" : ""));
        }
        return sb.ToString().TrimEnd();
    }

    /** 0 failing build, 1 rejected, 2 waiting for a moderator, 3 hidden, 4 live. */
    private static int Rank(MyExtension e, BuildReport? build)
    {
        if (build?.Status == "FAILED") return 0;
        if (!e.Approved && !string.IsNullOrWhiteSpace(e.RejectionReason)) return 1;
        if (!e.Approved) return 2;
        if (e.Unlisted) return 3;
        return 4;
    }

    private static string Line(MyExtension e, BuildReport? build, string storeBase)
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
            sb.AppendLine($"- {e.Name}{version} — waiting for a moderator ({url})");
        else if (e.Unlisted)
            sb.AppendLine($"- {e.Name}{version} — hidden from the catalogue ({url})");
        else
            sb.AppendLine($"- {e.Name}{version} — {url}" + (e.Category != null ? $" (category: {e.Category})" : " (category: other)")
                          + (build?.Status == "RUNNING" ? " — building now" : ""));
        return sb.ToString();
    }
}
