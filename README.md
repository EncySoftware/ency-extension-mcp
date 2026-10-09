# ency-extension-mcp

MCP server for the "write an ENCY extension in Cursor, never copy a file by hand" flow
(see [ency-extension-template](https://github.com/EncySoftware/ency-extension-template)):

| Tool | What it does |
|---|---|
| `create_extension_folder` | The project from the template on this machine, renamed — no GitHub account, no git. Start here. |
| `publish_folder` | Publishes a local folder with no git and no gh: the store makes the repository, commits the folder, builds and publishes; the author only signs in and approves the store app in the browser. |
| `publish_folder_status` | The latest publish_folder result: building, published (version + card), or failed (step + log). |
| `publish_package` | Publishes what you built on THIS machine — a ready `.nupkg` or a build-output folder the store packs. No git, no GitHub, no repository; the card is yours. |
| `connect_to_store` | For a project NOT made from the template, in a GitHub repository of its own and packed with `dotnet pack`: moves what the csproj packs where ENCY's store installer does not look (`contentFiles/`, or a folder named twice) to `build\`, adds `.github/workflows/publish_store.yml`, and proves both with a real pack laid out the way ENCY installs it. A version tag then publishes — `publish_extension` pushes it, `publish_status` follows the run. |
| `create_extension_repo` | GitHub repo from the ENCY template → waits for the copy → clones → renames the extension → sets the publish secret → pushes. |
| `publish_extension` | Tags `vX.Y.Z` and pushes — GitHub Actions builds, packs and publishes to the [ENCY Extension Store](https://apps.encycam.com). |
| `publish_status` | Follows the run (failure log tail when red) and reports the store card + moderation state when green. |
| `get_extension_guide` | The skill library: which of the eight ENCY entry points to implement, how to register it, a minimal skeleton and the traps. `type=list` first, then the type. |

Auth model: the server shells out to the **author's own `gh` and `git`** — your GitHub login is
the credential there. For the store, `ency-extension-mcp login` (once) **opens the ENCY sign-in page
in your browser** and keeps only a refresh token — the tool never sees your password, and SSO or
two-factor work because they happen where they are meant to. `--password` falls back to typing an
email and password in the terminal, for a machine with no browser.
`create_extension_repo` then **claims the extension name for
the new repository**, so no credential is stored in GitHub at all: every publish, the first one
included, authenticates with the workflow's own GitHub OIDC token. If the claim cannot be made (store
unreachable, name owned by somebody else) the tool falls back to planting an `ENCY_STORE_TOKEN`
secret, which covers the first publish. Either way the author never handles a token.

Repos made by hand from the template can be bound the same way:

```bash
ency-extension-mcp claim MyCoolExtension owner/MyCoolExtension
```

## Start from nothing — no GitHub account

`create_extension_folder(name, dir)` (or `ency-extension-mcp create-folder <Name> [dir]`) makes the
project from the template on this machine: the public zip, unpacked and renamed, with the rules for
the assistant and the MCP registration inside. Nothing on GitHub is touched until `publish_folder`.

## Publish from a folder — no git, no gh

`publish_folder(name, folder)` is the tool an assistant should reach for when the folder has no GitHub
repository of its own (one that has, and is packed with `dotnet pack`, is connected with
`connect_to_store` instead — see below), and `ency-extension-mcp update-extension [folder]` brings an
extension made from the template in line with the current template — the SDK pin and the files the
template owns — from a terminal. Both refuse a repository connected with `connect_to_store`, and a
project outside `src/` in a repository with no template workflow (`publish_folder`: a GitHub
repository): the template's files would undo what such a repository publishes with.

`ency-extension-mcp publish-folder <Name> <folder>` is the same route from a terminal. The store
does the GitHub work through its GitHub App: creates the repository in the author's account,
commits the folder into `src/`, runs the build and reports back — the version and the card link, or
the failing step with its log. The author is needed twice, in the browser only: the store sign-in
and, once, the app's consent page; the tool opens both and waits, and nothing is asked in a
terminal. The next version is the same call. Repositories made from the template carry
`.mcp.json` and `.cursor/mcp.json`, so Claude Code and Cursor see the server as soon as the tool is
installed (`dotnet tool install -g EncySoftware.ExtensionStoreMcp`).

## Publish what you built yourself — no repository at all

`publish_package [path]` is for the author who keeps the sources in their own repository and builds
the package on their own machine: the other two routes both end up needing a GitHub repository, and
without this one the only way left was uploading the `.nupkg` through the website for every version.

Give it a ready `.nupkg`, or the flat build-output folder (`<Name>.dll` + `<Name>.settings.json` +
`package.info.json`, the output of `dotnet build`, not `dotnet publish`): a folder is uploaded file
by file and the STORE packs it, exactly as it does for CI. Nothing is built here — that is the
author's own business. From a terminal:

```
ency-extension-mcp publish-package                      # the .nupkg or build output in this folder
ency-extension-mcp publish-package bin/Release/net10.0-windows --version 0.2.0
ency-extension-mcp publish-package MyExt.0.2.0.nupkg --category operation
```

It publishes under the author's own store account (the same browser sign-in as everything else). A
new name waits for a moderator once; a new version of an extension already in the catalogue appears
at once.
Today that first publication comes back with its card link; a store that reviews every version answers
**Submitted for review** instead, with no card link until a moderator approves it — and
`publish_folder_status`, `my_extensions` and (signed in) `publish_status` then say where it stands.

`check_package [path]` (or `ency-extension-mcp check-package`) reads a ready `.nupkg` before any of
that, with no sign-in and nothing uploaded: the `ency-extension` tag the catalogue needs, the
manifest and assembly the store requires, whether the manifest finds its dll once ENCY's store
installer has laid the package out (see below — a manifest packed under `contentFiles/` does not),
the `category:` tag, screenshots, readme, icon, and whether the SDK it was built against (from
`package.info.json` or the nuspec's dependency on `EncySoftware.CAMAPI.SDK.Net`) has shipped in a
released ENCY. `publish_package` runs the same checks first, so a package the store would refuse, or
ENCY would install without registering, never opens the browser; the rest come back as notes under
the result. For a source folder the counterpart is `check_extension`.

## What the store asks the publisher to declare

Two consents, neither of which this tool can give on the author's behalf:

- **Developer registration and the Developer Agreement**, once. Done in a browser at
  [apps.encycam.com/publish](https://apps.encycam.com/publish): a short registration (a company or a
  person, free or paid extensions, contact details), then I Agree. Until then, every publish route —
  this tool included — comes back with a 403 naming that page.
- **The Schedule A declaration**, with every submission: which of the capabilities ENCY licenses
  separately — the Reserved Functionality areas listed in Schedule A of the
  [Publishing Policy](https://encycam.com/legal/extension-store/publishing-policy/) — the extension
  provides. From a tool it travels in `reservedDomains` of `package.info.json`
  (Schedule B §B.3.2); `[]` is the answer "none":

  ```json
  "reservedDomains": []
  ```

  An extension that does provide some lists each area with the licence Schedule A assigns to it
  (`capabilities` is optional):

  ```json
  "reservedDomains": [
    { "domain": "A-05", "entitlement": "ENCY Nesting", "capabilities": ["nesting.layout"] }
  ]
  ```

  The manifest this tool writes for a project that had none carries `[]`. `check_extension` /
  `check_package` say when the list is missing, when the manifest still has the earlier
  `reservedFunctionality` block (the store reads it until 31 October 2026 and refuses it after),
  and when the list is malformed or an entry lacks its area or licence — those two stop the publish
  here, since the store refuses them on every date. The answer is the author's statement about
  their extension: an assistant should put the question to them, not answer it for them. A release
  with a new answer stops with a link to confirm it in the store; confirm, then publish again. The
  store asks again when the answer changes, when a new version of Schedule A comes into force, when
  the words of the statements change, or when a run is credited to someone else: once the repository
  is bound to the package (its first publication binds it), every run from it is credited to whoever
  bound it, so a colleague's run uses that person's confirmation. Areas and their licences:
  <https://encycam.com/legal/extension-store/reserved-functionality/>. Until **1 November 2026** the
  store publishes a submission without `reservedDomains` with a warning (printed under the publish
  result); after that it refuses.

## When something is off

- `doctor` (or `ency-extension-mcp doctor`) — the machine in six lines: the tool's version and whether
  nuget.org has a newer one, the .NET SDK, git and gh (and which routes need them), whether the store
  answers, who is signed in and through which Keycloak client, and whether Cursor lists the server.
  Each gap comes with the command that fixes it. Call it first when a publish fails for no clear reason.
- `my_extensions` (or `ency-extension-mcp my-extensions`) — every card of the signed-in author, what
  needs attention first: a failing build with its step and run link, a rejected card with the
  moderator's reason, one waiting for a moderator; then the live ones with their links, then the hidden.
- `ency-extension-mcp version` — what is running. The tool asks nuget.org once a day, and when a newer
  version exists every publish result ends with the update command
  (`dotnet tool update -g EncySoftware.ExtensionStoreMcp --no-cache` — the `--no-cache` because the
  package index lags a fresh release by up to an hour).

## Already have a project that was not made from the template?

Keep it as it is. A project in a GitHub repository of its own, packed with `dotnet pack` — the usual
shape of an extension that was on nuget.org before the store — is connected with
`connect_to_store [path]`:

```
ency-extension-mcp connect-to-store                     # the repository or project folder you are in
ency-extension-mcp connect-to-store C:\src\MyExtension --csproj project/main/MyExtension.csproj
```

- It finds the extension's csproj: the one that references `EncySoftware.CAMAPI.SDK.Net` or packs a
  `*.settings.json`. With several, it lists them and asks for `csproj`.
- It moves what the csproj packs under `contentFiles/` to `build\`. ENCY's store installer merges
  `build/`, `content/` and `lib/<tfm>/` into one folder next to the dll and keeps every other folder
  as it is, so a manifest packed under `contentFiles/any/any/` looks for its dll in that folder and the
  extension installs without ever registering. The new value is `build\` plus the file's folder in
  the Release build, minus the folder NuGet appends by itself (`<None Update="Images\*.png">` gets
  plain `build\`, or the pictures land in `Images\Images\`); a `build\` or `content\` value that names
  that folder again is corrected the same way. An item the build does not copy to its output has no
  place in the build to go by, so it keeps the folder its own value gave it under `contentFiles/`, and
  the answer marks it with `!` for the author to check. Only the value between the quotes changes: the
  byte order mark, the line endings and every other line stay as they were. An item a text edit cannot
  put right is listed instead, with what it needs.
- It adds `.github/workflows/publish_store.yml` when the repository has a GitHub remote (a mirror will
  do), never overwriting one and never touching the other workflows: a `vX.Y.Z` tag, or **Actions →
  Run workflow** with a version (and **dry run** to have the store only check the package), packs the
  project on GitHub — Release, nuget.org and the ENCY Nexus as sources (some SDK versions are only
  there), NU1603 as an error — and publishes the `.nupkg` with the run's own GitHub token. No secrets.
  One repository publishes one extension: a `publish_store.yml` already there that packs another
  csproj, or names none, stops the call with nothing changed. With no GitHub remote, or outside git, no
  workflow is added (it would never run), but the csproj is fixed and checked all the same, and the
  answer gives the `dotnet pack` line and `publish_package` to publish from this machine.
- It proves both with a real `dotnet pack` on this machine (so it needs the .NET SDK the project
  targets), laid out the way ENCY installs it and compared with the Release build: the manifest has
  to find its dll, every file has to land where the build has it, nothing under `contentFiles/`, no
  two files on one path. When that fails, both changes are taken back and the answer says what would
  land where. What the store would still refuse at the first publish is named in the answer: no
  `ency-extension` tag, a prerelease SDK, or an SDK newer than the line the store publishes under.

It writes no `package.info.json` and no Schedule A answer. The answer ends with what only the author
can do, once, in the browser — sign in at [apps.encycam.com](https://apps.encycam.com), the developer
registration and the terms on the Publish page, **Sign in with GitHub** in the profile, owning the
card — and with how to publish. The first publish binds the repository to the extension, so start it
as the card's owner with that GitHub account linked; from then on every run from the repository is
credited to that owner, whoever starts it (to bind it in advance: **Connect** on the account page, or
`ency-extension-mcp claim <PackageId> <owner/repo>`). After that `publish_extension` pushes the
version tag, and `publish_status` follows the run of `publish_store.yml` to the store card.

A project made from the template, or brought into its `src/` layout, publishes the template's way:
`publish_folder` and `publish_extension` find the csproj under `src/`, `publish_package` finds it
above the build output. A missing `package.info.json` is written from the csproj (name, version, SDK
pin, the `ency-extension` tag) and the answer says so; an existing one gets the tag. The template's
`Directory.Build.props` / `Directory.Build.targets` give any csproj under the repository the flat
output and the `PackReady` target the workflow builds with (`update_extension` brings those two files
into a repository made earlier) — which is why `update_extension` refuses a project of its own shape
(outside `src/` in a repository with no template workflow, or connected with `connect_to_store`):
`PackReady` deletes `*.xml` from the output, and an operation can keep its definition in one. The one thing no tool can write is `<Name>.settings.json` — the extension's manifest of entry
points, without which ENCY registers nothing; the template has a sample.

## Publishing without a console

Without the tool, the same route is the store page — and somebody who is not a developer should be
pointed there, not at `gh` (an assistant that finds `gh` on the machine tends to choose the console
route — that is how a first attempt ended in two red screens on 02.09.2026):

1. Open [apps.encycam.com/publish](https://apps.encycam.com/publish) → **A folder with the
   extension**. Install the store app on GitHub once (a consent page) and name the extension.
2. Choose the project folder — the one holding `<Name>.csproj`, `package.info.json` and
   `<Name>.settings.json` — and press **Upload and publish**. The store creates the repository in
   the author's GitHub account, commits the folder, runs the build and shows the result on the same
   page. The next version is the same button. No code yet? The same page can publish the template
   sample as a trial.

Prefer GitHub by hand? Open the **Use this template** form with the template already chosen:
[github.com/new?template_owner=EncySoftware&template_name=ency-extension-template](https://github.com/new?template_owner=EncySoftware&template_name=ency-extension-template) — name the
repository after the extension (the first push renames everything inside);
[apps.encycam.com/account](https://apps.encycam.com/account) → **Connect** once, in the browser;
put the code in `src/`, then **Actions → publish-to-ency-store → Run workflow** with the fields
empty. Version, tag, build, publish — all on their own.

**Already have a project that was not made from the template?** Keep it. One packed with
`dotnet pack` in a repository of its own needs nothing brought over: `connect_to_store` (above) gives
it a workflow as it is. Otherwise the template README has the exact list of what to bring over (the
`src/` layout, the `PackReady` target, the workflow); then connect the repository to the name — in
the browser, or with

```bash
ency-extension-mcp claim MyCoolExtension owner/MyCoolExtension
```

— and use the same Run workflow.

The tools below are the **console** route: they script the same steps and need `gh` signed in.
Reach for them when scripting is the point — creating many repositories, driving it from Cursor —
not as the default.

## Where the API itself is documented

The guides this server ships cover the ENCY **entry points** — which interface to implement and how
to register it. What you call inside them lives elsewhere:

- [CAM API reference](https://docs.encycam.com/CAMAPI/3/en/) — every interface, property and method.
- [Lessons](https://docs.encycam.com/CAMAPI/3/en/src/Lessons/Main.html) — the same API in order,
  starting from a first extension.
- [cam-api-examples](https://github.com/EncySoftware/cam-api-examples) — a worked example of every
  extension kind, code included.

## Setup (Cursor)

Prerequisites: .NET 8 SDK, `git`, `gh` (`gh auth login` once). `connect_to_store` packs your project
to check it, so it also needs the SDK your extension targets (.NET 10 for ENCY 3.1).

Two commands:

```bash
dotnet tool install -g EncySoftware.ExtensionStoreMcp
ency-extension-mcp setup
```

`setup` registers the server in `~/.cursor/mcp.json` (merging, so other MCP servers stay), registers it
with Claude Code when its CLI is present, and logs you in to the store if you have not yet (licsys
account; only a refresh token is kept, under `%APPDATA%`). Restart Cursor afterwards. `--no-login`
skips the login step.

Releases are published from `publish-tool.yml` on a version tag, through nuget.org **trusted
publishing** — the run swaps its GitHub OIDC token for a key that lives minutes, so no publishing
credential is stored in this repo either. The policy on nuget.org points at this repo + that workflow;
the account name comes from the `NUGET_USER` repository variable.

Doing it by hand instead of `setup` means `ency-extension-mcp login` plus this in `~/.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "ency-extension-store": {
      "command": "ency-extension-mcp"
    }
  }
}
```

(The `ENCY_STORE_TOKEN` env var still overrides the stored login when set — CI/debug escape hatch.)

## The flow it enables

1. In Cursor: *"create an ENCY extension called ToolpathTimer"* → `create_extension_repo`
   makes the repo, clones it next to your workspace, renames everything, wires the secret.
2. *"it should add an item to the right-click menu of an operation"* → `get_extension_guide`
   (`list` → `operation_popup`) tells the agent which interface to implement, which
   `*.settings.json` key to use and what breaks. The template carries the same guides as Cursor
   rules (`.cursor/rules/type-*.mdc`), generated from `guides/` here — the tool is the fresh copy.
3. Write the code in `src/` — the always-on rule covers the anatomy (factory, settings.json ids,
   package.info.json).
4. *"publish it as 0.1.0"* → `publish_extension` tags and pushes; CI does the rest.
5. *"did it publish?"* → `publish_status` → run status → store card link. New extensions land
   hidden until a store moderator approves them; the direct card link works immediately.

## Development

```bash
dotnet test tests/EncyExtensionMcp.Tests.csproj   # logic tests (processes faked)
dotnet run --project src                           # stdio server (speak JSON-RPC to it)
ENCY_SMOKE=1 dotnet test tests/EncyExtensionMcp.Tests.csproj --filter Category=Smoke
                                                   # live, read-only: the store's answer shapes and
                                                   # the Keycloak sign-in contract (CI runs it weekly)
```

The extension-type guides in `guides/` are the single source of truth: they are embedded into the
assembly for `get_extension_guide`, and the template repo carries a generated snapshot of the same
text as Cursor rules. After editing a guide:

```bash
powershell -NoProfile -File tools/sync-rules.ps1          # .cursor/rules/*.mdc + AGENTS.md in the template
powershell -NoProfile -File tools/sync-rules.ps1 -Check    # exit 1 if the snapshot drifted
```

Two formats, one source: Cursor picks up `.cursor/rules/*.mdc` by itself, while Claude Code, Codex and
Copilot read `AGENTS.md` — so the generator also writes an `AGENTS.md` router (what the repo is, which
guide to open for which kind of extension, how publishing works). It links the same `.mdc` files
instead of copying them, so a guide edit never needs a second pass.

`-TemplateDir` points elsewhere if your template checkout is not a sibling of this repo. Commit the
template repo separately — the script only writes files. Adding a new entry point means: a guide file,
an entry in `guides/_index.json` (the tests read it), and a re-run of the script.

Config knobs: `ENCY_STORE_API` overrides the store API base (test stands).
