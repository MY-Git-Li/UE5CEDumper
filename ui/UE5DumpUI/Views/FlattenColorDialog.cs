using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace UE5DumpUI.Views;

/// <summary>
/// Editor for the alternating row colours applied to flattened container records (Copy CE XML /
/// Copy CE Field). When a TMap/TArray of records is flattened into a flat list, each element's
/// rows are tinted by index parity so the records stay separable in CE — even-indexed
/// (struct[0],[2],…) and odd-indexed (struct[1],[3],…) get different colours.
///
/// Colours are stored as RGB hex "RRGGBB" (null/empty = unset → CE uses its theme); the export
/// converts to a CE COLORREF (BBGGRR) at emit time. The UI offers a curated neutral palette plus a
/// free hex field and a live preview. Code-behind only (no XAML / reflection binding) to stay
/// AOT-safe, and built on <see cref="ManagedDialogWindow"/> so maximize/restore matches the app's
/// other dialogs. Apply on OK invokes the caller's callback (which updates + persists the VM).
/// </summary>
public sealed class FlattenColorDialog : ManagedDialogWindow
{
    // Curated neutral palette — muted tones common in modern animation, plus the azure default.
    // RGB hex; rendered as clickable swatches in each section.
    private static readonly (string Name, string Rgb)[] Presets =
    {
        ("Azure",       "0080FF"),
        ("Teal",        "6BA6A6"),
        ("Sage",        "9CAF88"),
        ("Dusty Rose",  "CFA0A0"),
        ("Warm Sand",   "D8C3A5"),
        ("Slate Blue",  "8095B0"),
        ("Mauve",       "B59FC4"),
        ("Soft Amber",  "E0B878"),
        ("Soft Mint",   "A8C8B0"),
        ("Muted Coral", "E0A088"),
    };

    private static readonly IBrush PanelText = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush PreviewDefault = new SolidColorBrush(Color.Parse("#D4D4D4"));

    private readonly Action<bool, string?, string?> _onApply;

    private bool _enabled;
    private string? _even;
    private string? _odd;

    private CheckBox _enableCheck = null!;
    private StackPanel _sectionsHost = null!;
    private Border _evenSwatch = null!;
    private Border _oddSwatch = null!;
    private TextBox _evenHex = null!;
    private TextBox _oddHex = null!;
    private StackPanel _previewPanel = null!;

    public static void ShowFor(bool enabled, string? evenRgb, string? oddRgb,
        Action<bool, string?, string?> onApply)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
            return;
        var dlg = new FlattenColorDialog(enabled, evenRgb, oddRgb, onApply);
        _ = dlg.ShowDialog(owner);
    }

    public FlattenColorDialog(bool enabled, string? evenRgb, string? oddRgb,
        Action<bool, string?, string?> onApply)
    {
        _onApply = onApply;
        _enabled = enabled;
        _even = Normalize(evenRgb);
        _odd = Normalize(oddRgb);

        Title = "Flatten Record Colors";
        Width = 560;
        MinWidth = 460;
        Height = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        var root = new DockPanel { Margin = new Thickness(14) };

        _enableCheck = new CheckBox
        {
            Content = "Enable alternating row colors for flattened records",
            IsChecked = _enabled,
            Foreground = PanelText,
            FontSize = 13,
        };
        _enableCheck.IsCheckedChanged += (_, _) =>
        {
            _enabled = _enableCheck.IsChecked == true;
            RefreshEnabledState();
            RefreshPreview();
        };
        DockPanel.SetDock(_enableCheck, Dock.Top);
        root.Children.Add(_enableCheck);

        var blurb = new TextBlock
        {
            Text = "When a TMap / TArray of records is flattened (Copy CE XML / Copy CE Field), the "
                 + "per-element folder is gone — so each element's rows are tinted by index to keep "
                 + "the records apart in CE. Even = struct[0], [2], …  Odd = struct[1], [3], …  "
                 + "Reset = no color (CE uses its theme).",
            Foreground = MutedText,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 10),
        };
        DockPanel.SetDock(blurb, Dock.Top);
        root.Children.Add(blurb);

        // Bottom button row.
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var btnCancel = new Button { Content = "Cancel", Padding = new Thickness(16, 6), Margin = new Thickness(0, 0, 8, 0) };
        btnCancel.Click += (_, _) => Close();
        btnRow.Children.Add(btnCancel);
        var btnOk = new Button { Content = "OK", Padding = new Thickness(20, 6) };
        btnOk.Click += (_, _) => { _onApply(_enabled, _even, _odd); Close(); };
        btnRow.Children.Add(btnOk);
        DockPanel.SetDock(btnRow, Dock.Bottom);
        root.Children.Add(btnRow);

        // Live preview (above the buttons).
        var previewTitle = new TextBlock
        {
            Text = "Preview", Foreground = MutedText, FontSize = 11,
            Margin = new Thickness(0, 12, 0, 4),
        };
        DockPanel.SetDock(previewTitle, Dock.Bottom);
        root.Children.Add(previewTitle);

        _previewPanel = new StackPanel
        {
            Spacing = 1,
            Margin = new Thickness(6, 0, 0, 0),
        };
        DockPanel.SetDock(_previewPanel, Dock.Bottom);
        root.Children.Add(_previewPanel);

        // Even / Odd sections fill the remaining space.
        _sectionsHost = new StackPanel { Spacing = 12 };
        _evenSwatch = MakeSwatchPreview();
        _oddSwatch = MakeSwatchPreview();
        _evenHex = new TextBox { Width = 96, PlaceholderText = "RRGGBB", FontSize = 12 };
        _oddHex = new TextBox { Width = 96, PlaceholderText = "RRGGBB", FontSize = 12 };
        _sectionsHost.Children.Add(BuildSection(
            "Even  (struct[0], [2], …)", _evenSwatch, _evenHex, isEven: true));
        _sectionsHost.Children.Add(BuildSection(
            "Odd  (struct[1], [3], …)", _oddSwatch, _oddHex, isEven: false));
        root.Children.Add(_sectionsHost);

        Content = root;

        // Seed the hex fields then paint everything.
        _evenHex.Text = _even ?? "";
        _oddHex.Text = _odd ?? "";
        _evenHex.TextChanged += (_, _) => OnHexChanged(isEven: true);
        _oddHex.TextChanged += (_, _) => OnHexChanged(isEven: false);
        RefreshEnabledState();
        RefreshSwatches();
        RefreshPreview();
    }

    private Border BuildSection(string title, Border swatch, TextBox hex, bool isEven)
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = title, Foreground = PanelText, FontSize = 12, FontWeight = FontWeight.SemiBold,
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(swatch);
        row.Children.Add(new TextBlock
        {
            Text = "Hex", Foreground = MutedText, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(hex);
        var reset = new Button { Content = "Reset", Padding = new Thickness(10, 4), FontSize = 11 };
        ToolTip.SetTip(reset, "Clear this color — the row uses CE's theme color (no <Color>).");
        reset.Click += (_, _) =>
        {
            if (isEven) _even = null; else _odd = null;
            hex.Text = "";   // triggers OnHexChanged → refresh
        };
        row.Children.Add(reset);

        // Custom… → the in-app full-range picker (hue strip + RGB sliders), dark-themed.
        var custom = new Button { Content = "Custom…", Padding = new Thickness(10, 4), FontSize = 11 };
        ToolTip.SetTip(custom, "Open an in-app color picker (hue strip + RGB sliders) for a free choice.");
        custom.Click += async (_, _) =>
        {
            try
            {
                var picked = await ColorPickerDialog.PickAsync(this, isEven ? _even : _odd);
                if (!string.IsNullOrEmpty(picked))
                    hex.Text = picked;   // triggers OnHexChanged → refresh swatch + preview + state
            }
            catch { /* dialog teardown races are benign */ }
        };
        row.Children.Add(custom);
        panel.Children.Add(row);

        // Preset swatches for this section.
        var presetWrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, rgb) in Presets)
        {
            var btn = new Button
            {
                Width = 26, Height = 20, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse("#" + rgb)),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(btn, $"{name}  #{rgb}");
            var captured = rgb;
            btn.Click += (_, _) =>
            {
                if (isEven) _even = captured; else _odd = captured;
                hex.Text = captured;   // triggers OnHexChanged → refresh
            };
            presetWrap.Children.Add(btn);
        }
        panel.Children.Add(presetWrap);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Child = panel,
        };
    }

    private static Border MakeSwatchPreview() => new()
    {
        Width = 40, Height = 22,
        BorderBrush = new SolidColorBrush(Color.Parse("#666666")),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
    };

    private void OnHexChanged(bool isEven)
    {
        var raw = (isEven ? _evenHex : _oddHex).Text;
        var norm = Normalize(raw);
        if (isEven) _even = norm; else _odd = norm;
        RefreshSwatches();
        RefreshPreview();
    }

    private void RefreshEnabledState()
    {
        _sectionsHost.IsEnabled = _enabled;
        _sectionsHost.Opacity = _enabled ? 1.0 : 0.4;
    }

    private void RefreshSwatches()
    {
        PaintSwatch(_evenSwatch, _even);
        PaintSwatch(_oddSwatch, _odd);
    }

    private static void PaintSwatch(Border swatch, string? rgb)
    {
        if (rgb != null)
        {
            swatch.Background = new SolidColorBrush(Color.Parse("#" + rgb));
        }
        else
        {
            // Unset: a neutral fill so it reads as "no color".
            swatch.Background = new SolidColorBrush(Color.Parse("#2A2A2A"));
        }
    }

    private void RefreshPreview()
    {
        _previewPanel.Children.Clear();
        // Three sample records (indices 0,1,2), two rows each, tinted like the export will.
        for (int i = 0; i < 3; i++)
        {
            var color = !_enabled ? null : (i % 2 == 0 ? _even : _odd);
            var brush = color != null ? new SolidColorBrush(Color.Parse("#" + color)) : PreviewDefault;
            foreach (var leaf in new[] { "Score", "PilotName" })
            {
                _previewPanel.Children.Add(new TextBlock
                {
                    Text = $"[{i}] mc1om_00{i + 1} ▸ {leaf}",
                    Foreground = brush,
                    FontFamily = new FontFamily("Consolas, monospace"),
                    FontSize = 12,
                });
            }
        }
    }

    /// <summary>Trim/validate an RGB hex string to canonical "RRGGBB" (uppercase), or null.</summary>
    private static string? Normalize(string? rgb)
    {
        if (string.IsNullOrWhiteSpace(rgb)) return null;
        var s = rgb.Trim().TrimStart('#');
        if (s.Length != 6) return null;
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return null;
        return s.ToUpperInvariant();
    }
}
