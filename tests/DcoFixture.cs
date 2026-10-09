using System.IO.Compression;
using System.Text;

/**
 * The project that taught the layout rule (08.10.2026): DirectCladdingOperation, v3/main 460cae9,
 * project/main/DirectCladdingOperationExtension.csproj — byte for byte as git checks it out on
 * Windows: a UTF-8 byte order mark and CRLF line endings, line 18 holding two spaces. It packs its
 * manifest, the operation's XML, the smart hints and their pictures under contentFiles/any/any/.
 */
public static class DcoFixture
{
    public const string CsprojName = "DirectCladdingOperationExtension.csproj";
    public const string Dll = "DirectCladdingOperationExtension.dll";
    public const string Manifest = "DirectCladdingOperationExtension.settings.json";

    private static readonly string[] Lines =
    {
        "<Project Sdk=\"Microsoft.NET.Sdk\">",
        "  <PropertyGroup>",
        "    <TargetFramework>net10.0-windows</TargetFramework>",
        "    <NoWarn>NETSDK1138;1591</NoWarn>",
        "    <ImplicitUsings>enable</ImplicitUsings>",
        "    <Nullable>enable</Nullable>",
        "    <Authors>ENCY</Authors>",
        "    <PackageId>EncySoftware.DirectCladdingOperation</PackageId>",
        "    <PackageTags>ency-extension;operationsolver</PackageTags>",
        "    <Description>Operation for ENCY that calculates a toolpath for additive machining by directly transforming input geometric curves into tool movements at given speeds and with specified linking strategy parameters</Description>",
        "    <PackageIcon>icon.png</PackageIcon>",
        "    <PackageProjectUrl>https://github.com/EncySoftware/DirectCladdingOperation</PackageProjectUrl>",
        "    <RepositoryUrl>https://github.com/EncySoftware/DirectCladdingOperation</RepositoryUrl>",
        "    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>",
        "    <GenerateDocumentationFile>true</GenerateDocumentationFile>",
        "    <PackageReadmeFile>README.md</PackageReadmeFile>",
        "  </PropertyGroup>",
        "  ",
        "  <ItemGroup>",
        "    <PackageReference Include=\"EncySoftware.CAMAPI.SDK.Net\" Version=\"3.0.15\" />",
        "  </ItemGroup>",
        "",
        "  <ItemGroup>",
        "    <None Include=\"icon.png\" Pack=\"true\" PackagePath=\"\\\"/>",
        "    <None Update=\"DirectCladdingOperation.smarthint\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\">",
        "      <CopyToOutputDirectory>Always</CopyToOutputDirectory>",
        "      <CopyToPublishDirectory>Always</CopyToPublishDirectory>",
        "    </None>",
        "    <None Update=\"SmartHintImages\\*.png\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\SmartHintImages\\\">",
        "      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>",
        "      <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>",
        "    </None>",
        "    <None Include=\"..\\..\\README.md\" Pack=\"true\" PackagePath=\"\\\" />",
        "    <None Include=\"DirectCladdingOperation_ExtOp.xml\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\">",
        "      <CopyToOutputDirectory>Always</CopyToOutputDirectory>",
        "    </None>",
        "    <None Include=\"DirectCladdingOperationExtension.settings.json\" Pack=\"true\" PackagePath=\"contentFiles\\any\\any\\\">",
        "      <CopyToOutputDirectory>Always</CopyToOutputDirectory>",
        "    </None>",
        "  </ItemGroup>",
        "</Project>",
    };

    /** The text as the editor sees it: CRLF, no byte order mark (that is a byte, not a character). */
    public static string Text => string.Join("\r\n", Lines) + "\r\n";

    /** The file as it is on disk. */
    public static byte[] Bytes => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Text)).ToArray();

    /** The same file with the four values moved to build\ — and not one other byte changed. */
    public static string FixedText => Text
        .Replace("PackagePath=\"contentFiles\\any\\any\\SmartHintImages\\\"", "PackagePath=\"build\\\"")
        .Replace("PackagePath=\"contentFiles\\any\\any\\\"", "PackagePath=\"build\\\"");

    public static byte[] FixedBytes => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(FixedText)).ToArray();

    // ---- what a pack of it holds, and what its Release build holds

    public static readonly string SettingsJson =
        "{\n  \"name\": \"DirectCladding operation extensions\",\n  \"module_path\": \"${extensionJsonFolder}/" + Dll + "\",\n"
        + "  \"extensions\": [ { \"OperationSolver\": { \"id\": \"DirectCladdingOperationExtension.ToolPathCalculation\" } } ]\n}\n";

    /** The runtime files, by their path in the Release output, with their bytes. */
    public static readonly (string Path, string Text)[] Runtime =
    {
        (Dll, "MZ-dll-bytes"),
        ("DirectCladdingOperationExtension.xml", "<doc>the compiler's</doc>"),
        (Manifest, SettingsJson),
        ("DirectCladdingOperation_ExtOp.xml", "<ExtOp>the operation's definition</ExtOp>"),
        ("DirectCladdingOperation.smarthint", "smart hints"),
        ("SmartHintImages/link-clearance.png", "png-1"),
        ("SmartHintImages/start-point.png", "png-2"),
    };

    /** Where each runtime file sits in the package: lib/ for the assembly and its doc, the rest by `layout`. */
    public static string EntryFor(string path, string layout) =>
        path is Dll or "DirectCladdingOperationExtension.xml" ? "lib/net10.0-windows7.0/" + path
        : layout == "contentFiles" ? "contentFiles/any/any/" + (path.StartsWith("SmartHintImages/") ? "SmartHintImages/" + path : path)
        : "build/" + path;

    /** A package of the project as `dotnet pack` makes it — "build" (fixed) or "contentFiles" (as it was). */
    public static void WritePackage(string file, string layout, string version = "0.0.0-storecheck", string tags = "ency-extension operationsolver")
    {
        using var zip = new ZipArchive(File.Create(file), ZipArchiveMode.Create);
        Add(zip, "EncySoftware.DirectCladdingOperation.nuspec", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>EncySoftware.DirectCladdingOperation</id>
                <version>{version}</version>
                <icon>icon.png</icon>
                <readme>README.md</readme>
                <tags>{tags}</tags>
                <dependencies>
                  <group targetFramework="net10.0-windows7.0">
                    <dependency id="EncySoftware.CAMAPI.SDK.Net" version="3.0.15" exclude="Build,Analyzers" />
                  </group>
                </dependencies>
              </metadata>
            </package>
            """);
        Add(zip, "_rels/.rels", "<Relationships/>");
        Add(zip, "[Content_Types].xml", "<Types/>");
        Add(zip, "package/services/metadata/core-properties/x.psmdcp", "<coreProperties/>");
        Add(zip, "README.md", "# Direct Cladding");
        Add(zip, "icon.png", "icon");
        foreach (var (path, text) in Runtime) Add(zip, EntryFor(path, layout), text);
    }

    /** The Release build output the package came from. */
    public static void WriteBuildOutput(string dir)
    {
        foreach (var (path, text) in Runtime)
        {
            string file = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        // What the SDK's own packages copy into every build, and nobody packs.
        File.WriteAllText(Path.Combine(dir, "CAMAPI.Application.xml"), "<doc/>");
        File.WriteAllText(Path.Combine(dir, "DirectCladdingOperationExtension.pdb"), "pdb");
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var s = zip.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }
}
