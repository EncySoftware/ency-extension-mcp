using System.Globalization;

namespace EncyExtensionMcp;

/**
 * One version on its way to the feed, as a store that reviews before the feed tracks it
 * (GET /extensions/my/submissions). State: SUBMITTED (waits for a moderator), REJECTED, WITHDRAWN,
 * PENDING or SIGNED (waits to be signed), FAILED, PUBLISHED.
 */
public record MySubmission(string PackageId, string Version, string State, string? RejectionReason,
                           string? LastError, string? CreatedAt, string? UpdatedAt);

/**
 * A version the card does not show yet, and where it stands.
 *
 * @param FromStore read from the store's own submissions; false — only the run's report says so, and
 *                  a report is the run's last word, not the version's state: nothing rewrites it when
 *                  a moderator decides.
 */
public sealed record InFlight(string PackageId, string? Version, string State, string? Reason, bool FromStore);

public static class Submissions
{
    public const string Moderator = "a moderator approves it before it appears in the catalog";

    /** Waiting for a moderator. A 202 that names no state at all counts as review. */
    public static bool IsReview(string? state) => string.IsNullOrEmpty(state) || state == "SUBMITTED";

    /** Waiting to be signed: no moderator is involved. */
    public static bool IsSigning(string? state) => state is "PENDING" or "SIGNED";

    /** Stopped short of the feed: this version will not reach the catalog as it is. */
    public static bool IsStopped(string? state) => state is "FAILED" or "REJECTED" or "WITHDRAWN";

    /**
     * The version of the package that the card does not show yet, or null when the card tells the whole
     * story. The run's version is preferred when the store has it, otherwise the latest submission.
     *
     * @param cardVersion the card's latest version; null when there is no card, or only the empty row a
     *                    first submission claims the name with
     * @param submissions null when the store does not offer them (older store) or could not be asked;
     *                    the run's report is all there is then
     */
    public static InFlight? Of(string packageId, string? cardVersion, BuildReport? build,
                               IReadOnlyList<MySubmission>? submissions)
    {
        var rows = (submissions ?? Array.Empty<MySubmission>())
            .Where(s => s.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count > 0)
        {
            var ofRun = build?.Version == null ? new List<MySubmission>()
                : rows.Where(s => s.Version.Equals(build.Version, StringComparison.OrdinalIgnoreCase)).ToList();
            var pick = (ofRun.Count > 0 ? ofRun : rows)
                .OrderByDescending(s => Instant(s.CreatedAt)).ThenByDescending(s => Instant(s.UpdatedAt)).First();
            if (pick.State == "PUBLISHED") return null;
            if (cardVersion != null && !IsNewer(pick.Version, cardVersion)) return null;
            string? reason = pick.State == "REJECTED" ? pick.RejectionReason : pick.LastError;
            return new InFlight(pick.PackageId, pick.Version, pick.State, OneLine(reason), FromStore: true);
        }
        // No submissions to read: only the run's own report, and only a version newer than the card.
        if (build?.Status != "SUBMITTED") return null;
        if (cardVersion != null && (build.Version == null || !IsNewer(build.Version, cardVersion))) return null;
        return new InFlight(packageId, build.Version, "SUBMITTED", null, FromStore: false);
    }

    /** One sentence, for publish_status and publish_folder_status. */
    public static string Sentence(InFlight f)
    {
        string what = f.PackageId + (f.Version != null ? " " + f.Version : "");
        if (!f.FromStore)
            return $"Submitted for review: {what} — as the run reported it; this store does not say whether a moderator has decided since.";
        return f.State switch
        {
            "SUBMITTED" => $"Submitted for review: {what} — {Moderator}.",
            "PENDING" or "SIGNED" => $"Accepted, not in the catalog yet: {what} — it waits to be signed and appears in the catalog once it reaches the feed.",
            "REJECTED" => $"Rejected by a moderator: {what}{(f.Reason == null ? "" : " — " + f.Reason.TrimEnd('.'))}. Fix it and publish again — the version number is free.",
            "WITHDRAWN" => $"Withdrawn: {what} — taken back before a moderator decided. Publish it again to resubmit — the version number is free.",
            "FAILED" => $"Not published: {what} — it failed on the way to the feed{Because(f.Reason)}. Publish again with a new version.",
            _ => $"Not in the catalog yet: {what} — the store reports it as {f.State}.",
        };
    }

    /** The short form after a card's line in my_extensions, e.g. "1.0.1 submitted for review". */
    public static string Suffix(InFlight f)
    {
        string v = f.Version ?? "a new version";
        if (!f.FromStore) return $"{v} submitted for review";
        return f.State switch
        {
            "SUBMITTED" => $"{v} submitted for review",
            "PENDING" or "SIGNED" => $"{v} waiting to be signed",
            "REJECTED" => $"{v} rejected{Because(f.Reason)}",
            "WITHDRAWN" => $"{v} withdrawn",
            "FAILED" => $"{v} failed on the way to the feed{Because(f.Reason)}",
            _ => $"{v} is {f.State} in the store",
        };
    }

    /** Something the author has to act on: refused by a moderator, or stopped on the way to the feed. */
    public static bool NeedsAuthor(InFlight f) => f.FromStore && (f.State is "REJECTED" or "FAILED");

    /**
     * Semver order, enough for "is this newer than the card": the numbers first, then a release after
     * its own pre-releases. A version that does not read as one is not newer — the card is trusted then.
     */
    public static bool IsNewer(string candidate, string current)
    {
        if (!TryRead(candidate, out var a, out string? aPre) || !TryRead(current, out var b, out string? bPre)) return false;
        int byNumbers = a.CompareTo(b);
        if (byNumbers != 0) return byNumbers > 0;
        if (aPre == null) return bPre != null;
        return bPre != null && string.CompareOrdinal(aPre, bPre) > 0;
    }

    private static bool TryRead(string version, out Version numbers, out string? pre)
    {
        string v = version.Trim().Split('+')[0];
        int dash = v.IndexOf('-');
        pre = dash >= 0 ? v[(dash + 1)..] : null;
        string head = dash >= 0 ? v[..dash] : v;
        if (!Version.TryParse(head.Count(c => c == '.') == 0 ? head + ".0" : head, out var parsed))
        {
            numbers = new Version();
            return false;
        }
        // 1.0 and 1.0.0 are the same version; System.Version would order them.
        numbers = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }

    private static DateTimeOffset Instant(string? at) =>
        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;

    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 300 ? line[..297] + "..." : line;
    }

    private static string Because(string? reason) => reason == null ? "" : ": " + reason.TrimEnd('.');
}
