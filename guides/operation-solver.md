---
storeType: operation
settingsKey: OperationSolver
---

# Operation solver — `ICamApiTechOperationSolver`

## When to use

You are writing the **toolpath algorithm itself**: ENCY hands you an operation and a CLD sink, you emit
the moves. This is the entry point behind real released extensions (punching, cladding, planar
slicing). Use it when the machining strategy does not exist in ENCY and you want it to appear as a
normal operation with its own parameters in the UI.

Not this kind if:
- the toolpath already exists and you only want to change it afterwards → `type-cldata-converter.mdc`;
- you want an action on an existing operation → `type-operation-popup.mdc`;
- you are exporting/reporting something → `type-utility.mdc`.

This is the heaviest entry point in the API. Read the two examples before writing code.

## Register it

The key is **`OperationSolver`** (PascalCase — that is what released extensions use; the store also
accepts the `operation` spelling):

```json
{
  "name": "My machining strategy",
  "description": "What it computes",
  "module_path": "${extensionJsonFolder}/MyExtension.dll",
  "extensions": [
    {
      "OperationSolver": {
        "id": "Extension.Operation.MyStrategy",
        "name": "My strategy operation",
        "caption": "My strategy"
      }
    }
  ]
}
```

`id` must match the branch in `CAMAPI.ExtensionFactory.Create`:

```csharp
"Extension.Operation.MyStrategy" => new MyOperationSolver(),
```

## Interface

Note the naming: the role interface starts with `ICamApi`, not `IExtension`.

```csharp
public interface ICamApiTechOperationSolver
{
    void InitSolver(ICamApiTechOperationSolverInitializeContext context, out TResultStatus resultStatus);
    void FinalizeSolver();
    bool GetPropIterator(string pageId, out IST_CustomPropIterator? iterator, out TResultStatus resultStatus);
    void OnPropFilterChanged(string parameterName, string value);
    void MakeWorkPath(ICamApiCLDReceiver cldFormer, ICamApiTechOperation techOperation,
                      out TResultStatus resultStatus);
}
```

- `InitSolver` — keep what you need from the context (the operation, the progress handler).
- `GetPropIterator` — return `true` plus an iterator to publish your parameters on a UI page;
  return `false` for pages that are not yours.
- `MakeWorkPath` — read parameters from `techOperation.XMLProp`, emit moves into `cldFormer`.
- `FinalizeSolver` — release everything kept in `InitSolver`.

Parameters come out of the XML property tree by path: `xmlProp.Int["ToolpathParams.Pattern"]`,
`xmlProp.Flt["ToolpathParams.ZLayers.ZStep"]`.

Moves go in through `cldFormer`: `AddComment(...)`,
`OutStandardFeed((int)TFeedTypeFlag.affRapid | affPlunge | affWorking)`, `CutTo(TST3DPoint)`,
`ArcTo2d(end, center, TCLDPlaneType.aplXY, radius, clockwise)`.

## Skeleton

```csharp
using CAMAPI.DotnetHelper;
using CAMAPI.Extensions;
using CAMAPI.MCDFormerTypes;
using CAMAPI.ResultStatus;
using CAMAPI.TechOperation;
using STCustomPropTypes;
using STTypes;

namespace MyExtension;

public class MyOperationSolver : IExtension, ICamApiTechOperationSolver
{
    public IExtensionInfo? Info { get; set; }

    public void InitSolver(ICamApiTechOperationSolverInitializeContext context,
                           out TResultStatus resultStatus) => resultStatus = default;

    public void FinalizeSolver() { }

    public bool GetPropIterator(string pageId, out IST_CustomPropIterator? iterator,
                                out TResultStatus resultStatus)
    {
        resultStatus = default;
        iterator = null;
        return false;   // true + iterator to expose your own parameter page
    }

    public void OnPropFilterChanged(string parameterName, string value) { }

    public void MakeWorkPath(ICamApiCLDReceiver cldFormer, ICamApiTechOperation techOperation,
                             out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            using var xmlPropCom = ComWrapper.Create(techOperation.XMLProp);
            if (xmlPropCom.IsNull) throw new Exception("Can't get XML properties");
            // Reads go through Invoke (the apartment-thread switch); .Instance is obsolete.
            double zStart = xmlPropCom.Invoke(x => x.Flt["ToolpathParams.ZLayers.ZStart"]);
            double zStep  = xmlPropCom.Invoke(x => x.Flt["ToolpathParams.ZLayers.ZStep"]);
            int layers    = xmlPropCom.Invoke(x => x.Int["ToolpathParams.ZLayers.Count"]);

            cldFormer.AddComment("Go to start point");
            cldFormer.OutStandardFeed((int)TFeedTypeFlag.affRapid);
            cldFormer.CutTo(new TST3DPoint { X = 0, Y = 0, Z = zStart + 10 });

            for (int i = 0; i < layers; i++)
            {
                double z = zStart - i * zStep;
                cldFormer.OutStandardFeed((int)TFeedTypeFlag.affPlunge);
                cldFormer.CutTo(new TST3DPoint { X = 0, Y = 0, Z = z });
                cldFormer.OutStandardFeed((int)TFeedTypeFlag.affWorking);
                cldFormer.CutTo(new TST3DPoint { X = 50, Y = 0, Z = z });
                cldFormer.CutTo(new TST3DPoint { X = 50, Y = 50, Z = z });
            }
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

- **The interface is `ICamApiTechOperationSolver`, not `IExtension…`.** Easy to grab the wrong name
  and then wonder why ENCY never calls you.
- **`MakeWorkPath` runs on a calculation thread.** No dialogs, no windows, no UI access. Report progress
  through the handler from `InitSolver`, log through
  `ExtensionManagerHelper.GetInstance().InvokeAndWrap(m => m.Logger)`.
- **An escaping exception breaks the whole project calculation.** Catch everything, report
  `TResultStatusCode.rsError` with a message a machinist can act on.
- **`GetPropIterator` is called for pages you do not own** — return `false` there instead of throwing.
- **Release in `FinalizeSolver` whatever `InitSolver` kept.** A held COM reference keeps the library
  loaded and blocks updates.
- **Feed mode is stateful.** `OutStandardFeed` applies to every following `CutTo` until changed — a
  missing `affRapid` turns a repositioning move into a cut through material.
- **Parameter paths are strings.** A typo in `xmlProp.Flt["…"]` returns a default value silently;
  verify against the XML the operation actually has.

## Go deeper

- Upstream reference: `cam-api-examples/docs/general/extension-entry-points.md` →
  "IExtensionTypeInfoOperationSolver → ICamApiTechOperationSolver"; CLD and simulation:
  `docs/api/nc-simulation.md`; parameter UI: `docs/general/ui-patterns.md` (inspector pattern).
- Working examples: `Operation/ExtensionOperationSimpleNet` (rectangle/circle layers — read this one
  first), `Operation/ExtensionOperationParamsNet` (own parameter page via `GetPropIterator`).
- Repo: <https://github.com/EncySoftware/cam-api-examples>
- In the store this lands as type `operation`.
