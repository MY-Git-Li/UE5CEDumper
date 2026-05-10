# Lessons Learned

> Hard-won lessons from cross-game debugging. Moved from CLAUDE.md.

-----

## Cheat Engine Integration

- `executeCodeEx` internally uses `CreateRemoteThread` — games with thread injection protection will block it silently (returns nil, not an error code)
- CE `File -> Open` vs `File -> Save` update **different** dialog objects (`OpenDialog1` vs `SaveDialog1`)
- `getCheatEngineDir()` returns CE root (e.g., `C:\Program Files\CE 7.5\`) — DLL must be placed next to the CT, NOT in a subdirectory
- **CT Lua DLL path search**: Try `OpenDialog1.FileName` first, fall back to `SaveDialog1.FileName`, then `getCheatEngineDir()`
- **CE XML hierarchical tree addressing model**: Each child's Address is relative to its parent's **RESOLVED** address. Pointer fields use `Address=+{offset}, Offsets=[0]` (CE dereferences). Inline fields use `Address=+{offset}` with no Offsets. Leaf fields ALWAYS use just `Address=+{offset}` — parent's `Offsets=[0]` already handled the deref. Never do `Address=+0, Offsets=[fieldOffset]` (double-deref bug).
- **CE XML TArray addressing**: Array group: `Address=+{fieldOffset}, Offsets=[0]` dereferences `TArray.Data`. Elements: `Address=+{N*elemSize}` — simple offset from already-dereferenced data pointer, NO Offsets needed.
- **CE XML type mapping**: `IntProperty/Int8/16/64` → `ShowAsSigned=1`; `BoolProperty` with bit mask → Binary type (`BitStart + BitLength=1`); pointer `ObjectProperty` → `ShowAsHex=1`

-----

## Memory & Validation

- **Always add memory hex dumps in validators** — invaluable for diagnosis when running blind inside a game process
- **`strstr` in validators is dangerous**: It ignores null terminators within the buffer, allowing matches across garbage bytes. Always use `strcmp` with exact length checks in security-critical validation paths.
- **`LooksLikeHeapPtr` is insufficient for rejecting 32-bit flag values on x64**: Values like `0x40000000` (EObjectFlags::Const, ~1GB) pass all three checks. On x64 Windows with ASLR, real heap pointers are typically > 4GB (`0x100000000`). When strong pointer validation is needed, add a magnitude check (< 4GB → suspicious) plus a `ReadSafe` dereference check.
- **ValidateGObjects must validate the chunk chain, not just NumElements**: The upper 32 bits of a code-section pointer (e.g., `0x00007FF7` = 32759) can coincidentally fall in the valid NumElements range. Must also verify: (1) Objects pointer can be deref'd, (2) chunk[0] is a heap pointer (not in game module).
- **V12/RE2 pattern resolution**: RE-UE4SS signatures resolve to the ObjObjects field, then subtract 0x10 to get struct base. This is `target - 0x10` (no deref). Doing `*(target) - 0x10` is wrong (deref-then-subtract).
- **Build scripts should always force clean builds**: CMake incremental build can miss source changes. Delete `build/` before cmake configure, `bin/obj/` before dotnet build.

-----

## FName / FNamePool

- **AOB patterns for GNames must be context-specific for newer UE builds (UE5.5+)**; use pointer-scan fallback as primary for UE5.5+
- **GNames address `[addr]=0` is normal**: FNamePool is an inline struct in `.data`, not a pointer-to-pointer. `[GNames]` reads FRWLock (first 8 bytes), which is 0 when unlocked. Actual name data is at `GNames+0x10` (Blocks[]).
- **UE4 TNameEntryArray vs UE5 FNamePool**: Completely different data structures. UE4: double-deref (array→chunk→entry), 16384 entries per chunk, string at fixed offset. UE5: packed inline entries with 2-byte header, single deref, header encodes length.
- **Hash-prefixed FNameEntry (UE4.26 SE fork)**: Some builds prepend a 4-byte ComparisonId before the 2-byte header: `[4B hash][2B header][string]`. Validators must try header at both offset 0 and offset 4. Diagnostic clue: "None" appears at offset +6 from chunk pointer.
- **Hash-prefixed FNameEntry stride = 4, not 2**: `alignof(FNameEntry)` becomes 4 (due to uint32_t member). The index-to-byte offset conversion must use `offset * 4`. Diagnostic clue: names like 'operty' (tail of 'Property').
- **FName index 1 can be invalid**: FName indices are byte_offset / stride, not sequential. Index 1 (byte offset 2 with stride=2) falls INSIDE entry[0]'s "None" string. Don't use FName[1] as a diagnostic indicator.
- **FNamePool should be initialized before ObjectArray**: This enables using FName resolution as the primary validation signal for FUObjectItem stride detection — nearly impossible to get false positives.
- **`ValidateGNamesAny()` wrapper**: Tries FNamePool→Structural→UE4 validators in sequence. Single call site makes code DRYer and ensures new validators are automatically tried.

-----

## ObjectArray / Stride Detection

- **FUObjectItem item[0] can be null**: GObjects arrays often have null in slot 0 (especially UE4). `DetectItemSize` must scan forward — don't require item[0] to be valid.
- **Anchor-scan is fragile**: Scanning chunk bytes in 8-byte steps to find a "first valid pointer" produces false positives. Correct approach: walk at stride-aligned positions only (`offset = idx * stride`, starting from 0) and validate with FNamePool string resolution.
- **LCM alignment creates false positive strides**: For true stride S, probing with stride S' finds valid items at positions that are multiples of `LCM(S, S')`. For S=24 and S'=16, LCM=48 → every 3rd 16-byte position coincidentally aligns. Always add a tiebreaker (fewer bad items = correct stride).
- **Early exit bias**: `if (outNamed >= 5) break` makes all strides stop at the same named count when LCM alignment provides enough matches. Remove early exit; scan all items equally to reveal the true signal via bad counts.
- **Bad items are the strongest stride signal**: Correct stride reads valid data (objects or null) at every position. Wrong strides read misaligned garbage at most positions. Scoring: `named * 10 - bad * 3`.
- **Some UE4 builds use flat (non-chunked) GObjects arrays** (UE4.11–4.20): Instead of `FUObjectArray → chunk** → chunk[] → FUObjectItem`, the array is `Objects* → FUObjectItem[]` directly. Detection: check if `*(Objects + 8)` is a valid heap pointer when `numElements > OBJECTS_PER_CHUNK`. If not (e.g., `0x40000000` = EObjectFlags), the array is flat.
- **Flat array detection must happen BEFORE chunked probing**: Chunked Phase 1 can accidentally find valid items via LCM alignment when reading UObject internal memory, masking the flat layout.
- **EObjectFlags 0x40000000 = Const**: In UE4's FUObjectItem, the Flags field at item+0x08 can contain `0x40000000` (RF_Const). Reading flat item[0].Flags as "chunk[1]" gives a flag value that correctly fails strong pointer checks.
- **OctoPath Traveler uses stride 16 (not 24)**: Despite being UE4, FUObjectItem is 16 bytes. The stride depends on whether the engine was compiled with GC clustering support.
- **DetectLayout must validate Objects pointer**: Always validate that `*(addr + objectsOffset)` is a heap-like pointer — not null, user-mode range, NOT in the game module's code section.
- **UE4 FUObjectArray has extra members before ObjObjects**: Fields like `ObjFirstGCIndex`, `ObjLastNonGCIndex` can appear at offsets 0x00–0x0C, pushing `ObjObjects.Objects**` to +0x10.
- **Pagination: advance by scanned, not returned**: When the DLL skips unnamed/null objects, the UI must advance by the number of indices **scanned** (`scanned` field), not by objects returned. Otherwise pagination stalls or loops infinitely.

-----

## UE Offsets & Version Detection

- **CasePreservingName (UE5.5+/5.7)**: FName grows from 0x8 to 0x10 bytes (adds DisplayIndex), shifting FField::Flags and all FProperty offsets by +0x8. Never hardcode — always detect dynamically.
- **Dumper-7 reference patterns**: `FindFFieldNameOffset()` brute-force probing, `FixupHardcodedOffsets()`, `InitFNameSettings()` — gold standard for dynamic offset discovery.
- **"rty" diagnostic pattern**: When field names show truncated type names (e.g., "rty" from "Property"), Name offset is wrong and reading into adjacent FFieldClass data. Type names correct but field names wrong = ClassPrivate offset OK but Name offset shifted.
- **Version detection needs context**: Bare version patterns like "5.6." appear in game data. Three-tier approach: (1) exact `++UE5+Release-` prefix, (2) `Release` in preceding 16 bytes, (3) bare pattern with guard against preceding digit/period.
- **FProperty-to-UProperty fallback**: When FProperty scan fails in `ValidateAndFixOffsets`, retry with UProperty scan. This auto-corrects the mode even with wrong version detection.
- **RE-UE4SS CustomGameConfigs are ground truth for UE4 offset validation**: Use `MemberVariableLayout.ini` as reference only — don't consolidate per-game configs into UE5CEDumper. Our DynOff auto-detection already discovers correct offsets dynamically.
- **UE4.18 uses FNamePool, not TNameEntryArray**: Don't assume UE4 = TNameEntryArray. FF7R (Square Enix UE4.18 fork) uses standard FNamePool.

-----

## GWorld

- **GWorld null is common in UE4**: Many UE4 games have `*GWorld == 0`. The DLL's `walk_world` has a GObjects fallback that searches for a UWorld instance.
- **GObjects fallback for GWorld should skip CDOs**: Filter by checking if object name starts with `"Default__"`. CDOs have all instance pointers null, producing misleading empty results.
- **GWorld `[addr]=0` doesn't mean broken**: GWorld IS a pointer-to-pointer, but `*GWorld` can be 0 during loading screens or when AOB found wrong address.
- **DLL partial-success responses need UI error handling**: When the DLL returns `ok=true` with an `"error"` field, the UI must read and display it. Always propagate error fields through the model to the ViewModel.

-----

## Logging

- **Log structure matters for AI analysis**: Splitting by phase (scan vs pipe) + adding category tags + SUMMARY level reduces analysis from reading 2MB to `grep "[SUMMARY]"` → 3 lines.
- **Per-process mirror logging aids cross-session comparison**: Writing logs to both `Logs/` and `Logs/<ProcessName>/` allows comparing current vs previous runs.
- **Build system**: VS 2026 (v18) at `C:\Program Files\Microsoft Visual Studio\18\Community`. Use a temp `build.bat` calling `vcvarsall.bat amd64` then cmake — bash→cmd quoting for paths with spaces is fragile. Delete after build.

-----

## Game-Specific Notes

- **FF7Re** = Final Fantasy VII Rebirth (UE4.26 Square Enix fork)
- **FF7R** = Final Fantasy VII Remake Intergrade (UE4.18 Square Enix fork)
- **FF7R (Remake Intergrade) is UE4.18**: Uses FProperty mode but `FUObjectArray` has ObjObjects at +0x10 (needs -0x10 adjustment from GUObjectArray pattern)
- **DQ I&II HD-2D Remake is UE5.05**: Standard FNamePool, stride 24, works with no special handling. GWorld=0 is common.
- **Phase B scalar-only design is correct**: ArrayProperty Phase B reads inline element values only for scalar types. ObjectProperty and StructProperty arrays intentionally show only type info — those are Phase D/E scope.
- **MSVC C2360**: Variable declarations inside `case` require wrapping `{}` braces.

-----

## UFunction Invoke / ProcessEvent

- **`FProperty::PropertyFlags` is at `ElementSize + 4`, NOT `+8`** (build 642 fix in `dll/src/Genau.cpp`). UE source layout puts `ArrayDim` BEFORE `ElementSize`, not between `ElementSize` and `Flags`. The wrong `+8` formula reads the high 32 bits of the 64-bit Flags + adjacent fields, which silently nukes the parm-classification bits (`CPF_Parm=0x80`, `CPF_OutParm=0x100`, `CPF_ReturnParm=0x400`) that all live in the low 32. Symptoms: every UFunction parameter classified as `IsReturn=false` / `IsOut=false`. The bug was silent for years because almost no UI flow cared about the classification — it surfaced only when build 637's Verify Return Value mode tried to find the return slot.
- **KismetMathLibrary helpers are stubs in cooked Shipping (UE 5.5+)**: `KismetMathLibrary::Exp`, `Multiply_DoubleDouble`, `Add_IntInt`, etc. all have intact reflection metadata (correct flags, parmsSize, NumParms, parm offsets) but their `execXxx` thunks return without writing to the ReturnValueOffset. Likely a side effect of UE's BlueprintFastCall optimisation, where the BP VM bytecode bypasses ProcessEvent entirely and calls the C++ impl directly. Reflection-driven invokers (us, custom dumpers) end up calling the stub. **Don't use KismetMathLibrary as a verification target** — pick a game-specific instance method instead. Verified failing on Everspace 2 (UE 5.5).
- **`FUNC_Native | FUNC_Static` UFunctions are safe to call off-thread**: build 636 added a fast path in `Mimic::HandleInvoke` that bypasses `Stark::EnqueueInvoke` when both flags are set. These are pure helpers (KismetMath / KismetString / KismetArray / KismetSystem libraries — when they're not BP-FastCall-stubbed) with no `this` state, so `ProcessEvent` from the pipe thread is safe and side-effect free. Without the fast path an idle game (main menu, no PE traffic) leaves the queue undrained until the configured deadline expires (`-5` timeout). Stateful methods (BP events, RPCs) still route through `EnqueueInvoke` unchanged.
- **CE Lua `getAddress` vs `getAddressSafe`**: `getAddress(name)` throws / returns garbage when the symbol isn't in CE's module symbol table; `getAddressSafe(name)` returns nil/0. For DLL exports like `g_invokeMailbox`, also try the module-prefixed form (`'UE5Dumper.g_invokeMailbox'`) — some user setups only register the prefixed variant. The mailbox-resolution helper inside `ue5_invoke_helper.lua`'s `findMailbox` does both; any code reaching outside the helper (e.g. inline diagnostic dumps in generated AA Scripts) must mirror that pattern.
- **CE `tableFile.Stream.write(string)` silently drops content on some payloads**: Discovered when building the Tools -> Inject Helper feature (build 611 -> fix 3fc4d8c in AOBMaker repo). `f.Stream.write(content)` returns no error but `f.Stream.Size` keeps reading 0 immediately after, so any size-verify check fails. The reliable population pattern (used by CE's own `autorun/java.lua`) is `createStringStream(content)` + `f.Stream.copyFrom(ss, 0)` + `ss.destroy()`. Apply this for any TableFile-from-Lua-string scenario.
- **AA Script generation should use `ElementSize+4`-derived flag offsets across the board**: Anything that walks UFunction parameters and reads PropertyFlags must use the corrected offset. Sweep search: `propFlags`, `PropertyFlags`, `CPF_` references in DLL code (Ubel.cpp + Aura.cpp + Mimic.cpp).
