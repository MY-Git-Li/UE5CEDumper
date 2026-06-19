; ============================================================
; Lugner_Dxgi.asm — dxgi.dll proxy forwarding thunks (x64 MASM)
;
; Each fN is a LAZY self-resolving trampoline:
;   - Fast path: jump through mProcs[N] (the real dxgi export address).
;   - First call (mProcs[N] == 0): call the C resolver
;     DxgiProxy_EnsureResolved (which LoadLibrary's the real dxgi.dll and
;     fills mProcs[]), preserving all argument registers, then jump.
;
; Why lazy (this replaced an eager DxgiProxy_Init() in DllMain):
; resolving in DllMain meant LoadLibrary(real dxgi.dll) ran during the
; EXE's static-import initialisation with the loader lock held — a
; classic loader-reentrancy violation. It crashed games whose EXE imports
; dxgi.dll directly and BEFORE d3d11.dll (e.g. Octopath Traveler), because
; the real dxgi was not yet mapped, forcing a full recursive load of dxgi
; + its dependency tree under the lock. The first real dxgi call
; (CreateDXGIFactory1 during RHI init) happens on a game thread AFTER
; DllMain returns and the loader lock is released, so resolving there is
; safe — exactly what the version.dll / dinput8.dll proxies already do.
;
; A bare `jmp` preserves every register / the stack / the return address,
; so it forwards a call of ANY signature / calling convention
; transparently — required because several dxgi exports (DXGID3D10*,
; Compat*, PIX*) are undocumented and we don't know their prototypes. The
; one-time resolver detour (ResolveAll) likewise saves and restores all
; volatile argument registers (RCX/RDX/R8/R9 + XMM0-3) so the subsequent
; tail-jmp sees the original arguments intact.
;
; The fN index order MUST stay in sync with:
;   - kDxgiExports[] in Lugner_Dxgi.cpp
;   - the "name = fN @ordinal" map in ProxyDxgi.def
;
; Pattern mirrors RE-UE4SS's proxy_generator output (vendored under
; vendor/RE-UE4SS/UE4SS/proxy_generator/main.cpp), but resolves lazily
; instead of eagerly in DllMain.
; ============================================================

option casemap:none          ; keep mProcs / f0.. / DxgiProxy_EnsureResolved
                             ; case exact so they match the extern "C" symbols

.code

extern mProcs:QWORD
extern DxgiProxy_EnsureResolved:PROC

public f0,  f1,  f2,  f3,  f4,  f5,  f6,  f7,  f8,  f9
public f10, f11, f12, f13, f14, f15, f16, f17, f18, f19

; ── One-time resolver detour ────────────────────────────────────────────
; Preserves every register that could carry a forwarded argument (RCX/RDX/
; R8/R9 + XMM0-3), calls the C resolver once, then restores them. Touches
; no non-volatile register, so the original call's full register/stack
; state survives the detour and the subsequent tail-jmp into the real
; dxgi export is transparent.
;
; Stack alignment: entered via `call ResolveAll` from a thunk that was
; itself entered via `call fN` from the game, so RSP is 16-byte aligned
; here. The 4 pushes (32 bytes) + sub 64 + sub 32 (shadow) all keep RSP
; 16-aligned for the inner call. movups (unaligned) is used for the XMM
; saves so the save area needs no special alignment.
ResolveAll proc
    push rcx
    push rdx
    push r8
    push r9
    sub  rsp, 64
    movups [rsp+0],  xmm0
    movups [rsp+16], xmm1
    movups [rsp+32], xmm2
    movups [rsp+48], xmm3
    sub  rsp, 32                       ; shadow space (RSP stays 16-aligned)
    call DxgiProxy_EnsureResolved
    add  rsp, 32
    movups xmm0, [rsp+0]
    movups xmm1, [rsp+16]
    movups xmm2, [rsp+32]
    movups xmm3, [rsp+48]
    add  rsp, 64
    pop  r9
    pop  r8
    pop  rdx
    pop  rcx
    ret
ResolveAll endp

; ── Lazy forwarding thunk ───────────────────────────────────────────────
; fN: jump through mProcs[N]; on first use (mProcs[N]==0) resolve, then jump.
LAZY_THUNK macro idx
f&idx& proc
    mov  rax, qword ptr mProcs[8*idx]
    test rax, rax
    jnz  f&idx&_ready
    call ResolveAll
    mov  rax, qword ptr mProcs[8*idx]
f&idx&_ready:
    jmp  rax
f&idx& endp
endm

    LAZY_THUNK 0
    LAZY_THUNK 1
    LAZY_THUNK 2
    LAZY_THUNK 3
    LAZY_THUNK 4
    LAZY_THUNK 5
    LAZY_THUNK 6
    LAZY_THUNK 7
    LAZY_THUNK 8
    LAZY_THUNK 9
    LAZY_THUNK 10
    LAZY_THUNK 11
    LAZY_THUNK 12
    LAZY_THUNK 13
    LAZY_THUNK 14
    LAZY_THUNK 15
    LAZY_THUNK 16
    LAZY_THUNK 17
    LAZY_THUNK 18
    LAZY_THUNK 19

end
