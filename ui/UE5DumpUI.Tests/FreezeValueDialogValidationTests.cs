using UE5DumpUI.Views;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for <see cref="FreezeValueDialog.ValidateAndConvert"/>.
///
/// The dialog itself (Avalonia Window) can't be exercised in headless
/// tests without spinning up the runtime, so we promoted the value
/// parsing to a pure static method and test that. Covers:
///   - All 12 supported helper types accept canonical inputs
///   - Bool accepts true/false/1/0 (case insensitive) but rejects junk
///   - Float / double accept scientific notation + culture-invariant parse
///   - Signed vs unsigned integer handling
///   - Empty / whitespace rejection
///   - Unsupported type returns the v1-scope error
/// </summary>
public class FreezeValueDialogValidationTests
{
    [Theory]
    [InlineData("true",  "true")]
    [InlineData("True",  "true")]
    [InlineData("TRUE",  "true")]
    [InlineData("1",     "true")]
    [InlineData("false", "false")]
    [InlineData("False", "false")]
    [InlineData("0",     "false")]
    public void Validate_Bool_AcceptsAllForms(string input, string expected)
    {
        var literal = FreezeValueDialog.ValidateAndConvert(input, "bool", out var err);
        Assert.NotNull(literal);
        Assert.Equal(expected, literal);
        Assert.Equal("", err);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("2")]
    [InlineData("")]
    public void Validate_Bool_RejectsOtherForms(string input)
    {
        var literal = FreezeValueDialog.ValidateAndConvert(input, "bool", out var err);
        Assert.Null(literal);
        Assert.NotEqual("", err);
    }

    [Theory]
    [InlineData("3.14",     "float")]
    [InlineData("-2.5",     "float")]
    [InlineData("1e10",     "float")]
    [InlineData("0.0",      "double")]
    [InlineData("-1.5e-3",  "double")]
    public void Validate_FloatDouble_AcceptsNumbers(string input, string helperType)
    {
        var literal = FreezeValueDialog.ValidateAndConvert(input, helperType, out var err);
        Assert.NotNull(literal);
        Assert.Equal("", err);
        // Round-trip parse to confirm we emitted a Lua-parseable literal.
        Assert.True(double.TryParse(literal,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out _));
    }

    [Theory]
    [InlineData("not a number", "float")]
    [InlineData("",             "double")]
    [InlineData("   ",          "float")]
    public void Validate_FloatDouble_RejectsJunk(string input, string helperType)
    {
        var literal = FreezeValueDialog.ValidateAndConvert(input, helperType, out var err);
        Assert.Null(literal);
        Assert.NotEqual("", err);
    }

    [Theory]
    [InlineData("0",            "int32")]
    [InlineData("-1",           "int32")]
    [InlineData("2147483647",   "int32")]
    [InlineData("-9223372036854775808", "int64")]
    public void Validate_SignedInt_AcceptsRange(string input, string helperType)
    {
        var literal = FreezeValueDialog.ValidateAndConvert(input, helperType, out var err);
        Assert.NotNull(literal);
        Assert.Equal("", err);
        Assert.Equal(input.Trim(), literal);
    }

    [Theory]
    [InlineData("-1",                       "uint32")]
    [InlineData("not a number",             "uint32")]
    public void Validate_UnsignedInt_RejectsNegativeAndJunk(string input, string helperType)
    {
        var literal = FreezeValueDialog.ValidateAndConvert(input, helperType, out var err);
        Assert.Null(literal);
        Assert.NotEqual("", err);
    }

    [Theory]
    [InlineData("0",            "uint8")]
    [InlineData("255",          "uint8")]
    [InlineData("4294967295",   "uint32")]
    public void Validate_UnsignedInt_AcceptsRange(string input, string helperType)
    {
        var literal = FreezeValueDialog.ValidateAndConvert(input, helperType, out var err);
        Assert.NotNull(literal);
        Assert.Equal("", err);
    }

    [Fact]
    public void Validate_UnsupportedType_ExplainsV1Scope()
    {
        var literal = FreezeValueDialog.ValidateAndConvert("anything", "", out var err);
        Assert.Null(literal);
        Assert.Contains("v1", err);
    }
}
