# AOBMaker CE Plugin Integration

> UE5DumpUI communicates with the [AOBMaker](https://github.com/bbfox0703/AOBMaker) Cheat Engine Plugin via a dedicated named pipe to provide seamless CE navigation, script injection, and symbol registration.

---

## Architecture Overview

```
UE5DumpUI (C# Avalonia)                AOBMaker CE Plugin (Lua)
┌──────────────────────┐                ┌─────────────────────┐
│  AobMakerBridgeService│──── pipe ────▶│  Named Pipe Server   │
│  (IAobMakerBridge)   │◀─── pipe ─────│  (Lua JSON handler)  │
│                      │                │                      │
│  PointerPanelVM      │                │  Memory Viewer nav   │
│   - HEX buttons      │                │  Disassembler nav    │
│   - ASM buttons      │                │  AA Script creation  │
│   - SYM registration │                │  Symbol registration │
│                      │                │                      │
│  LiveWalkerVM        │                │                      │
│   - Field HEX nav    │                │                      │
│   - Ptr HEX nav      │                │                      │
│   - Object HEX nav   │                │                      │
│   - Invoke scripts   │                │                      │
└──────────────────────┘                └─────────────────────┘
       \\.\pipe\AOBMakerCEBridge
```

---

## Wire Protocol

| Item | Value |
|------|-------|
| Pipe name | `\\.\pipe\AOBMakerCEBridge` |
| Direction | Duplex (InOut) — request/response |
| Connection model | **Per-request reconnect** — CE Plugin disconnects after each response |
| Framing | 4-byte LE `uint32` length prefix + UTF-8 JSON payload |
| Connect timeout | 2000 ms |
| Response timeout | 5000 ms |
| Max message size | 10 MB |
| JSON encoder | `UnsafeRelaxedJsonEscaping` for requests (CE Lua parser cannot handle `\uXXXX` escapes) |

### Message Format

Every message (request and response) uses the same `AobMakerMessage` model:

```json
{
  "type": "NavigateHexView",
  "address": "7FF769E29110",
  "success": true,
  "message": "OK"
}
```

Common fields:

| Field | Type | Direction | Description |
|-------|------|-----------|-------------|
| `type` | string | request | Message type identifier |
| `address` | string? | request | Hex address without `0x` prefix |
| `success` | bool | response | Whether the operation succeeded |
| `message` | string? | response | Error detail on failure |

---

## Message Types

### 1. `NavigateHexView`

Navigate CE Memory Viewer hex dump (bottom pane) to a specific address.

```json
// Request
{ "type": "NavigateHexView", "address": "2DA53B24970" }

// Response
{ "type": "NavigateHexView", "success": true }
```

**Used by:**
- PointerPanel: HEX buttons for GObjects / GNames / GWorld data addresses
- LiveWalker: Field address HEX, pointer target HEX, object address HEX

### 2. `NavigateDisassembler`

Navigate CE Memory Viewer disassembler (top pane) to a specific code address.

```json
// Request
{ "type": "NavigateDisassembler", "address": "7FF7F3456789" }

// Response
{ "type": "NavigateDisassembler", "success": true }
```

**Used by:**
- PointerPanel: ASM buttons for GObjects / GNames / GWorld AOB scan hit addresses (the instruction that references the global pointer)

### 3. `CreateAAScript`

Create an Auto Assembler script entry in CE's address list.

```json
// Request
{
  "type": "CreateAAScript",
  "description": "Invoke: BP_SantiagoGameInstance_C::GetSkillManager",
  "script": "[ENABLE]\n...\n[DISABLE]\n...",
  "autoActivate": false
}

// Response
{ "type": "CreateAAScript", "success": true }
```

| Field | Type | Description |
|-------|------|-------------|
| `description` | string | Display name in CE address list |
| `script` | string | Full AA script content (`[ENABLE]`/`[DISABLE]` sections) |
| `autoActivate` | bool | Whether to activate immediately after creation |

**Used by:**
- LiveWalker: `GenerateInvokeScriptAsync` sends UFunction invoke scripts directly to CE (falls back to clipboard if AOBMaker unavailable)

### 4. `InjectTableFile`

Embed an arbitrary text/Lua file straight into the currently open Cheat Engine table. Replaces existing TableFile of the same name (delete-if-exists), then `createTableFile` + `Stream.write` + verify `Stream.Size`. Used by the **Tools -> Inject Helper into Current CE Table** menu so the user no longer has to save the helper to disk and `Table -> Add File...` it manually.

```json
// Request
{
  "type": "InjectTableFile",
  "fileName": "ue5_invoke_helper.lua",
  "content": "if not invokeUFunction then\n  function invokeUFunction(...)\n    ...\n  end\nend\n"
}

// Response
{ "type": "InjectTableFileResult", "success": true }
```

| Field | Type | Description |
|-------|------|-------------|
| `fileName` | string | Filename to register inside the .CT (case-sensitive — AA scripts use the same name in `findTableFile`) |
| `content` | string | Raw UTF-8 file content. The CE Plugin chooses a long-bracket level dynamically so any payload is safe |

The plugin handler runs `synchronize(function() ... end)` so all CE Lua APIs (`findTableFile`, `createTableFile`, `Stream.write`) execute on CE's main thread. Self-verifies via `f.Stream.Size == #content` before returning success. Bridge timeout is **15 s** (vs. the 5 s navigation default) to give the synchronize round-trip headroom for larger payloads.

**Used by:**
- Tools menu **Inject Helper into Current CE Table** — UE5DumpUI ships the embedded `ue5_invoke_helper.lua` straight into the user's open .CT. Falls back to "use Export to disk + Add File..." if AOBMaker plugin isn't loaded.

### 5. `CreateSymbolScript`

Create an AOB-scan-based symbol registration AA script. The CE Plugin's `BuildSymbolScanScript()` generates the full AA script from these parameters.

```json
// Request
{
  "type": "CreateSymbolScript",
  "name": "GWorld → gworld_addr",
  "aob": "48 8B 1D ?? ?? ?? ??",
  "pos": 3,
  "aoblen": 7,
  "symbol": "gworld_addr",
  "module": "DQIandIIHD2DRemake-Win64-Shipping.exe",
  "autoActivate": true
}

// Response
{ "type": "CreateSymbolScript", "success": true }
```

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | Display name in CE address list |
| `aob` | string | AOB pattern (e.g. `"48 8B 1D ?? ?? ?? ??"`) |
| `pos` | int | Displacement offset within AOB match (instrOffset + opcodeLen) |
| `aoblen` | int | Instruction end relative to AOB match (instrOffset + totalLen) |
| `symbol` | string | CE symbol name to register (e.g. `"gworld_addr"`) |
| `module` | string | Module name for `AOBScanModule` |
| `autoActivate` | bool | Whether to activate immediately |

The generated script performs: `AOBScanModule` → read RIP-relative displacement at `pos` → calculate `match + pos + 4 + [displacement]` → register as CE symbol. Survives game restarts (re-scans on script enable).

**Used by:**
- PointerPanel: SYM button registers GWorld pointer as persistent CE symbol

### 6. `CreateMemoryRecord`

Add a single typed memory record straight into CE's address list (the plugin calls
`getAddressList().createMemoryRecord()`, sets Description / Address / Type / ShowAsSigned /
ShowAsHex, and self-verifies). One-click alternative to "copy the address, then build the
record by hand in CE" — e.g. so the user can immediately run CE's **Find out what accesses
this address** on a Live Walker field.

```json
// Request
{
  "type": "CreateMemoryRecord",
  "description": "Health",
  "address": "2DA53B24970",
  "valueType": 4,
  "isSigned": false,
  "showAsHex": false
}

// Response
{ "type": "CreateMemoryRecordResult", "success": true }
```

| Field | Type | Description |
|-------|------|-------------|
| `description` | string | Record label in CE's address list (typically the field Name) |
| `address` | string | Hex address without `0x`, or a registersymbol name |
| `valueType` | int | CE `TVariableType`: `0`=Byte, `1`=Word, `2`=Dword, `3`=Qword, `4`=Single, `5`=Double, `6`=String, `7`=UnicodeString, `8`=ByteArray, `9`=Binary |
| `isSigned` | bool | Display integer types as signed |
| `showAsHex` | bool | Display as hex. **Requires an AOBMaker CE plugin compiled on/after 2026-06-07** — older builds silently ignore it (default `false` is back-compatible). Ignored for string types (6/7). |

The wire model omits `valueType` from unrelated messages (it is a nullable `int?` so a Byte
record's `0` still serializes), and omits `isSigned` / `showAsHex` when `false`.

**Used by:**
- LiveWalker: per-row **+CE** buttons (field address + pointer target) — see UI Integration
  Points. Type/signed/hex are derived via `CeXmlExportService.MapFieldToCeRecordType`, reusing
  the exact UE→CE type mapping that drives Copy CE XML / Copy CE Field.
- LiveWalker: **+CE Fields** toolbar button — flat batch form of the per-row +CE (loops
  `CreateMemoryRecord` over the multi-selection, one top-level record per field, early-bails
  if the pipe drops mid-batch). It does NOT reproduce the hierarchical pointer-chain layout —
  Copy CE XML / Copy CE Field stay clipboard-only for that.

> **Minimum AOBMaker build:** the typed-record push works against any plugin that handles
> `CreateMemoryRecord`, but the `showAsHex` flag (pointer / 8-byte fields shown as hex)
> needs a plugin **compiled on/after 2026-06-07**. On older plugins the record is still
> created — it just displays in decimal.

---

## Detection & Lifecycle

### Startup Detection

```
App.axaml.cs
  └─ new AobMakerBridgeService(logging)
       └─ Injected into MainWindowViewModel
            ├─ PointerPanelViewModel(aobMaker: ...)
            └─ LiveWalkerViewModel(aobMaker: ...)
```

On first connection (after pipe connects and engine state loads), both ViewModels call `CheckAobMakerAsync()`:

```csharp
// AobMakerBridgeService.CheckAvailabilityAsync():
// 1. Attempt pipe connect to \\.\pipe\AOBMakerCEBridge (2s timeout)
// 2. If connects → IsAvailable = true, close pipe
// 3. If fails → IsAvailable = false
```

### Tab-Switch Re-detection

Every time the user switches tabs in the main window:

```csharp
// MainWindowViewModel.OnSelectedTabIndexChanged()
case 0: _ = LiveWalker.CheckAobMakerAsync();   // Live Walker tab
case 5: _ = Pointers.CheckAobMakerAsync();      // Pointers tab
```

This detects:
- **CE opened after UI started** → buttons enable
- **CE closed while UI running** → buttons disable

### Navigation Cooldown

LiveWalkerViewModel uses a **5-second cooldown** to avoid spamming pipe connects during rapid field navigation (each connect attempt takes up to 2s when CE is not running):

```csharp
private DateTime _lastAobMakerCheck = DateTime.MinValue;
private static readonly TimeSpan AobMakerCheckCooldown = TimeSpan.FromSeconds(5);

private void TryCheckAobMaker()
{
    if (_aobMaker == null) return;
    if (DateTime.UtcNow - _lastAobMakerCheck < AobMakerCheckCooldown) return;
    _ = CheckAobMakerAsync();  // fire-and-forget
}
```

Called on every drilldown (`ClickFieldAsync`), breadcrumb navigation, and back navigation.

### Connection Recovery

Each `NavigateHexViewAsync` / `NavigateDisassemblerAsync` / `CreateAAScriptAsync` / `CreateSymbolScriptAsync` call:

1. **Reconnects** fresh (CE Plugin disconnects after each request)
2. On success → `IsAvailable = true`
3. On pipe connect failure → `IsAvailable = false` (buttons disable)
4. On response failure → logs warning, returns false (but keeps `IsAvailable`)
5. On timeout → logs warning, returns false

---

## UI Integration Points

### PointerPanel Buttons

| Button | AOBMaker Call | Address Source | Condition |
|--------|--------------|----------------|-----------|
| GObjects **HEX** | `NavigateHexView` | `GObjectsAddress` | `IsAobMakerAvailable && addr != 0` |
| GNames **HEX** | `NavigateHexView` | `GNamesAddress` | `IsAobMakerAvailable && addr != 0` |
| GWorld **HEX** | `NavigateHexView` | `GWorldAddress` | `IsAobMakerAvailable && addr != 0` |
| GObjects **ASM** | `NavigateDisassembler` | `GObjectsScanAddr` | `IsAobMakerAvailable && addr != 0` |
| GNames **ASM** | `NavigateDisassembler` | `GNamesScanAddr` | `IsAobMakerAvailable && addr != 0` |
| GWorld **ASM** | `NavigateDisassembler` | `GWorldScanAddr` | `IsAobMakerAvailable && addr != 0` |
| GWorld **SYM** | `CreateSymbolScript` | `GWorldAob` + metadata | `IsAobMakerAvailable && addr != 0 && AOB != ""` |

- **HEX** = data address → CE hex dump
- **ASM** = code address (AOB scan hit) → CE disassembler
- **SYM** = create persistent AOB-scan CE symbol

### LiveWalker Buttons

| Button | AOBMaker Call | Address Source |
|--------|--------------|----------------|
| Field **HEX** | `NavigateHexView` | `field.FieldAddress` (base + offset) |
| Ptr **HEX** | `NavigateHexView` | `field.PtrAddress` (dereferenced pointer target) |
| Object **HEX** | `NavigateHexView` | `CurrentAddress` (current object base) |
| Field **+CE** | `CreateMemoryRecord` | `field.FieldAddress` — typed via `MapFieldToCeRecordType` |
| Ptr **+CE** | `CreateMemoryRecord` | `field.PtrAddress` — 8 Bytes / ShowAsHex (`PointerRecordType`) |

### Top-Toolbar Status Chip

`MainWindow.axaml` carries an always-visible AOBMaker status chip (colored dot +
Connected/Offline + **⟳** refresh) bound to `MainWindowViewModel.IsAobMakerAvailable`.
It mirrors the per-tab availability (LiveWalker / Pointers each probe on tab activation),
and the **⟳** button re-probes on demand (`RefreshAobMakerCommand`). The System-tab
indicator (PointerPanel) stays as the detailed in-tab status.

### Invoke Script Delivery

When generating UFunction invoke scripts via `GenerateInvokeScriptAsync`:

```
1. Generate AA script via InvokeScriptGenerator.Generate()
2. If AOBMaker available:
   └─ CreateAAScriptAsync(description, script, autoActivate: false)
   └─ On success → status: "Invoke script created in CE"
3. Fallback (AOBMaker unavailable or failed):
   └─ Copy script to clipboard
   └─ Status: "Invoke script copied to clipboard"
```

---

## Data Flow: AOB Metadata

The AOB scan metadata flows from DLL → pipe → UI → AOBMaker:

```
DLL (OffsetFinder.cpp)
  │  Scans for GObjects/GNames/GWorld using AOB patterns
  │  Records: winning pattern ID, scan hit address, AOB string, pos, aoblen
  ▼
PipeServer.cpp (get_pointers / scan_status)
  │  Serializes to JSON response:
  │  {
  │    "gobjects_scan_addr": "0x7FF7F3493983",
  │    "gworld_aob": "48 8B 1D ?? ?? ?? ??",
  │    "gworld_aob_pos": 3,
  │    "gworld_aob_len": 7,
  │    "module_name": "Game-Win64-Shipping.exe",
  │    ...
  │  }
  ▼
DumpService.cs (GetPointersAsync / TriggerScanAsync)
  │  Parses into EngineState model
  ▼
PointerPanelViewModel / LiveWalkerViewModel
  │  Binds to UI buttons, calls IAobMakerBridge methods
  ▼
AobMakerBridgeService
  │  Sends length-prefixed JSON to \\.\pipe\AOBMakerCEBridge
  ▼
AOBMaker CE Plugin
  └─ Executes CE navigation / creates scripts
```

---

## Key Source Files

| File | Role |
|------|------|
| `ui/UE5DumpUI/Core/IAobMakerBridge.cs` | Interface — 7 methods (incl. `CreateMemoryRecordAsync`, `InjectTableFileAsync`) |
| `ui/UE5DumpUI/Services/CeXmlExportService.cs` | `MapFieldToCeRecordType` / `PointerRecordType` — UE→CE record-type mapping shared with Copy CE XML/Field |
| `ui/UE5DumpUI/Services/AobMakerBridgeService.cs` | Implementation — pipe client, per-request reconnect |
| `ui/UE5DumpUI/Models/AobMakerMessage.cs` | Wire model + AOT-safe `JsonSerializerContext` |
| `ui/UE5DumpUI/Models/EngineState.cs` | AOB metadata (scan addr, pattern, pos, aoblen) |
| `ui/UE5DumpUI/ViewModels/PointerPanelViewModel.cs` | HEX/ASM/SYM buttons |
| `ui/UE5DumpUI/ViewModels/LiveWalkerViewModel.cs` | Field/Ptr/Object HEX + invoke script delivery |
| `ui/UE5DumpUI/ViewModels/MainWindowViewModel.cs` | Tab-switch re-detection wiring |
| `ui/UE5DumpUI/App.axaml.cs` | Service creation + DI |

---

## JSON Encoding Note

The CE Plugin's Lua-side JSON parser does not handle `\uXXXX` Unicode escape sequences. For example, a single quote `'` serialized as `\u0027` would break AA script parsing.

Solution: `AobMakerJsonContext.Relaxed` uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` to emit literal characters instead of escape sequences:

```csharp
public static AobMakerJsonContext Relaxed => _relaxed ??= new(new JsonSerializerOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
});
```

This is only used for **outgoing requests** (which contain script content). Incoming responses use the default context.
