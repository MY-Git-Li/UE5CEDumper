using UE5DumpUI.Models;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for the per-row "🌍 Locate in GWorld" handoff added to the Interesting
/// Properties panel. Mirrors the Interesting Functions LocateRowInGWorld contract:
/// the command raises <see cref="InterestingPropertiesViewModel.LocateInGWorld"/>
/// with the row's class name ONLY when GWorld is available and the row is valid —
/// MainWindow then resolves a representative live instance and runs the path search.
/// </summary>
public class InterestingPropertiesViewModelTests
{
    private static InterestingPropertiesViewModel MakeVm()
        => new(new StubDumpService(), new MockLoggingService());

    private static ScoredPropertyRow Row(string className) => new()
    {
        Match             = new PropertySearchMatch { ClassName = className, PropName = "Health" },
        FinalScore        = 10,
        Category          = PropertyCategory.Stats,
        KeywordHits       = 1,
        ClassBonus        = 0,
        IsUnusualLocation = false,
    };

    [Fact]
    public void LocateRowInGWorld_GWorldAvailable_FiresWithClassName()
    {
        var vm = MakeVm();
        vm.IsGWorldAvailable = true;
        string? captured = null;
        vm.LocateInGWorld += cls => captured = cls;

        vm.LocateRowInGWorldCommand.Execute(Row("BP_PlayerState_C"));

        Assert.Equal("BP_PlayerState_C", captured);
    }

    [Fact]
    public void LocateRowInGWorld_GWorldUnavailable_DoesNotFire()
    {
        // Unlike Value Search (decoupled from the flag), the Interesting Properties
        // 🌍 mirrors Interesting Functions and is gated on IsGWorldAvailable: a
        // property is a class-level definition, so without a resolved GWorld there is
        // nothing to locate against.
        var vm = MakeVm();
        Assert.False(vm.IsGWorldAvailable);   // default
        bool fired = false;
        vm.LocateInGWorld += _ => fired = true;

        vm.LocateRowInGWorldCommand.Execute(Row("BP_PlayerState_C"));

        Assert.False(fired);
    }

    [Fact]
    public void LocateRowInGWorld_NullRow_DoesNotFire()
    {
        var vm = MakeVm();
        vm.IsGWorldAvailable = true;
        bool fired = false;
        vm.LocateInGWorld += _ => fired = true;

        vm.LocateRowInGWorldCommand.Execute(null);

        Assert.False(fired);
    }

    [Fact]
    public void LocateRowInGWorld_EmptyClassName_DoesNotFire()
    {
        var vm = MakeVm();
        vm.IsGWorldAvailable = true;
        bool fired = false;
        vm.LocateInGWorld += _ => fired = true;

        vm.LocateRowInGWorldCommand.Execute(Row(""));

        Assert.False(fired);
    }
}
