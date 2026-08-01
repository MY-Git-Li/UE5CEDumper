#pragma once

// ============================================================
// Solide — ゾリーデ (剣士 — blindfold swordsman, "solid")
// EnemyDetection / ForceField: hold an arbitrary reflected field at a value
// across ALL live instances of a class via a write-on-drift re-assert worker —
// bool ON/OFF, ObjectProperty→null, or numeric→absolute. Plus a player
// stealth/noise/visibility-meter auto-finder (keyword-scored numeric field on
// the local pawn + its owned components). The shipped, honest subset of the
// "enemies can't detect you" evaluation: no universal detection bool exists, so
// the user DISCOVERS the field (Property Search / the stealth finder) and Solide
// holds it "solid".
//
// Structurally the multi-instance sibling of Hemmung (absolute-value hold): same
// s_mutex + s_workerMutex two-lock split, same write-on-drift re-assert worker,
// same "no cached instance pointers — re-resolve every tick" discipline. The
// difference: the target is a POOL (Aura::FindInstancesByClass(className) each
// tick, bounded + CDO-skipped), not one re-resolved pawn. Reuses the public
// Solitar bit-write (SetActorBool/GetActorBool) for the bool kind and clones
// Hemmung's LWC-width-aware float read/write for the numeric kind. Pure reflected
// memory read/write via Macht (SEH) — NO UFunction invoke, NO game thread.
// ============================================================

#include <cstdint>
#include <string>
#include <vector>

namespace Solide {

// Result codes. Non-negative AddForce returns are the live "N held" instance
// count (0 = the class/field resolved nothing this instant — an honest no-op
// signal); negative values are errors.
enum ForceResult : int32_t {
    FR_OK            = 0,
    FR_ERR_NOT_INIT  = -1,   // DLL not initialized / GWorld unavailable
    FR_ERR_NO_TARGET = -6,   // no live instance of the class resolved
    FR_ERR_REFLECT   = -4,   // field not reflected on the class / wrong type
    FR_ERR_WRITE     = -10,  // raw write failed on every instance
    FR_ERR_BAD_KIND  = -11,  // unknown ForceKind
    FR_ERR_WEAK_PTR  = -12,  // object-null asked on a weak/soft/lazy ptr (refused — GObjects[0] trap)
    FR_ERR_BAD_ARGS  = -13,  // null/empty className or fieldName
};

// What to force the field to. Stable order — also the wire value used by the pipe
// "kind" string mapping.
enum ForceKind : int32_t {
    K_BOOL        = 0,   // FBoolProperty bit → value!=0 (ON) / ==0 (OFF)
    K_OBJECT_NULL = 1,   // strong ObjectProperty → nullptr (value ignored)
    K_NUMERIC     = 2,   // Float/Double/Int* → held ABSOLUTE `value`
};

// One active hold-job, for the UI list + N-held honesty badge + Locate handoff.
struct ForcedFieldInfo {
    std::string className;    // class whose instances are held (substring-matched)
    std::string fieldName;    // reflected property being forced
    int32_t     kind = K_BOOL;
    double      value = 0.0;  // desired value (bool: 1/0; numeric: absolute)
    int32_t     held = 0;     // live instance count the field resolved on last tick
    uintptr_t   sampleOwner = 0; // one live owner addr (Locate-in-GWorld handoff); 0 = none
    int32_t     sampleOffset = -1; // field offset within the owner
    // Last tick's instance pool hit SOLIDE_MAX_INSTANCES, so `held` is a floor, not a
    // total: more live instances of this class exist and are NOT being held. Without
    // this the UI cannot tell "found nothing" from "found more than the cap and threw
    // the rest away" — the pool is walked in ascending GObjects order, so the ones
    // dropped are the most recently spawned. Never report a true total here: the cheap
    // scan path breaks at the cap (Aura.cpp), and counting past it would cost a full
    // GObjects walk on every 300 ms re-assert tick.
    bool        poolTruncated = false;
};

// One ranked stealth-meter candidate from FindStealthMeter.
struct StealthCandidate {
    std::string className;    // class of the object holding the meter (pawn OR a component)
    uintptr_t   classAddr = 0;
    std::string fieldName;
    std::string typeName;     // reflected numeric type (FloatProperty/DoubleProperty/...)
    uintptr_t   ownerAddr = 0;// the resolved object the value was read from
    double      current = 0.0;// live value (for the UI readout)
    int32_t     score = 0;    // MatchStealthField keyword score (higher = better)
};

// Keyword scorer for a stealth/noise/visibility/detection meter field name.
// Given an ALREADY-LOWERCASED reflected numeric property name, return a score
// (> 0 = a candidate; higher = stronger). Name-only (the FloatProperty type
// prior is applied by the caller). Pure — covered by dll_helpers_test, mirrors
// Solitar::MatchProtectionBool + Edel's kPositive/kNegative split.
inline int32_t MatchStealthField(const std::string& nameLower) {
    struct Stem { const char* kw; int32_t w; };
    // Positive: detection/stealth-meter vocabulary.
    static const Stem kPos[] = {
        { "stealth",    10 }, { "visib",      9 },  // visibility
        { "detect",     9  }, { "noise",      8 },  // detection
        { "aware",      8  }, { "conceal",    8 },  // awareness / concealment
        { "camouflage", 7  }, { "suspicio",   7 },  // suspicion
        { "exposure",   7  }, { "expose",     6 },
        { "perceiv",    7  }, { "sight",      6 },  // perception
        { "hidden",     6  }, { "sneak",      6 },
        { "spotted",    6  }, { "alert",      5 },
        { "threat",     5  }, { "aggro",      5 },
    };
    // Negative: config/limit/meta names that merely CONTAIN a positive stem.
    static const Stem kNeg[] = {
        { "max",     6 }, { "min",     4 }, { "default", 6 },
        { "curve",   5 }, { "config",  5 }, { "radius",  5 },
        { "class",   6 }, { "range",   4 }, { "time",    3 },
        { "delay",   3 }, { "rate",    3 },
    };
    int32_t score = 0;
    for (const auto& s : kPos)
        if (nameLower.find(s.kw) != std::string::npos) score += s.w;
    if (score == 0) return 0;   // no positive signal → not a candidate
    for (const auto& s : kNeg)
        if (nameLower.find(s.kw) != std::string::npos) score -= s.w;
    return score > 0 ? score : 0;
}

// Add (or replace) a hold-job forcing `fieldName` on all live instances of
// `className` to a value, and hold it via the re-assert worker. `kind` selects
// bool/object-null/numeric; for K_BOOL `value` is 1.0 (ON) / 0.0 (OFF), for
// K_NUMERIC it is the absolute value, for K_OBJECT_NULL it is ignored. Captures a
// representative restore base (bool bit / numeric value) so RemoveForce can undo
// it (object-null is not reversible). Applies once immediately + starts the
// worker. Returns the live "N held" count (>= 0) or a negative ForceResult.
int32_t AddForce(const char* className, const char* fieldName, int32_t kind, double value);

// Remove a hold-job (matched by className + fieldName); best-effort restores the
// captured base on all currently-resolved instances (bool/numeric only). Stops
// the worker when no job remains. Returns FR_OK or a negative ForceResult.
int32_t RemoveForce(const char* className, const char* fieldName);

// Remove ALL hold-jobs (best-effort restore each). Stops the worker. FR_OK.
int32_t ClearAll();

// Snapshot the active hold-jobs + their live held counts for the UI list.
int32_t GetState(std::vector<ForcedFieldInfo>& out);

// Auto-find the player's stealth/noise/visibility/detection meter: resolve the
// local pawn (GWorld→GameInstance→LocalPlayers[0]→PC→Pawn) + its owned
// components (Aura::GetRelatedObjects), score every reflected numeric field with
// MatchStealthField, return the ranked candidates (best first, up to maxResults).
// Read-only. Returns FR_OK (out may be empty) or a negative ForceResult.
int32_t FindStealthMeter(std::vector<StealthCandidate>& out, int32_t maxResults = 8);

// Stop and join the re-assert worker. Idempotent. Called by RemoveForce/ClearAll
// (when nothing remains) and from UE5_Shutdown() before the DLL unloads.
void StopWorker();

} // namespace Solide
