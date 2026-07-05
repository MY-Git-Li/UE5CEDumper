// ============================================================
// Dunste — ドゥンスト / Dünste (「蒸氣」 — vapors)
// Fly: no-gravity free 3D flight of the local pawn, keyboard-driven. Contract:
// Dunste.h.
//
// Engine-flying (collision preserved): force the CMC into MOVE_Flying via a raw
// enum-byte write held by a re-assert worker (write-on-drift, like Laufen), then
// drive the CMC Velocity FVector each ~60 Hz tick from the keyboard, projected
// onto the pawn's facing. Pure reflected Macht (SEH) writes — NO invoke, NO game
// thread. Self-contained (Path B): only public Ubel/Aura/Macht + DynOff.
// ============================================================

#define LOG_CAT "FLY"
#include "Sein.h"
#include "Dunste.h"
#include "Grimoire.h"
#include "Macht.h"
#include "Aura.h"
#include "Ubel.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <mutex>
#include <thread>

#include <windows.h>   // GetAsyncKeyState / GetForegroundWindow (foreground gate)
                       // (WIN32_LEAN_AND_MEAN / NOMINMAX come from the CMake defs)

// &GWorld — deref once for UWorld* (defined in Frieren.cpp; same as Laufen).
extern uintptr_t g_cachedGWorld;

namespace {

using namespace Dunste;  // FlyResult / Preset codes

// ---- desired state (guarded by s_mutex) ----
struct FlyState {
    bool      active       = false;
    double    speed        = Grimoire::FLY_SPEED_DEFAULT;
    int32_t   preset       = PRESET_WASD;
    uint8_t   baseMode     = 1;      // captured MovementMode to restore (default MOVE_Walking)
    bool      baseCaptured = false;
    uintptr_t capturedPawn = 0;      // pawn the base mode was captured from (respawn re-capture)
};
FlyState s_state;

// Serializes whole operations: reachable from the Fern pipe thread, the Mimic
// mailbox thread, AND the fly worker.
std::mutex s_mutex;

// Fly worker — separate control mutex so StopWorker()'s join() never runs while
// s_mutex is held (the worker locks s_mutex per tick → would deadlock).
std::thread       s_worker;
std::mutex        s_workerMutex;
std::atomic<bool> s_workerStop{false};

// ---- key preset table: [preset][logical action] = Win32 virtual-key ----
enum Act { A_FWD, A_BACK, A_LEFT, A_RIGHT, A_YAW_L, A_YAW_R, A_UP, A_DOWN, A_COUNT };
const uint8_t kPresetKeys[PRESET_COUNT][A_COUNT] = {
    // FWD    BACK   LEFT   RIGHT  YAW_L  YAW_R  UP     DOWN
    { 'W',   'S',   'A',   'D',   'Q',   'E',   'Z',   'C'    },              // PRESET_WASD
    { VK_NUMPAD8, VK_NUMPAD2, VK_NUMPAD4, VK_NUMPAD6,
      VK_NUMPAD7, VK_NUMPAD9, VK_NUMPAD1, VK_NUMPAD3 },                       // PRESET_NUMPAD
    { VK_UP, VK_DOWN, VK_LEFT, VK_RIGHT,
      VK_INSERT, VK_DELETE, VK_PRIOR, VK_NEXT },                             // PRESET_ARROWS (PgUp/PgDn)
};

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

// Read/write a 3-component FVector/FRotator honoring the reflected struct width
// (12B = 3×float / 24B = 3×double under LWC).
bool ReadVec3At(uintptr_t addr, int32_t structSize, double out[3]) {
    int cw = (structSize >= 24) ? 8 : 4;
    for (int i = 0; i < 3; ++i) {
        if (cw == 8) {
            double d = 0;
            if (!Macht::ReadSafe(addr + static_cast<uintptr_t>(i * 8), d)) return false;
            out[i] = d;
        } else {
            float f = 0;
            if (!Macht::ReadSafe(addr + static_cast<uintptr_t>(i * 4), f)) return false;
            out[i] = static_cast<double>(f);
        }
    }
    return true;
}

bool WriteVec3At(uintptr_t addr, int32_t structSize, const double v[3]) {
    int cw = (structSize >= 24) ? 8 : 4;
    for (int i = 0; i < 3; ++i) {
        if (cw == 8) {
            double d = v[i];
            if (!Macht::WriteBytes(addr + static_cast<uintptr_t>(i * 8), &d, 8)) return false;
        } else {
            float f = static_cast<float>(v[i]);
            if (!Macht::WriteBytes(addr + static_cast<uintptr_t>(i * 4), &f, 4)) return false;
        }
    }
    return true;
}

// ---- resolution chain (local pawn → CMC → PC; same shape as Laufen) ----
// allowScan gates the expensive FindInstancesByClass fallback: the one-shot
// enable path passes true; the ~60 Hz worker passes false (the pointer walk is
// cheap and the fallback would be a per-tick full-pool scan).

uintptr_t ResolveLocalPC(uintptr_t world, bool allowScan) {
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

    if (!allowScan) return 0;
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

// Resolved local pawn + Controller + CharacterMovement + the three field
// locations the fly tick writes. Re-resolved every tick (no cached pointers).
struct Ctx {
    uintptr_t world = 0, pc = 0, pawn = 0, cmc = 0, cmcClass = 0;
    int32_t   modeOff = -1;                 // CMC.MovementMode (1 byte)
    uintptr_t velAddr = 0;  int32_t velSize = 12;   // CMC.Velocity FVector
    uintptr_t rotAddr = 0;  int32_t rotSize = 12;   // Controller.ControlRotation FRotator
};

int32_t ResolveCtx(Ctx& c, bool allowScan) {
    c = Ctx{};
    c.world = DerefWorld();
    if (!c.world) return FR_ERR_NOT_INIT;
    c.pc = ResolveLocalPC(c.world, allowScan);
    if (!c.pc) return FR_ERR_NO_PAWN;
    c.pc = HopThroughDebugCamera(c.pc);
    c.pawn = ResolvePawn(c.pc);
    if (!c.pawn) return FR_ERR_NO_PAWN;
    uintptr_t pawnClass = Ubel::GetClass(c.pawn);
    if (!pawnClass) return FR_ERR_REFLECT;
    int32_t cmOff = Ubel::FindFieldOffset(pawnClass, "CharacterMovement",
                                          "CharacterMovement", nullptr, "ObjectProperty");
    c.cmc = ReadPtrAt(c.pawn, cmOff);
    if (!c.cmc) return FR_ERR_NO_CMC;
    c.cmcClass = Ubel::GetClass(c.cmc);
    if (!c.cmcClass) return FR_ERR_REFLECT;

    // MovementMode: TEnumAsByte<EMovementMode> (FByteProperty). Match by name
    // (any type) — the enum byte lives at the property offset.
    c.modeOff = Ubel::FindFieldOffset(c.cmcClass, "MovementMode",
                                      "MovementMode", "Custom", nullptr);
    // Velocity FVector on UMovementComponent (base of the CMC).
    FieldInfo fi{};
    if (Ubel::FindField(c.cmcClass, "Velocity", "Velocity", nullptr, "StructProperty", fi)
        && fi.Offset >= 0) {
        c.velAddr = c.cmc + static_cast<uintptr_t>(fi.Offset);
        c.velSize = fi.Size;
    }
    // ControlRotation FRotator on the Controller (type filter nullptr — mirrors
    // Wirbel, which resolves it without a type constraint).
    FieldInfo rf{};
    if (Ubel::FindField(Ubel::GetClass(c.pc), "ControlRotation", nullptr, nullptr, nullptr, rf)
        && rf.Offset >= 0) {
        c.rotAddr = c.pc + static_cast<uintptr_t>(rf.Offset);
        c.rotSize = rf.Size;
    }
    if (c.modeOff < 0 || !c.velAddr) return FR_ERR_REFLECT;
    return FR_OK;
}

// True when the GAME window (any top-level window owned by this process) is
// foreground — so fly keys never fire while the user is tabbed to the dumper UI
// or another app.
bool GameIsForeground() {
    HWND fg = GetForegroundWindow();
    if (!fg) return false;
    DWORD pid = 0;
    GetWindowThreadProcessId(fg, &pid);
    return pid == GetCurrentProcessId();
}

bool KeyDown(uint8_t vk) {
    return (GetAsyncKeyState(vk) & 0x8000) != 0;
}

// Pitch-inclusive forward + yaw-only right basis from an FRotator (Pitch, Yaw,
// Roll in degrees). UE convention: forward = (cosP·cosY, cosP·sinY, sinP);
// right = (-sinY, cosY, 0).
void FacingBasis(const double pyr[3], double fwd[3], double right[3]) {
    constexpr double kDeg2Rad = 3.14159265358979323846 / 180.0;
    double pitch = pyr[0] * kDeg2Rad, yaw = pyr[1] * kDeg2Rad;
    double cp = std::cos(pitch), sp = std::sin(pitch);
    double cy = std::cos(yaw),   sy = std::sin(yaw);
    fwd[0] = cp * cy;  fwd[1] = cp * sy;  fwd[2] = sp;
    right[0] = -sy;    right[1] = cy;     right[2] = 0.0;
}

// ---- one fly tick (caller holds s_mutex; c already resolved MR_OK) ----
void FlyTickLocked(const Ctx& c, double dtSec) {
    // Re-capture base MovementMode on pawn change (respawn): the fresh pawn's
    // mode is the genuine one to restore later.
    uint8_t liveMode = 0;
    bool haveMode = (c.modeOff >= 0) &&
                    Macht::ReadSafe(c.cmc + static_cast<uintptr_t>(c.modeOff), liveMode);
    if (haveMode && c.pawn != s_state.capturedPawn) {
        s_state.baseMode = (liveMode == Grimoire::MOVE_FLYING) ? 1 /*Walking*/ : liveMode;
        s_state.baseCaptured = true;
        s_state.capturedPawn = c.pawn;
    }
    // Hold MOVE_Flying against the game re-asserting its own mode.
    if (haveMode && liveMode != Grimoire::MOVE_FLYING) {
        uint8_t fly = Grimoire::MOVE_FLYING;
        Macht::WriteBytes(c.cmc + static_cast<uintptr_t>(c.modeOff), &fly, 1);
    }

    // Read facing from ControlRotation (fall back to world axes if unresolved).
    double pyr[3] = {0, 0, 0};
    bool haveRot = c.rotAddr && ReadVec3At(c.rotAddr, c.rotSize, pyr);
    double fwd[3], right[3];
    FacingBasis(pyr, fwd, right);

    // Sample keys only while the game is foreground; otherwise hover (zero vel).
    double move[3] = {0, 0, 0};
    double turn = 0.0;
    if (GameIsForeground()) {
        const uint8_t* k = kPresetKeys[s_state.preset];
        auto add = [&](const double v[3], double s) {
            move[0] += v[0] * s; move[1] += v[1] * s; move[2] += v[2] * s;
        };
        if (KeyDown(k[A_FWD]))   add(fwd, 1.0);
        if (KeyDown(k[A_BACK]))  add(fwd, -1.0);
        if (KeyDown(k[A_RIGHT])) add(right, 1.0);
        if (KeyDown(k[A_LEFT]))  add(right, -1.0);
        if (KeyDown(k[A_UP]))    move[2] += 1.0;   // world up
        if (KeyDown(k[A_DOWN]))  move[2] -= 1.0;
        if (KeyDown(k[A_YAW_L])) turn -= Grimoire::FLY_TURN_DEG_PER_S * dtSec;
        if (KeyDown(k[A_YAW_R])) turn += Grimoire::FLY_TURN_DEG_PER_S * dtSec;
    }

    // Normalize the combined direction so diagonals aren't faster, then scale to
    // the flight speed. No keys → zero velocity (hover in place, no gravity).
    double len = std::sqrt(move[0]*move[0] + move[1]*move[1] + move[2]*move[2]);
    double vel[3] = {0, 0, 0};
    if (len > 1e-6) {
        double s = s_state.speed / len;
        vel[0] = move[0] * s; vel[1] = move[1] * s; vel[2] = move[2] * s;
    }
    WriteVec3At(c.velAddr, c.velSize, vel);

    // Apply yaw turn to ControlRotation (Q/E). Written back so the camera + our
    // facing basis follow next tick.
    if (haveRot && std::fabs(turn) > 1e-9) {
        pyr[1] += turn;
        WriteVec3At(c.rotAddr, c.rotSize, pyr);
    }
}

// ---- fly worker ----
void WorkerLoop() {
    LOG_INFO("Fly: worker started (%d ms tick)", Grimoire::FLY_TICK_MS);
    const double dtSec = Grimoire::FLY_TICK_MS / 1000.0;
    while (!s_workerStop.load()) {
        std::this_thread::sleep_for(std::chrono::milliseconds(Grimoire::FLY_TICK_MS));
        if (s_workerStop.load()) break;
        std::lock_guard<std::mutex> lk(s_mutex);
        if (!s_state.active) continue;             // disabled between ticks
        Ctx c;
        if (ResolveCtx(c, false) != FR_OK) continue;  // pawn/CMC gone (menu) — retry
        FlyTickLocked(c, dtSec);
    }
    LOG_INFO("Fly: worker stopped");
}

// Worker start/stop split (same discipline as Laufen): the join in
// StopWorkerLocked runs with s_mutex RELEASED (WorkerLoop takes s_mutex every
// tick → joining under it deadlocks); s_workerMutex serializes start vs stop.
void StartWorkerLocked() {
    if (s_worker.joinable()) return;
    s_workerStop.store(false);
    s_worker = std::thread(WorkerLoop);
}

void StopWorkerLocked() {
    if (!s_worker.joinable()) return;
    s_workerStop.store(true);
    s_worker.join();
}

} // namespace

namespace Dunste {

int32_t SetEnabled(bool enable) {
    // Hold s_workerMutex across the whole mutate + start/stop decision (Laufen
    // audit #8): lock order s_workerMutex (outer) → s_mutex (inner).
    std::lock_guard<std::mutex> wlk(s_workerMutex);
    if (enable) {
        int32_t rc;
        {
            std::lock_guard<std::mutex> lk(s_mutex);
            Ctx c;
            rc = ResolveCtx(c, true);   // one-shot: allow the scan fallback
            if (rc != FR_OK) return rc;
            uint8_t liveMode = 0;
            if (c.modeOff >= 0 &&
                Macht::ReadSafe(c.cmc + static_cast<uintptr_t>(c.modeOff), liveMode)) {
                s_state.baseMode = (liveMode == Grimoire::MOVE_FLYING) ? 1 : liveMode;
                s_state.baseCaptured = true;
            }
            s_state.capturedPawn = c.pawn;
            s_state.active = true;
            uint8_t fly = Grimoire::MOVE_FLYING;
            if (c.modeOff >= 0)
                Macht::WriteBytes(c.cmc + static_cast<uintptr_t>(c.modeOff), &fly, 1);
            LOG_INFO("Fly: ENABLED (baseMode=%u, speed=%.0f, preset=%d)",
                     s_state.baseMode, s_state.speed, s_state.preset);
        }
        StartWorkerLocked();   // s_workerMutex held, s_mutex released
        return 1;
    }

    // Disable: restore the captured MovementMode + zero velocity, stop worker.
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        if (s_state.active) {
            Ctx c;
            if (ResolveCtx(c, false) == FR_OK) {
                if (c.modeOff >= 0 && s_state.baseCaptured) {
                    uint8_t base = s_state.baseMode;
                    Macht::WriteBytes(c.cmc + static_cast<uintptr_t>(c.modeOff), &base, 1);
                }
                double zero[3] = {0, 0, 0};
                if (c.velAddr) WriteVec3At(c.velAddr, c.velSize, zero);
            }
        }
        s_state.active = false;
        s_state.baseCaptured = false;
        s_state.capturedPawn = 0;
        LOG_INFO("Fly: DISABLED");
    }
    StopWorkerLocked();   // join with s_mutex released
    return 0;
}

int32_t SetSpeed(double uuPerSec) {
    std::lock_guard<std::mutex> lk(s_mutex);
    s_state.speed = (std::max)(Grimoire::FLY_SPEED_MIN,
                               (std::min)(Grimoire::FLY_SPEED_MAX, uuPerSec));
    return FR_OK;
}

int32_t SetPreset(int32_t preset) {
    if (preset < 0 || preset >= PRESET_COUNT) return FR_ERR_REFLECT;
    std::lock_guard<std::mutex> lk(s_mutex);
    s_state.preset = preset;
    return FR_OK;
}

int32_t GetStatus(FlyStatus& out) {
    out = FlyStatus{};
    std::lock_guard<std::mutex> lk(s_mutex);
    out.active = s_state.active;
    out.preset = s_state.preset;
    out.speed  = s_state.speed;
    Ctx c;
    int32_t rc = ResolveCtx(c, false);
    out.code = rc;
    if (rc != FR_OK) return rc;
    out.hasCmc  = true;
    out.cmcAddr = c.cmc;
    uint8_t liveMode = 0;
    if (c.modeOff >= 0 &&
        Macht::ReadSafe(c.cmc + static_cast<uintptr_t>(c.modeOff), liveMode)) {
        out.modeResolved = true;
        out.currentMode  = liveMode;
    }
    return FR_OK;
}

bool IsActive() {
    std::lock_guard<std::mutex> lk(s_mutex);
    return s_state.active;
}

void StopWorker() {
    std::lock_guard<std::mutex> lk(s_workerMutex);
    StopWorkerLocked();
}

} // namespace Dunste
