using System.IO.Compression;
using System.Text;
using EncyExtensionMcp;
using Xunit;

/**
 * Where ENCY's store installer puts each file of a package — copied from the installer, so these
 * cases are its cases — and the check of a package against the Release build it came from.
 */
public class InstallLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcp-layout-" + Guid.NewGuid().ToString("N")[..8]);

    public InstallLayoutTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("content/a/b.txt", "a/b.txt")]
    [InlineData("build/X.settings.json", "X.settings.json")]
    [InlineData("build/SmartHintImages/a.png", "SmartHintImages/a.png")]
    [InlineData("build/X.targets", null)]
    [InlineData("lib/net10.0-windows7.0/X.dll", "X.dll")]
    [InlineData("lib/X.dll", null)]
    [InlineData("contentFiles/any/any/X.settings.json", "contentFiles/any/any/X.settings.json")]
    [InlineData("README.md", "README.md")]
    [InlineData("[Content_Types].xml", null)]
    [InlineData("_rels/.rels", null)]
    [InlineData("package/services/metadata/core-properties/x.psmdcp", null)]
    [InlineData("X.nuspec", null)]
    [InlineData("build/.store.json", null)]
    public void Each_entry_lands_where_the_installer_puts_it(string entry, string? installed) =>
        Assert.Equal(installed, InstallLayout.PayloadRelativePath(entry));

    [Fact]
    public void Folders_backslashes_and_paths_that_leave_the_folder_are_handled_like_the_installer_does()
    {
        var placed = InstallLayout.Place(new[] { "build/", "build\\a\\b.txt", "build/../../evil.dll", "content/./c.txt" });
        Assert.Equal(new[] { "a/b.txt", "c.txt" }, placed.Select(p => p.Installed).ToArray());
    }

    [Fact]
    public void A_manifest_under_contentFiles_would_not_find_its_dll()
    {
        var names = new[] { "contentFiles/any/any/X.settings.json", "lib/net10.0/X.dll" };
        string? problem = InstallLayout.ManifestProblem(InstallLayout.Place(names), names,
            _ => "{\"module_path\": \"${extensionJsonFolder}/X.dll\"}");

        Assert.NotNull(problem);
        Assert.Contains("contentFiles/any/any/X.settings.json", problem);
        Assert.Contains("points at contentFiles/any/any/X.dll", problem);
        Assert.Contains("the dll lands at X.dll (from lib/net10.0/X.dll)", problem);
    }

    [Fact]
    public void A_manifest_under_build_finds_its_dll()
    {
        var names = new[] { "build/X.settings.json", "lib/net10.0/X.dll" };
        Assert.Null(InstallLayout.ManifestProblem(InstallLayout.Place(names), names,
            _ => "{\"module_path\": \"${extensionJsonFolder}/X.dll\"}"));
        // A module_path not relative to the manifest cannot be judged here, and is not.
        Assert.Null(InstallLayout.ManifestProblem(InstallLayout.Place(new[] { "contentFiles/any/any/X.settings.json", "lib/net10.0/X.dll" }),
            new[] { "contentFiles/any/any/X.settings.json" }, _ => "{\"module_path\": \"X.dll\"}"));
    }

    /** The shallowest manifest is the one registered: a stray copy deeper down does not break anything. */
    [Fact]
    public void The_shallowest_manifest_is_the_one_registered()
    {
        var placed = InstallLayout.Place(new[] { "contentFiles/any/net10.0/X.settings.json", "content/X.settings.json" });
        Assert.Equal("X.settings.json", InstallLayout.RegisteredManifest(placed)!.Installed);
    }

    [Fact]
    public void A_build_layout_lands_where_the_Release_build_has_every_file()
    {
        string nupkg = Path.Combine(_dir, "after.nupkg"), bin = Path.Combine(_dir, "bin");
        DcoFixture.WritePackage(nupkg, "build");
        DcoFixture.WriteBuildOutput(bin);

        var verdict = InstallLayout.Verify(nupkg, bin);

        Assert.Empty(verdict.Failures);
        Assert.Empty(verdict.Notes);
        Assert.Equal(DcoFixture.Runtime.Length, verdict.AtBuildPath);
        Assert.Equal(new[] { "README.md", "icon.png" }, verdict.NamedByNuspec.ToArray());
        Assert.Equal((DcoFixture.Manifest, DcoFixture.Dll), (verdict.Manifest, verdict.Module));
    }

    /** The package DirectCladdingOperation shipped: the manifest in contentFiles, the pictures in a doubled folder. */
    [Fact]
    public void A_contentFiles_layout_fails_and_says_where_each_file_lands()
    {
        string nupkg = Path.Combine(_dir, "before.nupkg"), bin = Path.Combine(_dir, "bin");
        DcoFixture.WritePackage(nupkg, "contentFiles");
        DcoFixture.WriteBuildOutput(bin);

        var verdict = InstallLayout.Verify(nupkg, bin);

        Assert.Contains(verdict.Failures, f => f.StartsWith("the manifest would not find its dll"));
        Assert.Contains(verdict.Failures, f => f.Contains("contentFiles/any/any/SmartHintImages/SmartHintImages/start-point.png")
                                               && f.Contains("the Release build has it at SmartHintImages/start-point.png"));
        Assert.Equal(5, verdict.Failures.Count(f => f.Contains("keeps contentFiles/")));
    }

    [Fact]
    public void Two_entries_on_one_path_fail()
    {
        string nupkg = Path.Combine(_dir, "twice.nupkg"), bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "X.dll"), "MZ");
        File.WriteAllText(Path.Combine(bin, "X.settings.json"), "{}");
        WriteZip(nupkg, ("content/X.settings.json", "{}"), ("build/X.settings.json", "{}"), ("lib/net8.0/X.dll", "MZ"));

        var verdict = InstallLayout.Verify(nupkg, bin);

        Assert.Contains("2 entries land at X.settings.json (content/X.settings.json, build/X.settings.json)", Assert.Single(verdict.Failures));
    }

    [Fact]
    public void A_file_that_lands_in_the_wrong_folder_fails_and_one_the_build_lacks_is_only_noted()
    {
        string nupkg = Path.Combine(_dir, "flat.nupkg"), bin = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(Path.Combine(bin, "Images"));
        File.WriteAllText(Path.Combine(bin, "X.dll"), "MZ");
        File.WriteAllText(Path.Combine(bin, "X.settings.json"), "{}");
        File.WriteAllText(Path.Combine(bin, "Images", "a.png"), "png");
        File.WriteAllText(Path.Combine(bin, "Helper.dll"), "MZ2");
        WriteZip(nupkg, ("build/X.settings.json", "{}"), ("lib/net8.0/X.dll", "MZ"), ("build/a.png", "png"), ("build/notes.txt", "n"));

        var verdict = InstallLayout.Verify(nupkg, bin);

        Assert.Equal("build/a.png lands at a.png, but the Release build has it at Images/a.png.", Assert.Single(verdict.Failures));
        Assert.Contains(verdict.Notes, n => n.Contains("build/notes.txt lands at notes.txt; the Release build has no such file"));
        Assert.Contains(verdict.Notes, n => n.Contains("Helper.dll, which the package does not carry"));
    }

    private static void WriteZip(string file, params (string Name, string Text)[] entries)
    {
        using var zip = new ZipArchive(File.Create(file), ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            var bytes = Encoding.UTF8.GetBytes(text);
            s.Write(bytes, 0, bytes.Length);
        }
    }
}
