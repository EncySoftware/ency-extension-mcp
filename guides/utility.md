---
storeType: utility
settingsKey: utility
---

# Utility extension — `IExtensionUtility`

## When to use

The user should get a **button** (utilities menu / toolbar) and your code runs when they click it. This
is the default and by far the most common kind: exporters, importers, project reports, batch edits,
anything that starts from "the user asks for it". You get the whole application through the context,
so a utility can read and change the active project, tools, machine and geometry.

Not this kind if:
- the action belongs to the right-click menu of an operation → `type-operation-popup.mdc`;
- the action belongs to the right-click menu of a geometry tree node → `type-geom-node-popup.mdc`;
- nobody clicks anything, the code must run at startup → `type-global.mdc`;
- you are computing a toolpath → `type-operation-solver.mdc`.

This template ships a utility already (`src/Extension.cs`) — extend it instead of starting over.

## Register it

`src/<Name>.settings.json` — the key is `utility` (ENCY matches it case-insensitively; examples use
both `utility` and `Utility`):

```json
{
  "name": "My extension",
  "description": "What it does",
  "module_path": "${extensionJsonFolder}/MyExtension.dll",
  "extensions": [
    {
      "utility": {
        "id": "Extension.Utility.MyExtension",
        "name": "Copy the active project elsewhere",
        "caption": "Export project copy",
        "icon_path": "./MyExtension.png"
      }
    }
  ]
}
```

- `caption` is the button label, `name` is the longer description, `icon_path` is relative to the dll.
- `id` is the contract with the factory: the same string must appear in `ExtensionFactory.Create`.

```csharp
// src/ExtensionFactory.cs — namespace and class name must stay CAMAPI.ExtensionFactory
public IExtension? Create(string extensionIdent, out TResultStatus ret)
{
    ret = default;
    return extensionIdent switch
    {
        "Extension.Utility.MyExtension" => new UtilityExtension(),
        _ => throw new Exception("Unknown extension identifier: " + extensionIdent)
    };
}
```

One dll can hold many utilities: one JSON entry + one `switch` branch each.

## Interface

```csharp
public interface IExtension            { IExtensionInfo? Info { get; set; } }
public interface IExtensionUtility     { void Run(IExtensionUtilityContext context,
                                                  out TResultStatus resultStatus); }
```

`context.CamApplication` (`ICamApiApplication`) is the root of everything: `GetActiveProject()`,
`GetExtensionManager()`, tools manager, machines library, main window handle, `ExportCurrentProject`
and the rest. `Info` is set by ENCY — declare it as a plain auto-property and leave it alone.

## Skeleton

```csharp
using CAMAPI.Application;
using CAMAPI.DotnetHelper;
using CAMAPI.Extensions;
using CAMAPI.ResultStatus;

namespace MyExtension;

public class UtilityExtension : IExtension, IExtensionUtility
{
    public IExtensionInfo? Info { get; set; }

    public void Run(IExtensionUtilityContext context, out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            using var appCom = ComWrapper.Create(context.CamApplication);
            if (appCom.IsNull) throw new Exception("Application not found");

            using var projectCom = appCom.GetActiveProject();
            if (projectCom.IsNull)
                throw new Exception("No active project");

            // Your work here. Calls that report through their own status:
            // Every call on the COM object goes through Invoke - it runs on the thread the object
            // belongs to. Reading .Instance and calling it directly is obsolete (CS0618).
            resultStatus = appCom.Invoke(a => { a.ExportCurrentProject(@"C:\temp\copy.stcp", true, out var st); return st; });
            if (resultStatus.Code == TResultStatusCode.rsError)
                throw new Exception("Export failed: " + resultStatus.Description);
        }
        catch (Exception e)
        {
            resultStatus.Code = TResultStatusCode.rsError;
            resultStatus.Description = e.Message;
        }
    }
}
```

## Gotchas

- **Exceptions must not leave `Run`.** Catch everything and report through `resultStatus`
  (`Code = TResultStatusCode.rsError`, `Description = ...`). An escaping exception crosses the COM
  boundary and takes ENCY with it.
- **Every COM object goes through `ComWrapper`/`using`.** Raw references keep ENCY objects alive and
  the extension unloadable. `ComWrapper.Create(...)`, `.Invoke(...)`, `.InvokeAndWrap(...)`, `.IsNull` - never `.Instance`, it is
  obsolete (CS0618) because it skips the apartment-thread switch.
- **Check the status of calls that hand you one.** Many API methods return `out TResultStatus`
  instead of throwing — a silently ignored `rsError` looks like success.
- **A cancelled dialog is not an error.** If the user closes your folder/file picker, `return` with
  the default status; do not report `rsError`.
- **Want your own window?** Modal WPF/WinForms needs an STA thread and no COM inside it; a window
  that outlives `Run` needs `IExtensionLazyUnloadable`. Read `ency-cookbook.mdc` first.
- **`id` drift.** Renaming the extension without updating both the JSON `id` and the factory branch
  gives "Unknown extension identifier" at click time, not at build time.

## Go deeper

- Upstream reference: `cam-api-examples/docs/general/extension-entry-points.md` →
  "IExtensionTypeInfoUtility → IExtensionUtility"; application API: `docs/api/application.md`.
- Working examples: `ExtensionUtility/ExtensionUtilityNet/project/main/ExtensionUtility.cs` (project
  export with a folder dialog), `ExtensionEmpty/ExtensionEmptyNet` (the bare minimum),
  `Project/ExtensionUtilityExportInformationNet` (two utilities in one dll),
  `Geometry/*` (geometry importers — all of them are utilities).
- Repo: <https://github.com/EncySoftware/cam-api-examples>
- In the store this lands as type `utility`.
