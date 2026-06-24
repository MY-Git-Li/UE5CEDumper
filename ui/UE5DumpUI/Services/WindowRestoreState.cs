using Avalonia;
using Avalonia.Controls;

namespace UE5DumpUI.Services;

/// <summary>
/// Pure, Window-agnostic state machine for the "restore to the rect it had before
/// maximize" behaviour. Avalonia 12 on Windows can land a restored (Maximized →
/// Normal) window stretched across monitors or at the wrong spot, so we snapshot the
/// last NORMAL geometry and re-apply it on the way back down. The decision logic lives
/// here — free of Avalonia <c>Window</c> coupling beyond the plain <see cref="PixelPoint"/>
/// struct and the <see cref="WindowState"/> enum — so it is unit-testable without a
/// headless UI. <see cref="Views.ManagedDialogWindow"/> drives it from the live window's
/// events; <see cref="Views.MainWindow"/> keeps its own equivalent (intertwined with
/// cross-restart persistence) and is intentionally not migrated.
///
/// The deferred-commit dance (Width/Height arrive BEFORE WindowState during a maximize
/// on Windows) is the caller's responsibility: it stashes geometry via
/// <see cref="NoteSize"/>/<see cref="NotePosition"/> while the window reads as Normal,
/// then calls <see cref="Commit"/> one dispatcher tick later passing the RE-READ state —
/// <see cref="Commit"/> abandons the stash if the window has since flipped to non-Normal
/// (the stashed values would be the maximized dimensions).
/// </summary>
public sealed class WindowRestoreState
{
    private PixelPoint? _normalPos;
    private double _normalW;
    private double _normalH;

    private PixelPoint _pendPos;
    private double _pendW;
    private double _pendH;

    /// <summary>True once <see cref="Seed"/> has run (the window has opened in Normal
    /// state). Callers ignore size/position churn before this.</summary>
    public bool Seeded { get; private set; }

    /// <summary>Last committed normal-state top-left, or null before the first seed.</summary>
    public PixelPoint? NormalPosition => _normalPos;
    /// <summary>Last committed normal-state width.</summary>
    public double NormalWidth => _normalW;
    /// <summary>Last committed normal-state height.</summary>
    public double NormalHeight => _normalH;

    /// <summary>Capture the initial normal-state rect (call once, after the window is
    /// open and still Normal). Both the committed snapshot and the pending stash start
    /// here.</summary>
    public void Seed(PixelPoint pos, double w, double h)
    {
        _normalPos = pos;
        _normalW = w;
        _normalH = h;
        _pendPos = pos;
        _pendW = w;
        _pendH = h;
        Seeded = true;
    }

    /// <summary>Stash a new top-left seen while the window is Normal (commit later).</summary>
    public void NotePosition(PixelPoint pos) => _pendPos = pos;

    /// <summary>Stash a new size seen while the window is Normal (commit later).
    /// Non-positive values (transient layout noise) are ignored.</summary>
    public void NoteSize(double w, double h)
    {
        if (w > 0) _pendW = w;
        if (h > 0) _pendH = h;
    }

    /// <summary>Promote the pending stash into the committed snapshot — but only if the
    /// window is still Normal (<paramref name="isNormalNow"/>). When it has flipped to
    /// maximized/minimized since the stash, the pending values are the maximized
    /// dimensions, so the commit is abandoned and the prior snapshot kept.</summary>
    public void Commit(bool isNormalNow)
    {
        if (!isNormalNow) return;
        if (_pendW > 0) _normalW = _pendW;
        if (_pendH > 0) _normalH = _pendH;
        _normalPos = _pendPos;
    }

    /// <summary>The rect to re-apply when returning to Normal from a non-Normal state.
    /// False when there is no usable snapshot yet (not seeded / degenerate size).</summary>
    public bool TryGetRestoreRect(out PixelPoint pos, out double w, out double h)
    {
        pos = _normalPos ?? default;
        w = _normalW;
        h = _normalH;
        return _normalPos.HasValue && _normalW > 0 && _normalH > 0;
    }
}
