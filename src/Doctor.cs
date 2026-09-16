using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace EncyExtensionMcp;

/**
 * One look at the machine: the tool's version, the programs each route needs, the store, the sign-in
 * and the editor's config — with the fix next to whatever is missing. Written for the questions that
 * kept arriving in chat ("is it installed?", "why does publish say sign in?"), which are answered by
 * reading these six lines rather than by a round of messages (16.09.2026).
 */
public static class Doctor
{
    public static async Task<string> Report(IProcessRunner proc, IStoreClient store, IStoreAuth auth,
                                            IUpdateCheck updates, string? mcpConfigPath, string toolVersion)
    {
        var sb = new StringBuilder();

        string? update = await updates.Note();
        sb.AppendLine($"tool:     ency-extension-mcp {toolVersion}" + (update != null ? " — " + update[(update.IndexOf(';') + 1)..].Trim() : ""));

        var dotnet = await proc.Run("dotnet", "--version");
        sb.AppendLine(dotnet.Ok ? $"dotnet:   SDK {dotnet.StdOut.Trim()}"
                                : "dotnet:   NOT FOUND — install the .NET 8 SDK from https://dot.net (every route builds with it)");

        var git = await proc.Run("git", "--version");
        sb.AppendLine(git.Ok ? $"git:      {git.StdOut.Trim()}"
                             : "git:      not found — needed by create_extension_repo and publish_extension only; publish_folder and publish_package work without it");

        var gh = await proc.Run("gh", "--version");
        if (!gh.Ok)
            sb.AppendLine("gh:       not found — needed by create_extension_repo only; publish_folder and publish_package work without it");
        else
        {
            var status = await proc.Run("gh", "auth status");
            string first = gh.StdOut.Split('\n')[0].Trim();
            sb.AppendLine(status.Ok ? $"gh:       {first}, signed in" : $"gh:       {first}, NOT signed in — only create_extension_repo needs it (`gh auth login`)");
        }

        try
        {
            var categories = await store.GetCategories();
            sb.AppendLine($"store:    {store.StoreBaseUrl} — reachable ({categories.Count} categories)");
        }
        catch (Exception e)
        {
            sb.AppendLine($"store:    {store.StoreBaseUrl} — UNREACHABLE ({e.Message}); check the network or a proxy");
        }

        try
        {
            string? token = await auth.GetAccessToken();
            if (token == null)
                sb.AppendLine("sign-in:  not signed in — run `ency-extension-mcp login` (the browser opens the ENCY sign-in page)");
            else
            {
                var who = Identity(token);
                sb.AppendLine($"sign-in:  {who.User ?? "signed in"}" + (who.Client != null ? $" via client {who.Client}" : ""));
            }
        }
        catch (Exception e)
        {
            sb.AppendLine($"sign-in:  EXPIRED — {e.Message}");
        }

        if (mcpConfigPath == null)
            sb.AppendLine("editor:   (no MCP config path to check)");
        else if (!File.Exists(mcpConfigPath))
            sb.AppendLine($"editor:   {mcpConfigPath} not found — run `ency-extension-mcp setup` to register the server in Cursor");
        else if (File.ReadAllText(mcpConfigPath).Contains($"\"{SetupCommand.ServerName}\""))
            sb.AppendLine($"editor:   {mcpConfigPath} lists {SetupCommand.ServerName}");
        else
            sb.AppendLine($"editor:   {mcpConfigPath} does not list {SetupCommand.ServerName} — run `ency-extension-mcp setup`");

        return sb.ToString().TrimEnd();
    }

    /** Who a store token belongs to and which Keycloak client minted it — read off the JWT, unverified. */
    public static (string? User, string? Client) Identity(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return (null, null);
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Str("preferred_username") ?? Str("email") ?? Str("name"), Str("azp"));
        }
        catch (Exception) { return (null, null); }
    }
}

[McpServerToolType]
public class DoctorTools(IProcessRunner proc, IStoreClient store, IStoreAuth auth, IUpdateCheck? updates = null)
{
    private readonly IUpdateCheck updates = updates ?? new NoUpdateCheck();

    [McpServerTool(Name = "doctor"), Description(
        "Check this machine for the ENCY store tools: the tool's version and whether a newer one exists, " +
        "the .NET SDK, git and gh (and which routes need them), whether the store answers, who is signed " +
        "in and through which client, and whether Cursor lists the server. Each missing piece comes with " +
        "the command that fixes it. Call it first when a publish fails for no clear reason.")]
    public Task<string> Run() =>
        Doctor.Report(proc, store, auth, updates, SetupCommand.DefaultCursorConfigPath, UpdateCheck.Current);
}
