---
storeType: geom_model_node_popup
settingsKey: geom_model_node_popup
---

# Geometry node popup — `IExtensionGeomModelNodePopup`

## When to use

The action belongs to a **node of the 3D geometry model tree** and the user should find it by
right-clicking that node: show what the node is, export it, transform it, select everything of its
colour, feed it to your own analysis. Same shape as the operation popup — `Build` adds items, each item
has its own click handler.

Not this kind if:
- you right-click an operation, not geometry → `type-operation-popup.mdc`;
- the action is about the whole model or project → `type-utility.mdc` (a menu button; all the geometry
  importers in the examples are utilities).

## Register it

The key is `geom_model_node_popup`:

```json
{
  "name": "My geometry menu items",
  "description": "What they do",
  "module_path": "${extensionJsonFolder}/MyExtension.dll",
  "extensions": [
    {
      "geom_model_node_popup": {
        "id": "Extension.GeomModelNodePopup.MyExtension",
        "name": "Adds a popup item on geometry tree nodes"
      }
    }
  ]
}
```

`id` must match the branch in `CAMAPI.ExtensionFactory.Create`:

```csharp
"Extension.GeomModelNodePopup.MyExtension" => new GeomNodePopupExtension(),
```

## Interface

```csharp
public interface IExtension { IExtensionInfo? Info { get; set; } }

public interface IExtensionGeomModelNodePopup
{
    void Build(IExtensionGeomModelNodePopupBuildContext context, out TResultStatus resultStatus);
}
```

`IExtensionGeomModelNodePopupBuildContext` gives you `SelectedNode` (`ICAMAPIGeometryTreeNode` — the
right-clicked node), `ActiveProject` (`ICamApiProject`) and `NodePopup`
(`ICamApiGeomModelNodePopup`) with the same
`AddItem(name, caption, enabled, onClicked, out resultStatus)`.

The click handler is a separate class (see the example's `NodeFullNameAlertOnClicked`) receiving its own
context with the node and the project.

## Skeleton

```csharp
using CAMAPI.DotnetHelper;
using CAMAPI.Extensions;
using CAMAPI.GeometryModelForm;
using CAMAPI.ResultStatus;

namespace MyExtension;

public class GeomNodePopupExtension : IExtension, IExtensionGeomModelNodePopup
{
    public IExtensionInfo? Info { get; set; }

    public void Build(IExtensionGeomModelNodePopupBuildContext context, out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            var items = context.NodePopup;
            items.AddItem("ShowNodeFullName", "Show node full name", true,
                          new ShowNodeFullNameOnClicked(), out resultStatus);
        }
        catch (Exception e)
        {
            resultStatus.Code = TResultStatusCode.rsError;
            resultStatus.Description = e.Message;
        }
    }
}

/// The handler: the same shape as the operation-popup one. Everything from the context is a COM
/// object — wrap it, call through Invoke, catch everything, report through resultStatus.
public class ShowNodeFullNameOnClicked : ICamApiGeomModelNodePopupItemOnClicked
{
    public void OnItemClicked(IExtensionGeomModelNodePopupItemOnClickedContext context,
                              out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            using var nodeCom = ComWrapper.Create(context.SelectedNode);
            if (nodeCom.IsNull) throw new Exception("No node selected");
            string fullName = nodeCom.Invoke(n => n.FullName);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "node.txt"), fullName);
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

- **`Build` runs on every right-click.** Only `AddItem` belongs there; the work goes into the handler.
- **A node can be a group, a body, a face, a curve.** Check what `SelectedNode` actually is before
  acting — a transform that makes sense for a body is nonsense for a folder node.
- **Do not cache the context or the node.** They are valid for the current call only.
- **Skip or disable, do not fail.** For nodes you do not support, either do not add the item or add it
  with `enabled: false`.
- **Check the `out resultStatus` of every `AddItem`**, and never let an exception leave `Build` or the
  handler.
- **COM through `ComWrapper`/`using`** — tree nodes and the project are ENCY objects.

## Go deeper

- Upstream reference: `cam-api-examples/docs/general/extension-entry-points.md` →
  "IExtensionTypeInfoGeomModelNodePopup → IExtensionGeomModelNodePopup"; geometry API:
  `docs/api/geometry.md`. The reference shows `TResultStatus Build(context)`; the shipping example uses
  `void Build(context, out TResultStatus)` — follow the example.
- Working example: `GeometryModelNodePopup/NodeFullNameAlert/project/main/ExtensionGeomModelNodePopup.cs`
  plus its `NodeFullNameAlertOnClicked` handler. For reading geometry itself see
  `Geometry/UtilityGeometryEntityReader` and `Geometry/ExtensionUtilityGeometryModelNet`.
- Repo: <https://github.com/EncySoftware/cam-api-examples>
- In the store this lands as type `geom_model_node_popup`.
