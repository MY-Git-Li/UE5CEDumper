#pragma once

// ============================================================
// Utf8Helpers — UTF-8 string sanitization + UTF-16 → UTF-8 encoding
//
// Header-only so the unit test target can pick it up without depending on
// the rest of the DLL build (no MinHook, no nlohmann, no Win32 hooks).
//
// Two pure functions:
//
//   Sanitize(in) — walk a candidate UTF-8 byte sequence and replace any
//     malformed run with '?'. Defends against the historical
//     "invalid UTF-8 byte 0xA0" nlohmann::json failure: even when the
//     source path looks sound, ill-formed UTF-8 (CESU-8-style surrogate
//     encodings, overlongs, truncated sequences) can leak through and
//     trip nlohmann's strict validator.
//
//   EncodeUtf16(data, len) — convert a UTF-16 code-unit buffer to valid
//     UTF-8. Surrogate pairs are recognized and combined into a 4-byte
//     UTF-8 sequence (required for emoji and supplementary-plane chars).
//     Lone surrogates are replaced with '?'.
//
// Both are isolated logic with no dependencies beyond <string> + <cstdint>,
// so utf8_helpers_test.cpp can include them and run assert-based tests
// without linking into the full DLL.
// ============================================================

#include <string>
#include <cstdint>
#include <cstddef>

namespace Utf8Helpers {

inline std::string Sanitize(const std::string& in) {
    std::string out;
    out.reserve(in.size());
    size_t i = 0;
    while (i < in.size()) {
        unsigned char b0 = static_cast<unsigned char>(in[i]);
        if (b0 < 0x80) {
            // ASCII — pass through, but reject control bytes other than \t/\n/\r
            // because they can interact badly with downstream XML/JSON consumers.
            if (b0 < 0x20 && b0 != '\t' && b0 != '\n' && b0 != '\r') out += '?';
            else out += static_cast<char>(b0);
            ++i;
            continue;
        }
        // Multi-byte sequence: decode strictly, reject overlongs and surrogates.
        int extra = 0;
        uint32_t cp = 0;
        uint32_t minCp = 0;
        if ((b0 & 0xE0) == 0xC0) { extra = 1; cp = b0 & 0x1F; minCp = 0x80; }
        else if ((b0 & 0xF0) == 0xE0) { extra = 2; cp = b0 & 0x0F; minCp = 0x800; }
        else if ((b0 & 0xF8) == 0xF0) { extra = 3; cp = b0 & 0x07; minCp = 0x10000; }
        else { out += '?'; ++i; continue; }

        // Truncated sequence — fall back to per-byte rejection (we don't know
        // how many bytes belong to the bad sequence, so play it safe and let
        // the loop reject each byte individually).
        if (i + static_cast<size_t>(extra) >= in.size()) { out += '?'; ++i; continue; }

        bool ok = true;
        for (int k = 1; k <= extra; ++k) {
            unsigned char bk = static_cast<unsigned char>(in[i + k]);
            if ((bk & 0xC0) != 0x80) { ok = false; break; }
            cp = (cp << 6) | (bk & 0x3F);
        }
        if (!ok) {
            // Structurally malformed (a continuation byte was missing).
            // Reject only the lead byte; let the loop re-evaluate the next
            // byte (it might be a valid lead or another lone continuation).
            out += '?'; ++i; continue;
        }
        if (cp < minCp || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) {
            // Structurally well-formed but semantically invalid — we KNOW
            // the sequence length so skip past it as one malformed unit.
            // Produces one '?' per bad codepoint instead of one per byte,
            // keeping sanitized output compact.
            out += '?'; i += static_cast<size_t>(extra) + 1; continue;
        }
        for (int k = 0; k <= extra; ++k) out += in[i + k];
        i += static_cast<size_t>(extra) + 1;
    }
    return out;
}

// Heuristic guard against a junk / misidentified FName whose "wide" bit landed
// on an ANSI buffer. An asset path like "/Game/Foo/Bar" reinterpreted as
// UTF-16LE decodes to a long run of CJK mojibake — each ASCII byte-pair becomes
// one BMP code unit (e.g. bytes "/M" → U+4D2F 䴯). Genuine wide FNames are short
// localized display strings; UE asset/class/property FNames are ASCII. Return
// true when a wide decode is implausible so callers drop it instead of
// surfacing 亂碼 (DQ3 HD-2D: a value in raw heap resolved to a 435-char mojibake
// class name through a backward-scan false positive).
//
// Operates on the raw UTF-16 code units (NUL-terminated within `len`). The
// signal is length + non-ASCII density: a real wide FName is never long, and a
// long mostly-non-ASCII wide name is the mojibake fingerprint. Trade-off: a
// legitimately long all-CJK FName (rare to nonexistent in UE) would also be
// dropped — acceptable for this dumper's domain.
inline bool IsImplausibleWideName(const wchar_t* data, size_t len) {
    if (!data) return false;
    size_t total = 0, nonAscii = 0;
    for (size_t i = 0; i < len; ++i) {
        uint16_t u = static_cast<uint16_t>(data[i]);
        if (u == 0) break;          // NUL-terminated
        ++total;
        if (u >= 0x80) ++nonAscii;
    }
    if (total == 0) return false;
    // No real wide FName approaches this length — long wide = mojibake.
    if (total > 64) return true;
    // Medium length dominated by non-ASCII (>=75%) is the mojibake signature,
    // while still preserving realistic short localized names (< 24 chars).
    if (total > 24 && nonAscii * 4 >= total * 3) return true;
    return false;
}

// Decode a narrow (ANSI) UE FName payload into a clean string, or "" when it is junk.
// A real narrow FName is pure printable ASCII by construction — UE stores localized /
// non-ASCII text with the wide bit set (handled by EncodeUtf16 + IsImplausibleWideName).
// So ANY byte outside [0x20,0x7F), other than a NUL terminator, means the bytes are NOT
// a valid narrow FName: the entry is corrupt, or an invalid / uninitialized FName index
// landed mid-entry inside a packed FNamePool. (In a packed pool, indices 1 and 2 fall
// inside the 6-byte 'None' entry, so an uninitialized 4-byte NameProperty value such as
// ComparisonIndex=2 decodes the tail of 'None' plus adjacent packed-entry header bytes.)
// Returning "" lets callers treat it as a miss and render 'None' instead of surfacing a
// misleading concatenation like "??ByteProperty??IntProperty" (the sanitized header
// bytes between packed entries + the engine-intrinsic name tokens they sit next to).
//
// This is the ANSI sibling of IsImplausibleWideName, but stricter: a narrow FName has NO
// legitimate non-printable bytes, so the first one is decisive (no length/density
// threshold — that under-fires on a junk run dominated by printable name tokens). A
// legitimate literal '?' (0x3F) is printable and is preserved.
inline std::string SanitizeAnsiName(const char* data, size_t maxLen) {
    if (!data) return "";
    std::string out;
    out.reserve(maxLen < 64 ? maxLen : 64);
    for (size_t i = 0; i < maxLen; ++i) {
        unsigned char c = static_cast<unsigned char>(data[i]);
        if (c == 0) break;                      // NUL terminator
        if (c < 0x20 || c >= 0x7F) return "";   // non-ASCII => corrupt / misidentified entry
        out += static_cast<char>(c);
    }
    return out;
}

inline std::string EncodeUtf16(const wchar_t* data, size_t len) {
    std::string result;
    if (!data || len == 0) return result;
    result.reserve(len * 3);
    for (size_t i = 0; i < len; ++i) {
        uint32_t ch = static_cast<uint32_t>(static_cast<uint16_t>(data[i]));
        if (ch == 0) break;

        // Surrogate pair (high then low) → combine to 4-byte UTF-8.
        if (ch >= 0xD800 && ch <= 0xDBFF && i + 1 < len) {
            uint32_t low = static_cast<uint32_t>(static_cast<uint16_t>(data[i + 1]));
            if (low >= 0xDC00 && low <= 0xDFFF) {
                uint32_t cp = 0x10000u + ((ch - 0xD800u) << 10) + (low - 0xDC00u);
                result += static_cast<char>(0xF0 | (cp >> 18));
                result += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
                result += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
                result += static_cast<char>(0x80 | (cp & 0x3F));
                ++i;
                continue;
            }
        }

        // Lone surrogate (high without low, low without high) → '?'.
        if (ch >= 0xD800 && ch <= 0xDFFF) {
            result += '?';
            continue;
        }

        if (ch < 0x80) {
            result += static_cast<char>(ch);
        } else if (ch < 0x800) {
            result += static_cast<char>(0xC0 | (ch >> 6));
            result += static_cast<char>(0x80 | (ch & 0x3F));
        } else {
            // BMP character (0x800..0xFFFF, surrogate range already filtered).
            result += static_cast<char>(0xE0 | (ch >> 12));
            result += static_cast<char>(0x80 | ((ch >> 6) & 0x3F));
            result += static_cast<char>(0x80 | (ch & 0x3F));
        }
    }
    return result;
}

// Cheap "did this decode to real text?" guard for DecodeFStringBuffer. A buffer
// decoded at the WRONG element width is dominated by Sanitize's '?' replacement
// marker; genuine text is not. Counts decoded CHARACTERS (skipping UTF-8
// continuation bytes so a multi-byte CJK glyph counts once) and rejects when
// more than a third of them are replacements. A handful of legitimate literal
// '?' in a real sentence still passes.
inline bool LooksLikeDecodedText(const std::string& s) {
    if (s.empty()) return false;
    size_t total = 0, bad = 0;
    for (unsigned char c : s) {
        if ((c & 0xC0) == 0x80) continue;   // UTF-8 continuation — same char
        ++total;
        if (c == '?') ++bad;
    }
    if (total == 0) return false;
    return bad * 3 < total;
}

// ============================================================
// DecodeFStringBuffer — decode a raw FString / FUtf8String buffer whose
// element WIDTH is unknown, choosing UTF-8 (1-byte: FUtf8String, or a game
// that stores its FText display string as UTF-8) vs UTF-16 (2-byte TCHAR).
//
//   buf       raw bytes read from the string's Data pointer
//   bufLen    how many bytes were actually read (ideally >= numUnits*2 so the
//             UTF-16 hypothesis can be tested; >= numUnits is enough for UTF-8)
//   numUnits  the FString header's Num — a code-UNIT count INCLUDING the
//             trailing null (Len = Num - 1)
//
// Width is decided by where the null terminator sits, gated by an interior-null
// check:
//   * UTF-8  : byte[numUnits-1] == 0 AND no 0x00 in [0, numUnits-1). The
//              interior-null gate is what separates a genuine 1-byte buffer
//              from a UTF-16 ASCII buffer, whose high bytes are 0x00.
//   * UTF-16 : the unit at index numUnits-1 is 0x0000 (bytes at 2*(N-1)).
// UTF-8 is tried first because its gate is stricter; a real UTF-16 string
// (CJK high bytes non-zero, or ASCII with interspersed 0x00 interior nulls)
// never satisfies it. This is what lets the FText reader recover UTF-8 display
// strings that a blind UTF-16 decode turns into 亂碼.
//
// Returns sanitized UTF-8, or "" if neither hypothesis yields a
// null-terminated, mostly-textual string.
// ============================================================
inline std::string DecodeFStringBuffer(const uint8_t* buf, size_t bufLen, int32_t numUnits) {
    if (!buf || numUnits < 2) return "";   // < 2 units = empty (just the null)
    const size_t n = static_cast<size_t>(numUnits);

    // --- UTF-8 hypothesis: n bytes, terminator at [n-1], no interior null ---
    if (bufLen >= n && buf[n - 1] == 0) {
        bool interiorNull = false;
        for (size_t i = 0; i + 1 < n; ++i) {
            if (buf[i] == 0) { interiorNull = true; break; }
        }
        if (!interiorNull) {
            std::string decoded =
                Sanitize(std::string(reinterpret_cast<const char*>(buf), n - 1));
            if (LooksLikeDecodedText(decoded)) return decoded;
        }
    }

    // --- UTF-16 hypothesis: n wchar_t units, terminating unit at [n-1] ---
    if (bufLen >= n * 2) {
        const size_t termByte = (n - 1) * 2;
        if (buf[termByte] == 0 && buf[termByte + 1] == 0) {
            // Recombine little-endian byte pairs into UTF-16 units without
            // assuming buf is 2-byte aligned.
            std::wstring w;
            w.reserve(n);
            for (size_t i = 0; i < n; ++i) {
                uint16_t u = static_cast<uint16_t>(buf[i * 2]) |
                             (static_cast<uint16_t>(buf[i * 2 + 1]) << 8);
                w.push_back(static_cast<wchar_t>(u));
            }
            std::string decoded = Sanitize(EncodeUtf16(w.data(), w.size()));
            if (LooksLikeDecodedText(decoded)) return decoded;
        }
    }

    return "";
}

} // namespace Utf8Helpers
