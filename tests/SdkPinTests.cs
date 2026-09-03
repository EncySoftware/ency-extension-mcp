using EncyExtensionMcp;
using Xunit;

namespace EncyExtensionMcp.Tests;

/// <summary>Чинить надо ровно один случай — пин из будущего. Собранное под старый SDK грузится в
/// новых версиях, обратное не грузится нигде, поэтому «старее рекомендованного» трогать нельзя.</summary>
public class SdkPinTests
{
    [Theory]
    [InlineData("3.0.9", "3.0.8", true)]    // ровно случай EncyPulse
    [InlineData("3.0.10", "3.0.8", true)]   // сравнение числовое, а не строковое
    [InlineData("3.0.8", "3.0.8", false)]
    [InlineData("3.0.5", "3.0.8", false)]   // старее — законно, не трогаем
    [InlineData("3.0.1-rc.22", "3.0.8", false)]
    [InlineData(null, "3.0.8", false)]      // нечего чинить
    [InlineData("3.0.9", null, false)]      // стор молчит — не выдумываем
    public void OnlyAPinFromTheFutureIsWorthTouching(string? pinned, string? recommended, bool expected) =>
        Assert.Equal(expected, SdkPin.IsFromTheFuture(pinned, recommended));

    [Fact]
    public void ReadsAndRewritesTheManifest()
    {
        const string json = """{ "packageId": "X", "sdkVersion": "3.0.9", "category": "other" }""";

        Assert.Equal("3.0.9", SdkPin.ReadInfoJson(json));
        string fixedUp = SdkPin.WriteInfoJson(json, "3.0.8");
        Assert.Equal("3.0.8", SdkPin.ReadInfoJson(fixedUp));
        Assert.Contains("\"packageId\": \"X\"", fixedUp);   // остальное не тронуто
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
        // Скобки обязательны: без них NuGet читает «не ниже» и следующий restore поднимет версию сам.
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
