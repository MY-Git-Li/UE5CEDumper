using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// BugItGo / BugIt string parsing (docs/teleport-spec.md §10.3): the three
/// accepted formats, negatives, comma/space separators, ?BugRot= extraction,
/// and garbage rejection.
/// </summary>
public class BugItGoParserTests
{
    [Fact]
    public void Parses_bugitgo_space_separated()
    {
        Assert.True(BugItGoParser.TryParse("BugItGo 123.4 -56.7 890", out var t));
        Assert.NotNull(t);
        Assert.Equal(123.4, t!.X, 3);
        Assert.Equal(-56.7, t.Y, 3);
        Assert.Equal(890.0, t.Z, 3);
        Assert.False(t.HasRotation);
    }

    [Fact]
    public void Parses_bare_xyz_space()
    {
        Assert.True(BugItGoParser.TryParse("100 200 -300", out var t));
        Assert.Equal(100, t!.X, 3);
        Assert.Equal(-300, t.Z, 3);
    }

    [Fact]
    public void Parses_bare_xyz_comma()
    {
        Assert.True(BugItGoParser.TryParse("1.5, -2.5, 3.5", out var t));
        Assert.Equal(1.5, t!.X, 3);
        Assert.Equal(-2.5, t.Y, 3);
        Assert.Equal(3.5, t.Z, 3);
    }

    [Fact]
    public void Parses_full_bugloc_with_rotation()
    {
        const string s = "?BugLoc=(X=123.4,Y=-56.7,Z=890.0)?BugRot=(P=-30.0,Y=90.0,R=0.0)";
        Assert.True(BugItGoParser.TryParse(s, out var t));
        Assert.NotNull(t);
        Assert.Equal(123.4, t!.X, 3);
        Assert.Equal(-56.7, t.Y, 3);
        Assert.Equal(890.0, t.Z, 3);
        Assert.True(t.HasRotation);
        Assert.Equal(-30.0, t.Pitch!.Value, 3);
        Assert.Equal(90.0, t.Yaw!.Value, 3);
        Assert.Equal(0.0, t.Roll!.Value, 3);
    }

    [Fact]
    public void Parses_bugloc_without_rotation()
    {
        Assert.True(BugItGoParser.TryParse("?BugLoc=(X=1.0,Y=2.0,Z=3.0)", out var t));
        Assert.NotNull(t);
        Assert.False(t!.HasRotation);
    }

    [Fact]
    public void Parses_six_token_form_as_pose()
    {
        Assert.True(BugItGoParser.TryParse("BugItGo 1 2 3 10 20 30", out var t));
        Assert.True(t!.HasRotation);
        Assert.Equal(10, t.Pitch!.Value, 3);
        Assert.Equal(30, t.Roll!.Value, 3);
    }

    [Fact]
    public void Is_case_insensitive()
    {
        Assert.True(BugItGoParser.TryParse("bugitgo 1 2 3", out _));
        Assert.True(BugItGoParser.TryParse("?bugloc=(x=1,y=2,z=3)", out var t));
        Assert.Equal(1, t!.X, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("1 2")]            // too few
    [InlineData("1 2 3 4")]       // wrong count (4)
    [InlineData("BugItGo a b c")] // non-numeric
    [InlineData(null)]
    public void Rejects_garbage(string? input)
    {
        Assert.False(BugItGoParser.TryParse(input, out var t));
        Assert.Null(t);
    }
}
