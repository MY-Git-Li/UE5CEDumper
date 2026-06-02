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
    [InlineData("BoolProperty", "01")]      // not a captured numeric type
    [InlineData("StructProperty", "00000000")]
    [InlineData("IntProperty", "")]          // empty hex
    [InlineData("IntProperty", "1E0")]       // odd-length hex
    [InlineData("IntProperty", "1E")]        // too few bytes for Int32
    public void TryFromHex_RejectsInvalid(string type, string hex)
    {
        Assert.False(SnapshotNumeric.TryFromHex(type, hex, out _));
    }
}
