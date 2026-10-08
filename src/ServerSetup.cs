using Microsoft.Extensions.DependencyInjection;

namespace EncyExtensionMcp;

/**
 * The services the MCP server hands to its tools.
 *
 * <para>Only services, never the tool classes: the MCP SDK builds a tool class anew for every call
 * with ActivatorUtilities.CreateInstance, which asks the container for each constructor parameter and
 * never for the class itself. A singleton registered for a tool class is silently ignored, so whatever
 * it was meant to supply (a logger, a delay) has to be an optional parameter with a default. Kept in
 * one place so a test builds the classes exactly the way the SDK does.</para>
 */
public static class ServerSetup
{
    public static IServiceCollection AddStoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IStoreClient, StoreClient>();
        services.AddSingleton<StoreTokenProvider>();
        services.AddSingleton<IStoreAuth>(sp => sp.GetRequiredService<StoreTokenProvider>());
        services.AddSingleton<IUpdateCheck, UpdateCheck>();
        return services;
    }
}
