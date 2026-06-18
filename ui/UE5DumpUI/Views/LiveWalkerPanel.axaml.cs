using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UE5DumpUI.Helpers;
using UE5DumpUI.Models;
using UE5DumpUI.ViewModels;

namespace UE5DumpUI.Views;

public partial class LiveWalkerPanel : UserControl
{
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(60, 255, 200, 0));

    // AOT-safe sort comparer for the FieldGrid's only template data column
    // ("Value"). Text columns sort out-of-box via their Binding path; the
    // Value column has no column-level binding, so its reflection sort is
    // trimmed under AOT — wire an explicit comparer (aot-pitfalls.md §4.5).
    private static readonly IReadOnlyDictionary<string, IComparer> FieldsSortComparers =
        new Dictionary<string, IComparer>
        {
            ["DisplayValue"] = DataGridSortComparers.Ordinal<LiveFieldValue>(r => r.DisplayValue),
        };

    // Audit fix #18: track the currently-subscribed VM so we can `-=` from
    // it before re-subscribing to a new one. Without this, every
    // DataContext reassignment leaves a stale handler on the previous VM
    // (keeping it alive) and adds a duplicate handler on the new one
    // (firing the scroll callback N times per event).
    private LiveWalkerViewModel? _subscribedVm;

    public LiveWalkerPanel()
    {
        InitializeComponent();
        this.FindControl<DataGrid>("FieldGrid")?.WireSortComparers(FieldsSortComparers);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // Unsubscribe from previous VM first (if any)
        if (_subscribedVm != null)
        {
            _subscribedVm.ScrollToFieldRequested -= OnScrollToFieldRequested;
            _subscribedVm.ScrollToFirstSearchMatch -= OnScrollToFirstSearchMatch;
            _subscribedVm.ScrollToFunctionRequested -= OnScrollToFunctionRequested;
            _subscribedVm.ScrollFieldIntoView -= OnScrollFieldIntoView;
            _subscribedVm.CaptureViewAnchor -= OnCaptureViewAnchor;
            _subscribedVm.RestoreBookmarkView -= OnRestoreBookmarkView;
            _subscribedVm = null;
        }

        if (DataContext is LiveWalkerViewModel vm)
        {
            vm.ScrollToFieldRequested += OnScrollToFieldRequested;
            vm.ScrollToFirstSearchMatch += OnScrollToFirstSearchMatch;
            vm.ScrollToFunctionRequested += OnScrollToFunctionRequested;
            vm.ScrollFieldIntoView += OnScrollFieldIntoView;
            vm.CaptureViewAnchor += OnCaptureViewAnchor;
            vm.RestoreBookmarkView += OnRestoreBookmarkView;
            _subscribedVm = vm;
        }
    }

    private void OnDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // Final cleanup when the panel leaves the visual tree.
        if (_subscribedVm != null)
        {
            _subscribedVm.ScrollToFieldRequested -= OnScrollToFieldRequested;
            _subscribedVm.ScrollToFirstSearchMatch -= OnScrollToFirstSearchMatch;
            _subscribedVm.ScrollToFunctionRequested -= OnScrollToFunctionRequested;
            _subscribedVm.ScrollFieldIntoView -= OnScrollFieldIntoView;
            _subscribedVm.CaptureViewAnchor -= OnCaptureViewAnchor;
            _subscribedVm.RestoreBookmarkView -= OnRestoreBookmarkView;
            _subscribedVm = null;
        }
    }

    private void OnScrollToFieldRequested(string fieldName)
    {
        // Post to UI thread to ensure DataGrid has rendered the new items
        Dispatcher.UIThread.Post(() =>
        {
            var grid = this.FindControl<DataGrid>("FieldGrid");
            if (grid?.ItemsSource == null) return;

            var target = grid.ItemsSource.Cast<LiveFieldValue>()
                .FirstOrDefault(f => f.Name == fieldName);
            if (target != null)
            {
                grid.ScrollIntoView(target, null);
                grid.SelectedItem = target;
            }
        }, DispatcherPriority.Background);
    }

    private void OnScrollToFirstSearchMatch()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var grid = this.FindControl<DataGrid>("FieldGrid");
            if (grid?.ItemsSource == null) return;

            var target = grid.ItemsSource.Cast<LiveFieldValue>()
                .FirstOrDefault(f => f.IsSearchMatch);
            if (target != null)
                grid.ScrollIntoView(target, null);
        }, DispatcherPriority.Background);
    }

    // Match-navigation (prev/next): the VM already set SelectedField to this
    // exact row, so scroll it into view. Using the object (not the name)
    // keeps us on the right row when field names repeat.
    private void OnScrollFieldIntoView(LiveFieldValue field)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var grid = this.FindControl<DataGrid>("FieldGrid");
            grid?.ScrollIntoView(field, null);
        }, DispatcherPriority.Background);
    }

    // Bookmark save: report the topmost visible field row so loading can scroll
    // back to it. The DataGrid has no public pixel-offset API, so we anchor on a
    // row. Synchronous — the VM reads the carrier right after raising the event.
    private void OnCaptureViewAnchor(ViewAnchorRef anchor)
    {
        var grid = this.FindControl<DataGrid>("FieldGrid");
        if (grid == null) return;

        // Topmost realised data row whose top edge sits at/below the grid's top
        // (rows scrolled above the viewport translate to a negative Y).
        LiveFieldValue? topField = null;
        double bestY = double.MaxValue;
        foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (row.DataContext is not LiveFieldValue f) continue;
            var pt = row.TranslatePoint(new Point(0, 0), grid);
            if (pt is { } p && p.Y >= -1 && p.Y < bestY) { bestY = p.Y; topField = f; }
        }
        if (topField != null)
            anchor.TopRow = new BookmarkFieldRef(topField.Name, topField.Offset);
    }

    // Bookmark load: re-select the saved rows (one or many) and scroll the saved
    // anchor row back into view, so the bookmark returns to what was on screen.
    private void OnRestoreBookmarkView(IReadOnlyList<BookmarkFieldRef> selected, BookmarkFieldRef? topRow)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var grid = this.FindControl<DataGrid>("FieldGrid");
            if (grid?.ItemsSource == null) return;
            var rows = grid.ItemsSource.Cast<LiveFieldValue>().ToList();

            // Prefer an exact name+offset match; fall back to name only (container
            // element rows can share offset 0 across elements).
            static LiveFieldValue? Match(List<LiveFieldValue> rs, BookmarkFieldRef r)
                => rs.FirstOrDefault(f => f.Name == r.Name && f.Offset == r.Offset)
                   ?? rs.FirstOrDefault(f => f.Name == r.Name);

            if (selected.Count > 0)
            {
                try
                {
                    grid.SelectedItems.Clear();
                    foreach (var sel in selected)
                    {
                        var hit = Match(rows, sel);
                        if (hit != null && !grid.SelectedItems.Contains(hit))
                            grid.SelectedItems.Add(hit);
                    }
                }
                catch (System.NotSupportedException)
                {
                    // Defensive: if this Avalonia build's SelectedItems list is
                    // read-only, fall back to restoring the single anchor row.
                    grid.SelectedItem = Match(rows, selected[0]);
                }
            }

            // Restore the view position after selection-driven layout settles.
            // Anchor on the saved top row, falling back to the first selected row.
            Dispatcher.UIThread.Post(() =>
            {
                var anchor = topRow != null ? Match(rows, topRow) : null;
                if (anchor == null && selected.Count > 0)
                    anchor = Match(rows, selected[0]);
                if (anchor != null)
                    grid.ScrollIntoView(anchor, null);
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private void OnScrollToFunctionRequested(string functionName)
    {
        // Cross-tab nav from Interesting Funcs lands here. The grid lives
        // inside an Expander that is collapsed by default — when it has
        // never been expanded, ItemsSource has no realised rows yet, so
        // post twice (background then loaded) to give layout a chance to
        // populate the row presenters before scrolling.
        Dispatcher.UIThread.Post(() =>
        {
            var grid = this.FindControl<DataGrid>("FunctionGrid");
            if (grid?.ItemsSource == null) return;

            var target = grid.ItemsSource.Cast<FunctionInfoModel>()
                .FirstOrDefault(f => f.Name == functionName);
            if (target != null)
            {
                grid.SelectedItem = target;
                grid.ScrollIntoView(target, null);
            }
        }, DispatcherPriority.Background);
    }

    private void AddressInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is LiveWalkerViewModel vm
            && sender is TextBox tb)
        {
            if (vm.NavigateToAddressCommand.CanExecute(tb.Text))
            {
                vm.NavigateToAddressCommand.Execute(tb.Text);
                e.Handled = true;
            }
        }
    }

    private static readonly IBrush GuessedForeground = new SolidColorBrush(Color.Parse("#666666"));
    private static readonly IBrush NormalForeground = new SolidColorBrush(Color.Parse("#D4D4D4"));

    // Sync the DataGrid's multi-selection into the ViewModel snapshot.
    // Avalonia's DataGrid.SelectedItems is read-only, so we push it into
    // the VM here whenever the selection changes. The Copy CE Field(s)
    // export command reads this snapshot when invoked.
    private void FieldGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is LiveWalkerViewModel vm)
        {
            vm.UpdateSelectedFields(grid.SelectedItems);
        }
    }

    private void FieldGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is LiveFieldValue field)
        {
            e.Row.Background = field.IsSearchMatch ? HighlightBrush : Brushes.Transparent;
            e.Row.Foreground = field.IsGuessed ? GuessedForeground : NormalForeground;
        }
    }

    private void FieldGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        // Cancel edit for non-editable fields (pointers, structs, containers, strings, etc.)
        if (e.Row.DataContext is LiveFieldValue field && !field.IsEditable)
        {
            e.Cancel = true;
            return;
        }

        // Suppress auto-refresh while editing
        if (DataContext is LiveWalkerViewModel vm)
            vm.IsEditing = true;
    }

    private async void FieldGrid_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (DataContext is not LiveWalkerViewModel vm) return;

        try
        {
            // Only commit on user confirmation (Enter / focus loss), not on cancel (Escape)
            if (e.EditAction == DataGridEditAction.Cancel) return;

            if (e.Row.DataContext is LiveFieldValue field && field.IsEditable)
            {
                var newValue = field.GetPendingEditValue();
                if (!string.IsNullOrEmpty(newValue))
                {
                    // Save field identity for re-selection after refresh
                    var editedName = field.Name;
                    var editedOffset = field.Offset;

                    await vm.CommitFieldEditAsync(field, newValue);

                    // Scroll to keep the edited row visible after refresh
                    ScrollToField(editedName, editedOffset);
                }
            }
        }
        finally
        {
            vm.IsEditing = false;
        }
    }

    /// <summary>
    /// Commit (Enter) or cancel (Escape) the in-place edit without advancing.
    /// Avalonia's DataGrid default treats Enter as "commit + move selection to the
    /// next row (re-entering edit mode)", so each Enter walked the editor down the
    /// grid. Committing ourselves and marking the event Handled stops the event from
    /// bubbling to the DataGrid, so the editor closes on the field the user edited.
    /// </summary>
    private void EditBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Escape) return;

        var grid = this.FindControl<DataGrid>("FieldGrid");
        if (grid == null) return;

        if (e.Key == Key.Enter)
            grid.CommitEdit();
        else
            grid.CancelEdit();
        e.Handled = true;
    }

    /// <summary>Auto-open the dropdown when a BoolProperty enters edit mode.</summary>
    private void BoolCombo_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb)
        {
            // Delay to ensure layout is complete before opening dropdown
            Dispatcher.UIThread.Post(() => cb.IsDropDownOpen = true,
                DispatcherPriority.Background);
        }
    }

    /// <summary>Auto-commit the edit when user selects a value from the bool dropdown.</summary>
    private void BoolCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Only handle actual user changes (not initial binding load)
        if (e.RemovedItems.Count == 0) return;
        if (DataContext is not LiveWalkerViewModel vm || !vm.IsEditing) return;

        // Post CommitEdit to let the binding update _editableValue first
        Dispatcher.UIThread.Post(() =>
        {
            var grid = this.FindControl<DataGrid>("FieldGrid");
            grid?.CommitEdit();
        }, DispatcherPriority.Background);
    }

    /// <summary>Scroll the DataGrid to show the field with the given name and offset.</summary>
    private void ScrollToField(string fieldName, int fieldOffset)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var grid = this.FindControl<DataGrid>("FieldGrid");
            if (grid?.ItemsSource == null) return;

            var restored = grid.ItemsSource.Cast<LiveFieldValue>()
                .FirstOrDefault(f => f.Name == fieldName && f.Offset == fieldOffset);
            if (restored != null)
            {
                grid.SelectedItem = restored;
                grid.ScrollIntoView(restored, null);
            }
        }, DispatcherPriority.Background);
    }
}
