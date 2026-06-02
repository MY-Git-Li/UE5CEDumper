using System;
using Avalonia;
using Avalonia.Controls;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// Position + size of the window the last time it was in
    /// <see cref="WindowState.Normal"/>. When the OS reports a
    /// transition Normal → Maximized → Normal, Avalonia 12 on Windows
    /// can land the restored window stretched across multiple monitors
    /// (especially common on dual-monitor setups) or at the wrong
    /// position. Snapshotting before the maximize and re-applying on
    /// restore puts the window back on the monitor it started from at
    /// its original size.
    /// </summary>
    private PixelPoint? _normalPosition;
    private double _normalWidth;
    private double _normalHeight;
    private WindowState _previousWindowState = WindowState.Normal;

    // Deferred-commit snapshot state. On Windows + Avalonia 12 the
    // property-change order during a maximize transition is
    // (Width/Height first, WindowState second), so reading
    // WindowState inside the Width/Height handler still sees Normal
    // when the values are already the maximized dimensions. Capturing
    // synchronously into _normalWidth/_normalHeight at that point
    // poisons the snapshot — the next restore then lands at
    // near-maximized size. Defer the commit one dispatcher tick at
    // Background priority so any concurrent WindowState change in the
    // same Win32 message has been propagated; the commit re-checks
    // WindowState and abandons when it has flipped to non-Normal.
    private double _pendingWidth;
    private double _pendingHeight;
    private PixelPoint _pendingPosition;
    private bool _snapshotCommitScheduled;

    public MainWindow()
    {
        InitializeComponent();
        // Audit fixes #16 / #17: dispose timer-owning child VMs when the
        // window closes, so background DispatcherTimers and Threading.Timer
        // callbacks don't fire post-close on torn-down state.
        Closed += OnClosed;
        // Position isn't an AvaloniaProperty in 12 — listen via the
        // explicit event instead. Width / Height are AvaloniaProperty
        // and flow through OnPropertyChanged.
        PositionChanged += OnPositionChanged;
        // Seed the snapshot with the XAML-declared default so the very
        // first restore (without a prior maximize) still has something
        // sane to fall back to.
        _normalWidth = Width;
        _normalHeight = Height;
        _pendingWidth = Width;
        _pendingHeight = Height;
        _pendingPosition = Position;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is IDisposable d) d.Dispose();
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _pendingPosition = Position;
            ScheduleSnapshotCommit();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            var newState = change.GetNewValue<WindowState>();
            HandleWindowStateTransition(_previousWindowState, newState);
            _previousWindowState = newState;
        }
        else if (change.Property == WidthProperty || change.Property == HeightProperty)
        {
            // Stash, but commit later (see the field comments). Reading
            // WindowState here is unreliable on the to-maximized
            // transition.
            if (WindowState == WindowState.Normal)
            {
                _pendingWidth = Width;
                _pendingHeight = Height;
                ScheduleSnapshotCommit();
            }
        }
    }

    /// <summary>
    /// Queue a deferred snapshot commit on the dispatcher at
    /// Background priority. Coalesced: scheduling while a commit is
    /// already pending is a no-op.
    /// </summary>
    private void ScheduleSnapshotCommit()
    {
        if (_snapshotCommitScheduled)
        {
            return;
        }
        _snapshotCommitScheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            CommitSnapshot,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Apply the pending snapshot — but only if the window is still in
    /// <see cref="WindowState.Normal"/> at the time of the commit. If
    /// a WindowState change snuck in during the same dispatcher tick
    /// (the bug we're guarding against), the state will have flipped
    /// by now and the pending values are the maximized dimensions
    /// we don't want.
    /// </summary>
    private void CommitSnapshot()
    {
        _snapshotCommitScheduled = false;
        if (WindowState != WindowState.Normal)
        {
            return;
        }
        if (_pendingWidth > 0)
        {
            _normalWidth = _pendingWidth;
        }
        if (_pendingHeight > 0)
        {
            _normalHeight = _pendingHeight;
        }
        _normalPosition = _pendingPosition;
    }

    private void HandleWindowStateTransition(WindowState oldState, WindowState newState)
    {
        // Leaving Normal: snapshot was already kept fresh by the
        // property-change branch above. Nothing to do here.
        if (oldState == WindowState.Normal && newState != WindowState.Normal)
        {
            return;
        }
        // Returning to Normal: re-apply the snapshot. Without this,
        // restoring from Maximized on a multi-monitor setup can land
        // the window straddling both screens or at the wrong position.
        if (newState == WindowState.Normal && oldState != WindowState.Normal)
        {
            if (_normalPosition is { } pos)
            {
                Position = pos;
            }
            if (_normalWidth > 0)
            {
                Width = _normalWidth;
            }
            if (_normalHeight > 0)
            {
                Height = _normalHeight;
            }
        }
    }

    /// <summary>
    /// Per-tab activation work:
    /// 1. Stop Live Walker auto-refresh when the user switches away
    ///    from Live Walker (no point polling while viewing other tabs).
    /// 2. Refresh AOBMaker availability for tabs whose actions depend
    ///    on the CE plugin (LiveWalker, InterestingFunctions, Pointers).
    ///    The re-check is fire-and-forget (cooldown-throttled where the
    ///    panel VM supports it) so rapid tab switches don't stack 2s
    ///    pipe-connect timeouts -- keeps the grayout state honest when
    ///    the user starts/stops CE without blocking the UI thread.
    /// </summary>
    /// <remarks>
    /// Routing is keyed on the selected <see cref="TabItem.Tag"/>, NOT its
    /// position. Index-based routing here has silently drifted twice as tabs
    /// were inserted into MainWindow.axaml (the Pointers re-check pointed at
    /// the wrong index, and the autorefresh-stop assumed LiveWalker == 0).
    /// The Tag travels with its TabItem, so reordering can't break this.
    /// SelectedItem is the outer TabControl's selection, so inner
    /// SelectionChanged events bubbling from child grids are harmless.
    /// </remarks>
    private void MainTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs) return;
        if (DataContext is not MainWindowViewModel vm) return;

        var tag = (tabs.SelectedItem as TabItem)?.Tag as string;

        // Stop Live Walker auto-refresh when switching away from it.
        if (tag != "LiveWalker" && vm.LiveWalker.IsAutoRefreshing)
        {
            vm.LiveWalker.StopAutoRefreshTimer();
        }

        // Opening any experimental tab while enabled permanently commits the
        // opt-in: the System-tab checkbox can no longer be unticked from here
        // on. The gate persists the lock; LockExperimental is idempotent and a
        // no-op when the feature isn't enabled.
        if (tag is "Snapshot" or "SpcQuery" or "ClassPivot")
            vm.LockExperimental();

        // Refresh AOBMaker state for tabs whose toolbar / per-row buttons
        // depend on it. LiveWalker / InterestingFunctions throttle via
        // TryCheckAobMaker; PointerPanel has no cooldown wrapper, so call
        // its async check directly (fire-and-forget, as before).
        switch (tag)
        {
            case "LiveWalker": vm.LiveWalker.TryCheckAobMaker(); break;
            case "InterestingFunctions": vm.InterestingFunctions.TryCheckAobMaker(); break;
            case "Pointers": _ = vm.Pointers.CheckAobMakerAsync(); break;
            // SPC / Pivot read the snapshot list saved by the Snapshot tab —
            // refresh on activation so a just-captured snapshot shows up.
            case "SpcQuery": _ = vm.Spc?.RefreshCommand.ExecuteAsync(null); break;
            case "ClassPivot": _ = vm.Pivot?.RefreshCommand.ExecuteAsync(null); break;
        }
    }
}
