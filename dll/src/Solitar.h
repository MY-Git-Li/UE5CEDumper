#pragma once

// ============================================================
// Solitar — 索莉塔 (研究人類的大魔族 — Greater demon studying humanity)
// GodMode: force the local player pawn's AActor::bCanBeDamaged bitfield off via
// live UE reflection (the same FBoolProperty bit-write Wirbel uses for
// bShowMouseCursor, retargeted to the pawn). Stateful ON/OFF toggle backed by a
// re-assert worker so it survives respawns. Pure memory write — no UFunction
// invoke, no game thread. UE4/UE5 version-agnostic. Contract: docs/godmode-spec.md.
// ============================================================

#include <cstdint>
#include <string>

namespace Solitar {

// Result codes shared across exports, pipe, and mailbox (docs/godmode-spec.md §6.3).
// Non-negative SetGodMode/GetGodMode returns are the OBSERVED live state (1/0).
enum ProtectResult : int32_t {
    PR_OK            = 0,
    PR_ERR_NOT_INIT  = -1,   // DLL not initialized / GWorld unavailable
    PR_ERR_NO_PAWN   = -3,   // local pawn null (menu/cutscene/spectator)
    PR_ERR_REFLECT   = -4,   // bCanBeDamaged FBoolProperty not resolved
    PR_ERR_WRITE     = -10,  // raw bit write failed
};

// Combined snapshot for the UI badge / connect-time query.
struct State {
    int8_t want;          // desired toggle (1 = GodMode requested), survives UI reconnect
    int8_t live;          // observed live state (1 = immune, 0 = can be damaged, -1 = unresolvable)
    bool   resolvable;    // could the live pawn + property be read this instant
};

// Apply a desired single-bit value to a byte, leaving the other 7 bits intact.
// Pure helper — header-inline so dll_helpers_test can cover it without linking
// Solitar.cpp (the project's leaf-test pattern).
inline uint8_t ApplyBoolBit(uint8_t cur, uint8_t mask, bool value) {
    return value ? static_cast<uint8_t>(cur | mask)
                 : static_cast<uint8_t>(cur & ~mask);
}

// Generic damage/invincibility flag matcher (T2 — docs/godmode-spec.md §5.2a).
// Given an ALREADY-LOWERCASED reflected bool property name, return true when it
// is a known damage/invincibility flag and set outProtect to the value that
// means "protected" (what GodMode ON writes; OFF writes the inverse). Universal
// keyword table — no per-game config, same philosophy as PropertyScoringTable.
// Pure — covered by dll_helpers_test. Deliberately conservative: only
// unambiguous receive-damage / invincibility terms (NOT bare "candamage" /
// "nodamage" / "damageable", which can mean "deals damage" on a pawn).
inline bool MatchProtectionBool(const std::string& nameLower, bool& outProtect) {
    struct Rule { const char* kw; bool protect; };
    static const Rule kRules[] = {
        { "invulnerab",      true  },  // bInvulnerable / bIsInvulnerable
        { "invincib",        true  },  // bInvincible / bIsInvincible
        { "immort",          true  },  // bImmortal / bIsImmortal
        { "godmode",         true  },  // bGodMode
        { "unkillable",      true  },
        { "cannotdie",       true  },
        { "cannotbekilled",  true  },
        { "deathless",       true  },
        { "muteki",          true  },  // 無敵 (romaji) — common in JP titles
        { "damageimmun",     true  },  // bDamageImmune / DamageImmunity
        { "notakedamage",    true  },
        { "cantakedamage",   false },  // false = cannot take damage
        { "canbedamaged",    false },  // AActor::bCanBeDamaged
    };
    for (const auto& r : kRules)
        if (nameLower.find(r.kw) != std::string::npos) { outProtect = r.protect; return true; }
    return false;
}

// Turn GodMode on/off. GodMode ON ⇒ AActor::bCanBeDamaged set FALSE. Applies to
// the live pawn immediately and starts/stops the re-assert worker. Returns the
// OBSERVED live state (1/0) or a negative ProtectResult.
int32_t SetGodMode(bool on);

// Read the OBSERVED GodMode state on the live pawn (1 = immune, 0 = can be
// damaged) or a negative ProtectResult when the pawn/property can't be resolved.
int32_t GetGodMode();

// The desired toggle (s_wantGod), independent of whether a pawn currently
// resolves — lets the UI restore the toggle on reconnect even from a menu.
bool IsGodModeWanted();

// Combined state for the badge. Always returns PR_OK (fills State; live = -1 and
// resolvable = false when no pawn).
int32_t GetState(State& out);

// General primitive (docs/godmode-spec.md §5.4) — v2 hook for "force any bool"
// from Property Search. Resolve + read-modify-write one reflected FBoolProperty
// on an object. `on` is the DESIRED VALUE OF THE PROPERTY. One-shot (no
// re-assert). Returns the observed property bit (1/0) or a negative ProtectResult.
int32_t SetActorBool(uintptr_t obj, uintptr_t classAddr, const char* propName, bool on);

// Stop and join the re-assert worker. Idempotent. Called by SetGodMode(false)
// and from UE5_Shutdown() before the DLL unloads.
void StopWorker();

} // namespace Solitar
