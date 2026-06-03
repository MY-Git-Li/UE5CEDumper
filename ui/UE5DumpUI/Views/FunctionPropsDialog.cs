using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Views;

/// <summary>
/// Reverse edge: lists the properties a UFunction reads/writes (walk_function_props).
/// Opened from the Interesting Functions panel "Props" row button.
///
/// Static Kismet-bytecode scan of one function — Blueprint/script functions only
/// (native functions have empty bytecode; surfaced in the footer). Writes are
/// best-effort (EX_Let* LHS); wrapped LHS may read as a read.
///
/// Code-behind only (no XAML / CompiledBinding) to stay AOT-safe; columns use
/// FuncDataTemplate&lt;T&gt; typed lambdas, not the reflection Binding ctor.
/// </summary>
public sealed class FunctionPropsDialog : Window
{
    private readonly IDumpService _dump;
    private readonly IPlatformService _platform;
    private readonly string _funcName;
    private readonly string _funcAddr;
    private TextBlock _statusLabel = null!;
    private DataGrid _grid = null!;
    private Button _btnRefresh = null!;
    private Button _btnCopy = null!;
    private CheckBox _classFieldsOnly = null!;
    private List<FunctionPropRef> _allProps = new();

    /// <summary>Resolve owner window + show. No-op without an address/platform/window.</summary>
    public static async Task ShowForFunctionAsync(
        string funcName, string funcAddr, IDumpService dump, IPlatformService? platform)
    {
        if (string.IsNullOrEmpty(funcAddr) || funcAddr == "0x0" || platform == null)
            return;
        if (Avalonia.Application.Current?.ApplicationLifetime is not
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
            return;
        var dialog = new FunctionPropsDialog(funcName, funcAddr, dump, platform);
        await dialog.ShowDialog(owner);
    }

    public FunctionPropsDialog(string funcName, string funcAddr, IDumpService dump, IPlatformService platform)
    {
        _funcName = funcName ?? "";
        _funcAddr = funcAddr ?? "";
        _dump = dump;
        _platform = platform;

        Title = $"Properties used by: {_funcName}";
        Width = 760;
        MinWidth = 520;
        Height = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        var root = new DockPanel { Margin = new Thickness(12) };

        var topRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,Auto,8,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var targetLbl = new TextBlock
        {
            Text = $"{_funcName}  @ {_funcAddr}",
            Foreground = new SolidColorBrush(Color.Parse("#DCDCAA")),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(targetLbl, 0);
        topRow.Children.Add(targetLbl);
        _classFieldsOnly = new CheckBox
        {
            Content = "Class fields only",
            IsChecked = true,   // hide BP compiler locals (CallFunc_*) by default
            Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _classFieldsOnly.IsCheckedChanged += (_, _) => ApplyFilter();
        Grid.SetColumn(_classFieldsOnly, 2);
        topRow.Children.Add(_classFieldsOnly);
        _btnRefresh = new Button { Content = "Refresh", Padding = new Thickness(10, 4) };
        _btnRefresh.Click += async (_, _) => await RunScanAsync();
        Grid.SetColumn(_btnRefresh, 4);
        topRow.Children.Add(_btnRefresh);
        DockPanel.SetDock(topRow, Dock.Top);
        root.Children.Add(topRow);

        _statusLabel = new TextBlock
        {
            Text = "Scanning…",
            Foreground = new SolidColorBrush(Color.Parse("#808080")),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        DockPanel.SetDock(_statusLabel, Dock.Top);
        root.Children.Add(_statusLabel);

        var caveat = new TextBlock
        {
            Text = "Note: Blueprint/script bytecode only. Native (C++) functions have no bytecode. "
                 + "Writes are best-effort (direct assignments); wrapped LHS (Other.Field / Struct.Member / Arr[i]) may read as a read.",
            Foreground = new SolidColorBrush(Color.Parse("#7A7A7A")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        DockPanel.SetDock(caveat, Dock.Bottom);
        root.Children.Add(caveat);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _btnCopy = new Button
        {
            Content = "Copy property name",
            Padding = new Thickness(12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false,
        };
        _btnCopy.Click += OnCopyClicked;
        btnRow.Children.Add(_btnCopy);
        var btnClose = new Button { Content = "Close", Padding = new Thickness(14, 6) };
        btnClose.Click += (_, _) => Close();
        btnRow.Children.Add(btnClose);
        DockPanel.SetDock(btnRow, Dock.Bottom);
        root.Children.Add(btnRow);

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserResizeColumns = true,
            CanUserReorderColumns = false,
            CanUserSortColumns = true,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            FontSize = 12,
        };
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Access",
            Width = new DataGridLength(90),
            SortMemberPath = nameof(FunctionPropRef.WriteCount),
            CellTemplate = new FuncDataTemplate<FunctionPropRef>(
                (x, _) => new TextBlock
                {
                    Text = x?.AccessSummary ?? "",
                    Foreground = new SolidColorBrush(Color.Parse(
                        (x?.WriteCount ?? 0) > 0 ? "#E0A050" : "#808080")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                }, supportsRecycling: true),
        });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Refs",
            Width = new DataGridLength(60),
            SortMemberPath = nameof(FunctionPropRef.Occurrences),
            CellTemplate = new FuncDataTemplate<FunctionPropRef>(
                (x, _) => new TextBlock
                {
                    Text = x?.Occurrences.ToString() ?? "",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                }, supportsRecycling: true),
        });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Scope",
            Width = new DataGridLength(80),
            SortMemberPath = nameof(FunctionPropRef.Scope),
            CellTemplate = new FuncDataTemplate<FunctionPropRef>(
                (x, _) => new TextBlock
                {
                    Text = x?.Scope ?? "",
                    Foreground = new SolidColorBrush(Color.Parse(
                        (x?.IsClassField ?? false) ? "#4EC9B0" : "#808080")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                }, supportsRecycling: true),
        });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Property",
            Width = new DataGridLength(240),
            SortMemberPath = nameof(FunctionPropRef.Name),
            CellTemplate = new FuncDataTemplate<FunctionPropRef>(
                (x, _) => new TextBlock
                {
                    Text = x?.Name ?? "",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                }, supportsRecycling: true),
        });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Type",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            SortMemberPath = nameof(FunctionPropRef.Type),
            CellTemplate = new FuncDataTemplate<FunctionPropRef>(
                (x, _) => new TextBlock
                {
                    Text = x?.Type ?? "",
                    Foreground = new SolidColorBrush(Color.Parse("#569CD6")),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                }, supportsRecycling: true),
        });
        _grid.SelectionChanged += (_, _) => _btnCopy.IsEnabled = _grid.SelectedItem is FunctionPropRef;
        _grid.DoubleTapped += (_, _) => OnCopyClicked(null, null!);
        root.Children.Add(_grid);

        Content = root;
        Opened += async (_, _) => await RunScanAsync();
    }

    private async Task RunScanAsync()
    {
        if (string.IsNullOrEmpty(_funcAddr) || _funcAddr == "0x0")
        {
            _statusLabel.Text = "No function address.";
            _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#F44747"));
            return;
        }

        _btnRefresh.IsEnabled = false;
        _statusLabel.Text = "Scanning bytecode…";
        _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#808080"));

        try
        {
            var res = await _dump.WalkFunctionPropsAsync(_funcAddr);
            _allProps = res.Props;

            if (res.ScriptBytes <= 0)
            {
                _grid.ItemsSource = null;
                _statusLabel.Text = "No bytecode — native (C++) function or empty body.";
                _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#D4D4D4"));
            }
            else
            {
                ApplyFilter();
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Scan failed: {ex.Message}";
            _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#F44747"));
            _grid.ItemsSource = null;
        }
        finally
        {
            _btnRefresh.IsEnabled = true;
            _btnCopy.IsEnabled = _grid.SelectedItem is FunctionPropRef;
        }
    }

    private void ApplyFilter()
    {
        bool fieldsOnly = _classFieldsOnly.IsChecked == true;
        var shown = fieldsOnly
            ? _allProps.Where(p => p.IsClassField).ToList()
            : _allProps;
        _grid.ItemsSource = shown;

        int writers = 0;
        foreach (var p in shown) if (p.WriteCount > 0) writers++;
        var hiddenNote = fieldsOnly && _allProps.Count > shown.Count
            ? $"  ({_allProps.Count - shown.Count} locals/temporaries hidden)" : "";
        _statusLabel.Text = $"{shown.Count} propert{(shown.Count == 1 ? "y" : "ies")} "
                          + $"({writers} written){hiddenNote}";
        _statusLabel.Foreground = new SolidColorBrush(Color.Parse(
            shown.Count > 0 ? "#4EC9B0" : "#D4D4D4"));
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItem is not FunctionPropRef x || string.IsNullOrEmpty(x.Name)) return;
        await _platform.CopyToClipboardAsync(x.Name);
        _statusLabel.Text = $"Copied: {x.Name}";
        _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#4EC9B0"));
    }
}
