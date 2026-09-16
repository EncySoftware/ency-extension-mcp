using System.IO.Compression;
using System.Text;
using EncyExtensionMcp;
using Xunit;

/**
 * check_package: what a ready .nupkg says about itself, read off the archive without a sign-in or an
 * upload. Built around the facts the store cares about (marker, manifest, assembly) and the ones an
 * author only learns from a bare card (pictures, readme, icon, category, SDK).
 */
public class PackageCheckTests
{
    private static readonly StoreCategory[] Known = { new("other", "Other"), new("analyzer", "Analyzer"), new("operation", "Operation") };

    /** A package as the ENCY pack tool lays it out; callers strip what they want missing. */
    private static MemoryStream Nupkg(string tags = "ency-extension category:analyzer", bool manifest = true, bool dll = true,
                                      bool readme = true, bool icon = true, int screenshots = 2, string? sdk = "3.0.6")
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "MyExt.nuspec", $"""
                <?xml version="1.0"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata><id>MyExt</id><version>0.1.0</version><tags>{tags}</tags></metadata>
                </package>
                """);
            if (manifest) Add(zip, "build/MyExt.settings.json", "{}");
            if (dll) Add(zip, "lib/net8.0/MyExt.dll", "MZ");
            if (readme) Add(zip, "build/readme.md", "# MyExt");
            if (icon) Add(zip, "build/icon.png", "png");
            for (int i = 0; i < screenshots; i++) Add(zip, $"build/screenshots/shot{i}.png", "png");
            if (sdk != null) Add(zip, "build/package.info.json", $"{{\"packageId\":\"MyExt\",\"sdkVersion\":\"{sdk}\"}}");
        }
        ms.Position = 0;
        return ms;
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var s = zip.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void A_complete_package_has_nothing_to_fix()
    {
        var f = PackageCheck.Read(Nupkg());
        Assert.Equal(("MyExt", "0.1.0", true, "analyzer"), (f.PackageId, f.Version, f.HasMarker, f.CategoryTag));
        Assert.Equal(2, f.Screenshots);
        Assert.Empty(PackageCheck.Findings(f, Known, "3.0.6"));
    }

    [Fact]
    public void Missing_marker_manifest_or_assembly_stops_the_publish()
    {
        var f = PackageCheck.Read(Nupkg(tags: "category:analyzer", manifest: false, dll: false));
        var stops = PackageCheck.Findings(f, Known, "3.0.6").Where(x => x.Blocking).Select(x => x.Text).ToList();
        Assert.Equal(3, stops.Count);
        Assert.Contains(stops, t => t.Contains("ency-extension"));
        Assert.Contains(stops, t => t.Contains("settings.json"));
        Assert.Contains(stops, t => t.Contains(".dll"));
    }

    /** The icon is never a cover: a package whose only picture is icon.png has no screenshots. */
    [Fact]
    public void Pictures_readme_and_icon_are_worth_a_note_but_not_a_refusal()
    {
        var f = PackageCheck.Read(Nupkg(readme: false, icon: false, screenshots: 0));
        var notes = PackageCheck.Findings(f, Known, "3.0.6");
        Assert.All(notes, n => Assert.False(n.Blocking));
        Assert.Contains(notes, n => n.Text.Contains("no screenshots"));
        Assert.Contains(notes, n => n.Text.Contains("no readme"));
        Assert.Contains(notes, n => n.Text.Contains("no icon"));
    }

    [Fact]
    public void An_unknown_or_missing_category_names_the_store_list()
    {
        var none = PackageCheck.Findings(PackageCheck.Read(Nupkg(tags: "ency-extension")), Known, "3.0.6");
        Assert.Contains(none, n => n.Text.Contains("no `category:") && n.Text.Contains("analyzer, operation"));

        var unknown = PackageCheck.Findings(PackageCheck.Read(Nupkg(tags: "ency-extension category:Milling")), Known, "3.0.6");
        Assert.Contains(unknown, n => n.Text.Contains("'milling' is not one the store knows"));
    }

    [Fact]
    public void An_sdk_newer_than_any_release_is_called_out()
    {
        var f = PackageCheck.Read(Nupkg(sdk: "3.0.9-dev.4"));
        Assert.Contains(PackageCheck.Findings(f, Known, "3.0.6"), n => n.Text.Contains("newer than the newest released ENCY"));
        Assert.DoesNotContain(PackageCheck.Findings(PackageCheck.Read(Nupkg(sdk: "3.0.6")), Known, "3.0.8"), n => n.Text.Contains("newer"));
        // Without the store's number nothing can be compared, and nothing is claimed.
        Assert.DoesNotContain(PackageCheck.Findings(f, Known, null), n => n.Text.Contains("newer"));
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_left_to_the_server()
    {
        string path = Path.Combine(Path.GetTempPath(), "notazip-" + Guid.NewGuid().ToString("N") + ".nupkg");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 3, 4, 9 });
        Assert.Null(PackageCheck.TryRead(path));
    }
}
