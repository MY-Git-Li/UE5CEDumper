#pragma once

// ============================================================
// Laufen — 走る (高速移動の魔法 — "to run", high-speed-movement mage)
// MovementTuning: force per-pawn UCharacterMovementComponent float knobs
// (MaxWalkSpeed / GravityScale / JumpZVelocity) by a multiplier of their
// captured base value, held against per-tick game overwrites by a re-assert
// worker — the same write-on-drift pattern Solitar (GodMode) uses, retargeted
// from a single FBoolProperty bit to FloatProperty scalars on the CMC.
//
// Mechanism: resolve the local pawn (GWorld → OwningGameInstance →
// LocalPlayers[0] → PlayerController → Pawn), hop to its CharacterMovement
// sub-object (reflected "CharacterMovement" ObjectProperty), FindField the
// named float, capture its untouched base once, then write base*multiplier.
// Pure reflected memory read/write via Macht (SEH) — NO UFunction invoke, NO
// game thread. Self-contained (Path B): only public Ubel/Aura/Macht + DynOff,
// no coupling to Wirbel. All offsets resolved by FName (DynOff rule) →
// UE4/UE5-agnostic. No cached instance pointers — the pawn/CMC is re-resolved
// every operation and every worker tick (stale-pointer crash class).
//
// P1 wires KNOB_WALK_SPEED; P2 (Gravity) and P3 (Super Jump) reuse the same
// engine via KNOB_GRAVITY / KNOB_JUMP with no new machinery.
// ============================================================

#include <cstdint>
#include <string>

namespace Laufen {

// Result codes (mirrors Solitar::ProtectResult conventions). Non-negative
// returns from Set/Reset are the observed live state (1 = override active,
// 0 = inactive); negative values are errors.
enum MoveResult : int32_t {
    MR_OK           = 0,
    MR_ERR_NOT_INIT = -1,   // DLL not initialized / GWorld unavailable
    MR_ERR_REFLECT  = -4,   // target float property not resolved on the CMC
    MR_ERR_NO_PAWN  = -3,   // local pawn null (menu / cutscene / spectator)
    MR_ERR_NO_CMC   = -5,   // pawn has no CharacterMovement (vehicle / custom framework)
    MR_ERR_WRITE    = -10,  // raw float write failed
};

// One tunable CMC float. Stable order — also the wire value used by the pipe
// "knob" string mapping. P1 wires WALK_SPEED only; GRAVITY/JUMP are reserved
// for P2/P3 (the engine already supports them).
enum KnobId : int32_t {
    KNOB_WALK_SPEED = 0,    // UCharacterMovementComponent::MaxWalkSpeed
    KNOB_GRAVITY    = 1,    // UCharacterMovementComponent::GravityScale
    KNOB_JUMP       = 2,    // UCharacterMovementComponent::JumpZVelocity
    KNOB_COUNT
};

// Live snapshot of one knob, for the UI readout + Locate-in-GWorld handoff.
struct KnobInfo {
    bool        resolved   = false; // field found on the live CMC this instant
    double      current    = 0.0;   // live value read from memory
    double      base       = 0.0;   // captured base (valid only when active)
    double      multiplier = 1.0;   // desired multiplier (1.0 = off)
    bool        active     = false; // override engaged (worker holding it)
    uintptr_t   ownerAddr  = 0;     // CMC address — Locate-in-GWorld owner
    int32_t     fieldOffset = -1;   // float offset within the CMC
    std::string fieldName;          // reflected property name
};

struct Snapshot {
    int32_t   code    = 0;      // MoveResult: MR_OK or a negative error
    bool      hasCmc  = false;  // a CharacterMovement resolved on the pawn
    uintptr_t cmcAddr = 0;
    KnobInfo  knobs[KNOB_COUNT];
};

// Read every knob on the current pawn's CMC (current/base/multiplier/active +
// owner+offset). Always fills Snapshot; code carries why a read failed.
int32_t GetSnapshot(Snapshot& out);

// Read a single knob (for the UI badge / connect-time query).
int32_t GetKnob(int32_t knobId, KnobInfo& out);

// Apply a multiplier (of the captured base) to a knob. Captures the base on
// first activation (and re-captures when the pawn changes — respawn). Starts
// the re-assert worker. Returns the observed active state (1) or a negative
// MoveResult. multiplier is clamped to [MOVE_MULT_MIN, MOVE_MULT_MAX].
int32_t SetMultiplier(int32_t knobId, double multiplier);

// Disable a knob: best-effort restore the captured base, clear the override.
// Stops the worker when no knob remains active. Returns MR_OK or a negative
// MoveResult.
int32_t ResetKnob(int32_t knobId);

// Stop and join the re-assert worker. Idempotent. Called by ResetKnob (when
// nothing else is active) and from UE5_Shutdown() before the DLL unloads.
void StopWorker();

} // namespace Laufen
