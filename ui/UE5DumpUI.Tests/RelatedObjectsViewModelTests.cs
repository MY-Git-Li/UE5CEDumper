using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// VM-level tests for the Related Objects panel's Phase 2 current-target
/// auto-detect (Edel). Verifies the auto-pick-and-load happy path and the
/// graceful-degradation path (weak / unresolved → no auto-load).
/// Uses a stub IDumpService scoped to this file.
/// </summary>
public class RelatedObjectsViewModelTests
{
    private sealed class FakeDumpService : StubDumpService
    {
        public CurrentTargetResult NextTarget { get; set; } = new();
        public string? LastRelatedAddr { get; private set; }
        public int RelatedCallCount { get; private set; }

        public override Task<CurrentTargetResult> DetectCurrentTargetAsync(
            int maxCandidates = 8, CancellationToken ct = default)
            => Task.FromResult(NextTarget);

        public override Task<RelatedObjectsResult> GetRelatedObjectsAsync(
            string addr, int maxResults = 128, CancellationToken ct = default)
        {
            RelatedCallCount++;
            LastRelatedAddr = addr;
            return Task.FromResult(new RelatedObjectsResult
            {
                QueryAddress = addr,
                Related = new List<RelatedObject>
                {
                    new() { Address = addr, Relation = "Self", ClassName = "BP_Enemy_C" },
                },
            });
        }
    }

    private sealed class NoopLogger : ILoggingService
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception ex) { }
        public void Debug(string message) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message) { }
        public void Error(string category, string message, Exception ex) { }
        public void Debug(string category, string message) { }
        public void StartProcessMirror(string processName) { }
        public void StopProcessMirror() { }
    }

    private static RelatedObjectsViewModel CreateVm(FakeDumpService dump)
        => new(dump, new NoopLogger(), new MockPlatformService(System.IO.Path.GetTempPath()));

    [Fact]
    public async Task DetectTarget_Resolved_AutoLoadsTopCandidate()
    {
        var dump = new FakeDumpService
        {
            NextTarget = new CurrentTargetResult
            {
                Resolved = true,
                PlayerPawn = "0x7FF6BB00",
                Note = "Detected target: Enemy_0 (BP_Enemy_C) — score 95 via CurrentTarget.",
                Candidates = new List<TargetCandidate>
                {
                    new() { Address = "0x7FF6CC00", Name = "Enemy_0", ClassName = "BP_Enemy_C",
                            Score = 95, FieldName = "CurrentTarget" },
                    new() { Address = "0x7FF6DD00", Name = "Ally_0", ClassName = "BP_Ally_C",
                            Score = 45, FieldName = "FocusActor" },
                },
            },
        };
        var vm = CreateVm(dump);

        await vm.DetectTargetCommand.ExecuteAsync(null);

        Assert.True(vm.HasCandidates);
        Assert.Equal(2, vm.TargetCandidates.Count);
        Assert.Same(vm.TargetCandidates[0], vm.SelectedCandidate);   // auto-pick = top
        // The top candidate was loaded into the related graph.
        Assert.Equal(1, dump.RelatedCallCount);
        Assert.Equal("0x7FF6CC00", dump.LastRelatedAddr);
        Assert.Equal("0x7FF6CC00", vm.TargetAddress);
        Assert.Single(vm.Related);
    }

    [Fact]
    public async Task DetectTarget_Unresolved_ShowsNoteAndDoesNotAutoLoad()
    {
        var dump = new FakeDumpService
        {
            NextTarget = new CurrentTargetResult
            {
                Resolved = false,
                Note = "Player resolved but no target-like reference found.",
                Candidates = new List<TargetCandidate>(),
            },
        };
        var vm = CreateVm(dump);

        await vm.DetectTargetCommand.ExecuteAsync(null);

        Assert.False(vm.HasCandidates);
        Assert.Empty(vm.TargetCandidates);
        Assert.Equal(0, dump.RelatedCallCount);          // nothing auto-loaded
        Assert.Equal("", vm.TargetAddress);
        Assert.StartsWith("Player resolved", vm.StatusText);
    }

    [Fact]
    public async Task DetectTarget_WeakCandidates_ShownButNotAutoLoaded()
    {
        var dump = new FakeDumpService
        {
            NextTarget = new CurrentTargetResult
            {
                Resolved = false,   // weak: candidates exist but top score not positive
                Note = "Only weak candidates (no clear target field). Showing best guesses — verify before trusting.",
                Candidates = new List<TargetCandidate>
                {
                    new() { Address = "0x7FF6EE00", Name = "Widget_0", ClassName = "UUserWidget",
                            Score = -10, FieldName = "TargetWidget" },
                },
            },
        };
        var vm = CreateVm(dump);

        await vm.DetectTargetCommand.ExecuteAsync(null);

        Assert.True(vm.HasCandidates);                   // shown for manual pick
        Assert.Single(vm.TargetCandidates);
        Assert.Equal(0, dump.RelatedCallCount);          // but NOT auto-loaded
        Assert.Null(vm.SelectedCandidate);
    }
}
