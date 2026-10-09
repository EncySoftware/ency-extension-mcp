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
     * Whether pushing a version tag (vX.Y.Z) starts this workflow, from the structure of its `on:` block
     * — a string, a list or a map — never from a `tags:` line anywhere in the file (a docker step's
     * `with: tags:` is one). GitHub's rule: on.push with tags runs for a tag they match, with
     * tags-ignore for a tag they do not; on.push with none of branches, branches-ignore, tags and
     * tags-ignore runs for every tag (path filters are not applied to tag pushes); on.push with only
     * branches or branches-ignore never runs for a tag. `create` runs for every new tag. False when
     * the block cannot be read: then nothing is claimed.
     */
    public static bool StartsOnVersionTag(string yaml)
    {
        // A reader of a subset: whatever it cannot read is no claim, never a failed answer.
        try { return StartsOnVersionTag(Trigger.On(yaml)); }
        catch (Exception) { return false; }
    }

    private static bool StartsOnVersionTag(Trigger.Node? on)
    {
        if (on == null) return false;
        var events = new Dictionary<string, Trigger.Node?>(StringComparer.Ordinal);
        switch (on)
        {
            case Trigger.Scalar s when s.Value != null: events[s.Value] = null; break;
            case Trigger.Seq q:
                foreach (var item in q.Items) if (item is Trigger.Scalar { Value: { } name }) events[name] = null;
                break;
            case Trigger.Map m:
                foreach (var (key, value) in m.Entries) events[key] = value;
                break;
        }
        if (events.ContainsKey("create")) return true;
        if (!events.TryGetValue("push", out var push)) return false;
        if (push is null or Trigger.Scalar { Value: null }) return true;
        if (push is not Trigger.Map filters) return false;

        var keys = new Dictionary<string, Trigger.Node?>(StringComparer.Ordinal);
        foreach (var (key, value) in filters.Entries) keys[key] = value;
        if (keys.TryGetValue("tags", out var tags))
            return Patterns(tags) is { } p && VersionTags.Any(t => FilterMatches(p, t));
        if (keys.TryGetValue("tags-ignore", out var ignore))
            return Patterns(ignore) is { } p && VersionTags.Any(t => !FilterMatches(p, t));
        return !keys.ContainsKey("branches") && !keys.ContainsKey("branches-ignore");
    }

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
     * `[...]` and `{...}`, plain and quoted scalars, comments. Anchors, tags and block scalars are not
     * read — an `on:` block does not use them; a FormatException says this one did.
     */
    private static class Trigger
    {
        public abstract record Node;
        public sealed record Scalar(string? Value) : Node;
        public sealed record Seq(List<Node> Items) : Node;
        public sealed record Map(List<(string Key, Node? Value)> Entries) : Node;

        private sealed record Line(int Indent, string Text);

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
            if (rest.Length > 0)
            {
                // A plain scalar in block context keeps its brackets and commas: v[0-9]+.[0-9]+ is one pattern.
                if (rest[0] is not ('[' or '{' or '"' or '\''))
                    return new Scalar(rest is "~" or "null" or "Null" or "NULL" ? null : rest);
                // A flow collection may run over several lines.
                string flow = rest;
                if (flow[0] is '[' or '{')
                    while (Depth(flow) > 0 && i < lines.Count) flow += " " + lines[i++].Text;
                int at = 0;
                var node = Flow(flow, ref at);
                if (flow[at..].Trim().Length > 0) throw new FormatException("text after a value: " + flow);
                return node;
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
                    else if (item[0] is not ('[' or '{' or '"' or '\'') && SplitKey(item) is { } kv)
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

        /** `key: rest` with the key unquoted; null when the line is not a key. */
        private static (string Key, string Tail)? SplitKey(string text)
        {
            int at = 0;
            string key;
            if (text.Length > 0 && text[0] is '"' or '\'')
            {
                key = Quoted(text, ref at);
                while (at < text.Length && text[at] == ' ') at++;
                if (at >= text.Length || text[at] != ':') return null;
            }
            else
            {
                at = IndexOfColon(text);
                if (at < 0) return null;
                key = text[..at].Trim();
            }
            if (at + 1 < text.Length && text[at + 1] != ' ') return null;
            return (key, text[(at + 1)..].Trim());
        }

        /** The first ': ' (or a ':' ending the text) outside brackets — where a plain key ends. */
        private static int IndexOfColon(string text)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is '[' or '{') depth++;
                else if (text[i] is ']' or '}') depth--;
                else if (text[i] == ':' && depth == 0 && (i + 1 == text.Length || text[i + 1] == ' ')) return i;
            }
            return -1;
        }

        private static Node Flow(string s, ref int at)
        {
            while (at < s.Length && s[at] == ' ') at++;
            if (at >= s.Length) return new Scalar(null);
            char c = s[at];
            if (c is '[' or '{')
            {
                at++;
                var items = new List<Node>();
                var entries = new List<(string, Node?)>();
                while (true)
                {
                    while (at < s.Length && s[at] == ' ') at++;
                    if (at >= s.Length) throw new FormatException("unclosed " + c);
                    if (s[at] is ']' or '}') { at++; break; }
                    if (c == '{')
                    {
                        var key = Flow(s, ref at) as Scalar;
                        while (at < s.Length && s[at] == ' ') at++;
                        Node? value = null;
                        if (at < s.Length && s[at] == ':') { at++; value = Flow(s, ref at); }
                        entries.Add((key?.Value ?? "", value));
                    }
                    else items.Add(Flow(s, ref at));
                    while (at < s.Length && s[at] == ' ') at++;
                    if (at < s.Length && s[at] == ',') at++;
                }
                return c == '[' ? new Seq(items) : new Map(entries);
            }
            if (c is '"' or '\'') return new Scalar(Quoted(s, ref at));
            int start = at;
            while (at < s.Length && s[at] is not (',' or ']' or '}') && !(s[at] == ':' && (at + 1 == s.Length || s[at + 1] == ' '))) at++;
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
                else if (c is '"' or '\'' && (i == 0 || line[i - 1] is ' ' or '[' or '{' or ',' or ':')) quote = c;
                else if (c == '#' && (i == 0 || line[i - 1] is ' ' or '\t')) return line[..i];
            }
            return line;
        }

        private static int Depth(string s)
        {
            int depth = 0;
            char quote = '\0';
            foreach (char c in s)
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '"' or '\'') quote = c;
                else if (c is '[' or '{') depth++;
                else if (c is ']' or '}') depth--;
            }
            return depth;
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
