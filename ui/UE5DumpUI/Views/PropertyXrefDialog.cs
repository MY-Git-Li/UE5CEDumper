using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Views;

/// <summary>
/// Shows which UFunctions reference a given FProperty (find_property_xrefs):
/// answers "which methods use this field?". Opened from the Class Struct panel
/// field grid context menu.
///
/// Static Kismet-bytecode scan — Blueprint/script functions only. Native
/// (C++) functions have empty bytecode and are invisible; this is surfaced in
/// the footer so an empty result on an engine field doesn't read as a failure.
///
/// Code-behind only (no XAML / CompiledBinding) to stay AOT-safe like
/// ObjectInstancePickerDialog. Columns use FuncDataTemplate&lt;T&gt; (typed
/// lambdas) rather than the reflection-based <c>new Binding(string)</c> ctor.
/// </summary>
public sealed class PropertyXrefDialog : Window
{
    private readonly IDumpService _dump;
    private readonly IPlatformService _platform;
    private readonly string _fieldName;
    private readonly string _propAddr;
    private CheckBox _gameOnlyBox = null!;
    private TextBlock _statusLabel = null!;
    private DataGrid _grid = null!;
    private Button _btnRefresh = null!;
    private Button _btnCopy = null!;
    private Button _btnClose = null!;

    public PropertyXrefDialog(string fieldName, string fieldType, string propAddr,
                              IDumpService dump, IPlatformService platform)
    {
        _fieldName = fieldName ?? "";
        _propAddr = propAddr ?? "";
        _dump = dump;
        _platform = platform;

        Title = $"Functions using field: {_fieldName}";
        Width = 860;
        MinWidth = 560;
        Height = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        var root = new DockPanel { Margin = new Thickness(12) };

        // === Top: target + controls ===
        var topRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,Auto,8,Auto"),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var targetLbl = new TextBlock
        {
            Text = $"{fieldType} {_fieldName}  @ {_propAddr}",
            Foreground = new SolidColorBrush(Color.Parse("#DCDCAA")),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(targetLbl, 0);
        topRow.Children.Add(targetLbl);

        _gameOnlyBox = new CheckBox
        {
            Content = "Game only",
            IsChecked = true,
            Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_gameOnlyBox, 2);
        topRow.Children.Add(_gameOnlyBox);

        _btnRefresh = new Button { Content = "Refresh", Padding = new Thickness(10, 4) };
        _btnRefresh.Click += async (_, _) => await RunScanAsync();
        Grid.SetColumn(_btnRefresh, 4);
        topRow.Children.Add(_btnRefresh);

        DockPanel.SetDock(topRow, Dock.Top);
        root.Children.Add(topRow);

        // === Status (under top row) ===
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

        // === Footer: native-coverage caveat + buttons ===
        var caveat = new TextBlock
        {
            Text = "Note: Blueprint/script functions only. Native (C++) functions have no bytecode "
                 + "and cannot be detected here — an empty result on an engine field is expected.",
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
            Content = "Copy function path",
            Padding = new Thickness(12, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = false,
        };
        _btnCopy.Click += OnCopyClicked;
        btnRow.Children.Add(_btnCopy);

        _btnClose = new Button { Content = "Close", Padding = new Thickness(14, 6) };
        _btnClose.Click += (_, _) => Close();
        btnRow.Children.Add(_btnClose);

        DockPanel.SetDock(btnRow, Dock.Bottom);
        root.Children.Add(btnRow);

        // === Center: xref grid ===
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
            Header = "Kind",
            Width = new DataGridLength(90),
            SortMemberPath = nameof(PropertyXrefMatch.Kind),
            CellTemplate = new FuncDataTemplate<PropertyXrefMatch>(
                (x, _) => new TextBlock
                {
                    Text = x?.Kind ?? "",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                }, supportsRecycling: true),
        });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Refs",
            Width = new DataGridLength(60),
            SortMemberPath = nameof(PropertyXrefMatch.Occurrences),
            CellTemplate = new FuncDataTemplate<PropertyXrefMatch>(
                (x, _) => new TextBlock
                {
                    Text = x?.Occurrences.ToString() ?? "",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                }, supportsRecycling: true),
        });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Owner Class",
            Width = new DataGridLength(220),
            SortMemberPath = nameof(PropertyXrefMatch.OwnerClassName),
            CellTemplate = new FuncDataTemplate<PropertyXrefMatch>(
                (x, _) => new TextBlock
                {
                    Text = x?.OwnerClassName ?? "",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                }, supportsRecycling: true),
        });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Function",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            SortMemberPath = nameof(PropertyXrefMatch.FunctionFullName),
            CellTemplate = new FuncDataTemplate<PropertyXrefMatch>(
                (x, _) => new TextBlock
                {
                    Text = x?.FunctionFullName ?? "",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0),
                }, supportsRecycling: true),
        });
        _grid.SelectionChanged += (_, _) => _btnCopy.IsEnabled = _grid.SelectedItem is PropertyXrefMatch;
        _grid.DoubleTapped += (_, _) => OnCopyClicked(null, null!);

        root.Children.Add(_grid);

        Content = root;

        Opened += async (_, _) => await RunScanAsync();
    }

    private async Task RunScanAsync()
    {
        if (string.IsNullOrEmpty(_propAddr) || _propAddr == "0x0")
        {
            _statusLabel.Text = "No FProperty address for this field.";
            _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#F44747"));
            return;
        }

        _btnRefresh.IsEnabled = false;
        _statusLabel.Text = "Scanning bytecode…";
        _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#808080"));

        try
        {
            var gameOnly = _gameOnlyBox.IsChecked == true;
            var res = await _dump.FindPropertyXrefsAsync(_propAddr, gameOnly);
            _grid.ItemsSource = res.Xrefs;

            var s = res.Scan;
            var stats = s == null
                ? ""
                : $" — scanned {s.FunctionsScanned:N0} funcs ({s.FunctionsWithScript:N0} with bytecode) "
                + $"over {s.ObjectsTotal:N0} objects in {s.DurationMs}ms"
                + (s.DeadlineHit ? " [DEADLINE HIT — partial]" : "");

            _statusLabel.Text = $"{res.Xrefs.Count} function(s) reference this field{stats}";
            _statusLabel.Foreground = new SolidColorBrush(Color.Parse(
                res.Xrefs.Count > 0 ? "#4EC9B0" : "#D4D4D4"));
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
            _btnCopy.IsEnabled = _grid.SelectedItem is PropertyXrefMatch;
        }
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (_grid.SelectedItem is not PropertyXrefMatch x || string.IsNullOrEmpty(x.FunctionFullName))
            return;
        await _platform.CopyToClipboardAsync(x.FunctionFullName);
        _statusLabel.Text = $"Copied: {x.FunctionFullName}";
        _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#4EC9B0"));
    }
}
