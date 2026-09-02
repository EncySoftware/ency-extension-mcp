# ency-extension-mcp

MCP server for the "write an ENCY extension in Cursor, never copy a file by hand" flow
(see [ency-extension-template](https://github.com/EncySoftware/ency-extension-template)):

| Tool | What it does |
|---|---|
| `create_extension_folder` | The project from the template on this machine, renamed — no GitHub account, no git. Start here. |
| `publish_folder` | Publishes a local folder with no git and no gh: the store makes the repository, commits the folder, builds and publishes; the author only signs in and approves the store app in the browser. |
| `publish_folder_status` | The latest publish_folder result: building, published (version + card), or failed (step + log). |
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

`publish_folder(name, folder)` is the tool an assistant should reach for, and
`ency-extension-mcp publish-folder <Name> <folder>` is the same route from a terminal. The store
does the GitHub work through its GitHub App: creates the repository in the author's account,
commits the folder into `src/`, runs the build and reports back — the version and the card link, or
the failing step with its log. The author is needed twice, in the browser only: the store sign-in
and, once, the app's consent page; the tool opens both and waits, and nothing is asked in a
terminal. The next version is the same call. Repositories made from the template carry
`.mcp.json` and `.cursor/mcp.json`, so Claude Code and Cursor see the server as soon as the tool is
installed (`dotnet tool install -g EncySoftware.ExtensionStoreMcp`).

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

**Already have a project that was not made from the template?** Keep it. The template README
has the exact list of what to bring over (the `src/` layout, the `PackReady` target, the workflow);
then connect the repository to the name — in the browser, or with

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

Prerequisites: .NET 8 SDK, `git`, `gh` (`gh auth login` once).

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
