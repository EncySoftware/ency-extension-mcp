using EncyExtensionMcp;
using Xunit;

namespace EncyExtensionMcp.Tests;

/// <summary>Exactly one case is worth fixing - a pin from the future. What was built against an
/// older SDK loads in newer applications, the other way round loads nowhere, so "older than
/// recommended" has to be left alone.</summary>
public class SdkPinTests
{
    [Theory]
    [InlineData("3.0.9", "3.0.8", true)]    // the EncyPulse case, exactly
    [InlineData("3.0.10", "3.0.8", true)]   // compared as numbers, not as strings
    [InlineData("3.0.8", "3.0.8", false)]
    [InlineData("3.0.5", "3.0.8", false)]   // older is legitimate - left alone
    [InlineData("3.0.1-rc.22", "3.0.8", false)]
    [InlineData(null, "3.0.8", false)]      // nothing to fix
    [InlineData("3.0.9", null, false)]      // the store is silent - nothing is invented
    public void OnlyAPinFromTheFutureIsWorthTouching(string? pinned, string? recommended, bool expected) =>
        Assert.Equal(expected, SdkPin.IsFromTheFuture(pinned, recommended));

    [Fact]
    public void ReadsAndRewritesTheManifest()
    {
        const string json = """{ "packageId": "X", "sdkVersion": "3.0.9", "category": "other" }""";

        Assert.Equal("3.0.9", SdkPin.ReadInfoJson(json));
        string fixedUp = SdkPin.WriteInfoJson(json, "3.0.8");
        Assert.Equal("3.0.8", SdkPin.ReadInfoJson(fixedUp));
        Assert.Contains("\"packageId\": \"X\"", fixedUp);   // nothing else touched
    }

    /// <summary>The manifest names the SDK version twice, and the NuGet client resolves the second
    /// one at install: fixing only the first means building against one version and asking for another.</summary>
    [Fact]
    public void FixesBothPlacesTheManifestNamesTheSdk()
    {
        const string json = """
            {
              "packageId": "X",
              "targetFramework": "net10.0",
              "sdkVersion": "3.0.9",
              "dependencies": [ { "id": "EncySoftware.CAMAPI.SDK.Net", "version": "3.0.9" } ]
            }
            """;

        Assert.Equal("3.0.9", SdkPin.ReadInfoJsonDependency(json));
        string fixedUp = SdkPin.WriteInfoJson(json, "3.0.8");

        Assert.Equal("3.0.8", SdkPin.ReadInfoJson(fixedUp));
        Assert.Equal("3.0.8", SdkPin.ReadInfoJsonDependency(fixedUp));
        Assert.DoesNotContain("3.0.9", fixedUp);
        Assert.Contains("\"targetFramework\": \"net10.0\"", fixedUp);   // nothing else touched
    }

    /// <summary>Another dependency with a version field of its own keeps it.</summary>
    [Fact]
    public void LeavesEveryOtherDependencyAlone()
    {
        const string json = """
            { "sdkVersion": "3.0.9", "dependencies": [
                { "id": "Newtonsoft.Json", "version": "13.0.3" },
                { "id": "EncySoftware.CAMAPI.SDK.Net", "version": "3.0.9" } ] }
            """;

        string fixedUp = SdkPin.WriteInfoJson(json, "3.0.8");

        Assert.Contains("\"id\": \"Newtonsoft.Json\", \"version\": \"13.0.3\"", fixedUp);
        Assert.Equal("3.0.8", SdkPin.ReadInfoJsonDependency(fixedUp));
    }

    [Fact]
    public void RewritesTheProjectPinAsExact()
    {
        const string csproj = """
            <Project><ItemGroup>
              <PackageReference Include="EncySoftware.CAMAPI.Sdk.Net" Version="3.0.9" />
            </ItemGroup></Project>
            """;

        Assert.Equal("3.0.9", SdkPin.ReadCsproj(csproj));
        string fixedUp = SdkPin.WriteCsproj(csproj, "3.0.8");
        // The brackets are required: without them NuGet reads "no lower than" and the next restore raises it.
        Assert.Contains("Version=\"[3.0.8]\"", fixedUp);
        Assert.Equal("3.0.8", SdkPin.ReadCsproj(fixedUp));
    }

    [Fact]
    public void AnAlreadyExactPinIsReadWithoutItsBrackets()
    {
        const string csproj = """<PackageReference Include="EncySoftware.CAMAPI.Sdk.Net" Version="[3.0.8]" />""";
        Assert.Equal("3.0.8", SdkPin.ReadCsproj(csproj));
    }

    [Fact]
    public void AProjectWithoutTheSdkIsLeftAlone()
    {
        const string csproj = """<Project><ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup></Project>""";
        Assert.Null(SdkPin.ReadCsproj(csproj));
        Assert.Equal(csproj, SdkPin.WriteCsproj(csproj, "3.0.8"));
    }
}
