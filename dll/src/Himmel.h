#pragma once

#include <cstdint>

// ============================================================
// Himmel — 欣梅爾 (勇者 — The Hero, Remembered Forever)
// Signatures: 128+ AOB pattern database
//
// All byte-pattern signatures for GObjects, GNames, GWorld,
// and related UE global pointer scanning live in this file.
//
// HOW TO ADD NEW PATTERNS:
//   1. Add a constexpr const char* in the appropriate section
//   2. Name it AOB_{TARGET}_{SOURCE}{N} (e.g., AOB_GOBJECTS_RE3)
//   3. Add a comment with: opcode meaning, UE version, game
//   4. Add an AobSignature entry to the corresponding PATTERNS[] array
//
// Sources:
//   V1-V13  : Original UE5CEDumper patterns
//   PS1-PS7 : patternsleuth (github.com/trumank/patternsleuth)
//   RE1-RE5 : RE-UE4SS CustomGameConfigs (github.com/UE4SS-RE/RE-UE4SS)
//   D7_1    : Dumper-7 (github.com/Encryqed/Dumper-7)
//   CT1-CT5 : UE4 Dumper.CT (vendor/UE4 Dumper.CT)
//   UD1-UD3 : UEDumper (github.com/Spuckwaffel/UEDumper)
//   ES2     : Everspace 2 (UE 5.5)
//   SF      : SatisfFactory (UE 5.3, modular build — patterns in DLLs)
//   TQ      : TQ2 (UE 5.x)
//   G42     : UE 4.2 game analysis (docs/UE 4.2 AOBs.txt)
//   G427    : UE 4.27 game analysis (work/UE 4.27 AOBs.txt)
//   SAT422  : Satisfactory old UE 4.22 build analysis (work/SF UE 4.22 AOBs.txt)
//   ES53    : Everspace 2 UE 5.3 build analysis (work/ES2 UE 5.3 AOBs.txt)
//   SAT425  : Satisfactory UE 4.25 build analysis (work/SF UE 4.25 AOBs.txt)
//   SAT426  : Satisfactory UE 4.26 build analysis (work/SF UE 4.26 AOBs.txt)
//   SAT52   : Satisfactory UE 5.2 build analysis (work/SF UE 5.21 AOBs.txt)
//   OT      : Octopath Traveller (UE4, Ghidra + CE analysis, codename "Kingship")
//   GH      : Ghidra cross-game analysis (aob_export/analysis_report.md)
//   ME      : MindsEye (Build A Rocket Boy, UE 5.4.4 licensee fork — capstone + .pdata analysis)
//   SP57    : Solarpunk (rokaplay, UE 5.7 — ships a full PDB; symbols + xrefs mined offline
//             via Ghidra headless, then every candidate verified unique against the .text
//             image before inclusion — see docs/reversing-nonstandard-ue-games.md)
//   DI427   : DropIn - VR Battle Royale (UE 4.27.2, CL-18319896 — ships a full 286 MB PDB;
//             the project's FIRST symbolised UE 4.27 oracle). Mined with the same Ghidra
//             headless flow as SP57, but every candidate additionally had to survive a
//             THREE-BINARY gauntlet before inclusion: UNIQUE-OK (every hit resolves to the
//             true VA, zero decoys) on DropIn, and zero hits *or* correct on Solarpunk
//             (UE 5.7) and Avowed (UE 5.3, packed 20-byte items). See
//             tools/ghidra/scan_patterns.java — it reports hits/ok/decoy AND whether a
//             correct hit sorts before its decoys, which is what actually decides safety
//             for a weakly-validated target.
//   ES55    : Everspace 2, 2025-05-17 snapshot (UE 5.5, ships a full PDB — the second
//             symbolised oracle). Note the project name "ES2-0517" is a DATE, not a
//             version. Version pinned structurally: FFieldVariant=0x08 (>=5.1.1),
//             UEnum::Names still TArray<TTuple> (<5.6), FUObjectItem 24B WITH RefCount,
//             classic FChunkedFixedUObjectArray order (<5.8), and the PDB's
//             EUnrealEngineObjectUE5Version enum ends at ASSETREGISTRY_PACKAGEBUILDDEPENDENCIES.
// ============================================================

// ============================================================
// AOB Pattern Metadata Types
// ============================================================

enum class AobTarget : uint8_t {
    GObjects        = 0,
    GNames          = 1,
    GWorld          = 2,
    SparseDelegates = 3,  // FSparseDelegateStorage::SparseDelegates (UE 4.23+)
    GEngine         = 4,  // UEngine* GEngine — the &GEngine SLOT, not the object
};

// How to resolve the AOB match address into a final pointer
enum class AobResolve : uint8_t {
    RipDirect        = 0,  // RIP-relative -> address is direct target
    RipDeref         = 1,  // RIP-relative -> deref once (pointer-to-pointer)
    RipBoth          = 2,  // Try direct first, if validation fails try deref
    SymbolExport     = 3,  // MSVC mangled symbol → address IS the variable
    CallFollow       = 4,  // Follow CALL in AOB match, scan function body for RIP refs
    SymbolCallFollow = 5,  // MSVC mangled symbol → address IS a function → scan body for RIP refs
};

// Unified AOB signature descriptor.
// All fields are POD — constexpr-constructible, stored in .rdata.
struct AobSignature {
    const char* id;           // Unique identifier, e.g. "GOBJ_V1", "GWORLD_ES2_1"
    const char* pattern;      // AOB pattern string ("48 8B 05 ?? ?? ?? ??") or mangled symbol name
    AobTarget   target;       // What global pointer this pattern finds
    AobResolve  resolve;      // How to resolve the match address
    int  instrOffset;         // Byte offset from match start to the RIP instruction (0 = at match start)
    int  opcodeLen;           // Opcode bytes before the 4-byte displacement (typically 3)
    int  totalLen;            // Total instruction length (typically 7 for REX+opcode+modrm+disp32)
    int  adjustment;          // Post-resolution offset adjustment (e.g. -0x10 for struct base)
    int  priority;            // Lower = tried first. 0=symbol exports, 10-20=long, 50=standard, 80=legacy
    int  callOffset;          // For CallFollow: byte offset of E8 opcode within the pattern
    bool gworldAllowNull;     // For GWorld: accept null dereference (write-patterns at startup)
    const char* source;       // Attribution: "V", "PS", "RE", "ES2", "SF", "TQ", etc.
    const char* notes;        // Human-readable: game name, UE version
};

namespace Sig {

// ============================================================
// GObjects / FUObjectArray
// ============================================================

// --- Original patterns (V-series) ---

// V1: mov rax,[rip+X]; mov rcx,[rax+rcx*8]  — classic UE5.0-5.2
constexpr const char* AOB_GOBJECTS_V1 = "48 8B 05 ?? ?? ?? ?? 48 8B 0C C8";
// V2: mov r9,[rip+X]; mov [rip+Y],r9  — common UE5.3+
constexpr const char* AOB_GOBJECTS_V2 = "4C 8B 0D ?? ?? ?? ?? 4C 89 0D";
// V3: mov r8,[rip+X]; test r8,r8
constexpr const char* AOB_GOBJECTS_V3 = "4C 8B 05 ?? ?? ?? ?? 4D 85 C0";
// V4: mov rax,[rip+X]; mov rcx,[rax+rcx*8]; test rcx,rcx  (longer context)
constexpr const char* AOB_GOBJECTS_V4 = "48 8B 05 ?? ?? ?? ?? 48 8B 0C C8 48 85 C9";
// V5: mov r10,[rip+X]; test r10,r10
constexpr const char* AOB_GOBJECTS_V5 = "4C 8B 15 ?? ?? ?? ?? 4D 85 D2";
// V6: mov rcx,[rip+X]; mov [rdx],rax  — alt mov rcx variant
constexpr const char* AOB_GOBJECTS_V6 = "48 8B 0D ?? ?? ?? ?? 48 89 02";
// V7: mov r9,[rip+X]; cdq; movzx edx,dx  — GSpots variant
constexpr const char* AOB_GOBJECTS_V7 = "4C 8B 0D ?? ?? ?? ?? 99 0F B7 D2";
// V8: mov r9,[rip+X]; mov edx,eax; shr edx,10h  — bit shift variant
constexpr const char* AOB_GOBJECTS_V8 = "4C 8B 0D ?? ?? ?? ?? 8B D0 C1 EA 10";
// V9: mov r9,[rip+X]; cdqe; lea rcx,[rax+rax*2]  — extended index
constexpr const char* AOB_GOBJECTS_V9 = "4C 8B 0D ?? ?? ?? ?? 48 98 48 8D 0C 40 49";
// V10: lea rcx,[rip+X]; call; call; mov byte[],1  — Split Fiction (UE5.5+)
//   Needs -0x10 adjustment (points into struct, not base)
constexpr const char* AOB_GOBJECTS_V10 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? C6 05 ?? ?? ?? ?? 01";
// V11: lea reg,[rip+X]; mov r9,rcx; mov [rcx],rax; mov eax,-1  — Little Nightmares 3
constexpr const char* AOB_GOBJECTS_V11 = "48 8D ?? ?? ?? ?? ?? 4C 8B C9 48 89 01 B8 FF FF FF FF";
// V12: mov reg,[rip+X]; mov r8,[rax+rcx*8]; test r8,r8; jz  — FF7 Remake
//   Needs -0x10 adjustment
constexpr const char* AOB_GOBJECTS_V12 = "48 8B ?? ?? ?? ?? ?? 4C 8B 04 C8 4D 85 C0 74 07";
// V13: mov rax,[rip+X]; mov rcx,[rax+rcx*8]; lea rax,[rdx+rdx*2]; jmp+3  — Palworld
constexpr const char* AOB_GOBJECTS_V13 = "48 8B 05 ?? ?? ?? ?? 48 8B 0C C8 4C 8D 04 D1 EB 03";
// AV1: mov rdx,[rip+X]; movsxd r8,r8d; shl r8,4  — Avowed / Obsidian UE5.3
//   X resolves to ObjObjects.Objects (chunk table) = GUObjectArray + 0x10, so needs -0x10.
//   The standard GObjects patterns (incl. patternsleuth's) do NOT match Avowed; this is the
//   chunk-table load inside FUObjectArray::AllocateUObjectIndex (verified unique).
constexpr const char* AOB_GOBJECTS_AV1 = "48 8B 15 ?? ?? ?? ?? 4D 63 C0 49 C1 E0 04";
// AV2: mov rdx,[rip+X]; shr eax,10; lea rcx,[rcx+rcx*4]; shl ecx,2; add rcx,[rdx+rax*8]
//   The GENERIC FUObjectItem chunk-index codegen (idx>>16 = chunk, (idx&0xffff)*0x14 within
//   it — the lea*5 + shl<<2 bakes in the 20-byte item stride). X = GUObjectArray + 0x10 (so
//   -0x10). NOT unique (~10+ identical sites — object access is everywhere) but that is a
//   FEATURE: it is far more resilient to a game patch than AV1's single AllocateUObjectIndex
//   site, and the 20-byte stride math makes a false hit on a standard 24-byte-item UE game
//   essentially impossible. ValidateGObjects picks the real base among the matches.
constexpr const char* AOB_GOBJECTS_AV2 = "48 8B 15 ?? ?? ?? ?? C1 E8 10 48 8D 0C 89 C1 E1 02 48 03 0C C2";

// --- patternsleuth patterns (instrOffset != 0, use TryPatternRIPOffset) ---

// PS1: cmp/cmp/jne; lea rdx; lea rcx,[rip+X]  — instrOffset=23, opcodeLen=3, totalLen=7
constexpr const char* AOB_GOBJECTS_PS1 = "8B 05 ?? ?? ?? ?? 3B 05 ?? ?? ?? ?? 75 ?? 48 8D 15 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ??";
// PS2: jz; lea rcx,[rip+X]; mov byte; call  — instrOffset=2, opcodeLen=3, totalLen=7
constexpr const char* AOB_GOBJECTS_PS2 = "74 ?? 48 8D 0D ?? ?? ?? ?? C6 05 ?? ?? ?? ?? 01 E8";
// PS3: jne; mov; lea rcx,[rip+X]; call; xor r9d  — instrOffset=5, opcodeLen=3, totalLen=7
constexpr const char* AOB_GOBJECTS_PS3 = "75 ?? 48 ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 45 33 C9 4C 89 74 24";
// PS4: test; mov qword; mov eax,-1; lea r11,[rip+X]  — instrOffset=16, opcodeLen=3, totalLen=7
constexpr const char* AOB_GOBJECTS_PS4 = "45 84 C0 48 C7 41 10 00 00 00 00 B8 FF FF FF FF 4C 8D 1D ?? ?? ?? ??";
// PS5: or esi; and eax; mov [rdi+8]; lea rcx,[rip+X]  — instrOffset=12, opcodeLen=3, totalLen=7
constexpr const char* AOB_GOBJECTS_PS5 = "81 CE 00 00 00 02 83 E0 FB 89 47 08 48 8D 0D ?? ?? ?? ??";
// PS6: mov eax,[rip]; sub eax,[rip]; sub eax,[rip+X]  — arithmetic, instrOffset=14, opcodeLen=2, totalLen=6
constexpr const char* AOB_GOBJECTS_PS6 = "8B 05 ?? ?? ?? ?? 2B 05 ?? ?? ?? ?? 2B 05 ?? ?? ?? ??";
// PS7: call; mov eax,[rip]; mov ecx,[rip]; add ecx,[rip+X]  — arithmetic, instrOffset=17, opcodeLen=2, totalLen=6
constexpr const char* AOB_GOBJECTS_PS7 = "E8 ?? ?? ?? ?? 8B 05 ?? ?? ?? ?? 8B 0D ?? ?? ?? ?? 03 0D ?? ?? ?? ??";

// --- RE-UE4SS CustomGameConfigs ---

// RE1: FF7 Rebirth — special: add [rip+X],ecx; dec eax; cmp edx,eax; jge
//   instrOffset=2, resolution: nextInstr(+6) + DerefToInt32(matchAddr+2)
constexpr const char* AOB_GOBJECTS_RE1 = "03 ?? ?? ?? ?? ?? FF C8 3B D0 0F 8D ?? ?? ?? ?? 44 8B";
// RE2: FF7 Remake — mov reg,[rip+X]; mov r8,[rax+rcx*8]; test r8; jz; ?; ?; ?; setz
//   instrOffset=3, needs -0x10 adjustment (same as V12 but slightly different context)
constexpr const char* AOB_GOBJECTS_RE2 = "48 8B ?? ?? ?? ?? ?? 4C 8B 04 C8 4D 85 C0 74 07 ?? ?? ?? 0F 94";
// RE3: Little Nightmares 3 Demo — lea; mov r9,rcx; mov; mov eax,-1; mov [rcx+8]; cmovne; inc; mov; cmp
//   (extended context variant of V11)
constexpr const char* AOB_GOBJECTS_RE3 = "48 8D ?? ?? ?? ?? ?? 4C 8B C9 48 89 01 B8 FF FF FF FF 89 41 08 0F 45 ?? ?? ?? ?? ?? FF C0 89 41 08 3B";

// --- UE4 Dumper.CT patterns (x64) ---

// CT1: mov r8; lea rax; mov [rsi+10h]; mov qword — UE4 Dumper.CT v5+
//   44 8B * * * 48 8D 05 * * * * * * * * * 48 89 71 10
constexpr const char* AOB_GOBJECTS_CT1 = "44 8B ?? ?? ?? 48 8D 05 ?? ?? ?? ?? ?? ?? ?? ?? ?? 48 89 71 10";
// CT2: push rbx; sub rsp,20h; mov rbx,rcx; test rdx; jz; mov
//   40 53 48 83 EC 20 48 8B D9 48 85 D2 74 * 8B — function prologue
constexpr const char* AOB_GOBJECTS_CT2 = "40 53 48 83 EC 20 48 8B D9 48 85 D2 74 ?? 8B";
// CT3: mov r8,[rip+X]; cmp [r8+?]  — 4C 8B 05 * * * * 45 3B 88
constexpr const char* AOB_GOBJECTS_CT3 = "4C 8B 05 ?? ?? ?? ?? 45 3B 88";

// --- UEDumper patterns ---

// UD1: mov rax,[rip+X]; mov rcx,[rax+rcx*8]; lea rax,[rcx+rdx*8]; test rax,rax
constexpr const char* AOB_GOBJECTS_UD1 = "48 8B 05 ?? ?? ?? ?? 48 8B 0C C8 48 8D 04 D1 48 85 C0";

// --- UE 4.2 game analysis patterns (G42 series) ---

// G42_1: lea rax,[GUObjectArray]; xor esi; mov [rcx],rax; mov [rcx+10h],rsi  — UE4.2 constructor init
constexpr const char* AOB_GOBJECTS_G42_1 = "48 8D 05 ?? ?? ?? ?? 33 F6 48 89 01 48 89 71";
// G42_2: lea rcx,[GUObjectArray]; call RemoveUObjectDeleteListener; lea rcx,[rbx+18]; mov rbx  — UE4.2
constexpr const char* AOB_GOBJECTS_G42_2 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 4B 18 48 8B 5C";
// G42_3: lea rcx,[GUObjectArray]; mov r8d,[rsp+?]; mov edx,[rsp+?]; mov [GUObjectAllocator],rax  — UE4.2
constexpr const char* AOB_GOBJECTS_G42_3 = "48 8D 0D ?? ?? ?? ?? 44 8B 44 24 ?? 8B 54 24 ?? 48 89";
// G42_4: lea rcx,[GUObjectArray]; call; lea rcx,[rbp+58]; ... add rsp,40; pop r14; jmp  — UE4.2 long epilogue
constexpr const char* AOB_GOBJECTS_G42_4 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 4D 58 48 8B 5C 24 50 48 8B 6C 24 58 48 8B 74 24 60 48 8B 7C 24 68 48 83 C4 40 41 5E 48 FF 25 ?? ?? ?? ?? 45";

// --- UE 4.27 game analysis patterns (G427 series) ---

// G427_1: mov rax,[ObjObjects.Objects]; sar ecx,?; movsxd rcx,ecx; mov rdx,[rax+rcx*8]  — UE4.27 FEngineLoop::PreInitPostStartupScreen
constexpr const char* AOB_GOBJECTS_G427_1 = "48 8B 05 ?? ?? ?? ?? C1 F9 ?? 48 63 C9 48 8B";
// G427_2: cmp eax,[ObjObjects.NumElements]; jge; cdq; movzx edx,dx; add eax,edx  — UE4.27 FEngineLoop::PreInitPostStartupScreen
//   opcodeLen=2 (3B 05), totalLen=6, adjustment=-0x14 (NumElements at ObjObjects+0x14)
constexpr const char* AOB_GOBJECTS_G427_2 = "3B 05 ?? ?? ?? ?? 7D ?? 99 0F B7 D2 03 C2";
// G427_3: mov rax,[ObjObjects.Objects]; mov rcx,[rax+?*8]; lea r8,[?+rdx*8]; jmp; xor r8d; mov eax,[r8+8]  — UE4.27 FGCObject ctor
constexpr const char* AOB_GOBJECTS_G427_3 = "48 8B 05 ?? ?? ?? ?? ?? 8B 0C ?? ?? 8D 04 ?? EB ?? 45 33 C0 41 8B ?? 08";
// G427_4: mov eax,[ObjLastNonGCIndex]; mov r9d,eax; mov [rcx+8],eax; inc r9d  — UE4.27 TObjectIteratorBase
//   opcodeLen=2 (8B 05), totalLen=6, adjustment=+0x0C (ObjLastNonGCIndex at GUObjectArray+0x04, ObjObjects at +0x10)
constexpr const char* AOB_GOBJECTS_G427_4 = "8B 05 ?? ?? ?? ?? 44 8B C8 89 41 08 41";

// --- Everspace 2 UE 5.3 build patterns (ES53 series) ---

// ES53_1: sub rsp,28; lea rcx,[GUObjectArray]; call FUObjectArray::FUObjectArray; lea rcx,[atexit_fn]; add rsp,28; jmp atexit
//   instrOffset=4 (LEA RCX starts at byte 4), 26 bytes — very specific ctor+atexit pattern
constexpr const char* AOB_GOBJECTS_ES53_1 = "48 83 EC 28 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? 48 83 C4 28 E9";

// --- Satisfactory UE 4.22 patterns (SAT422 series) ---

// SAT422_1: lea rcx,[GUObjectArray]; call CloseDisregardForGC; lea rcx,[rbp+?]; call ~FString; call NotifyRegistrationComplete; call; mov  — FEngineLoop::PreInit
//   34 bytes, very specific 4-CALL chain in engine init sequence
constexpr const char* AOB_GOBJECTS_SAT422_1 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 8D ?? ?? 00 00 E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 89";

// --- Satisfactory UE 4.25 patterns (SAT425 series) ---

// SAT425_1: lea rcx,[GUObjectArray]; mov eax,-1; mov r15d,param; mov [RDI],rcx; mov [RDI+8],eax  — FObjectIterator ctor
constexpr const char* AOB_GOBJECTS_SAT425_1 = "48 8D 0D ?? ?? ?? ?? B8 FF FF FF FF 45 8B ?? 48 89 ?? 89 47 08";
// SAT425_2: lea rcx,[GUObjectArray]; mov r8d,[rsp+?]; mov edx,[rsp+?]; mov [GUObjectAllocator],rax (x3); call  — UObjectBaseInit
//   31 bytes, very specific init sequence
constexpr const char* AOB_GOBJECTS_SAT425_2 = "48 8D 0D ?? ?? ?? ?? 44 8B ?? 24 ?? ?? 00 00 8B ?? 24 ?? ?? 00 00 48 89 05 ?? ?? ?? ?? 48 89";

// --- Satisfactory UE 4.26 patterns (SAT426 series) ---

// SAT426_1: lea rcx,[GUObjectArray]; call RemoveUObjectDeleteListener; test rbx,rbx; jz; mov  — FUObjectAnnotationSparse::RemoveAnnotation
constexpr const char* AOB_GOBJECTS_SAT426_1 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 DB 74 ?? 48";
// SAT426_2: mov ecx,[GUObjectArray]; mov ebx,[GUObjectArray.NumElements]; mov [GUnreachableObjectIndex],r13d; cmp byte; cmovnz  — GatherUnreachableObjects
//   opcodeLen=2 (8B 0D), totalLen=6
constexpr const char* AOB_GOBJECTS_SAT426_2 = "8B 0D ?? ?? ?? ?? 8B 1D ?? ?? ?? ?? 44 89 ?? ?? ?? ?? ?? 80 38 00 41";

// --- Satisfactory UE 5.2 patterns (SAT52 series) ---

// SAT52_1: lea r10,[GUObjectArray]; xor r15d; mov [rcx],r10; mov ecx,-1; mov ebp,param  — TObjectIteratorBase ctor
constexpr const char* AOB_GOBJECTS_SAT52_1 = "4C 8D 15 ?? ?? ?? ?? 45 33 ?? 4C 89 ?? B9 FF FF FF FF 41 8B";
// SAT52_2: lea rcx,[GUObjectArray]; call IsValid; test al; jnz; call ExecCheck  — ~UObjectBase
constexpr const char* AOB_GOBJECTS_SAT52_2 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 75 ?? E8";

// --- Octopath Traveller patterns (OT series) ---

// OT_1: mov edx,edi; lea rcx,[GUObjectArray]; call AllocateObjectPool; mov eax,[MaxObjsNotGC]; test; jle; add [GObj+C]
//   UE4 FUObjectArray::Init — uses LEA RCX (48 8D 0D), not LEA RAX (48 8D 05) like G42 series
//   instrOffset=2 (LEA starts at byte 2), opcodeLen=3, totalLen=7
constexpr const char* AOB_GOBJECTS_OT_1 = "8B D7 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 05 ?? ?? ?? ?? 85 C0 7E ?? 01 05";
// OT_2: generalized OT_1 — wildcards register choices and REX prefix for cross-game UE4 compatibility
//   mov r32,r32; REX lea rcx,[GUObjectArray]; call; mov eax,[rip]; test; jle; add [rip],r32; call
//   instrOffset=2, opcodeLen=3, totalLen=7 (REX at byte 2 is always 48/4C in x64)
constexpr const char* AOB_GOBJECTS_OT_2 = "8B ?? ?? 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 05 ?? ?? ?? ?? 85 ?? 7E ?? 01 ?? ?? ?? ?? ?? E8";

// --- Ghidra cross-game analysis patterns (GH series) ---

// GH_1: UObjectBase::AddObject — and eax,-5; mov [rdi+8]; xor r8d; lea rcx,[GUObjectArray]; mov rdx,rdi; call; test ebx; jz
//   instrOffset=12 (LEA RCX at byte 12), 30 bytes, 22 fixed — cross-game ES/ES2/SAT
constexpr const char* AOB_GOBJECTS_GH_1 = "BA EB 19 83 E0 FB 89 47 08 45 33 C0 48 8D 0D ?? ?? ?? ?? 48 8B D7 E8 ?? ?? ?? ?? 85 DB 74";
// GH_2: UnMarkAllObjects — test esi; jle; mov rdx,rdi; lea rcx,[GUObjectArray]; call; add rsp,B8h
//   instrOffset=12, 31 bytes, 19 fixed — cross-game ES/ES2/SAT
constexpr const char* AOB_GOBJECTS_GH_2 = "F3 85 F6 0F 8E ?? ?? ?? ?? 48 8B D7 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 81 C4 B8 00 00 00";
// GH_3: IncrementalPurgeGarbage — mov rax,[bPurgeComplete]; cmp byte[rax],0; jz; lea rcx,[GUObjectArray]; mov byte[flag],1; call
//   instrOffset=12, 27 bytes, 15 fixed — cross-game ES/ES2/SAT. Extends PS2 with 12-byte leading context.
constexpr const char* AOB_GOBJECTS_GH_3 = "48 8B 05 ?? ?? ?? ?? 80 38 00 74 ?? 48 8D 0D ?? ?? ?? ?? C6 05 ?? ?? ?? 00 01 E8";
// GH_4: FWeakObjectPtr::operator= — mov ebx,ecx; test rdx; jz; mov edx,[rdx+0C]; mov [rcx],edx; lea rcx,[GUObjectArray]; call; mov [rbx+4]; add rsp,20
//   instrOffset=12, 31 bytes, 22 fixed — ES2/SAT
constexpr const char* AOB_GOBJECTS_GH_4 = "8B D9 48 85 D2 74 ?? 8B 52 0C 89 11 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 89 43 04 48 83 C4 20";


// ============================================================
// GNames / FNamePool
// ============================================================

// --- Original patterns (V-series) ---

// V1: lea rsi,[rip+X]; jmp
constexpr const char* AOB_GNAMES_V1 = "48 8D 35 ?? ?? ?? ?? EB";
// V2: lea rcx,[rip+X]; call; mov byte ptr
constexpr const char* AOB_GNAMES_V2 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? C6 05";
// V3: lea rax,[rip+X]; jmp
constexpr const char* AOB_GNAMES_V3 = "48 8D 05 ?? ?? ?? ?? EB";
// V4: lea r8,[rip+X]; jmp   (REX.R variant)
constexpr const char* AOB_GNAMES_V4 = "4C 8D 05 ?? ?? ?? ?? EB";
// V5: lea rcx,[rip+X]; call; mov byte ptr[??],1  — extended context
constexpr const char* AOB_GNAMES_V5 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? C6 05 ?? ?? ?? ?? 01";
// V6: mov rax,[rip+X]; test rax,rax; jnz; mov ecx,0808h  — GSpots UE5+
constexpr const char* AOB_GNAMES_V6 = "48 8B 05 ?? ?? ?? ?? 48 85 C0 75 ?? B9 08 08 00";
// V7: FName ctor call-site — mov r8d,1; lea rcx; call; mov byte — FF7 Rebirth
//   Resolves CALL target, then scans inside for FNamePool refs
constexpr const char* AOB_GNAMES_V7_FNAME_CTOR = "41 B8 01 00 00 00 48 8D 4C 24 ?? E8 ?? ?? ?? ?? C6 44 24";
// V8: lea rax,[rip+X]; jmp 0x13; lea rcx,[rip+Y]; call; mov byte; movaps  — Palworld
//   First LEA resolves to FNamePool.
constexpr const char* AOB_GNAMES_V8 = "48 8D 05 ?? ?? ?? ?? EB 13 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? C6 05 ?? ?? ?? ?? ?? 0F 10";

// --- patternsleuth patterns ---

// PS1: jz+9; lea r8,[rip+X]; jmp; lea rcx; call  — instrOffset=2, opcodeLen=3, totalLen=7
constexpr const char* AOB_GNAMES_PS1 = "74 09 4C 8D 05 ?? ?? ?? ?? EB ?? 48 8D 0D ?? ?? ?? ?? E8";
// PS2: sub rsp,0x20; shr edx,3; lea rbp,[rip+X]  — instrOffset=7, opcodeLen=3, totalLen=7
constexpr const char* AOB_GNAMES_PS2 = "48 83 EC 20 C1 EA 03 48 8D 2D ?? ?? ?? ??";

// --- Dumper-7 pattern ---

// D7_1 — REMOVED in build 2404. It was "48 8D 0D ?? ?? ?? ?? E8" = `lea rcx,[rip+X]; call`,
// THREE literal bytes, i.e. a match on essentially every this-call in the image: 27,001 hits on
// a UE4.20 title, 104,897 on UE4.27, 40,000 on UE5.5 — every one of them validated (several
// SEH-guarded reads each) before the scan could reach the patterns that actually resolve there
// (GNAM_CT3 pri 800 / GNAM_G42_1 pri 840 on 4.20). It was never the sole correct pattern on any
// of the eight binaries in the sweep, and its own comment already recorded that V2/V5 cover the
// same sites with real context.
//   Dumper-7 can afford this pattern because it follows the CALL and checks the callee for
//   InitializeSRWLock + a "ByteProperty" reference; we do not implement that second stage, so
//   for us it was pure cost. If it is ever wanted back, it needs AobResolve::CallFollow plus
//   that callee check — not a re-add of the bare byte string.

// --- UE4 Dumper.CT patterns ---

// CT1: lea rax,[rip+X]; jmp 0x16; lea rcx,[rip+Y]; call  — UE4 Dumper.CT v6+ (UE4.23+)
//   Same as V8 variant but with jmp 0x16 instead of 0x13
constexpr const char* AOB_GNAMES_CT1 = "4C 8D 05 ?? ?? ?? ?? EB 16 48 8D 0D ?? ?? ?? ?? E8";
// CT2 — REMOVED in build 2407. It was
//   "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0 C6"
// which is AOB_GNAMES_UD2 minus its final `05` byte, i.e. the same site with one byte less
// context. Measured over all 26 programs in the sweep the two produced BYTE-IDENTICAL hit
// counts on every single one (0/0, 10/10, 11/11, 15/15, 36/36, 932/932 on FF7R, ...) — there
// is no binary where CT2's extra looseness finds anything UD2 does not. The `C6` it stops on
// is `mov byte ptr`, and the only encoding that ever follows here is `C6 05` (rip-relative),
// which is exactly what UD2 pins. Keeping both cost a scan slot for zero coverage: patterns are
// scanned in batches of 8 and ScanForTarget returns on the first validated hit, so a redundant
// entry can push a genuinely different pattern into an extra full-.text pass.
// To restore: re-add with pattern above at priority 300 (UD2 then moves back to 320).
// CT3: sub rsp,28h; mov rax,[rip+X]; test rax; jnz; mov ecx,0x0808; mov rbx,[rsp+20h]; call
//   — pre-FNamePool (UE4 <4.23), deref pointer
constexpr const char* AOB_GNAMES_CT3 = "48 83 EC 28 48 8B 05 ?? ?? ?? ?? 48 85 C0 75 ?? B9 ?? ?? 00 00 48 89 5C 24 20 E8";
// CT4: ret; ? DB; mov [rip+X],rbx; ?; ?; mov rbx,[rsp+20h]
//   — pre-FNamePool write pattern, instrOffset=5
constexpr const char* AOB_GNAMES_CT4 = "C3 ?? DB 48 89 1D ?? ?? ?? ?? ?? ?? 48 8B 5C 24 20";

// --- UEDumper example patterns ---

// UD1 — REMOVED in build 2407. It was
//   "E8 ?? ?? ?? ?? 83 7D E8 00 4C 8D 05 ?? ?? ?? ?? 48 8D 15 ?? ?? ?? ??"
// i.e. `call; cmp dword [rbp-0x18],0; lea r8,[rip+X]; lea rdx,[rip+Y]`, and it was DEAD CODE:
// declared here since the pattern DB was written, but never referenced by GNAMES_PATTERNS[] or
// anything else, so it has never been scanned for in any build. The suspicion about it was
// well founded — `cmp [rbp-0x18], 0` pins an exact frame-pointer-relative stack slot, which is
// a property of one compilation of one function in one game, not of UE. UEDumper can afford it
// (its README calls the entry an example to be re-derived per game); a cross-game scanner
// cannot. Deleted rather than wired up: adding it would have cost a scan slot for a pattern
// that cannot generalise.
// UD2: lea rcx,[rip+X]; call FNamePool::FNamePool; mov r8,rax; mov byte[bInit]  — the lazy-init
//   head shared by the FName accessors. NOTE this is the SAME SITE the old GNAM_CT2 matched;
//   see the removal note in GNAMES_PATTERNS[].
constexpr const char* AOB_GNAMES_UD2 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0 C6 05";

// --- UE 4.2 game analysis patterns (G42 series) ---

// G42_1: mov rax,[Names]; test rax; jnz; mov ecx,0x408  — UE4.2 pre-FNamePool (TStaticIndirectArrayThreadSafeRead)
constexpr const char* AOB_GNAMES_G42_1 = "48 8B 05 ?? ?? ?? ?? 48 85 C0 75 ?? B9 ?? ?? ?? ?? 48";

// --- Satisfactory UE 4.22 patterns (SAT422 series) ---

// SAT422_1: FName::GetNames — the pre-FNamePool (TStaticIndirectArrayThreadSafeRead) lazy-init
//   head, WITH the game-thread assertion that UE 4.22 inserts and 4.20 does not:
//     mov rax,[Names]; test rax,rax; jnz(near) done;
//     cmp byte[GIsGameThreadIdInitialized],al; mov [rsp+0x20],rbx; jz skip;
//     call [__imp_GetCurrentThreadId]
//   18 literal bytes.
//
//   CORRECTED in build 2407. The previous form omitted the `48 85 C0` (test rax,rax) between
//   the load and the jump:
//     "48 8B 05 ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? 38 05 ?? ?? ?? ?? 48 89"
//   MSVC cannot emit `mov`+`jnz` with no flag-setting instruction between them, so that string
//   was unmatchable by construction — and the sweep confirms it: ZERO hits across all 26
//   programs, including the very Satisfactory UE 4.22 build it is named after. Re-derived here
//   from that build's PDB (FName::GetNames @ 0x140BCEBF0, load at +4).
//   Consequence of the old form being dead: UE 4.22 had to fall through to GNAM_CT4 — a
//   `ret; mov [rip],rbx` WRITE pattern — which reaches the right answer only after rejecting a
//   decoy. This restores a direct, purpose-built anchor for 4.22.
constexpr const char* AOB_GNAMES_SAT422_1 =
    "48 8B 05 ?? ?? ?? ?? 48 85 C0 0F 85 ?? ?? ?? ?? 38 05 ?? ?? ?? ?? 48 89 5C 24 20 74 ?? FF 15";

// --- Satisfactory UE 4.25 patterns (SAT425 series) ---

// SAT425_1: cmp [bNamePoolInitialized],0; mov [rsp+?],edi; mov [rsp+?],r8d; jz; lea r8,[NamePoolData]  — FName::AppendString
//   instrOffset=18 (LEA R8 at byte 18), 21 bytes
constexpr const char* AOB_GNAMES_SAT425_1 = "80 3D ?? ?? ?? ?? 00 89 7C 24 ?? 44 89 44 24 ?? 74 ?? 4C 8D 05";
// SAT425_2: lea rax,[NamePoolData]; mov eax,[rax+8]; inc eax; shl eax,11h; add rsp,28; ret  — FName::GetNameEntryMemorySize
constexpr const char* AOB_GNAMES_SAT425_2 = "48 8D 05 ?? ?? ?? ?? 8B 40 08 FF C0 C1 E0 11 48 83 C4 28 C3";
// SAT425_3: lea rax,[NamePoolData]; jmp; lea rcx,[NamePoolData]; call FNamePool::FNamePool; mov byte  — FName::GetNumAnsiNames
//   Generalized V8 with EB ?? (any JMP offset) instead of EB 13
constexpr const char* AOB_GNAMES_SAT425_3 = "48 8D 05 ?? ?? ?? ?? EB ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? C6 05";

// --- Satisfactory UE 5.2 patterns (SAT52 series) ---

// SAT52_1: lea rdx,[NamePoolData]; jmp; lea rcx,[NamePoolData]; ... mov rdx,rax  — FName::ToString init dual-LEA
//   Both LEAs point to NamePoolData. Use first LEA (offset 0) for resolution
constexpr const char* AOB_GNAMES_SAT52_1 = "48 8D 15 ?? ?? ?? ?? EB ?? 48 8D 0D ?? ?? ?? ?? 48 8B";

// --- Everspace 2 UE 5.3 build patterns (ES53 series) ---

// ES53_1: lea rcx,[FNamePool]; call FNamePool::FNamePool; mov rdx,rax; mov byte[],1  — FName::ToString init path
//   Like V5 but has extra MOV RDX,RAX (48 8B D0) between CALL and MOV byte
constexpr const char* AOB_GNAMES_ES53_1 = "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B D0 C6 05 ?? ?? ?? ?? 01";

// --- Ghidra cross-game analysis patterns (GH series) ---

// GH_1: ReserveNameBatch — mov [rsp+18],esi; push rdi; sub rsp,20; shr edx,3; lea rbp,[NamePoolData]; dec edx; mov ebx,ecx; mov rdi,magic_const
//   instrOffset=12 (LEA RBP at byte 12), 31 bytes, 27 fixed — cross-game ES/ES2/SAT. Best new GNames pattern.
//   Contains unique integer division constant 0xCCCCCCCCCCCC (compiler-generated magic number).
constexpr const char* AOB_GNAMES_GH_1 = "89 74 24 18 57 48 83 EC 20 C1 EA 03 48 8D 2D ?? ?? ?? ?? FF CA 8B D9 48 BF CD CC CC CC CC CC";
// GH_2: FNameEntryId::FromValidEName — sub rsp,20; cmp byte[bInitialized],0; mov rbx,rcx; lea rcx,[NamePoolData]; movsxd rdi,edx; jnz; call
//   instrOffset=12, 31 bytes, 19 fixed — cross-game ES/ES2/SAT
constexpr const char* AOB_GNAMES_GH_2 = "EC 20 80 3D ?? ?? ?? 00 00 48 8B D9 48 8D 0D ?? ?? ?? ?? 48 63 FA 75 ?? E8 ?? ?? ?? ?? 48 8B";


// ============================================================
// GWorld
// ============================================================

// V1: mov rax,[rip+X]; cmp rcx,rax; cmovz rax,[rip+Y]
constexpr const char* AOB_GWORLD_V1 = "48 8B 05 ?? ?? ?? ?? 48 3B C8 48 0F 44 05";
// V2: mov [rip+X],rax; test rax,rax; jz
constexpr const char* AOB_GWORLD_V2 = "48 89 05 ?? ?? ?? ?? 48 85 C0 74";
// V3: mov rbx,[rip+X]; test rbx,rbx
constexpr const char* AOB_GWORLD_V3 = "48 8B 1D ?? ?? ?? ?? 48 85 DB";
// V4: mov rdi,[rip+X]; test rdi,rdi
constexpr const char* AOB_GWORLD_V4 = "48 8B 3D ?? ?? ?? ?? 48 85 FF";
// V5: cmp [rip+X],rax; je
constexpr const char* AOB_GWORLD_V5 = "48 39 05 ?? ?? ?? ?? 74";
// V6: mov [rip+X],rbx; call  (GWorld write after UWorld creation)
constexpr const char* AOB_GWORLD_V6 = "48 89 1D ?? ?? ?? ?? E8";
// V7: mov rbx,[rip+X]; test rbx,rbx; jz 0x33; mov r8b  — Palworld
constexpr const char* AOB_GWORLD_V7 = "48 8B 1D ?? ?? ?? ?? 48 85 DB 74 33 41 B0";


// ============================================================
// MSVC Mangled Symbol Exports
// ============================================================
// Many retail UE games (especially modular builds) export these symbols.
// GetProcAddress resolves them in O(1) before any AOB scan.
// Source: RE-UE4SS (Satisfactory, Returnal use these exclusively)

constexpr const char* EXPORT_GOBJECTARRAY     = "?GUObjectArray@@3VFUObjectArray@@A";
constexpr const char* EXPORT_FNAME_CTOR       = "??0FName@@QEAA@PEB_WW4EFindName@@@Z";
constexpr const char* EXPORT_FNAME_TOSTRING   = "?ToString@FName@@QEBAXAEAVFString@@@Z";
constexpr const char* EXPORT_FNAME_CTOR_CHAR  = "??0FName@@QEAA@PEBDW4EFindName@@@Z";
constexpr const char* EXPORT_GWORLD           = "?GWorld@@3VUWorldProxy@@A";
// GEngine is exported by the Engine module in EVERY modular build we have binaries for —
// verified with tools/pe/pe_imports_exports.py against Satisfactory's
// FactoryGame-Engine-Win64-Shipping.dll on BOTH UE 4.26 (ordinal 13690) and UE 5.2
// (ordinal 19170), sitting directly beside `?GWorld@@3VUWorldProxy@@A` in the export table.
// We had a symbol export for GObjects and GWorld but simply never added one for GEngine, so a
// modular title paid for a full AOB sweep to find something GetProcAddress returns in O(1).
// The exported address IS &GEngine (the slot), which is exactly what AobTarget::GEngine wants.
// Costs nothing on a monolithic build: GetProcAddress just returns null and the scan proceeds.
constexpr const char* EXPORT_GENGINE          = "?GEngine@@3PEAVUEngine@@EA";


// ============================================================
// New patterns: Everspace 2 (UE 5.5)
// ============================================================

// --- GWorld (ES2) ---
// ES2_1: mov rax,[GWorld]; lea rdx,[rbp+1F8]; mov rcx,[rax+18]; mov [rbp+48],rcx; lea rcx,[rbp+48]; call
constexpr const char* AOB_GWORLD_ES2_1 = "48 8B 05 ?? ?? ?? ?? 48 8D 95 F8 01 00 00 48 8B 48 18 48 89 4D 48 48 8D 4D 48 E8";
// ES2_2: cmovz r13,[GWorld]; mov r10,[rax+358]; mov rax,[rsi]; mov [rbp-50],rax; mov rax,[rsi+8]
//   CMOVZ: opcodeLen=4 (4C 0F 44 2D), totalLen=8
constexpr const char* AOB_GWORLD_ES2_2 = "4C 0F 44 2D ?? ?? ?? ?? 4C 8B 90 58 03 00 00 48 8B 06 48 89 45 B0 48 8B 46 08";
// ES2_3: mov rax,[GWorld]; mov r8,rbx; mov rcx,[r8]; cmp [rcx+2C0],rax; jne
constexpr const char* AOB_GWORLD_ES2_3 = "48 8B 05 ?? ?? ?? ?? 4C 8B C3 49 8B 08 48 39 81 C0 02 00 00 0F 85 ?? ?? ?? ??";
// ES2_4: cmp [GWorld],rbx; jnz+8; and qword [GWorld],0; mov rcx,[rbx+440]; test rcx,rcx
constexpr const char* AOB_GWORLD_ES2_4 = "48 39 1D ?? ?? ?? ?? 75 08 48 83 25 ?? ?? ?? ?? 00 48 8B 8B 40 04 00 00 48 85 C9";
// ES2_5: mov rdx,[GWorld]; lea rcx,[rsi+28]; mov r9,rax; call r12; add rdi,10; sub r14,1
constexpr const char* AOB_GWORLD_ES2_5 = "48 8B 15 ?? ?? ?? ?? 48 8D 4E 28 4C 8B C8 41 FF D4 48 83 C7 10 49 83 EE 01";
// ES2_6: mov rdx,[GWorld]; lea rcx,[rdi+28]; cmovne r8,[rsp+20]; mov r9,rax; call rbx; mov rcx,[rsp+20]
constexpr const char* AOB_GWORLD_ES2_6 = "48 8B 15 ?? ?? ?? ?? 48 8D 4F 28 4C 0F 45 44 24 20 4C 8B C8 FF D3 48 8B 4C 24";

// --- GNames (ES2) ---
// ES2_1: lea rdx,[NamePoolData]; mov ecx,ebx; movzx eax,bx; mov [rsp+3C],eax; shr ecx,10; mov [rsp+38],ecx; mov rax,[rsp+38]
constexpr const char* AOB_GNAMES_ES2_1 = "48 8D 15 ?? ?? ?? ?? 8B CB 0F B7 C3 89 44 24 3C C1 E9 10 89 4C 24 ?? 48 8B";

// --- GObjects (ES2) ---
// ES2_1: lea rcx,[GUObjectArray]; mov esi,r9d; mov ebp,r8d; mov r15,rdx; call [rip+X]
constexpr const char* AOB_GOBJECTS_ES2_1 = "48 8D 0D ?? ?? ?? ?? 41 8B F1 41 8B E8 4C 8B FA FF 15";


// ============================================================
// New patterns: SatisfFactory (UE 5.3, modular build — in DLLs)
// ============================================================

// --- GWorld (SF, in Game-Engine-Win64-Shipping.DLL) ---
// SF_1: mov rax,[GWorld]; cmp [rcx+2C0],rax  — UGameEngine::Tick
constexpr const char* AOB_GWORLD_SF_1 = "48 8B 05 ?? ?? ?? ?? 48 39 81 C0 02 00 00";
// SF_2: mov rax,[GWorld]; lea r8,[rsp+38]; lea rdx,[rsp+20]; mov [rsp+38],rax  — FAudioDeviceManager::CreateMainAudioDevice
constexpr const char* AOB_GWORLD_SF_2 = "48 8B 05 ?? ?? ?? ?? 4C 8D 44 24 ?? 48 8D 54 24 ?? 48 89 44";
// SF_3: cmp [GWorld],rdi; jne; mov [GWorld],rbx; call  — UWorld::FinishDestroy
constexpr const char* AOB_GWORLD_SF_3 = "48 39 3D ?? ?? ?? ?? 75 ?? 48 89 1D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48";
// SF_4: mov rdi,[GWorld]; mov rbx,[rsp+70]; mov rax,rdi  — UEngine::GetWorldFromContextObject
constexpr const char* AOB_GWORLD_SF_4 = "48 8B 3D ?? ?? ?? ?? 48 8B 5C 24 70 48 8B";
// SF_5: mov rax,[GWorld]; mov ebx,edx; mov rdi,rcx; lea rdx,[r11-38]  — FMallocLeakReporter::WriteReports
constexpr const char* AOB_GWORLD_SF_5 = "48 8B 05 ?? ?? ?? ?? 8B DA 48 8B F9 49 8D";

// --- GNames (SF, in GameSteam-Core-Win64-Shipping.DLL) ---
// SF_1: lea r8,[NamePoolData]; jmp; lea rcx,[NamePoolData]; call FNamePool::FNamePool; mov r8,rax
constexpr const char* AOB_GNAMES_SF_1 = "4C 8D 05 ?? ?? ?? ?? EB ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C 8B C0";
// SF_2: lea rax,[NamePoolData]; movups [rsp+38],xmm0; shl rdi,6; add rdi,rax
constexpr const char* AOB_GNAMES_SF_2 = "48 8D 05 ?? ?? ?? ?? 0F 11 44 24 38 48 C1";
// SF_3: lea rcx,[NamePoolData]; mov edi,edx; jne; call FNamePool::FNamePool
constexpr const char* AOB_GNAMES_SF_3 = "48 8D 0D ?? ?? ?? ?? 8B FA 75 ?? E8 ?? ?? ?? ?? 48";

// --- GObjects (SF, via _imp_ import table in EXE) ---
// SF_1: mov rax,[_imp_GUObjectArray]; cmp [rax+0C],sil; je; lea rdx
constexpr const char* AOB_GOBJECTS_SF_1 = "48 8B 05 ?? ?? ?? ?? 40 38 70 0C 74 2E 48 8D 15";


// ============================================================
// New patterns: TQ2
// ============================================================

// --- GWorld (TQ2) ---
// TQ_1: mov rbx,[GWorld]; test rbx,rbx; jz; mov r8b,1; xor edx,edx; mov rcx,rbx; call  — extended V3
constexpr const char* AOB_GWORLD_TQ_1 = "48 8B 1D ?? ?? ?? ?? 48 85 ?? 74 ?? 41 B0 01 33 ?? ?? 8B ?? E8";
// TQ_2: mov rdx,[GWorld]; mov rcx,[GWorld_related]; call; jmp; mov rax,r15; cmp byte [rsi],1
constexpr const char* AOB_GWORLD_TQ_2 = "48 8B 15 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? EB 03 ?? 8B ?? 80 ?? 01";
// TQ_3: ?? prefix; mov rax,[GWorld]; mov rsi,rcx; movaps [r11-38],xmm8; movaps xmm8,xmm1; test rax,rax; je
//   Wildcard-prefixed, RIP at offset 3
constexpr const char* AOB_GWORLD_TQ_3 = "?? 8B 05 ?? ?? ?? ?? ?? 8B ?? ?? 0F 29 43 ?? 44 0F 28 C1 ?? 85 ?? 0F";
// TQ_4: ?? prefix; mov [GWorld],rcx; test rsi,rsi; jz; mov rax,[rsi]; mov rcx,rsi; call [rax+E0]
//   Wildcard-prefixed write pattern, RIP at offset 3
constexpr const char* AOB_GWORLD_TQ_4 = "?? 89 0D ?? ?? ?? ?? ?? 85 ?? 74 ?? 48 8B 06 ?? 8B ?? FF 90 ?? 00 00";

// --- UE 4.2 game analysis patterns (G42 series) ---

// G42_1: mov rbx,[GWorld]; mov rsi,[rbp+28]; call GetGlobalLogSingleton  — UE4.2
constexpr const char* AOB_GWORLD_G42_1 = "48 8B 1D ?? ?? ?? ?? 48 8B 75 ?? E8";
// G42_2: mov rbx,[GWorld]; test rbx; jz; mov r8b,1  — UE4.2 (wildcard jz offset)
constexpr const char* AOB_GWORLD_G42_2 = "48 8B 1D ?? ?? ?? ?? 48 85 DB 74 ?? 41 B0 01";
// G42_3: mov rax,[rax+30]; test rax; jnz; mov rax,[GWorld]; ret  — UE4.2 fallback return
//   RIP instruction starts at offset 9 (48 8B 05)
constexpr const char* AOB_GWORLD_G42_3 = "48 8B 40 30 48 85 C0 75 ?? 48 8B 05 ?? ?? ?? ?? C3";
// G42_4: mov rdi,[GWorld]; mov rbx,[rsp+60]  — UE4.2 epilogue context
constexpr const char* AOB_GWORLD_G42_4 = "48 8B 3D ?? ?? ?? ?? 48 8B 5C 24 60";
// G42_5: mov rax,[GWorld]; mov rbx,rcx; lea rcx,[rbp+20]; mov rdx,[rax+18]  — UE4.2 extended
constexpr const char* AOB_GWORLD_G42_5 = "48 8B 05 ?? ?? ?? ?? 48 8B D9 48 8D 4D 20 48";

// --- UE 4.27 game analysis patterns (G427 series) ---

// G427_1: mov rbx,[GWorld]; test rbx; jz; ??;??;01; xor edx; mov rcx,rbx  — UE4.27 FEngineLoop::Tick
//   Extended version of G42_2 with more trailing context and wildcarded MOV R8B encoding
constexpr const char* AOB_GWORLD_G427_1 = "48 8B 1D ?? ?? ?? ?? 48 85 DB 74 ?? ?? ?? 01 33 D2 48 8B CB";
// G427_2: mov rdi,[GWorld]; mov rbx,[rsp+?]; mov rax,rdi; 48  — UE4.27 UEngine::GetWorldFromContextObject
//   Stack offset wildcarded (varies: 0x50, 0x60, 0x70)
constexpr const char* AOB_GWORLD_G427_2 = "48 8B 3D ?? ?? ?? ?? 48 8B 5C 24 ?? 48 8B C7 48";
// G427_3: mov rdi,[R?+?]; mov r8,rsi; mov rax,[GWorld]; mov rdx,rdi  — UE4.27 UGameEngine::Tick (R8-R15 src)
//   instrOffset=10 (0x0A): RIP instruction 48 8B 05 starts at byte offset 10
constexpr const char* AOB_GWORLD_G427_3 = "49 8B ?? ?? ?? ?? ?? 4C 8B C6 48 8B 05 ?? ?? ?? ?? 48 8B D7";
// G427_4: mov rdi,[R?+?]; mov r8,rsi; mov rax,[GWorld]; mov rdx,rdi  — UE4.27 UGameEngine::Tick (RAX-RDI src)
//   instrOffset=10 (0x0A): RIP instruction 48 8B 05 starts at byte offset 10
constexpr const char* AOB_GWORLD_G427_4 = "48 8B ?? ?? ?? ?? ?? 4C 8B C6 48 8B 05 ?? ?? ?? ?? 48 8B D7";
// G427_5: mov rax,[GWorld]; cmp rax,rbx; cmovz rax,rsi; mov [GWorld],rax  — UE4.27 UWorld::FinishDestroy
//   Uses CMP RAX,RBX (48 3B C3) vs V1's CMP RAX,RCX (48 3B C8)
constexpr const char* AOB_GWORLD_G427_5 = "48 8B 05 ?? ?? ?? ?? 48 3B C3 ?? 0F 44 ?? 48 89 05";

// --- Satisfactory UE 4.22 patterns (SAT422 series) ---

// SAT422_1: mov rax,[GWorld]; mov r??d,edx; mov rbx,rcx; lea rdx,[rbp+?]; 48  — FMallocLeakReporter::WriteReports
//   UE4.22 version of SF_5 (different register encoding: 44 8B vs 8B DA)
constexpr const char* AOB_GWORLD_SAT422_1 = "48 8B 05 ?? ?? ?? ?? 44 8B ?? 48 8B D9 48 8D 55 ?? 48";
// SAT422_2: mov [GWorld],rcx; test rcx,rcx; jz(near); mov ebx,[rcx+0Ch]; test ebx,ebx  — SetGlobalWorld
//   Canonical GWorld setter with null check + ObjectIndex read. Write pattern.
constexpr const char* AOB_GWORLD_SAT422_2 = "48 89 0D ?? ?? ?? ?? 48 85 C9 0F 84 ?? ?? ?? ?? 8B 59 0C 85 DB";

// --- Satisfactory UE 4.25 patterns (SAT425 series) ---

// SAT425_1: cmp rcx,[GWorld]; jz; inc ebx; add r14,8; cmp ebx,[r12+0xC40]  — UGameEngine::Tick
constexpr const char* AOB_GWORLD_SAT425_1 = "48 3B 0D ?? ?? ?? ?? 74 ?? FF C3 49 83 ?? 08 41 3B";
// SAT425_2: mov [GWorld],rcx; mov rax,gs:[TLS]; mov ecx,[_tls_index]; mov edx,4  — UGameEngine::Tick write + TLS
constexpr const char* AOB_GWORLD_SAT425_2 = "48 89 0D ?? ?? ?? ?? 65 48 8B 04 25 ?? ?? ?? ?? 8B 0D";
// SAT425_3: mov [GWorld],rax; mov rcx,[r15+88h]; test byte; jnz  — write + context
constexpr const char* AOB_GWORLD_SAT425_3 = "48 89 05 ?? ?? ?? ?? 49 8B 8F ?? ?? ?? 00 F6 81 ?? ?? ?? 00 ?? 75";

// --- Everspace 2 UE 5.3 build patterns (ES53 series) ---

// ES53_1: mov [GWorld],rax; movaps xmm2,xmm6; mov rax,[r12]; mov rdx,r15  — UGameEngine::Tick write
//   Write pattern: gworldAllowNull=true
constexpr const char* AOB_GWORLD_ES53_1 = "48 89 05 ?? ?? ?? ?? 0F 28 ?? 49 8B 04 24 49 8B D7";
// ES53_2: mov [GWorld],rcx; test rsi,rsi; jz; mov rax,[rsi]; mov rcx,rsi(?); call [rax+E0]
//   Write pattern with RCX register: gworldAllowNull=true
constexpr const char* AOB_GWORLD_ES53_2 = "48 89 0D ?? ?? ?? ?? 48 85 F6 74 ?? 48 8B 06 48 ?? ?? FF";

// --- Satisfactory UE 4.26 patterns (SAT426 series) ---

// SAT426_1: mov rax,[GWorld]; mov rcx,[r15+rdx]; cmp [rcx+??],rax; jz; inc edi  — UGameEngine::Tick
constexpr const char* AOB_GWORLD_SAT426_1 = "48 8B 05 ?? ?? ?? ?? 49 8B 0C ?? 48 39 81 ?? ?? ?? 00 74 ?? FF";
// SAT426_2: mov [GWorld],rax; call FTickTaskManager::Get; mov rdx,[rbx+?]; mov rcx,rax  — UWorld::FinishDestroy
//   Write pattern: gworldAllowNull=true
constexpr const char* AOB_GWORLD_SAT426_2 = "48 89 05 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 93 ?? ?? ?? 00 48";

// --- Satisfactory UE 5.2 patterns (SAT52 series) ---

// SAT52_1: mov rcx,[GWorld]; test rcx; jz; lea rdx,[rbx+A0]; call UWorld::SetAudioDevice  — FAudioDeviceManager::CreateMainAudioDevice
constexpr const char* AOB_GWORLD_SAT52_1 = "48 8B 0D ?? ?? ?? ?? 48 85 C9 74 ?? 48 8D 93 ?? ?? ?? 00 E8";
// SAT52_2: mov [GWorld],rcx; test r14; jz; mov rax,[r14]; mov rcx,r14; call [rax+E0]  — UGameEngine::Tick
//   Write pattern: gworldAllowNull=true
constexpr const char* AOB_GWORLD_SAT52_2 = "48 89 0D ?? ?? ?? ?? 4D 85 ?? 74 ?? 49 8B ?? 49 8B ?? FF 90 ?? ?? 00 00";

// --- Ghidra cross-game analysis patterns (GH series) ---

// GH_1: FMallocLeakReporter::WriteReports — mov [rsp+?],edi; push rbp; mov rbp,rsp; sub rsp,?; mov rax,[GWorld]; mov rbx,rcx; lea rcx,[rbp+10]; mov rdx,[rax+18]
//   instrOffset=12, 31 bytes, 25 fixed — cross-game ES/ES2/SAT. Best new GWorld pattern.
constexpr const char* AOB_GWORLD_GH_1 = "89 7C 24 ?? 55 48 8B EC 48 83 EC ?? 48 8B 05 ?? ?? ?? ?? 48 8B D9 48 8D 4D 10 48 8B 50 18 48";
// GH_2: FUMGViewportClient::GetWorld — mov rax,[rax+30]; test rax; jnz; mov rax,[GWorld]; ret; mov [rsp+10],rbx; push rsi; sub rsp,20
//   instrOffset=9, 28 bytes, 23 fixed — cross-game ES/ES2/SAT. Extends G42_3 with trailing context.
constexpr const char* AOB_GWORLD_GH_2 = "48 8B 40 30 48 85 C0 75 ?? 48 8B 05 ?? ?? ?? ?? C3 48 89 5C 24 10 56 48 83 EC 20 48";
// GH_3: UEngine::GetWorldFromContextObject — call; cmp byte[rsp+58],0; jnz; mov rdi,[GWorld]; mov rbx,[rsp+60]; mov rax,rdi; mov rdi,[rsp+?]
//   instrOffset=12, 31 bytes, 22 fixed — cross-game ES/ES2/SAT. Extends SF_4/G427_2.
constexpr const char* AOB_GWORLD_GH_3 = "E8 ?? ?? ?? ?? 80 7C 24 58 00 75 ?? 48 8B 3D ?? ?? ?? ?? 48 8B 5C 24 60 48 8B C7 48 8B 7C 24";
// GH_4: FEngineLoop::Tick — xorps xmm1,xmm1; ucomiss xmm0,xmm1; jz; mov rbx,[GWorld]; test rbx; jz; mov r8b,1; xor edx
//   instrOffset=8, 27 bytes, 21 fixed — cross-game ES/ES2/SAT. Unique XORPS+UCOMISS prefix.
constexpr const char* AOB_GWORLD_GH_4 = "0F 57 C9 0F 2E C1 74 ?? 48 8B 1D ?? ?? ?? ?? 48 85 DB 74 ?? 41 B0 01 33 D2 48 8B";


// ============================================================
// New patterns: Solarpunk (UE 5.7, rokaplay — full PDB)
// ============================================================
// UE 5.7's MSVC codegen inserts an extra load / picks different registers around
// the GWorld access, so ALL of the Tier-1 (100–290) UE5 GWorld patterns (ES2_1-6,
// SF_1, GH_1/2, TQ_1/2, V1) get ZERO hits on this build. The scan then reaches the
// generic GWLD_SF_2 (Tier 2, pri 300), which matched a single DECOY .data global and
// passed the (intentionally loose) ValidateGWorldBasic → wrong GWorld. These four
// patterns re-anchor GWorld at the TOP of Tier 1 (pri 100–160, before every pattern
// that misses/mis-fires). Each was verified to hit ONLY the real GWorld slot (0
// decoys) across the whole .text image.

// SP57_1: mov rax,[GWorld]; mov rcx,[rbx+rax]; cmp [rcx+2C0],rax; jz  — UGameEngine::Tick
//   Loosened GWLD_SF_1: tolerates the inserted `mov rcx,[rbx+rax]` before the
//   `cmp [rcx+0x2C0],rax` world-compare. `4?` pins that byte to a REX prefix
//   (0x40-0x4F) via nibble wildcard — tighter than `??`. 0x2C0 seen across UE5.4-5.7.
constexpr const char* AOB_GWORLD_SP57_1 = "48 8B 05 ?? ?? ?? ?? 4? 8B ?? ?? 48 39 81 C0 02 00 00";
// SP57_2: mov rax,[GWorld]; mov rsi,rcx; lea rcx,[rbp+10]; mov rdx,[rax+18]  — FMallocLeakReporter::WriteReports
//   Same function as GWLD_GH_1 but UE5.7 uses `mov rsi,rcx` (48 8B F1) not `mov rbx,rcx`.
constexpr const char* AOB_GWORLD_SP57_2 = "48 8B 05 ?? ?? ?? ?? 48 8B F1 48 8D 4D 10 48 8B 50 18";
// SP57_3: mov rdi,[GWorld]; jmp; test rdi; jne; cmp ebx,1; jne  — UEngine::GetWorldFromContextObject
constexpr const char* AOB_GWORLD_SP57_3 = "48 8B 3D ?? ?? ?? ?? EB ?? 48 85 FF 75 ?? 83 FB 01 75";
// SP57_4: mov rax,[GWorld]; mov rdi,[rax+298]; test rdi; jz  — UActorComponent::On(Create|Destroy)PhysicsState
//   0x298 is a UWorld member offset — UE5.7-specific, so ordered LAST of the four.
constexpr const char* AOB_GWORLD_SP57_4 = "48 8B 05 ?? ?? ?? ?? 48 8B B8 98 02 00 00 48 85 FF 74";


// ============================================================
// FSparseDelegateStorage::SparseDelegates (UE 4.23+)
// ============================================================
// The static TMap<UObjectBase*, TMap<FName, TSharedPtr<TMulticastScriptDelegate>>>
// that backs every MulticastSparseDelegateProperty. Field on a UObject only
// stores `FSparseDelegate { uint8 bIsBound; }` (1-8 bytes); actual binding
// list lives in this global. Resolving its address lets the walker enumerate
// per-(owner, propertyName) FScriptDelegate bindings.
//
// Cross-version availability: UE 4.23 introduced sparse delegates. The outer TMap is
// keyed by a raw `UObjectBase const*` on UE 5.x AND on UE 4.27 — PDB-verified on
// DropIn 4.27.2, and vendor/UnrealEngine 5.8 declares it identically. The older note
// here ("UE 4.23-4.27 used FObjectKey, 16 bytes") was wrong on both counts: FObjectKey
// is 8 bytes ({int32 ObjectIndex; int32 ObjectSerialNumber}) and is not used as this
// key at 4.27. 4.23-4.26 remain unverified, so Aura's walker probes the live key shape
// instead of gating on a version number.

// ES2_1: NotifyUObjectDeleted middle — lea rcx,[crit]; call [EnterCriticalSection];
//        mov rdx,r??; lea rcx,[SparseDelegates]; call TSet::Remove; mov eax,[SparseDelegates+8]
//        Twin-reference (lea+mov of same static) makes false-positives near-zero.
//        instrOffset=16, 29 bytes; the `?? ?? ??` after critical-section call
//        is the 3-byte mov rdx,rXX (param register varies by build).
//
//        Cross-version validated:
//          ES2 (UE 5.4, bCasePreservingName=false) → SparseDelegates @ +9AA5F10
//          TQ2 (UE 5.7, bCasePreservingName=true ) → SparseDelegates @ +D46D170
//        Effectively universal across UE 5.x — same pattern, different layout
//        branches handled by Aura::WalkSparseDelegateBindings (FName=8 vs 16).
constexpr const char* AOB_SPARSE_ES2_1 =
    "48 8D 0D ?? ?? ?? ?? FF 15 ?? ?? ?? ?? 48 8B ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 05";

// SP57_1: mov rdx,[SparseDelegates]; movsxd rax,r?d; lea rcx,[rax+rax*2]; shl rcx,5;
//         cmp [rcx+r?],r?; jz  — the TSet<...>::Find/FindOrAdd/EmplaceByHash element-index
//         math (element stride 0x60 = *3<<5). SPARSE_ES2_1 (NotifyUObjectDeleted) does NOT
//         match this build. Verified: matches the 3 always-present hot accessors; the two
//         same-stride decoys resolve to a different global but sit at HIGHER addresses, so
//         the real (lower) sites validate first. RipDirect -> the TSet object base.
constexpr const char* AOB_SPARSE_SP57_1 =
    "48 8B 15 ?? ?? ?? ?? ?? 63 ?? 48 8D 0C 40 48 C1 E1 05 4C 39 ?? 11 74";
// SP57_2: mov r8,[SparseDelegates]; movsxd rax,ebx; lea rdx,[rax+rax*2]; shl rdx,5;
//         cmp [r8+rax],r11; jz  — TSet::Remove (r8 variant). Verified UNIQUE (0 decoys).
constexpr const char* AOB_SPARSE_SP57_2 =
    "4C 8B 05 ?? ?? ?? ?? 48 63 C3 48 8D 14 40 48 C1 E2 05 4E 39 1C 02 74";

// --- DI427: UE 4.27.2 sparse-delegate accessors (DropIn, PDB-verified) -------
// The 4.27 element math is identical in SHAPE to UE5.7 (stride 0x60 = *3 << 5) but
// MSVC picks different registers, which is why SPARSE_SP57_1/2 both get 0 hits here.
// Both of the patterns below are UNIQUE-OK on DropIn and 0-hit on Solarpunk/Avowed.
//
// TRAP worth recording: the obvious "make it register-agnostic with nibbles" move makes
// this WORSE. `83 F8 FF 74 ?? 48 8D ?4 40 48 C1 E? 05 48 03 ?? ...` picks up two unrelated
// 0x60-stride global TSets that sit at LOWER addresses than the real sites — and because
// ValidateSparseDelegates is deliberately weak (it only range-checks two ints), a decoy
// that scans first WINS. Exact-register forms are the safe ones here.

// DI427_1: TSet::FindId head — call [rip] EnterCriticalSection; lea rcx,[SparseDelegates];
//          call TSet::FindId; movsxd the out-param; cmp -1. Contains NO stride/offset
//          arithmetic at all (pure x64 ABI shape), so it is the most version-portable of
//          the set. 5 sites (Clear / Contains x2 / Remove x2), all correct, 0 decoys.
constexpr const char* AOB_SPARSE_DI427_1 =
    "FF 15 ?? ?? ?? ?? 4? 8B C? 48 8D 54 24 ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 63 44 24 ?? ?? 33 ?? 83 F8 FF 74 ?? 4? 8D";

// DI427_2: the element-address block plus the inner-TMap fetch tail —
//          cmp -1; lea r,[rax+rax*2]; shl r,5; add r,[SparseDelegates]; jmp;
//          mov r,<null>; test; lea r,[elem+8]; cmovz. The `+8 / cmovz` tail is
//          load-bearing: without it the same block collides with 0x60-stride decoys.
//          5 sites, all correct, 0 decoys.
constexpr const char* AOB_SPARSE_DI427_2 =
    "48 63 44 24 ?? 4? 33 ?? 83 F8 FF 74 ?? 48 8D ?? 40 48 C1 E? 05 48 03 ?? ?? ?? ?? ?? EB ?? 4? 8B ?? 48 85 ?? 4? 8D ?? 08 4? 0F 44 ??";

// ============================================================
// GObjects — DI427 (UE 4.27, 32-byte FUObjectItem)
// ============================================================
// WHY these exist: on DropIn every one of the 52 pre-existing GObjects patterns MISSES or
// resolves only to decoys. Root cause, measured over all 400 xrefs to ObjObjects.Objects:
// the destination register of the chunk load is rdi(156) / rsi(92) / r14(63) / rbx(40) /
// r15(19) / r12(15) / rbp(7) / rax(6) / r13(2) — and NEVER rcx, because rcx is the *index*
// register at every one of these sites. GOBJ_V1 hardcodes `48 8B 0C C8` (dest = rcx), so
// the whole V-series is structurally unable to fire here. Nibble-masking the REX + modrm
// is the fix.
//
// SECOND TRAP: do NOT shorten these. The 14-byte core
// `48 8B 05 ?? ?? ?? ?? 4? 8B ?? C8 4? 85 ??` is decoy-free on DropIn but produces 1 decoy
// on Solarpunk and 9 on Avowed. The `75 ?? E8` (jnz over the noreturn check-fail call)
// tail is what takes all three to zero.

// DI427_1: inlined FChunkedFixedUObjectArray::GetObjectPtr + the 32-byte-item shift.
//   mov rax,[ObjObjects.Objects]; mov <r>,[rax+rcx*8]; test <r>,<r>; jnz; call check-fail;
//   nop; int3; mov <r2>,<withinIdx>; shl <r2>,5
//   The trailing `4? C1 E? 05` (shl r,5) is the 32-byte-FUObjectItem fingerprint — no other
//   pattern in this file encodes a 32-byte stride (they assume 16/20/24).
constexpr const char* AOB_GOBJECTS_DI427_1 =
    "48 8B 05 ?? ?? ?? ?? 4? 8B ?? C8 4? 85 ?? 75 ?? E8 ?? ?? ?? ?? 90 CC 4? 8B ?? 4? C1 E? 05";
// DI427_2: item-size-AGNOSTIC core of _1 (stops before the shift), so it also covers the
//   sites where MSVC folded the mov away, and would still fire on a 24-byte-item build.
//   Broadest of the set -> lowest priority of the three.
constexpr const char* AOB_GOBJECTS_DI427_2 =
    "48 8B 05 ?? ?? ?? ?? 4? 8B ?? C8 4? 85 ?? 75 ?? E8 ?? ?? ?? ?? 90 CC";
// DI427_3: FUObjectArray::IndexToObject's real (non-check) bounds test + the
//   NumElementsPerChunk=64K divide/modulo. The 15-byte tail
//   `0F B7 D2 03 C2 8B C8 0F B7 C0 2B C2 C1 F9 10` is 100% literal and is the strongest
//   FChunkedFixedUObjectArray fingerprint in the image. Resolves to ObjObjects.NumElements,
//   so it needs adjustment -0x14 to land on ObjObjects.
constexpr const char* AOB_GOBJECTS_DI427_3 =
    "3B ?D ?? ?? ?? ?? 0F 8D ?? ?? ?? ?? 8B C? 89 ?? ?? 99 0F B7 D2 03 C2 8B C8 0F B7 C0 2B C2 C1 F9 10";

// ============================================================
// GNames / GWorld — DI427 (UE 4.27)
// ============================================================
// GNAM_DI427_1: the FName resolve prologue shared by ~10 leaf accessors
//   (operator== / GetComparisonNameEntry / GetDisplayNameEntry / GetEntry / ToString / ...).
//   lea rcx,[NamePoolData]; call FNamePool::FNamePool; mov <r>,rax; mov byte[bInit],1;
//   then reload the spilled FName as a qword and shift the Number half out.
//   Intended replacement for the GNAM_V5/V2/D7_1 family, which on this binary fire
//   16 686 / 16 692 / 104 897 times with ZERO correct hits. 10 sites, all correct.
constexpr const char* AOB_GNAMES_DI427_1 =
    "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4? 8B ?? C6 05 ?? ?? ?? ?? 01 48 8B 44 24 ?? 48 C1 E8 20";
// GNAM_DI427_2: same lazy-init head, but continued into the FNameEntry address math —
//   `add eax,eax` (FNameEntry stride 2) then `add rax,[pool + blockIdx*8 + 0x10]`
//   (Entries.Blocks at +0x10). Nothing but FName code does shr-32 / double / index-at-+0x10.
constexpr const char* AOB_GNAMES_DI427_2 =
    "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 4? 8B ?? C6 05 ?? ?? ?? ?? 01 48 8B 44 24 ?? 48 C1 E8 20 03 C0 4? 03 ?? ?? 10";

// GWLD_DI427_1: UEngine::LoadMap — the canonical `GWorld = NewWorld` store, followed by
//   `WorldContext.World()->WorldType = WorldContext.WorldType`. The two structural
//   displacements are UE4.27-correct and PDB-confirmed: FWorldContext::ThisCurrentWorld
//   at +0x280 and UWorld::WorldType at +0x10A. No existing pattern anchors on LoadMap.
constexpr const char* AOB_GWORLD_DI427_1 =
    "48 89 15 ?? ?? ?? ?? E8 ?? ?? ?? ?? 41 0F B6 ?? 24 49 8B ?? 24 80 02 00 00 88 ?? 0A 01 00 00";
// GWLD_DI427_2: FSeamlessTravelHandler::Tick — `mov qword ptr [rip+d32], 0`, the
//   `GWorld = nullptr` teardown store. NOTE totalLen = 11, not 7: the disp32 still starts
//   at byte 3 but the instruction carries a trailing imm32. Every one of the 52 existing
//   GWorld patterns uses 48 8B / 48 39 / 48 3B / 4C 0F 44 / 48 89 — the C7-imm store form
//   is absent from the table entirely, so this shape is invisible to the scanner in EVERY
//   game today, not just this one.
constexpr const char* AOB_GWORLD_DI427_2 =
    "48 C7 05 ?? ?? ?? ?? 00 00 00 00 49 8B ?? 24 80 00 00 00 48 81 C? 38 01 00 00";

// ============================================================
// GEngine (UEngine* GEngine) — the &GEngine SLOT
// ============================================================
// Resolving the *slot* (not just the live object) is what makes this worth a target:
//   * FindGameEngine / RecoverGWorldViaEngine currently locate the engine by walking the
//     whole GObjects pool resolving a "GameViewport" property offset per class. With the
//     slot that becomes a single deref.
//   * The Teleport tab's UE_GameEngine CE symbol can stop being an allocateMemory snapshot
//     of a UEngine* (which goes stale on restart) and register against &GEngine like
//     UE_GWorld does, auto-following engine recreation.
//
// X1 is CROSS-VERSION: UWorld::GetGameViewport is a tiny, stable accessor whose body is
// `sub rsp,0x2X; mov rdx,rcx; mov rcx,[GEngine]; call; test rax,rax; jz`. The only
// difference between UE 4.27 and UE 5.7 is the stack size, hence the `2?` nibble.
// Verified: DropIn 2/2 correct, Solarpunk 1/1 correct, and on Avowed (UE 5.3, no symbols)
// both hits converge on ONE .data global — the expected shape for its GEngine, which the
// runtime validator then confirms or rejects.
constexpr const char* AOB_GENGINE_X1 =
    "48 83 EC 2? 48 8B D1 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 ??";
// X3: X1's HEAD only — `sub rsp,0x2X; mov rdx,rcx; mov rcx,[GEngine]; call` — with the REX of
// the `mov rdx,rcx` nibble-masked and X1's trailing `test rax,rax; jz` dropped.
//
// Why it exists: FF7 Remake (UE 4.18, SquareEnix fork) is the ONLY binary in a 26-program sweep
// where GEngine resolved to nothing at all — every GEngine pattern missed. Its
// GetWorldFromContextObject wrapper spills the result (`mov rbx,rax`) BEFORE the null check, so
// X1's `48 85 C0` no longer follows the call and no amount of nibble-masking can bridge it.
//
// Dropping the tail is safe here, and measured rather than assumed: the head alone is
// UNIQUE-OK with ZERO decoys on both symbolised oracles it was calibrated against
// (DropIn 4.27: 3/3 correct; Solarpunk 5.7: 2/2) — i.e. it finds strictly MORE correct sites
// than X1 (2 and 1) while introducing none that are wrong. On FF7R it produces exactly one hit,
// at 0x145879EE8, and that site was confirmed by disassembly to be
// `GEngine->GetWorldFromContextObject(Obj)` — the callee returns a UWorld which the caller
// immediately runs through GUObjectArray.IndexToObject. `GENGC_A` (a wholly different shape)
// independently resolves to the same address.
// X1 is kept AHEAD of this: it is the tighter of the two and costs nothing when it hits.
constexpr const char* AOB_GENGINE_X3 =
    "48 83 EC 2? 4? 8B D1 48 8B 0D ?? ?? ?? ?? E8";
// X2: FEngineLoop::Tick — `mov rbx,[GEngine]; test rbx,rbx; jz; call; mov rcx,[rbx+0x10]`.
// Also cross-version (DropIn 6/6, Solarpunk 7/7) and far more redundant than X1.
constexpr const char* AOB_GENGINE_X2 =
    "48 8B 1D ?? ?? ?? ?? 48 85 DB 74 ?? E8 ?? ?? ?? ?? 48 8B 4B 10 4C 8D 40";
// DI427: UGameplayStatics::GetRealTimeSeconds shape — 6 redundant sites on UE 4.27.
constexpr const char* AOB_GENGINE_DI427_1 =
    "48 83 EC 28 48 8B D1 41 B8 01 00 00 00 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 85 C0 74 ?? F3 0F 10 80";
// SP57: UEngine::IsStereoscopic3D — UE 5.7 only, kept as an independent third anchor.
constexpr const char* AOB_GENGINE_SP57_1 =
    "48 89 5C 24 08 57 48 83 EC 20 48 8B 3D ?? ?? ?? ?? 33 DB 48 85 D2 74";
// ES55: UEngine::GetEngineSubsystem<T> prologue — `mov rdi,[GEngine]; call; cmp byte[flag],0`.
// Covers UE 5.5 AND 5.7 (7 sites on ES2 5.5, 6 on Solarpunk 5.7), 0 hits on UE4.27/5.3.
// This is the pattern that closes the 5.5 hole: X1/X2 both MISS on 5.5, because 5.5 emits
// FEngineLoop::Tick's null check as a NEAR jz (`0F 84`) where 4.27/5.7 use a short `74` —
// a length change no nibble can bridge. The obvious 5.5 FEngineLoop::Tick pattern was
// REJECTED instead: it takes 6 hits on Avowed that resolve to six DIFFERENT globals
// (a generic two-global null-check idiom). Divergent hits = generic shape; the accepted
// patterns' extra hits all converge on one address.
constexpr const char* AOB_GENGINE_ES55_1 =
    "48 89 5C 24 08 57 48 83 EC 20 48 8B 3D ?? ?? ?? ?? E8 ?? ?? ?? ?? 80 3D ?? ?? ?? ?? 00 48 8B D8";


// ============================================================
// Unified Pattern Arrays (sorted by priority)
// ============================================================
// Priority scheme (lower = tried first; each target's array is sorted by priority at
// scan time). Values are SPARSE across 0–1000 BY DESIGN — the gaps let a new pattern
// slot into the right band without renumbering its neighbours. The absolute number is
// meaningless; only the order within a target's array matters. When adding a pattern,
// pick an unused value in the matching band (they step by 10, so there is room between
// any two). Bands:
//     0– 30   Symbol exports / call-follow (exact address, O(1))
//    40– 90   Symbol-derived (FName ctor call-site scan, etc.)
//   100–290   Tier 1 — long, highly-specific, verified-unique (newest-engine, decoy-proof)
//   300–490   Tier 2 — medium specificity, good surrounding context (per-game)
//   500–590   Tier 3 — standard short patterns (common codegen)
//   600–690   Patternsleuth (arithmetic / offset-anchored)
//   700–790   UE4 / legacy-specific
//   800–990   Very short generic / last-resort
//
// BAND DISCIPLINE (build 2405). A pattern's band is set by how SPECIFIC it is — count its
// LITERAL (non-wildcard) bytes — not by how old it is or who contributed it. The GNames
// table had drifted badly in both directions and was re-sorted from measured data:
//   * GNAM_V1/V3/V4 are 8 bytes with FOUR literal bytes, the least specific patterns in the
//     file, yet sat at 500-540. Measured: DECOY-ONLY on UE4.20/5.5/5.7 and 539-2060 hits
//     where they do reach truth. They belong in 800-990 and are now there.
//   * GNAM_V5 (7 literal bytes) sat in the TIER 1 band at 110 while producing 16,686 hits on
//     UE4.27 and OK-BEHIND on every engine it touches. Demoting it is a straight upgrade:
//     UE5.5 and UE5.6 now select GNAM_ES53_1 and UE5.7 selects GNAM_SAT425_3, all UNIQUE-OK.
//   * The pre-FNamePool UE4 patterns (CT3 20 literal bytes, G42_1, CT4, SAT422_1) were
//     stranded at 800-860 in the last-resort band despite being the LONGEST and most specific
//     entries — they were hand-derived later and deliberately lengthened. They target
//     TStaticIndirectArrayThreadSafeRead / TNameEntryArray, a different structure entirely,
//     and measurably MISS on all four FNamePool binaries (4.27/5.5/5.6/5.7), so moving them
//     up to 700-730 cannot cost anything and saves ~710 wasted validations on a UE4.20 title.
// Rule of thumb: fewer than ~8 literal bytes means 800+, no matter what it is anchored on.
//
// BUILD 2407 — the same audit applied to GObjects and GWorld, which build 2405 left alone.
// Measured over 26 programs (11 with PDB truth) via tools/ghidra/sweep.sh + aggregate_sweep.py:
//   * GWLD_V3/V4/V5/V2/V6 sat at 500-580 on 4-7 literal bytes and are the noisiest block in the
//     file — GWLD_V3 takes 22,017 matches (95.7 per MB of .text on a monolithic EXE; 2,658 on
//     FF7 Remake alone). V2/V4/V5/V6 reach the true GWorld on ZERO oracles. Now 900-980.
//   * GOBJ_V1/V2/V3/V5/V6/V7/CT3 + the PS6/PS7 arithmetic pair, same story (GOBJ_V1: 10,152
//     matches, 53/MB). Now 890-970.
// Nothing demoted here is load-bearing: every oracle lands on a pattern at priority <= 435 for
// GWorld and <= 210 for GObjects, and the post-change sweep confirms not one landing pattern
// moved. What demotion actually buys is ORDERING SAFETY, not speed — see the per-table notes;
// the GObjects block really did outrank longer patterns, the GWorld block already sat last.
//
// The reason NOT to reflexively demote a noisy-but-early pattern: patterns are scanned in
// BATCHES OF 8 and ScanForTarget returns on the first validated match, so a pattern that wins
// from batch 1 avoids every later .text pass. Rejecting a few hundred candidates by validation
// is far cheaper than an extra AVX2 sweep of a 130 MB .text. That is precisely why
// GOBJ_ES53_1 stays at 100 despite costing up to 475 wasted validations (UE 5.5) — it is the
// landing pattern for six module-instances, and buying that with one batch is a good trade.
//
// A COUNTER-EXAMPLE worth keeping, because it shows literal-byte count is necessary but not
// sufficient: GOBJ_ES53_1 has 16 literal bytes yet takes 21-131 matches on every monolithic
// title. Its shape (`sub rsp,28; lea rcx,[X]; call ctor; lea rcx,[Y]; add rsp,28; jmp atexit`)
// is the generic MSVC function-scope-static registration thunk, so it matches once per static
// with a destructor. It stays at priority 100 regardless: it is the pattern the runtime lands
// on for FIVE engine versions, and its decoys are all rejected by ValidateGObjects. Judge a
// band by specificity AND semantics, not byte count alone.

// Helper macro to reduce boilerplate for common RipBoth patterns
#define SIG_RIP(id, pat, tgt, ioff, opc, tot, adj, pri, src, note) \
    { id, pat, tgt, AobResolve::RipBoth, ioff, opc, tot, adj, pri, 0, false, src, note }
#define SIG_RIP_DIRECT(id, pat, tgt, ioff, opc, tot, adj, pri, src, note) \
    { id, pat, tgt, AobResolve::RipDirect, ioff, opc, tot, adj, pri, 0, false, src, note }
#define SIG_EXPORT(id, sym, tgt, pri, note) \
    { id, sym, tgt, AobResolve::SymbolExport, 0, 0, 0, 0, pri, 0, false, "EXP", note }
#define SIG_SYM_CALL(id, sym, tgt, pri, note) \
    { id, sym, tgt, AobResolve::SymbolCallFollow, 0, 0, 0, 0, pri, 0, false, "EXP", note }
#define SIG_GWORLD_RIP(id, pat, ioff, opc, tot, adj, pri, allowNull, src, note) \
    { id, pat, AobTarget::GWorld, AobResolve::RipBoth, ioff, opc, tot, adj, pri, 0, allowNull, src, note }

// ── GObjects ─────────────────────────────────────────────────────────────
constexpr AobSignature GOBJECTS_PATTERNS[] = {
    // 0: Symbol export (O(1))
    SIG_EXPORT("GOBJ_EXP", EXPORT_GOBJECTARRAY, AobTarget::GObjects, 0, "MSVC mangled symbol"),

    // 100–290: Tier 1 — long, specific patterns
    SIG_RIP("GOBJ_ES53_1", AOB_GOBJECTS_ES53_1, AobTarget::GObjects, 4, 3, 7, 0, 100, "ES53", "ES2 UE5.3 FUObjectArray ctor+atexit"),
    SIG_RIP("GOBJ_DI427_1", AOB_GOBJECTS_DI427_1, AobTarget::GObjects, 0, 3, 7, 0, 105, "DI427",
            "UE4.27 GetObjectPtr + 32-byte-item shl 5 (nibble-masked dest reg)"),
    SIG_RIP("GOBJ_DI427_3", AOB_GOBJECTS_DI427_3, AobTarget::GObjects, 0, 2, 6, -0x14, 115, "DI427",
            "UE4.27 IndexToObject bounds test + 64K chunk divide (-> NumElements, adj -0x14)"),
    { "GOBJ_V10", AOB_GOBJECTS_V10, AobTarget::GObjects, AobResolve::RipBoth,
      0, 3, 7, -0x10, 110, 0, false, "V", "Split Fiction UE5.5+ lea+call+call" },
    SIG_RIP("GOBJ_AV1", AOB_GOBJECTS_AV1, AobTarget::GObjects, 0, 3, 7, -0x10, 120, "AV",
            "Avowed/Obsidian UE5.3 AllocateUObjectIndex MOV RDX,[ObjObjects.Objects]"),
    SIG_RIP("GOBJ_AV2", AOB_GOBJECTS_AV2, AobTarget::GObjects, 0, 3, 7, -0x10, 130, "AV",
            "Avowed/Obsidian UE5.3 FUObjectItem chunk-index (20B stride, ~10+ sites, patch-resilient)"),
    SIG_RIP("GOBJ_G42_4", AOB_GOBJECTS_G42_4, AobTarget::GObjects, 0, 3, 7, 0, 140, "G42", "UE4.2 long lea+call+epilogue"),
    SIG_RIP("GOBJ_SAT425_2", AOB_GOBJECTS_SAT425_2, AobTarget::GObjects, 0, 3, 7, 0, 150, "SAT425", "Satisfactory UE4.25 UObjectBaseInit 31-byte sequence"),
    SIG_RIP("GOBJ_SAT422_1", AOB_GOBJECTS_SAT422_1, AobTarget::GObjects, 0, 3, 7, 0, 160, "SAT422", "Satisfactory UE4.22 FEngineLoop::PreInit 4-CALL chain"),
    SIG_RIP("GOBJ_SAT425_1", AOB_GOBJECTS_SAT425_1, AobTarget::GObjects, 0, 3, 7, 0, 170, "SAT425", "Satisfactory UE4.25 FObjectIterator ctor"),
    { "GOBJ_RE3", AOB_GOBJECTS_RE3, AobTarget::GObjects, AobResolve::RipBoth,
      0, 3, 7, 0, 180, 0, false, "RE", "Little Nightmares 3 Demo extended" },
    { "GOBJ_V11", AOB_GOBJECTS_V11, AobTarget::GObjects, AobResolve::RipBoth,
      0, 3, 7, 0, 190, 0, false, "V", "Little Nightmares 3" },
    SIG_RIP("GOBJ_RE2", AOB_GOBJECTS_RE2, AobTarget::GObjects, 0, 3, 7, -0x10, 200, "RE", "FF7 Remake extended"),
    SIG_RIP("GOBJ_V13", AOB_GOBJECTS_V13, AobTarget::GObjects, 0, 3, 7, 0, 210, "V", "Palworld extended context"),
    SIG_RIP("GOBJ_ES2_1", AOB_GOBJECTS_ES2_1, AobTarget::GObjects, 0, 3, 7, 0, 220, "ES2", "UE5.5 AllocateUObjectIndex"),
    SIG_RIP("GOBJ_SAT52_1", AOB_GOBJECTS_SAT52_1, AobTarget::GObjects, 0, 3, 7, 0, 230, "SAT52", "Satisfactory UE5.2 TObjectIteratorBase ctor"),
    SIG_RIP("GOBJ_V12", AOB_GOBJECTS_V12, AobTarget::GObjects, 0, 3, 7, -0x10, 240, "V", "FF7 Remake"),
    SIG_RIP("GOBJ_SF_1", AOB_GOBJECTS_SF_1, AobTarget::GObjects, 0, 3, 7, 0, 250, "SF", "SatisfFactory via _imp_ (in EXE)"),
    SIG_RIP("GOBJ_DI427_2", AOB_GOBJECTS_DI427_2, AobTarget::GObjects, 0, 3, 7, 0, 255, "DI427",
            "UE4.27 GetObjectPtr core, item-size agnostic (broadest — last in Tier 1)"),

    // 300–490: Tier 2 — medium patterns
    SIG_RIP("GOBJ_G42_2", AOB_GOBJECTS_G42_2, AobTarget::GObjects, 0, 3, 7, 0, 260, "G42", "UE4.2 RemoveUObjectDeleteListener"),
    SIG_RIP("GOBJ_G42_3", AOB_GOBJECTS_G42_3, AobTarget::GObjects, 0, 3, 7, 0, 300, "G42", "UE4.2 lea+mov r8d+mov edx"),
    SIG_RIP("GOBJ_G42_1", AOB_GOBJECTS_G42_1, AobTarget::GObjects, 0, 3, 7, 0, 310, "G42", "UE4.2 lea+xor+mov constructor"),
    SIG_RIP("GOBJ_GH_1", AOB_GOBJECTS_GH_1, AobTarget::GObjects, 12, 3, 7, 0, 320, "GH", "Ghidra UObjectBase::AddObject cross-game"),
    SIG_RIP("GOBJ_GH_4", AOB_GOBJECTS_GH_4, AobTarget::GObjects, 12, 3, 7, 0, 330, "GH", "Ghidra FWeakObjectPtr::operator= cross-game"),
    { "GOBJ_RE1", AOB_GOBJECTS_RE1, AobTarget::GObjects, AobResolve::RipBoth,
      0, 2, 6, 0, 340, 0, false, "RE", "FF7 Rebirth add+cmp+jge" },
    SIG_RIP("GOBJ_GH_2", AOB_GOBJECTS_GH_2, AobTarget::GObjects, 12, 3, 7, 0, 350, "GH", "Ghidra UnMarkAllObjects cross-game"),
    SIG_RIP("GOBJ_V4",  AOB_GOBJECTS_V4,  AobTarget::GObjects, 0, 3, 7, 0, 360, "V", "classic UE5 longer context"),
    SIG_RIP("GOBJ_V8",  AOB_GOBJECTS_V8,  AobTarget::GObjects, 0, 3, 7, 0, 370, "V", "bit shift variant"),
    SIG_RIP("GOBJ_V9",  AOB_GOBJECTS_V9,  AobTarget::GObjects, 0, 3, 7, 0, 380, "V", "extended index cdqe"),
    SIG_RIP("GOBJ_V7",  AOB_GOBJECTS_V7,  AobTarget::GObjects, 0, 3, 7, 0, 890, "V", "GSpots cdq movzx"),
    SIG_RIP("GOBJ_UD1", AOB_GOBJECTS_UD1, AobTarget::GObjects, 0, 3, 7, 0, 400, "UD", "UEDumper"),
    SIG_RIP("GOBJ_GH_3", AOB_GOBJECTS_GH_3, AobTarget::GObjects, 12, 3, 7, 0, 410, "GH", "Ghidra IncrementalPurgeGarbage cross-game"),
    SIG_RIP("GOBJ_G427_1", AOB_GOBJECTS_G427_1, AobTarget::GObjects, 0, 3, 7, 0, 420, "G427", "UE4.27 Objects SAR context"),
    SIG_RIP("GOBJ_G427_3", AOB_GOBJECTS_G427_3, AobTarget::GObjects, 0, 3, 7, 0, 430, "G427", "UE4.27 FGCObject extended context"),
    SIG_RIP("GOBJ_SAT426_1", AOB_GOBJECTS_SAT426_1, AobTarget::GObjects, 0, 3, 7, 0, 440, "SAT426", "Satisfactory UE4.26 RemoveAnnotation lea+call+test"),
    SIG_RIP("GOBJ_SAT426_2", AOB_GOBJECTS_SAT426_2, AobTarget::GObjects, 0, 2, 6, 0, 450, "SAT426", "Satisfactory UE4.26 GatherUnreachableObjects"),
    SIG_RIP("GOBJ_SAT52_2", AOB_GOBJECTS_SAT52_2, AobTarget::GObjects, 0, 3, 7, 0, 460, "SAT52", "Satisfactory UE5.2 ~UObjectBase IsValid"),

    // 500–590: Tier 3 — now EMPTY for GObjects. The whole V-series short-pattern block moved to
    // the 800–990 last-resort band in build 2407; see the band-discipline note above. All six
    // carry 6–7 literal bytes, and not one of them is the pattern the runtime lands on for
    // ANY of the 11 symbolised oracles.

    // 600–690: Patternsleuth (instrOffset != 0)
    SIG_RIP("GOBJ_PS1", AOB_GOBJECTS_PS1, AobTarget::GObjects, 23, 3, 7, 0, 600, "PS", "cmp/cmp/jne; lea"),
    SIG_RIP("GOBJ_PS2", AOB_GOBJECTS_PS2, AobTarget::GObjects,  2, 3, 7, 0, 610, "PS", "jz; lea rcx"),
    SIG_RIP("GOBJ_PS3", AOB_GOBJECTS_PS3, AobTarget::GObjects,  5, 3, 7, 0, 620, "PS", "jne; mov; lea rcx"),
    SIG_RIP("GOBJ_PS4", AOB_GOBJECTS_PS4, AobTarget::GObjects, 16, 3, 7, 0, 630, "PS", "test; mov; lea r11"),
    SIG_RIP("GOBJ_PS5", AOB_GOBJECTS_PS5, AobTarget::GObjects, 12, 3, 7, 0, 640, "PS", "or; and; mov; lea rcx"),
    SIG_RIP("GOBJ_PS6", AOB_GOBJECTS_PS6, AobTarget::GObjects, 14, 2, 6, 0, 960, "PS", "arithmetic sub eax"),
    SIG_RIP("GOBJ_PS7", AOB_GOBJECTS_PS7, AobTarget::GObjects, 17, 2, 6, 0, 970, "PS", "arithmetic add ecx"),

    // 700–790: UE 4.27 patterns with offsets/adjustments
    SIG_RIP("GOBJ_G427_2", AOB_GOBJECTS_G427_2, AobTarget::GObjects, 0, 2, 6, -0x14, 700, "G427", "UE4.27 NumElements CMP (adj -0x14)"),
    SIG_RIP("GOBJ_G427_4", AOB_GOBJECTS_G427_4, AobTarget::GObjects, 0, 2, 6, 0x0C, 720, "G427", "UE4.27 ObjLastNonGCIndex (adj +0x0C)"),

    // 800–990: UE4/legacy
    SIG_RIP("GOBJ_CT1", AOB_GOBJECTS_CT1, AobTarget::GObjects, 5, 3, 7, 0, 800, "CT", "UE4 Dumper.CT v5+"),
    SIG_RIP("GOBJ_OT_1", AOB_GOBJECTS_OT_1, AobTarget::GObjects, 2, 3, 7, 0, 820, "OT", "Octopath Traveller UE4 FUObjectArray::Init LEA RCX"),
    SIG_RIP("GOBJ_OT_2", AOB_GOBJECTS_OT_2, AobTarget::GObjects, 2, 3, 7, 0, 840, "OT", "UE4 FUObjectArray::Init generalized (wildcarded regs)"),
    // 890–970: the short V-series + patternsleuth-arithmetic block, demoted here in build 2407
    // (was 390–660). Measured over 31 programs: GOBJ_V1 alone takes 10,152 matches (53/MB on a
    // monolithic EXE) and GOBJ_V3 1,333, while V2/V3/V5/V6 reach the true address on ZERO of the
    // 17 oracles and V1/V7 never win one either.
    //
    // Unlike the GWorld block this IS a real ordering change: at 390–660 they came BEFORE
    // GOBJ_G427_2 (700), G427_4 (720), CT1 (800) and the Octopath OT_1/OT_2 pair (820/840) —
    // all of which carry 9–13 literal bytes against these six or seven. A short generic pattern
    // outranking a long purpose-built one is exactly the ordering that lets a decoy win.
    // They stay in the table as insurance for engine builds the corpus does not cover.
    SIG_RIP("GOBJ_V2",  AOB_GOBJECTS_V2,  AobTarget::GObjects, 0, 3, 7, 0, 900, "V", "common UE5.3+"),
    SIG_RIP("GOBJ_V1",  AOB_GOBJECTS_V1,  AobTarget::GObjects, 0, 3, 7, 0, 910, "V", "classic UE5.0-5.2"),
    SIG_RIP("GOBJ_V6",  AOB_GOBJECTS_V6,  AobTarget::GObjects, 0, 3, 7, 0, 920, "V", "alt mov rcx"),
    SIG_RIP("GOBJ_V3",  AOB_GOBJECTS_V3,  AobTarget::GObjects, 0, 3, 7, 0, 930, "V", "mov r8"),
    SIG_RIP("GOBJ_V5",  AOB_GOBJECTS_V5,  AobTarget::GObjects, 0, 3, 7, 0, 940, "V", "mov r10"),
    SIG_RIP("GOBJ_CT3", AOB_GOBJECTS_CT3, AobTarget::GObjects, 0, 3, 7, 0, 950, "CT", "mov r8; cmp"),
};

// ── Obfuscated FName payloads (licensee forks) ───────────────────────────
// Not part of any PATTERNS[] table: this does not resolve a global pointer, so it is
// consumed directly by Genau::ResolveNameKeyTable rather than through ScanForTarget.
// It is scanned ONLY after the experimental gate is on AND both stock FNameEntry
// layouts have already been rejected, so an ordinary title never runs it.
//
// ME1: the fork's FNameEntry payload de-obfuscator, matched at its function entry.
//   mov [rsp+8],rbx / mov [rsp+10],rsi / push rdi / sub rsp,20
//   movzx r8d,word [rcx]      <- stock 2-byte header
//   lea   rdx,[rcx+4]         <- chars at entry+4 (stock is +2: the fork inserts a u16 tag)
//   shr   r8,6                <- len = header >> 6 (stock Format A)
//   call  memcpy              <- rel32 wildcarded
//   movzx edi,word [rbx] / shr edi,6
//   call  <key-table ctx getter>   <- rel32 wildcarded; followed at match+0x2F
//   movzx edx,word [rbx+2]    <- the non-stock u16 tag that selects the XOR key
// The match address is EVIDENCE, never a call target — Genau follows the second call
// and the getter's rip-relative LEA to reach the tag->key table and reads it directly.
// Verified unique in MindsEye's 145 MB .text (the 16-byte MSVC prologue alone hits 139
// times; the semantic tail is what carries the uniqueness).
constexpr const char* AOB_NAMEDECRYPT_ME1 =
    "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 44 0F B7 01 48 8B F2 "
    "48 8D 51 04 49 C1 E8 06 48 8B D9 48 8B CE E8 ?? ?? ?? ?? 0F B7 3B "
    "C1 EF 06 E8 ?? ?? ?? ?? 0F B7 53 02";
// Offset within a match of the `call <ctx getter>` instruction (its rel32 is at +1).
constexpr int AOB_NAMEDECRYPT_ME1_CTX_CALL_OFF = 0x2F;

// ── GNames ───────────────────────────────────────────────────────────────
constexpr AobSignature GNAMES_PATTERNS[] = {
    // 0–20: Symbol exports → scan function body for FNamePool references
    SIG_SYM_CALL("GNAM_EXP_TOSTR", EXPORT_FNAME_TOSTRING, AobTarget::GNames, 0, "FName::ToString export"),
    SIG_SYM_CALL("GNAM_EXP_CTOR",  EXPORT_FNAME_CTOR,     AobTarget::GNames, 10, "FName ctor (wchar) export"),
    SIG_SYM_CALL("GNAM_EXP_CTOR2", EXPORT_FNAME_CTOR_CHAR,AobTarget::GNames, 20, "FName ctor (char) export"),

    // 40: FName ctor call-site (follows CALL, scans body)
    { "GNAM_V7", AOB_GNAMES_V7_FNAME_CTOR, AobTarget::GNames, AobResolve::CallFollow,
      0, 0, 0, 0, 40, 11, false, "V", "FF7 Rebirth FName ctor call-site" },

    // 100–290: Tier 1 — long, specific patterns
    SIG_RIP("GNAM_V8",    AOB_GNAMES_V8,     AobTarget::GNames, 0, 3, 7, 0, 100, "V", "Palworld extended context"),
    SIG_RIP("GNAM_DI427_2", AOB_GNAMES_DI427_2, AobTarget::GNames, 0, 3, 7, 0, 105, "DI427",
            "UE4.27 FName resolve + FNameEntry addr math (stride 2, Blocks at +0x10)"),
    SIG_RIP("GNAM_DI427_1", AOB_GNAMES_DI427_1, AobTarget::GNames, 0, 3, 7, 0, 115, "DI427",
            "UE4.27 FName resolve prologue, 10 sites (replaces the V5/V2/D7_1 decoy family)"),
    SIG_RIP("GNAM_V5",    AOB_GNAMES_V5,     AobTarget::GNames, 0, 3, 7, 0, 850, "V", "lea rcx; call; mov byte[],1 extended"),
    SIG_RIP("GNAM_ES53_1", AOB_GNAMES_ES53_1, AobTarget::GNames, 0, 3, 7, 0, 120, "ES53", "ES2 UE5.3 FNamePool init + MOV RDX,RAX"),
    SIG_RIP("GNAM_GH_1",  AOB_GNAMES_GH_1,   AobTarget::GNames, 12, 3, 7, 0, 130, "GH", "Ghidra ReserveNameBatch 27-fixed cross-game"),
    SIG_RIP("GNAM_SAT52_1", AOB_GNAMES_SAT52_1, AobTarget::GNames, 0, 3, 7, 0, 140, "SAT52", "Satisfactory UE5.2 dual-LEA NamePoolData"),
    SIG_RIP("GNAM_SAT425_1", AOB_GNAMES_SAT425_1, AobTarget::GNames, 18, 3, 7, 0, 150, "SAT425", "Satisfactory UE4.25 FName::AppendString LEA R8"),
    SIG_RIP("GNAM_SAT425_2", AOB_GNAMES_SAT425_2, AobTarget::GNames, 0, 3, 7, 0, 160, "SAT425", "Satisfactory UE4.25 FName::GetNameEntryMemorySize"),
    SIG_RIP("GNAM_GH_2",  AOB_GNAMES_GH_2,   AobTarget::GNames, 12, 3, 7, 0, 170, "GH", "Ghidra FNameEntryId::FromValidEName cross-game"),
    SIG_RIP("GNAM_ES2_1", AOB_GNAMES_ES2_1,  AobTarget::GNames, 0, 3, 7, 0, 180, "ES2", "UE5.5 ResolveEntry"),
    SIG_RIP("GNAM_SAT425_3", AOB_GNAMES_SAT425_3, AobTarget::GNames, 0, 3, 7, 0, 190, "SAT425", "Satisfactory UE4.25 GetNumAnsiNames (general V8)"),
    SIG_RIP("GNAM_SF_1",  AOB_GNAMES_SF_1,   AobTarget::GNames, 0, 3, 7, 0, 200, "SF", "SatisfFactory NamePoolData init (in Core DLL)"),
    SIG_RIP("GNAM_CT1",   AOB_GNAMES_CT1,    AobTarget::GNames, 0, 3, 7, 0, 210, "CT", "UE4 Dumper.CT v6+ lea r8; jmp 16"),
    // 300: was GNAM_CT2 + GNAM_UD2. CT2 removed b2407 — identical hit set on all 26 programs.
    SIG_RIP("GNAM_UD2",   AOB_GNAMES_UD2,    AobTarget::GNames, 0, 3, 7, 0, 300, "UD", "UEDumper lea rcx; call; mov r8 (supersedes CT2)"),

    // 300–490: Tier 2 — medium patterns
    SIG_RIP("GNAM_SF_2",  AOB_GNAMES_SF_2,   AobTarget::GNames, 0, 3, 7, 0, 340, "SF", "SatisfFactory SHL pattern (in Core DLL)"),
    SIG_RIP("GNAM_SF_3",  AOB_GNAMES_SF_3,   AobTarget::GNames, 0, 3, 7, 0, 360, "SF", "SatisfFactory FNameEntryId (in Core DLL)"),
    SIG_RIP("GNAM_V6",    AOB_GNAMES_V6,     AobTarget::GNames, 0, 3, 7, 0, 380, "V", "GSpots UE5+ mov rax; test; jnz"),
    SIG_RIP("GNAM_V2",    AOB_GNAMES_V2,     AobTarget::GNames, 0, 3, 7, 0, 860, "V", "lea rcx; call; mov byte ptr"),

    // 500–590: Tier 3 — short patterns
    SIG_RIP("GNAM_V1",    AOB_GNAMES_V1,     AobTarget::GNames, 0, 3, 7, 0, 870, "V", "lea rsi; jmp"),
    SIG_RIP("GNAM_V3",    AOB_GNAMES_V3,     AobTarget::GNames, 0, 3, 7, 0, 880, "V", "lea rax; jmp"),
    SIG_RIP("GNAM_V4",    AOB_GNAMES_V4,     AobTarget::GNames, 0, 3, 7, 0, 890, "V", "lea r8; jmp"),

    // 600–690: Patternsleuth
    SIG_RIP("GNAM_PS1",   AOB_GNAMES_PS1,    AobTarget::GNames, 2, 3, 7, 0, 600, "PS", "jz+9; lea r8"),
    SIG_RIP("GNAM_PS2",   AOB_GNAMES_PS2,    AobTarget::GNames, 7, 3, 7, 0, 620, "PS", "sub rsp; shr; lea rbp"),

    // 800–990: UE4/legacy (pre-FNamePool)
    SIG_RIP("GNAM_CT3",   AOB_GNAMES_CT3,    AobTarget::GNames, 4, 3, 7, 0, 700, "CT", "UE4 <4.23 pre-FNamePool deref"),
    SIG_RIP("GNAM_CT4",   AOB_GNAMES_CT4,    AobTarget::GNames, 3, 3, 7, 0, 720, "CT", "UE4 pre-FNamePool write pattern"),
    SIG_RIP("GNAM_G42_1", AOB_GNAMES_G42_1,  AobTarget::GNames, 0, 3, 7, 0, 710, "G42", "UE4.2 pre-FNamePool TStaticIndirectArray"),
    // 715: ahead of CT4 (720) so a UE 4.22 title lands on its purpose-built anchor rather than
    // on CT4's write-pattern, which only gets there after the validator rejects a decoy.
    SIG_RIP("GNAM_SAT422_1", AOB_GNAMES_SAT422_1, AobTarget::GNames, 0, 3, 7, 0, 715, "SAT422", "Satisfactory UE4.22 FName::GetNames + game-thread assert (PDB-corrected b2407)"),
};

// ── GWorld ───────────────────────────────────────────────────────────────
constexpr AobSignature GWORLD_PATTERNS[] = {
    // 0: Symbol export (O(1))
    SIG_EXPORT("GWLD_EXP", EXPORT_GWORLD, AobTarget::GWorld, 0, "UWorldProxy symbol"),

    // 100–160: Solarpunk UE 5.7 (verified decoy-free; re-anchor GWorld before
    // the generic GWLD_SF_2 that mis-fires on a decoy in this build)
    SIG_GWORLD_RIP("GWLD_SP57_1", AOB_GWORLD_SP57_1, 0, 3, 7, 0, 100, false, "SP57", "UE5.7 UGameEngine::Tick cmp [rcx+2C0] (tolerates inserted mov)"),
    // 105/115: UE 4.27 (DropIn, PDB-verified). Both are WRITE sites -> allowNull.
    // NOTE DI427_2 has totalLen = 11: `mov qword[rip+d32], imm32` — the disp32 still starts
    // at byte 3 but the instruction carries a trailing imm32. Mis-encoding this as 7 is the
    // classic way a C7-form store pattern silently resolves to garbage.
    SIG_GWORLD_RIP("GWLD_DI427_1", AOB_GWORLD_DI427_1, 0, 3,  7, 0, 105, true, "DI427", "UE4.27 UEngine::LoadMap GWorld=NewWorld store"),
    SIG_GWORLD_RIP("GWLD_DI427_2", AOB_GWORLD_DI427_2, 0, 3, 11, 0, 115, true, "DI427", "UE4.27 FSeamlessTravelHandler::Tick GWorld=nullptr (C7-imm store form)"),
    SIG_GWORLD_RIP("GWLD_SP57_2", AOB_GWORLD_SP57_2, 0, 3, 7, 0, 120, false, "SP57", "UE5.7 FMallocLeakReporter::WriteReports (mov rsi,rcx variant)"),
    SIG_GWORLD_RIP("GWLD_SP57_3", AOB_GWORLD_SP57_3, 0, 3, 7, 0, 140, false, "SP57", "UE5.7 UEngine::GetWorldFromContextObject fallback"),
    SIG_GWORLD_RIP("GWLD_SP57_4", AOB_GWORLD_SP57_4, 0, 3, 7, 0, 160, false, "SP57", "UE5.7 UActorComponent::On*PhysicsState mov [rax+298]"),

    // 100–290: Tier 1 — long specific patterns (ES2, SF, TQ2, SP57)
    SIG_GWORLD_RIP("GWLD_ES2_1", AOB_GWORLD_ES2_1, 0, 3, 7, 0, 110, false, "ES2", "UE5.5 26-byte lea+mov chain"),
    SIG_GWORLD_RIP("GWLD_ES2_2", AOB_GWORLD_ES2_2, 0, 4, 8, 0, 130, false, "ES2", "UE5.5 CMOVZ r13"),
    SIG_GWORLD_RIP("GWLD_ES2_3", AOB_GWORLD_ES2_3, 0, 3, 7, 0, 150, false, "ES2", "UE5.5 cmp [rcx+2C0]"),
    SIG_GWORLD_RIP("GWLD_ES2_4", AOB_GWORLD_ES2_4, 0, 3, 7, 0, 170, false, "ES2", "UE5.5 cmp+and GWorld"),
    SIG_GWORLD_RIP("GWLD_ES2_5", AOB_GWORLD_ES2_5, 0, 3, 7, 0, 180, false, "ES2", "UE5.5 call r12 loop"),
    SIG_GWORLD_RIP("GWLD_ES2_6", AOB_GWORLD_ES2_6, 0, 3, 7, 0, 190, false, "ES2", "UE5.5 cmovne+call rbx"),
    SIG_GWORLD_RIP("GWLD_GH_1",  AOB_GWORLD_GH_1,  12, 3, 7, 0, 200, false, "GH", "Ghidra FMallocLeakReporter 25-fixed cross-game"),
    SIG_GWORLD_RIP("GWLD_TQ_1",  AOB_GWORLD_TQ_1,  0, 3, 7, 0, 210, false, "TQ", "TQ2 extended V3"),
    SIG_GWORLD_RIP("GWLD_TQ_2",  AOB_GWORLD_TQ_2,  0, 3, 7, 0, 220, false, "TQ", "TQ2 dual mov"),
    SIG_GWORLD_RIP("GWLD_GH_2",  AOB_GWORLD_GH_2,   9, 3, 7, 0, 230, false, "GH", "Ghidra FUMGViewportClient::GetWorld cross-game"),
    SIG_GWORLD_RIP("GWLD_V7",    AOB_GWORLD_V7,     0, 3, 7, 0, 240, false, "V", "Palworld long context"),
    SIG_GWORLD_RIP("GWLD_V1",    AOB_GWORLD_V1,     0, 3, 7, 0, 250, false, "V", "cmp/cmovz"),

    // 260–320: SatisfFactory DLL patterns + Ghidra cross-game
    SIG_GWORLD_RIP("GWLD_GH_3",  AOB_GWORLD_GH_3,  12, 3, 7, 0, 260, false, "GH", "Ghidra GetWorldFromContextObject cross-game"),
    SIG_GWORLD_RIP("GWLD_SF_1",  AOB_GWORLD_SF_1,   0, 3, 7, 0, 270, false, "SF", "Engine DLL UGameEngine::Tick"),
    SIG_GWORLD_RIP("GWLD_SF_2",  AOB_GWORLD_SF_2,   0, 3, 7, 0, 300, false, "SF", "Engine DLL FAudioDeviceManager"),
    SIG_GWORLD_RIP("GWLD_SF_3",  AOB_GWORLD_SF_3,   0, 3, 7, 0, 305, false, "SF", "Engine DLL UWorld::FinishDestroy"),
    SIG_GWORLD_RIP("GWLD_SF_4",  AOB_GWORLD_SF_4,   0, 3, 7, 0, 310, false, "SF", "Engine DLL GetWorldFromContextObject"),
    SIG_GWORLD_RIP("GWLD_SF_5",  AOB_GWORLD_SF_5,   0, 3, 7, 0, 315, false, "SF", "Engine DLL FMallocLeakReporter"),
    SIG_GWORLD_RIP("GWLD_GH_4",  AOB_GWORLD_GH_4,   8, 3, 7, 0, 320, false, "GH", "Ghidra FEngineLoop::Tick XORPS cross-game"),

    // 325–365: UE 4.2 / Satisfactory read patterns
    SIG_GWORLD_RIP("GWLD_G42_3", AOB_GWORLD_G42_3,  9, 3, 7, 0, 325, false, "G42", "UE4.2 fallback return pattern"),
    SIG_GWORLD_RIP("GWLD_G42_2", AOB_GWORLD_G42_2,  0, 3, 7, 0, 330, false, "G42", "UE4.2 test+jz+mov r8b"),
    SIG_GWORLD_RIP("GWLD_G42_5", AOB_GWORLD_G42_5,  0, 3, 7, 0, 335, false, "G42", "UE4.2 mov+mov rbx+lea"),
    SIG_GWORLD_RIP("GWLD_G42_1", AOB_GWORLD_G42_1,  0, 3, 7, 0, 880, false, "G42", "UE4.2 mov+mov rsi+call"),
    SIG_GWORLD_RIP("GWLD_G42_4", AOB_GWORLD_G42_4,  0, 3, 7, 0, 345, false, "G42", "UE4.2 mov rdi+mov rbx"),
    SIG_GWORLD_RIP("GWLD_SAT422_1", AOB_GWORLD_SAT422_1, 0, 3, 7, 0, 350, false, "SAT422", "Satisfactory UE4.22 FMallocLeakReporter"),
    SIG_GWORLD_RIP("GWLD_SAT425_1", AOB_GWORLD_SAT425_1, 0, 3, 7, 0, 355, false, "SAT425", "Satisfactory UE4.25 UGameEngine::Tick CMP"),
    SIG_GWORLD_RIP("GWLD_SAT426_1", AOB_GWORLD_SAT426_1, 0, 3, 7, 0, 360, false, "SAT426", "Satisfactory UE4.26 UGameEngine::Tick cmp+jz"),
    SIG_GWORLD_RIP("GWLD_SAT52_1",  AOB_GWORLD_SAT52_1,  0, 3, 7, 0, 365, false, "SAT52", "Satisfactory UE5.2 FAudioDeviceManager"),

    // 370–390: UE 4.27 patterns
    SIG_GWORLD_RIP("GWLD_G427_1", AOB_GWORLD_G427_1, 0, 3, 7, 0, 370, false, "G427", "UE4.27 FEngineLoop::Tick extended"),
    SIG_GWORLD_RIP("GWLD_G427_2", AOB_GWORLD_G427_2, 0, 3, 7, 0, 375, false, "G427", "UE4.27 GetWorldFromContextObject"),
    SIG_GWORLD_RIP("GWLD_G427_3", AOB_GWORLD_G427_3, 10, 3, 7, 0, 380, false, "G427", "UE4.27 UGameEngine::Tick (49 prefix)"),
    SIG_GWORLD_RIP("GWLD_G427_4", AOB_GWORLD_G427_4, 10, 3, 7, 0, 385, false, "G427", "UE4.27 UGameEngine::Tick (48 prefix)"),
    SIG_GWORLD_RIP("GWLD_G427_5", AOB_GWORLD_G427_5, 0, 3, 7, 0, 390, false, "G427", "UE4.27 UWorld::FinishDestroy cmp rbx"),

    // 395–400: Wildcard-prefixed TQ2 patterns
    SIG_GWORLD_RIP("GWLD_TQ_3",  AOB_GWORLD_TQ_3,   3, 3, 7, 0, 395, false, "TQ", "TQ2 ??-prefix mov rax"),
    { "GWLD_TQ_4", AOB_GWORLD_TQ_4, AobTarget::GWorld, AobResolve::RipBoth,
      3, 3, 7, 0, 400, 0, true, "TQ", "TQ2 ??-prefix write pattern" },

    // 405–420: Write patterns (Satisfactory UE 4.25, ES2 UE 5.3)
    { "GWLD_SAT425_3", AOB_GWORLD_SAT425_3, AobTarget::GWorld, AobResolve::RipBoth,
      0, 3, 7, 0, 405, 0, true, "SAT425", "Satisfactory UE4.25 write + R15 context" },
    { "GWLD_SAT425_2", AOB_GWORLD_SAT425_2, AobTarget::GWorld, AobResolve::RipBoth,
      0, 3, 7, 0, 410, 0, true, "SAT425", "Satisfactory UE4.25 write + TLS" },
    { "GWLD_ES53_1", AOB_GWORLD_ES53_1, AobTarget::GWorld, AobResolve::RipBoth,
      0, 3, 7, 0, 415, 0, true, "ES53", "ES2 UE5.3 UGameEngine::Tick MOVAPS write" },
    { "GWLD_ES53_2", AOB_GWORLD_ES53_2, AobTarget::GWorld, AobResolve::RipBoth,
      0, 3, 7, 0, 420, 0, true, "ES53", "ES2 UE5.3 UGameEngine::Tick RCX write" },

    // 425–435: Satisfactory write patterns
    { "GWLD_SAT422_2", AOB_GWORLD_SAT422_2, AobTarget::GWorld, AobResolve::RipBoth,
      0, 3, 7, 0, 425, 0, true, "SAT422", "Satisfactory UE4.22 SetGlobalWorld RCX write" },
    { "GWLD_SAT426_2", AOB_GWORLD_SAT426_2, AobTarget::GWorld, AobResolve::RipBoth,
      0, 3, 7, 0, 430, 0, true, "SAT426", "Satisfactory UE4.26 FinishDestroy RAX write" },
    { "GWLD_SAT52_2", AOB_GWORLD_SAT52_2, AobTarget::GWorld, AobResolve::RipBoth,
      0, 3, 7, 0, 435, 0, true, "SAT52", "Satisfactory UE5.2 UGameEngine::Tick RCX write" },

    // 900–980: the short V-series, moved out of the Tier-3 band in build 2407 (was 500–580).
    // These are the noisiest block in the whole file: over 31 programs GWLD_V3 alone takes
    // 22,017 matches — 95.7 per MB of .text on a monolithic game EXE, and 2,658 on FF7 Remake by
    // itself — out of SIX literal bytes (`mov rbx,[rip+d32]; test rbx,rbx`, an idiom every UE
    // global gets). V2/V4/V5/V6 reach the true GWorld on ZERO oracles and V3 never wins one.
    //
    // Be precise about what this buys, because it is NOT a cost saving: at 500–580 they already
    // sat behind every other GWorld pattern (the highest was GWLD_SAT52_2 at 435), so the
    // validator never reached them on any oracle anyway. The change is about the band MEANING
    // something — a 4-literal-byte pattern must not outrank a 25-byte one, so that the next
    // pattern added at 500 is not silently placed behind these. The one genuine ordering change
    // here is GWLD_G42_1 (7 literal bytes), moved 340 -> 880 so that it no longer outranks the
    // 10-14-byte SAT422/SAT425/SAT426/G427 block it used to precede.
    SIG_GWORLD_RIP("GWLD_V3",    AOB_GWORLD_V3,     0, 3, 7, 0, 900, false, "V", "mov rbx test rbx"),
    SIG_GWORLD_RIP("GWLD_V4",    AOB_GWORLD_V4,     0, 3, 7, 0, 920, false, "V", "mov rdi test rdi"),
    SIG_GWORLD_RIP("GWLD_V5",    AOB_GWORLD_V5,     0, 3, 7, 0, 940, false, "V", "cmp [rip] je"),
    { "GWLD_V2", AOB_GWORLD_V2, AobTarget::GWorld, AobResolve::RipBoth,
      0, 3, 7, 0, 960, 0, true, "V", "write: mov [rip],rax" },
    { "GWLD_V6", AOB_GWORLD_V6, AobTarget::GWorld, AobResolve::RipBoth,
      0, 3, 7, 0, 980, 0, true, "V", "write: mov [rip],rbx; call" },
};

// ── SparseDelegates (FSparseDelegateStorage::SparseDelegates) ────────────
// Lazily resolved on first MulticastSparseDelegateProperty drill-down — NOT
// part of the FindAll boot sequence. Resolves directly to the TMap value.
constexpr AobSignature SPARSE_PATTERNS[] = {
    // Solarpunk UE 5.7 — SPARSE_ES2_1 gets 0 hits on this build; these anchor on the
    // TSet element-index math instead. RipDirect -> TSet object base. Verified.
    SIG_RIP_DIRECT("SPARSE_SP57_1", AOB_SPARSE_SP57_1, AobTarget::SparseDelegates,
                   0, 3, 7, 0, 100,
                   "SP57", "UE5.7 TSet::Find/FindOrAdd/Emplace element index (mov rdx)"),
    SIG_RIP_DIRECT("SPARSE_DI427_1", AOB_SPARSE_DI427_1, AobTarget::SparseDelegates,
                   14, 3, 7, 0, 110,
                   "DI427", "UE4.27 EnterCriticalSection + TSet::FindId head (no stride math)"),
    SIG_RIP_DIRECT("SPARSE_SP57_2", AOB_SPARSE_SP57_2, AobTarget::SparseDelegates,
                   0, 3, 7, 0, 120,
                   "SP57", "UE5.7 TSet::Remove element index (mov r8, unique)"),
    SIG_RIP_DIRECT("SPARSE_DI427_2", AOB_SPARSE_DI427_2, AobTarget::SparseDelegates,
                   21, 3, 7, 0, 130,
                   "DI427", "UE4.27 element addr + inner-TMap fetch tail (tail is load-bearing)"),
    SIG_RIP_DIRECT("SPARSE_ES2_1", AOB_SPARSE_ES2_1, AobTarget::SparseDelegates,
                   16, 3, 7, 0, 140,
                   "ES2", "UE4.27 (DropIn, PDB-verified) + ES2 5.4 + TQ2 5.7 NotifyUObjectDeleted twin-ref"),
};

// ── GEngine (UEngine* GEngine — the &GEngine SLOT) ───────────────────────
// Resolved AFTER GObjects/GNames/offsets in FindAll, because the validator has to
// deref the slot and ask the reflected class for a "GameViewport" property.
// X1/X2 are cross-version (verified on UE 4.27 + UE 5.7; X1 also matches UE 5.3).
// Ordering is empirical, from a SIX-binary sweep with real symbols on five of them:
// Everspace 4.20, DropIn 4.27, ES2 5.5, Satisfactory 5.6, Solarpunk 5.7 (+ Avowed 5.3,
// symbol-less, so it can only ever say "no hits" — never "wrong hit").
//
// X1 is the broadest single pattern in the file: UWorld::GetGameViewport is a tiny stable
// accessor that survives UE 4.20 -> 5.7 with only its stack size changing (hence `2?`).
//
// HISTORY worth keeping: X1 and DI427_1 were briefly demoted here on the strength of an
// apparent decoy count on Everspace 4.20. That was a measurement artifact — the sweep had
// been given a PLACEHOLDER truth value for that binary, so every hit necessarily compared
// unequal and got labelled a decoy. With Everspace's real PDB both are UNIQUE-OK on 4.20
// (X1 1/1, DI427_1 5/5). tools/ghidra/scan_patterns.java now emits NO-TRUTH instead of
// DECOY-ONLY when it has no plausible truth, so the same mistake cannot be made silently.
constexpr AobSignature GENGINE_PATTERNS[] = {
    // 0: Symbol export (O(1)) — modular builds export &GEngine from the Engine module.
    SIG_EXPORT("GENG_EXP", EXPORT_GENGINE, AobTarget::GEngine, 0, "MSVC mangled symbol"),

    SIG_RIP_DIRECT("GENG_X1", AOB_GENGINE_X1, AobTarget::GEngine,
                   7, 3, 7, 0, 100, "DI427+SP57", "UWorld::GetGameViewport — UE4.20+4.27+5.7, decoy-free"),
    SIG_RIP_DIRECT("GENG_X3", AOB_GENGINE_X3, AobTarget::GEngine,
                   7, 3, 7, 0, 105, "X+FF7R", "X1 head only (no test/jz tail) — reaches FF7R UE4.18"),
    SIG_RIP_DIRECT("GENG_X2", AOB_GENGINE_X2, AobTarget::GEngine,
                   0, 3, 7, 0, 110, "DI427+SP57", "FEngineLoop::Tick (UE4.27+5.7, 6-7 sites)"),
    SIG_RIP_DIRECT("GENG_ES55_1", AOB_GENGINE_ES55_1, AobTarget::GEngine,
                   10, 3, 7, 0, 120, "ES55", "UE5.5+5.7 UEngine::GetEngineSubsystem<T> prologue"),
    SIG_RIP_DIRECT("GENG_SP57_1", AOB_GENGINE_SP57_1, AobTarget::GEngine,
                   10, 3, 7, 0, 130, "SP57", "UE5.5+5.7 UEngine::IsStereoscopic3D"),
    SIG_RIP_DIRECT("GENG_DI427_1", AOB_GENGINE_DI427_1, AobTarget::GEngine,
                   13, 3, 7, 0, 140, "DI427", "UE4.20+4.27 GetRealTimeSeconds shape (5-6 sites)"),
};

#undef SIG_RIP
#undef SIG_RIP_DIRECT
#undef SIG_EXPORT
#undef SIG_SYM_CALL
#undef SIG_GWORLD_RIP


// ============================================================
// Pattern count summary
// ============================================================
// GObjects: 55 AOB patterns + 1 symbol export
// GNames:   28 AOB patterns + 1 CallFollow + 3 symbol exports  (CT2 removed b2407 — see note)
// GWorld:   53 AOB patterns + 1 symbol export
// SparseDelegates: 5 — lazily resolved, not part of the FindAll boot sequence
// GEngine:   6 AOB patterns + 1 symbol export — resolved after GObjects/GNames
// Total:   147 AOB patterns + 1 CallFollow + 6 symbol exports = 154 entries (from 18 sources)
//
// Keep these in sync by running:  py tools/ghidra/extract_patterns.py dll/src/Himmel.h out.tsv
// which prints the per-target counts it parses out of this file.

} // namespace Sig
