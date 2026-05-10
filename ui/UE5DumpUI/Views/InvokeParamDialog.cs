using System.Buffers.Binary;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;

namespace UE5DumpUI.Views;

/// <summary>
/// Two entry-points for this dialog (each opened by a different LiveWalker
/// button on a UFunction row):
/// <list type="bullet">
/// <item><see cref="PipeInvoke"/> -- shows FIRE + Copy AA Script + Cancel.
///     User can either test the call live in-app via the pipe, or bake the
///     current form values into a redistributable AA Script.</item>
/// <item><see cref="CopyBakedScript"/> -- shows Copy AA Script + Cancel
///     only. FIRE is hidden so a user opening the dialog from the
///     "Copy AA Script (Baked)" button can't accidentally invoke the
///     function on their live game.</item>
/// </list>
/// </summary>
public enum InvokeDialogMode
{
    /// <summary>FIRE + Copy AA Script + Cancel. Default.</summary>
    PipeInvoke,
    /// <summary>Copy AA Script + Cancel. FIRE button hidden.</summary>
    CopyBakedScript,
}

/// <summary>
/// Modal dialog for UFunction invocation. Either fires the call live via
/// the pipe (FIRE button) or bakes the current form values into a
/// redistributable AA Script (Copy AA Script button), depending on the
/// <see cref="InvokeDialogMode"/> the dialog was opened in.
///
/// FIRE executes ProcessEvent via pipe and displays decoded results inline.
/// The dialog stays open after invocation so the user can read return values.
/// Copy AA Script generates a script via <see cref="BakedScriptGenerator"/>,
/// pushes it to AOBMaker (or clipboard fallback), and closes.
///
/// Returns: "ok" if any action completed, null if cancelled.
/// </summary>
public sealed class InvokeParamDialog : Window
{
    private readonly List<TextBox> _edits = new();
    private readonly IReadOnlyList<FunctionParamModel> _inputParams;
    private readonly IReadOnlyList<FunctionParamModel> _allParams;
    private readonly int _parmsSize;
    private readonly string _className;
    private readonly string _funcName;
    private readonly string _instanceAddr;
    private readonly IDumpService _dump;
    private readonly IAobMakerBridge? _aobMaker;
    private readonly IPlatformService? _platform;
    private readonly int _ueVersion;
    private readonly InvokeDialogMode _mode;

    // Struct expansion: param index → list of (sub-field, TextBox) pairs
    // Uses DynamicStructField as unified type for both known and DLL-discovered layouts.
    private readonly Dictionary<int, List<(DynamicStructField sf, TextBox edit)>> _structEdits = new();

    private TextBlock _resultLabel = null!;
    private Button _btnFire = null!;
    private Button _btnCopyBaked = null!;
    private Button _btnClose = null!;
    private int _fireCount;

    public InvokeParamDialog(
        string className, string funcName,
        IReadOnlyList<FunctionParamModel> inputParams,
        IReadOnlyList<FunctionParamModel> allParams,
        int parmsSize,
        string instanceAddr,
        IDumpService dump,
        int ueVersion = 0,
        IAobMakerBridge? aobMaker = null,
        IPlatformService? platform = null,
        InvokeDialogMode mode = InvokeDialogMode.PipeInvoke)
    {
        _inputParams = inputParams;
        _allParams = allParams;
        _parmsSize = parmsSize;
        _className = className;
        _funcName = funcName;
        _instanceAddr = instanceAddr;
        _dump = dump;
        _aobMaker = aobMaker;
        _platform = platform;
        _ueVersion = ueVersion;
        _mode = mode;

        Title = $"Invoke: {className}::{funcName}";
        Width = 560;
        MinWidth = 420;
        // Default starting Height grows up to a sensible cap; on a small
        // screen Avalonia clamps to the work area for us. The hard
        // MaxHeight=700 cap from the previous version was the actual
        // overflow bug -- on a 4K monitor a 12-param function still
        // showed a tiny scrollable strip when there was room for the
        // whole form. Bumping to 1100 lets the user see all params on
        // typical 1080p+ displays without manual resize, while
        // CanResize=true still lets them shrink it.
        Height = 480;
        MaxHeight = 1100;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        SizeToContent = SizeToContent.Height;  // grow to fit form, then cap at MaxHeight
        Background = new SolidColorBrush(Color.Parse("#1E1E1E"));

        var root = new DockPanel { Margin = new Thickness(16) };

        // Header (top, fixed)
        var header = new TextBlock
        {
            Text = $"{className}::{funcName}  (ParmsSize={parmsSize})",
            Foreground = new SolidColorBrush(Color.Parse("#DCDCAA")),
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Bottom panel: buttons + result (fixed at bottom)
        var bottomPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 8,
        };

        _btnFire = new Button
        {
            Content = "FIRE",
            Width = 100,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")),
            Background = new SolidColorBrush(Color.Parse("#4E7A25")),
            // CopyBakedScript mode: hide FIRE so the user can't accidentally
            // invoke the function on their live game when they only meant to
            // export an AA Script.
            IsVisible = _mode == InvokeDialogMode.PipeInvoke,
        };
        _btnFire.Click += OnFireClicked;

        // New: Copy AA Script (Baked) -- bakes current form values into a
        // self-contained AA Script via BakedScriptGenerator and hands it
        // to AOBMaker (if connected) or the clipboard. Available in BOTH
        // modes because the user filling the form may decide to either
        // FIRE-then-export or export-without-firing.
        _btnCopyBaked = new Button
        {
            Content = "Copy AA Script",
            Width = 130,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFF")),
            Background = new SolidColorBrush(Color.Parse("#3F6FA8")),
        };
        Avalonia.Controls.ToolTip.SetTip(_btnCopyBaked,
            "Bake current values into a CE AA Script + copy to clipboard / AOBMaker. " +
            "Requires ue5_invoke_helper.lua to be embedded in your CE table " +
            "(Table -> Add File...).");
        _btnCopyBaked.Click += OnCopyBakedScriptClicked;

        _btnClose = new Button
        {
            Content = "Close",
            Width = 80,
            // CopyBakedScript mode: no FIRE means no result to read; the
            // user finishes via Copy or Cancel, so Close adds noise.
            IsVisible = _mode == InvokeDialogMode.PipeInvoke,
        };
        _btnClose.Click += (_, _) => Close("ok");

        var btnCancel = new Button
        {
            Content = "Cancel",
            Width = 80,
        };
        btnCancel.Click += (_, _) => Close(null);

        btnPanel.Children.Add(_btnFire);
        btnPanel.Children.Add(_btnCopyBaked);
        btnPanel.Children.Add(_btnClose);
        btnPanel.Children.Add(btnCancel);
        bottomPanel.Children.Add(btnPanel);

        // Result area
        _resultLabel = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#4EC9B0")),
            FontSize = 12,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
        };
        bottomPanel.Children.Add(_resultLabel);

        DockPanel.SetDock(bottomPanel, Dock.Bottom);
        root.Children.Add(bottomPanel);

        // Scrollable param fields (fills remaining space)
        var paramPanel = new StackPanel();

        if (inputParams.Count == 0)
        {
            paramPanel.Children.Add(new TextBlock
            {
                Text = "(no input parameters -- will invoke directly)",
                Foreground = new SolidColorBrush(Color.Parse("#808080")),
                FontSize = 12,
                Margin = new Thickness(0, 4),
            });
        }
        else
        {
            for (int i = 0; i < inputParams.Count; i++)
            {
                var p = inputParams[i];

                // Check if this is a known struct that we can expand
                var structLayout = (p.TypeName == "StructProperty" && !string.IsNullOrEmpty(p.StructName))
                    ? KnownStructLayouts.GetLayout(p.StructName, _ueVersion)
                    : null;

                // Build a unified sub-field list from known layout or DLL-discovered fields
                IReadOnlyList<DynamicStructField>? expandFields = null;
                string expandSource;
                if (structLayout != null)
                {
                    // Known engine struct (version-aware LWC)
                    expandFields = structLayout.Fields
                        .Select(sf => new DynamicStructField(sf.Name, sf.TypeName, sf.Offset, sf.Size))
                        .ToList();
                    expandSource = "known";
                }
                else if (p.TypeName == "StructProperty" && p.StructFields.Count > 0)
                {
                    // Phase B: DLL-discovered dynamic struct layout
                    expandFields = p.StructFields;
                    expandSource = "dynamic";
                }
                else
                {
                    expandFields = null;
                    expandSource = "";
                }

                if (expandFields != null && expandFields.Count > 0)
                {
                    // Expand struct into sub-fields (works for both known and dynamic)
                    var structName = !string.IsNullOrEmpty(p.StructName) ? p.StructName : "struct";
                    var sourceTag = expandSource == "dynamic" ? " ⚡" : "";
                    var structLabel = $"{p.Name}  [F{structName}{sourceTag}, {p.Size}B, off={p.Offset}{(p.IsOut ? ", out" : "")}]";
                    paramPanel.Children.Add(new TextBlock
                    {
                        Text = structLabel,
                        Foreground = new SolidColorBrush(Color.Parse("#569CD6")),
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(0, 4, 0, 0),
                    });

                    // Placeholder in _edits (not used for structs — _structEdits handles them)
                    _edits.Add(null!);

                    var subEdits = new List<(DynamicStructField, TextBox)>();
                    foreach (var sf in expandFields)
                    {
                        var sfShortType = ParamBufferBuilder.ShortTypeName(sf.TypeName);
                        var sfLabel = $"  .{sf.Name}  [{sfShortType}]";

                        var row = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("Auto,8,*"),
                            Margin = new Thickness(12, 1),
                        };

                        var lbl = new TextBlock
                        {
                            Text = sfLabel,
                            Foreground = new SolidColorBrush(Color.Parse("#9CDCFE")),
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            MinWidth = 260,
                        };
                        Grid.SetColumn(lbl, 0);
                        row.Children.Add(lbl);

                        var edt = new TextBox
                        {
                            Text = ParamBufferBuilder.GetDefaultValue(sf.TypeName),
                            MinWidth = 120,
                            FontSize = 12,
                            Padding = new Thickness(4, 2),
                        };
                        Grid.SetColumn(edt, 2);
                        row.Children.Add(edt);
                        subEdits.Add((sf, edt));

                        paramPanel.Children.Add(row);
                    }

                    _structEdits[i] = subEdits;
                }
                else
                {
                    // Normal scalar param
                    var shortType = ParamBufferBuilder.ShortTypeName(p.TypeName);
                    var structSuffix = (p.TypeName == "StructProperty" && !string.IsNullOrEmpty(p.StructName))
                        ? $" ({p.StructName})"
                        : "";
                    var label = $"{p.Name}  [{shortType}{structSuffix}, {p.Size}B, off={p.Offset}{(p.IsOut ? ", out" : "")}]";

                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,8,*"),
                        Margin = new Thickness(0, 2),
                    };

                    var lbl = new TextBlock
                    {
                        Text = label,
                        Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        MinWidth = 280,
                    };
                    Grid.SetColumn(lbl, 0);
                    row.Children.Add(lbl);

                    var edt = new TextBox
                    {
                        Text = ParamBufferBuilder.GetDefaultValue(p.TypeName),
                        MinWidth = 120,
                        FontSize = 12,
                        Padding = new Thickness(4, 2),
                    };
                    Grid.SetColumn(edt, 2);
                    row.Children.Add(edt);
                    _edits.Add(edt);

                    paramPanel.Children.Add(row);
                }
            }
        }

        var scroll = new ScrollViewer
        {
            Content = paramPanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            // Floor on the scrollable area: when the FIRE result panel
            // grows after a successful invoke (decoded post-call buffer
            // listing), DockPanel would otherwise let the bottom panel
            // squish the scroll area down to a sliver. 200px keeps at
            // least ~6 param rows visible regardless of result-area
            // expansion.
            MinHeight = 200,
        };

        root.Children.Add(scroll);
        Content = root;
    }

    private async void OnFireClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _btnFire.IsEnabled = false;
        _btnFire.Content = "FIRING...";
        _resultLabel.IsVisible = true;
        _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#808080"));
        _resultLabel.Text = "Invoking ProcessEvent...";
        _fireCount++;

        try
        {
            // Build param hex from input fields (struct-aware)
            string? paramsHex = null;
            if (_inputParams.Count > 0 && _parmsSize > 0)
            {
                var buf = new byte[_parmsSize];
                for (int i = 0; i < _inputParams.Count; i++)
                {
                    var param = _inputParams[i];
                    if (param.Offset < 0 || param.Offset >= _parmsSize) continue;

                    if (_structEdits.TryGetValue(i, out var subEdits))
                    {
                        // Struct param: write each sub-field (DynamicStructField overload)
                        var subFields = subEdits.Select(se => se.sf).ToArray();
                        var subValues = subEdits.Select(se => se.edit.Text ?? "0").ToArray();
                        ParamBufferBuilder.WriteStructParam(buf, param.Offset,
                            (IReadOnlyList<DynamicStructField>)subFields, subValues);
                    }
                    else
                    {
                        // Scalar param
                        var text = (_edits[i]?.Text ?? "0").Trim();
                        ParamBufferBuilder.WriteParam(buf, param.Offset, param.TypeName, param.Size, text);
                    }
                }
                paramsHex = Convert.ToHexString(buf);
            }

            // Execute via pipe
            var result = await _dump.InvokeFunctionAsync(
                _funcName,
                instanceAddr: _instanceAddr,
                parmsSize: _parmsSize,
                paramsHex: paramsHex);

            // Display results
            ShowResult(result);
        }
        catch (Exception ex)
        {
            _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#F44747"));
            _resultLabel.Text = $"ERROR: {ex.Message}";
        }
        finally
        {
            _btnFire.IsEnabled = true;
            _btnFire.Content = $"FIRE ({_fireCount})";
        }
    }

    private void ShowResult(InvokeFunctionResult result)
    {
        _resultLabel.IsVisible = true;

        var lines = new List<string>();

        if (result.Success)
        {
            lines.Add($"[#{_fireCount}] ProcessEvent OK  (result={result.Result})");
        }
        else
        {
            var errorDetail = !string.IsNullOrEmpty(result.Error)
                ? result.Error
                : $"error code {result.Result}";
            lines.Add($"[#{_fireCount}] INVOKE FAILED: {errorDetail}");
            _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#F44747"));
            _resultLabel.Text = string.Join("\n", lines);
            return;
        }

        // Decode ALL param values from the post-call buffer
        // (shows return values, out params, and even input params after the call)
        if (!string.IsNullOrEmpty(result.ResultHex) && _parmsSize > 0)
        {
            try
            {
                var bytes = HexToBytes(result.ResultHex);
                lines.Add("--- Post-call buffer ---");

                foreach (var p in _allParams)
                {
                    // Use struct-aware decoding for known structs, then dynamic, then scalar
                    string decoded;
                    var structLayout = (p.TypeName == "StructProperty" && !string.IsNullOrEmpty(p.StructName))
                        ? KnownStructLayouts.GetLayout(p.StructName, _ueVersion)
                        : null;
                    if (structLayout != null)
                        decoded = DecodeStructParamValue(bytes, p, structLayout);
                    else if (p.TypeName == "StructProperty" && p.StructFields.Count > 0)
                        decoded = DecodeDynamicStructParamValue(bytes, p);
                    else
                        decoded = DecodeParamValue(bytes, p);

                    var tag = p.IsReturn ? " (return)"
                            : p.IsOut ? " (out)"
                            : "";
                    // Highlight return/out params, or detect by name convention
                    var isReturnByName = p.Name.Contains("ReturnValue", StringComparison.OrdinalIgnoreCase);
                    if (isReturnByName && !p.IsReturn)
                        tag = " (return*)";  // * = detected by name, not flag

                    lines.Add($"  {p.Name}{tag} = {decoded}");
                }

                // Also show raw hex (truncated)
                var rawHex = result.ResultHex.Length > 64
                    ? result.ResultHex[..64] + "..."
                    : result.ResultHex;
                lines.Add($"  raw: {rawHex}");
            }
            catch
            {
                lines.Add($"  result_hex: {result.ResultHex}");
            }
        }

        _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#4EC9B0"));
        _resultLabel.Text = string.Join("\n", lines);
    }

    /// <summary>
    /// "Copy AA Script (Baked)" handler: snapshot the current form values,
    /// flatten any expanded structs into individual scalar entries (each
    /// at parent_offset + sub_offset), generate the AA Script, and ship
    /// it via AOBMaker / clipboard.
    /// </summary>
    private async void OnCopyBakedScriptClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _btnCopyBaked.IsEnabled = false;
        _resultLabel.IsVisible = true;
        _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#808080"));
        _resultLabel.Text = "Generating AA Script...";

        try
        {
            var bakedValues = CollectBakedValues();
            var script = BakedScriptGenerator.Generate(
                _className, _funcName, _parmsSize, bakedValues);

            // Prefer AOBMaker (creates the AA Script entry directly in CE);
            // fall back to clipboard for users running without the plugin.
            // Sample IsAvailable BEFORE the send so we can distinguish
            // 'pipe broke mid-send' (was available, now isn't) from
            // 'never configured' (was already false) in the result message.
            var description = $"Invoke (baked): {_className}::{_funcName}";
            bool wasAvailable = _aobMaker?.IsAvailable ?? false;
            bool sentToCe = false;
            if (_aobMaker != null && wasAvailable)
            {
                sentToCe = await _aobMaker.CreateAAScriptAsync(
                    description, script, autoActivate: false);
            }

            if (sentToCe)
            {
                _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#4EC9B0"));
                _resultLabel.Text = $"AA Script created in CE: {description}\n" +
                    $"({bakedValues.Count} baked param(s); helper file required " +
                    "in your .CT)";
            }
            else if (_platform != null)
            {
                await _platform.CopyToClipboardAsync(script);
                if (wasAvailable)
                {
                    // Pipe was up at start; send failed. CE likely closed
                    // between availability check and send.
                    _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#E0A050"));
                    _resultLabel.Text =
                        $"⚠ AOBMaker pipe broke mid-send (CE closed?).\n" +
                        $"AA Script copied to clipboard ({script.Length:N0} chars) " +
                        "as a fallback.\nPaste into a new CE AA Script entry once CE is ready.";
                }
                else
                {
                    _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#4EC9B0"));
                    _resultLabel.Text = $"AOBMaker not connected.\nAA Script copied to clipboard " +
                        $"({script.Length:N0} chars).\nDon't forget to embed " +
                        $"ue5_invoke_helper.lua in your .CT (Table -> Add File...).";
                }
            }
            else
            {
                _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#F44747"));
                _resultLabel.Text = "ERROR: No clipboard service and AOBMaker " +
                    "unavailable -- cannot deliver script.";
                return;
            }

            // CopyBakedScript mode only: auto-close after a short delay so
            // the user reads the success message but doesn't have to click.
            // PipeInvoke mode keeps the dialog open so the user can also FIRE.
            if (_mode == InvokeDialogMode.CopyBakedScript)
            {
                await Task.Delay(800);
                Close("ok");
            }
        }
        catch (Exception ex)
        {
            _resultLabel.Foreground = new SolidColorBrush(Color.Parse("#F44747"));
            _resultLabel.Text = $"ERROR: {ex.Message}";
        }
        finally
        {
            _btnCopyBaked.IsEnabled = true;
        }
    }

    /// <summary>
    /// Snapshot the form into a flat list of <see cref="BakedParamValue"/>.
    /// Struct params are flattened to one entry per sub-field (offset =
    /// parent.Offset + subfield.Offset, name = "Parent.Sub"), so the
    /// generator + helper handle them as ordinary scalars.
    /// </summary>
    internal IReadOnlyList<BakedParamValue> CollectBakedValues()
    {
        var list = new List<BakedParamValue>(_inputParams.Count);
        for (int i = 0; i < _inputParams.Count; i++)
        {
            var p = _inputParams[i];
            if (p.Offset < 0 || p.Offset >= _parmsSize) continue;

            if (_structEdits.TryGetValue(i, out var subEdits))
            {
                // Struct param: emit one BakedParamValue per sub-field
                foreach (var (sf, edit) in subEdits)
                {
                    list.Add(new BakedParamValue(
                        ParamName:   $"{p.Name}.{sf.Name}",
                        UeTypeName:  sf.TypeName,
                        Size:        sf.Size,
                        Offset:      p.Offset + sf.Offset,
                        LiteralText: (edit.Text ?? "0").Trim()));
                }
            }
            else
            {
                var text = (_edits[i]?.Text ?? "0").Trim();
                list.Add(new BakedParamValue(
                    ParamName:   p.Name,
                    UeTypeName:  p.TypeName,
                    Size:        p.Size,
                    Offset:      p.Offset,
                    LiteralText: text));
            }
        }
        return list;
    }

    /// <summary>Decode a single param value from the post-call buffer bytes.</summary>
    internal static string DecodeParamValue(byte[] buf, FunctionParamModel p)
    {
        if (p.Offset < 0 || p.Offset >= buf.Length) return "?";
        int available = buf.Length - p.Offset;
        var span = buf.AsSpan(p.Offset);

        return p.TypeName switch
        {
            "BoolProperty" => buf[p.Offset] != 0 ? "true" : "false",
            "ByteProperty" or "Int8Property" => buf[p.Offset].ToString(),
            "Int16Property" when available >= 2
                => BinaryPrimitives.ReadInt16LittleEndian(span).ToString(),
            "UInt16Property" when available >= 2
                => BinaryPrimitives.ReadUInt16LittleEndian(span).ToString(),
            "FloatProperty" when available >= 4
                => BinaryPrimitives.ReadSingleLittleEndian(span).ToString(CultureInfo.InvariantCulture),
            "DoubleProperty" when available >= 8
                => BinaryPrimitives.ReadDoubleLittleEndian(span).ToString(CultureInfo.InvariantCulture),
            "IntProperty" or "UInt32Property" or "EnumProperty" when available >= 4
                => BinaryPrimitives.ReadInt32LittleEndian(span).ToString(),
            "Int64Property" when available >= 8
                => BinaryPrimitives.ReadInt64LittleEndian(span).ToString(),
            "UInt64Property" or "ObjectProperty" or "ClassProperty"
                or "NameProperty" or "SoftObjectProperty" or "WeakObjectProperty"
                or "InterfaceProperty" when available >= 8
                => $"0x{BinaryPrimitives.ReadUInt64LittleEndian(span):X}",
            _ => p.Size switch
            {
                1 => buf[p.Offset].ToString(),
                2 when available >= 2 => BinaryPrimitives.ReadInt16LittleEndian(span).ToString(),
                4 when available >= 4 => BinaryPrimitives.ReadInt32LittleEndian(span).ToString(),
                8 when available >= 8 => $"0x{BinaryPrimitives.ReadUInt64LittleEndian(span):X}",
                _ => BitConverter.ToString(buf, p.Offset, Math.Min(p.Size, available)),
            },
        };
    }

    /// <summary>Decode a struct param using DLL-discovered dynamic sub-fields. Returns "FieldA=val, FieldB=val" style.</summary>
    internal static string DecodeDynamicStructParamValue(byte[] buf, FunctionParamModel p)
    {
        if (p.StructFields.Count == 0) return DecodeParamValue(buf, p);

        var parts = new List<string>(p.StructFields.Count);
        foreach (var sf in p.StructFields)
        {
            var subParam = new FunctionParamModel
            {
                Name = sf.Name,
                TypeName = sf.TypeName,
                Size = sf.Size,
                Offset = p.Offset + sf.Offset,
            };
            var val = DecodeParamValue(buf, subParam);
            parts.Add($"{sf.Name}={val}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Decode a struct param using known sub-field layout. Returns "X=1.0, Y=2.0, Z=3.0" style.</summary>
    internal static string DecodeStructParamValue(byte[] buf, FunctionParamModel p,
        KnownStructLayouts.StructLayout layout)
    {
        var parts = new List<string>(layout.Fields.Count);
        foreach (var sf in layout.Fields)
        {
            var subParam = new FunctionParamModel
            {
                Name = sf.Name,
                TypeName = sf.TypeName,
                Size = sf.Size,
                Offset = p.Offset + sf.Offset,
            };
            var val = DecodeParamValue(buf, subParam);
            parts.Add($"{sf.Name}={val}");
        }
        return string.Join(", ", parts);
    }

    internal static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber);
        }
        return bytes;
    }
}
