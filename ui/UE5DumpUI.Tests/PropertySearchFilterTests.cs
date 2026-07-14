using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Audit #3 M10: the client-side ResultFilter box must follow the shared keyword-box
/// MUST-rule — space = AND (term-level AND, field-level OR) via
/// ObjectTreeFilter.MatchesAllTerms, and per-keyword memory via KeywordSearchMemory.
/// Before the fix it matched the whole string with one Contains per field, so a
/// two-word query like "max health" (never a literal substring of any single field)
/// found nothing.
/// </summary>
public class PropertySearchFilterTests
{
    private sealed class SearchDump : StubDumpService
    {
        public List<PropertySearchMatch> Next { get; set; } = new();
        public override Task<PropertySearchResult> SearchPropertiesAsync(
            string query, string[]? types = null, bool gameOnly = true, bool deep = false,
            int limit = 200, CancellationToken ct = default)
            => Task.FromResult(new PropertySearchResult { Results = Next });
    }

    private sealed class NoopLog : ILoggingService
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

    private static async Task<PropertySearchViewModel> SearchedVm(params PropertySearchMatch[] rows)
    {
        var dump = new SearchDump { Next = new List<PropertySearchMatch>(rows) };
        var vm = new PropertySearchViewModel(dump, new NoopLog()) { SearchQuery = "x" };
        await vm.SearchCommand.ExecuteAsync(null);   // populates _allResults + Results
        return vm;
    }

    private static PropertySearchMatch Row(string cls, string prop, string type = "FloatProperty") =>
        new() { ClassName = cls, DefiningClassName = cls, PropName = prop, PropType = type };

    [Fact]
    public async Task ResultFilter_SpaceIsAnd_WithFieldLevelOr()
    {
        var vm = await SearchedVm(
            Row("BP_PlayerState_C", "MaxHealth"),        // "max"+"health" both in PropName
            Row("BP_PlayerState_C", "CurrentHealth"),    // only "health"
            Row("BP_MaxCombo_C",    "Value", "IntProperty")); // only "max" (in ClassName)

        vm.ResultFilter = "max health";
        vm.ApplyResultFilter();   // deterministic (bypass the 150 ms debounce)

        // Term-level AND + field-level OR: only MaxHealth has BOTH terms (each matching
        // some field). The old whole-string Contains found the literal "max health" in
        // no field → zero rows.
        Assert.Single(vm.Results);
        Assert.Equal("MaxHealth", vm.Results[0].PropName);
        vm.Dispose();
    }

    [Fact]
    public async Task ResultFilter_TermMatchesClassOrType_NotJustPropName()
    {
        var vm = await SearchedVm(
            Row("BP_Enemy_C", "Awareness", "FloatProperty"),
            Row("BP_Ally_C",  "Health",    "IntProperty"));

        // "enemy" matches only the class, "float" matches only the type — both on row 1.
        vm.ResultFilter = "enemy float";
        vm.ApplyResultFilter();

        Assert.Single(vm.Results);
        Assert.Equal("Awareness", vm.Results[0].PropName);
        vm.Dispose();
    }

    [Fact]
    public async Task ResultFilter_Empty_ShowsAll()
    {
        var vm = await SearchedVm(Row("A", "One"), Row("B", "Two"));
        vm.ResultFilter = "";
        vm.ApplyResultFilter();
        Assert.Equal(2, vm.Results.Count);
        vm.Dispose();
    }

    [Fact]
    public void ResultFilterHistory_IsExposed_ForAutoCompleteBinding()
    {
        var vm = new PropertySearchViewModel(new SearchDump(), new NoopLog());
        Assert.NotNull(vm.ResultFilterHistory);   // bound to the AutoCompleteBox ItemsSource
        vm.Dispose();
    }
}
