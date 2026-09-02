using EncyExtensionMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// `ency-extension-mcp login` — one-time store sign-in through the browser (no MCP involved).
// `--password` falls back to typing an email and password here, for a machine or a Keycloak client
// where the browser round-trip cannot work.
if (args.Length > 0 && args[0].Equals("login", StringComparison.OrdinalIgnoreCase))
{
    var provider = new StoreTokenProvider();
    bool console = args.Contains("--password", StringComparer.OrdinalIgnoreCase)
                || args.Contains("--console", StringComparer.OrdinalIgnoreCase);
    return console ? await provider.LoginInteractive() : await provider.LoginBrowser();
}

// `ency-extension-mcp claim <PackageId> <owner/repo>` — bind a repo so its CI publishes without a secret.
if (args.Length > 0 && args[0].Equals("claim", StringComparison.OrdinalIgnoreCase))
{
    var tokenProvider = new StoreTokenProvider();
    return await ClaimCommand.Run(args, new StoreClient(), tokenProvider.GetAccessToken, Console.WriteLine);
}

// `ency-extension-mcp setup [--no-login]` — register in the editor's MCP config and log in.
if (args.Length > 0 && args[0].Equals("setup", StringComparison.OrdinalIgnoreCase))
{
    var tokenProvider = new StoreTokenProvider();
    bool console = args.Contains("--password", StringComparer.OrdinalIgnoreCase);
    return await SetupCommand.Run(SetupCommand.DefaultCursorConfigPath, new ProcessRunner(),
        () => File.Exists(StoreTokenProvider.AuthFilePath),
        console ? tokenProvider.LoginInteractive : tokenProvider.LoginBrowser,
        args.Contains("--no-login", StringComparer.OrdinalIgnoreCase), Console.WriteLine);
}

// `ency-extension-mcp publish-folder <Name> [folder] [--no-wait]` — the same route as the MCP tool
// publish_folder, for a terminal or a script: no git, no gh, the store does the GitHub work.
if (args.Length > 1 && args[0].Equals("publish-folder", StringComparison.OrdinalIgnoreCase))
{
    var tokenProvider = new StoreTokenProvider();
    var tools = new FolderPublishTools(new StoreClient(), tokenProvider, FolderPublishTools.OpenUrl, Task.Delay,
        Console.Error.WriteLine);
    string? folderArg = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--"));
    bool wait = !args.Contains("--no-wait", StringComparer.OrdinalIgnoreCase);
    string result = await tools.PublishFolder(args[1], folderArg, wait);
    Console.WriteLine(result);
    return result.StartsWith("ERROR") ? 1 : 0;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol — all logging must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IStoreClient, StoreClient>();
builder.Services.AddSingleton<StoreTokenProvider>();
builder.Services.AddSingleton<IStoreAuth>(sp => sp.GetRequiredService<StoreTokenProvider>());
builder.Services.AddSingleton<ExtensionStoreTools>();
// The folder route talks to the browser and waits between polls; both are handed in so a test can
// replace them, and every word goes to stderr — stdout is the MCP protocol.
builder.Services.AddSingleton(sp => new FolderPublishTools(
    sp.GetRequiredService<IStoreClient>(), sp.GetRequiredService<IStoreAuth>(),
    FolderPublishTools.OpenUrl, Task.Delay, s => Console.Error.WriteLine(s)));
builder.Services.AddSingleton<GuideTools>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ExtensionStoreTools>()
    .WithTools<FolderPublishTools>()
    .WithTools<GuideTools>();

await builder.Build().RunAsync();
return 0;
