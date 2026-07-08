// ============================================================
// Schlacht — シュラハト / Schlacht (「全知者」 — "The Omniscient")
// See-through occluders: let an omniscient view pass through the world so the
// wall between the camera and your character never blocks it.
//
// Each worker tick traces a ray from the CAMERA to the local PAWN
// (UKismetSystemLibrary::LineTraceSingle, game thread via Stark), takes the
// NEAREST blocking hit, and — only if that actor is NOT a Pawn/Character (so
// enemies / NPCs / the player itself are never touched) — hides it via
// AActor::SetActorHiddenInGame(true). The previously-hidden actor is restored
// as the camera moves; everything is un-hidden on disable / disconnect.
//
// Stage 1: NEAREST single occluder (camera→pawn LineTraceSingle). A future
// Stage 2 can widen to all occluders (LineTraceMulti). Single-player only.
// Offsets resolved by FName (DynOff), UE4/UE5-agnostic; pawn re-resolved per tick.
// ============================================================
#pragma once
#include <cstdint>

namespace Schlacht {

// Result / status code (0 = OK, negative = why the tick could not act).
enum SeeThroughResult {
    STR_OK             =  0,
    STR_ERR_NOT_INIT   = -1,   // GWorld not resolved (Connect & scan first)
    STR_ERR_NO_PAWN    = -2,   // no local player pawn (menu / loading / cutscene)
    STR_ERR_REFLECTION = -3,   // LineTraceSingle / SetActorHiddenInGame cooked out
    STR_ERR_NO_CAMERA  = -4,   // PlayerCameraManager / GetCameraLocation unavailable
};

struct SeeThroughStatus {
    int32_t code        = 0;      // last SeeThroughResult (0 = OK on the last tick)
    bool    active      = false;  // the see-through worker is engaged
    bool    hasTarget   = false;  // camera + pawn resolved on the last tick
    int32_t hiddenCount = 0;      // occluders currently hidden (0 or 1 in Stage 1)
    int32_t state       = -1;     // last enable/disable result (1/0/neg); -1 = poll-only
};

// Enable / disable the see-through worker. Idempotent. enable=false un-hides any
// actor we hid and stops the worker. Returns 1 (on), 0 (off), or negative on error.
int32_t SetEnabled(bool enable);

// Poll the live status (safe to call from the pipe thread).
int32_t GetStatus(SeeThroughStatus& out);

// True while the worker is engaged.
bool IsActive();

// Stop + join the worker. Idempotent. Called by SetEnabled(false) and from
// UE5_Shutdown() before the DLL unloads.
void StopWorker();

} // namespace Schlacht
