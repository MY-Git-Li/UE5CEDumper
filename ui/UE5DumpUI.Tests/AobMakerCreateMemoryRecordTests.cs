using System.Text.Json;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Covers the one-click "Add to CE" path: the CreateMemoryRecord wire model and the
/// UE-type -> CE memory-record type mapping that the Live Walker per-row buttons use.
///
/// End-to-end (against the AOBMaker plugin pipe) is intentionally NOT here — it needs
/// Cheat Engine running with the plugin loaded, which test environments don't have. The
/// plugin self-verifies the created record before returning success in production.
/// </summary>
public class AobMakerCreateMemoryRecordTests
{
    // ---- Wire model ----

    [Fact]
    public void Serialize_CreateMemoryRecord_AlwaysEmitsValueTypeEvenWhenZero()
    {
        // valueType 0 == Byte is a legitimate value (ByteProperty / bit-field bool),
        // so it must survive serialization rather than being dropped as a default int.
        var msg = new AobMakerMessage
        {
            Type = "CreateMemoryRecord",
            Description = "Health",
            Address = "7FF769E29110",
            ValueType = 0,
            IsSigned = false,
            ShowAsHex = false,
        };

        var json = JsonSerializer.Serialize(msg, AobMakerJsonContext.Relaxed.AobMakerMessage);

        Assert.Contains("\"type\":\"CreateMemoryRecord\"", json);
        Assert.Contains("\"description\":\"Health\"", json);
        Assert.Contains("\"address\":\"7FF769E29110\"", json);
        Assert.Contains("\"valueType\":0", json);

        // false flags stay omitted (default-ignore) so the wire payload is minimal.
        // Match JSON keys (quoted) — "description" itself contains the substring "script".
        Assert.DoesNotContain("\"isSigned\"", json);
        Assert.DoesNotContain("\"showAsHex\"", json);
        Assert.DoesNotContain("\"script\"", json);
    }

    [Fact]
    public void Serialize_CreateMemoryRecord_EmitsSignedAndHexWhenSet()
    {
        var msg = new AobMakerMessage
        {
            Type = "CreateMemoryRecord",
            Description = "X",
            Address = "1000",
            ValueType = 2,
            IsSigned = true,
            ShowAsHex = true,
        };

        var json = JsonSerializer.Serialize(msg, AobMakerJsonContext.Relaxed.AobMakerMessage);

        Assert.Contains("\"valueType\":2", json);
        Assert.Contains("\"isSigned\":true", json);
        Assert.Contains("\"showAsHex\":true", json);
    }

    [Fact]
    public void Serialize_NonMemoryRecordMessage_OmitsValueType()
    {
        // valueType is nullable specifically so unrelated messages don't carry it.
        var msg = new AobMakerMessage { Type = "NavigateHexView", Address = "1000" };

        var json = JsonSerializer.Serialize(msg, AobMakerJsonContext.Relaxed.AobMakerMessage);

        Assert.DoesNotContain("valueType", json);
        Assert.DoesNotContain("isSigned", json);
        Assert.DoesNotContain("showAsHex", json);
    }

    // ---- UE-type -> CE memory-record type mapping ----
    // CE TVariableType: 0=Byte 1=Word 2=Dword 3=Qword 4=Single 5=Double
    //                   6=String 7=UnicodeString 8=ByteArray 9=Binary

    [Theory]
    [InlineData("ByteProperty", 0, false, false)]    // Byte (unsigned)
    [InlineData("Int8Property", 0, true, false)]     // Byte (signed)
    [InlineData("Int16Property", 1, true, false)]    // 2 Bytes signed
    [InlineData("UInt16Property", 1, false, false)]  // 2 Bytes unsigned
    [InlineData("IntProperty", 2, true, false)]      // 4 Bytes signed
    [InlineData("UInt32Property", 2, false, false)]  // 4 Bytes unsigned
    [InlineData("Int64Property", 3, true, false)]    // 8 Bytes signed
    [InlineData("FloatProperty", 4, false, false)]   // Single
    [InlineData("DoubleProperty", 5, false, false)]  // Double
    [InlineData("NameProperty", 2, false, false)]    // FName index -> 4 Bytes
    [InlineData("EnumProperty", 2, false, false)]    // enum underlying -> 4 Bytes
    [InlineData("ObjectProperty", 3, false, true)]   // pointer -> 8 Bytes hex
    [InlineData("WeakObjectProperty", 3, false, true)]
    [InlineData("StructProperty", 3, false, true)]   // non-scalar -> 8 Bytes hex fallback
    [InlineData("ArrayProperty", 3, false, true)]    // non-scalar -> 8 Bytes hex fallback
    public void MapFieldToCeRecordType_MapsExpected(string typeName, int vt, bool signed, bool hex)
    {
        var field = new LiveFieldValue { Name = "f", TypeName = typeName };

        var t = CeXmlExportService.MapFieldToCeRecordType(field);

        Assert.Equal(vt, t.ValueType);
        Assert.Equal(signed, t.IsSigned);
        Assert.Equal(hex, t.ShowAsHex);
    }

    [Fact]
    public void MapFieldToCeRecordType_BitfieldBool_FallsBackToContainingByte()
    {
        // The single-record command carries no bit start/length, so a bit-field bool
        // (CE "Binary") degrades to the containing Byte — the useful breakpoint target.
        var field = new LiveFieldValue { Name = "b", TypeName = "BoolProperty", BoolBitIndex = 3 };

        var t = CeXmlExportService.MapFieldToCeRecordType(field);

        Assert.Equal(0, t.ValueType); // Byte
        Assert.False(t.IsSigned);
        Assert.False(t.ShowAsHex);
    }

    [Fact]
    public void PointerRecordType_Is8BytesHex()
    {
        var t = CeXmlExportService.PointerRecordType;

        Assert.Equal(3, t.ValueType); // Qword / 8 Bytes
        Assert.False(t.IsSigned);
        Assert.True(t.ShowAsHex);
    }
}
