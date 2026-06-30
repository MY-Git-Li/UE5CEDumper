using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace UE5DumpUI.Views;

/// <summary>
/// A small, dark-themed IN-APP colour picker — the "Custom…" target for the Record Colors editor.
/// Deliberately NOT the native Win32 ChooseColor dialog (that is a light-themed OS-modal window and
/// would need to route a P/Invoke through the Core platform abstraction); this stays on-theme and
/// inside Avalonia. A rainbow hue strip jumps to a pure hue, RGB sliders fine-tune saturation /
/// brightness, and a hex field accepts any value, all reflected in a live preview.
///
/// Returns the picked colour as an RGB hex string "RRGGBB" via <see cref="PickAsync"/> (null on
/// Cancel). Code-behind only (no XAML / reflection binding) to stay AOT-safe; built on
/// <see cref="ManagedDialogWindow"/> for the shared maximize/restore behaviour.
/// </summary>
public sealed class ColorPickerDialog : ManagedDialogWindow
{
    private static readonly IBrush PanelText = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#9A9A9A"));

    private int _r, _g, _b;
    private bool _suppress;       // guard slider<->hex<->state feedback loops
    private bool _dragHue;

    private Border _preview = null!;
    private TextBox _hex = null!;
    private Slider _rs = null!, _gs = null!, _bs = null!;
    private TextBlock _rv = null!, _gv = null!, _bv = null!;

    /// <summary>Show the picker modally over <paramref name="owner"/>, seeded with
    /// <paramref name="initialRgb"/> ("RRGGBB", optional '#'); returns the picked "RRGGBB" or null.</summary>
    public static async Task<string?> PickAsync(Window owner, string? initialRgb)
    {
        var dlg = new ColorPickerDialog(initialRgb);
        return await dlg.ShowDialog<string?>(owner);
    }

    public ColorPickerDialog(string? initialRgb)
    {
        (_r, _g, _b) = ParseRgb(initialRgb) ?? (128, 128, 128);

        Title = "Pick a Color";
        Width = 360;
        MinWidth = 320;
        Height = 320;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        var root = new DockPanel { Margin = new Thickness(14) };

        // Top: big preview + hex field.
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _preview = new Border
        {
            Width = 88, Height = 56,
            BorderBrush = new SolidColorBrush(Color.Parse("#666666")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        top.Children.Add(_preview);
        var hexCol = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        hexCol.Children.Add(new TextBlock { Text = "Hex (RRGGBB)", Foreground = MutedText, FontSize = 11 });
        _hex = new TextBox { Width = 110, FontSize = 12 };
        _hex.TextChanged += (_, _) => OnHexEdited();
        hexCol.Children.Add(_hex);
        top.Children.Add(hexCol);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // Rainbow hue strip — click / drag jumps to a pure hue.
        var hueStrip = new Border
        {
            Height = 22,
            Margin = new Thickness(0, 12, 0, 12),
            CornerRadius = new CornerRadius(3),
            BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            BorderThickness = new Thickness(1),
            Background = BuildHueGradient(),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        hueStrip.PointerPressed += (s, e) => { _dragHue = true; ApplyHueFromPointer(hueStrip, e); };
        hueStrip.PointerMoved += (s, e) => { if (_dragHue) ApplyHueFromPointer(hueStrip, e); };
        hueStrip.PointerReleased += (_, _) => _dragHue = false;
        DockPanel.SetDock(hueStrip, Dock.Top);
        root.Children.Add(hueStrip);

        // Bottom button row.
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var btnCancel = new Button { Content = "Cancel", Padding = new Thickness(16, 6), Margin = new Thickness(0, 0, 8, 0) };
        btnCancel.Click += (_, _) => Close(null);
        btnRow.Children.Add(btnCancel);
        var btnOk = new Button { Content = "OK", Padding = new Thickness(20, 6) };
        btnOk.Click += (_, _) => Close($"{_r:X2}{_g:X2}{_b:X2}");
        btnRow.Children.Add(btnOk);
        DockPanel.SetDock(btnRow, Dock.Bottom);
        root.Children.Add(btnRow);

        // R / G / B sliders fill the middle.
        var sliders = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        _rs = MakeChannelRow(sliders, "R", _r, out _rv);
        _gs = MakeChannelRow(sliders, "G", _g, out _gv);
        _bs = MakeChannelRow(sliders, "B", _b, out _bv);
        _rs.ValueChanged += (_, _) => OnSliderChanged();
        _gs.ValueChanged += (_, _) => OnSliderChanged();
        _bs.ValueChanged += (_, _) => OnSliderChanged();
        root.Children.Add(sliders);

        Content = root;
        RefreshAll(fromHex: false);
    }

    private Slider MakeChannelRow(StackPanel host, string label, int value, out TextBlock valueLabel)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,8,*,8,32") };
        var lbl = new TextBlock
        {
            Text = label, Foreground = PanelText, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        row.Children.Add(lbl);
        var slider = new Slider
        {
            Minimum = 0, Maximum = 255, Value = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(slider, 2);
        row.Children.Add(slider);
        valueLabel = new TextBlock
        {
            Text = value.ToString(), Foreground = MutedText, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(valueLabel, 4);
        row.Children.Add(valueLabel);
        host.Children.Add(row);
        return slider;
    }

    private static LinearGradientBrush BuildHueGradient()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        void Stop(double off, string rgb) =>
            b.GradientStops.Add(new GradientStop(Color.Parse("#" + rgb), off));
        Stop(0.0000, "FF0000");
        Stop(0.1667, "FFFF00");
        Stop(0.3333, "00FF00");
        Stop(0.5000, "00FFFF");
        Stop(0.6667, "0000FF");
        Stop(0.8333, "FF00FF");
        Stop(1.0000, "FF0000");
        return b;
    }

    private void ApplyHueFromPointer(Border strip, PointerEventArgs e)
    {
        double w = strip.Bounds.Width;
        if (w <= 0) return;
        double x = Math.Clamp(e.GetPosition(strip).X, 0, w);
        double hue = (x / w) * 360.0;
        (_r, _g, _b) = HueToRgb(hue);
        RefreshAll(fromHex: false);
    }

    private void OnSliderChanged()
    {
        if (_suppress) return;
        _r = (int)Math.Round(_rs.Value);
        _g = (int)Math.Round(_gs.Value);
        _b = (int)Math.Round(_bs.Value);
        RefreshAll(fromHex: false);
    }

    private void OnHexEdited()
    {
        if (_suppress) return;
        var parsed = ParseRgb(_hex.Text);
        if (parsed == null) return;          // ignore partial / invalid input while typing
        (_r, _g, _b) = parsed.Value;
        RefreshAll(fromHex: true);
    }

    private void RefreshAll(bool fromHex)
    {
        _suppress = true;
        _rs.Value = _r; _gs.Value = _g; _bs.Value = _b;
        _rv.Text = _r.ToString(); _gv.Text = _g.ToString(); _bv.Text = _b.ToString();
        if (!fromHex) _hex.Text = $"{_r:X2}{_g:X2}{_b:X2}";
        _preview.Background = new SolidColorBrush(Color.FromRgb((byte)_r, (byte)_g, (byte)_b));
        _suppress = false;
    }

    /// <summary>HSV→RGB at full saturation/value (the hue strip is pure hue).</summary>
    private static (int r, int g, int b) HueToRgb(double hueDeg)
    {
        double h = (hueDeg % 360 + 360) % 360 / 60.0;
        double x = 1 - Math.Abs(h % 2 - 1);
        double r = 0, g = 0, b = 0;
        switch ((int)Math.Floor(h) % 6)
        {
            case 0: r = 1; g = x; break;
            case 1: r = x; g = 1; break;
            case 2: g = 1; b = x; break;
            case 3: g = x; b = 1; break;
            case 4: r = x; b = 1; break;
            case 5: r = 1; b = x; break;
        }
        return ((int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
    }

    private static (int r, int g, int b)? ParseRgb(string? rgb)
    {
        if (string.IsNullOrWhiteSpace(rgb)) return null;
        var s = rgb.Trim().TrimStart('#');
        if (s.Length != 6) return null;
        if (!int.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r)
            || !int.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g)
            || !int.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
            return null;
        return (r, g, b);
    }
}
