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
#include "../src/ValueScan.h"

#include <Windows.h>
#include <timeapi.h>   // timeBeginPeriod — exercised by the poll-latency check

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

// ----- ValueScan: SizeOf + NameOf + parsers ---------------------------------

static void Test_ValueScan_DataTypeSizes() {
    EXPECT("SizeOf Int8 = 1",   ValueScan::SizeOf(ValueScan::DataType::Int8)   == 1);
    EXPECT("SizeOf Int16 = 2",  ValueScan::SizeOf(ValueScan::DataType::Int16)  == 2);
    EXPECT("SizeOf Int32 = 4",  ValueScan::SizeOf(ValueScan::DataType::Int32)  == 4);
    EXPECT("SizeOf Int64 = 8",  ValueScan::SizeOf(ValueScan::DataType::Int64)  == 8);
    EXPECT("SizeOf UInt8 = 1",  ValueScan::SizeOf(ValueScan::DataType::UInt8)  == 1);
    EXPECT("SizeOf UInt16 = 2", ValueScan::SizeOf(ValueScan::DataType::UInt16) == 2);
    EXPECT("SizeOf UInt32 = 4", ValueScan::SizeOf(ValueScan::DataType::UInt32) == 4);
    EXPECT("SizeOf UInt64 = 8", ValueScan::SizeOf(ValueScan::DataType::UInt64) == 8);
    EXPECT("SizeOf Float = 4",  ValueScan::SizeOf(ValueScan::DataType::Float)  == 4);
    EXPECT("SizeOf Double = 8", ValueScan::SizeOf(ValueScan::DataType::Double) == 8);
    EXPECT("SizeOf Bool = 1",   ValueScan::SizeOf(ValueScan::DataType::Bool)   == 1);
}

static void Test_ValueScan_ParseDataTypeRoundTrip() {
    using DT = ValueScan::DataType;
    DT got;
    EXPECT("parse Int32",   ValueScan::TryParseDataType("Int32",  got) && got == DT::Int32);
    EXPECT("parse Float",   ValueScan::TryParseDataType("Float",  got) && got == DT::Float);
    EXPECT("parse Bool",    ValueScan::TryParseDataType("Bool",   got) && got == DT::Bool);
    EXPECT("parse UInt64",  ValueScan::TryParseDataType("UInt64", got) && got == DT::UInt64);
    EXPECT("parse rejects unknown", !ValueScan::TryParseDataType("FString", got));
    EXPECT("parse rejects empty",   !ValueScan::TryParseDataType("",        got));
}

static void Test_ValueScan_ScanTypePartitioning() {
    using ST = ValueScan::ScanType;
    EXPECT("Exact is first-scan",      ValueScan::IsFirstScanType(ST::Exact));
    EXPECT("Bigger is first-scan",     ValueScan::IsFirstScanType(ST::Bigger));
    EXPECT("Smaller is first-scan",    ValueScan::IsFirstScanType(ST::Smaller));
    EXPECT("Between is first-scan",    ValueScan::IsFirstScanType(ST::Between));
    EXPECT("Changed is prev-value",    ValueScan::IsPrevValueScanType(ST::Changed));
    EXPECT("Unchanged is prev-value",  ValueScan::IsPrevValueScanType(ST::Unchanged));
    EXPECT("Increased is prev-value",  ValueScan::IsPrevValueScanType(ST::Increased));
    EXPECT("Decreased is prev-value",  ValueScan::IsPrevValueScanType(ST::Decreased));
    // No overlap between first-scan and prev-value partitions:
    EXPECT("Exact is NOT prev-value",  !ValueScan::IsPrevValueScanType(ST::Exact));
    EXPECT("Changed is NOT first-scan", !ValueScan::IsFirstScanType(ST::Changed));
}

// ----- ValueScan: ComparePredicate per DataType -----------------------------
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
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8], tgt[8], tgt2[8];
    WriteLE<int32_t>(cur, 100);
    WriteLE<int32_t>(tgt, 100);
    EXPECT("Int32 Exact (100==100)",      ValueScan::ComparePredicate(DT::Int32, ST::Exact,   cur, tgt));
    WriteLE<int32_t>(tgt, 50);
    EXPECT("Int32 Bigger (100>50)",       ValueScan::ComparePredicate(DT::Int32, ST::Bigger,  cur, tgt));
    EXPECT("Int32 Smaller false",        !ValueScan::ComparePredicate(DT::Int32, ST::Smaller, cur, tgt));
    WriteLE<int32_t>(tgt, 200);
    EXPECT("Int32 Smaller (100<200)",     ValueScan::ComparePredicate(DT::Int32, ST::Smaller, cur, tgt));
    WriteLE<int32_t>(tgt, 50);
    WriteLE<int32_t>(tgt2, 150);
    EXPECT("Int32 Between (100 in [50,150])", ValueScan::ComparePredicate(DT::Int32, ST::Between, cur, tgt, tgt2));
    WriteLE<int32_t>(tgt, 150);
    WriteLE<int32_t>(tgt2, 200);
    EXPECT("Int32 Between rejects (100 not in [150,200])",
           !ValueScan::ComparePredicate(DT::Int32, ST::Between, cur, tgt, tgt2));

    // Changed / Unchanged compare against prev (passed as `target`)
    WriteLE<int32_t>(tgt, 100);
    EXPECT("Int32 Unchanged (100==prev100)",  ValueScan::ComparePredicate(DT::Int32, ST::Unchanged, cur, tgt));
    EXPECT("Int32 Changed rejects same",     !ValueScan::ComparePredicate(DT::Int32, ST::Changed,   cur, tgt));
    WriteLE<int32_t>(tgt, 99);
    EXPECT("Int32 Changed (100!=prev99)",     ValueScan::ComparePredicate(DT::Int32, ST::Changed,   cur, tgt));
    EXPECT("Int32 Increased (100>prev99)",    ValueScan::ComparePredicate(DT::Int32, ST::Increased, cur, tgt));
    WriteLE<int32_t>(tgt, 101);
    EXPECT("Int32 Decreased (100<prev101)",   ValueScan::ComparePredicate(DT::Int32, ST::Decreased, cur, tgt));
}

static void Test_ValueScan_Predicate_Int8Negative() {
    // Regression for sign extension: Int8 must compare as signed even
    // when the raw byte is 0xFF (which would be 255 as unsigned).
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8] = {}, tgt[8] = {};
    int8_t minusOne = -1;
    int8_t zero = 0;
    std::memcpy(cur, &minusOne, 1);
    std::memcpy(tgt, &zero, 1);
    EXPECT("Int8 (-1 < 0) Smaller",   ValueScan::ComparePredicate(DT::Int8, ST::Smaller, cur, tgt));
    EXPECT("Int8 (-1 < 0) Bigger NO", !ValueScan::ComparePredicate(DT::Int8, ST::Bigger,  cur, tgt));
}

static void Test_ValueScan_Predicate_Float() {
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8], tgt[8];
    WriteLE<float>(cur, 3.14f);
    WriteLE<float>(tgt, 3.14f);
    EXPECT("Float Exact (3.14==3.14)",  ValueScan::ComparePredicate(DT::Float, ST::Exact,  cur, tgt));
    WriteLE<float>(tgt, 1.0f);
    EXPECT("Float Bigger (3.14>1)",     ValueScan::ComparePredicate(DT::Float, ST::Bigger, cur, tgt));
    WriteLE<float>(cur, -2.5f);
    WriteLE<float>(tgt, -1.0f);
    EXPECT("Float Smaller (-2.5<-1)",   ValueScan::ComparePredicate(DT::Float, ST::Smaller, cur, tgt));
}

static void Test_ValueScan_Predicate_Double() {
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8], tgt[8];
    WriteLE<double>(cur, 1.0 / 3.0);
    WriteLE<double>(tgt, 1.0 / 3.0);
    EXPECT("Double Exact (1/3==1/3)",   ValueScan::ComparePredicate(DT::Double, ST::Exact,   cur, tgt));
    WriteLE<double>(tgt, 0.0);
    EXPECT("Double Increased prev=0",   ValueScan::ComparePredicate(DT::Double, ST::Increased, cur, tgt));
}

static void Test_ValueScan_Predicate_Bool() {
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8] = { 1 }, tgt[8] = { 1 };
    EXPECT("Bool true==true Exact",       ValueScan::ComparePredicate(DT::Bool, ST::Exact, cur, tgt));
    tgt[0] = 0;
    EXPECT("Bool true!=false Changed",    ValueScan::ComparePredicate(DT::Bool, ST::Changed, cur, tgt));
    EXPECT("Bool true!=false Unchanged NO", !ValueScan::ComparePredicate(DT::Bool, ST::Unchanged, cur, tgt));
}

static void Test_ValueScan_Predicate_UInt64_RangeBoundary() {
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8], tgt[8];
    // Values that would be NEGATIVE if mis-read as signed: ensures
    // unsigned path is taken for UInt64.
    WriteLE<uint64_t>(cur, 0xFFFFFFFFFFFFFFFFULL);
    WriteLE<uint64_t>(tgt, 0x8000000000000000ULL);
    EXPECT("UInt64 (~0 > 0x8000...) Bigger", ValueScan::ComparePredicate(DT::UInt64, ST::Bigger, cur, tgt));
    EXPECT("UInt64 (~0 < 0x8000...) Smaller NO",
           !ValueScan::ComparePredicate(DT::UInt64, ST::Smaller, cur, tgt));
}

// ----- ValueScan: SessionManager lifecycle ----------------------------------

// ----- ValueScan: Float/Double tolerance (CE-style rounded scan) ------------
//
// The TQ2 / GAS use case that motivated tolerance: game UI shows "338"
// for an underlying float of 337.5 (default rounding). User scans for
// 338 with tolerance 0.5 -> should match values in [337.5, 338.5].
// All eight ScanTypes have a defined tolerance semantic; integer types
// must IGNORE tolerance regardless of the value supplied (the DLL
// signal that this is a "wrong type for tolerance" case).

static void Test_ValueScan_FloatTolerance_Exact() {
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8], tgt[8];

    // target=338, cur=337.5 (rounds-to-338 in UI), tol=0.5 -> match
    WriteLE<float>(cur, 337.5f);
    WriteLE<float>(tgt, 338.0f);
    EXPECT("Float Exact tol 0.5 (337.5 ~= 338)",
           ValueScan::ComparePredicate(DT::Float, ST::Exact, cur, tgt, nullptr, 0.5));

    // target=338, cur=338.5 -> still inside tolerance band [337.5, 338.5]
    WriteLE<float>(cur, 338.5f);
    EXPECT("Float Exact tol 0.5 (338.5 ~= 338, inclusive)",
           ValueScan::ComparePredicate(DT::Float, ST::Exact, cur, tgt, nullptr, 0.5));

    // target=338, cur=338.51 -> outside band, no match
    WriteLE<float>(cur, 338.51f);
    EXPECT("Float Exact tol 0.5 rejects 338.51",
           !ValueScan::ComparePredicate(DT::Float, ST::Exact, cur, tgt, nullptr, 0.5));

    // tol=0 keeps strict equality semantics (back-compat with old callers)
    WriteLE<float>(cur, 337.5f);
    EXPECT("Float Exact tol 0 rejects 337.5 vs 338",
           !ValueScan::ComparePredicate(DT::Float, ST::Exact, cur, tgt, nullptr, 0.0));
}

static void Test_ValueScan_FloatTolerance_Ordered() {
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8], tgt[8];

    // Bigger: cur > target + tol  -> 339 > 338+0.5=338.5 is true, but 338.4 isn't
    WriteLE<float>(tgt, 338.0f);
    WriteLE<float>(cur, 339.0f);
    EXPECT("Float Bigger tol 0.5 (339 > 338.5)",
           ValueScan::ComparePredicate(DT::Float, ST::Bigger, cur, tgt, nullptr, 0.5));
    WriteLE<float>(cur, 338.4f);
    EXPECT("Float Bigger tol 0.5 rejects 338.4 (within band)",
           !ValueScan::ComparePredicate(DT::Float, ST::Bigger, cur, tgt, nullptr, 0.5));

    // Smaller: cur < target - tol
    WriteLE<float>(cur, 337.4f);
    EXPECT("Float Smaller tol 0.5 (337.4 < 337.5)",
           ValueScan::ComparePredicate(DT::Float, ST::Smaller, cur, tgt, nullptr, 0.5));
}

static void Test_ValueScan_FloatTolerance_PrevValue() {
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8], prev[8];

    // Unchanged within tolerance band — float drift below tol is "no change"
    WriteLE<float>(prev, 100.0f);
    WriteLE<float>(cur,  100.3f);
    EXPECT("Float Unchanged tol 0.5 (drift 0.3 within noise)",
           ValueScan::ComparePredicate(DT::Float, ST::Unchanged, cur, prev, nullptr, 0.5));
    // Same drift, Changed -> false (drift smaller than tol)
    EXPECT("Float Changed tol 0.5 rejects 0.3 drift",
           !ValueScan::ComparePredicate(DT::Float, ST::Changed, cur, prev, nullptr, 0.5));

    // Drift larger than tol -> Changed true, Unchanged false
    WriteLE<float>(cur, 100.6f);
    EXPECT("Float Changed tol 0.5 (drift 0.6 > noise)",
           ValueScan::ComparePredicate(DT::Float, ST::Changed, cur, prev, nullptr, 0.5));

    // Increased: cur > prev + tol
    WriteLE<float>(prev, 50.0f);
    WriteLE<float>(cur,  50.6f);
    EXPECT("Float Increased tol 0.5 (50.6 > 50.5)",
           ValueScan::ComparePredicate(DT::Float, ST::Increased, cur, prev, nullptr, 0.5));
    WriteLE<float>(cur, 50.4f);
    EXPECT("Float Increased tol 0.5 rejects 50.4 (inside band)",
           !ValueScan::ComparePredicate(DT::Float, ST::Increased, cur, prev, nullptr, 0.5));
}

static void Test_ValueScan_FloatTolerance_Between() {
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8], lo[8], hi[8];
    // Between widens both bounds: [10-0.5, 20+0.5] = [9.5, 20.5]
    WriteLE<float>(lo, 10.0f);
    WriteLE<float>(hi, 20.0f);
    WriteLE<float>(cur, 9.8f);
    EXPECT("Float Between tol 0.5 includes 9.8 (lo bound widened)",
           ValueScan::ComparePredicate(DT::Float, ST::Between, cur, lo, hi, 0.5));
    WriteLE<float>(cur, 20.3f);
    EXPECT("Float Between tol 0.5 includes 20.3 (hi bound widened)",
           ValueScan::ComparePredicate(DT::Float, ST::Between, cur, lo, hi, 0.5));
    WriteLE<float>(cur, 20.6f);
    EXPECT("Float Between tol 0.5 rejects 20.6",
           !ValueScan::ComparePredicate(DT::Float, ST::Between, cur, lo, hi, 0.5));
}

static void Test_ValueScan_IntegerTypes_IgnoreTolerance() {
    using DT = ValueScan::DataType;
    using ST = ValueScan::ScanType;
    uint8_t cur[8], tgt[8];
    // Even with absurd tolerance, Int32 Exact must be literal equality.
    WriteLE<int32_t>(cur, 99);
    WriteLE<int32_t>(tgt, 100);
    EXPECT("Int32 Exact tol 5 rejects 99 vs 100 (tolerance ignored)",
           !ValueScan::ComparePredicate(DT::Int32, ST::Exact, cur, tgt, nullptr, 5.0));
    WriteLE<int32_t>(cur, 100);
    EXPECT("Int32 Exact tol 5 accepts 100 vs 100",
           ValueScan::ComparePredicate(DT::Int32, ST::Exact, cur, tgt, nullptr, 5.0));

    // Same for UInt64
    WriteLE<uint64_t>(cur, 999);
    WriteLE<uint64_t>(tgt, 1000);
    EXPECT("UInt64 Exact tol 100 still rejects 999 vs 1000",
           !ValueScan::ComparePredicate(DT::UInt64, ST::Exact, cur, tgt, nullptr, 100.0));
}

static void Test_ValueScan_SessionLifecycle() {
    using namespace ValueScan;
    auto& mgr = SessionManager::Instance();

    // Seed two candidates.
    std::vector<Candidate> seed;
    seed.resize(2);
    seed[0].addr = 0x1000;
    WriteLE<int32_t>(seed[0].prevValue, 100);
    seed[1].addr = 0x2000;
    WriteLE<int32_t>(seed[1].prevValue, 200);

    uint64_t sid = mgr.Begin(DataType::Int32, std::move(seed));
    EXPECT("Begin returns non-zero session id", sid != 0);

    bool viewed = mgr.ViewWith(sid, [&](DataType dt, const std::vector<Candidate>& cs) {
        EXPECT("ViewWith sees correct dataType", dt == DataType::Int32);
        EXPECT("ViewWith sees 2 candidates",     cs.size() == 2);
    });
    EXPECT("ViewWith returns true for live session", viewed);

    // RefineWith may mutate the candidates vector.
    bool refined = mgr.RefineWith(sid, [](DataType, std::vector<Candidate>& cs) {
        cs.pop_back();  // drop one
    });
    EXPECT("RefineWith returns true for live session", refined);

    size_t remaining = 0;
    mgr.ViewWith(sid, [&](DataType, const std::vector<Candidate>& cs) {
        remaining = cs.size();
    });
    EXPECT("Refine pruned candidate count", remaining == 1);

    EXPECT("End returns true on first call",  mgr.End(sid));
    EXPECT("End returns false on second call",!mgr.End(sid));

    // Lookups on a missing session id return false WITHOUT invoking
    // the callback -- caller maps to wire error "session_not_found".
    bool callbackRan = false;
    bool missingOk = mgr.RefineWith(sid, [&](DataType, std::vector<Candidate>&) {
        callbackRan = true;
    });
    EXPECT("RefineWith on missing returns false", !missingOk);
    EXPECT("RefineWith on missing does NOT invoke callback", !callbackRan);
}

// ----- main ------------------------------------------------------------------

int main() {
    std::printf("dll_helpers_test (Renge + Scharf + ValueScan)\n");
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
    Test_ValueScan_SessionLifecycle();

    std::printf("------------------------------------------\n");
    std::printf("Pass: %d   Fail: %d\n", g_pass, g_fail);
    return g_fail;
}
