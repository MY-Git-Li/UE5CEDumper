using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

public class SnapshotIdentityTests
{
    [Theory]
    // Spawn counter stripped from the leaf name.
    [InlineData("BP_Enemy_C_47", "BP_Enemy_C")]
    [InlineData("Weapon_12", "Weapon")]
    // Class suffix "_C" preserved (C is not a digit).
    [InlineData("BP_Player_C", "BP_Player_C")]
    // Only the trailing _<digits> goes; an inner _N is kept.
    [InlineData("Foo_1_2", "Foo_1")]
    // Empty-name underscore-number left intact.
    [InlineData("_5", "_5")]
    // Every path component normalised; separators preserved.
    [InlineData("/Game/Maps/Map.Map:PersistentLevel.BP_Enemy_C_47",
                "/Game/Maps/Map.Map:PersistentLevel.BP_Enemy_C")]
    [InlineData("/Game/Foo_3.Foo_3_C_0", "/Game/Foo_3.Foo_3_C")]
    // No-op cases.
    [InlineData("", "")]
    [InlineData("Plain", "Plain")]
    public void NormalizePath_StripsTrailingFNameNumber(string input, string expected)
    {
        Assert.Equal(expected, SnapshotIdentity.NormalizePath(input));
    }

    [Fact]
    public void NormalizePath_NullSafe()
    {
        Assert.Equal("", SnapshotIdentity.NormalizePath(null));
    }
}
