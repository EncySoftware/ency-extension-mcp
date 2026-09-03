using EncyExtensionMcp;
using Xunit;

namespace EncyExtensionMcp.Tests;

/// <summary>What is wrong with a folder before anyone builds it. Real files, because the whole point
/// is reading files an author edits by hand and a compiler never checks.</summary>
public class PreflightTests : IDisposable
{
    private const string TemplateReadme = "# EncyExtension\n\nDescribe your extension here.\n";
    private const string TemplateDescription = "Describe what your extension does";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcp-pre-" + Guid.NewGuid().ToString("N"));

    /// <summary>A folder as it looks when the author has done everything right.</summary>
    public PreflightTests()
    {
        Directory.CreateDirectory(_root);
        Write("MyExt.csproj", "<Project><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>");
        Write("package.info.json", """
            {
              "packageId": "MyExt",
              "targetFramework": "net10.0",
              "sdkVersion": "3.0.8",
              "description": "Counts holes and says how many",
              "author": "Andrey",
              "category": "analyzer"
            }
            """);
        Write("MyExt.settings.json", """
            {
              "name": "MyExt",
              "module_path": "${extensionJsonFolder}/MyExt.dll",
              "extensions": [ { "utility": { "name": "MyExt", "id": "Extension.Utility.MyExt" } } ]
            }
            """);
        Write("Extension.cs", "if (id == \"Extension.Utility.MyExt\") return new UtilityExtension();");
        Write("readme.md", "# MyExt\n\nCounts holes.\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* a temp folder that outlives the run is not a failure */ }
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_root, name), text);

    private IReadOnlyList<Finding> Check(string? name = "MyExt") =>
        Preflight.Check(_root, name, TemplateReadme, TemplateDescription);

    private string Texts() => string.Join(" | ", Check().Select(f => f.Text));

    [Fact]
    public void AFolderWithNothingWrongSaysNothing() => Assert.Empty(Check());

    /// <summary>The expensive one: a name of its own does not publish a new version, it creates a
    /// second extension and reserves the name for good.</summary>
    [Fact]
    public void APublishNameThatIsNotThePackageIdStopsTheWholeThing()
    {
        var stop = Assert.Single(Check("MyExtension").Where(f => f.Blocking));

        Assert.Contains("MyExtension", stop.Text);
        Assert.Contains("MyExt", stop.Text);
        Assert.Contains("SECOND", stop.Text);
    }

    [Fact]
    public void TheNameIsComparedWithoutCaseAndSkippedWhenNotGiven()
    {
        Assert.Empty(Preflight.Check(_root, "myext", TemplateReadme, TemplateDescription));
        Assert.Empty(Preflight.Check(_root, null, TemplateReadme, TemplateDescription));
    }

    /// <summary>The template's own words are what a reader of the store card would see.</summary>
    [Fact]
    public void TheTemplatesWordingIsNotACard()
    {
        Write("readme.md", TemplateReadme);
        Write("package.info.json", $$"""
            { "packageId": "MyExt", "description": "{{TemplateDescription}}", "author": "", "category": "other" }
            """);

        string all = Texts();

        Assert.Contains("readme.md is still the template's", all);
        Assert.Contains("description in package.info.json is still the template's", all);
        Assert.Contains("author", all);
        Assert.Contains("'other'", all);
        Assert.DoesNotContain(Check(), f => f.Blocking);   // sloppy, not fatal
    }

    /// <summary>Without the template to compare against, "untouched" cannot be told from "written",
    /// and the check goes quiet rather than guessing.</summary>
    [Fact]
    public void WithoutTheTemplateTheWordingIsNotJudged()
    {
        Write("readme.md", TemplateReadme);
        Assert.Empty(Preflight.Check(_root, "MyExt"));
    }

    /// <summary>ENCY asks the factory for exactly this string; nothing else ever compares them.</summary>
    [Fact]
    public void AnIdentifierNoSourceAnswersTo()
    {
        Write("MyExt.settings.json", """
            {
              "module_path": "${extensionJsonFolder}/MyExt.dll",
              "extensions": [ { "utility": { "id": "Extension.Utility.Renamed" } } ]
            }
            """);

        var f = Assert.Single(Check());

        Assert.Contains("Extension.Utility.Renamed", f.Text);
        Assert.Contains("install and never start", f.Text);
        Assert.False(f.Blocking);
    }

    [Fact]
    public void AModulePathPointingAtALibraryTheProjectDoesNotBuild()
    {
        Write("MyExt.settings.json", """
            {
              "module_path": "${extensionJsonFolder}/OldName.dll",
              "extensions": [ { "utility": { "id": "Extension.Utility.MyExt" } } ]
            }
            """);

        Assert.Contains("OldName.dll", Assert.Single(Check()).Text);
    }

    [Fact]
    public void AFrameworkTheManifestDoesNotName()
    {
        Write("package.info.json", """
            { "packageId": "MyExt", "targetFramework": "net8.0", "description": "d", "author": "a", "category": "analyzer" }
            """);

        Assert.Contains("net8.0", Assert.Single(Check()).Text);
    }

    /// <summary>The platform suffix belongs to the project and never to the manifest - that pair is right.</summary>
    [Fact]
    public void ThePlatformSuffixIsNotADisagreement() => Assert.Empty(Check());

    [Fact]
    public void AManifestThatIsNotJsonStopsThePublishAndNothingElseIsGuessed()
    {
        Write("package.info.json", "{ not json");

        var f = Assert.Single(Check());

        Assert.True(f.Blocking);
        Assert.Contains("package.info.json", f.Text);
    }

    [Fact]
    public void SettingsThatAreNotJsonStopThePublishToo()
    {
        Write("MyExt.settings.json", "{ nope");

        Assert.Contains(Check(), f => f.Blocking && f.Text.Contains("MyExt.settings.json"));
    }

    [Fact]
    public void AFolderWithNoManifestIsNotAnExtension()
    {
        File.Delete(Path.Combine(_root, "package.info.json"));

        var f = Assert.Single(Check());

        Assert.True(f.Blocking);
        Assert.Contains("no package.info.json", f.Text);
    }
}
