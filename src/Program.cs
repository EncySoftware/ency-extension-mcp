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

// `ency-extension-mcp create-folder <Name> [dir] [--category <id>]` — the project from the template, on
// this machine, renamed; no GitHub account needed to start.
if (args.Length > 1 && args[0].Equals("create-folder", StringComparison.OrdinalIgnoreCase))
{
    var tools = new FolderPublishTools(new StoreClient(), new StoreTokenProvider(), FolderPublishTools.OpenUrl, Task.Delay,
        Console.Error.WriteLine);
    int catAt = Array.FindIndex(args, a => a.Equals("--category", StringComparison.OrdinalIgnoreCase));
    string? cat = catAt > 0 && catAt + 1 < args.Length ? args[catAt + 1] : null;
    string? dir = args.Skip(2).Where((a, i) => !a.StartsWith("--") && (catAt < 0 || i + 2 != catAt + 1)).FirstOrDefault();
    string result = await tools.CreateExtensionFolder(args[1], dir, cat);
    Console.WriteLine(result);
    return result.StartsWith("ERROR") ? 1 : 0;
}

// `ency-extension-mcp update-extension [folder]` — the same as the MCP tool update_extension, for a
// terminal or a script. Without it an assistant that drives the tool as a COMMAND rather than as an
// MCP server had no way to bring an extension in line, and published it with the old SDK pin
// unchanged — which is exactly what happened on 04.09.2026.
if (args.Length > 0 && args[0].Equals("update-extension", StringComparison.OrdinalIgnoreCase))
{
    var tools = new FolderPublishTools(new StoreClient(), new StoreTokenProvider(), FolderPublishTools.OpenUrl, Task.Delay,
        Console.Error.WriteLine);
    string? folderArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
    string result = await tools.UpdateExtension(folderArg);
    Console.WriteLine(result);
    return result.StartsWith("ERROR") ? 1 : 0;
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

// `ency-extension-mcp publish-package [path] [--version X] [--category id]` — the MCP tool
// publish_package for a terminal: publish what was built on this machine, no git, no GitHub. It
// exists for the same reason as update-extension: an assistant that drives the tool as a COMMAND
// rather than as an MCP server has no other way onto this route.
if (args.Length > 0 && args[0].Equals("publish-package", StringComparison.OrdinalIgnoreCase))
{
    var tools = new LocalPublishTools(new StoreClient(), new StoreTokenProvider(), Console.Error.WriteLine, new UpdateCheck());
    string? pathArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
    string? Value(string name)
    {
        int i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
    string result = await tools.PublishPackage(pathArg, Value("version"), Value("category"));
    Console.WriteLine(result);
    return result.StartsWith("ERROR") ? 1 : 0;
}

// `ency-extension-mcp check-package [path]` — the MCP tool check_package for a terminal: what a ready
// .nupkg is missing, before anything is uploaded.
if (args.Length > 0 && args[0].Equals("check-package", StringComparison.OrdinalIgnoreCase))
{
    var tools = new LocalPublishTools(new StoreClient(), new StoreTokenProvider(), Console.Error.WriteLine);
    string result = await tools.CheckPackage(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")));
    Console.WriteLine(result);
    return result.StartsWith("ERROR") ? 1 : 0;
}

// `ency-extension-mcp doctor` — this machine's setup for the store tools, each gap with its fix.
if (args.Length > 0 && args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(await Doctor.Report(new ProcessRunner(), new StoreClient(), new StoreTokenProvider(), new UpdateCheck(),
        SetupCommand.DefaultCursorConfigPath, UpdateCheck.Current));
    return 0;
}

// `ency-extension-mcp my-extensions` — the MCP tool my_extensions for a terminal.
if (args.Length > 0 && args[0].Equals("my-extensions", StringComparison.OrdinalIgnoreCase))
{
    string result = await new AccountTools(new StoreClient(), new StoreTokenProvider()).MyExtensions();
    Console.WriteLine(result);
    return result.StartsWith("ERROR") ? 1 : 0;
}

// `ency-extension-mcp version` — what is running, and whether nuget.org has something newer.
if (args.Length > 0 && (args[0].Equals("version", StringComparison.OrdinalIgnoreCase) || args[0] == "--version"))
{
    Console.WriteLine(await new UpdateCheck().Note() ?? $"ency-extension-mcp {UpdateCheck.Current}");
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol — all logging must go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IStoreClient, StoreClient>();
builder.Services.AddSingleton<StoreTokenProvider>();
builder.Services.AddSingleton<IStoreAuth>(sp => sp.GetRequiredService<StoreTokenProvider>());
builder.Services.AddSingleton<IUpdateCheck, UpdateCheck>();
builder.Services.AddSingleton<ExtensionStoreTools>();
builder.Services.AddSingleton<DoctorTools>();
builder.Services.AddSingleton<AccountTools>();
// The folder route talks to the browser and waits between polls; both are handed in so a test can
// replace them, and every word goes to stderr — stdout is the MCP protocol.
builder.Services.AddSingleton(sp => new FolderPublishTools(
    sp.GetRequiredService<IStoreClient>(), sp.GetRequiredService<IStoreAuth>(),
    FolderPublishTools.OpenUrl, Task.Delay, s => Console.Error.WriteLine(s), null, sp.GetRequiredService<IUpdateCheck>()));
builder.Services.AddSingleton(sp => new LocalPublishTools(
    sp.GetRequiredService<IStoreClient>(), sp.GetRequiredService<IStoreAuth>(), s => Console.Error.WriteLine(s),
    sp.GetRequiredService<IUpdateCheck>()));
builder.Services.AddSingleton<GuideTools>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ExtensionStoreTools>()
    .WithTools<FolderPublishTools>()
    .WithTools<LocalPublishTools>()
    .WithTools<DoctorTools>()
    .WithTools<AccountTools>()
    .WithTools<GuideTools>();

await builder.Build().RunAsync();
return 0;
