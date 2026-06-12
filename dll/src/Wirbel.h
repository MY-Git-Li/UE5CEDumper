#pragma once

// ============================================================
// Wirbel — 維爾貝爾 (北部魔法隊小隊長 — pragmatic soldier)
// Teleport: marker save/recall + cursor teleport (BugIt-style).
// Swift battlefield repositioning — the soldier who relocates first.
//
// All property offsets and UFunctions are resolved live from UE
// reflection (Ubel) — nothing hardcoded, UE4/UE5 version-agnostic.
// Full design contract: docs/teleport-spec.md.
// ============================================================

#include <cstdint>
#include "Grimoire.h"

namespace Wirbel {

// Result codes shared across exports, pipe, and mailbox
// (docs/teleport-spec.md §8).
enum TeleportResult : int32_t {
    TP_OK                = 0,
    TP_ERR_NOT_INIT      = -1,   // DLL not initialized / GWorld unavailable
    TP_ERR_NO_CONTROLLER = -2,   // local PlayerController not resolved
    TP_ERR_NO_PAWN       = -3,   // pawn is null (menu/cutscene/spectator)
    TP_ERR_REFLECTION    = -4,   // required property/UFunction not resolved
    TP_ERR_INVOKE        = -5,   // invoke failed or timed out
    TP_ERR_EMPTY_MARKER  = -6,   // marker slot empty / bad slot index
    TP_ERR_MAP_MISMATCH  = -7,   // recall refused: marker saved on another map
    TP_ERR_NO_HIT        = -8,   // trace found no blocking hit
    TP_ERR_NO_CURSOR     = -9,   // mouse position unavailable, center fallback off
    TP_ERR_WRITE_FAILED  = -10,  // tier-2 raw write also failed
};

// Pose always crosses this layer as doubles regardless of engine width
// (UE4 float FVector/FRotator values are widened at the read boundary).
struct Pose {
    double X = 0, Y = 0, Z = 0;
    double Pitch = 0, Yaw = 0, Roll = 0;
};

struct Marker {
    bool Valid = false;
    Pose P{};
    char MapName[Grimoire::TELEPORT_MAPNAME_CAP] = {};
};

// Read the current pawn pose (location from RootComponent.RelativeLocation,
// rotation from Controller.ControlRotation). When the pawn's root is
// attached (vehicle/platform), falls back to invoking K2_GetActorLocation
// for world-space coordinates. outSource (optional): 0 = raw read,
// 1 = invoke path.
int32_t GetPose(Pose& out, char* mapName, int32_t mapNameCap, uint8_t* outSource);

// Save the current pose + map name into a marker slot (0..TELEPORT_SLOTS-1).
int32_t SaveMarker(int32_t slot);

// Teleport back to a saved marker. Refuses with TP_ERR_MAP_MISMATCH when the
// current map differs from the marker's, unless force. tierOut (optional):
// 1 = invoke path (K2_SetActorLocation), 2 = raw-write fallback.
int32_t RecallMarker(int32_t slot, bool force, uint8_t* tierOut);

// Teleport to an explicit pose (BugItGo interop, pipe-only path). Bypasses
// the marker store and the map check. hasRot: also restore Pitch/Yaw/Roll.
int32_t RecallExplicit(const Pose& pose, bool hasRot, uint8_t* tierOut);

// Teleport the pawn to the world position under the mouse cursor (or the
// screen center when the cursor is unavailable and fallbackToCenter is set).
// traceChannel is the raw ETraceTypeQuery byte (0 ≈ Visibility by default
// engine mapping; games can remap). zOffset is added to the hit Z so the
// capsule doesn't spawn intersecting the ground. outHit (optional) receives
// the raw hit point (without zOffset) in X/Y/Z.
int32_t TeleportToCursor(double zOffset, int32_t traceChannel,
                         bool fallbackToCenter, Pose* outHit,
                         uint8_t* tierOut, bool* outUsedCenter);

// Read a marker slot. TP_ERR_EMPTY_MARKER when the slot is empty.
int32_t GetMarker(int32_t slot, Marker& out);

// Clear a marker slot.
int32_t ClearMarker(int32_t slot);

// Current UWorld object name ("" when unavailable). Cheap — no chain walk.
bool GetCurrentMapName(char* buf, int32_t cap);

} // namespace Wirbel
