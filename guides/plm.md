---
storeType: plm
settingsKey: PLM
---

# PLM integration — `IExtensionPLM`

> **Not covered by the upstream reference.** `docs/general/extension-entry-points.md` documents seven
> entry points; PLM is the eighth and lives only as an example. Everything below is read off the
> shipping `PLMIntegration/PLMExtensionNet` sources.
>
> **Do not write this one from scratch.** Copy `PLMIntegration/PLMExtensionNet` and cut it down —
> the interface is an order of magnitude bigger than any other entry point.

## When to use

ENCY must talk to a **product data management system**: browse its item tree, find items, download a
project (or model, machine, postprocessor, tool) from it, upload the machining result back. ENCY drives
the dialogs; your extension answers with data.

Not this kind if:
- you only need to copy files to a network folder on demand → `type-utility.mdc`;
- you are reacting to project events → `type-global.mdc`.

## Register it

The key is `PLM`:

```json
{
  "name": "My PLM connector",
  "description": "Connects ENCY to <system>",
  "module_path": "${extensionJsonFolder}/MyPlm.dll",
  "extensions": [
    {
      "PLM": {
        "id": "PLM.Extension.MyConnector",
        "name": "My PLM connector",
        "caption": "My PLM connector"
      }
    }
  ]
}
```

`id` must match the branch in `CAMAPI.ExtensionFactory.Create`.

## Interface

`IExtension` + `IExtensionPLM`. Capability flags first — ENCY builds its UI from them:

```csharp
bool SupportProjectLoad { get; }                    bool SupportToolLoad { get; }
bool SupportModelLoad { get; }                      bool SupportToolSave { get; }
bool SupportMachineLoad { get; }                    bool SupportDomainAuth { get; }
bool SupportPostprocessorLoad { get; }
bool SupportPostprocessorInsideMachineLoad { get; }
```

Session and parameters:

```csharp
void SetLanguage(uint languageID, byte codePage);
IPLMParameters GetParameters();                     // the connection fields ENCY renders
IPLMResult Connect(IPLMParameterValues values, Guid connectionId, bool useDomainAuth);
IPLMResult Disconnect();
IPLMResult Install();
IPLMResult Uninstall();
```

Browsing and transfer — every method reports through `IPLMResult`, data comes back in `out` parameters:

```csharp
IPLMResult GetItem(TPLMItemType itemType, string itemId, out IPLMTree? items);
IPLMResult GetLinkedItem(TPLMItemType itemType, TPLMItemType linkedItemType, string itemId,
                         out IPLMTree? items);
IPLMResult GetChilds(TPLMItemType itemType, string parentItemId, out IPLMTree? items);
IPLMResult FindItems(TPLMItemType itemType, string itemName, out IPLMTree items);
IPLMResult GetItemData(TPLMItemType itemType, string itemId, out IPLMDataItem itemData);
IPLMResult DownloadItems(IPLMItems items, string downloadPath, out IPLMDataItems dwnItems);
IPLMResult DownloadProject(string itemId, string downloadPath, out IPLMDataItems dwnItems,
                          out IPLMProjectStructItems prjStructItems);
IPLMResult UploadProject(IPLMCAMProject project, bool saveAs, bool replace);
IPLMResult UploadItem(TPLMItemType itemType, string itemId, IPLMFiles files,
                     IPLMItemAttributes itemAttributes, bool replace, out IPLMDataItems uplItems);
```

## Skeleton

A frame, not a full implementation — the point is the shape: flags, connect, browse, download, and a
shared success/failure helper. Everything you have not implemented yet returns a successful stub or a
failure with a clear message; never leave a method throwing.

```csharp
using CAMAPI.Extensions;
using CAMAPI.Extension.PLM;

namespace MyPlm;

public class MyPlmExtension : IExtensionPLM, IExtension
{
    public IExtensionInfo? Info { get; set; }

    public bool SupportProjectLoad => true;
    public bool SupportModelLoad => true;
    public bool SupportMachineLoad => false;
    public bool SupportPostprocessorLoad => false;
    public bool SupportPostprocessorInsideMachineLoad => false;
    public bool SupportToolLoad => false;
    public bool SupportToolSave => false;
    public bool SupportDomainAuth => false;

    private Guid _connection;
    private readonly PlmParameters _parameters = new();

    public void SetLanguage(uint languageID, byte codePage) { }

    // ENCY reads these to draw the connection, login and settings dialogs - fill them in.
    public IPLMParameters GetParameters() => _parameters;

    public IPLMResult Connect(IPLMParameterValues values, Guid connectionId, bool useDomainAuth)
    {
        try
        {
            _connection = connectionId;                     // keep it: it identifies the session
            // open the session against your PLM here
            return Success();
        }
        catch (Exception e)
        {
            return Failure(e.Message);
        }
    }

    public IPLMResult Disconnect() => Success();
    public IPLMResult Install() => Success();
    public IPLMResult Uninstall() => Success();

    public IPLMResult GetChilds(TPLMItemType itemType, string parentItemId, out IPLMTree items)
    {
        items = null!;
        try
        {
            // fill items with the children of parentItemId
            return Success();
        }
        catch (Exception e)
        {
            return Failure(e.Message);
        }
    }

    public IPLMResult DownloadProject(string itemId, string downloadPath, out IPLMDataItems dwnItems,
                                      out IPLMProjectStructItems prjStructItems)
    {
        dwnItems = null!;
        prjStructItems = null!;
        try
        {
            // copy the project files into downloadPath and describe them in dwnItems
            return Success();
        }
        catch (Exception e)
        {
            return Failure(e.Message);
        }
    }

    // The rest of the surface, same pattern. A Failure() stub is honest: ENCY shows the message
    // instead of pretending the operation happened.
    public IPLMResult GetItem(TPLMItemType itemType, string itemId, out IPLMTree items)
    { items = null!; return Failure("GetItem is not implemented yet"); }
    public IPLMResult GetLinkedItem(TPLMItemType itemType, TPLMItemType linkedItemType, string itemId, out IPLMTree items)
    { items = null!; return Failure("GetLinkedItem is not implemented yet"); }
    public IPLMResult FindItems(TPLMItemType itemType, string itemName, out IPLMTree items)
    { items = null!; return Failure("FindItems is not implemented yet"); }
    public IPLMResult GetItemData(TPLMItemType itemType, string itemId, out IPLMDataItem itemData)
    { itemData = null!; return Failure("GetItemData is not implemented yet"); }
    public IPLMResult DownloadItems(IPLMItems items, string downloadPath, out IPLMDataItems dwnItems)
    { dwnItems = null!; return Failure("DownloadItems is not implemented yet"); }
    public IPLMResult UploadItem(TPLMItemType itemType, string itemId, IPLMFiles files, IPLMItemAttributes attributes,
                                 bool overwrite, out IPLMDataItems uplItems)
    { uplItems = null!; return Failure("UploadItem is not implemented yet"); }
    public IPLMResult UploadProject(IPLMCAMProject project, bool withModels, bool withTools)
        => Failure("UploadProject is not implemented yet");

    // IPLMResult is an interface: the extension brings its own carrier.
    private static IPLMResult Success() => new PlmResult(0, "", "");
    private static IPLMResult Failure(string message) => new PlmResult(1, message, "");

    private sealed class PlmResult(int code, string error, string warning) : IPLMResult
    {
        public int Code => code;
        public string ErrorMessage => error;
        public string WarningMessage => warning;
    }

    private sealed class PlmParameters : IPLMParameters
    {
        public IPLMConnectionParameters Connection { get; set; } = null!;
        public IPLMLoginParameters Login { get; set; } = null!;
        public IPLMProjectPreview ProjectPreview { get; set; } = null!;
        public IPLMSettingsParameters Settings { get; set; } = null!;
    }
}
```

## Gotchas

- **Results, not exceptions.** These methods return `IPLMResult`; an exception crossing the boundary
  kills ENCY. Wrap every method body.
- **The `Support*` flags are promises.** ENCY enables dialogs based on them and will call what you
  advertised — a `true` with an unimplemented method is a user-visible dead end.
- **Keep `connectionId`.** It is how ENCY refers to the session across calls; store it on `Connect` and
  invalidate it on `Disconnect`.
- **Never hardcode storage paths.** The example keeps all path arithmetic in a `PLMDirectoryHelper`
  (subdirectories, copy in/out, zip when multiple, group detection) — copy that helper, it is most of
  the boring work.
- **`out` parameters must be filled even on failure** (empty tree / empty items), otherwise the caller
  gets nulls it does not expect.
- **No upstream doc.** Verify behaviour against a live ENCY; the example plus the interface XML docs are
  the only contract.

## Go deeper

- Working examples: `PLMIntegration/PLMExtensionNet/project/main/src` — `PLMExtensionNet.cs` (the
  interface implementation), `PLMDirectoryHelper.cs` (file/folder mechanics), `Parameters/`, `Tree/`,
  `Items/` (how the returned structures are built). A Delphi twin sits in `PLMExtensionDelphi`.
- Repo: <https://github.com/EncySoftware/cam-api-examples>
- In the store this lands as type `plm`.
