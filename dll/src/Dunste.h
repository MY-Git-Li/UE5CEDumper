#pragma once

// ============================================================
// Dunste — ドゥンスト / Dünste (「蒸氣」 — vapors; Edel's teammate mage)
// Fly: no-gravity free 3D flight of the local pawn, driven by the keyboard.
// Vapors rise and drift weightlessly through the air, unbound by gravity —
// the module lifts the pawn off the ground and steers it in 3 dimensions.
//
// Mechanism (engine-flying, collision preserved): put the pawn's
// UCharacterMovementComponent into MOVE_Flying (raw enum-byte write, held
// against per-tick game overwrites by a re-assert worker — same write-on-drift
// pattern as Laufen/Solitar) so the engine suppresses gravity yet still sweeps
// world geometry. A fast (~60 Hz) worker then samples the keyboard directly
// (GetAsyncKeyState — the DLL lives inside the game process, so no per-frame
// IPC).
//
// Control is CHARACTER-RELATIVE: Dunste owns the flight HEADING (yaw), seeded
// from the pawn's live facing on engage. Q/E rotate the heading; WASD move
// relative to it (forward/strafe on the ground plane), Z/C move world-vertical.
// Each tick it writes the CMC Velocity FVector for motion and raw-writes the
// pawn RootComponent's RelativeRotation yaw = heading so the CHARACTER turns
// (the camera is left to the user's mouse/gamepad). All writes are pure
// reflected Macht (SEH) memory writes — NO UFunction invoke, NO game thread
// (Path B, like Laufen). All offsets resolved by FName (DynOff) →
// UE4/UE5-agnostic. No cached instance pointers: the pawn/CMC is re-resolved
// every worker tick (stale-pointer crash class).
//
// Input is captured only while the GAME window is foreground; the mouse is
// never touched (the game's own look input keeps working). Single-player only
// — same anti-cheat/desync caveat as Laufen/Wirbel.
// ============================================================

#include <cstdint>

namespace Dunste {

// Result codes (mirrors Laufen::MoveResult conventions). Non-negative returns
// from Set are the observed state (1 = flying active, 0 = inactive); negatives
// are errors.
enum FlyResult : int32_t {
    FR_OK           = 0,
    FR_ERR_NOT_INIT = -1,   // DLL not initialized / GWorld unavailable
    FR_ERR_NO_PAWN  = -3,   // local pawn null (menu / cutscene / spectator)
    FR_ERR_REFLECT  = -4,   // MovementMode / Velocity not resolved on the CMC
    FR_ERR_NO_CMC   = -5,   // pawn has no CharacterMovement (vehicle / custom framework)
    FR_ERR_WRITE    = -10,  // raw write failed
};

// Keyboard preset — which physical keys drive the 6 logical actions
// (forward/back, strafe L/R, yaw L/R, up/down). Wire value shared with the
// pipe/mailbox.
enum Preset : int32_t {
    PRESET_WASD   = 0,   // WASD + Q/E (yaw) + Z/C (up/down)
    PRESET_NUMPAD = 1,   // Num 8/2/4/6 + 7/9 (yaw) + 1/3 (up/down)
    PRESET_ARROWS = 2,   // Arrows + Ins/Del (yaw) + PgUp/PgDn (up/down)
    PRESET_COUNT
};

// Live snapshot for the UI badge / pipe readout.
struct FlyStatus {
    int32_t   code           = 0;      // FlyResult: FR_OK or a negative error
    bool      active         = false;  // fly override engaged (worker holding it)
    bool      hasCmc         = false;  // a CharacterMovement resolved on the pawn
    int32_t   preset         = PRESET_WASD;
    double    speed          = 0.0;    // uu/s
    bool      modeResolved   = false;  // MovementMode field found this instant
    int32_t   currentMode    = -1;     // live MovementMode enum byte (-1 = unknown)
    uintptr_t cmcAddr        = 0;
};

// Enable/disable fly. On enable: capture the pawn's current MovementMode (to
// restore later), force MOVE_Flying, start the worker. On disable: restore the
// captured MovementMode, zero velocity, stop the worker. Returns 1 (active),
// 0 (off), or a negative FlyResult.
int32_t SetEnabled(bool enable);

// Set the flight speed (uu/s), clamped to [FLY_SPEED_MIN, FLY_SPEED_MAX].
// Takes effect on the next worker tick. Returns FR_OK.
int32_t SetSpeed(double uuPerSec);

// Select the active keyboard preset (0 WASD / 1 numpad / 2 arrows). Returns
// FR_OK, or FR_ERR_REFLECT for an out-of-range value.
int32_t SetPreset(int32_t preset);

// Read the live fly state (+ CMC owner for Locate-in-GWorld). Always fills
// FlyStatus; code carries why a read failed.
int32_t GetStatus(FlyStatus& out);

// True when fly is currently engaged (cheap, for the UI badge).
bool IsActive();

// Stop and join the fly worker. Idempotent. Called by SetEnabled(false) and
// from UE5_Shutdown() before the DLL unloads.
void StopWorker();

} // namespace Dunste
