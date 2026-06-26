// ============================================================
// Laufen — 拉歐芬 / 走る (高速移動の魔法 — "to run", high-speed-movement mage)
// MovementTuning: force per-pawn UCharacterMovementComponent float knobs
// (MaxWalkSpeed / GravityScale / JumpZVelocity) by a multiplier of their
// captured base, held by a re-assert worker (write-on-drift). Contract: Laufen.h.
//
// This is the float analogue of Solitar (GodMode): same local-pawn resolution
// chain, same s_mutex + s_workerMutex two-lock split, same write-on-drift worker
// — but the target is a FloatProperty scalar on the CharacterMovement sub-object
// instead of a single FBoolProperty bit on the AActor. Self-contained (Path B):
// only public Ubel/Aura/Macht + DynOff, no Wirbel coupling.
// ============================================================

#define LOG_CAT "WALK"
#include "Sein.h"
#include "Laufen.h"
#include "Grimoire.h"
#include "Macht.h"
#include "Aura.h"
#include "Ubel.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <mutex>
#include <string>
#include <thread>

// &GWorld — deref once for UWorld* (defined in Frieren.cpp; same as Solitar/Wirbel).
extern uintptr_t g_cachedGWorld;

namespace {

using namespace Laufen;  // MoveResult codes

// ---- knob definitions (reflected float property names on the CMC) ----
struct KnobDef {
    const char* exact;     // exact FName match wins
    const char* contains;  // fuzzy fallback if a game renamed it slightly
};
const KnobDef kKnobs[KNOB_COUNT] = {
    { "MaxWalkSpeed",  "WalkSpeed"     },  // KNOB_WALK_SPEED
    { "GravityScale",  "GravityScale"  },  // KNOB_GRAVITY (P2)
    { "JumpZVelocity", "JumpZVelocity" },  // KNOB_JUMP    (P3)
};

// Per-knob desired state — survives UI reconnects (the override lives in the
// DLL while the game process lives). Guarded by s_mutex.
struct KnobState {
    bool      active       = false;  // override engaged (worker holding it)
    double    multiplier   = 1.0;    // desired multiplier of base
    double    base         = 0.0;    // captured untouched base value
    uintptr_t capturedPawn = 0;      // pawn the base was captured from (respawn re-capture)
};
KnobState s_knobs[KNOB_COUNT];

// Serializes whole operations: reachable from the Fern pipe thread AND the
// re-assert worker (and, in P4, the Mimic mailbox thread).
std::mutex s_mutex;

// Re-assert worker — separate control mutex so StopWorker()'s join() never runs
// while s_mutex is held (the worker locks s_mutex per tick → would deadlock).
std::thread       s_worker;
std::mutex        s_workerMutex;
std::atomic<bool> s_workerStop{false};

// ---- low-level reads (copied from Solitar; public APIs only) ----

uintptr_t DerefWorld() {
    if (!g_cachedGWorld) return 0;
    uintptr_t w = 0;
    if (!Macht::ReadSafe(g_cachedGWorld, w)) return 0;
    return w;
}

uintptr_t ReadPtrAt(uintptr_t obj, int32_t off) {
    if (!obj || off < 0) return 0;
    uintptr_t v = 0;
    Macht::ReadSafe(obj + static_cast<uintptr_t>(off), v);
    return v;
}

// Read/write a scalar float field honoring the reflected width (4B float / 8B
// double under LWC). Width comes from FieldInfo.Size — never assumed.
bool ReadFloatAt(uintptr_t addr, int32_t size, double& out) {
    if (size >= 8) {
        double d = 0;
        if (!Macht::ReadSafe(addr, d)) return false;
        out = d;
        return true;
    }
    float f = 0;
    if (!Macht::ReadSafe(addr, f)) return false;
    out = static_cast<double>(f);
    return true;
}

bool WriteFloatAt(uintptr_t addr, int32_t size, double v) {
    if (size >= 8) {
        double d = v;
        return Macht::WriteBytes(addr, &d, 8);
    }
    float f = static_cast<float>(v);
    return Macht::WriteBytes(addr, &f, 4);
}

// ---- resolution chain (identical to Solitar — local pawn → CMC) ----

uintptr_t ResolveLocalPC(uintptr_t world) {
    do {
        if (!world) break;
        uintptr_t worldClass = Ubel::GetClass(world);
        int32_t giOff = Ubel::FindFieldOffset(worldClass, "OwningGameInstance",
                                              "GameInstance", nullptr, "ObjectProperty");
        uintptr_t gi = ReadPtrAt(world, giOff);
        if (!gi) break;
        uintptr_t giClass = Ubel::GetClass(gi);
        int32_t lpOff = Ubel::FindFieldOffset(giClass, "LocalPlayers", "LocalPlayers",
                                              nullptr, "ArrayProperty");
        if (lpOff < 0) break;
        Macht::TArrayView arr;
        if (!Macht::ReadTArray(gi + static_cast<uintptr_t>(lpOff), arr) || arr.Count <= 0)
            break;
        uintptr_t lp = Macht::ReadTArrayElement(arr, 0);
        if (!lp) break;
        uintptr_t lpClass = Ubel::GetClass(lp);
        int32_t pcOff = Ubel::FindFieldOffset(lpClass, "PlayerController",
                                              "PlayerController", nullptr, "ObjectProperty");
        uintptr_t pc = ReadPtrAt(lp, pcOff);
        if (pc) return pc;
    } while (false);

    auto rset = Aura::FindInstancesByClass("PlayerController", false, 100);
    uintptr_t firstNonCdo = 0;
    for (const auto& r : rset.results) {
        if (!r.addr || r.name.find("Default__") != std::string::npos) continue;
        if (!firstNonCdo) firstNonCdo = r.addr;
        uintptr_t cls = Ubel::GetClass(r.addr);
        int32_t playerOff = Ubel::FindFieldOffset(cls, "Player", "Player",
                                                  "Controller", "ObjectProperty");
        if (playerOff >= 0 && ReadPtrAt(r.addr, playerOff))
            return r.addr;
    }
    return firstNonCdo;
}

uintptr_t HopThroughDebugCamera(uintptr_t pc) {
    if (!pc) return pc;
    uintptr_t cls = Ubel::GetClass(pc);
    std::string clsName = Ubel::GetName(cls);
    if (clsName.find("DebugCameraController") == std::string::npos) return pc;
    int32_t origOff = Ubel::FindFieldOffset(cls, "OriginalControllerRef",
                                            "OriginalController", nullptr, "ObjectProperty");
    uintptr_t orig = ReadPtrAt(pc, origOff);
    return orig ? orig : pc;
}

uintptr_t ResolvePawn(uintptr_t pc) {
    uintptr_t cls = Ubel::GetClass(pc);
    uintptr_t pawn = ReadPtrAt(pc, Ubel::FindFieldOffset(cls, "Pawn"));
    if (pawn) return pawn;
    return ReadPtrAt(pc, Ubel::FindFieldOffset(cls, "AcknowledgedPawn"));
}

// Resolved local pawn + its CharacterMovement sub-object for this instant.
struct Ctx {
    uintptr_t world = 0, pc = 0, pawn = 0, pawnClass = 0, cmc = 0, cmcClass = 0;
};

int32_t ResolveCtx(Ctx& c) {
    c = Ctx{};
    c.world = DerefWorld();
    if (!c.world) return MR_ERR_NOT_INIT;
    c.pc = ResolveLocalPC(c.world);
    if (!c.pc) return MR_ERR_NO_PAWN;
    c.pc = HopThroughDebugCamera(c.pc);
    c.pawn = ResolvePawn(c.pc);
    if (!c.pawn) return MR_ERR_NO_PAWN;
    c.pawnClass = Ubel::GetClass(c.pawn);
    if (!c.pawnClass) return MR_ERR_REFLECT;
    int32_t cmOff = Ubel::FindFieldOffset(c.pawnClass, "CharacterMovement",
                                          "CharacterMovement", nullptr, "ObjectProperty");
    c.cmc = ReadPtrAt(c.pawn, cmOff);
    if (!c.cmc) return MR_ERR_NO_CMC;
    c.cmcClass = Ubel::GetClass(c.cmc);
    if (!c.cmcClass) return MR_ERR_REFLECT;
    return MR_OK;
}

// Resolved float field location on the live CMC.
struct FieldLoc {
    uintptr_t   addr   = 0;
    int32_t     offset = -1;
    int32_t     size   = 4;
    std::string name;
};

bool ResolveField(const Ctx& c, int knobId, FieldLoc& f) {
    if (knobId < 0 || knobId >= KNOB_COUNT) return false;
    FieldInfo fi{};
    const KnobDef& d = kKnobs[knobId];
    if (!Ubel::FindField(c.cmcClass, d.exact, d.contains, nullptr, "FloatProperty", fi)
        || fi.Offset < 0)
        return false;
    f.addr   = c.cmc + static_cast<uintptr_t>(fi.Offset);
    f.offset = fi.Offset;
    f.size   = fi.Size;
    f.name   = fi.Name;
    return true;
}

bool AnyActiveLocked() {
    for (int i = 0; i < KNOB_COUNT; ++i)
        if (s_knobs[i].active) return true;
    return false;
}

// Apply one knob's desired value to the live CMC (caller holds s_mutex).
// Write-on-drift: only writes when the live value differs from base*multiplier.
void ApplyKnobLocked(const Ctx& c, int knobId, bool* drifted) {
    KnobState& k = s_knobs[knobId];
    if (!k.active) return;
    FieldLoc f;
    if (!ResolveField(c, knobId, f)) return;   // field gone this tick
    double current = 0;
    if (!ReadFloatAt(f.addr, f.size, current)) return;
    // Re-capture base on pawn change (respawn): the new CMC's value is untouched
    // by us, so it is the genuine base to scale.
    if (c.pawn != k.capturedPawn) {
        k.base = current;
        k.capturedPawn = c.pawn;
    }
    double target = k.base * k.multiplier;
    double eps = (std::max)(1e-3, std::fabs(target) * 1e-5);
    if (std::fabs(current - target) > eps) {
        if (WriteFloatAt(f.addr, f.size, target) && drifted) *drifted = true;
    }
}

// Fill a KnobInfo for the UI from the live CMC + stored desired state.
void FillKnobLocked(const Ctx& c, int knobId, KnobInfo& info) {
    const KnobState& k = s_knobs[knobId];
    info.multiplier = k.multiplier;
    info.active     = k.active;
    info.base       = k.base;
    FieldLoc f;
    if (ResolveField(c, knobId, f)) {
        double cur = 0;
        if (ReadFloatAt(f.addr, f.size, cur)) {
            info.resolved    = true;
            info.current     = cur;
            info.ownerAddr   = c.cmc;
            info.fieldOffset = f.offset;
            info.fieldName   = f.name;
        }
    }
}

// ---- re-assert worker ----

void WorkerLoop() {
    LOG_INFO("Movement: re-assert worker started (%d ms)", Grimoire::MOVE_REASSERT_MS);
    int driftCount = 0;
    while (!s_workerStop.load()) {
        for (int slept = 0;
             slept < Grimoire::MOVE_REASSERT_MS && !s_workerStop.load();
             slept += 25)
            std::this_thread::sleep_for(std::chrono::milliseconds(25));
        if (s_workerStop.load()) break;

        std::lock_guard<std::mutex> lk(s_mutex);
        if (!AnyActiveLocked()) continue;     // all knobs reset between ticks
        Ctx c;
        if (ResolveCtx(c) != MR_OK) continue; // pawn/CMC gone (menu) — retry next tick
        bool drifted = false;
        for (int i = 0; i < KNOB_COUNT; ++i)
            ApplyKnobLocked(c, i, &drifted);
        if (drifted) {
            ++driftCount;
            if (driftCount <= 5 || driftCount % 100 == 0)
                LOG_WARN("Movement: re-asserted knob(s) (drift #%d) — the game keeps "
                         "recomputing the value each tick (sprint/ability system); the "
                         "override is being held against it.", driftCount);
        }
    }
    LOG_INFO("Movement: re-assert worker stopped");
}

void StartWorker() {
    std::lock_guard<std::mutex> lk(s_workerMutex);
    if (s_worker.joinable()) return;   // already running
    s_workerStop.store(false);
    s_worker = std::thread(WorkerLoop);
}

} // namespace

namespace Laufen {

int32_t GetSnapshot(Snapshot& out) {
    out = Snapshot{};
    std::lock_guard<std::mutex> lk(s_mutex);
    Ctx c;
    int32_t rc = ResolveCtx(c);
    out.code = rc;
    // Always surface the stored desired state so the UI can render "active" even
    // when the pawn momentarily doesn't resolve (menu / loading).
    for (int i = 0; i < KNOB_COUNT; ++i) {
        out.knobs[i].multiplier = s_knobs[i].multiplier;
        out.knobs[i].active     = s_knobs[i].active;
        out.knobs[i].base       = s_knobs[i].base;
    }
    if (rc != MR_OK) return rc;
    out.hasCmc  = true;
    out.cmcAddr = c.cmc;
    for (int i = 0; i < KNOB_COUNT; ++i)
        FillKnobLocked(c, i, out.knobs[i]);
    return MR_OK;
}

int32_t GetKnob(int32_t knobId, KnobInfo& out) {
    out = KnobInfo{};
    if (knobId < 0 || knobId >= KNOB_COUNT) return MR_ERR_REFLECT;
    std::lock_guard<std::mutex> lk(s_mutex);
    Ctx c;
    int32_t rc = ResolveCtx(c);
    if (rc != MR_OK) {
        out.multiplier = s_knobs[knobId].multiplier;
        out.active     = s_knobs[knobId].active;
        out.base       = s_knobs[knobId].base;
        return rc;
    }
    FillKnobLocked(c, knobId, out);
    return MR_OK;
}

int32_t SetMultiplier(int32_t knobId, double multiplier) {
    if (knobId < 0 || knobId >= KNOB_COUNT) return MR_ERR_REFLECT;
    multiplier = (std::max)(Grimoire::MOVE_MULT_MIN,
                            (std::min)(Grimoire::MOVE_MULT_MAX, multiplier));
    int32_t rc = MR_OK;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        Ctx c;
        rc = ResolveCtx(c);
        if (rc != MR_OK) return rc;
        FieldLoc f;
        if (!ResolveField(c, knobId, f)) return MR_ERR_REFLECT;
        double current = 0;
        if (!ReadFloatAt(f.addr, f.size, current)) return MR_ERR_REFLECT;
        KnobState& k = s_knobs[knobId];
        // Capture an untouched base only when (re)activating or the pawn changed —
        // NOT when merely changing the multiplier on the same active pawn, or we
        // would fold our own write into the base and compound.
        if (!k.active || c.pawn != k.capturedPawn) {
            k.base = current;
            k.capturedPawn = c.pawn;
        }
        k.multiplier = multiplier;
        k.active = true;
        double target = k.base * multiplier;
        if (!WriteFloatAt(f.addr, f.size, target))
            rc = MR_ERR_WRITE;   // leave active; the worker will retry next tick
        LOG_INFO("Movement: knob %d ('%s') base=%.3f x%.3f -> %.3f (rc=%d)",
                 knobId, f.name.c_str(), k.base, multiplier, target, rc);
    }
    StartWorker();
    return (rc < 0) ? rc : 1;   // 1 = override active
}

int32_t ResetKnob(int32_t knobId) {
    if (knobId < 0 || knobId >= KNOB_COUNT) return MR_ERR_REFLECT;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        KnobState& k = s_knobs[knobId];
        if (k.active) {
            Ctx c;
            if (ResolveCtx(c) == MR_OK) {
                FieldLoc f;
                if (ResolveField(c, knobId, f))
                    WriteFloatAt(f.addr, f.size, k.base);   // best-effort restore
            }
        }
        k.active = false;
        k.multiplier = 1.0;
        k.capturedPawn = 0;
    }
    // Stop the worker OUTSIDE s_mutex when nothing remains active (join() locks
    // s_mutex per tick — joining under s_mutex would deadlock).
    bool anyActive;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        anyActive = AnyActiveLocked();
    }
    if (!anyActive) StopWorker();
    LOG_INFO("Movement: reset knob %d (any active left=%d)", knobId, anyActive ? 1 : 0);
    return MR_OK;
}

int32_t SetKnobPercent(int32_t knobId, double percent) {
    if (knobId < 0 || knobId >= KNOB_COUNT) return MR_ERR_REFLECT;
    // 100% (±0.5) means "off" — the single-call API the CE-Lua/mailbox path uses.
    if (std::fabs(percent - 100.0) < 0.5) {
        int32_t rc = ResetKnob(knobId);
        return (rc < 0) ? rc : 0;   // 0 = off
    }
    // Jump: percent is HEIGHT %, apply velocity multiplier = sqrt(height) (h ∝ v²).
    // Other knobs: percent is the multiplier directly.
    double mult = (knobId == KNOB_JUMP) ? std::sqrt(percent / 100.0)
                                        : (percent / 100.0);
    return SetMultiplier(knobId, mult);   // 1 active / negative MoveResult
}

void StopWorker() {
    std::lock_guard<std::mutex> lk(s_workerMutex);
    if (!s_worker.joinable()) return;
    s_workerStop.store(true);
    s_worker.join();
}

} // namespace Laufen
