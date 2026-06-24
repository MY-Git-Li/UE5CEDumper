using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using UE5DumpUI.Services;

namespace UE5DumpUI.Views;

/// <summary>
/// Base class for the resizable, <see cref="WindowStartupLocation.CenterOwner"/> code-behind
/// dialogs (PropertyXrefDialog / FunctionPropsDialog / ObjectInstancePickerDialog). It fixes
/// the same maximize→restore placement bug the main window already handles: on Avalonia 12
/// (Windows), restoring a maximized dialog can land it stretched across monitors or at the
/// wrong spot instead of where it was before the maximize. We snapshot the last NORMAL
/// geometry and re-apply it on the way back to Normal.
///
/// The snapshot/decision logic lives in the pure, unit-tested
/// <see cref="WindowRestoreState"/>; this class only wires it to the live window's events.
/// Seeding happens on <see cref="Window.Opened"/> (CenterOwner sets the position only after
/// the window is shown, so the ctor can't read it). The deferred commit handles the
/// Windows quirk where Width/Height change BEFORE WindowState during a maximize: stash on
/// the size/position change, then commit one Background-priority dispatcher tick later and
/// re-check WindowState — abandoning the stash if it has flipped to maximized.
///
/// MainWindow keeps its own equivalent (entangled with cross-restart persistence + screen
/// validation) and is intentionally not migrated onto this base.
/// </summary>
public abstract class ManagedDialogWindow : Window
{
    private readonly WindowRestoreState _restore = new();
    private WindowState _previousWindowState = WindowState.Normal;
    private bool _commitScheduled;

    protected ManagedDialogWindow()
    {
        // Position isn't an AvaloniaProperty in 12 — listen via the explicit event.
        PositionChanged += OnPositionChanged;
        // Seed once shown: CenterOwner positions the window at Opened, not in the ctor.
        Opened += OnOpenedSeed;
    }

    private void OnOpenedSeed(object? sender, EventArgs e)
    {
        // Unsubscribe in finally so a (vanishingly unlikely) throw while seeding can't
        // leave the handler attached and re-seed on a later Opened.
        try
        {
            _previousWindowState = WindowState;
            if (WindowState == WindowState.Normal)
                _restore.Seed(Position, Width, Height);
        }
        finally
        {
            Opened -= OnOpenedSeed;
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (!_restore.Seeded) return;
        if (WindowState == WindowState.Normal)
        {
            _restore.NotePosition(Position);
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
            // Reading WindowState here is unreliable on the to-maximized transition
            // (Width/Height flip first), so stash now and commit deferred (see below).
            if (_restore.Seeded && WindowState == WindowState.Normal)
            {
                _restore.NoteSize(Width, Height);
                ScheduleSnapshotCommit();
            }
        }
    }

    // Coalesced deferred commit at Background priority so any WindowState change in the
    // same Win32 message has propagated before we promote the stash.
    private void ScheduleSnapshotCommit()
    {
        if (_commitScheduled) return;
        _commitScheduled = true;
        Dispatcher.UIThread.Post(CommitSnapshot, DispatcherPriority.Background);
    }

    private void CommitSnapshot()
    {
        _commitScheduled = false;
        _restore.Commit(WindowState == WindowState.Normal);
    }

    private void HandleWindowStateTransition(WindowState oldState, WindowState newState)
    {
        // Leaving Normal: the snapshot is already kept fresh by the change handlers above.
        if (oldState == WindowState.Normal && newState != WindowState.Normal)
            return;
        // Returning to Normal: re-apply the snapshot so the dialog lands where it was.
        if (newState == WindowState.Normal && oldState != WindowState.Normal
            && _restore.TryGetRestoreRect(out var pos, out var w, out var h))
        {
            Position = pos;
            Width = w;
            Height = h;
        }
    }
}
