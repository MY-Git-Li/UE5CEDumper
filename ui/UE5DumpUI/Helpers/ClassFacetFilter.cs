using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UE5DumpUI.Helpers;

/// <summary>
/// Reusable, client-side "class noise" filter for result-list panels whose FULL
/// result set lives in a C# collection (Instance Finder, Interesting Functions /
/// Properties, Property Search).
///
/// It builds a Top-N "class → hit count" histogram from the current full result
/// set and lets the user tick classes to HIDE (UI widgets, sound, system
/// components, …). Ticking re-filters the list IMMEDIATELY — no Apply button —
/// via the <c>onChanged</c> callback the owner supplies: the owner re-runs its
/// existing ApplyFilter pass, which calls <see cref="IsExcluded"/> per row.
///
/// The exclusion is purely a display filter: the owner keeps its full raw list
/// intact, so unticking (or <see cref="ClearCommand"/>) restores hidden rows
/// instantly — there is no risk of losing real data. The excluded set is
/// preserved across <see cref="Rebuild"/> calls so a class the user marked as
/// noise stays hidden across re-searches in the same session.
///
/// This is the cheap CLIENT-SIDE sibling of the SPC / Diff / Pivot server-side
/// picker (NoiseRowVm + DenylistScope), which re-runs a DLL query and persists
/// per game. Here the full set is already in memory, so the histogram + filter
/// are instant and need zero pipe traffic. AOT-safe: plain LINQ + an
/// ObservableCollection, no reflection.
/// </summary>
public sealed partial class ClassFacetFilter : ObservableObject
{
    private readonly Action _onChanged;
    private readonly HashSet<string> _excluded = new(StringComparer.Ordinal);

    // Guards bulk Picked writes (Rebuild / Clear) from re-entrantly firing the
    // per-row toggle handler (which would re-run the owner's filter once per row).
    private bool _suppress;

    /// <summary>Top-N class rows shown in the picker, each with a "Hide" tick.</summary>
    public ObservableCollection<ClassFacetRow> Facets { get; } = new();

    /// <summary>True when there is at least one facet row (gates the picker button).</summary>
    [ObservableProperty] private bool _hasFacets;

    /// <summary>How many classes are currently hidden (drives the button badge).</summary>
    [ObservableProperty] private int _excludedCount;

    /// <summary>Total distinct classes in the source set (shown in the summary).</summary>
    [ObservableProperty] private int _distinctCount;

    /// <summary>True when the source list was capped / truncated, so the counts
    /// are a partial lower bound (e.g. Instance Finder hit its result cap).</summary>
    [ObservableProperty] private bool _countsPartial;

    /// <summary>One-line summary line shown under the picker.</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>True when any class is hidden — drives the button badge visibility.</summary>
    public bool AnyExcluded => ExcludedCount > 0;

    /// <summary>Whether the picker button should be openable. Stays true while
    /// anything is hidden EVEN IF the current result has no facets (e.g. a 0-row
    /// search, or a search whose every match is in a hidden class) — otherwise a
    /// stale exclusion would be stuck behind a disabled button with no way to
    /// reach <see cref="ClearCommand"/>.</summary>
    public bool CanOpen => HasFacets || AnyExcluded;

    partial void OnHasFacetsChanged(bool value) => OnPropertyChanged(nameof(CanOpen));

    partial void OnExcludedCountChanged(int value)
    {
        OnPropertyChanged(nameof(AnyExcluded));
        OnPropertyChanged(nameof(CanOpen));
    }

    /// <param name="onChanged">Invoked whenever the exclusion set changes; the
    /// owner re-runs its filter pass (which consults <see cref="IsExcluded"/>).</param>
    public ClassFacetFilter(Action onChanged)
        => _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

    /// <summary>True when <paramref name="className"/> is currently hidden. Call
    /// from the owner's ApplyFilter to skip excluded rows.</summary>
    public bool IsExcluded(string? className)
        => _excluded.Count > 0 && !string.IsNullOrEmpty(className) && _excluded.Contains(className);

    /// <summary>Snapshot of the currently-hidden class names. For SERVER-SIDE
    /// owners (Value Search): pass this to the DLL window query as
    /// <c>exclude_classes</c> so the histogram-driven exclusion is applied over
    /// the full session, not just the loaded page. A fresh array each call so an
    /// in-flight async query holds a stable copy.</summary>
    public IReadOnlyList<string> ExcludedClasses
        => _excluded.Count == 0 ? System.Array.Empty<string>() : _excluded.ToArray();

    /// <summary>
    /// Rebuild the Top-N histogram from the current FULL result set. Preserves
    /// the excluded set (re-ticks rebuilt rows that are still hidden). Does NOT
    /// fire <c>onChanged</c> — the owner is expected to (re)apply its own filter
    /// right after, or is mid-load.
    /// </summary>
    /// <param name="classNames">One class name per row in the FULL result set
    /// (duplicates expected — they are what gets counted).</param>
    /// <param name="topN">How many top classes to show (default 40).</param>
    /// <param name="countsPartial">Source list was capped (counts are a lower bound).</param>
    public void Rebuild(IEnumerable<string?> classNames, int topN = 40, bool countsPartial = false)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cn in classNames)
        {
            if (string.IsNullOrEmpty(cn)) continue;
            counts[cn] = counts.TryGetValue(cn, out var n) ? n + 1 : 1;
        }

        var top = counts
            .OrderByDescending(k => k.Value)
            .ThenBy(k => k.Key, StringComparer.Ordinal)
            .Take(topN)
            .Select(kv => (kv.Key, kv.Value));
        PopulateFacets(top, counts.Count, countsPartial);
    }

    /// <summary>
    /// Rebuild from a PRE-COUNTED histogram (server-side path: the DLL owns the
    /// full windowed candidate set, so it computes the class counts over the
    /// whole session and sends back the Top-N). <paramref name="counts"/> is
    /// expected already sorted (count desc); <paramref name="distinctTotal"/> is
    /// the true number of distinct classes (≥ counts.Count when the server
    /// capped). Preserves the excluded set like <see cref="Rebuild"/>.
    /// </summary>
    public void RebuildFromCounts(IEnumerable<(string className, int count)> counts,
                                  int distinctTotal, bool countsPartial = false)
    {
        var list = counts.Where(c => !string.IsNullOrEmpty(c.className)).ToList();
        PopulateFacets(list, Math.Max(distinctTotal, list.Count), countsPartial);
    }

    // Shared facet (re)population. `top` is the already-sorted, already-capped
    // (className, count) sequence; `distinctTotal` is the full distinct-class
    // count (may exceed top.Count when capped).
    private void PopulateFacets(IEnumerable<(string className, int count)> top,
                                int distinctTotal, bool countsPartial)
    {
        _suppress = true;
        Facets.Clear();
        foreach (var (className, count) in top)
        {
            Facets.Add(new ClassFacetRow(this)
            {
                ClassName = className,
                HitCount  = count,
                Picked    = _excluded.Contains(className),
            });
        }
        _suppress = false;

        DistinctCount = distinctTotal;
        CountsPartial = countsPartial;
        HasFacets     = Facets.Count > 0;
        ExcludedCount = _excluded.Count;
        UpdateSummary();
    }

    // Called by a row when its Hide checkbox toggles.
    internal void NotifyRowToggled(ClassFacetRow row)
    {
        if (_suppress || string.IsNullOrEmpty(row.ClassName)) return;
        if (row.Picked) _excluded.Add(row.ClassName);
        else _excluded.Remove(row.ClassName);
        ExcludedCount = _excluded.Count;
        UpdateSummary();
        _onChanged();
    }

    /// <summary>Clear every exclusion (untick all + restore hidden rows), then
    /// re-filter. The "show everything again" path.</summary>
    [RelayCommand]
    private void Clear()
    {
        if (_excluded.Count == 0) return;
        _excluded.Clear();
        _suppress = true;
        foreach (var r in Facets) r.Picked = false;
        _suppress = false;
        ExcludedCount = 0;
        UpdateSummary();
        _onChanged();
    }

    /// <summary>Drop the histogram AND exclusions entirely (e.g. when the owner
    /// switches to a different result context that the facets don't describe —
    /// the Instance Finder reverse-address lookup). Does NOT fire
    /// <c>onChanged</c>: the caller is rebuilding its list itself.</summary>
    public void Reset()
    {
        _excluded.Clear();
        _suppress = true;
        Facets.Clear();
        _suppress = false;
        HasFacets     = false;
        ExcludedCount = 0;
        DistinctCount = 0;
        CountsPartial = false;
        Summary       = "";
    }

    private void UpdateSummary()
    {
        if (!HasFacets) { Summary = ""; return; }
        Summary = ExcludedCount > 0
            ? $"{ExcludedCount} hidden · {DistinctCount} distinct class(es)"
            : $"{DistinctCount} distinct class(es)";
    }
}

/// <summary>One Top-N row in a <see cref="ClassFacetFilter"/> picker. Toggling
/// <see cref="Picked"/> hides/shows that class in the owning list immediately.</summary>
public sealed partial class ClassFacetRow : ObservableObject
{
    private readonly ClassFacetFilter _owner;

    public ClassFacetRow(ClassFacetFilter owner) => _owner = owner;

    public string ClassName { get; init; } = "";
    public int    HitCount  { get; init; }

    /// <summary>"ClassName  (1,234)" — the picker row label.</summary>
    public string Display => $"{ClassName}  ({HitCount:N0})";

    /// <summary>User's "hide this class" tick. Toggling re-filters live.</summary>
    [ObservableProperty] private bool _picked;

    partial void OnPickedChanged(bool value) => _owner.NotifyRowToggled(this);
}
