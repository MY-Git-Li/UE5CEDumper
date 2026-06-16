using System.Globalization;

namespace UE5DumpUI.Services;

/// <summary>
/// Decodes a captured field's little-endian hex bytes (as emitted by the DLL's
/// snapshot_chunk) into a canonical numeric value, using the field's DECLARED
/// property type — the same structured, type-correct interpretation the value
/// scan uses (no byte-reinterpret). Used by SPC Increased/Decreased/Delta
/// predicates; exact Changed/Unchanged compares the raw hex instead. Pure /
/// AOT-safe. Int64/UInt64 beyond 2^53 lose precision in the double (acceptable
/// for direction; exact compares use hex).
/// </summary>
public static class SnapshotNumeric
{
    public static bool TryFromHex(string declaredType, string hex, out double value)
    {
        value = 0;
        if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return false;

        int byteLen = hex.Length / 2;
        if (byteLen == 0 || byteLen > 8) return false;

        Span<byte> b = stackalloc byte[8];
        b.Clear();
        for (int i = 0; i < byteLen; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture, out b[i]))
                return false;
        }

        switch (declaredType)
        {
            // Non-finite floats (NaN / ±Infinity — uninitialised slack, garbage
            // in a deep struct-array slot, or a genuinely-NaN gameplay value) have
            // no meaningful numeric value: SQLite's REAL column rejects NaN ("Cannot
            // store 'NaN' values"), and NaN comparisons in SPC/diff are nonsense.
            // Return false → numeric_value stored as NULL; the raw bits are still
            // preserved in the hex column.
            case "FloatProperty":  if (byteLen < 4) return false; value = BitConverter.ToSingle(b);  return double.IsFinite(value);
            case "DoubleProperty": if (byteLen < 8) return false; value = BitConverter.ToDouble(b);  return double.IsFinite(value);
            case "IntProperty":    if (byteLen < 4) return false; value = BitConverter.ToInt32(b);   return true;
            case "UInt32Property": if (byteLen < 4) return false; value = BitConverter.ToUInt32(b);  return true;
            case "Int16Property":  if (byteLen < 2) return false; value = BitConverter.ToInt16(b);   return true;
            case "UInt16Property": if (byteLen < 2) return false; value = BitConverter.ToUInt16(b);  return true;
            case "Int64Property":  if (byteLen < 8) return false; value = BitConverter.ToInt64(b);   return true;
            case "UInt64Property": if (byteLen < 8) return false; value = BitConverter.ToUInt64(b);  return true;
            case "Int8Property":   value = (sbyte)b[0]; return true;
            case "ByteProperty":   value = b[0];        return true;
            default:               return false;
        }
    }

    /// <summary>
    /// Render a captured field's hex to a human-readable value for the diff
    /// grid. Integers are rendered exactly (no double precision loss); floats
    /// show up to 6 significant decimals. Falls back to the raw hex if the type
    /// isn't a known numeric. Pure / AOT-safe.
    /// </summary>
    public static string Render(string declaredType, string hex)
    {
        if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return hex ?? "";
        int byteLen = hex.Length / 2;
        if (byteLen == 0 || byteLen > 8) return hex;

        Span<byte> b = stackalloc byte[8];
        b.Clear();
        for (int i = 0; i < byteLen; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                               CultureInfo.InvariantCulture, out b[i]))
                return hex;
        }

        return declaredType switch
        {
            "FloatProperty"  when byteLen >= 4 => BitConverter.ToSingle(b).ToString("0.######", CultureInfo.InvariantCulture),
            "DoubleProperty" when byteLen >= 8 => BitConverter.ToDouble(b).ToString("0.######", CultureInfo.InvariantCulture),
            "IntProperty"    when byteLen >= 4 => BitConverter.ToInt32(b).ToString(CultureInfo.InvariantCulture),
            "UInt32Property" when byteLen >= 4 => BitConverter.ToUInt32(b).ToString(CultureInfo.InvariantCulture),
            "Int16Property"  when byteLen >= 2 => BitConverter.ToInt16(b).ToString(CultureInfo.InvariantCulture),
            "UInt16Property" when byteLen >= 2 => BitConverter.ToUInt16(b).ToString(CultureInfo.InvariantCulture),
            "Int64Property"  when byteLen >= 8 => BitConverter.ToInt64(b).ToString(CultureInfo.InvariantCulture),
            "UInt64Property" when byteLen >= 8 => BitConverter.ToUInt64(b).ToString(CultureInfo.InvariantCulture),
            "Int8Property"  => ((sbyte)b[0]).ToString(CultureInfo.InvariantCulture),
            "ByteProperty"  => b[0].ToString(CultureInfo.InvariantCulture),
            _               => hex,
        };
    }
}
