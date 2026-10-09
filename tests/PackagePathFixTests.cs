using System.Text;
using EncyExtensionMcp;
using Xunit;

/**
 * Moving a project's runtime files out of contentFiles/: the value NuGet needs is build\ plus the
 * item's folder in the build output, minus the folder NuGet appends by itself — and only the value
 * between the quotes changes, never another byte of the author's csproj.
 */
public class PackagePathFixTests
{
    private static readonly string ProjectDir = Path.Combine(Path.GetTempPath(), "mcp-pathfix", "project", "main");

    private static PackagePathPlan Plan(string items) => PackagePathFix.Plan(Csproj(items), ProjectDir);

    private static string Csproj(string items) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0-windows</TargetFramework>\n"
        + "  </PropertyGroup>\n  <ItemGroup>\n" + items + "\n  </ItemGroup>\n</Project>\n";

    /** The project that taught the rule: four values, all of them plain build\, the images' included. */
    [Fact]
    public void The_DirectCladding_csproj_gets_build_for_all_four_items_and_nothing_else_changes()
    {
        Assert.Equal(2208, DcoFixture.Bytes.Length);   // the fixture is the real file, byte for byte

        var plan = PackagePathFix.Plan(DcoFixture.Text, ProjectDir);

        Assert.Empty(plan.Problems);
        Assert.Equal(new[] { 25, 29, 34, 37 }, plan.Edits.Select(e => e.Line).ToArray());
        Assert.Equal(new[] { "DirectCladdingOperation.smarthint", "SmartHintImages\\*.png", "DirectCladdingOperation_ExtOp.xml",
                             "DirectCladdingOperationExtension.settings.json" }, plan.Edits.Select(e => e.Item).ToArray());
        Assert.All(plan.Edits, e => Assert.Equal("build\\", e.After));
        // NuGet appends the images' own folder; build\SmartHintImages\ would have doubled it again.
        Assert.Equal("contentFiles\\any\\any\\SmartHintImages\\", plan.Edits[1].Before);
        Assert.Equal(DcoFixture.FixedText, plan.Text);

        byte[] written = ConnectTools.Encode(plan.Text, bom: true);
        Assert.Equal(DcoFixture.FixedBytes, written);
        Assert.Equal(2132, written.Length);
        Assert.Equal(DcoFixture.Text.Count(c => c == '\r'), plan.Text.Count(c => c == '\r'));
    }

    [Fact]
    public void A_csproj_that_already_packs_under_build_is_left_exactly_as_it_is()
    {
        var plan = PackagePathFix.Plan(DcoFixture.FixedText, ProjectDir);

        Assert.Empty(plan.Edits);
        Assert.Empty(plan.Problems);
        Assert.Equal(DcoFixture.FixedText, plan.Text);
    }

    [Fact]
    public void A_Content_item_moves_like_a_None_item()
    {
        var plan = Plan("    <Content Include=\"MyExt.settings.json\" PackagePath=\"contentFiles\\any\\any\\\" CopyToOutputDirectory=\"Always\" />");
        Assert.Equal("build\\", Assert.Single(plan.Edits).After);
        Assert.Contains("<Content Include=\"MyExt.settings.json\" PackagePath=\"build\\\" CopyToOutputDirectory=\"Always\" />", plan.Text);
    }

    [Fact]
    public void Several_files_of_one_folder_in_one_item_share_one_value()
    {
        var plan = Plan("    <None Include=\"a.json;b.json\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\" />");
        Assert.Equal("build\\", Assert.Single(plan.Edits).After);
    }

    [Fact]
    public void Several_items_are_each_given_their_own_folder()
    {
        var plan = Plan("    <None Include=\"Images\\*.png\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\Images\\\" />\n"
                      + "    <None Include=\"Data\\**\\*.json\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\" />\n"
                      + "    <None Include=\"..\\shared\\common.json\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\" />");

        Assert.Empty(plan.Problems);
        // A wildcard in the file name adds no folder; ** adds what it matched; a file from outside the
        // project lands at the root of the output, as the SDK links it.
        Assert.Equal(new[] { "build\\Images\\", "build\\Data\\", "build\\" }, plan.Edits.Select(e => e.After).ToArray());
    }

    [Fact]
    public void Files_of_one_item_in_two_folders_are_reported_not_guessed()
    {
        var plan = Plan("    <None Include=\"a\\x.json;b\\y.json\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\" />");
        Assert.Empty(plan.Edits);
        Assert.Contains("split the item", Assert.Single(plan.Problems));
    }

    [Fact]
    public void Forward_slashes_stay_forward_slashes()
    {
        var plan = Plan("    <None Include=\"Images/*.png\" Pack=\"true\" PackagePath=\"contentFiles/any/any/Images/\" />");
        Assert.Equal("build/Images/", Assert.Single(plan.Edits).After);
        Assert.Contains("PackagePath=\"build/Images/\"", plan.Text);
    }

    [Fact]
    public void A_PackagePath_written_as_a_child_element_is_edited_in_place()
    {
        var plan = Plan("    <None Include=\"MyExt.settings.json\" Pack=\"true\">\n      <PackagePath>contentFiles\\any\\any\\</PackagePath>\n    </None>");
        Assert.Equal("build\\", Assert.Single(plan.Edits).After);
        Assert.Contains("<PackagePath>build\\</PackagePath>", plan.Text);
    }

    [Fact]
    public void Single_quotes_are_kept()
    {
        var plan = Plan("    <None Include='MyExt.settings.json' Pack='true' PackagePath='contentFiles\\any\\any\\' />");
        Assert.Contains("PackagePath='build\\'", plan.Text);
    }

    [Fact]
    public void A_link_puts_the_file_where_the_link_says()
    {
        var plan = Plan("    <None Include=\"..\\shared\\cfg.json\" Link=\"Config\\cfg.json\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\Config\\\" />\n"
                      + "    <None Include=\"..\\tree\\**\\*.txt\" Link=\"Tree\\%(RecursiveDir)%(Filename)%(Extension)\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\" />");
        Assert.Empty(plan.Problems);
        Assert.Equal(new[] { "build\\Config\\cfg.json", "build\\Tree\\" }, plan.Edits.Select(e => e.After).ToArray());
    }

    [Fact]
    public void A_value_that_already_installs_right_is_kept_and_the_contentFiles_one_dropped()
    {
        var plan = Plan("    <None Include=\"MyExt.settings.json\" Pack=\"true\" PackagePath=\"content\\;contentFiles\\any\\any\\\" />");
        Assert.Equal("content\\", Assert.Single(plan.Edits).After);
    }

    [Fact]
    public void What_cannot_be_read_off_the_text_is_reported_and_left_alone()
    {
        string items = "    <None Include=\"$(MSBuildThisFileDirectory)x.json\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\" />\n"
                     + "    <Content Include=\"MyExt.settings.json\" CopyToOutputDirectory=\"Always\" />\n"
                     + "    <None Include=\"y.json\" Pack=\"true\" packagepath=\"contentFiles\\any\\any\\\" />";
        var plan = Plan(items);

        Assert.Empty(plan.Edits);
        Assert.Equal(Csproj(items), plan.Text);
        Assert.Equal(3, plan.Problems.Count);
        Assert.Contains(plan.Problems, p => p.Contains("MSBuild properties"));
        Assert.Contains(plan.Problems, p => p.Contains("without a PackagePath") && p.Contains("PackagePath=\"build\\\""));
        Assert.Contains(plan.Problems, p => p.Contains("`packagepath`"));
    }

    [Fact]
    public void Comments_and_items_that_are_not_packed_are_not_touched()
    {
        string items = "    <!-- <None Include=\"c.json\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\" /> -->\n"
                     + "    <None Include=\"d.json\" Pack=\"false\" PackagePath=\"contentFiles\\any\\any\\\" />";
        var plan = Plan(items);
        Assert.Empty(plan.Edits);
        Assert.Empty(plan.Problems);
        Assert.Equal(Csproj(items), plan.Text);
    }

    /** An Update rides on the SDK's own **\* item, whose folder NuGet appends — unless the project switched that off. */
    [Fact]
    public void An_Update_with_the_default_items_off_is_reported()
    {
        string csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <EnableDefaultItems>false</EnableDefaultItems>\n"
                      + "  </PropertyGroup>\n  <ItemGroup>\n    <None Update=\"Hints\\*.png\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\Hints\\\" />\n"
                      + "  </ItemGroup>\n</Project>\n";
        var plan = PackagePathFix.Plan(csproj, ProjectDir);
        Assert.Empty(plan.Edits);
        Assert.Contains("default items off", Assert.Single(plan.Problems));
    }

    [Fact]
    public void Text_with_a_bom_round_trips_through_decode_and_encode()
    {
        Assert.True(ConnectTools.TryDecode(DcoFixture.Bytes, out string text, out bool bom, out _));
        Assert.True(bom);
        Assert.Equal(DcoFixture.Text, text);
        Assert.Equal(DcoFixture.Bytes, ConnectTools.Encode(text, bom));

        Assert.True(ConnectTools.TryDecode(Encoding.UTF8.GetBytes("<Project />"), out _, out bool none, out _));
        Assert.False(none);
        Assert.False(ConnectTools.TryDecode(new byte[] { 0xFF, 0xFE, 0x3C, 0x00 }, out _, out _, out string? error));
        Assert.Contains("UTF-16", error);
    }
}
