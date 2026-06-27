// ============================================================
// Mimic — 寶箱怪 (經典梗 — The Classic Gag)
// Mailbox: CE Lua shared-memory command interface
//
// CE Lua uses readQword/writeQword (ReadProcessMemory/WriteProcessMemory)
// to communicate with the DLL without needing CreateRemoteThread.
//
// CE Lua workflow:
//   1. mb = getAddress("g_invokeMailbox")
//   2. Write command fields (class_name, func_name, params, etc.)
//   3. Write cmd field (trigger) — MUST be written LAST
//   4. Poll status field until == 1 (done)
//   5. Read result fields
// ============================================================

#pragma once

#include <cstdint>

namespace Mimic {

// Mailbox commands (CE writes to cmd field)
enum Cmd : int32_t {
    CMD_IDLE            = 0,
    CMD_INVOKE          = 1,  // Call ProcessEvent (instance_addr, ufunc_addr, params_data)
    CMD_FIND_INSTANCE   = 2,  // Find non-CDO instance by class name
    CMD_FIND_FUNCTION   = 3,  // Find UFunction by name on instance's class
    CMD_INVOKE_BY_NAME  = 4,  // Combined: find instance + find function + invoke
    CMD_LIST_FUNCTIONS  = 5,  // List all UFunctions on an instance's class (paginated)
    CMD_LIST_INSTANCES  = 6,  // List all live (non-CDO) instances of a class (paginated)
    CMD_SET_DEBUG_CAMERA = 7, // Robust Debug Camera force on/off / query.
                              //   Input:  instanceAddr = 0 (OFF) / 1 (ON) / 2 (query, no change)
                              //   Output: result = resulting state (1=ON, 0=OFF, -1=error)
    CMD_TELEPORT        = 8,  // Teleport (Wirbel): marker save/recall + cursor teleport.
                              //   Input:  instanceAddr = op (TeleportOp below)
                              //           ufuncAddr    = slot (0..2) for SAVE/RECALL/GET/CLEAR
                              //           paramsData (op CURSOR only, read BEFORE outputs):
                              //             [0..7] double zOffset, [8] u8 traceChannel,
                              //             [9] u8 fallbackToCenter
                              //   Output: result = Wirbel code (0 OK, negatives per
                              //           docs/teleport-spec.md §8)
                              //           paramsData pose block (GET_POSE/SAVE/GET_MARKER):
                              //             [0..47]   6 doubles X,Y,Z,Pitch,Yaw,Roll
                              //             [48..175] mapName (null-terminated)
                              //             [176]     u8 source (0 raw / 1 invoke)
                              //             [177]     u8 tier (1 invoke / 2 raw write)
                              //           op CURSOR output:
                              //             [0..23] 3 doubles hit point, [177] tier,
                              //             [178] u8 usedCenter
    CMD_PROTECT         = 9,  // GodMode (Solitar): force AActor::bCanBeDamaged off.
                              //   Input:  instanceAddr = op (ProtectOp below)
                              //           ufuncAddr    = value (0/1) for SET ops
                              //   Output: result = observed state (1 immune /
                              //           0 can-be-damaged) or negative
                              //           Solitar::ProtectResult (docs/godmode-spec.md §6.3)
                              //           paramsData (op GET_STATE):
                              //             [0] u8 want (desired toggle 1/0)
                              //             [1] u8 live (observed 1/0, 0xFF if no pawn)
                              //             [2] u8 resolvable (1/0)
    CMD_MOVEMENT        = 10, // Movement tuning (Laufen): set a CharacterMovement
                              //   float knob to a percent (100% = OFF), or the
                              //   gravity DIRECTION vector (knobId 3).
                              //   Input:  instanceAddr = knobId (0 MaxWalkSpeed /
                              //             1 GravityScale / 2 JumpZVelocity /
                              //             3 GravityDirection, UE5.4+)
                              //           knob 0-2: paramsData[0..7] = double percent
                              //             (user slider %; 100 = off. knob 2 = jump
                              //             HEIGHT %, DLL applies sqrt).
                              //           knob 3:   paramsData[0..23] = 3 doubles
                              //             x/y/z (DLL normalizes; (0,0,0) = off).
                              //   Output: result = 1 (active) / 0 (off) / negative
                              //             Laufen::MoveResult.
};

// CMD_TELEPORT op codes (written into instanceAddr by CE Lua / pipe bridge)
enum TeleportOp : uint64_t {
    TP_OP_GET_POSE     = 0,
    TP_OP_SAVE         = 1,
    TP_OP_RECALL       = 2,
    TP_OP_RECALL_FORCE = 3,
    TP_OP_CURSOR       = 4,
    TP_OP_GET_MARKER   = 5,
    TP_OP_CLEAR_MARKER = 6,
    TP_OP_RECALL_LAST  = 7,  // recall the system "last" pose (auto-saved before
                             //   every recall/force/BugItGo/cursor jump). slot ignored.
    TP_OP_GET_LAST     = 8,  // read the system "last" slot (pose block output)
    TP_OP_BUGIT_SAVE   = 9,  // store current pose into the BugIt slot (pose block
                             //   output, like SAVE); user-triggered, slot ignored.
    TP_OP_BUGIT_GO     = 10, // teleport to the stored BugIt pose (no-op when empty)
    TP_OP_GET_POV      = 11, // read the camera POV (read-only). slot ignored.
                             //   Output POV block in paramsData:
                             //     [0..47]  6 doubles cam X,Y,Z,Pitch,Yaw,Roll
                             //     [48..55] double FOV
                             //     [56..79] 3 doubles pawn X,Y,Z (delta display)
                             //     [80]     u8 hasPawn   [81] u8 source
    TP_OP_RELATIVE     = 12, // teleport along the pawn's facing by a distance.
                             //   Input  paramsData: [0..7] double distance (uu;
                             //     negative = backward), [8] u8 mode (0 = horizontal
                             //     keep-Z, 1 = full 3D include pitch). slot ignored.
                             //   Output pose block (resulting pose, like GET_POSE) +
                             //     [177] tier.
    TP_OP_EXPLICIT     = 13, // teleport to explicit world coordinates (force; no
                             //   map check). Input paramsData: [0..47] 6 doubles
                             //   X,Y,Z,Pitch,Yaw,Roll, [48] u8 hasRot (restore
                             //   rotation). slot ignored. Output: [177] tier.
    TP_OP_SET_CURSOR   = 14, // force the mouse cursor on/off (writes
                             //   APlayerController.bShowMouseCursor). Input:
                             //   ufuncAddr (slot field) = 1 (show) / 0 (hide).
                             //   Output: result = Wirbel code, paramsData[0] =
                             //   resulting state (1/0).
    TP_OP_GET_CURSOR   = 15, // read the current bShowMouseCursor state. slot
                             //   ignored. Output: result = code, paramsData[0] =
                             //   state (1/0).
};

// CMD_PROTECT op codes (written into instanceAddr by CE Lua / pipe bridge).
enum ProtectOp : uint64_t {
    PROTECT_OP_SET_GODMODE = 0, // ufuncAddr = 1 (ON) / 0 (OFF). Output result =
                                //   observed state.
    PROTECT_OP_GET_GODMODE = 1, // Output result = observed state.
    PROTECT_OP_GET_STATE   = 2, // Output paramsData[0..2] = want / live / resolvable.
    // Reserved (v2 — docs/godmode-spec.md §5.4): PROTECT_OP_SET_ACTOR_BOOL = 3,
    // generic "force any reflected bool" using instanceAddr=obj + className=prop.
};

// Mailbox status (DLL writes to status field)
enum Status : int32_t {
    STATUS_IDLE         = 0,
    STATUS_DONE         = 1,
    STATUS_PROCESSING   = 0xFF,
};

// Mailbox structure (exported as global variable)
// CE Lua accesses via getAddress("g_invokeMailbox") + offset reads/writes
//
// Total size: ~1848 bytes (fits in single page)
#pragma pack(push, 1)
struct MailboxData {
    volatile int32_t  cmd;              // 0x000: Cmd enum (CE writes LAST as trigger)
    volatile int32_t  status;           // 0x004: Status enum (DLL writes)
    volatile int32_t  result;           // 0x008: Return code (0=ok, negative=error)
    int32_t           reserved;         // 0x00C: Alignment padding

    volatile uint64_t instanceAddr;     // 0x010: UObject* instance
    volatile uint64_t ufuncAddr;        // 0x018: UFunction* address

    uint16_t          parmsSize;        // 0x020: UFunction::ParmsSize (DLL fills)
    uint16_t          numParms;         // 0x022: UFunction::NumParms (DLL fills)
    uint32_t          functionFlags;    // 0x024: EFunctionFlags (DLL fills)

    char              className[256];   // 0x028: Input: class name (null-terminated)
    char              funcName[256];    // 0x128: Input: function name (null-terminated)
    char              errorMsg[256];    // 0x228: Output: error description

    uint8_t           paramsData[1024]; // 0x328: In/Out: inline parameter buffer
                                        //        Covers 99%+ of UFunctions
                                        //
                                        // CMD_LIST_FUNCTIONS uses this buffer for paginated results:
                                        //   Input:  paramsData[0..3] = page index (uint32, 0-based)
                                        //   Output: parmsSize = total function count
                                        //           numParms  = returned count this page
                                        //           functionFlags = total pages
                                        //   Each entry is 64 bytes (max 15 per page):
                                        //     [0..7]   addr (uint64)
                                        //     [8..9]   parmsSize (uint16)
                                        //     [10..11] numParms (uint16)
                                        //     [12..15] flags (uint32)
                                        //     [16..63] name (48 chars, null-terminated)
                                        //
                                        // CMD_LIST_INSTANCES uses this buffer for paginated results:
                                        //   Input:  paramsData[0..3] = page index (uint32, 0-based)
                                        //           className field = exact class name (case-insensitive)
                                        //   Output: parmsSize = total live instance count (capped at 0xFFFF)
                                        //           numParms  = returned count this page
                                        //           functionFlags = total pages
                                        //   Each entry is 8 bytes (max 128 per page):
                                        //     [0..7]   UObject* (uint64)
};
#pragma pack(pop)

static_assert(sizeof(MailboxData) <= 4096, "MailboxData must fit in a single page");

/// Start the mailbox polling thread.
/// Called from dllmain.cpp DLL_PROCESS_ATTACH.
void StartThread();

/// Stop the mailbox polling thread and clean up.
/// Called from UE5_Shutdown().
void StopThread();

/// Returns the address of the mailbox buffer.
uintptr_t GetAddress();

} // namespace Mimic

// Exported global — CE Lua uses getAddress("g_invokeMailbox") to find it.
// No function call needed! CE resolves the symbol from the DLL export table.
extern "C" __declspec(dllexport) extern Mimic::MailboxData g_invokeMailbox;
