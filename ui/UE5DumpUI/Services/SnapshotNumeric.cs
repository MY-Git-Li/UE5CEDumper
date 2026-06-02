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
            case "FloatProperty":  if (byteLen < 4) return false; value = BitConverter.ToSingle(b);  return true;
            case "DoubleProperty": if (byteLen < 8) return false; value = BitConverter.ToDouble(b);  return true;
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
}
