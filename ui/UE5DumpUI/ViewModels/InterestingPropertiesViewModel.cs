using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.ViewModels;

/// <summary>
/// ViewModel for the Interesting Properties Finder panel (B' / round 1).
///
/// Pragmatic loading strategy: rather than add a new DLL command, the VM
/// issues N parallel <c>search_properties</c> calls — one per seed
/// keyword from <see cref="PropertyScoringTable.SeedQueries"/>. Each
/// call returns up to <see cref="PerQueryLimit"/> matches; results are
/// deduped client-side by (DefiningClassName, PropName, PropOffset),
/// scored, sorted, and filtered.
///
/// Trade-offs:
///   - Captures the ~80-95% of high-relevance hits without needing
///     a new pipe command (saves a DLL build + protocol change for
///     round 1).
///   - Misses long-tail keywords not in the seed list. Iterate by
///     adding to <see cref="PropertyScoringTable.SeedQueries"/>.
///
/// The Unusual Location signal (LocalPlayer / GameViewportClient / HUD
/// / CheatManager) is the key reason this tab exists separate from
/// regular Property Search — it surfaces cheat-relevant fields that
/// live OUTSIDE the conventional Character/Pawn/PlayerState containers.
/// </summary>
public partial class InterestingPropertiesViewModel : ViewModelBase
{
    private readonly IDumpService _dump;
    private readonly ILoggingService _log;

    /// <summary>How many matches each seed-query call asks for. Default
    /// 200 mirrors the existing PropertySearch tab's limit. Total
    /// pre-dedup is up to SeedQueries.Length * PerQueryLimit which is
    /// ~6000 entries — typically dedupes down to a few hundred to a
    /// few thousand.</summary>
    public const int PerQueryLimit = 200;

    private List<ScoredPropertyRow> _allRows = new();

    [ObservableProperty] private bool   _gameOnly = true;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private PropertyCategory? _categoryFilter; // null = All
    [ObservableProperty] private bool   _unusualOnly;  // ⚠ rows only
    [ObservableProperty] private bool   _showAll;      // bypass threshold
    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusText = "Click Load to scan for interesting properties";
    [ObservableProperty] private ObservableCollection<ScoredPropertyRow> _results = new();
    [ObservableProperty] private ScoredPropertyRow? _selectedResult;

    // Category dropdown values (in display order).
    public IReadOnlyList<PropertyCategory?> CategoryOptions { get; } = new PropertyCategory?[]
    {
        null,                              // "All"
        PropertyCategory.Stats,
        PropertyCategory.Combat,
        PropertyCategory.Resources,
        PropertyCategory.Movement,
        PropertyCategory.Utility,
        PropertyCategory.Other,
    };

    // ------------------------------------------------------------------
    // Cross-tab navigation events. MainWindow routes these to the right
    // destination. Single event for now (the cheat workflow lives in
    // Live Walker on a real instance); add Class Struct fallback if a
    // user reports finding zero live instances of a class repeatedly.
    // ------------------------------------------------------------------

    public event Action<string, string>? NavigateToProperty;
    public event Action<string>? RequestCopyText;

    public InterestingPropertiesViewModel(IDumpService dump, ILoggingService log)
    {
        _dump = dump;
        _log  = log;
    }

    partial void OnFilterTextChanged(string value)                  => ApplyFilter();
    partial void OnCategoryFilterChanged(PropertyCategory? value)   => ApplyFilter();
    partial void OnUnusualOnlyChanged(bool value)                   => ApplyFilter();
    partial void OnShowAllChanged(bool value)                       => ApplyFilter();

    /// <summary>
    /// Send ONE batched search_properties_batch call carrying all
    /// SeedQueries. DLL walks GObjects + class fields once and checks
    /// every property against every keyword, returning per-query result
    /// envelopes. Wall-time drops from ~42s (legacy sequential pipe
    /// loop, build 681-682) to ~1.5s for a 4400-class game.
    ///
    /// Why progress is now one-shot: the speedup is so large that the
    /// per-keyword loop disappears entirely on the wire. Status text
    /// just shows the single round-trip + scoring phase.
    /// </summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            ClearError();
            IsLoading = true;

            var queries = PropertyScoringTable.SeedQueries;
            StatusText = $"Walking GObjects with {queries.Length} keyword queries (single batched call)...";

            var batch = await _dump.SearchPropertiesBatchAsync(
                queries, types: null, gameOnly: GameOnly, limitPerQuery: PerQueryLimit);

            StatusText = $"Scoring + dedup ({batch.Total:N0} raw hits across " +
                         $"{batch.ScannedClasses:N0} classes)...";

            // Flatten the per-query envelopes into a single list for the
            // existing dedup/score loop; that loop dedupes by
            // (DefiningClassName, PropName, PropOffset) so per-query
            // duplicates (same field matched by multiple keywords)
            // collapse into one ScoredPropertyRow.
            var results = new PropertySearchResult[batch.PerQuery.Count];
            for (int i = 0; i < batch.PerQuery.Count; i++)
            {
                var env = batch.PerQuery[i];
                results[i] = new PropertySearchResult
                {
                    Total          = env.MatchCount,
                    ScannedClasses = batch.ScannedClasses,
                    ScannedObjects = batch.ScannedObjects,
                    Results        = env.Results,
                };
            }

            // Score + dedup happens off the UI thread; on huge games the
            // pre-dedup set can hit ~6000 entries.
            _allRows = await Task.Run(() =>
            {
                // Dedup key: defining class + prop name + offset. Keep first
                // occurrence; the seed-query order is alphabetical-ish and
                // not semantically meaningful, so a stable first-win policy
                // is fine.
                var dedup = new Dictionary<(string, string, int), ScoredPropertyRow>();
                int totalRaw = 0;
                foreach (var r in results)
                {
                    if (r?.Results == null) continue;
                    totalRaw += r.Results.Count;
                    foreach (var m in r.Results)
                    {
                        var key = (
                            string.IsNullOrEmpty(m.DefiningClassName) ? m.ClassName : m.DefiningClassName,
                            m.PropName,
                            m.PropOffset);
                        if (dedup.ContainsKey(key)) continue;

                        var s = PropertyScoringTable.Score(m);
                        dedup[key] = new ScoredPropertyRow
                        {
                            Match              = m,
                            FinalScore         = s.FinalScore,
                            Category           = s.Category,
                            KeywordHits        = s.KeywordHits,
                            ClassBonus         = s.ClassBonus,
                            IsUnusualLocation  = s.IsUnusualLocation,
                        };
                    }
                }

                var rows = new List<ScoredPropertyRow>(dedup.Values);
                rows.Sort((a, b) =>
                {
                    // Unusual hits float to the top within equal-score
                    // bands, then by score, then by class+prop for stable
                    // ordering.
                    int cmp = b.FinalScore.CompareTo(a.FinalScore);
                    if (cmp != 0) return cmp;
                    cmp = b.IsUnusualLocation.CompareTo(a.IsUnusualLocation);
                    if (cmp != 0) return cmp;
                    cmp = string.Compare(a.ClassName, b.ClassName, StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                    return string.Compare(a.PropName, b.PropName, StringComparison.Ordinal);
                });

                return rows;
            });

            ApplyFilter();

            int interesting = 0;
            int unusual = 0;
            foreach (var r in _allRows)
            {
                if (r.FinalScore >= PropertyScoringTable.InterestingThreshold) interesting++;
                if (r.IsUnusualLocation) unusual++;
            }

            StatusText =
                $"{_allRows.Count:N0} unique properties  " +
                $"(threshold {PropertyScoringTable.InterestingThreshold}+: {interesting:N0}, " +
                $"⚠ unusual: {unusual:N0})";
            _log.Info($"InterestingProperties load: queries={queries.Length} " +
                      $"unique={_allRows.Count} interesting={interesting} unusual={unusual} " +
                      $"(gameOnly={GameOnly})");
        }
        catch (Exception ex)
        {
            SetError(ex);
            StatusText = "Load failed";
            _log.Error("InterestingProperties load failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Rebuild <see cref="Results"/> from <see cref="_allRows"/> applying
    /// name + category + Unusual + threshold filters. Order preserved
    /// from the pre-sorted full list.
    /// </summary>
    private void ApplyFilter()
    {
        Results.Clear();
        if (_allRows.Count == 0) return;

        var nameFilter = (FilterText ?? "").Trim();
        var hasName    = nameFilter.Length > 0;
        var cat        = CategoryFilter;
        var threshold  = ShowAll ? int.MinValue : PropertyScoringTable.InterestingThreshold;
        var unusualGate = UnusualOnly;

        foreach (var row in _allRows)
        {
            if (row.FinalScore < threshold) continue;
            if (cat.HasValue && row.Category != cat.Value) continue;
            if (unusualGate && !row.IsUnusualLocation) continue;
            if (hasName)
            {
                if (!row.PropName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
                    && !row.ClassName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            Results.Add(row);
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FilterText     = "";
        CategoryFilter = null;
        UnusualOnly    = false;
        ShowAll        = false;
    }

    /// <summary>Per-row action: fire navigate event so MainWindow can
    /// open the property's owning class in Live Walker (or fall back
    /// to Class Struct if no live instance).</summary>
    [RelayCommand]
    private void OpenInLiveWalker(ScoredPropertyRow? row)
    {
        if (row == null) return;
        NavigateToProperty?.Invoke(row.ClassName, row.PropName);
    }

    /// <summary>Per-row action: copy bare property name to clipboard.</summary>
    [RelayCommand]
    private void CopyPropertyName(ScoredPropertyRow? row)
    {
        if (row == null) return;
        if (string.IsNullOrEmpty(row.PropName)) return;
        RequestCopyText?.Invoke(row.PropName);
        StatusText = $"Copied property name: {row.PropName}";
    }
}
