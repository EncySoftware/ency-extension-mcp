<#
.SYNOPSIS
    Generates the Cursor rules snapshot in the extension template from guides/*.md.

.DESCRIPTION
    The guides in this repo are the single source of truth: the get_extension_guide MCP tool serves
    them from embedded resources, and this script writes them into the template repo as .mdc rules so
    a freshly created extension repo carries them without the MCP server.

    Run it after editing anything under guides/, then commit the template repo.

.EXAMPLE
    powershell -NoProfile -File tools\sync-rules.ps1
    powershell -NoProfile -File tools\sync-rules.ps1 -Check
#>
[CmdletBinding()]
param(
    # Template repo checkout. Default: sibling directory of this repo.
    [string]$TemplateDir,
    # Compare only: exit 1 when the snapshot drifted from guides/.
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not populated while parameter defaults are bound on PS 5.1, so resolve here.
$repoDir = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($TemplateDir)) {
    $TemplateDir = Join-Path (Split-Path $repoDir -Parent) 'ency-extension-template'
}

$guidesDir = Join-Path $repoDir 'guides'
$rulesDir = Join-Path $TemplateDir '.cursor\rules'
if (-not (Test-Path $rulesDir)) { throw "rules dir not found: $rulesDir" }

$index = Get-Content (Join-Path $guidesDir '_index.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$drift = @()

# Cursor reads the frontmatter from byte 0, so the rules must be UTF-8 WITHOUT a BOM.
# Set-Content -Encoding utf8 on PS 5.1 writes one, hence the explicit encoder.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Collect every generated file first, then write or compare - both modes share one list.
$outputs = @()

foreach ($g in $index.guides) {
    # -Encoding UTF8 matters: without it PS 5.1 reads the file as ANSI and mangles every dash.
    $body = Get-Content (Join-Path $guidesDir $g.file) -Raw -Encoding UTF8
    # Drop the guide's own frontmatter - the .mdc gets Cursor's one instead.
    $body = [regex]::Replace($body, '^---\r?\n.*?\r?\n---\r?\n', '', 'Singleline')

    $header = @"
---
description: $($g.description)
alwaysApply: false
---

<!-- Generated from guides/$($g.file) in EncySoftware/ency-extension-mcp.
     Edit it there and run tools/sync-rules.ps1 - changes made here are overwritten. -->

"@
    $outputs += [pscustomobject]@{
        Name = $g.cursorRule
        Path = Join-Path $rulesDir $g.cursorRule
        Text = $header + $body
    }
}

# AGENTS.md - the cross-tool entry point. Cursor picks the rules up on its own, but Claude Code,
# Codex and Copilot read AGENTS.md instead, so they need a router: what this repo is, which guide to
# open for which kind of extension, and how publishing works. It points at the same .mdc files
# (plain markdown, any agent can read them) so nothing is duplicated.
$rows = ($index.guides | Where-Object { $_.storeType -ne '' } | ForEach-Object {
    "| $($_.description) | ``$($_.key)`` | ``.cursor/rules/$($_.cursorRule)`` |"
}) -join "`n"

$agents = @"
# Working on this ENCY extension

<!-- Generated from guides/_index.json in EncySoftware/ency-extension-mcp.
     Edit the guides there and run tools/sync-rules.ps1 - changes made here are overwritten. -->

This repo is one extension for **ENCY 3**. GitHub Actions builds it and the ENCY Extension Store
packs and publishes it when you push a version tag.

**Write for ENCY 3, not ENCY 2.** The SDK is pinned in ``src/EncyExtension.csproj`` as
``EncySoftware.CAMAPI.Sdk.Net`` 3.0.1-rc.22 - a release candidate, because that is the only 3.x
published so far. Do not "fix" that by moving to a 2.x version: 2.x is the previous generation of the
product, and an extension built against it is an extension for the old ENCY. If the pinned rc is a
problem, say so instead of switching lines quietly. (An assistant that read the old instructions
spent an hour writing for ENCY 2 - hence this paragraph.)

**The API reference lives outside this repo.** Three places, three different questions:

- [CAM API reference](https://docs.encycam.com/CAMAPI/2/en/) - every interface, property and method.
  Go here to answer "what can I actually call".
- [Lessons](https://docs.encycam.com/CAMAPI/2/en/src/Lessons/Main.html) - the same API taught in
  order, starting from a first extension. Go here when the reference tells you what exists but not
  where to begin.
- [cam-api-examples/docs](https://github.com/EncySoftware/cam-api-examples/tree/v3/main/docs) - a
  worked example of every extension kind, with the code that compiles.

The guides below are the short path for ONE kind; those three are the whole picture, and they are
where to look when an interface here is not enough.

**If you write a PowerShell script for the author, run it as**
``powershell -ExecutionPolicy Bypass -NoProfile -File <script.ps1>``. A client Windows blocks ``.ps1``
by default ("running scripts is disabled on this system"), and a first-time author cannot tell that
error from a broken script. Prefer no script at all: publishing here needs none.

**Decide which entry point you need BEFORE writing extension code, then read its guide.** Each guide
gives the exact interface, the ``*.settings.json`` key, a compiling skeleton and the traps. The guides
are plain markdown under ``.cursor/rules/`` - open them directly. If the ``ency-extension-store`` MCP
server is connected, ``get_extension_guide`` serves the same text (``type=list`` lists every kind).

| What the extension should do | Kind | Guide |
|---|---|---|
$rows

Two guides apply to every change:

- ``.cursor/rules/ency-extension.mdc`` - repo anatomy: the ``CAMAPI.ExtensionFactory`` contract,
  matching ids between ``*.settings.json`` and the factory, ``package.info.json``, how to build for
  packing.
- ``.cursor/rules/ency-cookbook.mdc`` - COM lifetime (``ComWrapper``), errors through
  ``TResultStatus``, asking the user for parameters, windows and STA rules.

## Publishing

**Starting from nothing?** ``create_extension_folder(name, dir)`` from the same MCP server makes
the project from the template on this machine - no GitHub account, no git; write the code in its
``src/``, then publish it as below.

**Preferred: ``publish_folder(name, folder)`` from the ``ency-extension-store`` MCP server** - no git,
no gh. The store creates the repository in the author's GitHub account, commits this folder into
``src/``, runs the build and returns the result: the version and the card link, or the failing step
with its log (fix the code, call it again). The author is needed twice, in the browser only - the
store sign-in and, once, the app's consent page - and the tool opens both itself. Server not connected? Install it once, no questions asked: ``dotnet tool install -g EncySoftware.ExtensionStoreMcp``. Run that yourself - it asks nothing - rather than handing it to the author, then ask the author to
restart the editor: this repository carries ``.mcp.json`` and ``.cursor/mcp.json`` that register the server
(a folder made before they existed: ``ency-extension-mcp setup``, also yours to run). The server is connected but has no ``publish_folder``? The tool is old - update it yourself, it asks
nothing: ``dotnet tool update -g EncySoftware.ExtensionStoreMcp``, then ask the author to restart the editor. The same route from a terminal: ``ency-extension-mcp publish-folder <Name> <folder>``.

**Never run ``gh auth login`` for the author** - it stops on a Y/n prompt that swallows the next
pasted command. Without the tool, the same route is the store page: https://apps.encycam.com/publish
-> **Code in a folder** -> pick this project's folder -> **Upload and publish**.

From a terminal where git and gh are already set up, a version tag does the same:

``````bash
git tag v1.2.3 && git push --tags
``````

Actions builds the project, the store packs the ENCY-format package and publishes it. A brand new
extension waits for a store moderator (its direct card link works immediately); new versions of an
approved extension go live at once.
"@

$outputs += [pscustomobject]@{
    Name = 'AGENTS.md'
    Path = Join-Path $TemplateDir 'AGENTS.md'
    Text = $agents
}

foreach ($o in $outputs) {
    if ($Check) {
        if (-not (Test-Path $o.Path)) {
            $drift += "$($o.Name) (missing)"
        }
        elseif ([System.IO.File]::ReadAllText($o.Path, $utf8NoBom) -ne $o.Text) {
            $drift += $o.Name
        }
    }
    else {
        [System.IO.File]::WriteAllText($o.Path, $o.Text, $utf8NoBom)
        Write-Output "wrote $($o.Name)"
    }
}

if ($Check) {
    if ($drift.Count -gt 0) {
        Write-Output ("drift: " + ($drift -join ', '))
        exit 1
    }
    Write-Output 'rules are in sync'
}
else {
    Write-Output "$($outputs.Count) files written to $TemplateDir"
}
