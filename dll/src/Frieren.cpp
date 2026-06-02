// ============================================================
// Frieren — 芙莉蓮 (主角 — Protagonist)
// ExportAPI: ~30 C ABI exports for CE Lua bridge
// ============================================================

#include "Frieren.h"
#define LOG_CAT "INIT"
#include "Sein.h"
#include "BuildInfo.h"
#include "Grimoire.h"
#include "Macht.h"
#include "Genau.h"
#include "Aura.h"
#include "Serie.h"
#include "Ubel.h"
#include "Fern.h"
#include "Stark.h"
#include "Mimic.h"

#include <string>
#include <cstring>
#include <mutex>
#include <algorithm>
#include <thread>
#include <chrono>

// Global cached state (also accessed by PipeServer)
uintptr_t   g_cachedGObjects        = 0;
uintptr_t   g_cachedGNames          = 0;
uintptr_t   g_cachedGWorld          = 0;
uintptr_t   g_cachedSparseDelegates = 0;  // FSparseDelegateStorage::SparseDelegates (UE 5.0+, optional)
uint32_t    g_cachedUEVersion = 0;
bool        g_cachedVersionDetected = true;  // false if UE version detection failed (PE + memory scan)
bool        g_cachedIsUserOverride  = false; // true = ueVersion came from a user-set persistent override
bool        g_cachedIsLowConfidence = false; // true = Tier 3 bare-pattern OR publisher-bias fallback
const char* g_cachedPublisherThumbprint = nullptr;  // e.g. "SQUARE_ENIX" (nullptr if no match)
const char* g_cachedGObjectsMethod        = "not_found";  // "aob", "data_scan", "not_found"
const char* g_cachedGNamesMethod          = "not_found";  // "aob", "string_ref", "pointer_scan", "not_found"
const char* g_cachedGWorldMethod          = "not_found";  // "aob", "not_found"
const char* g_cachedSparseDelegatesMethod = "not_found";  // "aob", "not_found"

// AOB Usage Tracking: PE hash, winning pattern IDs, scan statistics
char        g_cachedPeHash[17] = {0};
const char* g_cachedGObjectsPatternId        = nullptr;
const char* g_cachedGNamesPatternId          = nullptr;
const char* g_cachedGWorldPatternId          = nullptr;
const char* g_cachedSparseDelegatesPatternId = nullptr;
int         g_cachedGObjectsTried = 0, g_cachedGObjectsHit = 0;
int         g_cachedGNamesTried   = 0, g_cachedGNamesHit   = 0;
int         g_cachedGWorldTried   = 0, g_cachedGWorldHit   = 0;
uintptr_t   g_cachedGObjectsScanAddr        = 0;
uintptr_t   g_cachedGNamesScanAddr          = 0;
uintptr_t   g_cachedGWorldScanAddr          = 0;
uintptr_t   g_cachedSparseDelegatesScanAddr = 0;
const char* g_cachedGWorldAob    = nullptr;
int         g_cachedGWorldAobPos = 0;
int         g_cachedGWorldAobLen = 0;

static bool        s_initialized = false;
static Fern  s_pipeServer;
static std::mutex  s_walkMutex;
static ClassInfo   s_walkCache;

// Global scan progress for UI polling (updated by UE5_Init, read by scan_status)
namespace ScanProgress {
    std::atomic<int>  phase{0};
    std::string       statusText;
    std::mutex        statusMutex;

    void Set(int p, const char* text) {
        phase.store(p, std::memory_order_release);
        std::lock_guard<std::mutex> lock(statusMutex);
        statusText = text;
    }
    std::string GetStatusText() {
        std::lock_guard<std::mutex> lock(statusMutex);
        return statusText;
    }
}

// Helper: copy string to buffer safely
static bool CopyToBuffer(const std::string& src, char* buf, int32_t bufLen) {
    if (!buf || bufLen <= 0) return false;
    size_t copyLen = (std::min)(src.size(), static_cast<size_t>(bufLen - 1));
    memcpy(buf, src.c_str(), copyLen);
    buf[copyLen] = '\0';
    return true;
}

extern "C" {

bool UE5_Init() {
    if (s_initialized) {
        LOG_WARN("UE5_Init: Already initialized");
        return true;
    }

    LOG_INFO("UE5_Init: Starting initialization...");

    Genau::EnginePointers ptrs;
    Genau::FindAll(ptrs, [](int phase, const char* text) {
        ScanProgress::Set(phase, text);
    });

    g_cachedGObjects        = ptrs.GObjects;
    g_cachedGNames          = ptrs.GNames;
    g_cachedGWorld          = ptrs.GWorld;
    g_cachedSparseDelegates = ptrs.SparseDelegates;
    g_cachedUEVersion = ptrs.UEVersion;
    g_cachedVersionDetected = ptrs.bVersionDetected;
    g_cachedIsUserOverride  = ptrs.bUserOverride;
    g_cachedIsLowConfidence = ptrs.bLowConfidence;
    g_cachedPublisherThumbprint = ptrs.publisherThumbprint;
    g_cachedGObjectsMethod        = ptrs.gobjectsMethod;
    g_cachedGNamesMethod          = ptrs.gnamesMethod;
    g_cachedGWorldMethod          = ptrs.gworldMethod;
    g_cachedSparseDelegatesMethod = ptrs.sparseDelegatesMethod;

    // AOB Usage Tracking
    memcpy(g_cachedPeHash, ptrs.peHash, sizeof(g_cachedPeHash));
    g_cachedGObjectsPatternId        = ptrs.gobjectsPatternId;
    g_cachedGNamesPatternId          = ptrs.gnamesPatternId;
    g_cachedGWorldPatternId          = ptrs.gworldPatternId;
    g_cachedSparseDelegatesPatternId = ptrs.sparseDelegatesPatternId;
    g_cachedGObjectsTried = ptrs.gobjectsPatternsTried;
    g_cachedGObjectsHit   = ptrs.gobjectsPatternsHit;
    g_cachedGNamesTried   = ptrs.gnamesPatternsTried;
    g_cachedGNamesHit     = ptrs.gnamesPatternsHit;
    g_cachedGWorldTried   = ptrs.gworldPatternsTried;
    g_cachedGWorldHit     = ptrs.gworldPatternsHit;
    g_cachedGObjectsScanAddr        = ptrs.gobjectsScanAddr;
    g_cachedGNamesScanAddr          = ptrs.gnamesScanAddr;
    g_cachedGWorldScanAddr          = ptrs.gworldScanAddr;
    g_cachedSparseDelegatesScanAddr = ptrs.sparseDelegatesScanAddr;
    g_cachedGWorldAob    = ptrs.gworldAob;
    g_cachedGWorldAobPos = ptrs.gworldAobPos;
    g_cachedGWorldAobLen = ptrs.gworldAobLen;

    // Initialize subsystems — only when their pointer was found
    ScanProgress::Set(5, "Initializing subsystems...");
    if (ptrs.GNames) {
        if (ptrs.bUE4NameArray) {
            Serie::InitUE4(ptrs.GNames, ptrs.ue4StringOffset);
        } else {
            Serie::Init(ptrs.GNames, ptrs.fnameEntryHeaderOffset);
        }
    }
    if (ptrs.GObjects) {
        Aura::Init(ptrs.GObjects);
    }

    // Sanity check + dynamic offset detection — only when BOTH are available
    ScanProgress::Set(6, "Validating offsets...");
    if (ptrs.GObjects && ptrs.GNames) {
        // Quick sanity check: verify name resolution works for a few objects
        {
            int verified = 0, tested = 0;
            for (int32_t i = 0; i < Aura::GetCount() && tested < 10; ++i) {
                uintptr_t obj = Aura::GetByIndex(i);
                if (!obj) continue;
                ++tested;
                uint32_t nameIdx = 0;
                if (Macht::ReadSafe(obj + Grimoire::OFF_UOBJECT_NAME, nameIdx)) {
                    std::string name = Serie::GetString(nameIdx);
                    if (!name.empty() && name != "None") {
                        ++verified;
                        if (verified <= 3) {
                            LOG_INFO("UE5_Init: Sanity obj[%d] name='%s' (idx=%u)", i, name.c_str(), nameIdx);
                        }
                    }
                }
            }
            LOG_INFO("UE5_Init: Name sanity: %d/%d objects resolved", verified, tested);
            if (verified == 0 && tested >= 5) {
                LOG_WARN("UE5_Init: WARNING — No objects resolved names! Check FUObjectItem size or FNamePool.");
            }
        }

        // Dynamically detect FField/FProperty/UStruct offsets
        // Must be called AFTER FNamePool + ObjectArray are initialized
        if (!Genau::ValidateAndFixOffsets(ptrs.UEVersion)) {
            LOG_WARN("UE5_Init: Offset validation failed — using default offsets (may be wrong for this UE version)");
        }

        // Post-DynOff version correction: UProperty mode definitively means UE4 pre-4.25.
        // Structural detection beats user input here — wrong offsets break exports far worse
        // than a wrong label, so we override even when bUserOverride is set (and log loudly
        // so the UI / log triage notices).
        if (!DynOff::bUseFProperty && ptrs.UEVersion >= 500) {
            uint32_t corrected = Aura::IsFlat() ? 418 : 424;
            if (ptrs.bUserOverride) {
                LOG_WARN("UE5_Init: User override = %u but UProperty mode detected — "
                         "structural correction wins, overriding to %u (flat=%s). "
                         "Update your UE Version override to UE4 if this game is misclassified.",
                         ptrs.UEVersion, corrected, Aura::IsFlat() ? "yes" : "no");
            } else {
                LOG_WARN("UE5_Init: UProperty mode detected (no FProperty) but version=%u (>= 500). "
                         "Overriding to %u (flat=%s)", ptrs.UEVersion, corrected,
                         Aura::IsFlat() ? "yes" : "no");
            }
            ptrs.UEVersion = corrected;
            g_cachedUEVersion = ptrs.UEVersion;
            g_cachedIsLowConfidence = true; // post-correction, user shouldn't blindly trust label
        }
    } else {
        LOG_WARN("UE5_Init: Partial init — GObjects=%s GNames=%s — skipping offset validation",
                 ptrs.GObjects ? "OK" : "MISSING", ptrs.GNames ? "OK" : "MISSING");
    }

    s_initialized = true;
    LOG_INFO("UE5_Init: Complete (UE%u, GObjects=0x%llX, GNames=0x%llX, Objects=%d)",
             ptrs.UEVersion,
             static_cast<unsigned long long>(ptrs.GObjects),
             static_cast<unsigned long long>(ptrs.GNames),
             Aura::GetCount());

    // Condensed summary for quick scan-log triage
    LOG_SUMMARY("build=%s config=%s UE=%u",
                BUILD_GIT_SHORT, BUILD_CONFIG, ptrs.UEVersion);
    LOG_SUMMARY("GObjects=0x%llX GNames=0x%llX GWorld=0x%llX Objects=%d",
                static_cast<unsigned long long>(ptrs.GObjects),
                static_cast<unsigned long long>(ptrs.GNames),
                static_cast<unsigned long long>(ptrs.GWorld),
                Aura::GetCount());
    LOG_SUMMARY("DynOff: CPN=%s FProp=%s TagFFV=%s Outer=+0x%02X validated=%s",
                DynOff::bCasePreservingName ? "yes" : "no",
                DynOff::bUseFProperty ? "yes" : "no",
                DynOff::bTaggedFFieldVariant ? "yes" : "no",
                DynOff::UOBJECT_OUTER,
                DynOff::bOffsetsValidated.load(std::memory_order_acquire) ? "yes" : "no");
    LOG_SUMMARY("  UStruct: Super=+0x%02X Children=+0x%02X ChildProps=+0x%02X PropsSize=+0x%02X",
                DynOff::USTRUCT_SUPER, DynOff::USTRUCT_CHILDREN,
                DynOff::USTRUCT_CHILDPROPS, DynOff::USTRUCT_PROPSSIZE);
    if (DynOff::bUseFProperty) {
        LOG_SUMMARY("  FField: Next=+0x%02X Name=+0x%02X | FProp: Offset=+0x%02X ElemSize=+0x%02X StructProp=+0x%02X",
                    DynOff::FFIELD_NEXT, DynOff::FFIELD_NAME,
                    DynOff::FPROPERTY_OFFSET, DynOff::FPROPERTY_ELEMSIZE, DynOff::FSTRUCTPROP_STRUCT);
    } else {
        LOG_SUMMARY("  UProperty: Next=+0x%02X Offset=+0x%02X ElemSize=+0x%02X",
                    DynOff::UFIELD_NEXT, DynOff::UPROPERTY_OFFSET, DynOff::UPROPERTY_ELEMSIZE);
    }

    ScanProgress::Set(7, "Complete");

    // Switch to Pipe channel — all subsequent runtime logging goes to pipe file
    Sein::SetChannel(LogChannel::Pipe);

    return true;
}

void UE5_Shutdown() {
    LOG_INFO("UE5_Shutdown: Cleaning up...");
    Mimic::StopThread();
    // Full teardown: RemoveHook + MH_Uninitialize + drain pending invoke queue.
    // Pipe server is stopped after Shutdown() so any in-flight pipe thread
    // blocked on EnqueueInvoke receives its -7 result and unwinds cleanly.
    Stark::Shutdown();
    s_pipeServer.Stop();
    s_initialized = false;
}

uint32_t UE5_GetVersion() {
    return g_cachedUEVersion;
}

uintptr_t UE5_GetGObjectsAddr() {
    return g_cachedGObjects;
}

uintptr_t UE5_GetGNamesAddr() {
    return g_cachedGNames;
}

void UE5_SetObjectDecryption(uintptr_t (*decryptFunc)(uintptr_t)) {
    Aura::SetDecryptFunc(decryptFunc);
    LOG_INFO("UE5_SetObjectDecryption: %s",
             decryptFunc ? "Custom decryption set" : "Decryption cleared");
}

int32_t UE5_GetObjectCount() {
    return Aura::GetCount();
}

uintptr_t UE5_GetObjectByIndex(int32_t index) {
    return Aura::GetByIndex(index);
}

bool UE5_GetObjectName(uintptr_t obj, char* buf, int32_t bufLen) {
    std::string name = Ubel::GetName(obj);
    return CopyToBuffer(name, buf, bufLen);
}

bool UE5_GetObjectFullName(uintptr_t obj, char* buf, int32_t bufLen) {
    std::string name = Ubel::GetFullName(obj);
    return CopyToBuffer(name, buf, bufLen);
}

uintptr_t UE5_GetObjectClass(uintptr_t obj) {
    return Ubel::GetClass(obj);
}

uintptr_t UE5_GetObjectOuter(uintptr_t obj) {
    return Ubel::GetOuter(obj);
}

uintptr_t UE5_FindObject(const char* fullPath) {
    if (!fullPath) return 0;
    return Aura::FindByName(fullPath);
}

uintptr_t UE5_FindClass(const char* className) {
    if (!className) return 0;

    uintptr_t result = 0;
    Aura::ForEach([&](int32_t /*idx*/, uintptr_t obj) -> bool {
        uintptr_t cls = Ubel::GetClass(obj);
        if (!cls) return true;

        std::string clsName = Ubel::GetName(cls);
        if (clsName == "Class") {
            std::string objName = Ubel::GetName(obj);
            if (objName == className) {
                result = obj;
                return false;
            }
        }
        return true;
    });
    return result;
}

int32_t UE5_WalkClassBegin(uintptr_t uclassAddr) {
    std::lock_guard<std::mutex> lock(s_walkMutex);
    s_walkCache = Ubel::WalkClass(uclassAddr);
    return static_cast<int32_t>(s_walkCache.Fields.size());
}

bool UE5_WalkClassGetField(int32_t index,
                           uintptr_t* outAddr,
                           char* nameOut, int32_t nameBufLen,
                           char* typeOut, int32_t typeBufLen,
                           int32_t* offsetOut,
                           int32_t* sizeOut)
{
    std::lock_guard<std::mutex> lock(s_walkMutex);
    if (index < 0 || index >= static_cast<int32_t>(s_walkCache.Fields.size())) return false;

    const auto& field = s_walkCache.Fields[index];
    if (outAddr)   *outAddr   = field.Address;
    if (offsetOut) *offsetOut = field.Offset;
    if (sizeOut)   *sizeOut   = field.Size;

    CopyToBuffer(field.Name, nameOut, nameBufLen);
    CopyToBuffer(field.TypeName, typeOut, typeBufLen);

    return true;
}

void UE5_WalkClassEnd() {
    std::lock_guard<std::mutex> lock(s_walkMutex);
    s_walkCache = ClassInfo{};
}

bool UE5_ResolveFName(uint64_t fname, char* buf, int32_t bufLen) {
    int32_t compIndex = static_cast<int32_t>(fname & 0xFFFFFFFF);
    int32_t number    = static_cast<int32_t>((fname >> 32) & 0xFFFFFFFF);

    std::string name = Serie::GetString(compIndex, number);
    return CopyToBuffer(name, buf, bufLen);
}

bool UE5_AutoStart() {
    // Called by CEPlugin's InjectDLL after the DLL is loaded into the game.
    // Idempotent: UE5_Init checks s_initialized and skips if already done.
    LOG_INFO("UE5_AutoStart: entry");
    UE5_Init();  // Always succeeds (partial init is OK — Extra Scan can recover)
    bool ok = UE5_StartPipeServer();
    LOG_INFO("UE5_AutoStart: pipe server %s", ok ? "started" : "FAILED to start");
    return ok;
}

// === Property Detail Queries (for CE Lua dissect) ===

int32_t UE5_GetFieldBoolMask(uintptr_t fieldAddr) {
    if (!fieldAddr) return 0;
    // FBoolProperty/UBoolProperty: { FieldSize(1), ByteOffset(1), ByteMask(1), FieldMask(1) }
    // FProperty (UE4.25+/UE5): at FBOOLPROP_FIELDSIZE (~0x78)
    // UProperty (UE4 <4.25):   at UBOOLPROP_FIELDSIZE (~0x70)
    int baseOff = DynOff::bUseFProperty ? DynOff::FBOOLPROP_FIELDSIZE : DynOff::UBOOLPROP_FIELDSIZE;
    for (int tryOff : { baseOff, baseOff - 4, baseOff + 4, baseOff + 8, baseOff - 8 }) {
        if (tryOff < 0) continue;
        uint8_t boolBytes[4] = {};
        if (Macht::ReadBytesSafe(fieldAddr + tryOff, boolBytes, 4)) {
            uint8_t fieldSize = boolBytes[0];
            uint8_t fieldMask = boolBytes[3];
            if (fieldSize >= 1 && fieldSize <= 8 && fieldMask != 0 && (fieldMask & (fieldMask - 1)) == 0) {
                return static_cast<int32_t>(fieldMask);
            }
        }
    }
    return 0;
}

uintptr_t UE5_GetFieldStructClass(uintptr_t fieldAddr) {
    if (!fieldAddr) return 0;
    // FStructProperty stores UScriptStruct* at DynOff::FSTRUCTPROP_STRUCT.
    constexpr int kDeltas[] = { 0, -8, 8, -16, 16, 4, -4, 12 };
    for (int delta : kDeltas) {
        int tryOff = DynOff::FSTRUCTPROP_STRUCT + delta;
        if (tryOff < 0) continue;
        uintptr_t structPtr = 0;
        if (Macht::ReadSafe(fieldAddr + tryOff, structPtr) && structPtr) {
            std::string sname = Ubel::GetName(structPtr);
            if (!sname.empty() && sname != "None") return structPtr;
        }
    }
    return 0;
}

uintptr_t UE5_GetFieldPropertyClass(uintptr_t fieldAddr) {
    // FObjectPropertyBase::PropertyClass sits at the same offset as
    // FStructProperty::Struct (DynOff::FSTRUCTPROP_STRUCT).
    // Delegate to the same probe logic — both store a UClass*/UScriptStruct*.
    return UE5_GetFieldStructClass(fieldAddr);
}

int32_t UE5_GetClassPropsSize(uintptr_t classAddr) {
    if (!classAddr) return 0;
    int32_t propsSize = 0;
    Macht::ReadSafe(classAddr + DynOff::USTRUCT_PROPSSIZE, propsSize);
    return propsSize;
}

// === UFunction Invocation ===

uintptr_t UE5_FindInstanceOfClass(const char* className) {
    if (!className || !className[0]) return 0;

    auto rset = Aura::FindInstancesByClass(className, false, 100);

    // Prefer non-CDO instance
    for (const auto& r : rset.results) {
        if (r.addr && r.name.find("Default__") == std::string::npos) {
            LOG_INFO("UE5_FindInstanceOfClass: '%s' -> 0x%llX (%s)",
                     className, (unsigned long long)r.addr, r.name.c_str());
            return r.addr;
        }
    }

    // Fallback: return first result even if CDO
    if (!rset.results.empty() && rset.results[0].addr) {
        LOG_WARN("UE5_FindInstanceOfClass: '%s' -> only CDO: 0x%llX (%s)",
                 className, (unsigned long long)rset.results[0].addr,
                 rset.results[0].name.c_str());
        return rset.results[0].addr;
    }

    LOG_WARN("UE5_FindInstanceOfClass: '%s' -> not found (scanned=%d)",
             className, rset.scanned);
    return 0;
}

uintptr_t UE5_FindFunctionByName(uintptr_t classAddr, const char* funcName) {
    if (!classAddr || !funcName || !funcName[0]) return 0;

    auto funcs = Ubel::WalkFunctions(classAddr);

    // Exact match
    for (const auto& f : funcs) {
        if (f.name == funcName) {
            LOG_INFO("UE5_FindFunctionByName: '%s' -> 0x%llX (exact match)",
                     funcName, (unsigned long long)f.address);
            return f.address;
        }
    }

    // Case-insensitive fallback
    std::string lower(funcName);
    std::transform(lower.begin(), lower.end(), lower.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    for (const auto& f : funcs) {
        std::string fl = f.name;
        std::transform(fl.begin(), fl.end(), fl.begin(),
                       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        if (fl == lower) {
            LOG_INFO("UE5_FindFunctionByName: '%s' -> 0x%llX (case-insensitive)",
                     funcName, (unsigned long long)f.address);
            return f.address;
        }
    }

    LOG_WARN("UE5_FindFunctionByName: '%s' not found (%d functions walked)",
             funcName, (int)funcs.size());
    return 0;
}

// ============================================================================
// ProcessEvent vtable detection (build 648+)
//
// Background: the pre-build-648 detector picked the vtable byte offset purely
// from a hardcoded UE-version table and "validated" the slot only by reading
// 1 byte to confirm it pointed to *some* code. Every UObject virtual passes
// that check, so on Geri (UE 4.27) and ES2 (UE 5.5) we silently hooked an
// adjacent virtual — the invoke queue never drained, KismetMathLibrary
// returns sat at 0, and the bug slept for 600+ builds because verify mode
// (the first reader of the return slot) only landed in build 637.
//
// New approach (cribbed from Dumper-7 vendor/Dumper-7/Dumper/Engine/Private/
// OffsetFinder/Offsets.cpp:15-74): iterate the UObject vtable slot-by-slot,
// fetch each candidate function's body, and look for two `TEST [reg+disp32],
// imm32` instructions that ProcessEvent uniquely checks against:
//   1) imm32 = 0x00000400  (FUNC_Native)         — within first 0x400 bytes
//   2) imm32 = 0x00400000  (FUNC_HasOutParms /
//                           FUNC_NetServer mask) — within first 0xF00 bytes
// Both `disp32` operands point at UFunction::FunctionFlags (~0x88..0xC0 across
// UE versions). We don't need to know the exact FunctionFlags offset — just
// that *some* `TEST DWORD PTR [reg+disp32], imm32` pair exists with the right
// immediates. Function-fingerprinting beats slot-fingerprinting because slot
// indexes drift with UObject virtual additions across versions (and across
// publisher forks), but the FUNC_Native test inside ProcessEvent is stable.
//
// Fallback: if no slot matches the pattern (e.g. heavily-optimised LTO build
// that rewrote the test sequence), fall back to the legacy version-based
// table so we degrade gracefully instead of bricking the whole invoke path.
// ============================================================================

// 10-byte signatures for `F7 /0 disp32, imm32` (TEST [reg+disp32], imm32):
//   [F7] [ModRM] [disp32 low byte = FF_off] [disp32 high 3 bytes = 0] [imm32]
// ModRM and the FF_off byte are wildcarded — FF_off varies by UE version
// (0x88..0xC0, always < 0x100 so the high 3 bytes of disp32 are 0).
// The remaining 7 bytes pin the instruction shape and the literal imm32.
namespace {
    constexpr uint8_t kPePat1Bytes[10] = { 0xF7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00 };
    constexpr char    kPePat1Mask[11] =   "x??xxxxxxx";   // 10 chars + NUL
    constexpr uint8_t kPePat2Bytes[10] = { 0xF7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00 };
    constexpr char    kPePat2Mask[11] =   "x??xxxxxxx";

    // Linear pattern search with 'x' = exact / '?' = wildcard mask.
    // Returns true if found within haystack[0..size).
    bool ContainsPattern(const uint8_t* haystack, size_t haystackSize,
                         const uint8_t* pattern, const char* mask, size_t patternSize) {
        if (haystackSize < patternSize) return false;
        for (size_t i = 0; i <= haystackSize - patternSize; ++i) {
            bool ok = true;
            for (size_t j = 0; j < patternSize; ++j) {
                if (mask[j] == 'x' && haystack[i + j] != pattern[j]) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }

    // Sample-many-objects vtable extractor. Most slots only need one valid
    // UObject*, but a few pathological GObjects entries near the head can be
    // garbage CDOs with NULL or torn vtable pointers — walk a window of 200.
    uintptr_t FindAnyValidVTable() {
        for (int i = 0; i < Aura::GetCount() && i < 200; ++i) {
            uintptr_t obj = Aura::GetByIndex(i);
            if (!obj) continue;
            uintptr_t vt = 0;
            if (Macht::ReadSafe(obj, vt) && vt) return vt;
        }
        return 0;
    }
}  // namespace

// Pattern-based detection: scan a window of vtable slots, return the byte
// offset whose function body contains both Dumper-7 TEST patterns. -1 = miss.
static int DetectProcessEventVTableOffsetByPattern(uintptr_t vtable) {
    // Vtable window: every UE version we support places PE between 0x100 and
    // 0x300. Step 8 = pointer stride. ~64 slots × ~0xF00 byte read is fast.
    constexpr int kMinOffset   = 0x100;
    constexpr int kMaxOffset   = 0x300;
    constexpr size_t kBodySize = 0xF00;

    uint8_t body[kBodySize];

    for (int off = kMinOffset; off <= kMaxOffset; off += 8) {
        uintptr_t funcAddr = 0;
        if (!Macht::ReadSafe(vtable + off, funcAddr) || !funcAddr) continue;

        // Read function body; failure means this slot points somewhere
        // unreadable (broken vtable entry) — skip without logging spam.
        if (!Macht::ReadBytesSafe(funcAddr, body, kBodySize)) continue;

        // Pattern 1 (FUNC_Native test) must be within first 0x400 bytes —
        // ProcessEvent's prologue does this very early.
        if (!ContainsPattern(body, 0x400, kPePat1Bytes, kPePat1Mask, sizeof(kPePat1Bytes))) continue;

        // Pattern 2 (high-flag test) can live deeper into the function body.
        if (!ContainsPattern(body, kBodySize, kPePat2Bytes, kPePat2Mask, sizeof(kPePat2Bytes))) continue;

        LOG_INFO("DetectProcessEvent (pattern): match at vtable+0x%X -> 0x%llX",
                 off, (unsigned long long)funcAddr);
        return off;
    }
    return -1;
}

// Legacy version-based detection — kept as a fallback for cases where the
// pattern scanner whiffs (unusual compiler output, heavily-optimised LTO,
// custom publisher fork). Not authoritative anymore: a "success" return
// here will be cross-checked by post-install hook-fire-count validation
// in TryInstallGameThreadHook below.
static int DetectProcessEventVTableOffsetByVersion(uintptr_t vtable) {
    int primary;
    if (g_cachedUEVersion >= 550)      primary = 0x228;
    else if (g_cachedUEVersion >= 500) primary = 0x220;
    else if (g_cachedUEVersion >= 425) primary = 0x218;
    else if (g_cachedUEVersion >= 420) primary = 0x210;
    else                               primary = 0x208;

    LOG_WARN("DetectProcessEvent (fallback): pattern scan missed, "
             "falling back to UE=%u version-table primary=0x%X",
             g_cachedUEVersion, primary);
    for (int delta = -16; delta <= 16; delta += 8) {
        int off = primary + delta;
        if (off < 0) continue;
        uintptr_t addr = 0;
        Macht::ReadSafe(vtable + off, addr);
        LOG_INFO("  vtable+0x%03X = 0x%llX%s",
                 off, (unsigned long long)addr,
                 delta == 0 ? "  <-- primary" : "");
    }

    uintptr_t funcAddr = 0;
    if (Macht::ReadSafe(vtable + primary, funcAddr) && funcAddr) {
        uint8_t test = 0;
        if (Macht::ReadBytesSafe(funcAddr, &test, 1)) {
            return primary;
        }
    }
    for (int d : { 8, -8, 16, -16 }) {
        int off = primary + d;
        if (off < 0) continue;
        funcAddr = 0;
        if (Macht::ReadSafe(vtable + off, funcAddr) && funcAddr) {
            uint8_t test = 0;
            if (Macht::ReadBytesSafe(funcAddr, &test, 1)) {
                return off;
            }
        }
    }
    return -1;
}

// Top-level resolver. Pattern-based first, version-table second. Caller is
// expected to additionally validate by post-install hook-fire-count check.
static int DetectProcessEventVTableOffset() {
    uintptr_t vtable = FindAnyValidVTable();
    if (!vtable) {
        LOG_ERROR("DetectProcessEvent: no valid UObject vtable available");
        return -1;
    }

    int off = DetectProcessEventVTableOffsetByPattern(vtable);
    if (off > 0) return off;

    off = DetectProcessEventVTableOffsetByVersion(vtable);
    if (off > 0) return off;

    LOG_ERROR("DetectProcessEvent: both pattern scan and version fallback failed");
    return -1;
}

static int s_processEventOffset = -2;  // -2 = not yet detected

/// Resolve the actual ProcessEvent function address from any valid UObject's vtable.
/// Used both for direct calls and for installing the game-thread hook.
static uintptr_t ResolveProcessEventAddr() {
    if (s_processEventOffset == -2) {
        s_processEventOffset = DetectProcessEventVTableOffset();
    }
    if (s_processEventOffset < 0) return 0;

    // Find any valid UObject to read its vtable
    uintptr_t testObj = 0;
    for (int idx = 1; idx < 100; idx++) {
        auto* item = Aura::GetItem(idx);
        if (item && item->Object) { testObj = item->Object; break; }
    }
    if (!testObj) return 0;

    uintptr_t vtable = 0;
    if (!Macht::ReadSafe(testObj, vtable) || !vtable) return 0;

    uintptr_t peAddr = 0;
    if (!Macht::ReadSafe(vtable + s_processEventOffset, peAddr) || !peAddr) return 0;

    return peAddr;
}

/// Try to install the game-thread ProcessEvent hook.
/// Called lazily on first UE5_CallProcessEvent invocation.
///
/// After a successful MinHook install, spawns a detached validator thread
/// (build 648+) that waits 1500ms and confirms Stark::GetHookFireCount() is
/// non-zero. UObject::ProcessEvent fires many times per second under normal
/// gameplay (every tick, every input dispatch, every anim notify), so a zero
/// reading after 1.5s is strong evidence we hooked an adjacent virtual
/// rather than PE itself. Logs ERROR loudly when that happens — silent
/// vtable-misdetection is the whole reason the bug at builds 1..647
/// stayed buried, so we now refuse to keep that failure mode silent.
static void TryInstallGameThreadHook() {
    static bool s_hookAttempted = false;
    if (s_hookAttempted) return;
    s_hookAttempted = true;

    uintptr_t peAddr = ResolveProcessEventAddr();
    if (!peAddr) {
        LOG_WARN("GameThreadDispatch: cannot resolve ProcessEvent address for hooking");
        return;
    }

    if (!Stark::InstallHook(peAddr)) {
        LOG_WARN("GameThreadDispatch: hook install failed, invoke will use direct call (unsafe)");
        return;
    }
    LOG_INFO("GameThreadDispatch: hook installed at 0x%llX, validator armed (1500ms)",
             (unsigned long long)peAddr);

    // Detached validator: don't block the caller — UE5_Init must keep
    // running. The validator just observes and logs; remediation (e.g.
    // unhook + re-detect with a different strategy) is intentionally out
    // of scope for v1 since unhook-while-game-thread-may-be-inside-the-
    // trampoline is itself unsafe (see Stark::RemoveHook comment).
    std::thread([peAddr]() {
        uint64_t before = Stark::GetHookFireCount();
        std::this_thread::sleep_for(std::chrono::milliseconds(1500));
        uint64_t after  = Stark::GetHookFireCount();
        uint64_t delta  = after - before;
        if (delta == 0) {
            LOG_ERROR("GameThreadDispatch: VALIDATION FAILED — hook at 0x%llX fired 0 "
                      "times in 1500ms. We are almost certainly hooked on the wrong "
                      "vtable slot. UFunction invokes via this path WILL time out. "
                      "(If the game is paused/main-menu, ignore; otherwise this is a "
                      "real misdetection — please collect logs.)",
                      (unsigned long long)peAddr);
        } else {
            LOG_INFO("GameThreadDispatch: validation OK — hook fired %llu times "
                     "in 1500ms (hook=0x%llX)",
                     (unsigned long long)delta, (unsigned long long)peAddr);
        }
    }).detach();
}

// Internal size-aware entry. paramsSize > 0 makes the queued GameThreadDispatch
// request OWN a copy of the param bytes, so a timed-out-but-still-queued invoke
// can't dereference a freed caller buffer (use-after-free). Out-params are copied
// back on success. The direct fallback is synchronous (buffer stays alive), so
// size is irrelevant there. Declared extern "C" so Fern links to it directly.
extern "C" int32_t UE5_CallProcessEventEx(uintptr_t instance, uintptr_t ufunc,
                                          uintptr_t params, uint32_t paramsSize) {
    if (!instance || !ufunc) return -1;

    // Lazy detection
    if (s_processEventOffset == -2) {
        s_processEventOffset = DetectProcessEventVTableOffset();
        TryInstallGameThreadHook();
    }
    if (s_processEventOffset < 0) return -3;

    // Prefer game-thread dispatch via hook
    if (Stark::IsHookActive()) {
        LOG_INFO("UE5_CallProcessEvent: dispatching to game thread inst=0x%llX func=0x%llX",
                 (unsigned long long)instance, (unsigned long long)ufunc);
        return Stark::EnqueueInvoke(instance, ufunc, params, paramsSize);
    }

    // Fallback: direct call from current thread (unsafe for state-changing functions)
    LOG_WARN("UE5_CallProcessEvent: hook not active, using direct call (unsafe)");

    // Read vtable from the target instance
    uintptr_t vtable = 0;
    if (!Macht::ReadSafe(instance, vtable) || !vtable) return -2;

    uintptr_t peAddr = 0;
    if (!Macht::ReadSafe(vtable + s_processEventOffset, peAddr) || !peAddr) return -3;

    typedef void (__fastcall *FnProcessEvent)(void*, void*, void*);
    auto pProcessEvent = reinterpret_cast<FnProcessEvent>(peAddr);

    LOG_INFO("UE5_CallProcessEvent: direct call inst=0x%llX func=0x%llX pe=0x%llX",
             (unsigned long long)instance, (unsigned long long)ufunc,
             (unsigned long long)peAddr);

    __try {
        pProcessEvent(reinterpret_cast<void*>(instance),
                      reinterpret_cast<void*>(ufunc),
                      reinterpret_cast<void*>(params));
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        LOG_ERROR("UE5_CallProcessEvent: EXCEPTION during direct ProcessEvent call!");
        return -4;
    }

    LOG_INFO("UE5_CallProcessEvent: direct call success (warn: not game-thread)");
    return 0;
}

int32_t UE5_CallProcessEvent(uintptr_t instance, uintptr_t ufunc, uintptr_t params) {
    // Legacy 3-arg export (CE Lua + Mimic's mailbox). Size 0 = no owned copy:
    // callers here pass persistent buffers that outlive the queued request.
    return UE5_CallProcessEventEx(instance, ufunc, params, 0);
}

// Direct call entry point — never goes through GameThreadDispatch.
// Mirrors the fallback path of UE5_CallProcessEvent without the hook
// check; intended for callers (e.g. Mimic::HandleInvoke) that have
// independently verified the function is safe to call off-thread.
// Sharing the body via a static helper would tangle SEH+C++ object
// lifetimes; the duplication is small.
int32_t UE5_CallProcessEventDirect(uintptr_t instance, uintptr_t ufunc, uintptr_t params) {
    if (!instance || !ufunc) return -1;

    // Lazy detection (same as the dispatching path)
    if (s_processEventOffset == -2) {
        s_processEventOffset = DetectProcessEventVTableOffset();
        TryInstallGameThreadHook();
    }
    if (s_processEventOffset < 0) return -3;

    uintptr_t vtable = 0;
    if (!Macht::ReadSafe(instance, vtable) || !vtable) return -2;

    uintptr_t peAddr = 0;
    if (!Macht::ReadSafe(vtable + s_processEventOffset, peAddr) || !peAddr) return -3;

    typedef void (__fastcall *FnProcessEvent)(void*, void*, void*);
    auto pProcessEvent = reinterpret_cast<FnProcessEvent>(peAddr);

    LOG_INFO("UE5_CallProcessEventDirect: inst=0x%llX func=0x%llX pe=0x%llX (caller-asserted safe)",
             (unsigned long long)instance, (unsigned long long)ufunc,
             (unsigned long long)peAddr);

    __try {
        pProcessEvent(reinterpret_cast<void*>(instance),
                      reinterpret_cast<void*>(ufunc),
                      reinterpret_cast<void*>(params));
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        LOG_ERROR("UE5_CallProcessEventDirect: EXCEPTION during direct ProcessEvent call!");
        return -4;
    }

    return 0;
}

// === Mailbox ===

uintptr_t UE5_GetMailboxAddr() {
    return Mimic::GetAddress();
}

// === Pipe Server ===

bool UE5_StartPipeServer() {
    // Guard: if another UE5Dumper instance (e.g., proxy DLL) already owns the pipe,
    // skip starting a competing pipe server to avoid connection failures.
    HANDLE testPipe = CreateFileW(
        Grimoire::PIPE_NAME,
        GENERIC_READ, 0, nullptr,
        OPEN_EXISTING, 0, nullptr);
    if (testPipe != INVALID_HANDLE_VALUE) {
        CloseHandle(testPipe);
        LOG_WARN("UE5_StartPipeServer: pipe already exists (another instance running) — skipping");
        return true;  // return true so CE Lua doesn't treat it as failure
    }
    return s_pipeServer.Start();
}

void UE5_StopPipeServer() {
    s_pipeServer.Stop();
}

bool UE5_IsPipeConnected() {
    return s_pipeServer.IsClientConnected();
}

} // extern "C"
