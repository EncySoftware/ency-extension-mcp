using System.Reflection;
using EncyExtensionMcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

/**
 * Every tool class has to be buildable the way the MCP SDK builds it: anew for each call, through
 * ActivatorUtilities.CreateInstance over the server's services. On 08.10.2026 seven of the thirteen
 * tools answered "An error occurred invoking ..." over MCP - their constructors wanted delegates the
 * container does not hold - while every unit test, which builds the classes by hand, passed.
 */
public class ToolConstructionTests
{
    public static IEnumerable<object[]> ToolTypes() =>
        typeof(ServerSetup).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .OrderBy(t => t.Name)
            .Select(t => new object[] { t.Name });

    [Theory, MemberData(nameof(ToolTypes))]
    public void The_sdk_can_build_the_tool_class(string toolType)
    {
        var type = typeof(ServerSetup).Assembly.GetTypes().Single(t => t.Name == toolType);
        using var services = new ServiceCollection().AddStoreServices().BuildServiceProvider();

        object instance = ActivatorUtilities.CreateInstance(services, type);

        Assert.IsType(type, instance);
    }

    /** The theory above proves nothing if it finds no classes to build. */
    [Fact]
    public void The_tool_classes_are_found()
    {
        var names = ToolTypes().Select(t => (string)t[0]).ToList();
        Assert.Contains("FolderPublishTools", names);
        Assert.Contains("LocalPublishTools", names);
        Assert.Contains("ExtensionStoreTools", names);
    }
}
