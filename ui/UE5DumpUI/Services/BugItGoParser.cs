using System.Globalization;
using System.Text.RegularExpressions;

namespace UE5DumpUI.Services;

/// <summary>
/// Parses pasted BugItGo / BugIt strings into a teleport target.
/// Accepts, case-insensitively:
///   • <c>BugItGo 123.4 -56.7 890</c>  (UE's BugItGo console form)
///   • bare <c>123.4 -56.7 890</c>     (space- or comma-separated)
///   • the full BugIt clipboard form
///     <c>?BugLoc=(X=123.4,Y=-56.7,Z=890.0)?BugRot=(P=-30.0,Y=90.0,R=0.0)</c>
/// The <c>?BugLoc=</c> form may also carry rotation, surfaced via
/// <see cref="BugItGoTarget.HasRotation"/>.
///
/// Why parse client-side: BugIt can't return data to the dumper (it writes to
/// the game's log/clipboard — see docs/teleport-spec.md §10.2), so the user
/// pastes the string here and we feed X/Y/Z into the explicit-pose recall
/// pipe path.
/// </summary>
public static class BugItGoParser
{
    private static readonly Regex BugLocRe = new(
        @"BugLoc\s*=\s*\(\s*X\s*=\s*(?<x>-?[\d.eE+]+)\s*,\s*Y\s*=\s*(?<y>-?[\d.eE+]+)\s*,\s*Z\s*=\s*(?<z>-?[\d.eE+]+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BugRotRe = new(
        @"BugRot\s*=\s*\(\s*P\s*=\s*(?<p>-?[\d.eE+]+)\s*,\s*Y\s*=\s*(?<y>-?[\d.eE+]+)\s*,\s*R\s*=\s*(?<r>-?[\d.eE+]+)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Try to parse <paramref name="input"/>. Returns false (and a
    /// null target) on anything unrecognised or non-numeric.</summary>
    public static bool TryParse(string? input, out BugItGoTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim();

        // 1. Full BugIt clipboard form (?BugLoc=...?BugRot=...).
        var loc = BugLocRe.Match(s);
        if (loc.Success)
        {
            if (!TryNum(loc.Groups["x"].Value, out var lx) ||
                !TryNum(loc.Groups["y"].Value, out var ly) ||
                !TryNum(loc.Groups["z"].Value, out var lz))
                return false;

            var rot = BugRotRe.Match(s);
            if (rot.Success &&
                TryNum(rot.Groups["p"].Value, out var rp) &&
                TryNum(rot.Groups["y"].Value, out var ry) &&
                TryNum(rot.Groups["r"].Value, out var rr))
            {
                target = new BugItGoTarget(lx, ly, lz, rp, ry, rr);
                return true;
            }
            target = new BugItGoTarget(lx, ly, lz);
            return true;
        }

        // 2. "BugItGo X Y Z" or bare "X Y Z" / "X,Y,Z".
        string body = s;
        if (body.StartsWith("BugItGo", StringComparison.OrdinalIgnoreCase))
            body = body.Substring("BugItGo".Length);

        var tokens = body.Split(
            new[] { ' ', '\t', ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Accept exactly 3 (X Y Z) or 6 (X Y Z P Y R) numeric tokens.
        if (tokens.Length == 3 &&
            TryNum(tokens[0], out var x) && TryNum(tokens[1], out var y) && TryNum(tokens[2], out var z))
        {
            target = new BugItGoTarget(x, y, z);
            return true;
        }
        if (tokens.Length == 6 &&
            TryNum(tokens[0], out var x6) && TryNum(tokens[1], out var y6) && TryNum(tokens[2], out var z6) &&
            TryNum(tokens[3], out var p6) && TryNum(tokens[4], out var yw6) && TryNum(tokens[5], out var r6))
        {
            target = new BugItGoTarget(x6, y6, z6, p6, yw6, r6);
            return true;
        }
        return false;
    }

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}

/// <summary>A parsed BugItGo target. Rotation is optional.</summary>
public sealed record BugItGoTarget(
    double X, double Y, double Z,
    double? Pitch = null, double? Yaw = null, double? Roll = null)
{
    public bool HasRotation => Pitch.HasValue && Yaw.HasValue && Roll.HasValue;
}
