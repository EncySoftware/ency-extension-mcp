using System.Text.RegularExpressions;

namespace EncyExtensionMcp;

/** A git repository on disk, read from its files: where its root is and where its remotes point. */
public sealed record GitRemote(string Name, string Url)
{
    /** owner/repo when the remote is on github.com; null for any other host. */
    public string? GitHub => GitRepository.GitHubRepository(Url);
}

/// <summary>
/// What a tool needs to know about the repository a folder sits in, read from the repository's own
/// files rather than through `git`: the people this is for often have git only inside an editor or
/// GitHub Desktop, with nothing on PATH, and the answer must not depend on that.
/// </summary>
public static class GitRepository
{
    private static readonly Regex GitHubUrl = new(
        @"(?:^|[@/])(?:www\.)?github\.com[:/]+(?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?/*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /** The folder holding .git (a directory, or a file in a worktree), from here up; null outside any repository. */
    public static string? Root(string dir)
    {
        for (string? d = Path.GetFullPath(dir); d != null; d = Path.GetDirectoryName(d))
            if (Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"))) return d;
        return null;
    }

    /** owner/repo of a github.com remote URL (https, ssh or scp form); null for any other host. */
    public static string? GitHubRepository(string url)
    {
        var m = GitHubUrl.Match(url.Trim());
        return m.Success ? $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}" : null;
    }

    /** The GitHub remote a workflow would run in — origin when it is on GitHub, else the first one that is. */
    public static GitRemote? GitHubRemote(IReadOnlyList<GitRemote> remotes) =>
        remotes.Where(r => r.GitHub != null)
               .OrderBy(r => r.Name.Equals("origin", StringComparison.Ordinal) ? 0 : 1)
               .FirstOrDefault();

    /**
     * The remotes named in the repository's config, in the order written; empty when there are none
     * or the config cannot be read. A worktree's .git is a file naming its own folder, whose
     * `commondir` leads to the repository that holds the config.
     */
    public static IReadOnlyList<GitRemote> Remotes(string root)
    {
        string? config = ConfigFile(root);
        if (config == null) return Array.Empty<GitRemote>();
        string[] lines;
        try { lines = File.ReadAllLines(config); }
        catch (Exception) { return Array.Empty<GitRemote>(); }

        var remotes = new List<GitRemote>();
        string? remote = null;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                var section = Regex.Match(line, @"^\[\s*remote\s+""(?<name>[^""]+)""\s*\]", RegexOptions.IgnoreCase);
                remote = section.Success ? section.Groups["name"].Value : null;
                continue;
            }
            if (remote == null) continue;
            var kv = Regex.Match(line, @"^url\s*=\s*(?<value>.*)$", RegexOptions.IgnoreCase);
            if (!kv.Success) continue;
            string url = kv.Groups["value"].Value.Trim();
            if (url.Length >= 2 && url[0] == '"' && url[^1] == '"') url = url[1..^1];
            if (url.Length > 0 && !remotes.Any(r => r.Name == remote)) remotes.Add(new GitRemote(remote, url));
        }
        return remotes;
    }

    private static string? ConfigFile(string root)
    {
        string git = Path.Combine(root, ".git");
        string? gitDir = null;
        try
        {
            if (Directory.Exists(git)) gitDir = git;
            else if (File.Exists(git))
            {
                var m = Regex.Match(File.ReadAllText(git), @"^\s*gitdir:\s*(?<dir>.+?)\s*$", RegexOptions.Multiline);
                if (m.Success) gitDir = Path.GetFullPath(Path.Combine(root, m.Groups["dir"].Value));
            }
            if (gitDir == null) return null;
            string common = Path.Combine(gitDir, "commondir");
            if (File.Exists(common))
                gitDir = Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(common).Trim()));
            string config = Path.Combine(gitDir, "config");
            return File.Exists(config) ? config : null;
        }
        catch (Exception) { return null; }
    }
}
