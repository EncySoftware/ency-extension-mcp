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
    /** The answer "none", as a package ready to publish carries it (Schedule B §B.3.2). */
    private const string ScheduleABlock = ""","reservedDomains":[]""";

    private static MemoryStream Nupkg(string tags = "ency-extension category:analyzer", bool manifest = true, bool dll = true,
                                      bool readme = true, bool icon = true, int screenshots = 2, string? sdk = "3.0.6",
                                      bool declared = true, string? nuspecSdk = null)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            string dependencies = nuspecSdk == null ? ""
                : $"<dependencies><group targetFramework=\"net10.0-windows7.0\"><dependency id=\"EncySoftware.CAMAPI.SDK.Net\" version=\"{nuspecSdk}\" /></group></dependencies>";
            Add(zip, "MyExt.nuspec", $"""
                <?xml version="1.0"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata><id>MyExt</id><version>0.1.0</version><tags>{tags}</tags>{dependencies}</metadata>
                </package>
                """);
            if (manifest) Add(zip, "build/MyExt.settings.json", "{}");
            if (dll) Add(zip, "lib/net8.0/MyExt.dll", "MZ");
            if (readme) Add(zip, "build/readme.md", "# MyExt");
            if (icon) Add(zip, "build/icon.png", "png");
            for (int i = 0; i < screenshots; i++) Add(zip, $"build/screenshots/shot{i}.png", "png");
            if (sdk != null) Add(zip, "build/package.info.json",
                $$"""{"packageId":"MyExt","sdkVersion":"{{sdk}}"{{(declared ? ScheduleABlock : "")}}}""");
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

    /** The store reads the Schedule A declaration out of the package; so does this, before the upload. */
    [Fact]
    public void A_package_that_declares_nothing_under_Schedule_A_is_told_so()
    {
        var f = PackageCheck.Read(Nupkg(declared: false));
        Assert.Null(f.Declaration);
        var note = Assert.Single(PackageCheck.Findings(f, Known, "3.0.6", new DateOnly(2026, 10, 15))
                                             .Where(x => x.Text.Contains("reservedDomains")));
        Assert.False(note.Blocking, "before the documents take effect the store publishes it with a warning");

        var answered = PackageCheck.Read(Nupkg()).Declaration!;
        Assert.Empty(answered.Domains);
        Assert.False(answered.Legacy);
    }

    /**
     * Which package.info.json the store reads — the first entry ending in it — is recorded with or without
     * an answer in it: connect_to_store names that file for the answer instead of asking for a second one.
     */
    [Fact]
    public void The_package_info_json_the_store_reads_is_named_with_or_without_an_answer()
    {
        var f = PackageCheck.Read(Nupkg(declared: false));
        Assert.Equal("build/package.info.json", f.ManifestEntry);
        Assert.Null(f.Declaration);
        Assert.Null(PackageCheck.Read(Nupkg(sdk: null)).ManifestEntry);
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

    /**
     * Both a manifest and a dll inside, which is all the store and this check used to ask — and ENCY
     * installs it as files that never register: the installer keeps contentFiles/ as it is, so the
     * manifest looks for its dll in there (DirectCladdingOperation, 08.10.2026).
     */
    [Fact]
    public void A_manifest_that_would_not_find_its_dll_after_installation_stops_the_publish()
    {
        string file = Path.Combine(Path.GetTempPath(), "mcp-dco-" + Guid.NewGuid().ToString("N")[..8] + ".nupkg");
        try
        {
            DcoFixture.WritePackage(file, "contentFiles");
            var f = PackageCheck.TryRead(file)!;

            var stop = Assert.Single(PackageCheck.Findings(f, Known, "3.0.15", new DateOnly(2026, 10, 15)), x => x.Blocking);
            Assert.Contains("the manifest lands at contentFiles/any/any/DirectCladdingOperationExtension.settings.json", stop.Text);
            Assert.Contains("the dll lands at DirectCladdingOperationExtension.dll (from lib/net10.0-windows7.0/DirectCladdingOperationExtension.dll)", stop.Text);
            Assert.Contains("PackagePath=\"build\\\"", stop.Text);
            Assert.Contains("connect_to_store", stop.Text);

            DcoFixture.WritePackage(file, "build");
            Assert.DoesNotContain(PackageCheck.Findings(PackageCheck.TryRead(file)!, Known, "3.0.15", new DateOnly(2026, 10, 15)), x => x.Blocking);
        }
        finally { File.Delete(file); }
    }

    /** A package made with `dotnet pack` names its SDK in the nuspec only; the store reads it there too. */
    [Fact]
    public void The_SDK_is_read_from_the_nuspec_dependency_as_well_and_the_higher_one_wins()
    {
        var onlyNuspec = PackageCheck.Read(Nupkg(sdk: null, nuspecSdk: "[3.0.15]"));
        Assert.Equal("3.0.15", onlyNuspec.SdkVersion);
        Assert.DoesNotContain(PackageCheck.Findings(onlyNuspec, Known, "3.0.15"), n => n.Text.Contains("names no SDK version"));

        Assert.Equal("3.0.15", PackageCheck.Read(Nupkg(sdk: "3.0.8", nuspecSdk: "3.0.15")).SdkVersion);
        Assert.Equal("3.0.9", PackageCheck.Read(Nupkg(sdk: "3.0.9", nuspecSdk: "3.0.8")).SdkVersion);

        var none = PackageCheck.Read(Nupkg(sdk: null));
        Assert.Contains(PackageCheck.Findings(none, Known, "3.0.15"), n => n.Text.Contains("names no SDK version") && !n.Blocking);
    }

    /**
     * The store's PackageSdk.Reading.version(): SemVers.compare(manifest, nuspec) >= 0 ? manifest : nuspec,
     * each side cleaned first. A release outranks its own prerelease — keeping package.info.json's value on
     * a numeric tie read 3.0.15-dev.3 beside 3.0.15 as a prerelease, and connect_to_store added a false stop.
     */
    [Theory]
    [InlineData("3.0.15-dev.3", "3.0.15", "3.0.15")]
    [InlineData("3.0.15", "3.0.15-dev.3", "3.0.15")]
    [InlineData("3.0.15", "[3.0.15]", "3.0.15")]
    [InlineData("3.0.8", "[3.0.15, )", "3.0.15")]
    [InlineData("3.0.9", "3.0.8", "3.0.9")]
    [InlineData("[3.0.8]", null, "3.0.8")]                    // a bracketed pin copied into the manifest
    [InlineData("3.0.15.0", null, "3.0.15")]                  // NuGet's spelling: no zero fourth part
    [InlineData("03.0.015", null, "3.0.15")]
    [InlineData("not-a-version", "3.0.8", "3.0.8")]
    [InlineData(null, "(, 3.0.15]", null)]                    // no lower bound: no SDK named
    [InlineData("3.0.15-dev.10", "3.0.15-dev.9", "3.0.15-dev.10")]   // numeric labels compare as numbers
    [InlineData("3.0.15-Beta", "3.0.15-alpha", "3.0.15-Beta")]       // and words without regard to case
    [InlineData("1.0.20260925123456", "1.0.9", "1.0.20260925123456")] // a part past two billion
    public void The_SDK_is_the_one_the_store_reads(string? manifest, string? nuspec, string? expected) =>
        Assert.Equal(expected, PackageCheck.SdkOf(manifest, nuspec));

    [Fact]
    public void Versions_compare_the_way_the_store_compares_them()
    {
        Assert.True(PackageCheck.CompareVersions("3.0.15", "3.0.15-dev.3") > 0);
        Assert.True(PackageCheck.CompareVersions("3.0.15-dev", "3.0.15-dev.1") < 0);   // fewer labels rank lower
        Assert.True(PackageCheck.CompareVersions("3.0.15-1", "3.0.15-alpha") < 0);     // a number below a word
        Assert.Equal(0, PackageCheck.CompareVersions("3.0.15+build.7", "3.0.15"));      // build metadata is ignored
        Assert.Equal(0, PackageCheck.CompareVersions("3.0", "3.0.0"));
        Assert.Null(PackageCheck.CleanSdk("3.0." + new string('1', 70)));                // longer than the store keeps
    }

    /** Read out of a package: the manifest's prerelease beside the nuspec's release is the release. */
    [Fact]
    public void A_prerelease_in_package_info_json_beside_its_release_in_the_nuspec_reads_as_the_release()
    {
        Assert.Equal("3.0.15", PackageCheck.Read(Nupkg(sdk: "3.0.15-dev.3", nuspecSdk: "3.0.15")).SdkVersion);
        Assert.Equal("3.0.15", PackageCheck.Read(Nupkg(sdk: "3.0.15", nuspecSdk: "[3.0.15-dev.3]")).SdkVersion);
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_left_to_the_server()
    {
        string path = Path.Combine(Path.GetTempPath(), "notazip-" + Guid.NewGuid().ToString("N") + ".nupkg");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 3, 4, 9 });
        Assert.Null(PackageCheck.TryRead(path));
    }
}
