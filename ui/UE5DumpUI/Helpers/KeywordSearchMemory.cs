using System;
using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Threading;

namespace UE5DumpUI.Helpers;

/// <summary>
/// Reusable per-search-box keyword memory — the Live Walker field-search pattern
/// packaged so every keyword/filter box in the app can reuse it with ~4 lines of
/// ViewModel wiring instead of re-implementing the debounce, gating and LRU each time.
///
/// It keeps an LRU list (<see cref="History"/>) of the keywords the user has actually
/// typed AND that produced matches, ordered most-recently-used first, capped and
/// deduplicated by <see cref="SearchKeywordHistory"/> (longest-valid wins). Bind that
/// list to an <c>AutoCompleteBox.ItemsSource</c> so past keywords resurface as
/// suggestions; the box's <c>Text</c> stays two-way bound to the VM's filter property.
///
/// <para>ViewModel usage (mirrors LiveWalkerViewModel):</para>
/// <code>
///   private readonly KeywordSearchMemory _filterMemory;
///   public ObservableCollection&lt;string&gt; FilterHistory =&gt; _filterMemory.History;
///   // ctor:
///   _filterMemory = new KeywordSearchMemory(() =&gt; (FilterText, Results.Count &gt; 0));
///   // in OnFilterTextChanged(value), AFTER re-running the filter:
///   _filterMemory.Schedule(value);
///   // before clearing the box on a tab switch / navigation, or in Dispose:
///   _filterMemory.Flush();  // (or Dispose())
/// </code>
///
/// <para>AXAML: swap the plain <c>&lt;TextBox&gt;</c> for an
/// <c>&lt;AutoCompleteBox&gt;</c> with
/// <c>Text="{Binding FilterText, Mode=TwoWay}"</c>,
/// <c>ItemsSource="{Binding FilterHistory}"</c> and <c>FilterMode="Contains"</c>
/// (TextBox <c>Watermark</c> becomes AutoCompleteBox <c>PlaceholderText</c>).</para>
///
/// AOT-safe: plain CommunityToolkit-compatible types, no reflection.
/// </summary>
public sealed class KeywordSearchMemory : IDisposable
{
    /// <summary>Default quiet period (ms) before a settled keyword is remembered.
    /// Matches LiveWalkerViewModel's field-search debounce.</summary>
    public const int DefaultDelayMs = 700;

    /// <summary>Remembered keywords, most-recently-used first. Bind this to the
    /// box's <c>AutoCompleteBox.ItemsSource</c>.</summary>
    public ObservableCollection<string> History { get; } = new();

    private readonly Func<(string? text, bool hasMatches)> _probe;
    private readonly int _delayMs;
    private Timer? _debounce;
    private bool _disposed;

    /// <param name="probe">Returns the box's CURRENT text and whether it currently
    /// yields matches. Evaluated on the UI thread when the debounce fires, so it may
    /// read ViewModel state (e.g. a Results.Count). Only keywords with matches are kept.</param>
    /// <param name="delayMs">Quiet period before a settled keyword is remembered.</param>
    public KeywordSearchMemory(Func<(string?, bool)> probe, int delayMs = DefaultDelayMs)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _delayMs = delayMs;
    }

    /// <summary>
    /// Call from the box's OnTextChanged handler. Schedules a "remember" pass once
    /// typing settles, so only the keyword the user pauses on is kept — the
    /// intermediate prefixes typed on the way to it are never remembered. Text below
    /// <see cref="SearchKeywordHistory.MinLength"/> cancels any pending pass and
    /// schedules nothing.
    /// </summary>
    public void Schedule(string? currentText)
    {
        if (_disposed) return;
        _debounce?.Dispose();
        _debounce = null;
        if ((currentText?.Trim().Length ?? 0) < SearchKeywordHistory.MinLength) return;
        _debounce = new Timer(
            _ => Dispatcher.UIThread.Post(CommitSettled),
            null, _delayMs, Timeout.Infinite);
    }

    /// <summary>
    /// Commit the current keyword immediately (probe now, no debounce). Use this from
    /// callers whose match count only becomes known ASYNCHRONOUSLY — e.g. a server-side
    /// filter that returns its result count after a pipe round-trip: call this once the
    /// result lands, instead of <see cref="Schedule"/>, so the probe never races the
    /// async reload and reads a stale count.
    /// </summary>
    public void Commit()
    {
        if (_disposed) return;
        _debounce?.Dispose();
        _debounce = null;
        CommitSettled();
    }

    /// <summary>
    /// Commit any pending (debounced) keyword immediately — call this BEFORE clearing
    /// the box on a tab switch / navigation, so a quick type-then-switch still remembers
    /// the keyword (a switch is itself a strong "settled" signal). Alias of
    /// <see cref="Commit"/>.
    /// </summary>
    public void Flush() => Commit();

    private void CommitSettled()
    {
        if (_disposed) return;
        var (text, hasMatches) = _probe();
        var k = text?.Trim() ?? "";
        if (k.Length < SearchKeywordHistory.MinLength || !hasMatches) return;
        SearchKeywordHistory.Remember(History, k);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce?.Dispose();
        _debounce = null;
    }
}
