using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for <see cref="FreezeScriptGenerator"/>.
///
/// Exercise four axes:
/// 1. Type mapping (UE -> helper) covers every numeric + bool case.
/// 2. Lua escaping survives single-quote / backslash / newline.
/// 3. The rendered script includes (a) the helper-file lookup, (b) a
///    CFG block with className/offset/type/value embedded literally,
///    (c) start() in ENABLE and stop() in DISABLE.
/// 4. Embedded helper resource is reachable from the assembly manifest
///    (catches packaging drift).
/// </summary>
public class FreezeScriptGeneratorTests
{
    [Theory]
    [InlineData("BoolProperty",    "bool")]
    [InlineData("ByteProperty",    "uint8")]
    [InlineData("Int8Property",    "int8")]
    [InlineData("Int16Property",   "int16")]
    [InlineData("UInt16Property",  "uint16")]
    [InlineData("IntProperty",     "int32")]
    [InlineData("UInt32Property",  "uint32")]
    [InlineData("EnumProperty",    "int32")]
    [InlineData("Int64Property",   "int64")]
    [InlineData("UInt64Property",  "uint64")]
    [InlineData("FloatProperty",   "float")]
    [InlineData("DoubleProperty",  "double")]
    public void MapToHelperType_KnownTypes_MapsCorrectly(string ue, string expected)
    {
        Assert.Equal(expected, FreezeScriptGenerator.MapToHelperType(ue));
        Assert.True(FreezeScriptGenerator.IsTypeSupported(ue));
    }

    [Theory]
    [InlineData("StructProperty")]
    [InlineData("ObjectProperty")]
    [InlineData("ArrayProperty")]
    [InlineData("StrProperty")]
    [InlineData("NameProperty")]
    [InlineData("UnknownProperty")]
    public void MapToHelperType_UnsupportedTypes_ReturnsEmpty(string ue)
    {
        Assert.Equal("", FreezeScriptGenerator.MapToHelperType(ue));
        Assert.False(FreezeScriptGenerator.IsTypeSupported(ue));
    }

    [Theory]
    [InlineData("plain",          "plain")]
    [InlineData(@"back\slash",    @"back\\slash")]
    [InlineData("with'quote",     @"with\'quote")]
    [InlineData("line\nbreak",    @"line\nbreak")]
    [InlineData("carriage\rret",  @"carriage\rret")]
    [InlineData("tab\there",      @"tab\there")]
    public void EscapeLua_HandlesSpecialChars(string input, string expected)
    {
        Assert.Equal(expected, FreezeScriptGenerator.EscapeLua(input));
    }

    [Fact]
    public void Generate_FloatProperty_ProducesExpectedSections()
    {
        var p = new FreezeScriptParams
        {
            ClassName      = "BP_Teammate_C",
            PropertyName   = "CurrentHealth",
            PropertyOffset = 0x4F8,
            UeTypeName     = "FloatProperty",
            ValueLiteral   = "9999.0",
        };

        var script = FreezeScriptGenerator.Generate(p);

        // [ENABLE] / [DISABLE] block structure
        Assert.Contains("[ENABLE]", script);
        Assert.Contains("[DISABLE]", script);

        // Helper file lookup (no filesystem fallback)
        Assert.Contains("findTableFile('ue5_freeze_helper.lua')", script);

        // CFG block fields literal
        Assert.Contains("className          = 'BP_Teammate_C',", script);
        Assert.Contains("propOffset         = 0x4F8,", script);
        Assert.Contains("valueType          = 'float',", script);
        Assert.Contains("value              = 9999.0,", script);

        // Start in ENABLE, stop in DISABLE -- handles tracked in a shared
        // keyed table so multiple Freeze scripts don't clobber each other.
        var enableIdx = script.IndexOf("[ENABLE]");
        var disableIdx = script.IndexOf("[DISABLE]");
        Assert.True(enableIdx < disableIdx);
        var enableBlock = script.Substring(enableIdx, disableIdx - enableIdx);
        var disableBlock = script.Substring(disableIdx);
        Assert.Contains("handleOrErr.start", enableBlock);
        Assert.Contains("h.stop", disableBlock);
        // Per-script key includes the class + prop + offset
        Assert.Contains("BP_Teammate_C::CurrentHealth@0x4F8", script);
        // Shared global table -- avoids one script's [DISABLE] killing another's handle
        Assert.Contains("_ue5_freeze_handles", script);
    }

    [Fact]
    public void Generate_BoolProperty_EmitsBoolHelperType()
    {
        var p = new FreezeScriptParams
        {
            ClassName      = "PlayerCharacter",
            PropertyName   = "bCanBeDamaged",
            PropertyOffset = 0x328,
            UeTypeName     = "BoolProperty",
            ValueLiteral   = "false",
        };

        var script = FreezeScriptGenerator.Generate(p);

        Assert.Contains("valueType          = 'bool',", script);
        Assert.Contains("value              = false,", script);
    }

    [Fact]
    public void Generate_ClassNameWithQuote_IsEscaped()
    {
        var p = new FreezeScriptParams
        {
            ClassName      = "Weird'Class",
            PropertyName   = "X",
            PropertyOffset = 0x10,
            UeTypeName     = "IntProperty",
            ValueLiteral   = "1",
        };

        var script = FreezeScriptGenerator.Generate(p);

        // Single quote must be backslash-escaped inside the Lua literal
        Assert.Contains(@"className          = 'Weird\'Class',", script);
    }

    [Fact]
    public void Generate_OffsetRendersAsHex()
    {
        // 256 = 0x100 -- verify the formatter produces 0x{X} not 256.
        var p = new FreezeScriptParams
        {
            ClassName      = "Foo",
            PropertyName   = "Bar",
            PropertyOffset = 256,
            UeTypeName     = "IntProperty",
            ValueLiteral   = "0",
        };

        var script = FreezeScriptGenerator.Generate(p);

        Assert.Contains("propOffset         = 0x100,", script);
    }

    [Fact]
    public void FreezeHelperLuaResource_Read_ReturnsNonTrivialContent()
    {
        var content = FreezeHelperLuaResource.Read();

        Assert.NotNull(content);
        Assert.True(content.Length > 500,
            $"freeze helper content suspiciously short ({content.Length} chars)");
        // Sanity check: contains the public API surface the generator depends on.
        Assert.Contains("freezeProperty", content);
        Assert.Contains("CMD_LIST_INSTANCES", content);
    }
}
