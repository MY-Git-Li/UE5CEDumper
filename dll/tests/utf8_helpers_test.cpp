// ============================================================
// Utf8Helpers self-test
//
// Stand-alone executable (no GoogleTest / Catch2 dependency) that exercises
// Utf8Helpers::Sanitize and Utf8Helpers::EncodeUtf16 with crafted inputs.
// Exit code is the count of failures — 0 = all pass.
//
// Built as a separate CMake target (utf8_helpers_test). build.ps1 -Target Test
// runs it before C# tests; any failure short-circuits the test phase.
// ============================================================

#include "../src/Utf8Helpers.h"

#include <cstdio>
#include <cstdint>
#include <string>

static int g_pass = 0;
static int g_fail = 0;

#define EXPECT(label, cond) do { \
    if (cond) { ++g_pass; } \
    else { ++g_fail; std::printf("  FAIL: %s\n    at %s:%d\n", label, __FILE__, __LINE__); } \
} while (0)

#define EXPECT_EQ_STR(label, actual, expected) do { \
    std::string _a = (actual); \
    std::string _e = (expected); \
    if (_a == _e) { ++g_pass; } \
    else { \
        ++g_fail; \
        std::printf("  FAIL: %s\n    actual=", label); \
        for (char c : _a) std::printf("\\x%02X", static_cast<unsigned char>(c)); \
        std::printf("\n    expected="); \
        for (char c : _e) std::printf("\\x%02X", static_cast<unsigned char>(c)); \
        std::printf("\n    at %s:%d\n", __FILE__, __LINE__); \
    } \
} while (0)

// ----- Sanitize tests --------------------------------------------------------

static void Test_Sanitize_AsciiPassthrough() {
    EXPECT_EQ_STR("ascii passthrough",
                  Utf8Helpers::Sanitize("hello world"), "hello world");
    EXPECT_EQ_STR("empty string",
                  Utf8Helpers::Sanitize(""), "");
    EXPECT_EQ_STR("preserved tab/newline/cr",
                  Utf8Helpers::Sanitize("a\tb\nc\rd"), "a\tb\nc\rd");
}

static void Test_Sanitize_RejectsControlBytes() {
    // \x01..\x1F (except \t \n \r) → '?'
    EXPECT_EQ_STR("control byte 0x01",
                  Utf8Helpers::Sanitize(std::string("a\x01" "b")), "a?b");
    EXPECT_EQ_STR("control byte 0x07",
                  Utf8Helpers::Sanitize(std::string("a\x07" "b")), "a?b");
}

static void Test_Sanitize_ValidMultiBytePassthrough() {
    // U+00A0 NO-BREAK SPACE (correctly encoded as 0xC2 0xA0)
    EXPECT_EQ_STR("U+00A0 NBSP correct",
                  Utf8Helpers::Sanitize("\xC2\xA0"), "\xC2\xA0");
    // U+4E2D 中 (3-byte CJK)
    EXPECT_EQ_STR("U+4E2D CJK 中",
                  Utf8Helpers::Sanitize("\xE4\xB8\xAD"), "\xE4\xB8\xAD");
    // U+1F600 😀 (4-byte emoji, valid)
    EXPECT_EQ_STR("U+1F600 emoji 😀",
                  Utf8Helpers::Sanitize("\xF0\x9F\x98\x80"), "\xF0\x9F\x98\x80");
}

static void Test_Sanitize_RejectsLoneContinuation() {
    // Lone 0xA0 (no leader) — this is THE Squad bug 0xA0 case
    EXPECT_EQ_STR("lone 0xA0 → ?",
                  Utf8Helpers::Sanitize(std::string("\xA0", 1)), "?");
    // Multiple lone continuations
    EXPECT_EQ_STR("multiple lone continuations",
                  Utf8Helpers::Sanitize(std::string("\xA0\xA0\x80", 3)), "???");
}

static void Test_Sanitize_RejectsSurrogateEncodings() {
    // CESU-8: U+D800 encoded as 0xED 0xA0 0x80 — what the broken Serie.cpp wide
    // path used to produce on lone surrogates. nlohmann::json type_error.316.
    EXPECT_EQ_STR("CESU-8 high surrogate",
                  Utf8Helpers::Sanitize("\xED\xA0\x80"), "?");
    // U+DFFF → 0xED 0xBF 0xBF
    EXPECT_EQ_STR("CESU-8 low surrogate",
                  Utf8Helpers::Sanitize("\xED\xBF\xBF"), "?");
}

static void Test_Sanitize_RejectsOverlongs() {
    // U+0041 'A' overlong-encoded as 0xC1 0x81 (should be 1 byte 0x41)
    EXPECT_EQ_STR("overlong U+0041",
                  Utf8Helpers::Sanitize("\xC1\x81"), "?");
    // NUL overlong-encoded as 0xC0 0x80 (the "Modified UTF-8" pattern Java uses)
    EXPECT_EQ_STR("overlong NUL",
                  Utf8Helpers::Sanitize(std::string("\xC0\x80", 2)), "?");
}

static void Test_Sanitize_RejectsTruncated() {
    // 2-byte sequence missing continuation
    EXPECT_EQ_STR("truncated 2-byte",
                  Utf8Helpers::Sanitize("\xC2"), "?");
    // 3-byte sequence with only 1 continuation
    EXPECT_EQ_STR("truncated 3-byte",
                  Utf8Helpers::Sanitize("\xE4\xB8"), "??");
    // 4-byte sequence cut off
    EXPECT_EQ_STR("truncated 4-byte",
                  Utf8Helpers::Sanitize("\xF0\x9F\x98"), "???");
}

static void Test_Sanitize_MixedGoodAndBad() {
    // The Squad-style scenario: a real name with one bad lone surrogate inside
    EXPECT_EQ_STR("mixed valid+bad",
                  Utf8Helpers::Sanitize("hi\xED\xA0\x80world"),
                  "hi?world");
}

static void Test_Sanitize_Idempotent() {
    // Sanitize(Sanitize(x)) == Sanitize(x) for any x
    std::string input = "abc\xED\xA0\x80\xF0\x9F\x98\x80\xA0";
    std::string once = Utf8Helpers::Sanitize(input);
    std::string twice = Utf8Helpers::Sanitize(once);
    EXPECT("sanitize is idempotent", once == twice);
}

// ----- EncodeUtf16 tests -----------------------------------------------------

static void Test_EncodeUtf16_AsciiPassthrough() {
    wchar_t hello[] = { 'h', 'e', 'l', 'l', 'o' };
    EXPECT_EQ_STR("ascii encode",
                  Utf8Helpers::EncodeUtf16(hello, 5), "hello");
}

static void Test_EncodeUtf16_NullTerminationStops() {
    wchar_t buf[] = { 'a', 'b', 0, 'c', 'd' };
    EXPECT_EQ_STR("stops at NUL",
                  Utf8Helpers::EncodeUtf16(buf, 5), "ab");
}

static void Test_EncodeUtf16_BMPCharacters() {
    wchar_t cjk[] = { 0x4E2D, 0x6587 };  // 中文
    EXPECT_EQ_STR("CJK 中文",
                  Utf8Helpers::EncodeUtf16(cjk, 2),
                  "\xE4\xB8\xAD\xE6\x96\x87");
    wchar_t latin[] = { 0x00E9 };  // é
    EXPECT_EQ_STR("Latin é",
                  Utf8Helpers::EncodeUtf16(latin, 1), "\xC3\xA9");
}

static void Test_EncodeUtf16_SurrogatePair() {
    // U+1F600 😀 GRINNING FACE = 0xD83D 0xDE00 (high surrogate + low surrogate)
    // Should produce 4-byte UTF-8: 0xF0 0x9F 0x98 0x80
    wchar_t emoji[] = { 0xD83D, 0xDE00 };
    EXPECT_EQ_STR("emoji surrogate pair → 4-byte UTF-8",
                  Utf8Helpers::EncodeUtf16(emoji, 2),
                  "\xF0\x9F\x98\x80");
}

static void Test_EncodeUtf16_MultipleSurrogatePairs() {
    // 😀😀
    wchar_t emojis[] = { 0xD83D, 0xDE00, 0xD83D, 0xDE00 };
    EXPECT_EQ_STR("two emoji",
                  Utf8Helpers::EncodeUtf16(emojis, 4),
                  "\xF0\x9F\x98\x80\xF0\x9F\x98\x80");
}

static void Test_EncodeUtf16_LoneSurrogates() {
    // The Squad 0xA0 bug: lone high surrogate, no low partner.
    // Old code emitted 0xED 0xA0 0x80 (CESU-8). New code emits '?'.
    wchar_t loneHigh[] = { 0xD800 };
    EXPECT_EQ_STR("lone high surrogate → ?",
                  Utf8Helpers::EncodeUtf16(loneHigh, 1), "?");

    // Lone low surrogate (no high before it)
    wchar_t loneLow[] = { 0xDC00 };
    EXPECT_EQ_STR("lone low surrogate → ?",
                  Utf8Helpers::EncodeUtf16(loneLow, 1), "?");

    // High surrogate followed by ASCII (not a valid pair)
    wchar_t badPair[] = { 0xD800, 'A' };
    EXPECT_EQ_STR("high surrogate + ASCII → ? + A",
                  Utf8Helpers::EncodeUtf16(badPair, 2), "?A");
}

static void Test_EncodeUtf16_ReversedSurrogates() {
    // Low followed by high (out-of-order) — both lone
    wchar_t reversed[] = { 0xDC00, 0xD800 };
    EXPECT_EQ_STR("reversed surrogates → ??",
                  Utf8Helpers::EncodeUtf16(reversed, 2), "??");
}

static void Test_EncodeUtf16_MixedRealistic() {
    // Realistic mixed content: ASCII + CJK + surrogate pair + lone surrogate
    wchar_t mix[] = { 'h', 'i', 0x4E2D, 0xD83D, 0xDE00, 0xD800, '!' };
    EXPECT_EQ_STR("mixed realistic",
                  Utf8Helpers::EncodeUtf16(mix, 7),
                  "hi\xE4\xB8\xAD\xF0\x9F\x98\x80?!");
}

static void Test_EncodeUtf16_OutputAlwaysValidUtf8() {
    // Any output from EncodeUtf16 must round-trip cleanly through Sanitize.
    // This is the invariant nlohmann::json relies on.
    wchar_t pathological[] = {
        'A', 0xD800, 0xDE00,        // ASCII + lone high + lone low (mixed)
        0xD83D, 0xDE00,             // valid emoji pair
        0x4E2D,                     // CJK
        0xDC00,                     // lone low
        0,                          // NUL stops
    };
    std::string encoded = Utf8Helpers::EncodeUtf16(pathological, 8);
    std::string sanitized = Utf8Helpers::Sanitize(encoded);
    EXPECT("EncodeUtf16 output passes Sanitize unchanged", encoded == sanitized);
}

// ----- IsImplausibleWideName tests -------------------------------------------

// Build the UTF-16 code units that result from reading an ASCII byte sequence
// as UTF-16LE — exactly the mojibake an ANSI buffer produces under a stray wide
// flag (e.g. "/M" → 0x4D2F 䴯). Mirrors the DQ3 HD-2D backward-scan junk.
static std::wstring AsciiAsUtf16LE(const std::string& ascii) {
    std::wstring out;
    for (size_t i = 0; i + 1 < ascii.size(); i += 2) {
        uint16_t u = static_cast<uint16_t>(
            (static_cast<unsigned char>(ascii[i + 1]) << 8) |
             static_cast<unsigned char>(ascii[i]));
        out.push_back(static_cast<wchar_t>(u));
    }
    return out;
}

static void Test_ImplausibleWide_RejectsAsciiAsUtf16Mojibake() {
    // A real asset path reinterpreted as UTF-16LE — the actual failure mode.
    std::string path =
        "/Game/NiagaraCore/Materials/IMP_L_C01_Church_05_Inst.IMP_L_C01_Church";
    std::wstring moji = AsciiAsUtf16LE(path);
    EXPECT("long ASCII-as-UTF16 path is implausible",
           Utf8Helpers::IsImplausibleWideName(moji.data(), moji.size()));
}

static void Test_ImplausibleWide_AcceptsShortLocalized() {
    // Genuine short localized names must survive (no false drops).
    wchar_t jp[] = { 0x30EC, 0x30D9, 0x30EB };       // レベル
    EXPECT("short katakana name is plausible",
           !Utf8Helpers::IsImplausibleWideName(jp, 3));
    wchar_t mixed[] = { 'H', 'P', 0x4F53, 0x529B };  // HP体力
    EXPECT("short mixed ASCII+CJK is plausible",
           !Utf8Helpers::IsImplausibleWideName(mixed, 4));
}

static void Test_ImplausibleWide_AcceptsAsciiAndEmpty() {
    wchar_t ascii[] = { 'P', 'l', 'a', 'y', 'e', 'r', 'P', 'a', 'w', 'n' };
    EXPECT("ascii wide name is plausible",
           !Utf8Helpers::IsImplausibleWideName(ascii, 10));
    EXPECT("zero length is plausible (nothing to reject)",
           !Utf8Helpers::IsImplausibleWideName(ascii, 0));
    EXPECT("null data is plausible",
           !Utf8Helpers::IsImplausibleWideName(nullptr, 5));
}

static void Test_ImplausibleWide_LengthAndDensityRules() {
    // > 64 wide chars of anything → implausible (length rule); real wide FNames
    // are short localized strings, never this long.
    std::wstring longAscii(80, L'A');
    EXPECT("80 wide chars exceed the length cap",
           Utf8Helpers::IsImplausibleWideName(longAscii.data(), longAscii.size()));
    // 30 all-CJK chars → implausible (density rule: >24 chars and >=75% non-ASCII).
    std::wstring midCjk(30, static_cast<wchar_t>(0x4E2D));
    EXPECT("30 CJK chars trip the density rule",
           Utf8Helpers::IsImplausibleWideName(midCjk.data(), midCjk.size()));
    // 20 all-CJK chars → still plausible (under the 24-char density gate).
    std::wstring shortCjk(20, static_cast<wchar_t>(0x4E2D));
    EXPECT("20 CJK chars stay plausible",
           !Utf8Helpers::IsImplausibleWideName(shortCjk.data(), shortCjk.size()));
    // NUL terminates the count: long buffer, NUL at index 3 → plausible.
    std::wstring nulCut(80, static_cast<wchar_t>(0x4E2D));
    nulCut[3] = 0;
    EXPECT("NUL terminator caps the counted length",
           !Utf8Helpers::IsImplausibleWideName(nulCut.data(), nulCut.size()));
}

// ----- main ------------------------------------------------------------------

int main() {
    std::printf("Utf8Helpers self-test\n");
    std::printf("---------------------\n");

    Test_Sanitize_AsciiPassthrough();
    Test_Sanitize_RejectsControlBytes();
    Test_Sanitize_ValidMultiBytePassthrough();
    Test_Sanitize_RejectsLoneContinuation();
    Test_Sanitize_RejectsSurrogateEncodings();
    Test_Sanitize_RejectsOverlongs();
    Test_Sanitize_RejectsTruncated();
    Test_Sanitize_MixedGoodAndBad();
    Test_Sanitize_Idempotent();

    Test_EncodeUtf16_AsciiPassthrough();
    Test_EncodeUtf16_NullTerminationStops();
    Test_EncodeUtf16_BMPCharacters();
    Test_EncodeUtf16_SurrogatePair();
    Test_EncodeUtf16_MultipleSurrogatePairs();
    Test_EncodeUtf16_LoneSurrogates();
    Test_EncodeUtf16_ReversedSurrogates();
    Test_EncodeUtf16_MixedRealistic();
    Test_EncodeUtf16_OutputAlwaysValidUtf8();

    Test_ImplausibleWide_RejectsAsciiAsUtf16Mojibake();
    Test_ImplausibleWide_AcceptsShortLocalized();
    Test_ImplausibleWide_AcceptsAsciiAndEmpty();
    Test_ImplausibleWide_LengthAndDensityRules();

    std::printf("---------------------\n");
    std::printf("Pass: %d   Fail: %d\n", g_pass, g_fail);
    return g_fail;
}
