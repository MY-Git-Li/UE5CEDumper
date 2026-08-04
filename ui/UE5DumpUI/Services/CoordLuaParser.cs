using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Reads a coordinate block back out of a previously generated AA script (R7 — the
/// user pastes the WHOLE script and we parse only our own fenced region).
///
/// Mechanism copied from AOBMaker's <c>TryParseConfig</c> — brace-balanced scan plus
/// per-key anchored regex — because that is what makes the format tolerant of the
/// edits a human actually makes: reordered fields, a missing or extra comma, an
/// entry split over several lines, an inserted comment, and unknown keys from a
/// newer build. A line-oriented regex would break on every one of those.
///
/// Four defects of that parser are deliberately NOT inherited (each confirmed by
/// running AOBMaker's own shipped assembly):
///  1. Version baked into the marker literal, so a v2 block is indistinguishable
///     from "no block at all" and the UI reports "no markers found". Here the
///     version is CAPTURED and an unsupported one gets its own message.
///  2. Markers matched anywhere via IndexOf, including inside a string literal.
///     Here they must start a line.
///  3. 100% silent failure — no throw, no diagnostic, ever. Here every rejection is
///     reported with a line number.
///  4. Only double-quoted strings recognised, single-quoted ones silently ignored so
///     the field falls back to a default. Here both quote styles are accepted, and an
///     unparseable value is a reported error rather than a silent default.
/// </summary>
public static class CoordLuaParser
{
    /// <summary>Newest format version this build understands.</summary>
    public const int SupportedVersion = CoordLibraryScriptGenerator.FormatVersion;

    // Line-anchored so a marker inside a string literal or a nested comment cannot
    // be mistaken for the real fence. Version captured, not baked into the literal.
    private static readonly Regex s_startFence = new(
        @"^[ \t]*--[ \t]*@UE5CD:COORDS[ \t]+v(\d+)[ \t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex s_endFence = new(
        @"^[ \t]*--[ \t]*@UE5CD:END[ \t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex s_coordsTable = new(
        @"local[ \t]+COORDS[ \t]*=[ \t]*\{",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse a pasted script. Returns the same shape the CSV importer produces, so
    /// both feed one preview/commit path.
    /// </summary>
    public static CoordCsvParseResult Parse(string? script)
    {
        var result = new CoordCsvParseResult();
        if (string.IsNullOrWhiteSpace(script))
        {
            result.Issues.Add(new CoordCsvIssue(0, "", "", "nothing was pasted"));
            return result;
        }

        var text = script!;

        // A pasted CE .CT clipboard fragment carries the script XML-ESCAPED. Our own
        // exporter escapes FIVE entities (CheatTableBuilder.EscapeXml) while
        // CeXmlExportService.ExtractAssemblerScript only reverses three, so decode all
        // five here — and only when the paste really is the XML form, so a label that
        // legitimately contains the text "&lt;" in a raw script is left alone.
        if (LooksLikeCheatTableXml(text)) text = DecodeXmlEntities(text);

        var start = s_startFence.Match(text);
        if (!start.Success)
        {
            result.Issues.Add(new CoordCsvIssue(0, "", "",
                "no '-- @UE5CD:COORDS v1' block found. Paste the whole generated script, " +
                "including the comment lines around the coordinate list."));
            return result;
        }

        if (!int.TryParse(start.Groups[1].Value, out int version))
        {
            result.Issues.Add(new CoordCsvIssue(LineOf(text, start.Index), "", start.Value,
                "the block's version is not a number"));
            return result;
        }
        if (version > SupportedVersion)
        {
            // Distinguishable from "no block" — the AOBMaker bug this avoids.
            result.Issues.Add(new CoordCsvIssue(LineOf(text, start.Index), "", start.Value,
                $"this block was written by a newer UE5CEDumper (format v{version}); " +
                $"this build understands v{SupportedVersion}"));
            return result;
        }

        var end = s_endFence.Match(text, start.Index + start.Length);
        if (!end.Success)
        {
            result.Issues.Add(new CoordCsvIssue(LineOf(text, start.Index), "", "",
                "the block is not closed — '-- @UE5CD:END' is missing. Was the paste truncated?"));
            return result;
        }

        var block = text[(start.Index + start.Length)..end.Index];
        int blockOffset = start.Index + start.Length;

        // Optional: absent in blocks written before the field existed, which is why a
        // missing value leaves the user's current setting alone rather than zeroing it.
        var zt = Regex.Match(block,
            @"(?<![A-Za-z0-9_])Z_TOLERANCE[ 	]*=[ 	]*(-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)",
            RegexOptions.CultureInvariant);
        if (zt.Success && CoordPrecision.TryParse(zt.Groups[1].Value, out var ztv))
            result.ZTolerance = CoordPrecision.Round(ztv);

        var table = s_coordsTable.Match(block);
        if (!table.Success)
        {
            result.Issues.Add(new CoordCsvIssue(LineOf(text, blockOffset), "", "",
                "the block has no 'local COORDS = {' table"));
            return result;
        }

        int open = block.IndexOf('{', table.Index);
        var inner = ExtractBalanced(block, open);
        if (inner == null)
        {
            result.Issues.Add(new CoordCsvIssue(LineOf(text, blockOffset + table.Index), "", "",
                "the COORDS table's braces are unbalanced"));
            return result;
        }

        int innerOffset = blockOffset + open + 1;
        foreach (var (entryText, entryOffset) in TopLevelBraceGroups(inner))
        {
            int line = LineOf(text, innerOffset + entryOffset);
            var entry = ParseEntry(entryText, line, result.Issues);
            if (entry != null) result.Entries.Add(entry);
        }

        if (result.Entries.Count == 0 && result.Issues.Count == 0)
            result.Issues.Add(new CoordCsvIssue(0, "", "", "the coordinate list is empty"));

        return result;
    }

    private static CoordEntry? ParseEntry(string entry, int line, List<CoordCsvIssue> issues)
    {
        var e = new CoordEntry
        {
            Uid = CoordText.Normalize(Str(entry, "uid"), CoordText.MaxUidLength),   // (B7)
        };

        var rawLabel = Str(entry, "label");
        if (rawLabel == null)
        {
            issues.Add(new CoordCsvIssue(line, "label", Trim(entry), "no label field"));
            return null;
        }
        if (CoordText.HasBlockedChars(rawLabel))
            issues.Add(new CoordCsvIssue(line, "label", rawLabel, "control characters removed"));
        e.Label = CoordText.Normalize(rawLabel, CoordText.MaxLabelLength);
        if (e.Label.Length == 0)
        {
            issues.Add(new CoordCsvIssue(line, "label", rawLabel, "label is empty"));
            return null;
        }

        e.Group = CoordText.Normalize(Str(entry, "group") ?? "", CoordText.MaxGroupLength);
        e.Map = CoordText.Normalize(Str(entry, "map") ?? "", 256);

        bool bad = false;
        double N(string key)
        {
            var raw = RawNum(entry, key);
            if (raw != null && CoordPrecision.TryParse(raw, out var v))
                return CoordPrecision.Round(v);

            // Absent is fine (rotation fields are optional and default to 0). But a
            // key that IS present with a value the number regex could not match --
            // `x=oops`, a stray unit suffix, a comma decimal separator -- must be
            // reported, not silently imported as 0. That distinction is the whole
            // difference between a diagnostic and a quietly wrong coordinate.
            if (!HasKey(entry, key)) return 0;

            issues.Add(new CoordCsvIssue(line, key, ValueTextAfter(entry, key), "not a number"));
            bad = true;
            return 0;
        }

        e.X = N("x"); e.Y = N("y"); e.Z = N("z");
        e.Pitch = N("pitch"); e.Yaw = N("yaw"); e.Roll = N("roll");
        return bad ? null : e;
    }

    // ── Field extraction ────────────────────────────────────────────────

    /// <summary>
    /// Read a string field. Accepts BOTH quote styles — AOBMaker's parser silently
    /// ignores single-quoted values and falls back to a default, which is exactly the
    /// kind of quiet wrong answer this format is meant to avoid. The identifier
    /// lookbehind stops <c>xlabel=</c> shadowing <c>label=</c>.
    /// </summary>
    private static string? Str(string block, string key)
    {
        var dq = Regex.Match(block,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(key)}[ \t]*=[ \t]*""((?:[^""\\]|\\.)*)""",
            RegexOptions.CultureInvariant);
        if (dq.Success) return Unescape(dq.Groups[1].Value);

        var sq = Regex.Match(block,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(key)}[ \t]*=[ \t]*'((?:[^'\\]|\\.)*)'",
            RegexOptions.CultureInvariant);
        return sq.Success ? Unescape(sq.Groups[1].Value) : null;
    }

    /// <summary>True when the key appears at all, whatever follows it.</summary>
    private static bool HasKey(string block, string key) =>
        Regex.IsMatch(block, $@"(?<![A-Za-z0-9_]){Regex.Escape(key)}[ \t]*=",
                      RegexOptions.CultureInvariant);

    /// <summary>The raw text a key was assigned, up to the next comma or brace — used
    /// only to quote the offending value back to the user in a diagnostic.</summary>
    private static string ValueTextAfter(string block, string key)
    {
        var m = Regex.Match(block, $@"(?<![A-Za-z0-9_]){Regex.Escape(key)}[ \t]*=[ \t]*([^,}}]*)",
                            RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    private static string? RawNum(string block, string key)
    {
        var m = Regex.Match(block,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(key)}[ \t]*=[ \t]*(-?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)",
            RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Reverse <see cref="CeLuaHygiene.EscapeLuaString"/>, including the
    /// decimal escapes it uses to break closing long brackets.</summary>
    internal static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            if (i + 1 >= s.Length) { sb.Append('\\'); break; }
            char n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                default:
                    if (char.IsDigit(n))
                    {
                        // \ddd decimal escape, up to three digits.
                        int v = n - '0', digits = 1;
                        while (digits < 3 && i + 1 < s.Length && char.IsDigit(s[i + 1]))
                        {
                            v = v * 10 + (s[++i] - '0');
                            digits++;
                        }
                        sb.Append(v <= 0xFF ? (char)v : '?');
                    }
                    else sb.Append(n);
                    break;
            }
        }
        return sb.ToString();
    }

    // ── Brace scanning ──────────────────────────────────────────────────

    /// <summary>Text between <paramref name="openIndex"/> and its matching brace,
    /// skipping braces that live inside quoted strings.</summary>
    internal static string? ExtractBalanced(string s, int openIndex)
    {
        if (openIndex < 0 || openIndex >= s.Length || s[openIndex] != '{') return null;
        int depth = 0;
        for (int i = openIndex; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' || c == '"') { i = SkipString(s, i); continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return s[(openIndex + 1)..i];
            }
        }
        return null;
    }

    /// <summary>Each top-level <c>{ … }</c> group with its offset inside
    /// <paramref name="inner"/>, so a bad entry can be reported by line.</summary>
    internal static IEnumerable<(string Text, int Offset)> TopLevelBraceGroups(string inner)
    {
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '\'' || c == '"') { i = SkipString(inner, i); continue; }
            if (c != '{') continue;
            var body = ExtractBalanced(inner, i);
            if (body == null) yield break;
            yield return (body, i);
            i += body.Length + 1;
        }
    }

    /// <summary>Index of the closing quote of the string starting at
    /// <paramref name="start"/>, honouring backslash escapes.</summary>
    private static int SkipString(string s, int start)
    {
        char quote = s[start];
        for (int i = start + 1; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == quote) return i;
            if (s[i] == '\n') return i;      // unterminated: don't run off the end
        }
        return s.Length - 1;
    }

    // ── Misc ────────────────────────────────────────────────────────────

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static string Trim(string s) => s.Length <= 60 ? s.Trim() : s[..60].Trim() + "…";

    internal static bool LooksLikeCheatTableXml(string s) =>
        s.Contains("<AssemblerScript", StringComparison.OrdinalIgnoreCase) ||
        s.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("<CheatTable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decode the FIVE entities <c>CheatTableBuilder.EscapeXml</c> emits.
    /// <c>&amp;amp;</c> last, so an escaped entity like <c>&amp;amp;lt;</c> is not
    /// collapsed twice.</summary>
    internal static string DecodeXmlEntities(string s) => s
        .Replace("&lt;", "<")
        .Replace("&gt;", ">")
        .Replace("&quot;", "\"")
        .Replace("&apos;", "'")
        .Replace("&amp;", "&");
}
