// ============================================================
// Schlacht — シュラハト / Schlacht (「全知者」 — "The Omniscient")
// See-through occluders. Contract: Schlacht.h.
//
// Worker tick (SCHLACHT_TICK_MS): resolve local pawn + camera, trace
// camera→pawn (LineTraceSingle, game thread via Stark), take the NEAREST hit;
// if it is NOT a Pawn/Character, hide it (SetActorHiddenInGame). The prior
// occluder is restored as the view changes; all hides are undone on disable.
// Stage 1: one nearest occluder at a time. Single-player only.
// ============================================================

#define LOG_CAT "SEETHRU"
#include "Sein.h"
#include "Schlacht.h"
#include "Grimoire.h"
#include "Macht.h"
#include "Aura.h"
#include "Tot.h"     // Tot::MarkBackgroundWorker — see-through worker ignores per-command cancel (M4)
#include "Ubel.h"
#include "Stark.h"      // IsGameThreadResponsive — gate the game-thread invokes

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

// &GWorld — deref once for UWorld* (defined in Frieren.cpp; same as Dunste/Laufen).
extern uintptr_t g_cachedGWorld;
// Game-thread ProcessEvent invoke (Stark) — LineTraceSingle reads the physics
// scene and SetActorHiddenInGame refreshes render state, both game-thread only.
extern "C" int32_t   UE5_CallProcessEventEx(uintptr_t instance, uintptr_t ufunc,
                                            uintptr_t params, uint32_t size);
// Resolve a class CDO/instance by short name (KismetSystemLibrary for the trace).
extern "C" uintptr_t UE5_FindInstanceOfClass(const char* className);

namespace {

using namespace Schlacht;

// ---- low-level reads (public Macht APIs only) ----

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

// Read a 3-component FVector honouring the reflected struct width
// (12B = 3×float / 24B = 3×double under LWC).
void ReadVec3Buf(const uint8_t* p, int32_t structSize, double out[3]) {
    if (structSize >= 24) {
        for (int i = 0; i < 3; ++i) { double d; std::memcpy(&d, p + i * 8, 8); out[i] = d; }
    } else {
        for (int i = 0; i < 3; ++i) { float f; std::memcpy(&f, p + i * 4, 4); out[i] = static_cast<double>(f); }
    }
}

// Case-insensitive name compare (UFunction / param / field names).
bool IEq(const std::string& a, const char* b) {
    size_t n = std::strlen(b);
    if (a.size() != n) return false;
    for (size_t i = 0; i < n; ++i)
        if (std::tolower(static_cast<unsigned char>(a[i])) !=
            std::tolower(static_cast<unsigned char>(b[i]))) return false;
    return true;
}

// ---- reflection: find a UFunction / param, marshal a param buffer ----

// Find a UFunction by name, walking class → Super (engine setters live on the
// AActor base, above the concrete pawn/actor class). Public Ubel only.
bool FindFuncByName(uintptr_t classAddr, const char* name, FunctionInfo& out) {
    uintptr_t cls = classAddr;
    for (int guard = 0; cls && guard < 64; ++guard) {
        for (const auto& f : Ubel::WalkFunctions(cls))
            if (IEq(f.name, name)) { out = f; return true; }
        uintptr_t super = 0;
        if (!Macht::ReadSafe(cls + static_cast<uintptr_t>(DynOff::USTRUCT_SUPER), super)
            || super == cls)
            break;
        cls = super;
    }
    return false;
}

const FunctionParam* FindParam(const FunctionInfo& fi, const char* name) {
    for (const auto& p : fi.params)
        if (IEq(p.name, name)) return &p;
    return nullptr;
}

const FunctionParam* FindReturnParam(const FunctionInfo& fi) {
    for (const auto& p : fi.params)
        if (p.isReturn) return &p;
    return nullptr;
}

bool ParamFits(const std::vector<uint8_t>& buf, const FunctionParam* p, int32_t need) {
    return p && p->offset >= 0 && p->offset + need <= static_cast<int32_t>(buf.size());
}

void WriteVecParam(std::vector<uint8_t>& buf, const FunctionInfo& fi, const char* name, const double v[3]) {
    const FunctionParam* p = FindParam(fi, name);
    int32_t need = (p && p->size >= 24) ? 24 : 12;
    if (!ParamFits(buf, p, need)) return;
    if (need == 24) { for (int i = 0; i < 3; ++i) { double d = v[i];               std::memcpy(buf.data() + p->offset + i * 8, &d, 8); } }
    else            { for (int i = 0; i < 3; ++i) { float f = static_cast<float>(v[i]); std::memcpy(buf.data() + p->offset + i * 4, &f, 4); } }
}

void WriteByteParam(std::vector<uint8_t>& buf, const FunctionInfo& fi, const char* name, uint8_t b) {
    const FunctionParam* p = FindParam(fi, name);
    if (ParamFits(buf, p, 1)) buf[p->offset] = b;
}

void WriteBoolParam(std::vector<uint8_t>& buf, const FunctionInfo& fi, const char* name, bool v) {
    WriteByteParam(buf, fi, name, v ? 1 : 0);
}

void WritePtrParam(std::vector<uint8_t>& buf, const FunctionInfo& fi, const char* name, uintptr_t ptr) {
    const FunctionParam* p = FindParam(fi, name);
    if (ParamFits(buf, p, static_cast<int32_t>(sizeof(uintptr_t))))
        std::memcpy(buf.data() + p->offset, &ptr, sizeof(uintptr_t));
}

int32_t Invoke(uintptr_t instance, const FunctionInfo& fi, std::vector<uint8_t>& buf) {
    if (buf.empty()) buf.resize(1, 0);
    return UE5_CallProcessEventEx(instance, fi.address,
                                  reinterpret_cast<uintptr_t>(buf.data()),
                                  static_cast<uint32_t>(buf.size()));
}

// Generic no-arg FVector getter (GetCameraLocation) via ProcessEvent.
bool InvokeRetVec(uintptr_t instance, const char* fn, double out[3]) {
    if (!instance) return false;
    FunctionInfo fi;
    if (!FindFuncByName(Ubel::GetClass(instance), fn, fi) || fi.parmsSize <= 0) return false;
    const FunctionParam* rv = FindReturnParam(fi);
    if (!rv || rv->offset < 0) return false;
    std::vector<uint8_t> buf(fi.parmsSize, 0);
    if (Invoke(instance, fi, buf) != 0) return false;
    int32_t need = (rv->size >= 24) ? 24 : 12;
    if (rv->offset + need > static_cast<int32_t>(buf.size())) return false;
    ReadVec3Buf(buf.data() + rv->offset, rv->size, out);
    return true;
}

// ---- resolution chain (local pawn / PC / camera; same shape as Dunste) ----

uintptr_t ResolveLocalPC(uintptr_t world) {
    if (!world) return 0;
    uintptr_t worldClass = Ubel::GetClass(world);
    int32_t giOff = Ubel::FindFieldOffset(worldClass, "OwningGameInstance",
                                          "GameInstance", nullptr, "ObjectProperty");
    uintptr_t gi = ReadPtrAt(world, giOff);
    if (!gi) return 0;
    uintptr_t giClass = Ubel::GetClass(gi);
    int32_t lpOff = Ubel::FindFieldOffset(giClass, "LocalPlayers", "LocalPlayers",
                                          nullptr, "ArrayProperty");
    if (lpOff < 0) return 0;
    Macht::TArrayView arr;
    if (!Macht::ReadTArray(gi + static_cast<uintptr_t>(lpOff), arr) || arr.Count <= 0) return 0;
    uintptr_t lp = Macht::ReadTArrayElement(arr, 0);
    if (!lp) return 0;
    uintptr_t lpClass = Ubel::GetClass(lp);
    int32_t pcOff = Ubel::FindFieldOffset(lpClass, "PlayerController",
                                          "PlayerController", nullptr, "ObjectProperty");
    return ReadPtrAt(lp, pcOff);
}

// If the local PC is a DebugCameraController, hop to the original gameplay PC.
uintptr_t HopThroughDebugCamera(uintptr_t pc) {
    if (!pc) return pc;
    uintptr_t cls = Ubel::GetClass(pc);
    if (!Aura::ClassDerivesFromAny(cls, {"DebugCameraController"})) return pc;
    int32_t origOff = Ubel::FindFieldOffset(cls, "OriginalControllerRef",
                                            "OriginalControllerRef", nullptr, "ObjectProperty");
    uintptr_t orig = ReadPtrAt(pc, origOff);
    return orig ? orig : pc;
}

uintptr_t ResolvePawn(uintptr_t pc) {
    if (!pc) return 0;
    uintptr_t cls = Ubel::GetClass(pc);
    uintptr_t pawn = ReadPtrAt(pc, Ubel::FindFieldOffset(cls, "Pawn"));
    if (pawn) return pawn;
    return ReadPtrAt(pc, Ubel::FindFieldOffset(cls, "AcknowledgedPawn"));
}

// Camera world location + forward unit vector via APlayerCameraManager. We trace
// along the VIEW direction (not camera→pawn) so it works in first-person (camera
// sits at the pawn, so camera→pawn is degenerate) AND third-person.
bool ResolveCameraPose(uintptr_t pc, double outLoc[3], double outFwd[3]) {
    int32_t camOff = Ubel::FindFieldOffset(Ubel::GetClass(pc), "PlayerCameraManager",
                                           "PlayerCameraManager", nullptr, "ObjectProperty");
    uintptr_t cam = ReadPtrAt(pc, camOff);
    if (!cam) return false;
    if (!InvokeRetVec(cam, "GetCameraLocation", outLoc)) return false;
    double rot[3] = {};   // FRotator (Pitch, Yaw, Roll) in degrees
    if (!InvokeRetVec(cam, "GetCameraRotation", rot)) return false;
    const double d2r = 3.14159265358979323846 / 180.0;
    double p = rot[0] * d2r, y = rot[1] * d2r;
    outFwd[0] = std::cos(p) * std::cos(y);   // UE forward = X axis of the rotation
    outFwd[1] = std::cos(p) * std::sin(y);
    outFwd[2] = std::sin(p);
    return true;
}

// ---- hit-actor extraction (the fragile, per-game part — LIVE-VERIFY) ----

// Pull the hit AActor* out of a returned FHitResult buffer slice. UE4 stores it
// as `Actor` (TWeakObjectPtr — first int32 is the GObjects ObjectIndex, resolved
// via Aura::GetByIndex). UE5 replaced it with `HitObjectHandle`
// (FActorInstanceHandle) whose first member is a weak ref — best-effort read of
// the leading int32 as an ObjectIndex. Returns 0 when it can't be resolved.
uintptr_t ExtractHitActor(const std::vector<uint8_t>& buf, const FunctionParam& outHit) {
    if (outHit.offset < 0) return 0;
    auto readIndexAt = [&](int32_t sub) -> uintptr_t {
        int32_t at = outHit.offset + sub;
        if (at < 0 || at + 4 > static_cast<int32_t>(buf.size())) return 0;
        int32_t idx = 0;
        std::memcpy(&idx, buf.data() + at, 4);
        if (idx <= 0) return 0;                 // 0/negative = null weak ptr
        return Aura::GetByIndex(idx);
    };
    // UE4: FHitResult.Actor (WeakObjectProperty)
    for (const auto& sf : outHit.structFields)
        if (IEq(sf.name, "Actor")) return readIndexAt(sf.offset);
    // UE5: FHitResult.HitObjectHandle (FActorInstanceHandle) — leading weak ref.
    for (const auto& sf : outHit.structFields)
        if (IEq(sf.name, "HitObjectHandle")) return readIndexAt(sf.offset);
    return 0;
}

// Pull the impact POINT out of a returned FHitResult (ImpactPoint, else Location)
// so the pierce loop can advance the ray start just past this hit.
bool ExtractHitImpactPoint(const std::vector<uint8_t>& buf, const FunctionParam& outHit, double out[3]) {
    const FunctionParam::StructSubField* best = nullptr;
    for (const auto& sf : outHit.structFields) if (IEq(sf.name, "ImpactPoint")) { best = &sf; break; }
    if (!best) for (const auto& sf : outHit.structFields) if (IEq(sf.name, "Location")) { best = &sf; break; }
    if (!best || best->offset < 0 || outHit.offset < 0) return false;
    int32_t need = (best->size >= 24) ? 24 : 12;
    int32_t at = outHit.offset + best->offset;
    if (at + need > static_cast<int32_t>(buf.size())) return false;
    ReadVec3Buf(buf.data() + at, best->size, out);
    return true;
}

// Invoke AActor::SetActorHiddenInGame(hidden) on the game thread. Returns false
// when the setter is cooked out or the actor no longer looks like a live Actor.
bool InvokeSetHidden(uintptr_t actor, bool hidden) {
    if (!actor) return false;
    uintptr_t cls = Ubel::GetClass(actor);
    // Cheap liveness/sanity guard: a freed/garbage actor won't walk to AActor.
    if (!cls || !Aura::ClassDerivesFromAny(cls, {"Actor"})) return false;
    FunctionInfo fi;
    if (!FindFuncByName(cls, "SetActorHiddenInGame", fi)) {
        static bool s_warned = false;   // one-shot: persistent condition, don't spam
        if (!s_warned) { s_warned = true; LOG_WARN("SeeThrough: SetActorHiddenInGame NOT FOUND (cooked out?) — can't hide occluders"); }
        return false;
    }
    std::vector<uint8_t> buf((std::max<size_t>)(static_cast<size_t>(fi.parmsSize), size_t{1}), 0);
    WriteBoolParam(buf, fi, "bNewHidden", hidden);
    Invoke(actor, fi, buf);
    return true;
}

// Bumped by every SetEnabled(true). CollectOccluders keys its resolved-once
// KismetSystemLibrary cache off it, so a re-enable (or a game that reloaded its
// object pool between enables) re-resolves instead of trusting a stale CDO.
std::atomic<int> s_enableGen{0};

// Trace along the view direction and collect the first `pierceN` NON-Pawn
// occluders. Reuses LineTraceSingle iteratively: after each hit we advance the ray
// start just past the impact point (fwd·STEP) and trace again, so we pierce through
// one surface at a time — no LineTraceMulti / TArray<FHitResult> (which would leak
// the engine-allocated OutHits every tick). Pawns/Characters (and the pawn itself)
// are skipped: kept visible, passed through, and do NOT consume the pierce depth.
void CollectOccluders(uintptr_t pawn, const double start0[3], const double fwd[3],
                      const double end[3], int32_t pierceN,
                      std::unordered_set<uintptr_t>& out) {
    out.clear();

    // The KismetSystemLibrary CDO + LineTraceSingle signature are resolved ONCE
    // per enable, not per tick. `UE5_FindInstanceOfClass` is a full GObjects scan
    // (it only stops early on a non-CDO hit, and a function library has none), so
    // the pre-cache version paid one whole-pool scan per trace: measured on The
    // Adventures of Elliot (326K objects) at ~5 scans/second for the whole 29 s
    // the feature was on, plus a class walk each time. A native function-library
    // CDO is rooted for the process lifetime, so caching it is safe; the
    // generation counter re-resolves on the next enable anyway.
    // Only the worker thread reaches here, so the cache itself needs no lock.
    static uintptr_t   s_ksl = 0;
    static FunctionInfo s_lt;
    static int         s_cachedGen = -1;
    int gen = s_enableGen.load(std::memory_order_relaxed);
    if (s_cachedGen != gen) {
        s_ksl = 0;
        s_lt = FunctionInfo{};
        uintptr_t ksl = UE5_FindInstanceOfClass("KismetSystemLibrary");
        if (ksl && FindFuncByName(Ubel::GetClass(ksl), "LineTraceSingle", s_lt)
            && s_lt.parmsSize > 0) {
            s_ksl = ksl;
        } else if (ksl) {
            LOG_WARN("SeeThrough: LineTraceSingle not found (cooked out?) — no occluder detection");
        }
        s_cachedGen = gen;   // cache the FAILURE too: don't rescan 10x/s on a game that lacks it
    }
    if (!s_ksl) return;
    const uintptr_t ksl = s_ksl;
    const FunctionInfo& lt = s_lt;
    const FunctionParam* hr = FindParam(lt, "OutHit");
    if (!hr) return;
    const FunctionParam* rv = FindReturnParam(lt);

    double curStart[3] = { start0[0], start0[1], start0[2] };
    const int32_t maxIters = pierceN + Grimoire::SCHLACHT_MAX_EXTRA_ITERS;
    for (int32_t iter = 0; iter < maxIters && static_cast<int32_t>(out.size()) < pierceN; ++iter) {
        std::vector<uint8_t> buf(lt.parmsSize, 0);
        WritePtrParam(buf, lt, "WorldContextObject", pawn);
        WriteVecParam(buf, lt, "Start", curStart);
        WriteVecParam(buf, lt, "End",   end);
        WriteByteParam(buf, lt, "TraceChannel", static_cast<uint8_t>(Grimoire::SCHLACHT_TRACE_CHANNEL));
        WriteBoolParam(buf, lt, "bTraceComplex", false);
        WriteBoolParam(buf, lt, "bIgnoreSelf",   true);
        int32_t r = Invoke(ksl, lt, buf);
        bool hit = (r == 0) &&
                   (!rv || rv->offset < 0 || rv->offset >= static_cast<int32_t>(buf.size()) || buf[rv->offset] != 0);
        if (!hit) break;

        uintptr_t actor = ExtractHitActor(buf, *hr);
        double impact[3] = {};
        bool haveImpact = ExtractHitImpactPoint(buf, *hr, impact);
        if (!actor) {
            // Couldn't resolve the hit actor — dump the FHitResult layout ONCE so a
            // game where extraction fails (e.g. an unhandled UE5 HitObjectHandle) can
            // be diagnosed without per-tick spam.
            static bool s_dumped = false;
            if (!s_dumped) {
                s_dumped = true;
                LOG_WARN("SeeThrough: HIT but couldn't extract actor from FHitResult — sub-fields:");
                for (const auto& sf : hr->structFields)
                    LOG_INFO("  FHitResult.%s : %s  @+%d sz=%d", sf.name.c_str(), sf.typeName.c_str(), sf.offset, sf.size);
            }
        } else if (actor != pawn &&
                   !Aura::ClassDerivesFromAny(Ubel::GetClass(actor), {"Pawn", "Character"})) {
            out.insert(actor);   // occluder → hide (Pawns / self skipped + pierced through)
        }
        if (!haveImpact) break;   // can't advance the ray → stop here
        for (int i = 0; i < 3; ++i) curStart[i] = impact[i] + fwd[i] * Grimoire::SCHLACHT_TRACE_STEP;
    }
}

// ---- state + worker ----

struct State {
    bool      active      = false;
    std::unordered_set<uintptr_t> hiddenActors;   // occluders currently hidden
    int32_t   code        = 0;
    bool      hasTarget   = false;
    int32_t   hiddenCount = 0;
    int32_t   pierceCount = Grimoire::SCHLACHT_PIERCE_DEFAULT;  // nearest occluders to hide
    int32_t   state       = -1;  // last enable/disable result (1/0/neg); -1 = poll-only
};
State s_state;
std::mutex s_mutex;

std::thread       s_worker;
std::mutex        s_workerMutex;
std::atomic<bool> s_workerStop{false};

void SetCode(int32_t code, bool hasTarget) {
    std::lock_guard<std::mutex> lk(s_mutex);
    s_state.code = code;
    s_state.hasTarget = hasTarget;
    // Transient resolution failures leave the currently-hidden set as-is.
    s_state.hiddenCount = static_cast<int32_t>(s_state.hiddenActors.size());
}

// One resolution+trace+apply cycle. All game-thread invokes run here (the worker
// only calls Tick when active) — never under s_mutex (they block on the game
// thread up to the invoke timeout).
void Tick() {
    // Resolution failures (menu / loading / not-scanned) are normal and frequent —
    // record the code for the UI (GetStatus / hasTarget), no logging.
    if (!Stark::IsGameThreadResponsive()) { SetCode(STR_OK, false); return; }
    uintptr_t world = DerefWorld();
    if (!world) { SetCode(STR_ERR_NOT_INIT, false); return; }
    uintptr_t pc = ResolveLocalPC(world);
    if (!pc)    { SetCode(STR_ERR_NO_PAWN, false); return; }
    pc = HopThroughDebugCamera(pc);
    uintptr_t pawn = ResolvePawn(pc);
    if (!pawn)  { SetCode(STR_ERR_NO_PAWN, false); return; }
    double camLoc[3] = {}, fwd[3] = {};
    if (!ResolveCameraPose(pc, camLoc, fwd)) { SetCode(STR_ERR_NO_CAMERA, false); return; }
    const double D = Grimoire::SCHLACHT_TRACE_DIST;
    double end[3] = { camLoc[0] + fwd[0] * D, camLoc[1] + fwd[1] * D, camLoc[2] + fwd[2] * D };

    int32_t pierceN;
    { std::lock_guard<std::mutex> lk(s_mutex); pierceN = s_state.pierceCount; }

    std::unordered_set<uintptr_t> desired;
    CollectOccluders(pawn, camLoc, fwd, end, pierceN, desired);

    // Diff against the currently-hidden set: un-hide those no longer occluding, hide
    // the newly-occluding ones. Nothing to do (and nothing logged) when unchanged.
    std::unordered_set<uintptr_t> old;
    { std::lock_guard<std::mutex> lk(s_mutex); old = s_state.hiddenActors; }
    for (uintptr_t a : old)     if (!desired.count(a)) InvokeSetHidden(a, false);
    for (uintptr_t a : desired) if (!old.count(a))     InvokeSetHidden(a, true);

    std::lock_guard<std::mutex> lk(s_mutex);
    s_state.hiddenActors = std::move(desired);
    s_state.code = STR_OK;
    s_state.hasTarget = true;
    s_state.hiddenCount = static_cast<int32_t>(s_state.hiddenActors.size());
}

void WorkerLoop() {
    Tot::MarkBackgroundWorker();   // ignore per-command cancel; abort only on shutdown (M4)
    LOG_INFO("SeeThrough: worker started (%d ms tick)", Grimoire::SCHLACHT_TICK_MS);
    while (!s_workerStop.load()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(Grimoire::SCHLACHT_TICK_MS));
        if (s_workerStop.load()) break;
        bool active;
        { std::lock_guard<std::mutex> lk(s_mutex); active = s_state.active; }
        if (!active) continue;
        Tick();
    }
    LOG_INFO("SeeThrough: worker stopped");
}

void StartWorkerLocked() {
    if (Tot::ShutdownRequested()) return;   // don't (re)spawn during the shutdown window (M5)
    if (s_worker.joinable()) return;
    s_workerStop.store(false);
    s_worker = std::thread(WorkerLoop);
}
void StopWorkerLocked() {
    if (!s_worker.joinable()) return;
    s_workerStop.store(true);
    s_worker.join();   // s_mutex must NOT be held here (the worker locks it per tick)
}

} // anonymous namespace

namespace Schlacht {

int32_t SetEnabled(bool enable) {
    // lock order: s_workerMutex (outer) → s_mutex (inner).
    std::lock_guard<std::mutex> wlk(s_workerMutex);
    if (enable) {
        // Recover any actors left hidden by a prior stalled disable. Only pull the
        // leftover OUT of hiddenActors when we can actually un-hide it now (game thread
        // responsive); if it's still unresponsive, LEAVE the set intact so the worker
        // inherits it and its first live tick's diff un-hides anything no longer
        // occluding — moving+clearing it here without un-hiding would orphan those
        // actors (they'd stay SetActorHiddenInGame with nothing tracking them). (M1/M2)
        bool responsive = Stark::IsGameThreadResponsive();
        std::unordered_set<uintptr_t> leftover;
        {
            std::lock_guard<std::mutex> lk(s_mutex);
            if (s_state.active) return 1;   // already on
            if (responsive) {
                leftover = std::move(s_state.hiddenActors);
                s_state.hiddenActors.clear();
            }
        }
        for (uintptr_t a : leftover) InvokeSetHidden(a, false);
        int32_t n;
        {
            std::lock_guard<std::mutex> lk(s_mutex);
            n = s_state.pierceCount;
            s_state.active = true;
            s_state.hiddenCount = static_cast<int32_t>(s_state.hiddenActors.size());
            s_state.code = STR_OK;
            s_state.state = 1;
        }
        // Invalidate CollectOccluders' resolved-once KismetSystemLibrary cache
        // before the worker's first tick reads it.
        s_enableGen.fetch_add(1, std::memory_order_relaxed);
        StartWorkerLocked();   // s_workerMutex held, s_mutex released
        LOG_INFO("SeeThrough: enabled (pierce=%d)", n);
        return 1;
    }
    // Disable. Early-out when there is genuinely nothing to undo (not running, nothing
    // hidden) so blind disconnect/shutdown callers don't spam or probe. (M3)
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        if (!s_state.active && s_state.hiddenActors.empty()) return 0;
        s_state.active = false;
        s_state.state = 0;
        s_state.hasTarget = false;
    }
    // Quiesce the worker BEFORE snapshotting the hidden set — otherwise an in-flight
    // Tick could repopulate hiddenActors AFTER we restore it, orphaning those actors
    // on the next enable. Join first, THEN restore. (M1)
    StopWorkerLocked();        // join with s_mutex released
    if (Stark::IsGameThreadResponsive()) {
        std::unordered_set<uintptr_t> restore;
        {
            std::lock_guard<std::mutex> lk(s_mutex);
            restore = std::move(s_state.hiddenActors);
            s_state.hiddenActors.clear();
            s_state.hiddenCount = 0;
        }
        for (uintptr_t a : restore) InvokeSetHidden(a, false);
        LOG_INFO("SeeThrough: disabled (%zu restored)", restore.size());
    } else {
        // Game thread unresponsive (paused / backgrounded) → we can't un-hide now.
        // KEEP the record so the next enable (when responsive) restores them, rather
        // than discarding it and leaving the actors hidden with nothing tracking. (M2)
        size_t kept;
        {
            std::lock_guard<std::mutex> lk(s_mutex);
            kept = s_state.hiddenActors.size();
            s_state.hiddenCount = static_cast<int32_t>(kept);
        }
        LOG_WARN("SeeThrough: disabled but %zu actor(s) remain hidden (game thread unresponsive); "
                 "re-enable after the game resumes to restore them", kept);
    }
    return 0;
}

void SetPierceCount(int32_t count) {
    if (count < 1) count = 1;
    if (count > Grimoire::SCHLACHT_PIERCE_MAX) count = Grimoire::SCHLACHT_PIERCE_MAX;
    std::lock_guard<std::mutex> lk(s_mutex);
    s_state.pierceCount = count;
}

int32_t GetStatus(SeeThroughStatus& out) {
    std::lock_guard<std::mutex> lk(s_mutex);
    out.code        = s_state.code;
    out.active      = s_state.active;
    out.hasTarget   = s_state.hasTarget;
    out.hiddenCount = s_state.hiddenCount;
    out.pierceCount = s_state.pierceCount;
    out.state       = s_state.state;
    return s_state.code;
}

bool IsActive() {
    std::lock_guard<std::mutex> lk(s_mutex);
    return s_state.active;
}

void StopWorker() {
    std::lock_guard<std::mutex> lk(s_workerMutex);
    StopWorkerLocked();
}

} // namespace Schlacht
