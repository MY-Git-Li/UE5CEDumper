using System.IO;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// C5 — the "Pivot this property" context-menu command on the source panels raises
/// NavigateToPivot with (className, propName) so MainWindowViewModel can hand off to
/// the Class Pivot tab. The handoff stays inert when the row/class is empty.
/// </summary>
public class PivotHandoffCommandTests
{
    [Fact]
    public void PropertySearch_PivotThis_RaisesNavigateToPivot()
    {
        var vm = new PropertySearchViewModel(new StubDumpService(), new MockLoggingService());
        (string cls, string prop)? got = null;
        vm.NavigateToPivot += (c, p) => got = (c, p);

        vm.PivotThisCommand.Execute(new PropertySearchMatch { ClassName = "AMyActor", PropName = "Health" });

        Assert.Equal(("AMyActor", "Health"), got);
    }

    [Fact]
    public void PropertySearch_PivotThis_NullRow_DoesNothing()
    {
        var vm = new PropertySearchViewModel(new StubDumpService(), new MockLoggingService());
        bool fired = false;
        vm.NavigateToPivot += (_, _) => fired = true;

        vm.PivotThisCommand.Execute(null);   // no SelectedResult either

        Assert.False(fired);
    }

    [Fact]
    public void LiveWalker_PivotThis_UsesCurrentClassAndFieldName()
    {
        var vm = new LiveWalkerViewModel(new StubDumpService(), new MockLoggingService(),
                                         new MockPlatformService(Path.GetTempPath()));
        vm.CurrentClassName = "APlayerState";
        (string cls, string prop)? got = null;
        vm.NavigateToPivot += (c, p) => got = (c, p);

        vm.PivotThisCommand.Execute(new LiveFieldValue { Name = "Score" });

        Assert.Equal(("APlayerState", "Score"), got);
    }

    [Fact]
    public void LiveWalker_PivotThis_NoClassContext_DoesNothing()
    {
        var vm = new LiveWalkerViewModel(new StubDumpService(), new MockLoggingService(),
                                         new MockPlatformService(Path.GetTempPath()));
        vm.CurrentClassName = "";   // synthetic/empty context
        bool fired = false;
        vm.NavigateToPivot += (_, _) => fired = true;

        vm.PivotThisCommand.Execute(new LiveFieldValue { Name = "Score" });

        Assert.False(fired);
    }

    // Value-locator -> pivot: a value-scan hit already carries class + field, so
    // "I can see this value" reaches a pivot in one click (same handoff contract).

    [Fact]
    public void ValueSearch_PivotThis_RaisesNavigateToPivot()
    {
        var vm = new ValueSearchViewModel(new StubDumpService(), new MockLoggingService());
        (string cls, string prop)? got = null;
        vm.NavigateToPivot += (c, p) => got = (c, p);

        vm.PivotThisCommand.Execute(new ValueCandidate { ClassName = "APlayerState", FieldName = "Gold" });

        Assert.Equal(("APlayerState", "Gold"), got);
    }

    [Fact]
    public void ValueSearch_PivotThis_NoClass_DoesNothing()
    {
        var vm = new ValueSearchViewModel(new StubDumpService(), new MockLoggingService());
        bool fired = false;
        vm.NavigateToPivot += (_, _) => fired = true;

        vm.PivotThisCommand.Execute(new ValueCandidate { ClassName = "", FieldName = "Gold" });

        Assert.False(fired);
    }
}
