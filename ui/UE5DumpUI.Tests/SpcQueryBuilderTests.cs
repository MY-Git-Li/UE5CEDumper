using UE5DumpUI.Models;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Locks the pure SPC SQL shape: correct join keys per mode, correct directional
/// predicate clauses comparing snapshot i vs i-1, optional LIKE filters, the
/// truncation-detecting limit, and validation. No database — this is the
/// builder contract only. Engine semantics are covered by SnapshotStore tests.
/// </summary>
public class SpcQueryBuilderTests
{
    private static SpcQuery Q(SpcJoinMode mode, params SpcPredicateKind[] preds)
    {
        var q = new SpcQuery { JoinMode = mode };
        for (int i = 0; i < preds.Length; i++)
        {
            q.SnapshotIds.Add(100 + i);
            q.Predicates.Add(preds[i]);
        }
        return q;
    }

    [Fact]
    public void NeedsAtLeastTwoSnapshots()
    {
        var q = Q(SpcJoinMode.Strict, SpcPredicateKind.Any);
        Assert.Throws<ArgumentException>(() => SpcQueryBuilder.Compile(q));
    }

    [Fact]
    public void PredicateCountMustMatchSnapshotCount()
    {
        var q = new SpcQuery { JoinMode = SpcJoinMode.Strict };
        q.SnapshotIds.Add(1); q.SnapshotIds.Add(2);
        q.Predicates.Add(SpcPredicateKind.Any);   // only one predicate for two snapshots
        Assert.Throws<ArgumentException>(() => SpcQueryBuilder.Compile(q));
    }

    [Theory]
    [InlineData(SpcJoinMode.Strict,    "norm_path", "prop_offset")]
    [InlineData(SpcJoinMode.Loose,     "outer_chain", null)]
    [InlineData(SpcJoinMode.InSession, "gobjects_index", null)]
    public void JoinUsesModeKeyColumns(SpcJoinMode mode, string col1, string? col2)
    {
        var q = Q(mode, SpcPredicateKind.Any, SpcPredicateKind.Unchanged);
        var c = SpcQueryBuilder.Compile(q);

        // class_fqn + prop_name always; mode-specific keys on top.
        Assert.Contains("f1.class_fqn=f0.class_fqn", c.Sql);
        Assert.Contains("f1.prop_name=f0.prop_name", c.Sql);
        Assert.Contains($"f1.{col1}=f0.{col1}", c.Sql);
        if (col2 != null) Assert.Contains($"f1.{col2}=f0.{col2}", c.Sql);
        // The other modes' exclusive keys must NOT appear.
        if (mode != SpcJoinMode.Loose)     Assert.DoesNotContain("outer_chain", c.Sql);
        if (mode != SpcJoinMode.InSession) Assert.DoesNotContain("gobjects_index", c.Sql);
    }

    [Fact]
    public void DirectionalPredicatesCompareConsecutiveSnapshots()
    {
        // baseline, Increased, Decreased, Unchanged, Changed
        var q = Q(SpcJoinMode.Strict,
            SpcPredicateKind.Any, SpcPredicateKind.Increased, SpcPredicateKind.Decreased,
            SpcPredicateKind.Unchanged, SpcPredicateKind.Changed);
        var c = SpcQueryBuilder.Compile(q);

        Assert.Contains("f1.numeric_value>f0.numeric_value", c.Sql);
        Assert.Contains("f2.numeric_value<f1.numeric_value", c.Sql);
        Assert.Contains("f3.hex=f2.hex", c.Sql);
        Assert.Contains("f4.hex<>f3.hex", c.Sql);
        // Baseline (index 0) never produces a predicate clause.
        Assert.DoesNotContain("f0.numeric_value>", c.Sql);
        Assert.DoesNotContain("f0.hex=", c.Sql);
    }

    [Fact]
    public void IncreasedDecreasedGuardAgainstNullNumeric()
    {
        var q = Q(SpcJoinMode.Strict, SpcPredicateKind.Any, SpcPredicateKind.Increased);
        var c = SpcQueryBuilder.Compile(q);
        Assert.Contains("f1.numeric_value IS NOT NULL", c.Sql);
        Assert.Contains("f0.numeric_value IS NOT NULL", c.Sql);
    }

    [Fact]
    public void AnyChainAddsNoPredicateClauses()
    {
        var q = Q(SpcJoinMode.Strict, SpcPredicateKind.Any, SpcPredicateKind.Any);
        var c = SpcQueryBuilder.Compile(q);
        Assert.DoesNotContain("numeric_value>", c.Sql);
        Assert.DoesNotContain("numeric_value<", c.Sql);
        Assert.DoesNotContain(".hex=f", c.Sql);
        Assert.DoesNotContain(".hex<>f", c.Sql);
    }

    [Fact]
    public void ArrayRowsExcludedOnEveryAlias()
    {
        var q = Q(SpcJoinMode.Strict, SpcPredicateKind.Any, SpcPredicateKind.Unchanged);
        var c = SpcQueryBuilder.Compile(q);
        Assert.Contains("f0.array_field IS NULL", c.Sql);
        Assert.Contains("f1.array_field IS NULL", c.Sql);
    }

    [Fact]
    public void SnapshotIdsAreInlinedInOrder()
    {
        var q = Q(SpcJoinMode.Strict, SpcPredicateKind.Any, SpcPredicateKind.Unchanged);
        var c = SpcQueryBuilder.Compile(q);
        Assert.Contains("f0.snapshot_id=100", c.Sql);
        Assert.Contains("f1.snapshot_id=101", c.Sql);
    }

    [Fact]
    public void FiltersBecomeParametersOnlyWhenPresent()
    {
        var bare = SpcQueryBuilder.Compile(Q(SpcJoinMode.Strict, SpcPredicateKind.Any, SpcPredicateKind.Any));
        Assert.Null(bare.ClassLike);
        Assert.Null(bare.PropLike);
        Assert.DoesNotContain("LIKE", bare.Sql);

        var q = Q(SpcJoinMode.Strict, SpcPredicateKind.Any, SpcPredicateKind.Any);
        q.ClassContains = "Player";
        q.PropContains = "HP";
        var c = SpcQueryBuilder.Compile(q);
        Assert.Equal("%Player%", c.ClassLike);
        Assert.Equal("%HP%", c.PropLike);
        Assert.Contains("f0.class_fqn LIKE $cls", c.Sql);
        Assert.Contains("f0.prop_name LIKE $prop", c.Sql);
    }

    [Fact]
    public void LimitIsMaxRowsPlusOne()
    {
        var q = Q(SpcJoinMode.Strict, SpcPredicateKind.Any, SpcPredicateKind.Any);
        q.MaxRows = 25;
        var c = SpcQueryBuilder.Compile(q);
        Assert.Contains("LIMIT 26;", c.Sql);
    }

    [Fact]
    public void SelectsOneHexColumnPerSnapshot()
    {
        var q = Q(SpcJoinMode.Strict, SpcPredicateKind.Any, SpcPredicateKind.Any, SpcPredicateKind.Any);
        var c = SpcQueryBuilder.Compile(q);
        Assert.Contains("f0.hex", c.Sql);
        Assert.Contains("f1.hex", c.Sql);
        Assert.Contains("f2.hex", c.Sql);
        // Display columns come from the newest alias (f2).
        Assert.Contains("f2.obj_addr", c.Sql);
        Assert.Contains("f2.norm_path", c.Sql);
    }
}
