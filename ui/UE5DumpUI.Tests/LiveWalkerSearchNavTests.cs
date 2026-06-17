using System.IO;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for Live Walker prev/next match navigation. The field search only
/// re-colours matching rows (IsSearchMatch); the up/down buttons step the
/// selection between matches with wrap-around so the user can jump through
/// them. Logic is pure VM state — no running game required.
/// </summary>
public class LiveWalkerSearchNavTests
{
    private static LiveWalkerViewModel MakeVm()
        => new LiveWalkerViewModel(new StubDumpService(), new MockLoggingService(),
                                   new MockPlatformService(Path.GetTempPath()));

    // 5 rows; matches at indices 1, 3, 4 (non-adjacent, with non-matches
    // interleaved so IndexOf-in-matches differs from IndexOf-in-Fields).
    private static LiveWalkerViewModel WithMatches(
        out LiveFieldValue m0, out LiveFieldValue m1, out LiveFieldValue m2)
    {
        var vm = MakeVm();
        vm.Fields.Add(new LiveFieldValue { Name = "Alpha" });
        vm.Fields.Add(m0 = new LiveFieldValue { Name = "Health",    IsSearchMatch = true });
        vm.Fields.Add(new LiveFieldValue { Name = "Beta" });
        vm.Fields.Add(m1 = new LiveFieldValue { Name = "HealthMax", IsSearchMatch = true });
        vm.Fields.Add(m2 = new LiveFieldValue { Name = "CurHealth", IsSearchMatch = true });
        vm.HasSearchResults = true;
        return vm;
    }

    [Fact]
    public void NextMatch_FromNoSelection_SelectsFirstMatch()
    {
        var vm = WithMatches(out var m0, out _, out _);
        vm.NextSearchMatchCommand.Execute(null);
        Assert.Same(m0, vm.SelectedField);
    }

    [Fact]
    public void PrevMatch_FromNoSelection_SelectsLastMatch()
    {
        var vm = WithMatches(out _, out _, out var m2);
        vm.PrevSearchMatchCommand.Execute(null);
        Assert.Same(m2, vm.SelectedField);
    }

    [Fact]
    public void NextMatch_StepsForward_AndWrapsToFirst()
    {
        var vm = WithMatches(out var m0, out var m1, out var m2);
        vm.NextSearchMatchCommand.Execute(null);
        Assert.Same(m0, vm.SelectedField);
        vm.NextSearchMatchCommand.Execute(null);
        Assert.Same(m1, vm.SelectedField);
        vm.NextSearchMatchCommand.Execute(null);
        Assert.Same(m2, vm.SelectedField);
        vm.NextSearchMatchCommand.Execute(null);   // last → wrap to first
        Assert.Same(m0, vm.SelectedField);
    }

    [Fact]
    public void PrevMatch_FromFirst_WrapsToLast()
    {
        var vm = WithMatches(out var m0, out _, out var m2);
        vm.NextSearchMatchCommand.Execute(null);   // m0 (first match)
        Assert.Same(m0, vm.SelectedField);
        vm.PrevSearchMatchCommand.Execute(null);   // first → wrap to last
        Assert.Same(m2, vm.SelectedField);
    }

    [Fact]
    public void Navigate_FromNonMatchSelection_JumpsToFirstMatchForward()
    {
        var vm = WithMatches(out var m0, out _, out _);
        vm.SelectedField = vm.Fields[0];           // "Alpha" — not a match
        vm.NextSearchMatchCommand.Execute(null);
        Assert.Same(m0, vm.SelectedField);
    }

    [Fact]
    public void Navigate_NoMatches_DoesNothing()
    {
        var vm = MakeVm();
        vm.Fields.Add(new LiveFieldValue { Name = "X" });
        // HasSearchResults stays false (no search active).
        vm.NextSearchMatchCommand.Execute(null);
        Assert.Null(vm.SelectedField);
    }

    [Fact]
    public void Navigate_RaisesScrollFieldIntoView_ForTarget()
    {
        var vm = WithMatches(out var m0, out _, out _);
        LiveFieldValue? scrolled = null;
        vm.ScrollFieldIntoView += f => scrolled = f;
        vm.NextSearchMatchCommand.Execute(null);
        Assert.Same(m0, scrolled);
    }
}
