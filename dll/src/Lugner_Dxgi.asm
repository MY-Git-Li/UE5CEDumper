; ============================================================
; Lugner_Dxgi.asm — dxgi.dll proxy forwarding thunks (x64 MASM)
;
; Each f<N> is a naked trampoline that jumps to the real dxgi
; export address stored in mProcs[N] (populated at load time by
; Lugner_Dxgi.cpp::DxgiProxy_Init). A bare `jmp` preserves every
; register, the stack and the return address, so it forwards a
; call of ANY signature / calling convention transparently — this
; is required because several dxgi exports (DXGID3D10*, Compat*,
; PIX*) are undocumented and we don't know their prototypes. The
; existing version.dll / dinput8.dll proxies could use plain C
; forwarders only because every one of their exports has a known,
; documented signature; dxgi does not, hence this asm trampoline.
;
; The f<N> index order MUST stay in sync with:
;   - kDxgiExports[] in Lugner_Dxgi.cpp
;   - the "name = f<N> @ordinal" map in ProxyDxgi.def
;
; Pattern mirrors RE-UE4SS's proxy_generator output (vendored under
; vendor/RE-UE4SS/UE4SS/proxy_generator/main.cpp).
; ============================================================

option casemap:none          ; keep mProcs / f0.. case exact so they
                             ; match the extern "C" symbols from C++

.code

extern mProcs:QWORD

public f0,  f1,  f2,  f3,  f4,  f5,  f6,  f7,  f8,  f9
public f10, f11, f12, f13, f14, f15, f16, f17, f18, f19

f0  proc
    jmp mProcs[8*0]
f0  endp
f1  proc
    jmp mProcs[8*1]
f1  endp
f2  proc
    jmp mProcs[8*2]
f2  endp
f3  proc
    jmp mProcs[8*3]
f3  endp
f4  proc
    jmp mProcs[8*4]
f4  endp
f5  proc
    jmp mProcs[8*5]
f5  endp
f6  proc
    jmp mProcs[8*6]
f6  endp
f7  proc
    jmp mProcs[8*7]
f7  endp
f8  proc
    jmp mProcs[8*8]
f8  endp
f9  proc
    jmp mProcs[8*9]
f9  endp
f10 proc
    jmp mProcs[8*10]
f10 endp
f11 proc
    jmp mProcs[8*11]
f11 endp
f12 proc
    jmp mProcs[8*12]
f12 endp
f13 proc
    jmp mProcs[8*13]
f13 endp
f14 proc
    jmp mProcs[8*14]
f14 endp
f15 proc
    jmp mProcs[8*15]
f15 endp
f16 proc
    jmp mProcs[8*16]
f16 endp
f17 proc
    jmp mProcs[8*17]
f17 endp
f18 proc
    jmp mProcs[8*18]
f18 endp
f19 proc
    jmp mProcs[8*19]
f19 endp

end
