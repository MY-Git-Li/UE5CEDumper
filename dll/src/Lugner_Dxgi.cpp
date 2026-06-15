// ============================================================
// Lugner_Dxgi — dxgi.dll forwarding proxy (data + resolver half)
//
// Compiled only for UE5_PROXY_DXGI_BUILD. Resolves the real
// dxgi.dll exports from System32 into mProcs[], which the asm
// jmp-thunks in Lugner_Dxgi.asm jump through. The .def
// (ProxyDxgi.def) maps each Windows export name to its thunk.
//
// Why dxgi in addition to version.dll / dinput8.dll? Some games
// import neither version.dll nor dinput8.dll, so those proxies
// are never loaded by the OS at all (their forwarders are dead
// weight). dxgi.dll, by contrast, is statically imported by every
// D3D11/D3D12 Unreal Engine game on Windows — making it the
// reliable hijack target for that population. (Observed concrete
// case: a SQUARE ENIX UE4.27 demo that imports dxgi + winmm but
// neither version nor dinput8.)
//
// Unlike Lugner.cpp / Lugner_Dinput8.cpp — which use plain C
// forwarders because every version/dinput8 export has a known,
// documented signature — dxgi exports several undocumented
// internals (DXGID3D10*, Compat*, PIX*) whose prototypes we don't
// know. So forwarding goes through signature-agnostic asm jmp
// trampolines (Lugner_Dxgi.asm) instead. This mirrors RE-UE4SS's
// proxy generator (vendor/RE-UE4SS/UE4SS/proxy_generator).
//
// Mutual exclusion: when multiple UE5CEDumper proxy DLLs sit in
// the same game folder, Heiter.cpp's mutex makes only the first to
// load run full init. DxgiProxy_Init still runs in BOTH paths
// (active and passive) — a passive forwarder must keep forwarding
// dxgi calls — so it is called at the very top of DllMain, before
// the mutex check.
// ============================================================

#ifdef UE5_PROXY_DXGI_BUILD

#include <Windows.h>
#include <cstdint>
#define LOG_CAT "PROXY"
#include "Sein.h"

// Real dxgi export addresses, indexed to match the f<N> thunks in
// Lugner_Dxgi.asm and the "name = f<N>" map in ProxyDxgi.def.
// The .asm references this exact symbol via `extern mProcs:QWORD`,
// so it must have C linkage (no name mangling) and the matching name.
extern "C" uintptr_t mProcs[20] = { 0 };

// Status, logged later from the (Sein-initialised) auto-start thread
// because DxgiProxy_Init runs before Sein::Init in DllMain.
extern "C" bool g_dxgiProxyLoaded = false;
extern "C" int  g_dxgiProxyResolved = 0;

// Export names in f0..f19 order. MUST stay in sync with ProxyDxgi.def
// and the asm thunk order. Resolution is by NAME (version-robust: a name
// absent on some Windows build simply yields a null slot, which only
// matters if that rarely-used internal is ever called).
static const char* const kDxgiExports[20] = {
    "ApplyCompatResolutionQuirking",    // f0  @1
    "CompatString",                     // f1  @2
    "CompatValue",                      // f2  @3
    "DXGIDumpJournal",                  // f3  @4
    "PIXBeginCapture",                  // f4  @5
    "PIXEndCapture",                    // f5  @6
    "PIXGetCaptureState",               // f6  @7
    "SetAppCompatStringPointer",        // f7  @8
    "UpdateHMDEmulationStatus",         // f8  @9
    "CreateDXGIFactory",                // f9  @10
    "CreateDXGIFactory1",               // f10 @11
    "CreateDXGIFactory2",               // f11 @12
    "DXGID3D10CreateDevice",            // f12 @13
    "DXGID3D10CreateLayeredDevice",     // f13 @14
    "DXGID3D10GetLayeredDeviceSize",    // f14 @15
    "DXGID3D10RegisterLayers",          // f15 @16
    "DXGIDeclareAdapterRemovalSupport", // f16 @17
    "DXGIDisableVBlankVirtualization",  // f17 @18
    "DXGIGetDebugInterface1",           // f18 @19
    "DXGIReportAdapterConfiguration",   // f19 @20
};

// Populate mProcs from the real System32 dxgi.dll. Must run before any
// forwarded dxgi export can be called. The game's first dxgi call
// (CreateDXGIFactory1 during RHI init) happens after the EXE entry point
// — i.e. after our DllMain returns — so resolving synchronously in
// DllMain ATTACH is both necessary (a delayed thread would race the call
// and jump through a null slot) and sufficient.
//
// LoadLibrary of a leaf system DLL from DllMain is the established proxy
// pattern (RE-UE4SS does the same); dxgi has minimal loader-time init and
// no dependency back on us, so it does not risk loader-lock recursion.
// Intentionally does NOT log: Sein is not initialised this early — status
// is recorded into globals and logged by DxgiProxy_LogStatus().
extern "C" void DxgiProxy_Init()
{
    static bool s_done = false;
    if (s_done) return;
    s_done = true;

    wchar_t sysDir[MAX_PATH] = {};
    GetSystemDirectoryW(sysDir, MAX_PATH);

    wchar_t realPath[MAX_PATH] = {};
    wsprintfW(realPath, L"%s\\dxgi.dll", sysDir);

    HMODULE real = LoadLibraryW(realPath);
    if (!real) {
        g_dxgiProxyLoaded = false;
        return;
    }
    g_dxgiProxyLoaded = true;

    int resolved = 0;
    for (int i = 0; i < 20; ++i) {
        mProcs[i] = reinterpret_cast<uintptr_t>(GetProcAddress(real, kDxgiExports[i]));
        if (mProcs[i]) ++resolved;
    }
    g_dxgiProxyResolved = resolved;
}

// Emit the resolution result once the logger is up. Called from the proxy
// auto-start thread (Heiter.cpp) after Sein::Init / InitProcessMirror.
extern "C" void DxgiProxy_LogStatus()
{
    if (g_dxgiProxyLoaded) {
        LOG_INFO("dxgi proxy: forwarded %d/20 exports to real System32 dxgi.dll",
                 g_dxgiProxyResolved);
    } else {
        LOG_ERROR("dxgi proxy: FAILED to load real System32 dxgi.dll — forwarded calls will crash");
    }
}

#endif // UE5_PROXY_DXGI_BUILD
