---
storeType: utility_runner
settingsKey: utility_runner
---

# Utility runner — `IExtensionUtilityRunner`

> **No working example ships in `cam-api-examples`.** This guide is built from the upstream reference
> only; verify the behaviour against a live ENCY before you promise it to users.

## When to use

You are not adding an action — you are changing **how an existing utility gets executed**. ENCY calls
the runner instead of (or around) the plain utility run, so the runner can supply the input, wrap the
call, or redirect it. Typical work: a custom file selector that feeds the utility, a pre-flight check,
post-processing of what the utility produced, logging or telemetry around utility runs.

Not this kind if:
- you just want your own action on a button → `type-utility.mdc`;
- the behaviour belongs to one specific extension you own → put it inside that extension, a runner is
  a cross-cutting hook and affects the utilities of its group.

## Register it

The key is `utility_runner`:

```json
{
  "name": "My utility runner",
  "description": "What it wraps",
  "module_path": "${extensionJsonFolder}/MyExtension.dll",
  "extensions": [
    {
      "utility_runner": {
        "id": "Extension.UtilityRunner.MyExtension",
        "name": "Runs utilities through my file selector",
        "group": "Extension.Util.Common",
        "FilterTypes": "Text files|*.txt|All files|*.*",
        "VisibleInEditor": true
      }
    }
  ]
}
```

- `FilterTypes` — file filter for the selector dialog ENCY shows for this runner.
- `VisibleInEditor` — whether the runner shows up in the utility editor.
- **`group` decides which utilities this runner serves** — ENCY looks up runners by group (upstream
  names `Extension.Util.Common` as the utility group). Get it wrong and the runner is never called.
- `id` must match the branch in `CAMAPI.ExtensionFactory.Create`.

## Interface

```csharp
public interface IExtension              { IExtensionInfo? Info { get; set; } }
public interface IExtensionUtilityRunner { void Execute(IExtensionUtilityRunnerContext context,
                                                        out TResultStatus resultStatus); }
```

`IExtensionUtilityRunnerContext` carries two things:
- `UtilButtonContext` (`IUtilButtonContext`) — properties of the utility button being run;
- `UtilityContext` (`IExtensionUtilityContext`) — the standard utility context, i.e.
  `.CamApplication` (`ICamApiApplication`) and everything under it.

## Skeleton

```csharp
using CAMAPI.Application;
using CAMAPI.DotnetHelper;
using CAMAPI.Extensions;
using CAMAPI.ResultStatus;

namespace MyExtension;

public class UtilityRunnerExtension : IExtension, IExtensionUtilityRunner
{
    public IExtensionInfo? Info { get; set; }

    public void Execute(IExtensionUtilityRunnerContext context, out TResultStatus resultStatus)
    {
        resultStatus = default;
        try
        {
            var button = context.UtilButtonContext;          // which utility is being run
            using var appCom = ComWrapper.Create(context.UtilityContext.CamApplication);
            if (appCom.IsNull) throw new Exception("Application not found");

            // Pre-processing: gather input, validate, ask the user.
            // Then let the actual work happen (run the target utility / do the wrapped action).
            // Then post-processing on its result.
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

- **A runner that does nothing kills the button.** You are in the execution path: if you neither run
  the target action nor reproduce it, the user clicks and nothing happens. Always define what the
  "pass through" branch is.
- **`group` is the wiring.** Runner not firing is almost always a group mismatch, not a code bug.
- **Cancellation is not an error.** User closed your selector → return with the default status.
- **Exceptions must not leave `Execute`** — catch everything and report through `resultStatus`.
- **COM through `ComWrapper`/`using`**, and do not stash context objects in fields: the context is
  valid for this call only.
- **Unverified type.** Since there is no upstream sample, test the whole loop (button → runner →
  utility) on a live ENCY before publishing.

## Go deeper

- Upstream reference: `cam-api-examples/docs/general/extension-entry-points.md` →
  "IExtensionTypeInfoUtilityRunner → IExtensionUtilityRunner".
- Closest working code to read: any utility (`ExtensionUtility/ExtensionUtilityNet`) plus
  `docs/general/extension-entry-points.md` "Extension JSON Registration" for the `group` semantics.
- Repo: <https://github.com/EncySoftware/cam-api-examples>
- In the store this lands as type `utility_runner`.
