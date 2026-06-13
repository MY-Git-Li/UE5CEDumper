#pragma once

// ============================================================
// Frieren — 芙莉蓮 (主角 — Protagonist)
// ExportAPI: ~30 C ABI exports for CE Lua bridge
// ============================================================

#include <cstdint>
#include <Windows.h>

extern "C" {

// === Initialization ===
__declspec(dllexport) bool     UE5_Init();
__declspec(dllexport) void     UE5_Shutdown();
__declspec(dllexport) uint32_t UE5_GetVersion();

// Combined init + pipe server start — called by CEPlugin's InjectDLL
// so that a single entry point activates everything in the game process.
__declspec(dllexport) bool     UE5_AutoStart();

// === Global Pointers ===
__declspec(dllexport) uintptr_t UE5_GetGObjectsAddr();
__declspec(dllexport) uintptr_t UE5_GetGNamesAddr();

// === Object Queries ===
__declspec(dllexport) int32_t   UE5_GetObjectCount();
__declspec(dllexport) uintptr_t UE5_GetObjectByIndex(int32_t index);
__declspec(dllexport) bool      UE5_GetObjectName(uintptr_t obj, char* buf, int32_t bufLen);
__declspec(dllexport) bool      UE5_GetObjectFullName(uintptr_t obj, char* buf, int32_t bufLen);
__declspec(dllexport) uintptr_t UE5_GetObjectClass(uintptr_t obj);
__declspec(dllexport) uintptr_t UE5_GetObjectOuter(uintptr_t obj);

// === Search ===
__declspec(dllexport) uintptr_t UE5_FindObject(const char* fullPath);
__declspec(dllexport) uintptr_t UE5_FindClass(const char* className);

// === WalkClass (batch mode) ===
__declspec(dllexport) int32_t   UE5_WalkClassBegin(uintptr_t uclassAddr);
__declspec(dllexport) bool      UE5_WalkClassGetField(int32_t index,
                                    uintptr_t* outAddr,
                                    char* nameOut, int32_t nameBufLen,
                                    char* typeOut, int32_t typeBufLen,
                                    int32_t* offsetOut,
                                    int32_t* sizeOut);
__declspec(dllexport) void      UE5_WalkClassEnd();

// === FName Resolution ===
__declspec(dllexport) bool      UE5_ResolveFName(uint64_t fname, char* buf, int32_t bufLen);

// === Object Decryption (GAP #1) ===
// Set a custom decryption function for encrypted GObjects pointers.
// Pass NULL to clear (revert to identity/no decryption).
// Must be called BEFORE UE5_Init() — decryption is needed during scanning.
// UE5_AutoStart() does NOT support decryption (use manual Lua flow).
__declspec(dllexport) void      UE5_SetObjectDecryption(uintptr_t (*decryptFunc)(uintptr_t));

// === Property Detail Queries (for CE Lua dissect) ===
// Returns the FieldMask byte for a BoolProperty field (0 if not a bool).
// fieldAddr: FProperty* address from UE5_WalkClassGetField.
__declspec(dllexport) int32_t   UE5_GetFieldBoolMask(uintptr_t fieldAddr);

// Returns the UScriptStruct* for a StructProperty (0 if not a struct).
// fieldAddr: FProperty* address from UE5_WalkClassGetField.
__declspec(dllexport) uintptr_t UE5_GetFieldStructClass(uintptr_t fieldAddr);

// Returns the PropertyClass (UClass*) for an ObjectProperty (0 if not an object prop).
// fieldAddr: FProperty* address from UE5_WalkClassGetField.
// Same offset as StructProperty::Struct — separate export for semantic clarity.
__declspec(dllexport) uintptr_t UE5_GetFieldPropertyClass(uintptr_t fieldAddr);

// Returns the PropertiesSize of a UClass/UStruct (total struct byte size).
__declspec(dllexport) int32_t   UE5_GetClassPropsSize(uintptr_t classAddr);

// === UFunction Invocation ===
// Find first non-CDO instance of a class by name. Returns UObject* address or 0.
__declspec(dllexport) uintptr_t UE5_FindInstanceOfClass(const char* className);

// Find a UFunction by name on a UClass. Returns UFunction* address or 0.
__declspec(dllexport) uintptr_t UE5_FindFunctionByName(uintptr_t classAddr, const char* funcName);

// Call UObject::ProcessEvent(ufunc, params). Returns 0 on success, negative on error.
// params must point to a buffer of at least UFunction::ParmsSize bytes.
// Error codes: -1=bad args, -2=vtable read fail, -3=ProcessEvent not found, -4=exception.
__declspec(dllexport) int32_t   UE5_CallProcessEvent(uintptr_t instance, uintptr_t ufunc, uintptr_t params);

// Direct ProcessEvent call from the calling thread, bypassing
// GameThreadDispatch. Safe for pure native helpers (FUNC_Native|FUNC_Static
// — KismetMathLibrary, KismetStringLibrary, BFLs without game-state side
// effects). NOT safe for instance methods that read/write actor state from
// off-thread; use UE5_CallProcessEvent for those.
// Error codes match UE5_CallProcessEvent: -1=bad args, -2=vtable, -3=PE
// vtable offset unresolved, -4=SEH exception.
__declspec(dllexport) int32_t   UE5_CallProcessEventDirect(uintptr_t instance, uintptr_t ufunc, uintptr_t params);

// === Debug Camera (robust force on/off; shared by UI pipe + CE Lua) ===
// Read the live Debug Camera state. 1 = ON (a DebugCameraController is
// possessing the player), 0 = OFF, -1 = unknown / no live CheatManager.
// Two-hop reflection read of DebugCameraController.OriginalControllerRef.
__declspec(dllexport) int32_t   UE5_GetDebugCameraState();

// Force Debug Camera ON (enable!=0) or OFF (enable==0). Idempotent — no-op if
// already in the desired state. Fires ToggleDebugCamera only when needed; if a
// disable can't take (Shipping builds that strip DisableDebugCamera), switches
// the local player's controller back to the original PlayerController by hand.
// Returns the resulting state (1/0) or -1 on error. All offsets resolved live
// from reflection (UE4/UE5 version-agnostic).
__declspec(dllexport) int32_t   UE5_SetDebugCamera(int32_t enable);

// === Teleport (Wirbel: marker save/recall + cursor teleport) ===
// All return Wirbel result codes (0 = OK, negatives per docs/teleport-spec.md
// §8). Pose arrays are X,Y,Z,Pitch,Yaw,Roll as doubles regardless of the
// engine's FVector width (UE4 floats are widened at the boundary).
// NOTE for CE: executeCodeEx cannot retrieve these return values — CE Lua
// integration goes through the Mimic mailbox (CMD_TELEPORT=8) instead.
__declspec(dllexport) int32_t   UE5_TeleportGetPose(double* outPose6,
                                    char* outMapName, int32_t mapNameCap);
__declspec(dllexport) int32_t   UE5_TeleportSaveMarker(int32_t slot);
__declspec(dllexport) int32_t   UE5_TeleportRecallMarker(int32_t slot, int32_t force);
__declspec(dllexport) int32_t   UE5_TeleportToCursor(double zOffset,
                                    int32_t traceChannel, int32_t fallbackToCenter);
__declspec(dllexport) int32_t   UE5_TeleportGetMarker(int32_t slot, double* outPose6,
                                    char* outMapName, int32_t mapNameCap);
__declspec(dllexport) int32_t   UE5_TeleportClearMarker(int32_t slot);
// Recall the system "last" pose (auto-saved before every recall/force/BugItGo/
// cursor jump) — one-way restore so a bad teleport can be undone.
__declspec(dllexport) int32_t   UE5_TeleportRecallLast();
__declspec(dllexport) int32_t   UE5_TeleportGetLast(double* outPose6,
                                    char* outMapName, int32_t mapNameCap);

// === Mailbox (CE Lua shared memory interface) ===
// Returns the address of the g_invokeMailbox buffer.
// CE Lua can also use getAddress("g_invokeMailbox") directly.
__declspec(dllexport) uintptr_t UE5_GetMailboxAddr();

// === Pipe Server ===
__declspec(dllexport) bool      UE5_StartPipeServer();
__declspec(dllexport) void      UE5_StopPipeServer();
__declspec(dllexport) bool      UE5_IsPipeConnected();

} // extern "C"
