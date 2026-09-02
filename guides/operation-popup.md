---
storeType: operation_popup
settingsKey: operation_popup
---

# Operation popup — `IExtensionOperationPopup`

## When to use

The action belongs to **one operation** and the user should find it by right-clicking that operation in
the technology tree: recalculate this operation, dump its CLData, show its parameters, copy its tool
settings, generate NC for it alone. `Build` is called every time the menu opens; you add items, and
each item's own handler does the work when clicked.

Not this kind if:
- the action is about the whole project → `type-utility.mdc` (a menu button);
- you right-click geometry, not an operation → `type-geom-node-popup.mdc`;
- you are computing the toolpath itself → `type-operation-solver.mdc`.

## Register it

The key is `operation_popup`:

```json
{
  "name": "My operation menu items",
  "description": "What they do",
  "module_path": "${extensionJsonFolder}/MyExtension.dll",
  "extensions": [
    {
      "operation_popup": {
        "id": "Extension.OperationPopup.MyExtension",
        "name": "Adds a popup item that calculates NC for the selected operation"
      }
    }
  ]
}
```

One dll can register several popup extensions (`Attributes/ExtensionAttributesManageNet` ships three)
— each with its own `id` and its own branch in `CAMAPI.ExtensionFactory.Create`:

```csharp
"Extension.OperationPopup.MyExtension" => new OperationPopupExtension(),
```

## Interface

```csharp
public interface IExtension { IExtensionInfo? Info { get; set; } }

public interface IExtensionOperationPopup
{
    void Build(IExtensionOperationPopupBuildContext context, out TResultStatus resultStatus);
}

public interface ICamApiTechnologyFormOperationPopupItemOnClicked
{
    void OnItemClicked(IExtensionOperationPopupItemOnClickedContext context,
                       out TResultStatus resultStatus);
}
```

`IExtensionOperationPopupBuildContext` gives you `SelectedOperation` (`ICamApiTechOperation` — the
right-clicked operation: name, type, `XMLProp`, tool, machine, LCS, flags), `ActiveProject`
(`ICamApiProject`) and `OperationPopup` (`ICamApiTechnologyFormOperationPopup`) whose
`AddItem(name, caption, enabled, onClicked, out resultStatus)` builds the menu.

The click context carries `ActiveProject` and the operation, so the handler does not depend on state
you captured during `Build`.

## Skeleton

```csharp
using CAMAPI.DotnetHelper;
using CAMAPI.Extensions;
using CAMAPI.ResultStatus;
using CAMAPI.TechnologyForm;

namespace MyExtension;

public class OperationPopupExtension : IExtension, IExtensionOperationPopup
{
    public IExtensionInfo? Info { get; set; }

    public void Build(IExtensionOperationPopupBuildContext context, out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            var items = context.OperationPopup;
            items.AddItem("MyAction", "My action", true, new MyActionOnClicked(), out resultStatus);
        }
        catch (Exception e)
        {
            resultStatus.Code = TResultStatusCode.rsError;
            resultStatus.Description = e.Message;
        }
    }
}

public class MyActionOnClicked : ICamApiTechnologyFormOperationPopupItemOnClicked
{
    public void OnItemClicked(IExtensionOperationPopupItemOnClickedContext context,
                              out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            using var projectCom = ComWrapper.Create(context.ActiveProject);
            // The real work goes here — this runs on the user's click, not while building the menu.
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

- **`Build` runs on every menu opening.** Keep it to `AddItem` calls — no file IO, no calculations, no
  dialogs. All the work belongs in `OnItemClicked`.
- **Do not cache the context.** `SelectedOperation`, `ActiveProject` and the popup builder are valid
  for that one call; the handler gets its own context.
- **Decide visibility inside `Build`.** Either skip `AddItem` for operations you do not support, or add
  it with `enabled: false` so the user sees why nothing happens. Read the operation type from
  `SelectedOperation` first.
- **Two `out` statuses, two checks.** `AddItem` reports through its own `out resultStatus`; do not
  overwrite it blindly if you add several items — check it after each call.
- **Exceptions must not leave `Build` or `OnItemClicked`** — catch and report through the status.
- **COM through `ComWrapper`/`using`** in the handler; the operation and project are ENCY objects.

## Go deeper

- Upstream reference: `cam-api-examples/docs/general/extension-entry-points.md` →
  "IExtensionTypeInfoOperationPopup → IExtensionOperationPopup". Note that the reference shows
  `TResultStatus Build(context)`; the shipping examples use
  `void Build(context, out TResultStatus)` — follow the examples.
- Working examples: `ExtensionOperationPopup/ExtensionOperationPopupNet` (NC for one operation, with a
  separate `MakeNcOnClicked` handler), `ExtensionOperationPopup/ExtensionOperationPopupOnChangeNet`
  (reacting to a tool change), `Attributes/ExtensionAttributesManageNet` (three popup items in one
  dll).
- Repo: <https://github.com/EncySoftware/cam-api-examples>
- In the store this lands as type `operation_popup`.
