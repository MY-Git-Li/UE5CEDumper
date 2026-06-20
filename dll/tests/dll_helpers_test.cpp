// ============================================================
// dll_helpers_test
//
// Stand-alone executable (no GoogleTest / Catch2 dependency) covering pure
// helpers in Renge.h (TryStrToAddr) and Scharf.h (IsAlignmentSuspicious).
// Same EXPECT-style harness as utf8_helpers_test.cpp; exit code = failure count.
//
// Why a separate exe? Both helpers used to be inlined into hot-path code
// (Fern.cpp pipe handler and Ubel.cpp WalkInstance) where they couldn't be
// exercised without a real game process. Extracting them to small headers
// makes regressions catchable at build time.
//
// Real-world cases driving these tests come from cross-game logs:
//   - Renge: Squirrel With A Gun sent {"addr":"0x[ply_base]"} (unsubstituted
//     CE placeholder), throwing std::invalid_argument and crashing the pipe
//     command. TryStrToAddr now returns false on any non-hex input.
//   - Scharf: Meltopia (UE 5.0.5) emitted ~75 "Misaligned field"
//     warnings per session for legitimate uint8 EnumProperty / FName layouts.
//     RequiredAlignment now consults ElemSize and CasePreservingName mode.
// ============================================================

#include "../src/Renge.h"
#include "../src/Scharf.h"
#include "../src/Radar.h"
#include "../src/Macht.h"   // ComputeSetElementStride / ComputeMapValueOffset (V1a geometry)
#include "../src/Denken.h"
#include "../src/Lineal.h"  // UE5.7+ packed FUObjectItem reconstruction (Reconstruct/Encode)
#include "../src/Neu.h"     // UEnum::Names layout parse (legacy TArray vs UE5.6+ FNameData)
#include "../src/GraphPath.h"   // Pure BFS shortest-path core ("Locate in GWorld")
#include "../src/Solitar.h"     // GodMode FBoolProperty bit write (ApplyBoolBit, header-inline)
#include "../src/Orden.h"       // Multi-value group scan: source-agnostic SDR matcher (MatchGroup)
#include "../src/Ubel.h"        // Native-C scan P0: ComputeHoles / ComputeClassHoles / NormalizeGuessedTypeToProperty (inline, pure)

#include <Windows.h>
#include <timeapi.h>   // timeBeginPeriod — exercised by the poll-latency check

#include <algorithm>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

static int g_pass = 0;
static int g_fail = 0;

#define EXPECT(label, cond) do { \
    if (cond) { ++g_pass; } \
    else { ++g_fail; std::printf("  FAIL: %s\n    at %s:%d\n", label, __FILE__, __LINE__); } \
} while (0)

#define EXPECT_EQ_U64(label, actual, expected) do { \
    uint64_t _a = static_cast<uint64_t>(actual); \
    uint64_t _e = static_cast<uint64_t>(expected); \
    if (_a == _e) { ++g_pass; } \
    else { \
        ++g_fail; \
        std::printf("  FAIL: %s\n    actual=0x%llX expected=0x%llX\n    at %s:%d\n", \
            label, (unsigned long long)_a, (unsigned long long)_e, __FILE__, __LINE__); \
    } \
} while (0)

// ----- TryStrToAddr ----------------------------------------------------------

static void Test_TryStrToAddr_AcceptsValidHex() {
    uintptr_t v = 0;
    EXPECT("0x prefix",       Renge::TryStrToAddr("0x1F809E08FB0", v));
    EXPECT_EQ_U64("0x1F809E08FB0", v, 0x1F809E08FB0ULL);

    v = 0;
    EXPECT("0X prefix uppercase", Renge::TryStrToAddr("0X1f809e08fb0", v));
    EXPECT_EQ_U64("0X1f809e08fb0", v, 0x1F809E08FB0ULL);

    v = 0;
    EXPECT("no prefix",       Renge::TryStrToAddr("1A2B3C", v));
    EXPECT_EQ_U64("1A2B3C", v, 0x1A2B3CULL);

    v = 0;
    EXPECT("zero",            Renge::TryStrToAddr("0x0", v));
    EXPECT_EQ_U64("zero=0", v, 0ULL);

    v = 0;
    EXPECT("max 64-bit",      Renge::TryStrToAddr("0xFFFFFFFFFFFFFFFF", v));
    EXPECT_EQ_U64("max 64-bit", v, 0xFFFFFFFFFFFFFFFFULL);

    v = 0;
    EXPECT("trailing whitespace tolerated", Renge::TryStrToAddr("0x1234 ", v));
    EXPECT_EQ_U64("trailing space", v, 0x1234ULL);
}

static void Test_TryStrToAddr_RejectsCePlaceholder() {
    // The Squirrel With A Gun crash: UI sent unsubstituted "0x[ply_base]"
    uintptr_t v = 0xDEADBEEF;
    EXPECT("rejects 0x[ply_base]", !Renge::TryStrToAddr("0x[ply_base]", v));
    EXPECT("outAddr untouched on failure", v == 0xDEADBEEF);
}

static void Test_TryStrToAddr_RejectsTrailingGarbage() {
    uintptr_t v = 0;
    EXPECT("rejects 0x123junk",   !Renge::TryStrToAddr("0x123junk", v));
    EXPECT("rejects 0xABC]",      !Renge::TryStrToAddr("0xABC]", v));
    EXPECT("rejects 0x12 0x34",   !Renge::TryStrToAddr("0x12 0x34", v));
}

static void Test_TryStrToAddr_RejectsEmpty() {
    uintptr_t v = 0;
    EXPECT("rejects empty",       !Renge::TryStrToAddr("", v));
    EXPECT("rejects whitespace",  !Renge::TryStrToAddr("   ", v));
    EXPECT("rejects 0x alone",    !Renge::TryStrToAddr("0x", v));
}

static void Test_TryStrToAddr_RejectsNonHex() {
    uintptr_t v = 0;
    EXPECT("rejects ply_base",    !Renge::TryStrToAddr("ply_base", v));
    EXPECT("rejects -1",          !Renge::TryStrToAddr("-1", v));
    EXPECT("rejects negative hex",!Renge::TryStrToAddr("-0x1", v));
    EXPECT("rejects null literal",!Renge::TryStrToAddr("null", v));
}

static void Test_StrToAddr_NoexceptZeroOnFailure() {
    // Legacy convenience wrapper must not throw on any input.
    EXPECT_EQ_U64("malformed → 0",         Renge::StrToAddr("0x[ply_base]"), 0ULL);
    EXPECT_EQ_U64("empty → 0",             Renge::StrToAddr(""), 0ULL);
    EXPECT_EQ_U64("ply_base → 0",          Renge::StrToAddr("ply_base"), 0ULL);
    EXPECT_EQ_U64("valid still parses",    Renge::StrToAddr("0xCAFE"), 0xCAFEULL);
}

// ----- Scharf::IsAlignmentSuspicious --------------------------------

static void Test_Alignment_PointerProperties_Need8() {
    // Pointer-shaped fields at 8-aligned offsets — never suspicious.
    EXPECT("ObjectProperty @ 0x10 OK",      !Scharf::IsAlignmentSuspicious("ObjectProperty", 0x10, 8, false));
    EXPECT("ClassProperty @ 0x40 OK",       !Scharf::IsAlignmentSuspicious("ClassProperty",  0x40, 8, false));
    EXPECT("InterfaceProperty @ 0x18 OK",   !Scharf::IsAlignmentSuspicious("InterfaceProperty", 0x18, 16, false));

    // Misaligned pointer — real concern.
    EXPECT("ObjectProperty @ 0x4 BAD",       Scharf::IsAlignmentSuspicious("ObjectProperty", 0x4, 8, false));
    EXPECT("ArrayProperty @ 0x14 BAD",       Scharf::IsAlignmentSuspicious("ArrayProperty",  0x14, 16, false));
}

static void Test_Alignment_EnumProperty_RespectsElemSize() {
    // Real-world Meltopia / CaravanSandWitch case:
    //   "DefaultUpdateOverlapsMethodDuringLevelStreaming" (EnumProperty) at offset 0x5F
    //   ElemSize = 1 (uint8 enum) — 0x5F % 1 == 0 → not suspicious
    EXPECT("uint8 enum @ 0x5F OK",  !Scharf::IsAlignmentSuspicious("EnumProperty", 0x5F, 1, false));
    EXPECT("uint8 enum @ 0x16A OK", !Scharf::IsAlignmentSuspicious("EnumProperty", 0x16A, 1, false));
    EXPECT("uint8 enum @ 0x99A OK", !Scharf::IsAlignmentSuspicious("EnumProperty", 0x99A, 1, false));
    EXPECT("uint8 enum @ 0x5E OK",  !Scharf::IsAlignmentSuspicious("EnumProperty", 0x5E, 1, false));

    // uint16 enum: alignment 2.
    EXPECT("uint16 enum @ 0x6 OK",  !Scharf::IsAlignmentSuspicious("EnumProperty", 0x6, 2, false));
    EXPECT("uint16 enum @ 0x5 BAD",  Scharf::IsAlignmentSuspicious("EnumProperty", 0x5, 2, false));

    // uint32 enum: alignment 4.
    EXPECT("uint32 enum @ 0xC OK",  !Scharf::IsAlignmentSuspicious("EnumProperty", 0xC, 4, false));
    EXPECT("uint32 enum @ 0xA BAD",  Scharf::IsAlignmentSuspicious("EnumProperty", 0xA, 4, false));
}

static void Test_Alignment_NameProperty_RespectsCpnMode() {
    // Non-CPN: FName = 8 bytes (int32 + int32), aligned to 4.
    //   CaravanSandWitch case: "MipFilter" (NameProperty) at offset 0x3C, ElemSize=8
    //   0x3C % 4 == 0 → not suspicious
    EXPECT("non-CPN FName @ 0x3C OK", !Scharf::IsAlignmentSuspicious("NameProperty", 0x3C, 8, false));
    EXPECT("non-CPN FName @ 0x4 OK",  !Scharf::IsAlignmentSuspicious("NameProperty", 0x4, 8, false));
    EXPECT("non-CPN FName @ 0x3 BAD",  Scharf::IsAlignmentSuspicious("NameProperty", 0x3, 8, false));

    // CPN (Titan Quest II): FName = 16 bytes, aligned to 8.
    EXPECT("CPN FName @ 0x10 OK", !Scharf::IsAlignmentSuspicious("NameProperty", 0x10, 16, true));
    EXPECT("CPN FName @ 0xC BAD",  Scharf::IsAlignmentSuspicious("NameProperty", 0xC, 16, true));
}

static void Test_Alignment_ScalarPrimitives() {
    // BoolProperty / ByteProperty: 1-byte aligned, never suspicious.
    EXPECT("Bool @ 0x1 OK",  !Scharf::IsAlignmentSuspicious("BoolProperty", 0x1, 1, false));
    EXPECT("Byte @ 0x7 OK",  !Scharf::IsAlignmentSuspicious("ByteProperty", 0x7, 1, false));

    // IntProperty / FloatProperty: 4-byte aligned.
    EXPECT("Int @ 0x4 OK",   !Scharf::IsAlignmentSuspicious("IntProperty", 0x4, 4, false));
    EXPECT("Int @ 0x6 BAD",   Scharf::IsAlignmentSuspicious("IntProperty", 0x6, 4, false));

    // Int64Property: 8-byte aligned.
    EXPECT("Int64 @ 0x8 OK", !Scharf::IsAlignmentSuspicious("Int64Property", 0x8, 8, false));
    EXPECT("Int64 @ 0xC BAD", Scharf::IsAlignmentSuspicious("Int64Property", 0xC, 8, false));
}

static void Test_Alignment_OffsetZeroNeverSuspicious() {
    EXPECT("Object @ 0 OK",     !Scharf::IsAlignmentSuspicious("ObjectProperty", 0, 8, false));
    EXPECT("Enum @ 0 OK",       !Scharf::IsAlignmentSuspicious("EnumProperty", 0, 1, false));
    EXPECT("Name CPN @ 0 OK",   !Scharf::IsAlignmentSuspicious("NameProperty", 0, 16, true));
}

static void Test_Alignment_UnknownTypesNotValidated() {
    // StructProperty layout depends on the script struct — skip alignment check.
    EXPECT("Struct @ 0x3 not flagged",
           !Scharf::IsAlignmentSuspicious("StructProperty", 0x3, 32, false));
    // FieldPathProperty / OptionalProperty / unknown types: skip.
    EXPECT("FieldPath @ 0x5 not flagged",
           !Scharf::IsAlignmentSuspicious("FieldPathProperty", 0x5, 16, false));
    EXPECT("OptionalProperty @ 0x9 not flagged",
           !Scharf::IsAlignmentSuspicious("OptionalProperty", 0x9, 8, false));
    EXPECT("garbage type not flagged",
           !Scharf::IsAlignmentSuspicious("GarbageProperty", 0x1, 4, false));
}

static void Test_Alignment_WeakAndSparseDelegate() {
    // FWeakObjectPtr: 2x int32, 4-byte aligned.
    EXPECT("Weak @ 0x4 OK",    !Scharf::IsAlignmentSuspicious("WeakObjectProperty", 0x4, 8, false));
    EXPECT("Weak @ 0x2 BAD",    Scharf::IsAlignmentSuspicious("WeakObjectProperty", 0x2, 8, false));

    // MulticastSparseDelegateProperty: only 1 byte stored on the field.
    EXPECT("SparseDelegate @ 0x5 OK",
           !Scharf::IsAlignmentSuspicious("MulticastSparseDelegateProperty", 0x5, 1, false));
}

// ----- Mimic poll-latency micro-benchmark ------------------------------------
//
// Mimic.cpp's polling thread does `Sleep(kPollIntervalMs)` (=1) every iteration
// and bumps timer resolution via timeBeginPeriod(1) so Sleep(1) actually
// delivers ~1ms latency. This test reproduces the same setup in the test
// process and asserts that 100 × Sleep(1) takes < 200ms wall-clock.
//
// Without timeBeginPeriod, Sleep(1) on a system with the default 15.6ms tick
// rounds up to 15.6ms per call → 100 calls = ~1560ms, which would fail this
// assertion. So a green test confirms the Mimic-side latency reduction is
// actually achievable on this host's OS configuration.
//
// 300ms threshold (vs. ideal ~100ms): generous to account for CI scheduler
// jitter, thread contention, and the kernel's discretion on tick rounding.
// Idle baseline on a quiet machine landed at 193ms (≈1.94ms/sleep) — Windows
// commonly rounds Sleep(1) up to the next 1-2ms tick boundary, so anything
// under ~250ms confirms timeBeginPeriod is in effect. The 5× headroom keeps
// the test from flaking under heavy load while still catching the legacy-
// tick regression cleanly (which would land near 1560ms).

static void Test_Mimic_PollLatency_OneMillisecond() {
    // Mirror the DLL polling thread's timer-resolution request.
    MMRESULT rc = timeBeginPeriod(1);
    EXPECT("timeBeginPeriod(1) ok", rc == TIMERR_NOERROR);
    if (rc != TIMERR_NOERROR) {
        std::printf("  [warn] timeBeginPeriod failed rc=%u — skipping latency assert\n", rc);
        return;
    }

    LARGE_INTEGER freq{}, start{}, end{};
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&start);

    constexpr int kIters = 100;
    for (int i = 0; i < kIters; ++i) {
        Sleep(1);
    }

    QueryPerformanceCounter(&end);
    timeEndPeriod(1);

    double elapsedMs = double(end.QuadPart - start.QuadPart) * 1000.0 / double(freq.QuadPart);
    std::printf("  [info] 100 x Sleep(1) under timeBeginPeriod(1) = %.1f ms "
                "(avg %.2f ms/sleep)\n",
                elapsedMs, elapsedMs / kIters);

    // Hard ceiling: if a sleep really cost the legacy 15.6ms tick, this would
    // be ~1560ms. 300ms catches that regression cleanly while tolerating noise.
    if (elapsedMs >= 300.0) {
        ++g_fail;
        std::printf("  FAIL: poll-latency over threshold\n"
                    "    actual=%.1f ms expected<200 ms\n"
                    "    at %s:%d\n", elapsedMs, __FILE__, __LINE__);
    } else {
        ++g_pass;
    }
}

// ----- Radar: SizeOf + NameOf + parsers ---------------------------------

static void Test_ValueScan_DataTypeSizes() {
    EXPECT("SizeOf Int8 = 1",   Radar::SizeOf(Radar::DataType::Int8)   == 1);
    EXPECT("SizeOf Int16 = 2",  Radar::SizeOf(Radar::DataType::Int16)  == 2);
    EXPECT("SizeOf Int32 = 4",  Radar::SizeOf(Radar::DataType::Int32)  == 4);
    EXPECT("SizeOf Int64 = 8",  Radar::SizeOf(Radar::DataType::Int64)  == 8);
    EXPECT("SizeOf UInt8 = 1",  Radar::SizeOf(Radar::DataType::UInt8)  == 1);
    EXPECT("SizeOf UInt16 = 2", Radar::SizeOf(Radar::DataType::UInt16) == 2);
    EXPECT("SizeOf UInt32 = 4", Radar::SizeOf(Radar::DataType::UInt32) == 4);
    EXPECT("SizeOf UInt64 = 8", Radar::SizeOf(Radar::DataType::UInt64) == 8);
    EXPECT("SizeOf Float = 4",  Radar::SizeOf(Radar::DataType::Float)  == 4);
    EXPECT("SizeOf Double = 8", Radar::SizeOf(Radar::DataType::Double) == 8);
    EXPECT("SizeOf Bool = 1",   Radar::SizeOf(Radar::DataType::Bool)   == 1);
    // Phase 2A: string types — variable length, signalled by SizeOf = 0.
    EXPECT("SizeOf FString = 0", Radar::SizeOf(Radar::DataType::FString) == 0);
    EXPECT("SizeOf FName = 0",   Radar::SizeOf(Radar::DataType::FName)   == 0);
    EXPECT("SizeOf FText = 0",   Radar::SizeOf(Radar::DataType::FText)   == 0);
    // Phase 2B: vector types — three floats = 12 bytes.
    EXPECT("SizeOf FVector = 12",    Radar::SizeOf(Radar::DataType::FVector)    == 12);
    EXPECT("SizeOf FRotator = 12",   Radar::SizeOf(Radar::DataType::FRotator)   == 12);
    EXPECT("SizeOf FTransform = 12", Radar::SizeOf(Radar::DataType::FTransform) == 12);
    // Multi-numeric meta types — variable width, signalled by SizeOf = 0.
    EXPECT("SizeOf NumericNoByte = 0", Radar::SizeOf(Radar::DataType::NumericNoByte) == 0);
    EXPECT("SizeOf NumericAll = 0",    Radar::SizeOf(Radar::DataType::NumericAll)    == 0);
}

static void Test_ValueScan_ParseDataTypeRoundTrip() {
    using DT = Radar::DataType;
    DT got;
    EXPECT("parse Int32",   Radar::TryParseDataType("Int32",  got) && got == DT::Int32);
    EXPECT("parse Float",   Radar::TryParseDataType("Float",  got) && got == DT::Float);
    EXPECT("parse Bool",    Radar::TryParseDataType("Bool",   got) && got == DT::Bool);
    EXPECT("parse UInt64",  Radar::TryParseDataType("UInt64", got) && got == DT::UInt64);
    // Phase 2 DataTypes — locks the wire-protocol shape.
    EXPECT("parse FString", Radar::TryParseDataType("FString", got) && got == DT::FString);
    EXPECT("parse FName",   Radar::TryParseDataType("FName",   got) && got == DT::FName);
    EXPECT("parse FText",   Radar::TryParseDataType("FText",   got) && got == DT::FText);
    EXPECT("parse FVector",  Radar::TryParseDataType("FVector",  got) && got == DT::FVector);
    EXPECT("parse FRotator", Radar::TryParseDataType("FRotator", got) && got == DT::FRotator);
    EXPECT("parse FTransform", Radar::TryParseDataType("FTransform", got) && got == DT::FTransform);
    // Multi-numeric meta DataTypes — locks the wire-protocol shape.
    EXPECT("parse NumericNoByte", Radar::TryParseDataType("NumericNoByte", got) && got == DT::NumericNoByte);
    EXPECT("parse NumericAll",    Radar::TryParseDataType("NumericAll",    got) && got == DT::NumericAll);
    EXPECT("parse rejects unknown", !Radar::TryParseDataType("TArray<Int32>", got));
    EXPECT("parse rejects empty",   !Radar::TryParseDataType("",              got));
}

static void Test_ValueScan_ScanTypePartitioning() {
    using ST = Radar::ScanType;
    EXPECT("Exact is first-scan",      Radar::IsFirstScanType(ST::Exact));
    EXPECT("Bigger is first-scan",     Radar::IsFirstScanType(ST::Bigger));
    EXPECT("Smaller is first-scan",    Radar::IsFirstScanType(ST::Smaller));
    EXPECT("Between is first-scan",    Radar::IsFirstScanType(ST::Between));
    EXPECT("Changed is prev-value",    Radar::IsPrevValueScanType(ST::Changed));
    EXPECT("Unchanged is prev-value",  Radar::IsPrevValueScanType(ST::Unchanged));
    EXPECT("Increased is prev-value",  Radar::IsPrevValueScanType(ST::Increased));
    EXPECT("Decreased is prev-value",  Radar::IsPrevValueScanType(ST::Decreased));
    // No overlap between first-scan and prev-value partitions:
    EXPECT("Exact is NOT prev-value",  !Radar::IsPrevValueScanType(ST::Exact));
    EXPECT("Changed is NOT first-scan", !Radar::IsFirstScanType(ST::Changed));
    // Phase 2A: substring predicates are first-scan eligible.
    EXPECT("Contains is first-scan",   Radar::IsFirstScanType(ST::Contains));
    EXPECT("StartsWith is first-scan", Radar::IsFirstScanType(ST::StartsWith));
    EXPECT("EndsWith is first-scan",   Radar::IsFirstScanType(ST::EndsWith));
    EXPECT("Contains is NOT prev-value",   !Radar::IsPrevValueScanType(ST::Contains));
}

static void Test_ValueScan_TypeFamilyPredicates() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    // IsStringDataType: only the three string types.
    EXPECT("FString isString",  Radar::IsStringDataType(DT::FString));
    EXPECT("FName isString",    Radar::IsStringDataType(DT::FName));
    EXPECT("FText isString",    Radar::IsStringDataType(DT::FText));
    EXPECT("Int32 NOT isString", !Radar::IsStringDataType(DT::Int32));
    EXPECT("Float NOT isString", !Radar::IsStringDataType(DT::Float));
    EXPECT("FVector NOT isString", !Radar::IsStringDataType(DT::FVector));
    // IsVectorDataType: only the three vector types.
    EXPECT("FVector isVector",    Radar::IsVectorDataType(DT::FVector));
    EXPECT("FRotator isVector",   Radar::IsVectorDataType(DT::FRotator));
    EXPECT("FTransform isVector", Radar::IsVectorDataType(DT::FTransform));
    EXPECT("Int32 NOT isVector",  !Radar::IsVectorDataType(DT::Int32));
    EXPECT("FString NOT isVector", !Radar::IsVectorDataType(DT::FString));
    // IsSubstringScanType: only Contains/StartsWith/EndsWith.
    EXPECT("Contains is substring",   Radar::IsSubstringScanType(ST::Contains));
    EXPECT("StartsWith is substring", Radar::IsSubstringScanType(ST::StartsWith));
    EXPECT("EndsWith is substring",   Radar::IsSubstringScanType(ST::EndsWith));
    EXPECT("Exact NOT substring",   !Radar::IsSubstringScanType(ST::Exact));
    EXPECT("Bigger NOT substring",  !Radar::IsSubstringScanType(ST::Bigger));
    EXPECT("Changed NOT substring", !Radar::IsSubstringScanType(ST::Changed));
}

static void Test_ValueScan_IsScanTypeValidFor() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    // Numerics: substring predicates reject, ordering predicates accept.
    EXPECT("Int32 Exact valid",    Radar::IsScanTypeValidFor(DT::Int32, ST::Exact));
    EXPECT("Int32 Bigger valid",   Radar::IsScanTypeValidFor(DT::Int32, ST::Bigger));
    EXPECT("Int32 Changed valid",  Radar::IsScanTypeValidFor(DT::Int32, ST::Changed));
    EXPECT("Int32 Contains REJ",   !Radar::IsScanTypeValidFor(DT::Int32, ST::Contains));
    EXPECT("Int32 StartsWith REJ", !Radar::IsScanTypeValidFor(DT::Int32, ST::StartsWith));
    EXPECT("Float EndsWith REJ",   !Radar::IsScanTypeValidFor(DT::Float, ST::EndsWith));
    // Strings: ordering predicates reject, substring + Exact + Changed/Unchanged accept.
    EXPECT("FString Exact valid",     Radar::IsScanTypeValidFor(DT::FString, ST::Exact));
    EXPECT("FString Contains valid",  Radar::IsScanTypeValidFor(DT::FString, ST::Contains));
    EXPECT("FString StartsWith valid", Radar::IsScanTypeValidFor(DT::FString, ST::StartsWith));
    EXPECT("FName EndsWith valid",    Radar::IsScanTypeValidFor(DT::FName,   ST::EndsWith));
    EXPECT("FText Changed valid",     Radar::IsScanTypeValidFor(DT::FText,   ST::Changed));
    EXPECT("FText Unchanged valid",   Radar::IsScanTypeValidFor(DT::FText,   ST::Unchanged));
    EXPECT("FString Bigger REJ",   !Radar::IsScanTypeValidFor(DT::FString, ST::Bigger));
    EXPECT("FString Smaller REJ",  !Radar::IsScanTypeValidFor(DT::FString, ST::Smaller));
    EXPECT("FString Between REJ",  !Radar::IsScanTypeValidFor(DT::FString, ST::Between));
    EXPECT("FString Increased REJ", !Radar::IsScanTypeValidFor(DT::FString, ST::Increased));
    EXPECT("FString Decreased REJ", !Radar::IsScanTypeValidFor(DT::FString, ST::Decreased));
    // Vectors: substring predicates reject; ordering predicates accept.
    EXPECT("FVector Exact valid",    Radar::IsScanTypeValidFor(DT::FVector, ST::Exact));
    EXPECT("FVector Bigger valid",   Radar::IsScanTypeValidFor(DT::FVector, ST::Bigger));
    EXPECT("FVector Between valid",  Radar::IsScanTypeValidFor(DT::FVector, ST::Between));
    EXPECT("FVector Changed valid",  Radar::IsScanTypeValidFor(DT::FVector, ST::Changed));
    EXPECT("FRotator Contains REJ", !Radar::IsScanTypeValidFor(DT::FRotator, ST::Contains));
    // Multi-numeric meta type behaves like a numeric: ordering accept,
    // substring reject.
    EXPECT("NumericNoByte Exact valid",   Radar::IsScanTypeValidFor(DT::NumericNoByte, ST::Exact));
    EXPECT("NumericNoByte Bigger valid",  Radar::IsScanTypeValidFor(DT::NumericNoByte, ST::Bigger));
    EXPECT("NumericNoByte Between valid", Radar::IsScanTypeValidFor(DT::NumericNoByte, ST::Between));
    EXPECT("NumericNoByte Changed valid", Radar::IsScanTypeValidFor(DT::NumericNoByte, ST::Changed));
    EXPECT("NumericNoByte Contains REJ", !Radar::IsScanTypeValidFor(DT::NumericNoByte, ST::Contains));
    EXPECT("NumericAll Exact valid",   Radar::IsScanTypeValidFor(DT::NumericAll, ST::Exact));
    EXPECT("NumericAll Bigger valid",  Radar::IsScanTypeValidFor(DT::NumericAll, ST::Bigger));
    EXPECT("NumericAll Contains REJ", !Radar::IsScanTypeValidFor(DT::NumericAll, ST::Contains));
}

// ----- Radar: multi-numeric meta type -----------------------------------

static void Test_ValueScan_MultiNumericMembers() {
    using DT = Radar::DataType;
    EXPECT("NumericNoByte is multi-numeric",  Radar::IsMultiNumericDataType(DT::NumericNoByte));
    EXPECT("NumericAll is multi-numeric",     Radar::IsMultiNumericDataType(DT::NumericAll));
    EXPECT("Int32 is NOT multi-numeric",     !Radar::IsMultiNumericDataType(DT::Int32));
    EXPECT("FString is NOT multi-numeric",   !Radar::IsMultiNumericDataType(DT::FString));

    const auto& m = Radar::MultiNumericMembers(DT::NumericNoByte);
    EXPECT("NumericNoByte has 8 members", m.size() == 8);
    auto has = [](const std::vector<DT>& v, DT d) {
        for (auto x : v) if (x == d) return true;
        return false;
    };
    EXPECT("members include Int16",  has(m, DT::Int16));
    EXPECT("members include UInt16", has(m, DT::UInt16));
    EXPECT("members include Int32",  has(m, DT::Int32));
    EXPECT("members include UInt32", has(m, DT::UInt32));
    EXPECT("members include Int64",  has(m, DT::Int64));
    EXPECT("members include UInt64", has(m, DT::UInt64));
    EXPECT("members include Float",  has(m, DT::Float));
    EXPECT("members include Double", has(m, DT::Double));
    // The "no byte" contract: no 1-byte or bool members.
    EXPECT("members exclude Int8",  !has(m, DT::Int8));
    EXPECT("members exclude UInt8", !has(m, DT::UInt8));
    EXPECT("members exclude Bool",  !has(m, DT::Bool));

    // NumericAll = NumericNoByte + { Int8, UInt8 } (10 members), still no Bool.
    const auto& ma = Radar::MultiNumericMembers(DT::NumericAll);
    EXPECT("NumericAll has 10 members", ma.size() == 10);
    EXPECT("NumericAll includes Int8",  has(ma, DT::Int8));
    EXPECT("NumericAll includes UInt8", has(ma, DT::UInt8));
    EXPECT("NumericAll includes Int32", has(ma, DT::Int32));
    EXPECT("NumericAll includes Double",has(ma, DT::Double));
    EXPECT("NumericAll excludes Bool", !has(ma, DT::Bool));
    // Non-meta types yield an empty member set.
    EXPECT("Int32 members empty", Radar::MultiNumericMembers(DT::Int32).empty());
}

static void Test_ValueScan_DataTypeFromPropertyTypeName() {
    using DT = Radar::DataType;
    DT got;
    EXPECT("IntProperty -> Int32",     Radar::TryDataTypeFromPropertyTypeName("IntProperty", got)    && got == DT::Int32);
    EXPECT("Int16Property -> Int16",   Radar::TryDataTypeFromPropertyTypeName("Int16Property", got)  && got == DT::Int16);
    EXPECT("Int64Property -> Int64",   Radar::TryDataTypeFromPropertyTypeName("Int64Property", got)  && got == DT::Int64);
    EXPECT("UInt16Property -> UInt16", Radar::TryDataTypeFromPropertyTypeName("UInt16Property", got) && got == DT::UInt16);
    EXPECT("UInt32Property -> UInt32", Radar::TryDataTypeFromPropertyTypeName("UInt32Property", got) && got == DT::UInt32);
    EXPECT("UInt64Property -> UInt64", Radar::TryDataTypeFromPropertyTypeName("UInt64Property", got) && got == DT::UInt64);
    EXPECT("FloatProperty -> Float",   Radar::TryDataTypeFromPropertyTypeName("FloatProperty", got)  && got == DT::Float);
    EXPECT("DoubleProperty -> Double", Radar::TryDataTypeFromPropertyTypeName("DoubleProperty", got) && got == DT::Double);
    // 1-byte families resolve too (NumericAll includes them; NumericNoByte
    // simply never feeds them in via its PropertyTypeNames union).
    EXPECT("ByteProperty -> UInt8",  Radar::TryDataTypeFromPropertyTypeName("ByteProperty", got) && got == DT::UInt8);
    EXPECT("Int8Property -> Int8",   Radar::TryDataTypeFromPropertyTypeName("Int8Property", got)  && got == DT::Int8);
    // Bool + non-numeric still reject.
    EXPECT("BoolProperty rejected",  !Radar::TryDataTypeFromPropertyTypeName("BoolProperty", got));
    EXPECT("StrProperty rejected",   !Radar::TryDataTypeFromPropertyTypeName("StrProperty", got));
    EXPECT("StructProperty rejected",!Radar::TryDataTypeFromPropertyTypeName("StructProperty", got));

    // PropertyTypeNames(meta) MUST be exactly the set that
    // TryDataTypeFromPropertyTypeName resolves — otherwise a field could
    // be accepted into the scan index yet fail per-field resolution.
    auto allResolve = [](const std::vector<std::string>& names) {
        for (const auto& n : names) {
            DT d;
            if (!Radar::TryDataTypeFromPropertyTypeName(n, d)) return false;
        }
        return true;
    };
    const auto& noByteNames = Radar::PropertyTypeNames(DT::NumericNoByte);
    EXPECT("NumericNoByte has 8 property names", noByteNames.size() == 8);
    EXPECT("every NumericNoByte property name resolves", allResolve(noByteNames));
    const auto& allNames = Radar::PropertyTypeNames(DT::NumericAll);
    EXPECT("NumericAll has 10 property names", allNames.size() == 10);
    EXPECT("every NumericAll property name resolves", allResolve(allNames));
}

static void Test_ValueScan_PropertyTypeNameOf_Inverse() {
    using DT = Radar::DataType;
    // PropertyTypeNameOf must be the exact inverse of
    // TryDataTypeFromPropertyTypeName for every concrete numeric width — the
    // Native-C scan stamps raw descriptors with PropertyTypeNameOf(dt) and refine
    // re-resolves them via TryDataTypeFromPropertyTypeName, so a mismatch would
    // silently drop native candidates on the first Next Scan.
    const DT widths[] = {
        DT::Int8, DT::UInt8, DT::Int16, DT::UInt16, DT::Int32,
        DT::UInt32, DT::Int64, DT::UInt64, DT::Float, DT::Double,
    };
    for (DT w : widths) {
        const char* name = Radar::PropertyTypeNameOf(w);
        EXPECT("PropertyTypeNameOf non-empty", name[0] != '\0');
        DT back;
        EXPECT("PropertyTypeNameOf round-trips",
               Radar::TryDataTypeFromPropertyTypeName(name, back) && back == w);
    }
    // Non-numeric / meta / bool have no property-type name.
    EXPECT("Bool -> empty",         Radar::PropertyTypeNameOf(DT::Bool)[0]        == '\0');
    EXPECT("FString -> empty",      Radar::PropertyTypeNameOf(DT::FString)[0]     == '\0');
    EXPECT("FVector -> empty",      Radar::PropertyTypeNameOf(DT::FVector)[0]     == '\0');
    EXPECT("NumericNoByte -> empty",Radar::PropertyTypeNameOf(DT::NumericNoByte)[0] == '\0');
}

// Helper: does the set contain an entry for `dt`, and (optionally) does
// it decode to the expected scalar value?
static void Test_ValueScan_BuildNumericTargets() {
    using DT = Radar::DataType;

    // "100" fits every member width.
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(100) ok", Radar::BuildNumericTargets(DT::NumericNoByte, "100", ts));
        EXPECT("100 fits all 8 widths", ts.entries.size() == 8);
        const uint8_t* i32 = ts.Find(DT::Int32);
        EXPECT("100 Int32 entry present", i32 != nullptr);
        if (i32) { int32_t v; std::memcpy(&v, i32, 4); EXPECT("100 Int32 decodes", v == 100); }
        const uint8_t* f = ts.Find(DT::Float);
        EXPECT("100 Float entry present", f != nullptr);
        if (f) { float v; std::memcpy(&v, f, 4); EXPECT("100 Float decodes", v == 100.0f); }
    }
    // "70000" overflows 16-bit widths — no Int16/UInt16 entries.
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(70000) ok", Radar::BuildNumericTargets(DT::NumericNoByte, "70000", ts));
        EXPECT("70000 has no Int16",  ts.Find(DT::Int16)  == nullptr);
        EXPECT("70000 has no UInt16", ts.Find(DT::UInt16) == nullptr);
        EXPECT("70000 has Int32",     ts.Find(DT::Int32)  != nullptr);
        EXPECT("70000 has UInt32",    ts.Find(DT::UInt32) != nullptr);
        EXPECT("70000 has Float",     ts.Find(DT::Float)  != nullptr);
    }
    // "-5" can't be unsigned — signed + float members only.
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(-5) ok", Radar::BuildNumericTargets(DT::NumericNoByte, "-5", ts));
        EXPECT("-5 has Int16",   ts.Find(DT::Int16)  != nullptr);
        EXPECT("-5 has Int32",   ts.Find(DT::Int32)  != nullptr);
        EXPECT("-5 has Float",   ts.Find(DT::Float)  != nullptr);
        EXPECT("-5 has NO UInt16", ts.Find(DT::UInt16) == nullptr);
        EXPECT("-5 has NO UInt32", ts.Find(DT::UInt32) == nullptr);
        EXPECT("-5 has NO UInt64", ts.Find(DT::UInt64) == nullptr);
    }
    // "100.5" is non-integral — float/double members only.
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(100.5) ok", Radar::BuildNumericTargets(DT::NumericNoByte, "100.5", ts));
        EXPECT("100.5 has 2 entries", ts.entries.size() == 2);
        EXPECT("100.5 has Float",  ts.Find(DT::Float)  != nullptr);
        EXPECT("100.5 has Double", ts.Find(DT::Double) != nullptr);
        EXPECT("100.5 has NO Int32", ts.Find(DT::Int32) == nullptr);
        const uint8_t* d = ts.Find(DT::Double);
        if (d) { double v; std::memcpy(&v, d, 8); EXPECT("100.5 Double decodes", v == 100.5); }
    }
    // Hex "0x10" → integer widths only (no float reinterpret).
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(0x10) ok", Radar::BuildNumericTargets(DT::NumericNoByte, "0x10", ts));
        EXPECT("0x10 has Int32",    ts.Find(DT::Int32) != nullptr);
        EXPECT("0x10 has NO Float", ts.Find(DT::Float) == nullptr);
        const uint8_t* i = ts.Find(DT::Int32);
        if (i) { int32_t v; std::memcpy(&v, i, 4); EXPECT("0x10 Int32 == 16", v == 16); }
    }
    // Empty / whitespace / garbage → false, no entries.
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets('') false", !Radar::BuildNumericTargets(DT::NumericNoByte, "", ts));
        EXPECT("empty leaves no entries", ts.entries.empty());
        EXPECT("BuildNumericTargets('  ') false", !Radar::BuildNumericTargets(DT::NumericNoByte, "   ", ts));
        EXPECT("BuildNumericTargets('abc') false", !Radar::BuildNumericTargets(DT::NumericNoByte, "abc", ts));
    }
    // Non-meta data type yields no targets.
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(Int32 meta) false", !Radar::BuildNumericTargets(DT::Int32, "100", ts));
    }
    // NumericAll: "100" fits all 10 widths (incl. Int8/UInt8).
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(All,100) ok", Radar::BuildNumericTargets(DT::NumericAll, "100", ts));
        EXPECT("All 100 fits 10 widths", ts.entries.size() == 10);
        EXPECT("All 100 has Int8",  ts.Find(DT::Int8)  != nullptr);
        EXPECT("All 100 has UInt8", ts.Find(DT::UInt8) != nullptr);
        const uint8_t* i8 = ts.Find(DT::Int8);
        if (i8) { int8_t v; std::memcpy(&v, i8, 1); EXPECT("All 100 Int8 decodes", v == 100); }
    }
    // NumericAll: "300" overflows 8-bit widths — no Int8/UInt8 entries.
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(All,300) ok", Radar::BuildNumericTargets(DT::NumericAll, "300", ts));
        EXPECT("All 300 has NO Int8",  ts.Find(DT::Int8)  == nullptr);
        EXPECT("All 300 has NO UInt8", ts.Find(DT::UInt8) == nullptr);
        EXPECT("All 300 has Int16",    ts.Find(DT::Int16) != nullptr);
        EXPECT("All 300 has UInt16",   ts.Find(DT::UInt16)!= nullptr);
    }
    // NumericAll: "-5" → Int8 yes (signed), UInt8 no (negative).
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(All,-5) ok", Radar::BuildNumericTargets(DT::NumericAll, "-5", ts));
        EXPECT("All -5 has Int8",     ts.Find(DT::Int8)  != nullptr);
        EXPECT("All -5 has NO UInt8", ts.Find(DT::UInt8) == nullptr);
    }
    // NumericAll: "200" → UInt8 yes (<=255), Int8 no (>127).
    {
        Radar::NumericTargetSet ts;
        EXPECT("BuildNumericTargets(All,200) ok", Radar::BuildNumericTargets(DT::NumericAll, "200", ts));
        EXPECT("All 200 has UInt8",   ts.Find(DT::UInt8) != nullptr);
        EXPECT("All 200 has NO Int8", ts.Find(DT::Int8)  == nullptr);
    }
}

// ----- Radar: ComparePredicate per DataType -----------------------------
//
// Each test seeds two byte buffers as if they were the raw memory of a
// real UProperty, then exercises every ScanType predicate. Prev-value
// scan types reuse `target` as the candidate's stored prevValue, so the
// same buffer layout works for both flavours.

template <typename T>
static void WriteLE(uint8_t buf[8], T val) {
    std::memset(buf, 0, 8);
    std::memcpy(buf, &val, sizeof(T));
}

static void Test_ValueScan_Predicate_Int32() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8], tgt[8], tgt2[8];
    WriteLE<int32_t>(cur, 100);
    WriteLE<int32_t>(tgt, 100);
    EXPECT("Int32 Exact (100==100)",      Radar::ComparePredicate(DT::Int32, ST::Exact,   cur, tgt));
    WriteLE<int32_t>(tgt, 50);
    EXPECT("Int32 Bigger (100>50)",       Radar::ComparePredicate(DT::Int32, ST::Bigger,  cur, tgt));
    EXPECT("Int32 Smaller false",        !Radar::ComparePredicate(DT::Int32, ST::Smaller, cur, tgt));
    WriteLE<int32_t>(tgt, 200);
    EXPECT("Int32 Smaller (100<200)",     Radar::ComparePredicate(DT::Int32, ST::Smaller, cur, tgt));
    WriteLE<int32_t>(tgt, 50);
    WriteLE<int32_t>(tgt2, 150);
    EXPECT("Int32 Between (100 in [50,150])", Radar::ComparePredicate(DT::Int32, ST::Between, cur, tgt, tgt2));
    WriteLE<int32_t>(tgt, 150);
    WriteLE<int32_t>(tgt2, 200);
    EXPECT("Int32 Between rejects (100 not in [150,200])",
           !Radar::ComparePredicate(DT::Int32, ST::Between, cur, tgt, tgt2));

    // Changed / Unchanged compare against prev (passed as `target`)
    WriteLE<int32_t>(tgt, 100);
    EXPECT("Int32 Unchanged (100==prev100)",  Radar::ComparePredicate(DT::Int32, ST::Unchanged, cur, tgt));
    EXPECT("Int32 Changed rejects same",     !Radar::ComparePredicate(DT::Int32, ST::Changed,   cur, tgt));
    WriteLE<int32_t>(tgt, 99);
    EXPECT("Int32 Changed (100!=prev99)",     Radar::ComparePredicate(DT::Int32, ST::Changed,   cur, tgt));
    EXPECT("Int32 Increased (100>prev99)",    Radar::ComparePredicate(DT::Int32, ST::Increased, cur, tgt));
    WriteLE<int32_t>(tgt, 101);
    EXPECT("Int32 Decreased (100<prev101)",   Radar::ComparePredicate(DT::Int32, ST::Decreased, cur, tgt));
}

static void Test_ValueScan_Predicate_Int8Negative() {
    // Regression for sign extension: Int8 must compare as signed even
    // when the raw byte is 0xFF (which would be 255 as unsigned).
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8] = {}, tgt[8] = {};
    int8_t minusOne = -1;
    int8_t zero = 0;
    std::memcpy(cur, &minusOne, 1);
    std::memcpy(tgt, &zero, 1);
    EXPECT("Int8 (-1 < 0) Smaller",   Radar::ComparePredicate(DT::Int8, ST::Smaller, cur, tgt));
    EXPECT("Int8 (-1 < 0) Bigger NO", !Radar::ComparePredicate(DT::Int8, ST::Bigger,  cur, tgt));
}

static void Test_ValueScan_Predicate_Float() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8], tgt[8];
    WriteLE<float>(cur, 3.14f);
    WriteLE<float>(tgt, 3.14f);
    EXPECT("Float Exact (3.14==3.14)",  Radar::ComparePredicate(DT::Float, ST::Exact,  cur, tgt));
    WriteLE<float>(tgt, 1.0f);
    EXPECT("Float Bigger (3.14>1)",     Radar::ComparePredicate(DT::Float, ST::Bigger, cur, tgt));
    WriteLE<float>(cur, -2.5f);
    WriteLE<float>(tgt, -1.0f);
    EXPECT("Float Smaller (-2.5<-1)",   Radar::ComparePredicate(DT::Float, ST::Smaller, cur, tgt));
}

static void Test_ValueScan_Predicate_Double() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8], tgt[8];
    WriteLE<double>(cur, 1.0 / 3.0);
    WriteLE<double>(tgt, 1.0 / 3.0);
    EXPECT("Double Exact (1/3==1/3)",   Radar::ComparePredicate(DT::Double, ST::Exact,   cur, tgt));
    WriteLE<double>(tgt, 0.0);
    EXPECT("Double Increased prev=0",   Radar::ComparePredicate(DT::Double, ST::Increased, cur, tgt));
}

static void Test_ValueScan_Predicate_Bool() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8] = { 1 }, tgt[8] = { 1 };
    EXPECT("Bool true==true Exact",       Radar::ComparePredicate(DT::Bool, ST::Exact, cur, tgt));
    tgt[0] = 0;
    EXPECT("Bool true!=false Changed",    Radar::ComparePredicate(DT::Bool, ST::Changed, cur, tgt));
    EXPECT("Bool true!=false Unchanged NO", !Radar::ComparePredicate(DT::Bool, ST::Unchanged, cur, tgt));
}

static void Test_ValueScan_Predicate_UInt64_RangeBoundary() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8], tgt[8];
    // Values that would be NEGATIVE if mis-read as signed: ensures
    // unsigned path is taken for UInt64.
    WriteLE<uint64_t>(cur, 0xFFFFFFFFFFFFFFFFULL);
    WriteLE<uint64_t>(tgt, 0x8000000000000000ULL);
    EXPECT("UInt64 (~0 > 0x8000...) Bigger", Radar::ComparePredicate(DT::UInt64, ST::Bigger, cur, tgt));
    EXPECT("UInt64 (~0 < 0x8000...) Smaller NO",
           !Radar::ComparePredicate(DT::UInt64, ST::Smaller, cur, tgt));
}

// ----- Radar: SessionManager lifecycle ----------------------------------

// ----- Radar: Float/Double tolerance (CE-style rounded scan) ------------
//
// The TQ2 / GAS use case that motivated tolerance: game UI shows "338"
// for an underlying float of 337.5 (default rounding). User scans for
// 338 with tolerance 0.5 -> should match values in [337.5, 338.5].
// All eight ScanTypes have a defined tolerance semantic; integer types
// must IGNORE tolerance regardless of the value supplied (the DLL
// signal that this is a "wrong type for tolerance" case).

static void Test_ValueScan_FloatTolerance_Exact() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8], tgt[8];

    // target=338, cur=337.5 (rounds-to-338 in UI), tol=0.5 -> match
    WriteLE<float>(cur, 337.5f);
    WriteLE<float>(tgt, 338.0f);
    EXPECT("Float Exact tol 0.5 (337.5 ~= 338)",
           Radar::ComparePredicate(DT::Float, ST::Exact, cur, tgt, nullptr, 0.5));

    // target=338, cur=338.5 -> still inside tolerance band [337.5, 338.5]
    WriteLE<float>(cur, 338.5f);
    EXPECT("Float Exact tol 0.5 (338.5 ~= 338, inclusive)",
           Radar::ComparePredicate(DT::Float, ST::Exact, cur, tgt, nullptr, 0.5));

    // target=338, cur=338.51 -> outside band, no match
    WriteLE<float>(cur, 338.51f);
    EXPECT("Float Exact tol 0.5 rejects 338.51",
           !Radar::ComparePredicate(DT::Float, ST::Exact, cur, tgt, nullptr, 0.5));

    // tol=0 keeps strict equality semantics (back-compat with old callers)
    WriteLE<float>(cur, 337.5f);
    EXPECT("Float Exact tol 0 rejects 337.5 vs 338",
           !Radar::ComparePredicate(DT::Float, ST::Exact, cur, tgt, nullptr, 0.0));
}

static void Test_ValueScan_FloatTolerance_Ordered() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8], tgt[8];

    // Bigger: cur > target + tol  -> 339 > 338+0.5=338.5 is true, but 338.4 isn't
    WriteLE<float>(tgt, 338.0f);
    WriteLE<float>(cur, 339.0f);
    EXPECT("Float Bigger tol 0.5 (339 > 338.5)",
           Radar::ComparePredicate(DT::Float, ST::Bigger, cur, tgt, nullptr, 0.5));
    WriteLE<float>(cur, 338.4f);
    EXPECT("Float Bigger tol 0.5 rejects 338.4 (within band)",
           !Radar::ComparePredicate(DT::Float, ST::Bigger, cur, tgt, nullptr, 0.5));

    // Smaller: cur < target - tol
    WriteLE<float>(cur, 337.4f);
    EXPECT("Float Smaller tol 0.5 (337.4 < 337.5)",
           Radar::ComparePredicate(DT::Float, ST::Smaller, cur, tgt, nullptr, 0.5));
}

static void Test_ValueScan_FloatTolerance_PrevValue() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8], prev[8];

    // Unchanged within tolerance band — float drift below tol is "no change"
    WriteLE<float>(prev, 100.0f);
    WriteLE<float>(cur,  100.3f);
    EXPECT("Float Unchanged tol 0.5 (drift 0.3 within noise)",
           Radar::ComparePredicate(DT::Float, ST::Unchanged, cur, prev, nullptr, 0.5));
    // Same drift, Changed -> false (drift smaller than tol)
    EXPECT("Float Changed tol 0.5 rejects 0.3 drift",
           !Radar::ComparePredicate(DT::Float, ST::Changed, cur, prev, nullptr, 0.5));

    // Drift larger than tol -> Changed true, Unchanged false
    WriteLE<float>(cur, 100.6f);
    EXPECT("Float Changed tol 0.5 (drift 0.6 > noise)",
           Radar::ComparePredicate(DT::Float, ST::Changed, cur, prev, nullptr, 0.5));

    // Increased: cur > prev + tol
    WriteLE<float>(prev, 50.0f);
    WriteLE<float>(cur,  50.6f);
    EXPECT("Float Increased tol 0.5 (50.6 > 50.5)",
           Radar::ComparePredicate(DT::Float, ST::Increased, cur, prev, nullptr, 0.5));
    WriteLE<float>(cur, 50.4f);
    EXPECT("Float Increased tol 0.5 rejects 50.4 (inside band)",
           !Radar::ComparePredicate(DT::Float, ST::Increased, cur, prev, nullptr, 0.5));
}

static void Test_ValueScan_FloatTolerance_Between() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8], lo[8], hi[8];
    // Between widens both bounds: [10-0.5, 20+0.5] = [9.5, 20.5]
    WriteLE<float>(lo, 10.0f);
    WriteLE<float>(hi, 20.0f);
    WriteLE<float>(cur, 9.8f);
    EXPECT("Float Between tol 0.5 includes 9.8 (lo bound widened)",
           Radar::ComparePredicate(DT::Float, ST::Between, cur, lo, hi, 0.5));
    WriteLE<float>(cur, 20.3f);
    EXPECT("Float Between tol 0.5 includes 20.3 (hi bound widened)",
           Radar::ComparePredicate(DT::Float, ST::Between, cur, lo, hi, 0.5));
    WriteLE<float>(cur, 20.6f);
    EXPECT("Float Between tol 0.5 rejects 20.6",
           !Radar::ComparePredicate(DT::Float, ST::Between, cur, lo, hi, 0.5));
}

static void Test_ValueScan_IntegerTypes_IgnoreTolerance() {
    using DT = Radar::DataType;
    using ST = Radar::ScanType;
    uint8_t cur[8], tgt[8];
    // Even with absurd tolerance, Int32 Exact must be literal equality.
    WriteLE<int32_t>(cur, 99);
    WriteLE<int32_t>(tgt, 100);
    EXPECT("Int32 Exact tol 5 rejects 99 vs 100 (tolerance ignored)",
           !Radar::ComparePredicate(DT::Int32, ST::Exact, cur, tgt, nullptr, 5.0));
    WriteLE<int32_t>(cur, 100);
    EXPECT("Int32 Exact tol 5 accepts 100 vs 100",
           Radar::ComparePredicate(DT::Int32, ST::Exact, cur, tgt, nullptr, 5.0));

    // Same for UInt64
    WriteLE<uint64_t>(cur, 999);
    WriteLE<uint64_t>(tgt, 1000);
    EXPECT("UInt64 Exact tol 100 still rejects 999 vs 1000",
           !Radar::ComparePredicate(DT::UInt64, ST::Exact, cur, tgt, nullptr, 100.0));
}

// ----- Radar: CompareStringPredicate (Phase 2A) -------------------------

static void Test_ValueScan_StringPredicate_Exact() {
    using ST = Radar::ScanType;
    EXPECT("Exact case-insensitive match",
           Radar::CompareStringPredicate(ST::Exact, "PlayerName", "playername", false));
    EXPECT("Exact case-sensitive rejects",
           !Radar::CompareStringPredicate(ST::Exact, "PlayerName", "playername", true));
    EXPECT("Exact case-sensitive accepts",
           Radar::CompareStringPredicate(ST::Exact, "PlayerName", "PlayerName", true));
    EXPECT("Exact rejects different length",
           !Radar::CompareStringPredicate(ST::Exact, "PlayerName", "Player", false));
    EXPECT("Exact accepts empty == empty",
           Radar::CompareStringPredicate(ST::Exact, "", "", false));
}

static void Test_ValueScan_StringPredicate_Substring() {
    using ST = Radar::ScanType;
    EXPECT("Contains case-insensitive: 'Health' in 'PlayerHealth'",
           Radar::CompareStringPredicate(ST::Contains, "PlayerHealth", "Health", false));
    EXPECT("Contains case-insensitive lowercase: 'health' in 'PlayerHealth'",
           Radar::CompareStringPredicate(ST::Contains, "PlayerHealth", "health", false));
    EXPECT("Contains case-sensitive rejects case mismatch",
           !Radar::CompareStringPredicate(ST::Contains, "PlayerHealth", "health", true));
    EXPECT("Contains rejects missing substring",
           !Radar::CompareStringPredicate(ST::Contains, "PlayerHealth", "Mana", false));
    EXPECT("Contains empty needle always true",
           Radar::CompareStringPredicate(ST::Contains, "PlayerHealth", "", false));
    EXPECT("Contains rejects longer-than-haystack",
           !Radar::CompareStringPredicate(ST::Contains, "Hi", "Player", false));

    EXPECT("StartsWith: 'Player' starts 'PlayerHealth'",
           Radar::CompareStringPredicate(ST::StartsWith, "PlayerHealth", "Player", false));
    EXPECT("StartsWith rejects suffix",
           !Radar::CompareStringPredicate(ST::StartsWith, "PlayerHealth", "Health", false));
    EXPECT("StartsWith case-insensitive 'player'",
           Radar::CompareStringPredicate(ST::StartsWith, "PlayerHealth", "player", false));

    EXPECT("EndsWith: 'Health' ends 'PlayerHealth'",
           Radar::CompareStringPredicate(ST::EndsWith, "PlayerHealth", "Health", false));
    EXPECT("EndsWith rejects prefix",
           !Radar::CompareStringPredicate(ST::EndsWith, "PlayerHealth", "Player", false));
    EXPECT("EndsWith case-sensitive rejects",
           !Radar::CompareStringPredicate(ST::EndsWith, "PlayerHealth", "HEALTH", true));
}

static void Test_ValueScan_StringPredicate_PrevValue() {
    using ST = Radar::ScanType;
    EXPECT("Changed: different strings",
           Radar::CompareStringPredicate(ST::Changed, "NewName", "OldName", false));
    EXPECT("Changed rejects identical",
           !Radar::CompareStringPredicate(ST::Changed, "Same", "Same", false));
    EXPECT("Unchanged: identical strings",
           Radar::CompareStringPredicate(ST::Unchanged, "Same", "Same", false));
    EXPECT("Unchanged: case-insensitive identical",
           Radar::CompareStringPredicate(ST::Unchanged, "SAME", "same", false));
    EXPECT("Unchanged case-sensitive rejects case-diff",
           !Radar::CompareStringPredicate(ST::Unchanged, "SAME", "same", true));
}

static void Test_ValueScan_StringPredicate_RejectsNumericOrdering() {
    using ST = Radar::ScanType;
    // Numeric predicates have no meaning for strings — return false
    // unconditionally so the pipe handler's IsScanTypeValidFor guard
    // is belt-and-braces.
    EXPECT("Bigger rejects",
           !Radar::CompareStringPredicate(ST::Bigger, "B", "A", false));
    EXPECT("Smaller rejects",
           !Radar::CompareStringPredicate(ST::Smaller, "A", "B", false));
    EXPECT("Between rejects",
           !Radar::CompareStringPredicate(ST::Between, "M", "A", false));
    EXPECT("Increased rejects",
           !Radar::CompareStringPredicate(ST::Increased, "B", "A", false));
    EXPECT("Decreased rejects",
           !Radar::CompareStringPredicate(ST::Decreased, "A", "B", false));
}

// ----- Radar: CompareVectorPredicate (Phase 2B) -------------------------

static void WriteVector(uint8_t buf[12], float x, float y, float z) {
    std::memcpy(buf + 0, &x, 4);
    std::memcpy(buf + 4, &y, 4);
    std::memcpy(buf + 8, &z, 4);
}

static void Test_ValueScan_VectorPredicate_Exact() {
    using ST = Radar::ScanType;
    uint8_t cur[12], tgt[12];
    WriteVector(cur, 100.0f, 200.0f, 300.0f);
    WriteVector(tgt, 100.0f, 200.0f, 300.0f);
    EXPECT("Vec Exact all match", Radar::CompareVectorPredicate(ST::Exact, cur, tgt));
    WriteVector(cur, 100.5f, 200.0f, 300.0f);
    EXPECT("Vec Exact rejects component mismatch", !Radar::CompareVectorPredicate(ST::Exact, cur, tgt));
    EXPECT("Vec Exact tol 0.5 accepts within band",
           Radar::CompareVectorPredicate(ST::Exact, cur, tgt, nullptr, 0.5));
}

static void Test_ValueScan_VectorPredicate_Ordering() {
    using ST = Radar::ScanType;
    uint8_t cur[12], tgt[12];
    WriteVector(cur, 10.0f, 20.0f, 30.0f);
    WriteVector(tgt, 5.0f,  10.0f, 15.0f);
    EXPECT("Vec Bigger: all axes above", Radar::CompareVectorPredicate(ST::Bigger, cur, tgt));
    EXPECT("Vec Smaller (10,20,30) NOT < (5,10,15)",
           !Radar::CompareVectorPredicate(ST::Smaller, cur, tgt));

    // One axis equal kills Bigger
    WriteVector(cur, 10.0f, 10.0f, 30.0f);
    EXPECT("Vec Bigger fails when one axis equals",
           !Radar::CompareVectorPredicate(ST::Bigger, cur, tgt));
}

static void Test_ValueScan_VectorPredicate_Between() {
    using ST = Radar::ScanType;
    uint8_t cur[12], lo[12], hi[12];
    WriteVector(lo, 0.0f,   0.0f,   0.0f);
    WriteVector(hi, 100.0f, 100.0f, 100.0f);
    WriteVector(cur, 50.0f, 50.0f, 50.0f);
    EXPECT("Vec Between: (50,50,50) in [(0,0,0),(100,100,100)]",
           Radar::CompareVectorPredicate(ST::Between, cur, lo, hi));
    WriteVector(cur, 50.0f, 150.0f, 50.0f);
    EXPECT("Vec Between rejects Y outside",
           !Radar::CompareVectorPredicate(ST::Between, cur, lo, hi));
}

static void Test_ValueScan_VectorPredicate_PrevValue() {
    using ST = Radar::ScanType;
    uint8_t cur[12], prev[12];
    WriteVector(prev, 100.0f, 100.0f, 100.0f);

    // Movement on any single axis = Changed
    WriteVector(cur, 100.0f, 100.0f, 105.0f);
    EXPECT("Vec Changed: one axis moved",
           Radar::CompareVectorPredicate(ST::Changed, cur, prev));
    EXPECT("Vec Unchanged rejects when axis differs",
           !Radar::CompareVectorPredicate(ST::Unchanged, cur, prev));

    // No movement
    WriteVector(cur, 100.0f, 100.0f, 100.0f);
    EXPECT("Vec Unchanged accepts identical",
           Radar::CompareVectorPredicate(ST::Unchanged, cur, prev));
    EXPECT("Vec Changed rejects identical",
           !Radar::CompareVectorPredicate(ST::Changed, cur, prev));

    // Increased: ANY axis moved up beyond tolerance
    WriteVector(cur, 100.0f, 100.0f, 110.0f);
    EXPECT("Vec Increased: Z went up",
           Radar::CompareVectorPredicate(ST::Increased, cur, prev));
    // All went down — Increased rejects
    WriteVector(cur, 90.0f, 90.0f, 90.0f);
    EXPECT("Vec Increased rejects when all axes down",
           !Radar::CompareVectorPredicate(ST::Increased, cur, prev));
    EXPECT("Vec Decreased: all axes down",
           Radar::CompareVectorPredicate(ST::Decreased, cur, prev));
}

static void Test_ValueScan_VectorPredicate_RejectsSubstring() {
    using ST = Radar::ScanType;
    uint8_t cur[12], tgt[12];
    WriteVector(cur, 0,0,0); WriteVector(tgt, 0,0,0);
    EXPECT("Vec Contains rejects",
           !Radar::CompareVectorPredicate(ST::Contains, cur, tgt));
    EXPECT("Vec StartsWith rejects",
           !Radar::CompareVectorPredicate(ST::StartsWith, cur, tgt));
    EXPECT("Vec EndsWith rejects",
           !Radar::CompareVectorPredicate(ST::EndsWith, cur, tgt));
}

// ----- VectorStructNames (Phase 2B) ----------------------------------------

static void Test_ValueScan_VectorStructNames() {
    using DT = Radar::DataType;
    const auto& vec = Radar::VectorStructNames(DT::FVector);
    EXPECT("FVector accepts 'Vector'",
           std::find(vec.begin(), vec.end(), std::string("Vector")) != vec.end());
    EXPECT("FVector accepts 'Vector3f'",
           std::find(vec.begin(), vec.end(), std::string("Vector3f")) != vec.end());
    const auto& rot = Radar::VectorStructNames(DT::FRotator);
    EXPECT("FRotator accepts 'Rotator'",
           std::find(rot.begin(), rot.end(), std::string("Rotator")) != rot.end());
    // FTransform is intentionally empty until per-version Translation
    // offset detection ships.
    const auto& xfm = Radar::VectorStructNames(DT::FTransform);
    EXPECT("FTransform empty (deferred)", xfm.empty());
    // Non-vector dt returns empty.
    const auto& none = Radar::VectorStructNames(DT::Int32);
    EXPECT("Int32 has no vector struct names", none.empty());
}

static void Test_ValueScan_SessionLifecycle() {
    using namespace Radar;
    auto& mgr = SessionManager::Instance();

    // Seed two candidates.
    std::vector<Candidate> seed;
    seed.resize(2);
    seed[0].addr = 0x1000;
    WriteLE<int32_t>(seed[0].prevValue, 100);
    seed[1].addr = 0x2000;
    WriteLE<int32_t>(seed[1].prevValue, 200);

    // Shared metadata pools the candidates index into (V3-A). Both
    // candidates reference one descriptor + one instance to exercise the
    // dedup path.
    std::vector<FieldDescriptor> descriptors(1);
    descriptors[0].className     = "AActor";
    descriptors[0].fieldName     = "Health";
    descriptors[0].fieldType     = "IntProperty";
    std::vector<InstanceRecord> instances(1);
    instances[0].instanceAddr = 0x4000;
    instances[0].instanceName = "Actor_0";

    uint64_t sid = mgr.Begin(DataType::Int32, std::move(seed),
                             std::move(descriptors), std::move(instances));
    EXPECT("Begin returns non-zero session id", sid != 0);

    bool viewed = mgr.ViewWith(sid, [&](const Session& sess) {
        EXPECT("ViewWith sees correct dataType", sess.dt == DataType::Int32);
        EXPECT("ViewWith sees 2 candidates",     sess.candidates.size() == 2);
        EXPECT("ViewWith preserves descriptor pool", sess.descriptors.size() == 1);
        EXPECT("ViewWith preserves instance pool",   sess.instances.size() == 1);
        EXPECT("Descriptor field name interned",
               sess.descriptors[0].fieldName == "Health");
    });
    EXPECT("ViewWith returns true for live session", viewed);

    // RefineWith may mutate the candidates vector.
    bool refined = mgr.RefineWith(sid, [](Session& sess) {
        sess.candidates.pop_back();  // drop one
    });
    EXPECT("RefineWith returns true for live session", refined);

    size_t remaining = 0;
    mgr.ViewWith(sid, [&](const Session& sess) {
        remaining = sess.candidates.size();
    });
    EXPECT("Refine pruned candidate count", remaining == 1);

    EXPECT("End returns true on first call",  mgr.End(sid));
    EXPECT("End returns false on second call",!mgr.End(sid));

    // Lookups on a missing session id return false WITHOUT invoking
    // the callback -- caller maps to wire error "session_not_found".
    bool callbackRan = false;
    bool missingOk = mgr.RefineWith(sid, [&](Session&) {
        callbackRan = true;
    });
    EXPECT("RefineWith on missing returns false", !missingOk);
    EXPECT("RefineWith on missing does NOT invoke callback", !callbackRan);
}

// V3-A — FieldDisplayName reconstructs the candidate display name from the
// interned descriptor + the candidate's element index: the base name for a
// direct field (-1), and "base[idx]" for a TArray/container element.
static void Test_ValueScan_FieldDisplayName() {
    using namespace Radar;
    FieldDescriptor desc;
    desc.fieldName = "Items";

    EXPECT("Direct field uses base name (-1)",
           FieldDisplayName(desc, -1) == "Items");
    EXPECT("Element 0 renders [0]",
           FieldDisplayName(desc, 0) == "Items[0]");
    EXPECT("Element 42 renders [42]",
           FieldDisplayName(desc, 42) == "Items[42]");

    FieldDescriptor nested;
    nested.fieldName = "MaximumHealth.CurrentValue";
    EXPECT("Dotted nested base name preserved (-1)",
           FieldDisplayName(nested, -1) == "MaximumHealth.CurrentValue");

    // V1a — TMap key/value scan fields carry a "Map.Key" / "Map.Value" base
    // name (the per-pair half), so element rendering reads "Map.Key[idx]".
    FieldDescriptor mapKey;
    mapKey.fieldName = "Inventory.Key";
    EXPECT("Map key element renders Map.Key[idx]",
           FieldDisplayName(mapKey, 2) == "Inventory.Key[2]");
    FieldDescriptor mapVal;
    mapVal.fieldName = "Inventory.Value";
    EXPECT("Map value element renders Map.Value[idx]",
           FieldDisplayName(mapVal, 5) == "Inventory.Value[5]");

    // build 1201 — struct-array-inner descriptors carry a "[]" placeholder so
    // the element index lands after the ARRAY name, not at the very end:
    // "SaveSlotList[].GP" -> "SaveSlotList[3].GP".
    FieldDescriptor structArr;
    structArr.fieldName = "SaveSlotList[].GP";
    EXPECT("Struct-array-inner inserts index at placeholder",
           FieldDisplayName(structArr, 3) == "SaveSlotList[3].GP");
    EXPECT("Struct-array-inner drops empty placeholder when no index",
           FieldDisplayName(structArr, -1) == "SaveSlotList.GP");
    FieldDescriptor structArrNested;
    structArrNested.fieldName = "SaveSlotList[].MsTuneData.GP2";
    EXPECT("Struct-array-inner nested direct-struct path",
           FieldDisplayName(structArrNested, 1) == "SaveSlotList[1].MsTuneData.GP2");
}

// V1c — TOptional<T> bIsSet flag offset. A non-intrusive optional is laid out
// { T value; bool bIsSet; } padded to alignof(T), so the flag sits at
// offset == sizeof(T). OptionalFlagOffset returns that offset when the optional
// is larger than its value (room for the bool), else -1 (intrusive / unknown).
static void Test_ValueScan_OptionalFlagOffset() {
    using namespace Radar;
    // Non-intrusive numerics: flag at sizeof(T).
    EXPECT("TOptional<int8>  -> flag at 1", OptionalFlagOffset(2, 1)  == 1);
    EXPECT("TOptional<int16> -> flag at 2", OptionalFlagOffset(4, 2)  == 2);
    EXPECT("TOptional<int32> -> flag at 4", OptionalFlagOffset(8, 4)  == 4);
    EXPECT("TOptional<int64> -> flag at 8", OptionalFlagOffset(16, 8) == 8);
    EXPECT("TOptional<float> -> flag at 4", OptionalFlagOffset(8, 4)  == 4);
    EXPECT("TOptional<double>-> flag at 8", OptionalFlagOffset(16, 8) == 8);
    // FVector (double, 24B) -> 24 value + bool padded to 32.
    EXPECT("TOptional<FVector>-> flag at 24", OptionalFlagOffset(32, 24) == 24);
    // FString (16B) -> 16 value + bool padded to 24.
    EXPECT("TOptional<FString>-> flag at 16", OptionalFlagOffset(24, 16) == 16);
    // Intrusive / pointer-shaped: optional size == value size, no flag.
    EXPECT("Intrusive (size==inner) -> -1", OptionalFlagOffset(8, 8) == -1);
    // Unknown / unresolved inner size -> no gate.
    EXPECT("Zero inner size -> -1",     OptionalFlagOffset(8, 0)  == -1);
    EXPECT("Negative inner size -> -1", OptionalFlagOffset(8, -1) == -1);
    // Defensive: a value somehow larger than the optional -> no gate.
    EXPECT("inner > optional -> -1", OptionalFlagOffset(4, 8) == -1);
}

// V3-C — server-side ordered view (filter + sort + window) over a candidate
// pool. The DLL owns the full set; the UI is a window. These pure helpers run
// over the DLL's own pools (no game memory), so filter/sort never touch the
// game thread. Builds a tiny synthetic pool and checks filter / sort / format.
static void Test_ValueScan_OrderedView() {
    using namespace Radar;

    std::vector<FieldDescriptor> descs(2);
    descs[0].className = "BP_Player_C"; descs[0].definingClassName = "ACharacter";
    descs[0].fieldName = "Health"; descs[0].fieldType = "IntProperty"; descs[0].fieldOffset = 0x1C;
    descs[1].className = "BP_Enemy_C"; descs[1].definingClassName = "BP_Enemy_C";
    descs[1].fieldName = "Mana"; descs[1].fieldType = "IntProperty"; descs[1].fieldOffset = 0x40;

    std::vector<InstanceRecord> insts(2);
    insts[0].instanceAddr = 0x1000; insts[0].instanceIndex = 5; insts[0].instanceName = "Player_0";
    insts[1].instanceAddr = 0x2000; insts[1].instanceIndex = 9; insts[1].instanceName = "Enemy_3";

    auto mk = [](int32_t v, uintptr_t addr, uint32_t d, uint32_t inst) {
        Candidate c;
        std::memcpy(c.prevValue, &v, 4);
        c.addr = addr; c.descriptorIdx = d; c.instanceIdx = inst; c.elementIndex = -1;
        return c;
    };
    // Addresses chosen with no decimal-digit overlap with the test values /
    // offsets so a value/offset filter doesn't also match an address.
    std::vector<Candidate> cands = {
        mk(100, 0xAAAA, 0, 0),   // c0: Player.Health = 100
        mk(50,  0xBBBB, 1, 1),   // c1: Enemy.Mana    = 50
        mk(30,  0xCCCC, 0, 1),   // c2: Enemy.Health  = 30
        mk(200, 0xDDDD, 1, 0),   // c3: Player.Mana   = 200
    };
    const DataType dt = DataType::Int32;

    auto view = [&](const std::string& f, SortKey k, bool desc) {
        return BuildOrderedView(cands, descs, insts, dt, f, k, desc);
    };

    // --- no filter, ordering ---
    auto o = view("", SortKey::ScanOrder, false);
    EXPECT("ScanOrder keeps all in order", o.size() == 4 && o[0] == 0 && o[3] == 3);
    o = view("", SortKey::ScanOrder, true);
    EXPECT("ScanOrder desc reverses", o.size() == 4 && o[0] == 3 && o[3] == 0);

    o = view("", SortKey::Value, false);
    EXPECT("Value asc 30,50,100,200", o.size() == 4 && o[0] == 2 && o[1] == 1 && o[2] == 0 && o[3] == 3);
    o = view("", SortKey::Value, true);
    EXPECT("Value desc 200..30", o[0] == 3 && o[1] == 0 && o[2] == 1 && o[3] == 2);

    o = view("", SortKey::Offset, false);
    EXPECT("Offset asc stable (Health 0x1C then Mana 0x40)",
           o.size() == 4 && o[0] == 0 && o[1] == 2 && o[2] == 1 && o[3] == 3);

    o = view("", SortKey::ClassName, false);
    EXPECT("ClassName asc stable (Enemy then Player)",
           o[0] == 1 && o[1] == 3 && o[2] == 0 && o[3] == 2);

    o = view("", SortKey::Address, false);
    EXPECT("Address asc by pointer", o[0] == 0 && o[1] == 1 && o[2] == 2 && o[3] == 3);

    o = view("", SortKey::InstanceIndex, false);
    EXPECT("InstanceIndex asc (5 then 9)", o[0] == 0 && o[1] == 3 && o[2] == 1 && o[3] == 2);

    // --- filtering (case-insensitive substring across displayed columns) ---
    o = view("mana", SortKey::ScanOrder, false);
    EXPECT("filter field name 'mana'", o.size() == 2 && o[0] == 1 && o[1] == 3);
    o = view("enemy", SortKey::ScanOrder, false);
    EXPECT("filter class/instance 'enemy'", o.size() == 3 && o[0] == 1 && o[1] == 2 && o[2] == 3);
    o = view("100", SortKey::ScanOrder, false);
    EXPECT("filter by value '100'", o.size() == 1 && o[0] == 0);
    o = view("0x40", SortKey::ScanOrder, false);
    EXPECT("filter by offset hex '0x40'", o.size() == 2 && o[0] == 1 && o[1] == 3);
    // 'player' matches Player_0 instance (c0, c3) AND BP_Player_C class (c0, c2).
    o = view("PLAYER", SortKey::ScanOrder, false);
    EXPECT("filter case-insensitive 'PLAYER'", o.size() == 3 && o[0] == 0 && o[1] == 2 && o[2] == 3);
    o = view("zzz", SortKey::ScanOrder, false);
    EXPECT("filter no match -> empty", o.empty());

    // filter + sort compose (Health rows, by value asc): c2(30) then c0(100)
    o = view("health", SortKey::Value, false);
    EXPECT("filter 'health' + Value asc", o.size() == 2 && o[0] == 2 && o[1] == 0);

    // --- value formatting / decode ---
    EXPECT("FormatCandidateValue Int32 100", FormatCandidateValue(cands[0], dt, descs[0]) == "100");
    EXPECT("DecodeNumericToDouble Int32 100", DecodeNumericToDouble(DataType::Int32, cands[0].prevValue) == 100.0);
    Candidate bc; bc.prevValue[0] = 1;
    EXPECT("FormatCandidateValue Bool true", FormatCandidateValue(bc, DataType::Bool, descs[0]) == "true");
    EXPECT("DecodeNumericToDouble Bool true", DecodeNumericToDouble(DataType::Bool, bc.prevValue) == 1.0);

    // --- sort key parsing ---
    SortKey k;
    EXPECT("parse 'value'", TryParseSortKey("value", k) && k == SortKey::Value);
    EXPECT("parse '' -> ScanOrder", TryParseSortKey("", k) && k == SortKey::ScanOrder);
    EXPECT("parse 'offset'", TryParseSortKey("offset", k) && k == SortKey::Offset);
    EXPECT("parse unknown -> false", !TryParseSortKey("bogus", k));
}

// V2 (build 950) — scaling smoke for the server-side ordered view. The cap
// ceiling was raised to 1,000,000 now that the UI windows server-side (V3-C);
// confirm a full filter + sort over a set that size stays well under a second
// (it runs on every filter/sort change, debounced 250ms in the UI). Uses
// QueryPerformanceCounter to match the poll-latency bench above.
static void Test_ValueScan_OrderedViewScale() {
    using namespace Radar;
    const int N = 1'000'000;

    std::vector<FieldDescriptor> descs(10);
    for (int i = 0; i < 10; ++i) {
        descs[i].className   = "Class_" + std::to_string(i);
        descs[i].fieldName   = "Field_" + std::to_string(i);
        descs[i].fieldType   = "IntProperty";
        descs[i].fieldOffset = i * 4;
    }
    std::vector<InstanceRecord> insts(1000);
    for (int i = 0; i < 1000; ++i) {
        insts[i].instanceAddr  = 0x10000 + (uintptr_t)i * 0x100;
        insts[i].instanceIndex = i;
        insts[i].instanceName  = "Obj_" + std::to_string(i);
    }
    std::vector<Candidate> cands(N);
    for (int i = 0; i < N; ++i) {
        int32_t v = (int32_t)(((uint32_t)i * 2654435761u) & 0x7FFFFFFF);  // scattered
        std::memcpy(cands[i].prevValue, &v, 4);
        cands[i].addr          = 0x100000 + (uintptr_t)i * 8;
        cands[i].descriptorIdx = (uint32_t)(i % 10);
        cands[i].instanceIdx   = (uint32_t)(i % 1000);
        cands[i].elementIndex  = -1;
    }

    LARGE_INTEGER freq, t0, t1;
    QueryPerformanceFrequency(&freq);

    QueryPerformanceCounter(&t0);
    auto sorted = BuildOrderedView(cands, descs, insts, DataType::Int32, "", SortKey::Value, false);
    QueryPerformanceCounter(&t1);
    double sortMs = (double)(t1.QuadPart - t0.QuadPart) * 1000.0 / freq.QuadPart;
    std::printf("  [bench] BuildOrderedView sort-by-value %d candidates: %.1f ms\n", N, sortMs);
    EXPECT("scale: sort retains all", sorted.size() == (size_t)N);
    bool asc = true;
    for (size_t i = 1; i < sorted.size(); i += 40000) {
        if (DecodeNumericToDouble(DataType::Int32, cands[sorted[i - 1]].prevValue) >
            DecodeNumericToDouble(DataType::Int32, cands[sorted[i]].prevValue)) { asc = false; break; }
    }
    EXPECT("scale: sorted ascending", asc);

    QueryPerformanceCounter(&t0);
    auto filtered = BuildOrderedView(cands, descs, insts, DataType::Int32, "class_3", SortKey::ScanOrder, false);
    QueryPerformanceCounter(&t1);
    double filtMs = (double)(t1.QuadPart - t0.QuadPart) * 1000.0 / freq.QuadPart;
    std::printf("  [bench] BuildOrderedView filter 'class_3' over %d: %.1f ms -> %zu rows\n",
                N, filtMs, filtered.size());
    EXPECT("scale: filter 'class_3' = N/10", filtered.size() == (size_t)(N / 10));

    // Generous bounds catch an O(n^2) regression; the printed numbers are far
    // lower. The filter is allocation-heavier than the sort (lowercases each
    // displayed column) — if it ever creeps past this, the follow-up is the
    // incremental/top-k path noted in todo V2.
    EXPECT("scale: sort under 5s",   sortMs < 5000.0);
    EXPECT("scale: filter under 5s", filtMs < 5000.0);
}

// V1a — TSet / TMap sparse-container element geometry. ComputeSetElementStride
// accounts for the TSetElement hash overhead (HashNextId + HashIndex, value
// aligned to 4); ComputeMapValueOffset aligns the TPair value to its natural
// alignment. These drive the slot addresses the container scan reads, so lock
// the math the Address Finder + Value Search both depend on.
static void Test_ValueScan_SparseContainerGeometry() {
    // TSetElement<T> = { T value; int32 HashNextId; int32 HashIndex; }, with
    // value padded up to 4-byte alignment before the two hash ints (+8).
    EXPECT("Set<int32> stride = 4 + 8",        Macht::ComputeSetElementStride(4)  == 12);
    EXPECT("Set<int64> stride = 8 + 8",        Macht::ComputeSetElementStride(8)  == 16);
    EXPECT("Set<uint8> stride pads to 4 + 8",  Macht::ComputeSetElementStride(1)  == 12);
    EXPECT("Set<3-byte> stride pads to 4 + 8", Macht::ComputeSetElementStride(3)  == 12);
    EXPECT("Set<FVector 12> stride = 12 + 8",  Macht::ComputeSetElementStride(12) == 20);

    // TPair<K,V> value offset = K size aligned up to V's natural alignment
    // (guessed from V size: >=8 -> 8, >=4 -> 4, >=2 -> 2, else 1).
    EXPECT("Map<int32,int32> value at +4",    Macht::ComputeMapValueOffset(4, 4)  == 4);
    EXPECT("Map<uint8,int32> value aligns +4", Macht::ComputeMapValueOffset(1, 4) == 4);
    EXPECT("Map<uint8,struct80> value at +8",  Macht::ComputeMapValueOffset(1, 80) == 8);
    EXPECT("Map<int32,uint8> value at +4",     Macht::ComputeMapValueOffset(4, 1) == 4);
    EXPECT("Map<int64,int64> value at +8",     Macht::ComputeMapValueOffset(8, 8) == 8);

    // Explicit value alignment overrides the size guess — REQUIRED for FName
    // (8 bytes but 4-aligned) and FWeakObjectPtr. Map<Enum, FName>: value at +4,
    // NOT +8 (the size guess would corrupt every element). Align comes from
    // Scharf::RequiredAlignment("NameProperty", 8, false) == 4.
    EXPECT("Map<uint8,FName> value at +4 (align 4)",  Macht::ComputeMapValueOffset(1, 8, 4) == 4);
    EXPECT("Map<uint8,FName> WOULD be +8 w/o align",  Macht::ComputeMapValueOffset(1, 8)    == 8);
    EXPECT("Map<uint8,ptr> value at +8 (align 8)",    Macht::ComputeMapValueOffset(1, 8, 8) == 8);
    EXPECT("Scharf NameProperty(8) align = 4",        Scharf::RequiredAlignment("NameProperty", 8, false) == 4);
    EXPECT("Scharf WeakObjectProperty align = 4",     Scharf::RequiredAlignment("WeakObjectProperty", 8, false) == 4);
}

// ----- main ------------------------------------------------------------------

// Phase A1a — snapshot field selection: pick numeric scalar fields by scope,
// preserving original field indices and resolving each to its concrete width.
static void Test_ValueScan_SelectSnapshotNumericFields() {
    using DT = Radar::DataType;
    // Mixed class layout (field order matters — indices must be preserved).
    const std::vector<std::string> fields = {
        "FloatProperty",   // 0  -> captured (Float) in both scopes
        "BoolProperty",    // 1  -> never (bool excluded)
        "IntProperty",     // 2  -> captured (Int32) in both
        "StrProperty",     // 3  -> never (non-numeric)
        "Int8Property",    // 4  -> NumericAll only (Int8)
        "ByteProperty",    // 5  -> NumericAll only (UInt8)
        "StructProperty",  // 6  -> never
        "Int16Property",   // 7  -> captured (Int16) in both
        "DoubleProperty",  // 8  -> captured (Double) in both
    };

    auto noByte = Radar::SelectSnapshotNumericFields(fields, DT::NumericNoByte);
    EXPECT("NoByte picks 4 fields", noByte.size() == 4);
    if (noByte.size() == 4) {
        EXPECT("NoByte[0] = field 0 Float",  noByte[0].fieldIndex == 0 && noByte[0].dt == DT::Float);
        EXPECT("NoByte[1] = field 2 Int32",  noByte[1].fieldIndex == 2 && noByte[1].dt == DT::Int32);
        EXPECT("NoByte[2] = field 7 Int16",  noByte[2].fieldIndex == 7 && noByte[2].dt == DT::Int16);
        EXPECT("NoByte[3] = field 8 Double", noByte[3].fieldIndex == 8 && noByte[3].dt == DT::Double);
    }

    auto all = Radar::SelectSnapshotNumericFields(fields, DT::NumericAll);
    EXPECT("All picks 6 fields", all.size() == 6);
    if (all.size() == 6) {
        EXPECT("All includes field 4 Int8",  all[2].fieldIndex == 4 && all[2].dt == DT::Int8);
        EXPECT("All includes field 5 UInt8", all[3].fieldIndex == 5 && all[3].dt == DT::UInt8);
    }

    // Non-meta scope captures nothing (snapshot only runs with meta types).
    auto none = Radar::SelectSnapshotNumericFields(fields, DT::Int32);
    EXPECT("Int32 scope captures nothing", none.empty());

    // Empty input is fine.
    auto empty = Radar::SelectSnapshotNumericFields({}, DT::NumericNoByte);
    EXPECT("empty field list -> empty picks", empty.empty());

    // Every captured field must have a non-zero fixed width (SizeOf invariant).
    for (const auto& p : all) {
        EXPECT("captured dt has 1..8 byte width",
               Radar::SizeOf(p.dt) >= 1 && Radar::SizeOf(p.dt) <= 8);
    }
}

// Phase A1b — struct-array inner-key selection.
static void Test_ValueScan_SelectArrayInnerKey() {
    // FCargoSlot { FName ItemID; int32 Quantity; } -> key = ItemID (index 0).
    EXPECT("FName ItemID is the key",
        Radar::SelectArrayInnerKey({"NameProperty", "IntProperty"}, {"ItemID", "Quantity"}) == 0);
    // A plain (keyword-less) FName still beats an integer.
    EXPECT("plain FName beats int",
        Radar::SelectArrayInnerKey({"IntProperty", "NameProperty"}, {"Count", "Slot"}) == 1);
    // No FName -> first integer field.
    EXPECT("first int when no FName",
        Radar::SelectArrayInnerKey({"FloatProperty", "IntProperty", "Int64Property"}, {"X", "Qty", "Big"}) == 1);
    // A keyworded FName is preferred over an earlier plain FName.
    EXPECT("keyworded FName preferred",
        Radar::SelectArrayInnerKey({"NameProperty", "NameProperty"}, {"Display", "RowName"}) == 1);
    // Neither FName nor integer -> -1 (caller uses the element index).
    EXPECT("no key field -> -1",
        Radar::SelectArrayInnerKey({"FloatProperty", "BoolProperty"}, {"X", "Flag"}) == -1);
}

// ----- Denken: native x64 disassembly (Path 2) -------------------------------
//
// Hand-assembled x64 byte buffers exercise the decoder core through a buffer-
// backed MemReader (no live process). The encodings below are standard MS x64;
// `this` is assumed in RCX at entry (Denken's seed), matching the UE exec-thunk
// signature Func(UObject* Context, FFrame&, void*).

namespace {

struct DenkenRegion { uintptr_t base; std::vector<uint8_t> bytes; };

static Denken::MemReader MakeReader(const std::vector<DenkenRegion>* regions) {
    return [regions](uintptr_t addr, uint8_t* out, size_t maxLen) -> size_t {
        for (const auto& r : *regions) {
            if (addr >= r.base && addr < r.base + r.bytes.size()) {
                size_t avail = r.base + r.bytes.size() - addr;
                size_t n = avail < maxLen ? avail : maxLen;
                std::memcpy(out, r.bytes.data() + (addr - r.base), n);
                return n;
            }
        }
        return 0;
    };
}

static const Denken::NativeFieldAccess* FindAccess(
    const Denken::NativeAnalysisResult& r, uint32_t off) {
    for (const auto& a : r.accesses) if (a.offset == off) return &a;
    return nullptr;
}

} // namespace

static void Test_Denken_BasicAccesses() {
    // mov [rcx+0x10], eax   89 41 10   write @0x10, base=this -> high-conf
    // mov eax, [rdx+0x20]   8B 42 20   read  @0x20, base=rdx  -> low-conf
    // mov rbx, rcx          48 89 CB   rbx becomes a this-alias
    // mov eax, [rbx+0x08]   8B 43 08   read  @0x08 via alias  -> high-conf
    // ret                   C3
    std::vector<DenkenRegion> regions = {{ 0x140000000ULL, {
        0x89, 0x41, 0x10,
        0x8B, 0x42, 0x20,
        0x48, 0x89, 0xCB,
        0x8B, 0x43, 0x08,
        0xC3,
    }}};
    auto r = Denken::Analyze(0x140000000ULL, MakeReader(&regions));
    EXPECT("basic: ran", r.ok);

    const auto* w10 = FindAccess(r, 0x10);
    EXPECT("basic: @0x10 present",   w10 != nullptr);
    if (w10) {
        EXPECT("basic: @0x10 write",     w10->writeCount == 1);
        EXPECT("basic: @0x10 high-conf", w10->highConfidence);
        EXPECT("basic: @0x10 size 4",    w10->accessSize == 4);
    }
    const auto* r20 = FindAccess(r, 0x20);
    EXPECT("basic: @0x20 present",   r20 != nullptr);
    if (r20) {
        EXPECT("basic: @0x20 read",      r20->writeCount == 0);
        EXPECT("basic: @0x20 low-conf",  !r20->highConfidence);
    }
    const auto* r08 = FindAccess(r, 0x08);
    EXPECT("basic: @0x08 present (alias)", r08 != nullptr);
    if (r08) EXPECT("basic: @0x08 high-conf via rbx alias", r08->highConfidence);
}

static void Test_Denken_ExcludesStackAndZeroDisp() {
    // mov eax, [rbp+0x10]   8B 45 10        rbp-relative (local) -> excluded
    // mov eax, [rsp+0x10]   8B 44 24 10     rsp-relative (local) -> excluded
    // mov [rcx+0x04], eax   89 41 04        valid this write     -> recorded
    // ret                   C3
    std::vector<DenkenRegion> regions = {{ 0x140000000ULL, {
        0x8B, 0x45, 0x10,
        0x8B, 0x44, 0x24, 0x10,
        0x89, 0x41, 0x04,
        0xC3,
    }}};
    auto r = Denken::Analyze(0x140000000ULL, MakeReader(&regions));
    EXPECT("stack: ran", r.ok);
    EXPECT("stack: rbp excluded", FindAccess(r, 0x10) == nullptr);
    const auto* a = FindAccess(r, 0x04);
    EXPECT("stack: this write recorded", a != nullptr && a->writeCount == 1 && a->highConfidence);
}

static void Test_Denken_FollowsCallHandoff() {
    // Thunk @ B0: save this, restore to rcx, call impl, ret.
    //   mov rbx, rcx     48 89 CB
    //   mov rcx, rbx     48 89 D9
    //   call rel32       E8 <rel>      (instr at B0+6, next = B0+11)
    //   ret              C3
    // Impl  @ B1: mov [rcx+0x40], eax ; ret  (write @0x40, this in rcx)
    const uintptr_t B0 = 0x140000000ULL;
    const uintptr_t B1 = 0x140001000ULL;
    const int32_t rel = static_cast<int32_t>(B1 - (B0 + 11));
    std::vector<uint8_t> thunk = {
        0x48, 0x89, 0xCB,
        0x48, 0x89, 0xD9,
        0xE8,
        static_cast<uint8_t>(rel & 0xFF),
        static_cast<uint8_t>((rel >> 8) & 0xFF),
        static_cast<uint8_t>((rel >> 16) & 0xFF),
        static_cast<uint8_t>((rel >> 24) & 0xFF),
        0xC3,
    };
    std::vector<DenkenRegion> regions = {
        { B0, thunk },
        { B1, { 0x89, 0x41, 0x40, 0xC3 } },
    };
    auto r = Denken::Analyze(B0, MakeReader(&regions));
    EXPECT("follow: ran", r.ok);
    EXPECT("follow: followed 1 call", r.callsFollowed == 1);
    const auto* a = FindAccess(r, 0x40);
    EXPECT("follow: impl write @0x40 found", a != nullptr);
    if (a) EXPECT("follow: @0x40 high-conf in impl", a->highConfidence && a->writeCount == 1);
}

static void Test_Denken_DoesNotFollowNonThisCall() {
    // call rel32 with a NON-this rcx (rcx was clobbered by a load) must NOT
    // follow. Sequence: mov rcx, [rdx] (clobbers rcx) ; call impl ; ret.
    //   mov rcx, [rdx]   48 8B 0A
    //   call rel32       E8 <rel>     (instr at B0+3, next = B0+8)
    //   ret              C3
    const uintptr_t B0 = 0x140000000ULL;
    const uintptr_t B1 = 0x140001000ULL;
    const int32_t rel = static_cast<int32_t>(B1 - (B0 + 8));
    std::vector<uint8_t> thunk = {
        0x48, 0x8B, 0x0A,
        0xE8,
        static_cast<uint8_t>(rel & 0xFF),
        static_cast<uint8_t>((rel >> 8) & 0xFF),
        static_cast<uint8_t>((rel >> 16) & 0xFF),
        static_cast<uint8_t>((rel >> 24) & 0xFF),
        0xC3,
    };
    std::vector<DenkenRegion> regions = {
        { B0, thunk },
        { B1, { 0x89, 0x41, 0x40, 0xC3 } },   // would write @0x40 if (wrongly) followed
    };
    auto r = Denken::Analyze(B0, MakeReader(&regions));
    EXPECT("no-follow: ran", r.ok);
    EXPECT("no-follow: did not follow", r.callsFollowed == 0);
    EXPECT("no-follow: impl access not recorded", FindAccess(r, 0x40) == nullptr);
}

static void Test_Denken_TerminatesAndGuards() {
    // Bare ret -> ok, zero accesses, no crash.
    std::vector<DenkenRegion> ret = {{ 0x140000000ULL, { 0xC3 } }};
    auto r0 = Denken::Analyze(0x140000000ULL, MakeReader(&ret));
    EXPECT("guard: bare ret ok", r0.ok && r0.accesses.empty());

    // Unreadable start address -> not ok (reader returns 0).
    std::vector<DenkenRegion> empty;
    auto r1 = Denken::Analyze(0x140000000ULL, MakeReader(&empty));
    EXPECT("guard: unreadable start -> !ok", !r1.ok);

    // Null start / null reader -> not ok.
    auto r2 = Denken::Analyze(0, MakeReader(&ret));
    EXPECT("guard: null addr -> !ok", !r2.ok);
}

// ----- Lineal (UE5.7+ packed FUObjectItem reconstruction) ----------------
//
// No live game uses this layout yet, so the reconstruction MATH is the only
// thing verifiable today. These tests assert the Encode/Reconstruct round trip
// (any 8-aligned pointer survives a split-and-rebuild regardless of flag bits)
// plus the calibration-knob edges (alignBits / ptrMaskBits actually matter).

static void Test_Packed_RoundTrip_Basic() {
    Lineal::PackedConsts c;  // defaults: alignBits=3, ptrMask=0x3FFF
    const uintptr_t ptrs[] = {
        0x0000000140001000ULL,   // typical module-region pointer
        0x000001F809E08FB0ULL,   // typical heap pointer (8-aligned)
        0x0000700000000008ULL,   // high heap, minimal low bits
        0x0000000000000008ULL,   // smallest non-null aligned value
    };
    for (uintptr_t obj : ptrs) {
        uint64_t flags = 0; uint32_t low = 0;
        Lineal::Encode(obj, c, flags, low);
        EXPECT_EQ_U64("round-trip default consts", Lineal::Reconstruct(flags, low, c), obj);
    }
}

static void Test_Packed_RoundTrip_HighBits() {
    Lineal::PackedConsts c;
    // Top of the 47-bit x64 user-mode range, 8-aligned — proves the 14-bit
    // ptrMask captures every high pointer bit a real UObject* can carry.
    const uintptr_t obj = 0x00007FFFFFFFFFF8ULL;
    uint64_t flags = 0; uint32_t low = 0;
    Lineal::Encode(obj, c, flags, low);
    EXPECT_EQ_U64("round-trip 47-bit high pointer", Lineal::Reconstruct(flags, low, c), obj);
}

static void Test_Packed_ZeroAndNull() {
    Lineal::PackedConsts c;
    // ptrLow == 0 must reconstruct to 0 (the "empty/null slot" contract the
    // object walk relies on), regardless of any flag bits sitting in the high dword.
    EXPECT_EQ_U64("ptrLow=0 -> null", Lineal::Reconstruct(0xFFFFFFFF00000000ULL, 0, c), 0ULL);
    EXPECT_EQ_U64("all-zero -> null", Lineal::Reconstruct(0, 0, c), 0ULL);
}

static void Test_Packed_FlagsDoNotLeak() {
    Lineal::PackedConsts c;
    const uintptr_t obj = 0x000001F809E08FB0ULL;
    uint64_t flags = 0; uint32_t low = 0;
    // Seed the low 32 bits (real flags/refcount) with noise — they must NOT
    // bleed into the reconstructed pointer.
    Lineal::Encode(obj, c, flags, low, /*flagsExtra=*/0xDEADBEEFull);
    EXPECT_EQ_U64("flags in low dword do not corrupt ptr",
                  Lineal::Reconstruct(flags, low, c), obj);
    // And confirm the low dword actually carried the seeded flags (so the test
    // proves isolation, not that flagsExtra was silently dropped).
    EXPECT_EQ_U64("flagsExtra preserved in low dword", flags & 0xFFFFFFFFull, 0xDEADBEEFull);
}

static void Test_Packed_AlignBitsKnob() {
    // A non-default alignBits changes the encoding; round trip must still hold
    // when Encode and Reconstruct share the same consts.
    Lineal::PackedConsts c4; c4.alignBits = 4;  // hypothetical 16-byte alignment
    const uintptr_t obj = 0x000001F809E08F00ULL;     // 16-aligned
    uint64_t flags = 0; uint32_t low = 0;
    Lineal::Encode(obj, c4, flags, low);
    EXPECT_EQ_U64("round-trip alignBits=4", Lineal::Reconstruct(flags, low, c4), obj);

    // Decoding the SAME fields with the default alignBits=3 must yield a
    // DIFFERENT pointer — i.e. the knob is load-bearing, not ignored.
    Lineal::PackedConsts c3;
    EXPECT("alignBits mismatch diverges",
           Lineal::Reconstruct(flags, low, c3) != obj);
}

static void Test_Packed_PtrMaskKnob() {
    // Widening ptrMask must not break a pointer whose high bits already fit the
    // narrower mask (round trip stable), but a deliberately-too-narrow mask must
    // drop high bits — guarding against the constant being ignored.
    const uintptr_t obj = 0x00007FFFFFFFFFF8ULL;

    Lineal::PackedConsts wide; wide.ptrMaskBits = 0x7FFFull;  // 15 bits
    uint64_t f = 0; uint32_t l = 0;
    Lineal::Encode(obj, wide, f, l);
    EXPECT_EQ_U64("round-trip wider mask", Lineal::Reconstruct(f, l, wide), obj);

    Lineal::PackedConsts narrow; narrow.ptrMaskBits = 0x00FFull;  // 8 bits — too narrow
    EXPECT("too-narrow mask loses high bits",
           Lineal::Reconstruct(f, l, narrow) != obj);
}

// ----- GraphPath::BfsShortestObjectPath (Locate in GWorld) -----------------
//
// The BFS core is pure (no live memory) so the search invariants — shortest
// path, cycle safety, depth bound, abort, visited cap, reconstruction — are
// exercised here against an in-memory mock graph. The live adjacency adapter
// (EnumerateOutgoingObjectPtrs over real GObjects) is integration-only.

namespace {

struct MockEdge {
    uintptr_t   to;
    int32_t     off;
    std::string name;
    std::string type;
    std::string inner;
    int32_t     elem;
    int32_t     stride;
    int32_t     valOff;
};

struct MockGraph {
    std::unordered_map<uintptr_t, std::vector<MockEdge>> adj;
    void add(uintptr_t from, uintptr_t to, int32_t off = 0,
             std::string name = "f", std::string type = "ObjectProperty",
             std::string inner = "", int32_t elem = -1,
             int32_t stride = 0, int32_t valOff = 0) {
        adj[from].push_back({to, off, std::move(name), std::move(type), std::move(inner),
                             elem, stride, valOff});
    }
};

// Build a neighbor functor over a mock graph (generic-lambda compatible).
#define MOCK_NB(g) [&](uintptr_t node, auto&& emit) {                                       \
        auto it = (g).adj.find(node);                                                       \
        if (it == (g).adj.end()) return;                                                    \
        for (const auto& e : it->second)                                                    \
            if (emit(e.to, e.off, e.name, e.type, e.inner, e.elem, e.stride, e.valOff)) return; \
    }

static auto kNeverAbort = [] { return false; };

} // namespace

static void Test_GraphPath_DirectChild() {
    MockGraph g;
    g.add(0x1000, 0x2000, 0x40, "Target");
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x2000ull, 5, 1000000,
                                         MOCK_NB(g), kNeverAbort);
    EXPECT("direct child found", r.found);
    EXPECT("direct child status ok", r.status == "ok");
    EXPECT("direct child 1 hop", r.depthReached == 1);
    EXPECT("direct child step toObj", r.steps.size() == 1 && r.steps[0].toObj == 0x2000ull);
    EXPECT("direct child step offset", r.steps.size() == 1 && r.steps[0].fieldOffset == 0x40);
    EXPECT("direct child step name", r.steps.size() == 1 && r.steps[0].fieldName == "Target");
}

static void Test_GraphPath_RootEqualsTarget() {
    MockGraph g;
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x1000ull, 5, 1000000,
                                         MOCK_NB(g), kNeverAbort);
    EXPECT("root==target found", r.found);
    EXPECT("root==target no steps", r.steps.empty());
    EXPECT("root==target status ok", r.status == "ok");
}

static void Test_GraphPath_ShortestAmongTwo() {
    // root -> A -> B -> target  (3 hops)   and   root -> C -> target (2 hops)
    // BFS must return the 2-hop path regardless of edge insertion order.
    MockGraph g;
    g.add(0x1000, 0x2000, 1, "A");      // long branch first
    g.add(0x2000, 0x3000, 2, "B");
    g.add(0x3000, 0x9000, 3, "target_via_B");
    g.add(0x1000, 0x4000, 4, "C");      // short branch
    g.add(0x4000, 0x9000, 5, "target_via_C");
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x9000ull, 5, 1000000,
                                         MOCK_NB(g), kNeverAbort);
    EXPECT("shortest found", r.found);
    EXPECT("shortest is 2 hops", r.depthReached == 2);
    EXPECT("shortest goes via C", r.steps.size() == 2 && r.steps[0].toObj == 0x4000ull);
    EXPECT("shortest last edge name", r.steps.size() == 2 && r.steps[1].fieldName == "target_via_C");
}

static void Test_GraphPath_Cycle() {
    // root -> A -> B -> A (cycle), B -> target. Must terminate + find target.
    MockGraph g;
    g.add(0x1000, 0x2000, 1, "A");
    g.add(0x2000, 0x3000, 2, "B");
    g.add(0x3000, 0x2000, 3, "back_to_A");   // cycle edge
    g.add(0x3000, 0x9000, 4, "target");
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x9000ull, 10, 1000000,
                                         MOCK_NB(g), kNeverAbort);
    EXPECT("cycle terminates + finds", r.found);
    EXPECT("cycle path 3 hops", r.depthReached == 3);
    EXPECT("cycle visited bounded", r.visited == 4);  // root, A, B, target
}

static void Test_GraphPath_DepthBound() {
    // Linear chain root(0) -> n1 -> n2 -> n3 -> n4 -> n5 -> n6(target at depth 6)
    MockGraph g;
    uintptr_t prev = 0x1000;
    for (int i = 1; i <= 6; ++i) {
        uintptr_t cur = 0x1000 + static_cast<uintptr_t>(i) * 0x1000;
        g.add(prev, cur, i, "n" + std::to_string(i));
        prev = cur;
    }
    uintptr_t target = 0x1000 + 6 * 0x1000;

    auto tooShallow = Aura::BfsShortestObjectPath(0x1000ull, target, 5, 1000000,
                                                  MOCK_NB(g), kNeverAbort);
    EXPECT("depth 5 cannot reach depth-6 target", !tooShallow.found);
    EXPECT("depth 5 status not_reachable", tooShallow.status == "not_reachable");

    auto deepEnough = Aura::BfsShortestObjectPath(0x1000ull, target, 6, 1000000,
                                                  MOCK_NB(g), kNeverAbort);
    EXPECT("depth 6 reaches depth-6 target", deepEnough.found);
    EXPECT("depth 6 path is 6 hops", deepEnough.depthReached == 6);
}

static void Test_GraphPath_Unreachable() {
    MockGraph g;
    g.add(0x1000, 0x2000, 1, "A");   // target 0x9000 not in graph
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x9000ull, 5, 1000000,
                                         MOCK_NB(g), kNeverAbort);
    EXPECT("unreachable not found", !r.found);
    EXPECT("unreachable status", r.status == "not_reachable");
}

static void Test_GraphPath_Abort() {
    MockGraph g;
    g.add(0x1000, 0x2000, 1, "A");
    g.add(0x2000, 0x9000, 2, "target");
    auto alwaysAbort = [] { return true; };
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x9000ull, 5, 1000000,
                                         MOCK_NB(g), alwaysAbort);
    EXPECT("abort not found", !r.found);
    EXPECT("abort flag set", r.aborted);
    EXPECT("abort status", r.status == "aborted");
}

static void Test_GraphPath_VisitedCap() {
    // root -> A -> B -> target, cap visited at 2 → cannot discover B/target.
    MockGraph g;
    g.add(0x1000, 0x2000, 1, "A");
    g.add(0x2000, 0x3000, 2, "B");
    g.add(0x3000, 0x9000, 3, "target");
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x9000ull, 10, /*maxVisited=*/2,
                                         MOCK_NB(g), kNeverAbort);
    EXPECT("cap not found", !r.found);
    EXPECT("cap status", r.status == "visited_cap");
}

static void Test_GraphPath_ContainerEdgePreserved() {
    // An array-element edge must round-trip its type + element index into the step.
    MockGraph g;
    g.add(0x1000, 0x2000, 0x80, "Actors", "ArrayProperty", "ObjectProperty", 5234);
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x2000ull, 5, 1000000,
                                         MOCK_NB(g), kNeverAbort);
    EXPECT("container edge found", r.found && r.steps.size() == 1);
    EXPECT("container edge type", r.steps.size() == 1 && r.steps[0].fieldType == "ArrayProperty");
    EXPECT("container edge inner", r.steps.size() == 1 && r.steps[0].innerType == "ObjectProperty");
    EXPECT("container edge element index", r.steps.size() == 1 && r.steps[0].elementIndex == 5234);
}

static void Test_GraphPath_MapSetElementGeometryRoundTrip() {
    // A Map-value element edge must round-trip its element stride + within-pair value
    // offset into the step so the UI can split it into container + element CE derefs.
    MockGraph g;
    // MapProperty, sparse slot 3, pairStride=0x18, valueOffset=0x10 (the .Value edge).
    g.add(0x1000, 0x2000, 0xC0, "Attrs.Value", "MapProperty", "ObjectProperty", 3, 0x18, 0x10);
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x2000ull, 5, 1000000,
                                         MOCK_NB(g), kNeverAbort);
    EXPECT("map edge found", r.found && r.steps.size() == 1);
    EXPECT("map edge type", r.steps.size() == 1 && r.steps[0].fieldType == "MapProperty");
    EXPECT("map edge element index", r.steps.size() == 1 && r.steps[0].elementIndex == 3);
    EXPECT("map edge stride", r.steps.size() == 1 && r.steps[0].elemStride == 0x18);
    EXPECT("map edge value offset", r.steps.size() == 1 && r.steps[0].elemValueOffset == 0x10);
    // A direct (non-element) edge leaves the geometry zeroed.
    MockGraph g2;
    g2.add(0x1000, 0x2000, 0x40, "Direct");
    auto r2 = Aura::BfsShortestObjectPath(0x1000ull, 0x2000ull, 5, 1000000,
                                          MOCK_NB(g2), kNeverAbort);
    EXPECT("direct edge zero stride", r2.steps.size() == 1 && r2.steps[0].elemStride == 0
                                       && r2.steps[0].elemValueOffset == 0);
}

static void Test_GraphPath_Reconstruction() {
    // GWorld(0x1000) -> Level(0x2000) -> Actor(0x3000) -> Comp(0x4000=target)
    MockGraph g;
    g.add(0x1000, 0x2000, 0x30, "PersistentLevel");
    g.add(0x2000, 0x3000, 0x98, "Actors", "ArrayProperty", "ObjectProperty", 12);
    g.add(0x3000, 0x4000, 0x140, "RootComponent");
    auto r = Aura::BfsShortestObjectPath(0x1000ull, 0x4000ull, 5, 1000000,
                                         MOCK_NB(g), kNeverAbort);
    EXPECT("reconstruct found", r.found && r.steps.size() == 3);
    if (r.steps.size() == 3) {
        EXPECT("step0 from=root", r.steps[0].fromObj == 0x1000ull);
        EXPECT("step0 to=Level",  r.steps[0].toObj == 0x2000ull && r.steps[0].fieldName == "PersistentLevel");
        EXPECT("step1 to=Actor",  r.steps[1].toObj == 0x3000ull && r.steps[1].elementIndex == 12);
        EXPECT("step2 to=target", r.steps[2].toObj == 0x4000ull && r.steps[2].fieldName == "RootComponent");
        EXPECT("steps are ordered root->target",
               r.steps[0].fromObj == 0x1000ull &&
               r.steps[1].fromObj == r.steps[0].toObj &&
               r.steps[2].fromObj == r.steps[1].toObj);
    }
}

// ----- Solitar::ApplyBoolBit (GodMode FBoolProperty bit write) ----------------
// The critical correctness property: a single-bit read-modify-write must leave
// the other 7 bitfields packed in the same byte untouched (GodMode ON clears
// bCanBeDamaged; OFF restores it).

static void Test_Solitar_ApplyBoolBit() {
    using Solitar::ApplyBoolBit;
    // Set a bit.
    EXPECT_EQ_U64("set bit into 0x00",          ApplyBoolBit(0x00, 0x04, true),  0x04);
    EXPECT_EQ_U64("set already-set bit",        ApplyBoolBit(0x04, 0x04, true),  0x04);
    EXPECT_EQ_U64("set bit preserves others",   ApplyBoolBit(0xFB, 0x04, true),  0xFF);
    // Clear a bit (GodMode ON ⇒ bCanBeDamaged FALSE).
    EXPECT_EQ_U64("clear bit from 0xFF",        ApplyBoolBit(0xFF, 0x04, false), 0xFB);
    EXPECT_EQ_U64("clear already-clear bit",    ApplyBoolBit(0x00, 0x04, false), 0x00);
    EXPECT_EQ_U64("clear bit preserves others", ApplyBoolBit(0x05, 0x04, false), 0x01);
    // Every single-bit mask: set/clear touches only that bit; idempotent.
    for (int i = 0; i < 8; ++i) {
        uint8_t mask = static_cast<uint8_t>(1u << i);
        EXPECT_EQ_U64("clear one bit of 0xFF leaves ~mask",
                      ApplyBoolBit(0xFF, mask, false), static_cast<uint8_t>(0xFF & ~mask));
        EXPECT_EQ_U64("set one bit of 0x00 leaves mask",
                      ApplyBoolBit(0x00, mask, true), mask);
        uint8_t setOnce = ApplyBoolBit(0xA5, mask, true);
        EXPECT_EQ_U64("idempotent set",   ApplyBoolBit(setOnce, mask, true),  setOnce);
        uint8_t clrOnce = ApplyBoolBit(0xA5, mask, false);
        EXPECT_EQ_U64("idempotent clear", ApplyBoolBit(clrOnce, mask, false), clrOnce);
    }
}

// ----- Solitar::MatchProtectionBool (T2 generic invincibility-flag matcher) ---
// Polarity is the bug-prone part: a wrong value would ENABLE damage. Lock the
// keyword set + protect-value for each known flag, and confirm unrelated /
// ambiguous names (deal-damage, visibility) are NOT matched.

static void Test_Solitar_MatchProtectionBool() {
    bool p = false;
    // Positive (protect = true): set the flag ON for godmode.
    EXPECT("binvincible matched",  Solitar::MatchProtectionBool("binvincible", p));
    EXPECT("binvincible protect=true", p == true);
    EXPECT("bisinvulnerable matched", Solitar::MatchProtectionBool("bisinvulnerable", p));
    EXPECT("invulnerable protect=true (NOT read as vulnerable)", p == true);
    EXPECT("bisimmortal matched",  Solitar::MatchProtectionBool("bisimmortal", p));
    EXPECT("immortal protect=true", p == true);
    EXPECT("bmuteki matched",      Solitar::MatchProtectionBool("bmuteki", p));
    EXPECT("muteki protect=true",  p == true);
    EXPECT("bdamageimmune matched", Solitar::MatchProtectionBool("bdamageimmune", p));
    EXPECT("damageimmune protect=true", p == true);
    // Negative (protect = false): clear the flag for godmode.
    EXPECT("bcanbedamaged matched", Solitar::MatchProtectionBool("bcanbedamaged", p));
    EXPECT("canbedamaged protect=false", p == false);
    EXPECT("bcantakedamage matched", Solitar::MatchProtectionBool("bcantakedamage", p));
    EXPECT("cantakedamage protect=false", p == false);
    // Must NOT match: ambiguous deal-damage flags + unrelated bools.
    EXPECT("bcandamage NOT matched (deal-damage)", !Solitar::MatchProtectionBool("bcandamage", p));
    EXPECT("bnodamage NOT matched (ambiguous)",    !Solitar::MatchProtectionBool("bnodamage", p));
    EXPECT("bhidden NOT matched",   !Solitar::MatchProtectionBool("bhidden", p));
    EXPECT("bvisible NOT matched",  !Solitar::MatchProtectionBool("bvisible", p));
    EXPECT("breplicates NOT matched", !Solitar::MatchProtectionBool("breplicates", p));
}

// ----- Neu: UEnum::Names layout (legacy TArray vs UE5.6+ FNameData) -----------
// Synthetic memory: register buffers at chosen virtual addresses; the read
// callback serves bytes from registered ranges and FAILS for any unmapped
// address — exactly mirroring Macht::ReadSafe on game memory, so the parser's
// pointer-readability checks (the format disambiguator) are exercised WITHOUT a
// live process or FNamePool. Names are stored as raw int32 FName comparison
// indices (string resolution is Serie's job, not Neu's).
struct NeuFakeMem {
    std::vector<std::pair<uintptr_t, std::vector<uint8_t>>> regions;
    void Put(uintptr_t addr, const void* data, size_t n) {
        std::vector<uint8_t> b(n);
        std::memcpy(b.data(), data, n);
        regions.emplace_back(addr, std::move(b));
    }
    bool Read(uintptr_t a, void* o, size_t n) const {
        for (const auto& r : regions) {
            if (a >= r.first && (a - r.first) + n <= r.second.size()) {
                std::memcpy(o, r.second.data() + (a - r.first), n);
                return true;
            }
        }
        return false;
    }
};

// Legacy TArray<TPair<FName,int64>> header (padded to 0x20 so +0x10 is readable,
// like a real UEnum where CppForm/EnumFlags follow the array) + interleaved data.
static void NeuPutLegacy(NeuFakeMem& fm, uintptr_t region, uintptr_t dataAddr,
                         const std::vector<std::pair<int32_t,int64_t>>& es, int fnameStride) {
    const size_t entryStride = static_cast<size_t>(fnameStride) + 8;
    std::vector<uint8_t> data(es.size() * entryStride, 0);
    for (size_t i = 0; i < es.size(); ++i) {
        std::memcpy(&data[i*entryStride], &es[i].first, 4);                            // FName idx @ +0
        std::memcpy(&data[i*entryStride + fnameStride], &es[i].second, 8);             // int64 value @ +stride
    }
    fm.Put(dataAddr, data.data(), data.size());
    uint8_t hdr[0x20] = {};
    uint64_t dataU = dataAddr;       std::memcpy(hdr + 0, &dataU, 8);
    int32_t num = (int32_t)es.size(); std::memcpy(hdr + 8, &num, 4);
    int32_t maxN = (int32_t)es.size(); std::memcpy(hdr + 12, &maxN, 4);  // ArrayMax
    fm.Put(region, hdr, sizeof(hdr));
}

// UE5.6+ FNameData {tagged FName*, tagged int64*, int32 NumValues} + parallel arrays.
static void NeuPutFNameData(NeuFakeMem& fm, uintptr_t region, uintptr_t namesAddr,
                            uintptr_t valuesAddr, const std::vector<std::pair<int32_t,int64_t>>& es,
                            int fnameStride, bool tagged) {
    std::vector<uint8_t> names(es.size() * fnameStride, 0);
    std::vector<uint8_t> vals(es.size() * 8, 0);
    for (size_t i = 0; i < es.size(); ++i) {
        std::memcpy(&names[i*fnameStride], &es[i].first, 4);  // FName idx at start of each FName slot
        std::memcpy(&vals[i*8], &es[i].second, 8);
    }
    fm.Put(namesAddr, names.data(), names.size());
    fm.Put(valuesAddr, vals.data(), vals.size());
    uint8_t hdr[0x18] = {};
    uint64_t tn = static_cast<uint64_t>(namesAddr)  | (tagged ? 1ull : 0ull);
    uint64_t tv = static_cast<uint64_t>(valuesAddr) | (tagged ? 1ull : 0ull);
    int32_t num = (int32_t)es.size();
    std::memcpy(hdr + 0,  &tn, 8);
    std::memcpy(hdr + 8,  &tv, 8);
    std::memcpy(hdr + 16, &num, 4);
    fm.Put(region, hdr, sizeof(hdr));
}

static void Test_Neu_Legacy_Basic() {
    NeuFakeMem fm;
    std::vector<std::pair<int32_t,int64_t>> es = {{10,0},{20,1},{30,2},{40,3}};
    NeuPutLegacy(fm, 0x10000000, 0x20000000, es, 8);
    auto rd = [&](uintptr_t a, void* o, size_t n){ return fm.Read(a, o, n); };

    Neu::EnumNamesLayout L;
    EXPECT("legacy detect",  Neu::DetectLayout(rd, 0x10000000, 8, 16384, L));
    EXPECT("legacy format",  L.format == Neu::EnumNamesFormat::Legacy);
    EXPECT_EQ_U64("legacy count", L.count, 4);
    int32_t idx = 0; int64_t v = 0;
    EXPECT("legacy entry0",  Neu::ReadEntry(rd, L, 0, idx, v));
    EXPECT_EQ_U64("legacy idx0", idx, 10);  EXPECT_EQ_U64("legacy val0", v, 0);
    Neu::ReadEntry(rd, L, 3, idx, v);
    EXPECT_EQ_U64("legacy idx3", idx, 40);  EXPECT_EQ_U64("legacy val3", v, 3);
    // BuildLayout with the known format (what the live reader uses) matches.
    Neu::EnumNamesLayout L2;
    EXPECT("legacy build", Neu::BuildLayout(rd, 0x10000000, Neu::EnumNamesFormat::Legacy, 8, 16384, L2));
    EXPECT_EQ_U64("legacy build count", L2.count, 4);
}

static void Test_Neu_Legacy_CasePreserving() {
    NeuFakeMem fm;
    std::vector<std::pair<int32_t,int64_t>> es = {{5,0},{6,1},{7,2}};
    NeuPutLegacy(fm, 0x10000000, 0x20000000, es, 0x10);  // FName=16 -> stride 24, value @ +16
    auto rd = [&](uintptr_t a, void* o, size_t n){ return fm.Read(a, o, n); };
    Neu::EnumNamesLayout L;
    EXPECT("legacy CPN detect", Neu::DetectLayout(rd, 0x10000000, 0x10, 16384, L));
    int32_t idx = 0; int64_t v = 0;
    Neu::ReadEntry(rd, L, 2, idx, v);
    EXPECT_EQ_U64("legacy CPN idx2", idx, 7);  EXPECT_EQ_U64("legacy CPN val2", v, 2);
}

static void Test_Neu_FNameData_Basic() {
    NeuFakeMem fm;
    std::vector<std::pair<int32_t,int64_t>> es = {{100,0},{200,1},{300,2},{400,3},{500,4}};
    NeuPutFNameData(fm, 0x10000000, 0x30000000, 0x40000000, es, 8, /*tagged*/true);
    auto rd = [&](uintptr_t a, void* o, size_t n){ return fm.Read(a, o, n); };
    Neu::EnumNamesLayout L;
    EXPECT("fnd detect", Neu::DetectLayout(rd, 0x10000000, 8, 16384, L));
    EXPECT("fnd format", L.format == Neu::EnumNamesFormat::FNameData57);
    EXPECT_EQ_U64("fnd count", L.count, 5);
    EXPECT_EQ_U64("fnd namesPtr masked",  L.namesPtr,  0x30000000);  // tag bit stripped
    EXPECT_EQ_U64("fnd valuesPtr masked", L.valuesPtr, 0x40000000);
    int32_t idx = 0; int64_t v = 0;
    Neu::ReadEntry(rd, L, 0, idx, v);  EXPECT_EQ_U64("fnd idx0", idx, 100);  EXPECT_EQ_U64("fnd val0", v, 0);
    Neu::ReadEntry(rd, L, 4, idx, v);  EXPECT_EQ_U64("fnd idx4", idx, 500);  EXPECT_EQ_U64("fnd val4", v, 4);
}

static void Test_Neu_FNameData_CasePreserving() {
    NeuFakeMem fm;
    std::vector<std::pair<int32_t,int64_t>> es = {{11,0},{22,1},{33,2}};
    NeuPutFNameData(fm, 0x10000000, 0x30000000, 0x40000000, es, 0x10, true);  // FName=16 stride
    auto rd = [&](uintptr_t a, void* o, size_t n){ return fm.Read(a, o, n); };
    Neu::EnumNamesLayout L;
    EXPECT("fnd CPN detect", Neu::DetectLayout(rd, 0x10000000, 0x10, 16384, L));
    int32_t idx = 0; int64_t v = 0;
    Neu::ReadEntry(rd, L, 1, idx, v);
    EXPECT_EQ_U64("fnd CPN idx1", idx, 22);  EXPECT_EQ_U64("fnd CPN val1", v, 1);
}

static void Test_Neu_FNameData_SparseValues() {
    // Proves we read the ACTUAL values array, not assume sequential [0,1,2,...].
    NeuFakeMem fm;
    std::vector<std::pair<int32_t,int64_t>> es = {{1,0},{2,1},{3,2},{4,4},{5,8},{6,255}};
    NeuPutFNameData(fm, 0x10000000, 0x30000000, 0x40000000, es, 8, true);
    auto rd = [&](uintptr_t a, void* o, size_t n){ return fm.Read(a, o, n); };
    Neu::EnumNamesLayout L;
    EXPECT("fnd sparse detect", Neu::DetectLayout(rd, 0x10000000, 8, 16384, L));
    int32_t idx = 0; int64_t v = 0;
    Neu::ReadEntry(rd, L, 3, idx, v);  EXPECT_EQ_U64("fnd sparse val3", v, 4);
    Neu::ReadEntry(rd, L, 4, idx, v);  EXPECT_EQ_U64("fnd sparse val4", v, 8);
    Neu::ReadEntry(rd, L, 5, idx, v);  EXPECT_EQ_U64("fnd sparse val5", v, 255);
}

static void Test_Neu_TagBitMasked() {
    // Untagged (low bit 0) name/value pointers must still mask to the same base.
    NeuFakeMem fm;
    std::vector<std::pair<int32_t,int64_t>> es = {{7,0},{8,1}};
    NeuPutFNameData(fm, 0x10000000, 0x30000000, 0x40000000, es, 8, /*tagged*/false);
    auto rd = [&](uintptr_t a, void* o, size_t n){ return fm.Read(a, o, n); };
    Neu::EnumNamesLayout L;
    EXPECT("fnd untagged detect", Neu::DetectLayout(rd, 0x10000000, 8, 16384, L));
    EXPECT_EQ_U64("fnd untagged namesPtr", L.namesPtr, 0x30000000);
    int32_t idx = 0; int64_t v = 0;
    Neu::ReadEntry(rd, L, 1, idx, v);  EXPECT_EQ_U64("fnd untagged idx1", idx, 8);
}

static void Test_Neu_Disambiguation() {
    // A legacy header whose Num|Max 8-byte word masks into the pointer numeric
    // range but at UNMAPPED memory must still be read as Legacy — the FNameData
    // hypothesis is rejected because its "values pointer" won't dereference.
    NeuFakeMem fm;
    std::vector<std::pair<int32_t,int64_t>> es = {{10,0},{20,1}};
    const int stride = 8; const size_t entryStride = (size_t)stride + 8;
    std::vector<uint8_t> data(es.size() * entryStride, 0);
    for (size_t i = 0; i < es.size(); ++i) {
        std::memcpy(&data[i*entryStride], &es[i].first, 4);
        std::memcpy(&data[i*entryStride + stride], &es[i].second, 8);
    }
    fm.Put(0x20000000, data.data(), data.size());
    // Num=2, Max=0x55 -> w1 = 0x0000005500000002; (&~1) ~= 0x5500000002 is in the
    // pointer numeric range yet unmapped. Bait +0x10 with a plausible "NumValues".
    uint8_t hdr[0x18] = {};
    uint64_t dataU = 0x20000000;  std::memcpy(hdr + 0, &dataU, 8);
    int32_t num = 2, maxN = 0x55;  std::memcpy(hdr + 8, &num, 4);  std::memcpy(hdr + 12, &maxN, 4);
    int32_t bait = 2;              std::memcpy(hdr + 16, &bait, 4);
    fm.Put(0x10000000, hdr, sizeof(hdr));
    auto rd = [&](uintptr_t a, void* o, size_t n){ return fm.Read(a, o, n); };
    Neu::EnumNamesLayout L;
    EXPECT("disambig detect",       Neu::DetectLayout(rd, 0x10000000, 8, 16384, L));
    EXPECT("disambig picks Legacy", L.format == Neu::EnumNamesFormat::Legacy);
    EXPECT_EQ_U64("disambig count", L.count, 2);
}

static void Test_Neu_Edge() {
    Neu::EnumNamesLayout L;
    auto rd_none = [](uintptr_t, void*, size_t){ return false; };
    EXPECT("edge all-fault -> false", !Neu::DetectLayout(rd_none, 0x10000000, 8, 16384, L));

    {   // count over the cap -> rejected
        NeuFakeMem fm;
        std::vector<std::pair<int32_t,int64_t>> es = {{1,0},{2,1},{3,2},{4,3},{5,4}};
        NeuPutLegacy(fm, 0x10000000, 0x20000000, es, 8);
        auto rd = [&](uintptr_t a, void* o, size_t n){ return fm.Read(a, o, n); };
        Neu::EnumNamesLayout L2;
        EXPECT("edge over-cap -> false", !Neu::DetectLayout(rd, 0x10000000, 8, /*maxCount*/3, L2));
    }
    {   // FNameData header present but the arrays are unmapped -> rejected
        NeuFakeMem fm;
        uint8_t hdr[0x18] = {};
        uint64_t tn = 0x30000000ull | 1, tv = 0x40000000ull | 1;  int32_t num = 3;
        std::memcpy(hdr + 0, &tn, 8);  std::memcpy(hdr + 8, &tv, 8);  std::memcpy(hdr + 16, &num, 4);
        fm.Put(0x10000000, hdr, sizeof(hdr));   // arrays intentionally NOT registered
        auto rd = [&](uintptr_t a, void* o, size_t n){ return fm.Read(a, o, n); };
        Neu::EnumNamesLayout L3;
        EXPECT("edge unmapped arrays -> false", !Neu::DetectLayout(rd, 0x10000000, 8, 16384, L3));
    }
}

// ----- Orden::MatchGroup — multi-value group scan SDR matcher --------------
//
// Orden works on already-read Leaf structs (no memory functor) — the scanner
// produces the leaves, Orden does the pure combinatorial match. Helpers build
// synthetic leaves + multi-width slot targets via the SAME BuildNumericTargets
// machinery the live scan uses.

static Orden::Leaf OrdenLeaf(Radar::DataType width, int32_t pos,
                             const void* raw, size_t n, uint32_t descIdx = 0) {
    Orden::Leaf lf;
    lf.position      = pos;
    lf.width         = width;
    lf.descriptorIdx = descIdx;
    lf.elementIndex  = -1;
    std::memcpy(lf.bytes, raw, n);
    return lf;
}
static Orden::Leaf OrdenLeafI32(int32_t pos, int32_t v, uint32_t descIdx = 0) {
    return OrdenLeaf(Radar::DataType::Int32, pos, &v, 4, descIdx);
}
static Orden::Leaf OrdenLeafI16(int32_t pos, int16_t v) {
    return OrdenLeaf(Radar::DataType::Int16, pos, &v, 2);
}
static Orden::Leaf OrdenLeafFloat(int32_t pos, float v) {
    return OrdenLeaf(Radar::DataType::Float, pos, &v, 4);
}

static void Test_Orden_DistinctValues() {
    // Four numeric leaves at scattered offsets; four slots in a DIFFERENT order.
    // Mirrors the spec example: Str 24, Def 10, Dex 14, Int 8.
    std::vector<Orden::Leaf> leaves = {
        OrdenLeafI32(0x18, 8),    // Int  (smallest offset, last input slot)
        OrdenLeafI32(0x1C, 14),   // Dex
        OrdenLeafI32(0x20, 24),   // Str
        OrdenLeafI32(0x24, 10),   // Def
        OrdenLeafI32(0x2C, 99),   // unrelated leaf
    };
    Radar::NumericTargetSet t0, t1, t2, t3;
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "24", t0);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "10", t1);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "14", t2);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "8",  t3);
    std::vector<Orden::SlotTarget> slots = {{&t0},{&t1},{&t2},{&t3}};

    std::vector<Orden::SlotMatches> out;
    EXPECT("group distinct match", Orden::MatchGroup(leaves, slots, out));
    EXPECT("group 4 slots", out.size() == 4);
    // Each value is unique -> each slot resolves to exactly one leaf (locked).
    EXPECT("slot0 (24) singleton", out[0].leafIdx.size() == 1);
    EXPECT("slot0 -> pos 0x20", leaves[out[0].leafIdx[0]].position == 0x20);
    EXPECT("slot3 (8) -> pos 0x18 (order-independent)",
           out[3].leafIdx.size() == 1 && leaves[out[3].leafIdx[0]].position == 0x18);
}

static void Test_Orden_MissingValueRejected() {
    // No leaf holds 10 -> the Def slot has zero matches -> reject whole block.
    std::vector<Orden::Leaf> leaves = {
        OrdenLeafI32(0x10, 24), OrdenLeafI32(0x14, 14), OrdenLeafI32(0x18, 8),
    };
    Radar::NumericTargetSet t0, t1, t2, t3;
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "24", t0);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "10", t1);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "14", t2);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "8",  t3);
    std::vector<Orden::SlotTarget> slots = {{&t0},{&t1},{&t2},{&t3}};
    std::vector<Orden::SlotMatches> out;
    EXPECT("group missing value rejected", !Orden::MatchGroup(leaves, slots, out));
}

static void Test_Orden_DuplicateValuesSDR() {
    // Two slots want 24, one wants 10. Needs TWO distinct leaves holding 24.
    Radar::NumericTargetSet t24a, t24b, t10;
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "24", t24a);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "24", t24b);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "10", t10);
    std::vector<Orden::SlotTarget> slots = {{&t24a},{&t24b},{&t10}};

    {   // two leaves hold 24 -> distinct assignment exists
        std::vector<Orden::Leaf> leaves = {
            OrdenLeafI32(0x10, 24), OrdenLeafI32(0x14, 24), OrdenLeafI32(0x18, 10),
        };
        std::vector<Orden::SlotMatches> out;
        EXPECT("dup-value SDR ok (two 24s)", Orden::MatchGroup(leaves, slots, out));
    }
    {   // only ONE leaf holds 24 -> cannot satisfy both 24 slots
        std::vector<Orden::Leaf> leaves = {
            OrdenLeafI32(0x10, 24), OrdenLeafI32(0x18, 10),
        };
        std::vector<Orden::SlotMatches> out;
        EXPECT("dup-value SDR fail (one 24)", !Orden::MatchGroup(leaves, slots, out));
    }
}

static void Test_Orden_MultiWidthMatch() {
    // "24" must match the same value stored as Int16, Int32, or Float; "25" must not.
    Radar::NumericTargetSet t24, t25;
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "24", t24);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "25", t25);

    std::vector<Orden::Leaf> leaves = {
        OrdenLeafI16(0x10, 24), OrdenLeafFloat(0x14, 24.0f), OrdenLeafI32(0x18, 24),
    };
    {   // two slots both want 24 -> match against the int16 + float + int32 pool
        std::vector<Orden::SlotTarget> slots = {{&t24},{&t24}};
        std::vector<Orden::SlotMatches> out;
        EXPECT("multi-width 24 matches", Orden::MatchGroup(leaves, slots, out));
        EXPECT("multi-width slot0 has >=2 leaves", out[0].leafIdx.size() >= 2);
    }
    {   // 25 is absent at every width
        std::vector<Orden::SlotTarget> slots = {{&t25},{&t24}};
        std::vector<Orden::SlotMatches> out;
        EXPECT("multi-width 25 absent rejected", !Orden::MatchGroup(leaves, slots, out));
    }
}

static void Test_Orden_ConvergenceAndAssignment() {
    // HasDistinctAssignment directly models refine convergence: as per-slot lists
    // shrink they stay feasible until one empties.
    std::vector<Orden::SlotMatches> m(2);
    m[0].leafIdx = {0, 1};
    m[1].leafIdx = {1};
    EXPECT("SDR feasible before convergence", Orden::HasDistinctAssignment(m, 2));
    // Refine locks slot1->leaf1, forcing slot0->leaf0 (still feasible).
    m[0].leafIdx = {0};
    EXPECT("SDR feasible after lock", Orden::HasDistinctAssignment(m, 2));
    // Both collapse onto the same single leaf -> no distinct assignment.
    m[0].leafIdx = {1};
    EXPECT("SDR infeasible on collision", !Orden::HasDistinctAssignment(m, 2));
    // An emptied slot (its value vanished on refine) -> reject.
    m[0].leafIdx.clear();
    EXPECT("SDR infeasible on empty slot", !Orden::HasDistinctAssignment(m, 2));
}

static void Test_Orden_OrderedFirstScan() {
    // P2: per-slot ordered predicates on the FIRST scan (Bigger / Smaller),
    // routed through Radar::ComparePredicate by LeafSatisfiesSlot. Leaves at
    // Str 24, Def 10.
    std::vector<Orden::Leaf> leaves = {
        OrdenLeafI32(0x10, 24), OrdenLeafI32(0x14, 10),
    };
    Radar::NumericTargetSet t20, t15, t30;
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "20", t20);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "15", t15);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "30", t30);
    {   // slot0: > 20 (24 ok), slot1: < 15 (10 ok) -> distinct match
        std::vector<Orden::SlotTarget> slots = {
            { &t20, Radar::ScanType::Bigger,  0.0 },
            { &t15, Radar::ScanType::Smaller, 0.0 },
        };
        std::vector<Orden::SlotMatches> out;
        EXPECT("group ordered first-scan match", Orden::MatchGroup(leaves, slots, out));
    }
    {   // slot0: > 30 -> no leaf qualifies -> reject the whole block
        std::vector<Orden::SlotTarget> slots = {
            { &t30, Radar::ScanType::Bigger,  0.0 },
            { &t15, Radar::ScanType::Smaller, 0.0 },
        };
        std::vector<Orden::SlotMatches> out;
        EXPECT("group ordered first-scan reject (no leaf > 30)",
               !Orden::MatchGroup(leaves, slots, out));
    }
}

static void Test_Orden_BetweenFirstScan() {
    // P2: per-slot Between (inclusive range) on the first scan — needs both the
    // lower (`targets`) and upper (`targets2`) bound. Leaves Str 24, Def 10.
    std::vector<Orden::Leaf> leaves = {
        OrdenLeafI32(0x10, 24), OrdenLeafI32(0x14, 10),
    };
    Radar::NumericTargetSet lo20, hi30, lo5, hi12, hi8;
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "20", lo20);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "30", hi30);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "5",  lo5);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "12", hi12);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "8",  hi8);
    {   // slot0 in [20,30] (24 ok), slot1 in [5,12] (10 ok) -> distinct match
        std::vector<Orden::SlotTarget> slots = {
            { &lo20, Radar::ScanType::Between, 0.0, &hi30 },
            { &lo5,  Radar::ScanType::Between, 0.0, &hi12 },
        };
        std::vector<Orden::SlotMatches> out;
        EXPECT("group Between first-scan match", Orden::MatchGroup(leaves, slots, out));
    }
    {   // slot1 in [5,8] -> 10 is out of range -> reject the block
        std::vector<Orden::SlotTarget> slots = {
            { &lo20, Radar::ScanType::Between, 0.0, &hi30 },
            { &lo5,  Radar::ScanType::Between, 0.0, &hi8 },
        };
        std::vector<Orden::SlotMatches> out;
        EXPECT("group Between first-scan reject (10 not in [5,8])",
               !Orden::MatchGroup(leaves, slots, out));
    }
    {   // missing upper bound -> Between can't evaluate -> no match
        std::vector<Orden::SlotTarget> slots = {
            { &lo20, Radar::ScanType::Between, 0.0, nullptr },
            { &lo5,  Radar::ScanType::Between, 0.0, &hi12 },
        };
        std::vector<Orden::SlotMatches> out;
        EXPECT("group Between missing upper bound rejected",
               !Orden::MatchGroup(leaves, slots, out));
    }
}

static void Test_Orden_PrevValueRejectedOnFirstScan() {
    // Prev-value predicates (Increased / ...) have no baseline on the first scan,
    // so LeafSatisfiesSlot — and thus MatchGroup — must never match them,
    // regardless of the leaf value. (The refine path is what honours them.)
    std::vector<Orden::Leaf> leaves = {
        OrdenLeafI32(0x10, 24), OrdenLeafI32(0x14, 10),
    };
    Radar::NumericTargetSet t24, t10;
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "24", t24);
    Radar::BuildNumericTargets(Radar::DataType::NumericNoByte, "10", t10);
    std::vector<Orden::SlotTarget> slots = {
        { &t24, Radar::ScanType::Increased, 0.0 },   // prev-value: never matches here
        { &t10, Radar::ScanType::Exact,     0.0 },
    };
    std::vector<Orden::SlotMatches> out;
    EXPECT("group prev-value slot never matches on first scan",
           !Orden::MatchGroup(leaves, slots, out));
    EXPECT("group prev-value slot0 collected zero leaves", out[0].leafIdx.empty());
}

// ----- Ubel: Native-C scan P0 — hole computation + type normalization --------
//
// Pure helpers (no game memory). ComputeHoles is the interval-complement core
// shared by the Guess-What gap pass (WalkInstance) and the Native-C value scan;
// ComputeClassHoles is the ArrayDim-aware class-level builder the scan will use;
// NormalizeGuessedTypeToProperty maps Guess labels to canonical property strings.

static void Test_Holes_ComputeHoles_Basic() {
    // Two occupied fields in [0x28, 0x40): a gap before, between, and after.
    std::vector<Ubel::Interval> occ = { {0x30, 0x34}, {0x38, 0x3C} };
    auto holes = Ubel::ComputeHoles(occ, 0x28, 0x40);
    EXPECT("3 holes", holes.size() == 3);
    if (holes.size() == 3) {
        EXPECT_EQ_U64("hole0 start", holes[0].start, 0x28); EXPECT_EQ_U64("hole0 end", holes[0].end, 0x30);
        EXPECT_EQ_U64("hole1 start", holes[1].start, 0x34); EXPECT_EQ_U64("hole1 end", holes[1].end, 0x38);
        EXPECT_EQ_U64("hole2 start", holes[2].start, 0x3C); EXPECT_EQ_U64("hole2 end", holes[2].end, 0x40);
    }
}

static void Test_Holes_LeadingGapSurvives() {
    // Regression for commit 75ea723: a field at/after the first real offset must
    // NOT swallow the [windowStart, firstField) leading region.
    std::vector<Ubel::Interval> occ = { {0x40, 0x44} };
    auto holes = Ubel::ComputeHoles(occ, 0x28, 0x80);
    EXPECT("leading + trailing = 2 holes", holes.size() == 2);
    if (holes.size() == 2) {
        EXPECT_EQ_U64("leading hole start", holes[0].start, 0x28);
        EXPECT_EQ_U64("leading hole end",   holes[0].end,   0x40);
        EXPECT_EQ_U64("trailing hole start", holes[1].start, 0x44);
        EXPECT_EQ_U64("trailing hole end",   holes[1].end,   0x80);
    }
}

static void Test_Holes_FullyCovered() {
    std::vector<Ubel::Interval> occ = { {0x28, 0x40} };
    EXPECT("fully covered -> no holes", Ubel::ComputeHoles(occ, 0x28, 0x40).empty());
    // Overlapping + adjacent intervals merge to full coverage.
    std::vector<Ubel::Interval> occ2 = { {0x28, 0x34}, {0x30, 0x3A}, {0x3A, 0x40} };
    EXPECT("merged coverage -> no holes", Ubel::ComputeHoles(occ2, 0x28, 0x40).empty());
}

static void Test_Holes_ClampsOutOfWindow() {
    // A field reaching below windowStart is trimmed (header bytes excluded), and
    // a field with a garbage-huge end is trimmed to windowEnd — neither drops the
    // surrounding holes.
    std::vector<Ubel::Interval> occ = { {0x00, 0x2C}, {0x30, 0x7FFFFFF0} };
    auto holes = Ubel::ComputeHoles(occ, 0x28, 0x40);
    EXPECT("one middle hole", holes.size() == 1);
    if (holes.size() == 1) {
        EXPECT_EQ_U64("hole start (after clamped header field)", holes[0].start, 0x2C);
        EXPECT_EQ_U64("hole end (before clamped huge field)",   holes[0].end,   0x30);
    }
    // Empty / inverted window yields nothing.
    EXPECT("empty window -> no holes", Ubel::ComputeHoles(occ, 0x40, 0x40).empty());
    EXPECT("inverted window -> no holes", Ubel::ComputeHoles(occ, 0x40, 0x28).empty());
}

static void Test_Holes_ComputeClassHoles_ArrayDim() {
    // A static C-array UPROPERTY int Foo[10] at 0x40 (ElementSize 4, ArrayDim 10)
    // occupies [0x40, 0x68) — its tail must NOT be reported as a hole (the
    // phantom-hole bug the ArrayDim read fixes). A scalar int at 0x68 follows.
    ClassInfo ci;
    ci.PropertiesSize = 0x80;
    FieldInfo arr; arr.Offset = 0x40; arr.Size = 4; arr.ArrayDim = 10; ci.Fields.push_back(arr);
    FieldInfo sc;  sc.Offset = 0x68;  sc.Size = 4;  sc.ArrayDim = 1;  ci.Fields.push_back(sc);

    auto holes = Ubel::ComputeClassHoles(ci, 0x28, 0x80);
    // Expect: [0x28,0x40) leading, [0x6C,0x80) trailing. NO [0x44,0x68) phantom.
    EXPECT("array-dim: 2 holes (no phantom)", holes.size() == 2);
    if (holes.size() == 2) {
        EXPECT_EQ_U64("leading hole end == array start", holes[0].end, 0x40);
        EXPECT_EQ_U64("trailing hole start == after scalar", holes[1].start, 0x6C);
    }
    // Sanity: if ArrayDim were ignored (==1) a phantom [0x44,0x68) would appear.
    FieldInfo arrBad = arr; arrBad.ArrayDim = 1;
    ClassInfo ciBad; ciBad.PropertiesSize = 0x80; ciBad.Fields = { arrBad, sc };
    EXPECT("array-dim=1 control yields a phantom hole",
           Ubel::ComputeClassHoles(ciBad, 0x28, 0x80).size() == 3);
}

static void Test_Holes_NormalizeGuessedType() {
    using DT = Radar::DataType;
    // Every label GuessGapTypes can emit must normalize to a canonical property
    // string that BOTH Radar::TryDataTypeFromPropertyTypeName (DLL) and (verified
    // separately, C# side) SnapshotNumeric.TryFromHex accept — or to "" (drop).
    struct Case { const char* guess; const char* canon; };
    const Case cases[] = {
        {"Float",  "FloatProperty"},  {"Float?",  "FloatProperty"},
        {"Double", "DoubleProperty"}, {"Double?", "DoubleProperty"},
        {"Int32?", "IntProperty"},    {"Int16?",  "Int16Property"},
        {"Byte?",  "ByteProperty"},
    };
    for (const auto& c : cases) {
        std::string canon = Ubel::NormalizeGuessedTypeToProperty(c.guess);
        EXPECT(c.guess, canon == c.canon);
        DT dt;
        EXPECT("normalized resolves in Radar", Radar::TryDataTypeFromPropertyTypeName(canon, dt));
    }
    // Padding / Pointer? have no gameplay-numeric meaning -> dropped.
    EXPECT("Padding -> drop",  Ubel::NormalizeGuessedTypeToProperty("Padding").empty());
    EXPECT("Pointer? -> drop", Ubel::NormalizeGuessedTypeToProperty("Pointer?").empty());
    EXPECT("unknown -> drop",  Ubel::NormalizeGuessedTypeToProperty("Mystery").empty());
}

int main() {
    std::printf("dll_helpers_test (Renge + Scharf + Radar)\n");
    std::printf("------------------------------------------\n");

    Test_TryStrToAddr_AcceptsValidHex();
    Test_TryStrToAddr_RejectsCePlaceholder();
    Test_TryStrToAddr_RejectsTrailingGarbage();
    Test_TryStrToAddr_RejectsEmpty();
    Test_TryStrToAddr_RejectsNonHex();
    Test_StrToAddr_NoexceptZeroOnFailure();

    Test_Alignment_PointerProperties_Need8();
    Test_Alignment_EnumProperty_RespectsElemSize();
    Test_Alignment_NameProperty_RespectsCpnMode();
    Test_Alignment_ScalarPrimitives();
    Test_Alignment_OffsetZeroNeverSuspicious();
    Test_Alignment_UnknownTypesNotValidated();
    Test_Alignment_WeakAndSparseDelegate();

    Test_Mimic_PollLatency_OneMillisecond();

    Test_ValueScan_DataTypeSizes();
    Test_ValueScan_ParseDataTypeRoundTrip();
    Test_ValueScan_ScanTypePartitioning();
    Test_ValueScan_Predicate_Int32();
    Test_ValueScan_Predicate_Int8Negative();
    Test_ValueScan_Predicate_Float();
    Test_ValueScan_Predicate_Double();
    Test_ValueScan_Predicate_Bool();
    Test_ValueScan_Predicate_UInt64_RangeBoundary();
    Test_ValueScan_FloatTolerance_Exact();
    Test_ValueScan_FloatTolerance_Ordered();
    Test_ValueScan_FloatTolerance_PrevValue();
    Test_ValueScan_FloatTolerance_Between();
    Test_ValueScan_IntegerTypes_IgnoreTolerance();

    // Phase 2A — string predicates + family predicates
    Test_ValueScan_TypeFamilyPredicates();
    Test_ValueScan_IsScanTypeValidFor();
    Test_ValueScan_StringPredicate_Exact();
    Test_ValueScan_StringPredicate_Substring();
    Test_ValueScan_StringPredicate_PrevValue();
    Test_ValueScan_StringPredicate_RejectsNumericOrdering();
    // Phase 2B — vector predicates
    Test_ValueScan_VectorPredicate_Exact();
    Test_ValueScan_VectorPredicate_Ordering();
    Test_ValueScan_VectorPredicate_Between();
    Test_ValueScan_VectorPredicate_PrevValue();
    Test_ValueScan_VectorPredicate_RejectsSubstring();
    Test_ValueScan_VectorStructNames();
    // build 794 — multi-numeric (NumericNoByte) meta type
    Test_ValueScan_MultiNumericMembers();
    Test_ValueScan_DataTypeFromPropertyTypeName();
    Test_ValueScan_PropertyTypeNameOf_Inverse();
    Test_ValueScan_BuildNumericTargets();
    // Phase A1a — snapshot field selection
    Test_ValueScan_SelectSnapshotNumericFields();
    // Phase A1b — struct-array inner-key selection
    Test_ValueScan_SelectArrayInnerKey();

    Test_ValueScan_SessionLifecycle();
    Test_ValueScan_FieldDisplayName();
    Test_ValueScan_OptionalFlagOffset();
    Test_ValueScan_OrderedView();
    Test_ValueScan_OrderedViewScale();
    Test_ValueScan_SparseContainerGeometry();

    // Path 2 — native x64 disassembly (Denken decoder core)
    Test_Denken_BasicAccesses();
    Test_Denken_ExcludesStackAndZeroDisp();
    Test_Denken_FollowsCallHandoff();
    Test_Denken_DoesNotFollowNonThisCall();
    Test_Denken_TerminatesAndGuards();

    // UE5.7+ packed FUObjectItem reconstruction (math-only; no live game exists)
    Test_Packed_RoundTrip_Basic();
    Test_Packed_RoundTrip_HighBits();
    Test_Packed_ZeroAndNull();
    Test_Packed_FlagsDoNotLeak();
    Test_Packed_AlignBitsKnob();
    Test_Packed_PtrMaskKnob();

    // GraphPath BFS core — "Locate in GWorld" shortest-path search (mock graph)
    Test_GraphPath_DirectChild();
    Test_GraphPath_RootEqualsTarget();
    Test_GraphPath_ShortestAmongTwo();
    Test_GraphPath_Cycle();
    Test_GraphPath_DepthBound();
    Test_GraphPath_Unreachable();
    Test_GraphPath_Abort();
    Test_GraphPath_VisitedCap();
    Test_GraphPath_ContainerEdgePreserved();
    Test_GraphPath_MapSetElementGeometryRoundTrip();
    Test_GraphPath_Reconstruction();

    // Solitar GodMode — FBoolProperty single-bit read-modify-write
    Test_Solitar_ApplyBoolBit();
    Test_Solitar_MatchProtectionBool();

    // Neu — UEnum::Names layout: legacy TArray vs UE5.6+ FNameData (synthetic memory)
    Test_Neu_Legacy_Basic();
    Test_Neu_Legacy_CasePreserving();
    Test_Neu_FNameData_Basic();
    Test_Neu_FNameData_CasePreserving();
    Test_Neu_FNameData_SparseValues();
    Test_Neu_TagBitMasked();
    Test_Neu_Disambiguation();
    Test_Neu_Edge();

    // Orden — multi-value group scan SDR matcher (synthetic leaves, no game)
    Test_Orden_DistinctValues();
    Test_Orden_MissingValueRejected();
    Test_Orden_DuplicateValuesSDR();
    Test_Orden_MultiWidthMatch();
    Test_Orden_ConvergenceAndAssignment();
    Test_Orden_OrderedFirstScan();
    Test_Orden_BetweenFirstScan();
    Test_Orden_PrevValueRejectedOnFirstScan();

    // Ubel — Native-C scan P0: hole computation + Guess-type normalization (pure)
    Test_Holes_ComputeHoles_Basic();
    Test_Holes_LeadingGapSurvives();
    Test_Holes_FullyCovered();
    Test_Holes_ClampsOutOfWindow();
    Test_Holes_ComputeClassHoles_ArrayDim();
    Test_Holes_NormalizeGuessedType();

    std::printf("------------------------------------------\n");
    std::printf("Pass: %d   Fail: %d\n", g_pass, g_fail);
    return g_fail;
}
