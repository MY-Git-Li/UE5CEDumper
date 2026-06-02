using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Pure key/value scorer: enum/int + key-named + partitioning fields rank as keys
/// above continuous floats and unique ids; value interest reuses the calibrated
/// PropertyScoringTable.
/// </summary>
public class PivotKeyScorerTests
{
    private static PivotFieldInfo F(string name, string type, int distinct, int instances)
        => new() { Name = name, DeclaredType = type, DistinctCount = distinct, InstanceCount = instances };

    [Fact]
    public void IntKey_BeatsFloat()
    {
        double idScore = PivotKeyScorer.KeyScore(F("ItemID", "IntProperty", 5, 20));
        double hpScore = PivotKeyScorer.KeyScore(F("Health", "FloatProperty", 20, 20));
        Assert.True(idScore > hpScore);
    }

    [Fact]
    public void SuggestKey_PicksThePartitioningIdField()
    {
        var fields = new[]
        {
            F("ItemID",   "IntProperty",   5, 20),   // strong: id name + good partition
            F("Quantity", "IntProperty",  18, 20),
            F("Health",   "FloatProperty", 20, 20),  // continuous — poor key
            F("Flags",    "ByteProperty",  3, 20),
        };
        var best = PivotKeyScorer.SuggestKey(fields);
        Assert.NotNull(best);
        Assert.Equal("ItemID", best!.Name);
    }

    [Fact]
    public void GroupingKey_BeatsUniqueId()
    {
        // Same name/type; the one that partitions (distinct < instances) wins over
        // the one that is unique-per-instance (degenerate, like identity).
        double grouping = PivotKeyScorer.KeyScore(F("TypeId", "IntProperty", 4, 20));
        double unique   = PivotKeyScorer.KeyScore(F("TypeId", "IntProperty", 20, 20));
        Assert.True(grouping > unique);
    }

    [Fact]
    public void AllSameValue_IsUselessKey()
    {
        // distinct == 1 -> cardinality contributes nothing.
        double s = PivotKeyScorer.KeyScore(F("Constant", "IntProperty", 1, 20));
        double partitioning = PivotKeyScorer.KeyScore(F("Constant", "IntProperty", 5, 20));
        Assert.True(partitioning > s);
    }

    [Fact]
    public void ValueScore_RanksInterestingFieldsViaScoringTable()
    {
        Assert.True(PivotKeyScorer.ValueScore("BP_Player_C", "Health")
                    >= PropertyScoringTable.InterestingThreshold);
        Assert.True(PivotKeyScorer.ValueScore("BP_Player_C", "Health")
                    > PivotKeyScorer.ValueScore("BP_Player_C", "ZqxInternalPad"));
    }
}
