using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

public class SnapshotNumericTests
{
    [Theory]
    // Little-endian hex (as the DLL emits) -> canonical numeric.
    [InlineData("FloatProperty",  "0000803F", 1.0)]            // 0x3F800000
    [InlineData("DoubleProperty", "0000000000000440", 2.5)]   // 0x4004000000000000
    [InlineData("IntProperty",    "1E000000", 30.0)]
    [InlineData("IntProperty",    "FFFFFFFF", -1.0)]
    [InlineData("UInt32Property", "FFFFFFFF", 4294967295.0)]
    [InlineData("Int16Property",  "FEFF", -2.0)]
    [InlineData("UInt16Property", "FFFF", 65535.0)]
    [InlineData("Int64Property",  "0500000000000000", 5.0)]
    [InlineData("Int8Property",   "FF", -1.0)]
    [InlineData("ByteProperty",   "FF", 255.0)]
    public void TryFromHex_DecodesPerDeclaredType(string type, string hex, double expected)
    {
        Assert.True(SnapshotNumeric.TryFromHex(type, hex, out var v));
        Assert.Equal(expected, v, precision: 6);
    }

    [Theory]
    [InlineData("FloatProperty",  "0000C842", "100")]            // 100.0 -> "100"
    [InlineData("FloatProperty",  "0000803F", "1")]              // 1.0 -> "1"
    [InlineData("DoubleProperty", "0000000000000440", "2.5")]
    [InlineData("IntProperty",    "1E000000", "30")]
    [InlineData("IntProperty",    "FFFFFFFF", "-1")]
    [InlineData("UInt32Property", "FFFFFFFF", "4294967295")]
    [InlineData("Int64Property",  "FFFFFFFFFFFFFFFF", "-1")]      // exact, no double loss
    [InlineData("ByteProperty",   "FF", "255")]
    [InlineData("StructProperty", "DEADBEEF", "DEADBEEF")]       // unknown -> raw hex
    public void Render_ProducesDisplayString(string type, string hex, string expected)
    {
        Assert.Equal(expected, SnapshotNumeric.Render(type, hex));
    }

    [Theory]
    [InlineData("BoolProperty", "01")]      // not a captured numeric type
    [InlineData("StructProperty", "00000000")]
    [InlineData("IntProperty", "")]          // empty hex
    [InlineData("IntProperty", "1E0")]       // odd-length hex
    [InlineData("IntProperty", "1E")]        // too few bytes for Int32
    public void TryFromHex_RejectsInvalid(string type, string hex)
    {
        Assert.False(SnapshotNumeric.TryFromHex(type, hex, out _));
    }

    [Theory]
    // Non-finite floats must NOT yield a numeric value: SQLite's REAL column
    // rejects NaN ("Cannot store 'NaN' values") and NaN/Inf are meaningless for
    // SPC/diff. The deep recursive capture (build 1205) can reach such leaves.
    // (Little-endian hex as the DLL emits.)
    [InlineData("FloatProperty",  "0000C07F")]          // float qNaN  0x7FC00000
    [InlineData("FloatProperty",  "0000807F")]          // float +Inf  0x7F800000
    [InlineData("FloatProperty",  "000080FF")]          // float -Inf  0xFF800000
    [InlineData("DoubleProperty", "000000000000F87F")]  // double qNaN 0x7FF8000000000000
    [InlineData("DoubleProperty", "000000000000F07F")]  // double +Inf 0x7FF0000000000000
    [InlineData("DoubleProperty", "000000000000F0FF")]  // double -Inf 0xFFF0000000000000
    public void TryFromHex_RejectsNonFiniteFloats(string type, string hex)
    {
        Assert.False(SnapshotNumeric.TryFromHex(type, hex, out _));
    }

    [Theory]
    // Native-C scan P0 contract (docs/native-c-value-scan-spec.md §4.3): the DLL's
    // Ubel::NormalizeGuessedTypeToProperty maps every "Guess What" label to one of
    // these canonical property-type strings before emitting a native snapshot row.
    // SnapshotNumeric.TryFromHex MUST accept each (with correctly-sized hex) or the
    // native field would store NULL numeric_value and silently break SPC / Pivot.
    // This is the C# half of the cross-language round-trip the DLL test locks via
    // Radar::TryDataTypeFromPropertyTypeName.
    [InlineData("FloatProperty",  "0000803F")]          // <- Float / Float?  (1.0)
    [InlineData("DoubleProperty", "000000000000F03F")]  // <- Double / Double? (1.0)
    [InlineData("IntProperty",    "01000000")]          // <- Int32?
    [InlineData("Int16Property",  "0100")]              // <- Int16?
    [InlineData("ByteProperty",   "01")]                // <- Byte?
    [InlineData("Int64Property",  "0100000000000000")]  // <- Int64? (if ever emitted)
    public void TryFromHex_AcceptsEveryNormalizedNativeType(string canonicalType, string hex)
    {
        Assert.True(SnapshotNumeric.TryFromHex(canonicalType, hex, out _));
    }
}
