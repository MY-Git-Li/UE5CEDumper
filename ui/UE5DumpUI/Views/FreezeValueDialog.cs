using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.Views;

/// <summary>
/// Minimal single-input modal for capturing the freeze target value.
///
/// Triggered from <c>PropertySearchPanel</c>'s row-level "Freeze" button.
/// Shows the target (class / prop / offset / type) read-only so the user
/// can verify they're freezing the right thing, then collects ONE typed
/// value. Validates against the property's UE type before accepting.
///
/// Returns the validated Lua literal on OK (e.g. <c>"100.0"</c>, <c>"true"</c>);
/// returns <c>null</c> on Cancel or invalid input that the user dismissed.
///
/// Code-behind only (no XAML / CompiledBinding) for AOT compatibility,
/// matching the project convention (see <see cref="ObjectInstancePickerDialog"/>).
/// </summary>
public sealed class FreezeValueDialog : Window
{
    private readonly PropertySearchMatch _match;
    private readonly string _helperType;
    private TextBox _valueBox = null!;
    private TextBlock _errorLabel = null!;
    private Button _btnOk = null!;
    private Button _btnCancel = null!;

    /// <summary>Validated Lua literal (e.g. <c>"42"</c>, <c>"3.14"</c>,
    /// <c>"true"</c>). Null when the user cancels.</summary>
    public string? ValueLiteral { get; private set; }

    public FreezeValueDialog(PropertySearchMatch match)
    {
        _match = match;
        _helperType = FreezeScriptGenerator.MapToHelperType(match.PropType);

        Title = "Freeze property value";
        Width = 520;
        MinWidth = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        var root = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 8,
        };

        // Read-only target details
        root.Children.Add(BuildLabelRow("Class:",    _match.ClassName));
        root.Children.Add(BuildLabelRow("Property:", _match.PropName));
        root.Children.Add(BuildLabelRow("Type:",     $"{_match.PropType} -> {_helperType}"));
        root.Children.Add(BuildLabelRow("Offset:",   _match.OffsetHex));

        // Value input
        var valueLbl = new TextBlock
        {
            Text = $"Freeze value ({_helperType}):",
            Foreground = new SolidColorBrush(Color.Parse("#DCDCAA")),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 2),
        };
        root.Children.Add(valueLbl);

        _valueBox = new TextBox
        {
            Text = SuggestedDefault(_helperType),
            FontSize = 13,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            Padding = new Thickness(6, 4),
        };
        _valueBox.KeyDown += OnValueKeyDown;
        root.Children.Add(_valueBox);

        // Inline error label (initially blank)
        _errorLabel = new TextBlock
        {
            Text = "",
            Foreground = new SolidColorBrush(Color.Parse("#F48771")),
            FontSize = 11,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 14,
        };
        root.Children.Add(_errorLabel);

        // Hint about bool acceptance
        if (_helperType == "bool")
        {
            root.Children.Add(new TextBlock
            {
                Text = "Accepts: true / false / 1 / 0",
                Foreground = new SolidColorBrush(Color.Parse("#808080")),
                FontSize = 11,
            });
        }

        // Buttons
        var btnRow = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };

        _btnCancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4) };
        _btnCancel.Click += (_, _) => { ValueLiteral = null; Close(); };
        btnRow.Children.Add(_btnCancel);

        _btnOk = new Button
        {
            Content = "Create freeze script",
            Padding = new Thickness(14, 4),
            IsDefault = true,
        };
        _btnOk.Click += OnOkClicked;
        btnRow.Children.Add(_btnOk);

        root.Children.Add(btnRow);

        Content = root;

        // Focus the value box on open so the user can type immediately.
        Opened += (_, _) => _valueBox.Focus();
    }

    private static StackPanel BuildLabelRow(string label, string value)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.Parse("#9CDCFE")),
            FontSize = 12,
            Width = 80,
        });
        row.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
        });
        return row;
    }

    private static string SuggestedDefault(string helperType) => helperType switch
    {
        "bool"              => "true",
        "float" or "double" => "9999.0",
        ""                  => "",  // unsupported type
        _                   => "9999",
    };

    private void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnOkClicked(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        var input = _valueBox.Text ?? "";
        var literal = ValidateAndConvert(input, _helperType, out var err);
        if (literal == null)
        {
            _errorLabel.Text = err;
            return;
        }
        ValueLiteral = literal;
        Close();
    }

    /// <summary>
    /// Convert user input to a Lua literal expression for the given
    /// helper type, OR return null with an error message in <paramref name="err"/>.
    /// </summary>
    public static string? ValidateAndConvert(string input, string helperType, out string err)
    {
        err = "";
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0)
        {
            err = "Value cannot be empty";
            return null;
        }

        switch (helperType)
        {
            case "bool":
                var lower = trimmed.ToLowerInvariant();
                if (lower is "true" or "1") return "true";
                if (lower is "false" or "0") return "false";
                err = "Expected: true / false / 1 / 0";
                return null;

            case "float" or "double":
                // TryParse accepts "NaN"/"Infinity" and overflow rounds to ±Infinity, and
                // ToString("R") then emits the bare word — not a Lua number literal, so the
                // emitted script reads it as an undefined global (nil) and freezes nothing.
                // Reject at the dialog, where the user can still see why. (B23)
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var dv)
                    || !double.IsFinite(dv))
                {
                    err = $"Not a valid {helperType} number";
                    return null;
                }
                return dv.ToString("R", CultureInfo.InvariantCulture);

            case "int8" or "int16" or "int32" or "int64":
                if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv))
                {
                    err = $"Not a valid {helperType} integer";
                    return null;
                }
                return sv.ToString(CultureInfo.InvariantCulture);

            case "uint8" or "uint16" or "uint32" or "uint64":
                if (!ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uv))
                {
                    err = $"Not a valid {helperType} unsigned integer";
                    return null;
                }
                return uv.ToString(CultureInfo.InvariantCulture);

            case "":
                err = "Type not supported by freeze v1 -- numerics + bool only";
                return null;

            default:
                err = $"Internal: unhandled helper type '{helperType}'";
                return null;
        }
    }
}
