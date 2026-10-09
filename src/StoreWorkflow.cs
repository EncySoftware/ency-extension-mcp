using System.Text.RegularExpressions;

namespace EncyExtensionMcp;

/// <summary>
/// The workflow connect_to_store adds to a repository: on a version tag (or a manual run) it packs
/// the project on GitHub with `dotnet pack` and hands the .nupkg to the store's publish action, signed
/// in with the run's own GitHub token. Unlike the template's workflow it builds the author's csproj
/// where it is and as it is — no src/ layout, no Directory.Build.targets, no PackReady step (that
/// step deletes *.xml from the output, and some extensions keep their operation's definition in one).
/// </summary>
public static class StoreWorkflow
{
    public const string FileName = "publish_store.yml";
    public const string RelativePath = ".github/workflows/" + FileName;
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";
    /** Some EncySoftware.CAMAPI.SDK.Net versions are published here only (3.0.15, for one). */
    public const string EncyNexus = "https://nexus.encycam.com/repository/master/index.json";

    private static readonly Regex CsprojToken = new(
        @"""(?<p>[^""\r\n]+?\.csproj)""|'(?<p>[^'\r\n]+?\.csproj)'|(?<p>[^\s""'=]+\.csproj)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /**
     * The SDK line setup-dotnet installs for a target framework; null for any other framework. ENCY 3
     * extensions target .NET 8, 9 or 10 — a new one is added here when ENCY moves to it.
     */
    public static string? DotnetVersion(string? targetFramework)
    {
        var m = Regex.Match(targetFramework?.Trim() ?? "", @"^net(?<major>\d+)\.\d+", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Groups["major"].Value switch { "8" => "8.0.x", "9" => "9.0.x", "10" => "10.0.x", _ => null };
    }

    /** Whether a path can go into the workflow's bash step as it is: nothing there for the shell to read. */
    public static bool IsSafePath(string path) =>
        path.Length > 0 && path.IndexOfAny(new[] { '"', '$', '`', '\\', '\r', '\n' }) < 0;

    /** The workflow for this project, with the line ending its neighbours use. */
    public static string Render(string csprojPath, string dotnetVersion, string newline = "\n") =>
        Template.Replace("\r\n", "\n")   // a raw literal takes the source file's line endings
                .Replace("__CSPROJ__", csprojPath)
                .Replace("__DOTNET__", dotnetVersion)
                .Replace("\n", newline);

    // ---- reading a repository that has one

    /**
     * This workflow in the repository the folder sits in (or in the folder itself, outside git);
     * null when there is none. A repository that has it publishes the connect_to_store way.
     */
    public static string? Find(string dir)
    {
        foreach (var root in new[] { GitRepository.Root(dir), Path.GetFullPath(dir) })
        {
            if (root == null) continue;
            string file = Path.Combine(root, ".github", "workflows", FileName);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /** The repository root a workflow file belongs to: two folders above .github/workflows/. */
    public static string RepositoryOf(string workflowFile) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(workflowFile)!, "..", ".."));

    /**
     * Every csproj a workflow names outside its comments, '/'-separated, in order — what its pack step
     * builds. A workflow that names none packs whatever its folder holds, which cannot be told here.
     */
    public static IReadOnlyList<string> PackedProjects(string yaml)
    {
        var found = new List<string>();
        foreach (var line in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith('#')) continue;
            foreach (Match m in CsprojToken.Matches(line))
            {
                string p = Normalize(m.Groups["p"].Value);
                if (p.Length > 0 && !found.Contains(p, StringComparer.OrdinalIgnoreCase)) found.Add(p);
            }
        }
        return found;
    }

    /**
     * Whether a csproj path from a workflow is this project, given as a path in the repository: the
     * same path, or the end of it when the step runs from a folder further down.
     */
    public static bool SameProject(string named, string relProject)
    {
        string a = Normalize(named), b = Normalize(relProject);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase);
    }

    /**
     * The package id of the project a connected repository publishes: the csproj its workflow packs,
     * read for <PackageId>, then <AssemblyName>, then its file name — NuGet's own order. Null when
     * the workflow names no csproj, or the one it names is not in the repository.
     */
    public static string? PackageIdOf(string workflowFile)
    {
        try
        {
            var named = PackedProjects(File.ReadAllText(workflowFile));
            if (named.Count == 0) return null;
            string csproj = Path.Combine(RepositoryOf(workflowFile), named[0].Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(csproj)) return null;
            string text = File.ReadAllText(csproj);
            return ProjectLayout.CsprojProperty(text, "PackageId") ?? ProjectLayout.CsprojProperty(text, "AssemblyName")
                   ?? Path.GetFileNameWithoutExtension(csproj);
        }
        catch (Exception) { return null; }
    }

    /** A workflow other than this one that publishes to the store already — the template's, for one. */
    public static string? OtherStoreWorkflow(string repo) =>
        Workflows(Path.Combine(repo, ".github", "workflows"))
            .FirstOrDefault(f => Read(f)?.Contains("publish-extension-action", StringComparison.OrdinalIgnoreCase) == true)
            is { } file ? Path.GetFileName(file) : null;

    private static readonly Regex BuildsSrc = new(
        @"\bdotnet\s+(?:build|pack)\s+[""']?(?:\./)?src(?:[""'/\s]|$)|^\s*folder:\s*[""']?(?:\./)?src/",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /**
     * Whether a workflow is the template's own: publish.yml, building src/ — the one publish_extension
     * tags for and update_extension refreshes. Renamed or hand-made, it is the repository's own, and the
     * template's files would only add a second publish beside it.
     */
    public static bool IsTemplates(string workflowFile) =>
        Path.GetFileName(workflowFile).Equals("publish.yml", StringComparison.OrdinalIgnoreCase)
        && Read(workflowFile) is { } yaml && BuildsSrc.IsMatch(yaml);

    /** The workflow files of a folder other than this one, in name order. */
    public static IEnumerable<string> Workflows(string dir) =>
        !Directory.Exists(dir) ? Enumerable.Empty<string>()
        : Directory.EnumerateFiles(dir)
            .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /** The file names of the workflows in a folder, other than this one, that a version tag push starts. */
    public static List<string> TagWorkflows(string dir) =>
        Workflows(dir).Where(f => Read(f) is { } t && StartsOnVersionTag(t)).Select(f => Path.GetFileName(f)).ToList();

    /** "a", "a and b", "a, b and c". */
    public static string Listed(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.SkipLast(1)) + " and " + items[^1];

    /** The name GitHub lists a workflow under in the Actions tab: its `name:`, else its file name. */
    public static string DisplayName(string workflowFile)
    {
        var m = Read(workflowFile) is { } yaml
            ? Regex.Match(yaml, @"^name:[ \t]*(?<n>[^\r\n]*?)[ \t]*(?:[ \t]#[^\r\n]*)?\r?$", RegexOptions.Multiline) : null;
        string name = m is { Success: true } ? m.Groups["n"].Value.Trim().Trim('"', '\'').Trim() : "";
        return name.Length > 0 ? name : Path.GetFileName(workflowFile);
    }

    /**
     * How a person starts a workflow, read from its `on:` block: a version tag, Actions -> Run workflow,
     * or both — for the sentences that send an author to a workflow of their own.
     */
    public static string HowItStarts(string workflowFile)
    {
        string? yaml = Read(workflowFile);
        bool? tag = yaml == null ? null : TagTrigger(yaml);
        bool? hand = yaml == null ? null : RunsByHand(yaml);
        string run = $"Actions -> \"{DisplayName(workflowFile)}\" -> Run workflow on GitHub";
        return (tag, hand) switch
        {
            (true, true) => $"a version tag (vX.Y.Z), or {run}",
            (true, _) => "a version tag (vX.Y.Z)",
            (_, true) => run,
            _ => "the way its on: block says",
        };
    }

    private static string Normalize(string path)
    {
        string p = path.Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    // ---- what starts a workflow: its `on:` block, read the way GitHub reads it

    /** Version tags a push of which publishes; a workflow is said to start on a tag push when any of them starts it. */
    private static readonly string[] VersionTags = { "v0.1.0", "v1.0.0", "v1.2.3", "v10.20.30" };

    /**
     * Whether pushing a version tag (vX.Y.Z) starts this workflow — see TagTrigger. False when that
     * cannot be told: then nothing is claimed.
     */
    public static bool StartsOnVersionTag(string yaml) => TagTrigger(yaml) == true;

    /**
     * Whether pushing a version tag (vX.Y.Z) starts this workflow, from the structure of its `on:` block
     * — a string, a list or a map — never from a `tags:` line anywhere in the file (a docker step's
     * `with: tags:` is one). GitHub's rule: on.push with tags runs for a tag they match, with
     * tags-ignore for a tag they do not; on.push with none of branches, branches-ignore, tags and
     * tags-ignore runs for every tag (path filters are not applied to tag pushes); on.push with only
     * branches or branches-ignore never runs for a tag. `create` runs for every new tag. Null when the
     * part of the block that decides it cannot be read (an alias, a construct this reader does not take
     * apart): then nothing is claimed either way.
     */
    public static bool? TagTrigger(string yaml)
    {
        // A reader of a subset: whatever it cannot read is no claim, never a failed answer.
        try { return TagTrigger(Trigger.On(yaml)); }
        catch (Exception) { return null; }
    }

    /** TagTrigger of a workflow file; null when the file cannot be read either. */
    public static bool? TagTriggerOf(string workflowFile) => Read(workflowFile) is { } yaml ? TagTrigger(yaml) : null;

    /** RunsByHand of a workflow file; null when the file cannot be read either. */
    public static bool? RunsByHandOf(string workflowFile) => Read(workflowFile) is { } yaml ? RunsByHand(yaml) : null;

    /** Whether GitHub offers Actions -> Run workflow for it: its `on:` block names workflow_dispatch. Null when that cannot be told. */
    public static bool? RunsByHand(string yaml)
    {
        try
        {
            if (Events(Trigger.On(yaml)) is not { } on) return null;
            return on.Events.ContainsKey("workflow_dispatch") ? true : on.Partial ? null : false;
        }
        catch (Exception) { return null; }
    }

    /** The events an `on:` value names, with what each holds; Partial when some of the list could not be read. */
    private sealed record EventSet(Dictionary<string, Trigger.Node?> Events, bool Partial);

    private static EventSet? Events(Trigger.Node? on)
    {
        var events = new Dictionary<string, Trigger.Node?>(StringComparer.Ordinal);
        bool partial = false;
        switch (on)
        {
            case null: break;   // no `on:` at all: GitHub runs it for nothing
            case Trigger.Unknown: return null;
            case Trigger.Scalar s: if (s.Value != null) events[s.Value] = null; break;
            case Trigger.Seq q:
                foreach (var item in q.Items)
                    if (item is Trigger.Scalar { Value: { } name }) events[name] = null;
                    else if (item is Trigger.Unknown) partial = true;
                break;
            case Trigger.Map m:
                foreach (var (key, value) in m.Entries) events[key] = value;
                break;
        }
        return new EventSet(events, partial);
    }

    private static bool? TagTrigger(Trigger.Node? on)
    {
        if (Events(on) is not { } set) return null;
        var events = set.Events;
        if (events.ContainsKey("create")) return true;
        if (!events.TryGetValue("push", out var push)) return set.Partial ? null : false;
        if (push is Trigger.Unknown) return null;
        if (push is null or Trigger.Scalar { Value: null }) return true;
        if (push is not Trigger.Map filters) return false;

        var keys = new Dictionary<string, Trigger.Node?>(StringComparer.Ordinal);
        foreach (var (key, value) in filters.Entries) keys[key] = value;
        if (keys.TryGetValue("tags", out var tags))
            return Unread(tags) ? null : Patterns(tags) is { } p && VersionTags.Any(t => FilterMatches(p, t));
        if (keys.TryGetValue("tags-ignore", out var ignore))
            return Unread(ignore) ? null : Patterns(ignore) is { } p && VersionTags.Any(t => !FilterMatches(p, t));
        return !keys.ContainsKey("branches") && !keys.ContainsKey("branches-ignore");
    }

    /** A value, or an item of a list, this reader could not read. */
    private static bool Unread(Trigger.Node? node) =>
        node is Trigger.Unknown || (node is Trigger.Seq q && q.Items.Any(i => i is Trigger.Unknown));

    private static List<string>? Patterns(Trigger.Node? node) => node switch
    {
        Trigger.Scalar { Value: { } one } => new List<string> { one },
        Trigger.Seq q when q.Items.All(i => i is Trigger.Scalar { Value: not null }) =>
            q.Items.Select(i => ((Trigger.Scalar)i).Value!).ToList(),
        _ => null,
    };

    /**
     * GitHub's filter patterns, in order: a name is in when the last pattern that matches it is a
     * positive one (`!` negates). `*` stops at '/', `**` does not, `?` and `+` act on the character
     * before them, `[]` is a class, `\` escapes.
     */
    internal static bool FilterMatches(IReadOnlyList<string> patterns, string name)
    {
        bool matched = false;
        foreach (var raw in patterns)
        {
            bool negate = raw.StartsWith('!');
            if (FilterRegex(negate ? raw[1..] : raw) is { } r && r.IsMatch(name)) matched = !negate;
        }
        return matched;
    }

    private static Regex? FilterRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length) sb.Append(Regex.Escape(pattern[++i].ToString()));
            else if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*') { sb.Append(".*"); i++; }
            else if (c == '*') sb.Append("[^/]*");
            else if ((c == '?' || c == '+') && sb.Length > 1) sb.Append(c);
            else if (c == '[' && pattern.IndexOf(']', i + 1) is var close && close > i + 1)
            {
                sb.Append('[').Append(pattern[(i + 1)..close].Replace("\\", "\\\\")).Append(']');
                i = close;
            }
            else sb.Append(Regex.Escape(c.ToString()));
        }
        try { return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant); }
        catch (ArgumentException) { return null; }   // `*+` and the like: nothing a version tag is matched by
    }

    /**
     * Just enough YAML to read a workflow's `on:` value: block maps and lists by indentation, flow
     * `[...]` and `{...}` (an entry `key: value` in a list is a map of one pair), plain scalars over
     * several lines, quoted scalars, comments, anchors and tags (dropped: they say nothing about the
     * value). A block scalar (`|`, `>`) is skipped as text, an alias is not followed: both read as
     * Unknown, which decides nothing. What it cannot take apart throws a FormatException — never a
     * loop: every step of a flow collection has to move on (review, 09.10.2026: `[cron: '0 6 * * 1']`
     * held the reader in place until the process ran out of memory).
     */
    private static class Trigger
    {
        public abstract record Node;
        public sealed record Scalar(string? Value) : Node;
        public sealed record Seq(List<Node> Items) : Node;
        public sealed record Map(List<(string Key, Node? Value)> Entries) : Node;
        /** Known to be there, not what it says: a block scalar, an alias. */
        public sealed record Unknown(string What) : Node;

        private sealed record Line(int Indent, string Text);

        /** `|` or `>`, with a chomping and an indentation indicator in either order. */
        private static readonly Regex BlockScalar = new(@"^[|>](?:[+-]?[1-9]?|[1-9][+-])$", RegexOptions.Compiled);

        /** The value of the top-level `on` key; null when the file has none. */
        public static Node? On(string yaml)
        {
            var lines = new List<Line>();
            foreach (var raw in yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string text = StripComment(raw).TrimEnd();
                if (text.Trim().Length == 0 || text.Trim() is "---" or "...") continue;
                int indent = text.Length - text.TrimStart(' ').Length;
                lines.Add(new Line(indent, text[indent..]));
            }
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Indent != 0 || SplitKey(lines[i].Text) is not { } kv || kv.Key != "on") continue;
                int next = i + 1;
                return ValueOf(kv.Tail, lines, ref next, 0);
            }
            return null;
        }

        /** The value after `key:` — inline, or the block below it (a list may sit at the key's own indent). */
        private static Node? ValueOf(string rest, List<Line> lines, ref int i, int keyIndent)
        {
            rest = WithoutProperties(rest);
            if (rest.Length > 0)
            {
                if (rest[0] == '*')
                    return new Unknown("an alias");
                // Its lines are text, whatever they look like: all of them sit deeper than the key.
                if (BlockScalar.IsMatch(rest))
                {
                    while (i < lines.Count && lines[i].Indent > keyIndent) i++;
                    return new Unknown("a block scalar");
                }
                // A plain scalar in block context keeps its brackets and commas: v[0-9]+.[0-9]+ is one
                // pattern. It goes on over the lines below that sit deeper than its key.
                if (rest[0] is not ('[' or '{' or '"' or '\''))
                {
                    string plain = rest;
                    while (i < lines.Count && lines[i].Indent > keyIndent) plain += " " + lines[i++].Text;
                    return new Scalar(plain is "~" or "null" or "Null" or "NULL" ? null : plain);
                }
                // A flow collection, or a quoted scalar, may run over several lines.
                string flow = rest;
                while (Open(flow) && i < lines.Count && lines[i].Indent > keyIndent) flow += " " + lines[i++].Text;
                int at = 0;
                var node = Flow(flow, ref at);
                if (flow[at..].Trim().Length > 0) throw new FormatException("text after a value: " + flow);
                return node;
            }
            // A value on the line below its key: `on:` then `  push`, or `  [push, pull_request]`.
            if (i < lines.Count && lines[i].Indent > keyIndent && !IsItem(lines[i].Text) && SplitKey(lines[i].Text) == null)
            {
                string below = lines[i++].Text;
                return ValueOf(below, lines, ref i, keyIndent);
            }
            if (i < lines.Count && (lines[i].Indent > keyIndent || (lines[i].Indent == keyIndent && IsItem(lines[i].Text))))
                return Block(lines, ref i, lines[i].Indent);
            return null;
        }

        private static Node Block(List<Line> lines, ref int i, int indent)
        {
            if (IsItem(lines[i].Text))
            {
                var items = new List<Node>();
                while (i < lines.Count && lines[i].Indent == indent && IsItem(lines[i].Text))
                {
                    string item = lines[i].Text[1..].TrimStart(' ');
                    int inner = indent + (lines[i].Text.Length - item.Length);
                    i++;
                    if (item.Length == 0)
                        items.Add(i < lines.Count && lines[i].Indent > indent ? Block(lines, ref i, lines[i].Indent) : new Scalar(null));
                    else if (item[0] is not ('[' or '{' or '"' or '\'' or '&' or '!' or '*' or '|' or '>') && SplitKey(item) is { } kv)
                    {
                        // `- key: value` opens a map whose further keys sit under the first one.
                        lines.Insert(i, new Line(inner, item));
                        items.Add(Block(lines, ref i, inner));
                    }
                    else items.Add(ValueOf(item, lines, ref i, indent) ?? new Scalar(null));
                }
                return new Seq(items);
            }
            var entries = new List<(string, Node?)>();
            while (i < lines.Count && lines[i].Indent == indent && !IsItem(lines[i].Text))
            {
                var kv = SplitKey(lines[i].Text) ?? throw new FormatException("not a key: " + lines[i].Text);
                i++;
                entries.Add((kv.Key, ValueOf(kv.Tail, lines, ref i, indent)));
            }
            if (i < lines.Count && lines[i].Indent > indent) throw new FormatException("unexpected indent: " + lines[i].Text);
            return new Map(entries);
        }

        private static bool IsItem(string text) => text == "-" || text.StartsWith("- ", StringComparison.Ordinal);

        private static bool IsBlank(char c) => c is ' ' or '\t';

        /** A value's anchor (&name) and tag (!name) taken off its front: neither says what the value is. */
        private static string WithoutProperties(string text)
        {
            string t = text;
            while (t.Length > 0 && t[0] is '&' or '!')
            {
                int end = t.IndexOfAny(new[] { ' ', '\t' });
                t = end < 0 ? "" : t[end..].TrimStart(' ', '\t');
            }
            return t;
        }

        /** `key: rest` with the key unquoted; null when the line is not a key. */
        private static (string Key, string Tail)? SplitKey(string text)
        {
            int at = 0;
            string key;
            if (text.Length > 0 && text[0] is '"' or '\'')
            {
                key = Quoted(text, ref at);
                while (at < text.Length && IsBlank(text[at])) at++;
                if (at >= text.Length || text[at] != ':') return null;
            }
            else
            {
                at = IndexOfColon(text);
                if (at < 0) return null;
                key = text[..at].Trim();
            }
            if (at + 1 < text.Length && !IsBlank(text[at + 1])) return null;
            return (key, text[(at + 1)..].Trim());
        }

        /** The first ': ' (or a ':' ending the text; a tab counts as a space) outside brackets — where a plain key ends. */
        private static int IndexOfColon(string text)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is '[' or '{') depth++;
                else if (text[i] is ']' or '}') depth--;
                else if (text[i] == ':' && depth == 0 && (i + 1 == text.Length || IsBlank(text[i + 1]))) return i;
            }
            return -1;
        }

        private static void SkipBlanks(string s, ref int at)
        {
            while (at < s.Length && IsBlank(s[at])) at++;
        }

        private static Node Flow(string s, ref int at)
        {
            SkipBlanks(s, ref at);
            // An anchor or a tag in front of a node: dropped, like in block context.
            while (at < s.Length && s[at] is '&' or '!')
            {
                while (at < s.Length && !IsBlank(s[at]) && s[at] is not (',' or '[' or ']' or '{' or '}')) at++;
                SkipBlanks(s, ref at);
            }
            if (at >= s.Length) return new Scalar(null);
            char c = s[at];
            if (c == '*')
            {
                while (at < s.Length && !IsBlank(s[at]) && s[at] is not (',' or ']' or '}')) at++;
                return new Unknown("an alias");
            }
            if (c is '[' or '{')
            {
                at++;
                var items = new List<Node>();
                var entries = new List<(string, Node?)>();
                while (true)
                {
                    SkipBlanks(s, ref at);
                    if (at >= s.Length) throw new FormatException("unclosed " + c);
                    if (s[at] is ']' or '}') { at++; break; }
                    int before = at;
                    var first = Flow(s, ref at);
                    SkipBlanks(s, ref at);
                    // `key: value` — every entry of a {...}; in a [...] a map of one pair, as in
                    // `schedule: [cron: '0 6 * * 1']`.
                    bool pair = at < s.Length && s[at] == ':';
                    Node? value = null;
                    if (pair) { at++; value = Flow(s, ref at); }
                    string key = first is Scalar { Value: { } k } ? k : "";
                    if (c == '{') entries.Add((key, value));
                    else items.Add(pair ? new Map(new List<(string, Node?)> { (key, value) }) : first);
                    SkipBlanks(s, ref at);
                    if (at < s.Length && s[at] == ',') at++;
                    // What this reader cannot take apart must not hold it in place: no step, no claim.
                    if (at == before) throw new FormatException("cannot read past: " + s[at..]);
                }
                return c == '[' ? new Seq(items) : new Map(entries);
            }
            if (c is '"' or '\'') return new Scalar(Quoted(s, ref at));
            int start = at;
            while (at < s.Length && s[at] is not (',' or ']' or '}') && !(s[at] == ':' && (at + 1 == s.Length || IsBlank(s[at + 1])))) at++;
            string plain = s[start..at].Trim();
            return new Scalar(plain is "" or "~" or "null" or "Null" or "NULL" ? null : plain);
        }

        private static string Quoted(string s, ref int at)
        {
            char q = s[at++];
            var sb = new System.Text.StringBuilder();
            while (at < s.Length)
            {
                char c = s[at++];
                if (q == '\'' && c == '\'' && at < s.Length && s[at] == '\'') { sb.Append('\''); at++; }
                else if (c == q) return sb.ToString();
                else if (q == '"' && c == '\\' && at < s.Length) sb.Append(s[at++]);
                else sb.Append(c);
            }
            throw new FormatException("unclosed quote");
        }

        /** The text up to a comment: '#' at the start or after a space, outside quotes. */
        private static string StripComment(string line)
        {
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else if (quote == '"' && c == '\\') i++;
                }
                else if (c is '"' or '\'' && (i == 0 || line[i - 1] is ' ' or '\t' or '[' or '{' or ',' or ':')) quote = c;
                else if (c == '#' && (i == 0 || IsBlank(line[i - 1]))) return line[..i];
            }
            return line;
        }

        /**
         * Whether a bracket or a quote is still open at the end of the text. A quote opens a scalar only
         * where a node starts — at the front, or after [ { , or : — so the apostrophe in `[it's]` does not.
         */
        private static bool Open(string s)
        {
            int depth = 0;
            char quote = '\0';
            char last = '\0';   // the last character outside quotes that is not a blank
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (quote == '\'' && c == '\'' && i + 1 < s.Length && s[i + 1] == '\'') i++;
                    else if (quote == '"' && c == '\\') i++;
                    else if (c == quote) { quote = '\0'; last = c; }
                    continue;
                }
                if (c is '"' or '\'' && last is '\0' or '[' or '{' or ',' or ':') quote = c;
                else if (c is '[' or '{') depth++;
                else if (c is ']' or '}') depth--;
                if (!IsBlank(c) && quote == '\0') last = c;
            }
            return depth > 0 || quote != '\0';
        }
    }

    private static string? Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception) { return null; }
    }

    // Verified on DirectCladdingOperation (08.10.2026): the pack step for real, the YAML parsed and the
    // version step tested. Kept in step with the template's publish.yml where they do the same thing.
    private const string Template = """
        # Publishes this extension to the ENCY Extension Store (https://apps.encycam.com).
        #   - push a tag vX.Y.Z         -> publishes version X.Y.Z
        #   - Actions -> Run workflow   -> publishes the version you type; tick "dry run" to only check it
        # No secrets: the run signs in to the store with its own GitHub token (OIDC). The first publish
        # binds this repository to the extension: start it as the extension's owner in the store, with
        # that GitHub account linked there (apps.encycam.com -> Profile -> Sign in with GitHub). After
        # that every run from this repository is credited to that owner, whoever starts it.
        # Added by ency-extension-mcp (connect_to_store); the other workflows here were left as they were.

        name: Publish to ENCY Extension Store

        on:
          push:
            tags: ['v*']
          workflow_dispatch:
            inputs:
              version:
                description: 'Version to publish, e.g. 3.0.1'
                required: true
              dry-run:
                description: 'Only check the package on the store, publish nothing'
                type: boolean
                default: false

        permissions:
          contents: read
          id-token: write

        jobs:
          publish:
            runs-on: windows-latest
            steps:
              - uses: actions/checkout@v4

              - uses: actions/setup-dotnet@v4
                with:
                  dotnet-version: '__DOTNET__'

              - name: Resolve version
                shell: bash
                env:
                  TYPED_VERSION: ${{ inputs.version }}
                run: |
                  if [ "$GITHUB_REF_TYPE" = "tag" ]; then v="${GITHUB_REF_NAME#v}"; else v="${TYPED_VERSION#v}"; fi
                  if [[ ! "$v" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
                    echo "::error::'$v' is not a version like 3.0.1"
                    exit 1
                  fi
                  echo "PKG_VERSION=$v" >> "$GITHUB_ENV"

              # Release: the build ENCY installs. Runtime files go under build/ in the package
              # (PackagePath="build\"), never contentFiles/ - ENCY's store installer keeps that folder as it is.
              # The ENCY Nexus is a source because some EncySoftware.CAMAPI.SDK.Net versions are not on
              # nuget.org. Without it NuGet silently takes the next version up (warning NU1603), and that
              # build does not load in the ENCY release it targets, so NU1603 is made an error here.
              - name: Pack
                shell: bash
                run: |
                  dotnet pack "__CSPROJ__" -c Release \
                    -p:Version="$PKG_VERSION" -p:WarningsAsErrors=NU1603 \
                    --source https://api.nuget.org/v3/index.json \
                    --source https://nexus.encycam.com/repository/master/index.json \
                    -o out

              - name: Publish to the ENCY Extension Store
                uses: EncySoftware/publish-extension-action@v1
                with:
                  nupkg: out/*.nupkg
                  dry-run: ${{ github.event_name == 'workflow_dispatch' && inputs.dry-run }}

        """;
}
