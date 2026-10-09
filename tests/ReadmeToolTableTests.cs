using System.Reflection;
using System.Text.RegularExpressions;
using EncyExtensionMcp;
using ModelContextProtocol.Server;
using Xunit;

/**
 * The table of tools at the top of README.md is what an author reads first, on GitHub and on the
 * nuget.org page the README is packed into. Nothing compared it with the tools the server offers,
 * and it fell behind unnoticed: on 09.10.2026 it named 9 of the 14 tools (the release before, 8 of 13).
 */
public class ReadmeToolTableTests
{
    [Fact]
    public void The_readme_table_names_every_tool_the_server_offers_and_no_other()
    {
        var offered = ToolNames();
        var listed = TableNames(File.ReadAllLines(ReadmePath()));
        Assert.Contains("connect_to_store", offered);   // proves the reflection below finds tools at all

        var missing = offered.Except(listed).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var notATool = listed.Except(offered).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var twice = listed.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(missing.Count == 0 && notATool.Count == 0 && twice.Count == 0,
            "README.md's table of tools - missing: [" + string.Join(", ", missing)
            + "], not a tool: [" + string.Join(", ", notATool)
            + "], listed twice: [" + string.Join(", ", twice) + "]");
    }

    /**
     * Every tool the way tools/list names it: the attribute's Name, or the method's own name when the
     * attribute has none - the rule the MCP SDK documents for McpServerToolAttribute.Name.
     */
    private static List<string> ToolNames() =>
        typeof(ServerSetup).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => (Method: m, Tool: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Tool != null)
            .Select(x => x.Tool!.Name ?? x.Method.Name)
            .Distinct()
            .ToList();

    /** The tool name in backticks that opens each row of the table headed "| Tool | What it does |". */
    private static List<string> TableNames(string[] lines)
    {
        int header = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^\|\s*Tool\s*\|\s*What it does\s*\|"));
        Assert.True(header >= 0, "README.md has no table headed '| Tool | What it does |'");

        var names = new List<string>();
        foreach (string line in lines.Skip(header + 1).TakeWhile(l => l.StartsWith('|')))
        {
            if (Regex.IsMatch(line, @"^\|[\s:|-]+$")) continue;   // the |---|---| line under the header
            var cell = Regex.Match(line, @"^\|\s*`([^`]+)`\s*\|");
            Assert.True(cell.Success, "a row of README.md's table of tools does not open with a tool name in backticks: " + line);
            names.Add(cell.Groups[1].Value);
        }
        Assert.NotEmpty(names);
        return names;
    }

    /** README.md at the root of the checkout: the first folder above the test binaries holding it beside src/. */
    private static string ReadmePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string readme = Path.Combine(dir.FullName, "README.md");
            if (File.Exists(readme) && File.Exists(Path.Combine(dir.FullName, "src", "EncyExtensionMcp.csproj")))
                return readme;
        }
        throw new Xunit.Sdk.XunitException("no README.md beside src/EncyExtensionMcp.csproj above " + AppContext.BaseDirectory);
    }
}
