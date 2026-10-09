using System.Text.RegularExpressions;

namespace EncyExtensionMcp;

/// <summary>
/// The workflow connect_to_store adds to a repository: on a version tag (or a manual run) it packs
/// the project on GitHub with `dotnet pack` and hands the .nupkg to the store's publish action, signed
/// in with the run's own GitHub token. Unlike the template's workflow it builds the author's csproj
/// where it is and as it is — no src/ layout, no Directory.Build.targets, no PackReady step (that
/// step deletes *.xml from the output, and some extensions keep their operation's definition in one).
/// </summary>
public static class StoreWorkflow
{
    public const string FileName = "publish_store.yml";
    public const string RelativePath = ".github/workflows/" + FileName;
    public const string NuGetOrg = "https://api.nuget.org/v3/index.json";
    /** Some EncySoftware.CAMAPI.SDK.Net versions are published here only (3.0.15, for one). */
    public const string EncyNexus = "https://nexus.encycam.com/repository/master/index.json";

    /**
     * The SDK line setup-dotnet installs for a target framework; null for any other framework. ENCY 3
     * extensions target .NET 8, 9 or 10 — a new one is added here when ENCY moves to it.
     */
    public static string? DotnetVersion(string? targetFramework)
    {
        var m = Regex.Match(targetFramework?.Trim() ?? "", @"^net(?<major>\d+)\.\d+", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Groups["major"].Value switch { "8" => "8.0.x", "9" => "9.0.x", "10" => "10.0.x", _ => null };
    }

    /** Whether a path can go into the workflow's bash step as it is: nothing there for the shell to read. */
    public static bool IsSafePath(string path) =>
        path.Length > 0 && path.IndexOfAny(new[] { '"', '$', '`', '\\', '\r', '\n' }) < 0;

    /** The workflow for this project, with the line ending its neighbours use. */
    public static string Render(string csprojPath, string dotnetVersion, string newline = "\n") =>
        Template.Replace("\r\n", "\n")   // a raw literal takes the source file's line endings
                .Replace("__CSPROJ__", csprojPath)
                .Replace("__DOTNET__", dotnetVersion)
                .Replace("\n", newline);

    // Verified on DirectCladdingOperation (08.10.2026): the pack step for real, the YAML parsed and the
    // version step tested. Kept in step with the template's publish.yml where they do the same thing.
    private const string Template = """
        # Publishes this extension to the ENCY Extension Store (https://apps.encycam.com).
        #   - push a tag vX.Y.Z         -> publishes version X.Y.Z
        #   - Actions -> Run workflow   -> publishes the version you type; tick "dry run" to only check it
        # No secrets: the run signs in to the store with its own GitHub token (OIDC). The store credits the
        # run to whoever started it, so that person links their GitHub account once in the store profile
        # (apps.encycam.com -> Profile -> Sign in with GitHub) and must own the extension's card there.
        # Added by ency-extension-mcp (connect_to_store); the other workflows here were left as they were.

        name: Publish to ENCY Extension Store

        on:
          push:
            tags: ['v*']
          workflow_dispatch:
            inputs:
              version:
                description: 'Version to publish, e.g. 3.0.1'
                required: true
              dry-run:
                description: 'Only check the package on the store, publish nothing'
                type: boolean
                default: false

        permissions:
          contents: read
          id-token: write

        jobs:
          publish:
            runs-on: windows-latest
            steps:
              - uses: actions/checkout@v4

              - uses: actions/setup-dotnet@v4
                with:
                  dotnet-version: '__DOTNET__'

              - name: Resolve version
                shell: bash
                env:
                  TYPED_VERSION: ${{ inputs.version }}
                run: |
                  if [ "$GITHUB_REF_TYPE" = "tag" ]; then v="${GITHUB_REF_NAME#v}"; else v="${TYPED_VERSION#v}"; fi
                  if [[ ! "$v" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]]; then
                    echo "::error::'$v' is not a version like 3.0.1"
                    exit 1
                  fi
                  echo "PKG_VERSION=$v" >> "$GITHUB_ENV"

              # Release: the build ENCY installs. Runtime files go under build/ in the package
              # (PackagePath="build\"), never contentFiles/ - ENCY's store installer keeps that folder as it is.
              # The ENCY Nexus is a source because some EncySoftware.CAMAPI.SDK.Net versions are not on
              # nuget.org. Without it NuGet silently takes the next version up (warning NU1603), and that
              # build does not load in the ENCY release it targets, so NU1603 is made an error here.
              - name: Pack
                shell: bash
                run: |
                  dotnet pack "__CSPROJ__" -c Release \
                    -p:Version="$PKG_VERSION" -p:WarningsAsErrors=NU1603 \
                    --source https://api.nuget.org/v3/index.json \
                    --source https://nexus.encycam.com/repository/master/index.json \
                    -o out

              - name: Publish to the ENCY Extension Store
                uses: EncySoftware/publish-extension-action@v1
                with:
                  nupkg: out/*.nupkg
                  dry-run: ${{ github.event_name == 'workflow_dispatch' && inputs.dry-run }}

        """;
}
