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

// Invoke AActor::SetActorHiddenInGame(hidden) on the game thread. Returns false
// when the setter is cooked out or the actor no longer looks like a live Actor.
bool InvokeSetHidden(uintptr_t actor, bool hidden) {
    if (!actor) return false;
    uintptr_t cls = Ubel::GetClass(actor);
    // Cheap liveness/sanity guard: a freed/garbage actor won't walk to AActor.
    if (!cls || !Aura::ClassDerivesFromAny(cls, {"Actor"})) return false;
    FunctionInfo fi;
    if (!FindFuncByName(cls, "SetActorHiddenInGame", fi)) {
        LOG_WARN("SeeThrough: SetActorHiddenInGame NOT FOUND (cooked out?) — can't hide occluders");
        return false;
    }
    std::vector<uint8_t> buf((std::max<size_t>)(static_cast<size_t>(fi.parmsSize), size_t{1}), 0);
    WriteBoolParam(buf, fi, "bNewHidden", hidden);
    Invoke(actor, fi, buf);
    return true;
}

// Trace along the view direction (start → end); return the NEAREST blocking actor
// that should be hidden (0 = none / clear / the hit is the pawn itself / a
// Pawn/Character we keep). `logThis` gates verbose per-tick diagnostics.
uintptr_t TraceNearestOccluder(uintptr_t pawn, const double start[3], const double end[3], bool logThis) {
    uintptr_t ksl = UE5_FindInstanceOfClass("KismetSystemLibrary");
    if (!ksl) { if (logThis) LOG_WARN("SeeThrough: KismetSystemLibrary instance/CDO not found"); return 0; }
    FunctionInfo lt;
    if (!FindFuncByName(Ubel::GetClass(ksl), "LineTraceSingle", lt) || lt.parmsSize <= 0) {
        if (logThis) LOG_WARN("SeeThrough: LineTraceSingle not found (cooked out?) — no occluder detection");
        return 0;
    }
    std::vector<uint8_t> buf(lt.parmsSize, 0);
    WritePtrParam(buf, lt, "WorldContextObject", pawn);
    WriteVecParam(buf, lt, "Start", start);
    WriteVecParam(buf, lt, "End",   end);
    WriteByteParam(buf, lt, "TraceChannel", static_cast<uint8_t>(Grimoire::SCHLACHT_TRACE_CHANNEL));
    WriteBoolParam(buf, lt, "bTraceComplex", false);
    WriteBoolParam(buf, lt, "bIgnoreSelf",   true);
    // ActorsToIgnore (TArray), DrawDebugType, colours, DrawTime stay zeroed.
    const FunctionParam* hr = FindParam(lt, "OutHit");
    if (!hr) { if (logThis) LOG_WARN("SeeThrough: LineTraceSingle has no OutHit param (layout?)"); return 0; }
    const FunctionParam* rv = FindReturnParam(lt);
    int32_t r = Invoke(ksl, lt, buf);
    bool hit = (r == 0) &&
               (!rv || rv->offset < 0 || rv->offset >= static_cast<int32_t>(buf.size()) || buf[rv->offset] != 0);
    if (!hit) { if (logThis) LOG_INFO("SeeThrough: trace ran (r=%d) — no blocking hit on channel %d",
                                      r, (int)Grimoire::SCHLACHT_TRACE_CHANNEL); return 0; }
    uintptr_t actor = ExtractHitActor(buf, *hr);
    if (!actor) {
        // Couldn't resolve the hit actor — dump the FHitResult sub-field layout so we
        // can fix ExtractHitActor for this engine (UE5 HitObjectHandle etc.).
        if (logThis) {
            LOG_WARN("SeeThrough: HIT but couldn't extract actor from FHitResult — sub-fields:");
            for (const auto& sf : hr->structFields)
                LOG_INFO("  FHitResult.%s : %s  @+%d sz=%d", sf.name.c_str(), sf.typeName.c_str(), sf.offset, sf.size);
        }
        return 0;
    }
    std::string nm = Ubel::GetName(actor);
    std::string cl = Ubel::GetName(Ubel::GetClass(actor));
    bool isPawn = Aura::ClassDerivesFromAny(Ubel::GetClass(actor), {"Pawn", "Character"});
    if (logThis) LOG_INFO("SeeThrough: hit actor=0x%llX '%s' class=%s pawnOrChar=%d self=%d",
                          (unsigned long long)actor, nm.c_str(), cl.c_str(), isPawn ? 1 : 0, actor == pawn ? 1 : 0);
    if (actor == pawn) return 0;
    if (isPawn) return 0;   // keep Pawns / Characters (enemies / NPCs / the player) visible
    return actor;
}

// ---- state + worker ----

struct State {
    bool      active      = false;
    uintptr_t hiddenActor = 0;   // the single occluder currently hidden (0 = none)
    int32_t   code        = 0;
    bool      hasTarget   = false;
    int32_t   hiddenCount = 0;
    int32_t   state       = -1;  // last enable/disable result (1/0/neg); -1 = poll-only
    uint64_t  tick        = 0;
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
    if (!hasTarget) s_state.hiddenCount = (s_state.hiddenActor != 0) ? 1 : 0;
}

// One resolution+trace+apply cycle. All game-thread invokes run here (the worker
// only calls Tick when active) — never under s_mutex (they block on the game
// thread up to the invoke timeout).
void Tick() {
    uint64_t t;
    { std::lock_guard<std::mutex> lk(s_mutex); t = ++s_state.tick; }
    const bool logThis = (t % Grimoire::SCHLACHT_LOG_EVERY) == 0;   // ~1/sec heartbeat

    if (!Stark::IsGameThreadResponsive()) { if (logThis) LOG_INFO("SeeThrough: game thread not responsive — skip"); SetCode(STR_OK, false); return; }
    uintptr_t world = DerefWorld();
    if (!world) { if (logThis) LOG_WARN("SeeThrough: GWorld=0 (Connect & scan first)"); SetCode(STR_ERR_NOT_INIT, false); return; }
    uintptr_t pc = ResolveLocalPC(world);
    if (!pc)    { if (logThis) LOG_WARN("SeeThrough: no local PlayerController"); SetCode(STR_ERR_NO_PAWN, false); return; }
    pc = HopThroughDebugCamera(pc);
    uintptr_t pawn = ResolvePawn(pc);
    if (!pawn)  { if (logThis) LOG_WARN("SeeThrough: no pawn (menu / loading / cutscene?)"); SetCode(STR_ERR_NO_PAWN, false); return; }
    double camLoc[3] = {}, fwd[3] = {};
    if (!ResolveCameraPose(pc, camLoc, fwd)) { if (logThis) LOG_WARN("SeeThrough: no camera pose (PlayerCameraManager / GetCameraLocation / GetCameraRotation)"); SetCode(STR_ERR_NO_CAMERA, false); return; }
    const double D = Grimoire::SCHLACHT_TRACE_DIST;
    double end[3] = { camLoc[0] + fwd[0] * D, camLoc[1] + fwd[1] * D, camLoc[2] + fwd[2] * D };
    if (logThis) LOG_INFO("SeeThrough: pawn=0x%llX cam=(%.0f,%.0f,%.0f) fwd=(%.2f,%.2f,%.2f)",
                          (unsigned long long)pawn, camLoc[0], camLoc[1], camLoc[2], fwd[0], fwd[1], fwd[2]);

    uintptr_t occ = TraceNearestOccluder(pawn, camLoc, end, logThis);

    uintptr_t old = 0;
    { std::lock_guard<std::mutex> lk(s_mutex); old = s_state.hiddenActor; }
    if (occ != old) {
        LOG_INFO("SeeThrough: occluder change old=0x%llX -> new=0x%llX", (unsigned long long)old, (unsigned long long)occ);
        if (old) { bool ok = InvokeSetHidden(old, false); LOG_INFO("SeeThrough: unhide 0x%llX ok=%d", (unsigned long long)old, ok ? 1 : 0); }
        if (occ) { bool ok = InvokeSetHidden(occ, true);  LOG_INFO("SeeThrough: HIDE   0x%llX ok=%d", (unsigned long long)occ, ok ? 1 : 0); }
        std::lock_guard<std::mutex> lk(s_mutex);
        s_state.hiddenActor = occ;
    }
    std::lock_guard<std::mutex> lk(s_mutex);
    s_state.code = STR_OK;
    s_state.hasTarget = true;
    s_state.hiddenCount = occ ? 1 : 0;
}

void WorkerLoop() {
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
        {
            std::lock_guard<std::mutex> lk(s_mutex);
            if (s_state.active) return 1;   // already on
            s_state.active = true;
            s_state.hiddenActor = 0;
            s_state.hiddenCount = 0;
            s_state.code = STR_OK;
            s_state.state = 1;
        }
        StartWorkerLocked();   // s_workerMutex held, s_mutex released
        LOG_INFO("SeeThrough: enabled");
        return 1;
    }
    // Disable: un-hide whatever we hid (game thread, off-lock), then stop.
    uintptr_t restore = 0;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        restore = s_state.hiddenActor;
        s_state.active = false;
        s_state.hiddenActor = 0;
        s_state.hiddenCount = 0;
        s_state.state = 0;
        s_state.hasTarget = false;
    }
    if (restore && Stark::IsGameThreadResponsive())
        InvokeSetHidden(restore, false);
    StopWorkerLocked();        // join with s_mutex released
    LOG_INFO("SeeThrough: disabled");
    return 0;
}

int32_t GetStatus(SeeThroughStatus& out) {
    std::lock_guard<std::mutex> lk(s_mutex);
    out.code        = s_state.code;
    out.active      = s_state.active;
    out.hasTarget   = s_state.hasTarget;
    out.hiddenCount = s_state.hiddenCount;
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
