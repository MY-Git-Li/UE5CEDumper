using Avalonia;
using UE5DumpUI.Services;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Unit tests for the pure maximize/restore snapshot state machine that backs
/// <see cref="Views.ManagedDialogWindow"/> (the Find Func / Prop / instance-picker
/// dialogs). Verifies the core bug fix: after a maximize→restore, the window's
/// pre-maximize NORMAL rect is what gets re-applied — even though the Windows
/// property-change order stashes the maximized dimensions first.
/// </summary>
public class WindowRestoreStateTests
{
    [Fact]
    public void Unseeded_HasNoRestoreRect()
    {
        var s = new WindowRestoreState();
        Assert.False(s.Seeded);
        Assert.False(s.TryGetRestoreRect(out _, out _, out _));
    }

    [Fact]
    public void Seed_CapturesNormalRect()
    {
        var s = new WindowRestoreState();
        s.Seed(new PixelPoint(100, 200), 860, 520);

        Assert.True(s.Seeded);
        Assert.Equal(new PixelPoint(100, 200), s.NormalPosition);
        Assert.Equal(860, s.NormalWidth);
        Assert.Equal(520, s.NormalHeight);

        Assert.True(s.TryGetRestoreRect(out var pos, out var w, out var h));
        Assert.Equal(new PixelPoint(100, 200), pos);
        Assert.Equal(860, w);
        Assert.Equal(520, h);
    }

    [Fact]
    public void Commit_WhileNormal_PromotesPendingGeometry()
    {
        var s = new WindowRestoreState();
        s.Seed(new PixelPoint(0, 0), 800, 500);

        // User drags + resizes the still-Normal window.
        s.NotePosition(new PixelPoint(300, 150));
        s.NoteSize(900, 600);
        s.Commit(isNormalNow: true);

        Assert.Equal(new PixelPoint(300, 150), s.NormalPosition);
        Assert.Equal(900, s.NormalWidth);
        Assert.Equal(600, s.NormalHeight);
    }

    [Fact]
    public void Commit_AfterFlipToMaximized_IsAbandoned()
    {
        var s = new WindowRestoreState();
        s.Seed(new PixelPoint(120, 80), 860, 520);

        // The Windows quirk: Width/Height arrive as the MAXIMIZED dims while the
        // window still reads Normal, so they get stashed...
        s.NoteSize(2560, 1380);
        s.NotePosition(new PixelPoint(0, 0));
        // ...but by commit time WindowState has flipped to Maximized -> abandon.
        s.Commit(isNormalNow: false);

        // The pre-maximize NORMAL rect must survive untouched.
        Assert.Equal(new PixelPoint(120, 80), s.NormalPosition);
        Assert.Equal(860, s.NormalWidth);
        Assert.Equal(520, s.NormalHeight);
    }

    [Fact]
    public void MaximizeThenRestore_ReappliesOriginalRect()
    {
        var s = new WindowRestoreState();
        s.Seed(new PixelPoint(120, 80), 860, 520);   // opened, Normal

        // --- maximize: poisoned stash gets abandoned at commit ---
        s.NoteSize(2560, 1380);
        s.NotePosition(new PixelPoint(0, 0));
        s.Commit(isNormalNow: false);

        // --- restore: the rect we re-apply is the original normal one ---
        Assert.True(s.TryGetRestoreRect(out var pos, out var w, out var h));
        Assert.Equal(new PixelPoint(120, 80), pos);
        Assert.Equal(860, w);
        Assert.Equal(520, h);
    }

    [Fact]
    public void NoteSize_IgnoresNonPositiveValues()
    {
        var s = new WindowRestoreState();
        s.Seed(new PixelPoint(0, 0), 800, 500);

        s.NoteSize(0, -1);          // transient layout noise
        s.Commit(isNormalNow: true);

        Assert.Equal(800, s.NormalWidth);
        Assert.Equal(500, s.NormalHeight);
    }
}
