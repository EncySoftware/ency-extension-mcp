# ency-extension-mcp

MCP server for the "write an ENCY extension in Cursor, never copy a file by hand" flow
(see [ency-extension-template](https://github.com/EncySoftware/ency-extension-template)):

| Tool | What it does |
|---|---|
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

## Publishing without a console

Most authors need none of the tools below. The whole route is in the browser, and it is the one
to point somebody at who is not a developer (an assistant that finds `gh` on the machine tends to
choose the console route instead — that is how a first attempt ended in two red screens on 02.09.2026):

1. Open the **Use this template** form with the template already chosen:
   [github.com/new?template_owner=EncySoftware&template_name=ency-extension-template](https://github.com/new?template_owner=EncySoftware&template_name=ency-extension-template) — name the repository after the extension. The
   first push renames everything inside.
2. [apps.encycam.com/account](https://apps.encycam.com/account) → **Connect** — once, in the
   browser. No token, nothing goes into GitHub.
3. Put the code in `src/`, then **Actions → publish-to-ency-store → Run workflow** with the fields
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

- [CAM API reference](https://docs.encycam.com/CAMAPI/2/en/) — every interface, property and method.
- [Lessons](https://docs.encycam.com/CAMAPI/2/en/src/Lessons/Main.html) — the same API in order,
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

Until the package reaches nuget.org, install it from the `.nupkg` attached to the latest
[release](https://github.com/EncySoftware/ency-extension-mcp/releases):

```bash
dotnet tool install -g EncySoftware.ExtensionStoreMcp --add-source <folder with the .nupkg>
```

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
