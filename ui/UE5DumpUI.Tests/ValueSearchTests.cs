using System.IO;
using System.Text.Json.Nodes;
using UE5DumpUI.Core;
using UE5DumpUI.Models;
using UE5DumpUI.Services;
using UE5DumpUI.ViewModels;
using Xunit;

namespace UE5DumpUI.Tests;

/// <summary>
/// Tests for the Value Search feature (build 733+, port from
/// discrete Phase 27b shape):
///
/// - <see cref="DumpService"/>: JSON wire round-trips for the three
///   value-scan commands (begin / refine / end). Locks the field name
///   mapping so a DLL rename of a JSON key fails here at build time
///   rather than at user-runtime.
/// - <see cref="ValueSearchViewModel"/>: First-Scan / Next-Scan /
///   New-Scan workflow with a fake dump service. Verifies the
///   First-Scan-only / Prev-Value-only contract enforcement.
/// - Banner contract: the Native-C++-fields-unreachable banner in
///   ValueSearchPanel.axaml is locked in by literal-text assertion.
///   This is a project-memory UX rule (project_value_search_caveats).
/// </summary>
public class ValueSearchTests
{
    // ------------------------------------------------------------------
    // Service-level: DumpService → wire JSON → parsed model
    // ------------------------------------------------------------------

    private static DumpService MakeService(out MockPipeClient pipe)
    {
        pipe = new MockPipeClient();
        return new DumpService(pipe, new MockLoggingService());
    }

    [Fact]
    public async Task BeginValueScanAsync_BuildsCorrectRequest()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 7UL,
                ["data_type"]       = "Int32",
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 12L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        var res = await svc.BeginValueScanAsync(
            ValueScanDataType.Int32, ValueScanType.Between,
            value: "10", value2: "20",
            gameOnly: true, maxResults: 1234,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("begin_value_scan", captured!["cmd"]?.GetValue<string>());
        Assert.Equal("Int32",            captured["data_type"]?.GetValue<string>());
        Assert.Equal("Between",          captured["scan_type"]?.GetValue<string>());
        Assert.Equal("10",               captured["value"]?.GetValue<string>());
        Assert.Equal("20",               captured["value2"]?.GetValue<string>());
        Assert.Equal(true,               captured["game_only"]?.GetValue<bool>());
        Assert.Equal(1234,               captured["max_results"]?.GetValue<int>());

        Assert.Equal(7UL, res.SessionId);
        Assert.Equal("Int32", res.DataType);
        Assert.Equal(12L, res.DurationMs);
    }

    [Fact]
    public async Task BeginValueScanAsync_OmitsValue2WhenNotBetween()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = "Float",
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            ValueScanDataType.Float, ValueScanType.Exact,
            value: "3.14", value2: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured!.ContainsKey("value2"),
            "value2 must not be sent for non-Between scans");
    }

    [Fact]
    public async Task BeginValueScanAsync_ParsesCandidates()
    {
        var svc = MakeService(out var pipe);
        pipe.SetHandler(req => new JsonObject
        {
            ["id"]              = req["id"]?.GetValue<int>() ?? 0,
            ["ok"]              = true,
            ["session_id"]      = 42UL,
            ["data_type"]       = "Int32",
            ["total"]           = 1,
            ["scanned_classes"] = 100,
            ["scanned_objects"] = 1000,
            ["duration_ms"]     = 50L,
            ["deadline_hit"]    = false,
            ["candidates"]      = new JsonArray
            {
                new JsonObject
                {
                    ["addr"]                = "0x7FF601234560",
                    ["instance_addr"]       = "0x7FF601234540",
                    ["instance_index"]      = 12345,
                    ["field_offset"]        = 0x20,
                    ["instance_name"]       = "PlayerPawn_0",
                    ["class_name"]          = "BP_Player_C",
                    ["defining_class_name"] = "ACharacter",
                    ["field_name"]          = "Health",
                    ["field_type"]          = "FloatProperty",
                    ["bool_field_mask"]     = 255,
                    ["value"]               = "100",
                },
            },
        });

        var res = await svc.BeginValueScanAsync(
            ValueScanDataType.Int32, ValueScanType.Exact, "100",
            ct: TestContext.Current.CancellationToken);

        Assert.Single(res.Candidates);
        var c = res.Candidates[0];
        Assert.Equal("0x7FF601234560", c.Addr);
        Assert.Equal("PlayerPawn_0",   c.InstanceName);
        Assert.Equal("BP_Player_C",    c.ClassName);
        Assert.Equal("ACharacter",     c.DefiningClassName);
        Assert.Equal("Health",         c.FieldName);
        Assert.Equal(0x20,             c.FieldOffset);
        Assert.Equal("100",            c.Value);
        Assert.Equal("0x20",           c.OffsetHex);

        // LocationLabel surfaces inheritance when defining differs.
        Assert.Equal("BP_Player_C.Health  (ACharacter)", c.LocationLabel);
    }

    [Fact]
    public async Task RefineValueScanAsync_OmitsValueForPrevScanType()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]            = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]            = true,
                ["session_id"]    = 1UL,
                ["data_type"]     = "Int32",
                ["scan_type"]     = "Changed",
                ["total"]         = 0,
                ["duration_ms"]   = 1L,
                ["candidates"]    = new JsonArray(),
            };
        });

        await svc.RefineValueScanAsync(
            sessionId: 1UL, scanType: ValueScanType.Changed,
            value: null, value2: null,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("refine_value_scan", captured!["cmd"]?.GetValue<string>());
        Assert.Equal(1UL,                 captured["session_id"]?.GetValue<ulong>());
        Assert.Equal("Changed",           captured["scan_type"]?.GetValue<string>());
        Assert.False(captured.ContainsKey("value"));
        Assert.False(captured.ContainsKey("value2"));
    }

    [Fact]
    public async Task BeginValueScanAsync_AttachesToleranceForFloat()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = "Float",
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            ValueScanDataType.Float, ValueScanType.Exact, "338",
            tolerance: 0.5,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("tolerance"));
        Assert.Equal(0.5, captured["tolerance"]?.GetValue<double>());
    }

    [Theory]
    [InlineData(ValueScanDataType.Int8)]
    [InlineData(ValueScanDataType.Int32)]
    [InlineData(ValueScanDataType.Int64)]
    [InlineData(ValueScanDataType.UInt32)]
    [InlineData(ValueScanDataType.Bool)]
    public async Task BeginValueScanAsync_OmitsToleranceForIntegerTypes(ValueScanDataType dt)
    {
        // Integer-typed scans must not carry tolerance on the wire even
        // when the caller supplies a non-zero value -- DLL ignores it
        // and the wire shape stays byte-identical to the pre-tolerance
        // protocol for the common (integer) case. Locks the
        // SupportsTolerance gating logic in DumpService.
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = dt.ToString(),
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            dt, ValueScanType.Exact, "10",
            tolerance: 5.0,   // explicitly non-zero; should still be dropped
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured!.ContainsKey("tolerance"),
            $"tolerance must not be on the wire for {dt}");
    }

    [Fact]
    public async Task BeginValueScanAsync_OmitsToleranceWhenZero()
    {
        // Even for Float, tolerance=0 means "exact" -- skip the field so
        // the existing exact-scan call sites stay byte-identical.
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = "Float",
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            ValueScanDataType.Float, ValueScanType.Exact, "100",
            tolerance: 0.0,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured!.ContainsKey("tolerance"));
    }

    [Fact]
    public async Task RefineValueScanAsync_AttachesToleranceWhenNonZero()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]          = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]          = true,
                ["session_id"]  = 1UL,
                ["data_type"]   = "Float",
                ["scan_type"]   = "Decreased",
                ["total"]       = 0,
                ["duration_ms"] = 1L,
                ["candidates"]  = new JsonArray(),
            };
        });

        await svc.RefineValueScanAsync(
            1UL, ValueScanType.Decreased,
            value: null, value2: null,
            tolerance: 0.5,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(0.5, captured!["tolerance"]?.GetValue<double>());
    }

    [Fact]
    public void ViewModel_SupportsTolerance_GatesByDataType()
    {
        var (vm, _) = MakeVm();
        vm.SelectedDataType = ValueScanDataType.Int32;
        Assert.False(vm.SupportsTolerance);
        vm.SelectedDataType = ValueScanDataType.Float;
        Assert.True(vm.SupportsTolerance);
        vm.SelectedDataType = ValueScanDataType.Double;
        Assert.True(vm.SupportsTolerance);
        vm.SelectedDataType = ValueScanDataType.UInt64;
        Assert.False(vm.SupportsTolerance);
    }

    [Fact]
    public async Task ViewModel_TolerancePassesThroughForFloat()
    {
        var (vm, fake) = MakeVm();
        fake.NextBeginResult = new ValueScanBeginResult { SessionId = 1UL };
        vm.SelectedDataType = ValueScanDataType.Float;
        vm.SelectedScanType = ValueScanType.Exact;
        vm.Value = "338";
        vm.Tolerance = 0.5;

        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.Single(fake.Begins);
        var (_, _, _, _, _, _, tol, _) = fake.Begins[0];
        Assert.Equal(0.5, tol);
    }

    [Fact]
    public async Task ViewModel_ToleranceIgnoredForIntegerType()
    {
        // Even though the user has Tolerance=2 set, an Int32 scan must
        // send tolerance=0 to the service (which then strips it from the
        // wire). Mirror of the wire-level OmitsToleranceForIntegerTypes
        // test, one layer up at the VM.
        var (vm, fake) = MakeVm();
        fake.NextBeginResult = new ValueScanBeginResult { SessionId = 1UL };
        vm.SelectedDataType = ValueScanDataType.Int32;
        vm.SelectedScanType = ValueScanType.Exact;
        vm.Value = "100";
        vm.Tolerance = 2.0;

        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.Single(fake.Begins);
        var (_, _, _, _, _, _, tol, _) = fake.Begins[0];
        Assert.Equal(0.0, tol);
    }

    [Fact]
    public async Task EndValueScanAsync_SendsSessionId()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]         = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]         = true,
                ["session_id"] = 99UL,
                ["ended"]      = true,
            };
        });

        await svc.EndValueScanAsync(99UL, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("end_value_scan", captured!["cmd"]?.GetValue<string>());
        Assert.Equal(99UL,             captured["session_id"]?.GetValue<ulong>());
    }

    // ------------------------------------------------------------------
    // Scan-type partition predicates (mirror DLL-side contract)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ValueScanType.Exact,      true,  false)]
    [InlineData(ValueScanType.Bigger,     true,  false)]
    [InlineData(ValueScanType.Smaller,    true,  false)]
    [InlineData(ValueScanType.Between,    true,  false)]
    [InlineData(ValueScanType.Changed,    false, true)]
    [InlineData(ValueScanType.Unchanged,  false, true)]
    [InlineData(ValueScanType.Increased,  false, true)]
    [InlineData(ValueScanType.Decreased,  false, true)]
    // Phase 2A: substring predicates are first-scan eligible (used as
    // narrowing predicates on the user's needle, like Exact).
    [InlineData(ValueScanType.Contains,   true,  false)]
    [InlineData(ValueScanType.StartsWith, true,  false)]
    [InlineData(ValueScanType.EndsWith,   true,  false)]
    public void ScanType_Partition_IsExhaustiveAndDisjoint(
        ValueScanType st, bool expectFirst, bool expectPrev)
    {
        Assert.Equal(expectFirst, ValueSearchViewModel.IsFirstScanType(st));
        Assert.Equal(expectPrev,  ValueSearchViewModel.IsPrevValueScanType(st));
        Assert.NotEqual(ValueSearchViewModel.IsFirstScanType(st),
                        ValueSearchViewModel.IsPrevValueScanType(st));
    }

    // ------------------------------------------------------------------
    // Phase 2: IsScanTypeValidFor partition (mirror of DLL contract).
    // String types: substring + Exact + Changed/Unchanged accept;
    //               numeric ordering predicates reject.
    // Vector / numeric types: substring predicates reject; ordering
    //                         predicates accept.
    // ------------------------------------------------------------------
    [Theory]
    // Numeric type accepts everything except substring predicates.
    [InlineData(ValueScanDataType.Int32, ValueScanType.Exact,      true)]
    [InlineData(ValueScanDataType.Int32, ValueScanType.Bigger,     true)]
    [InlineData(ValueScanDataType.Int32, ValueScanType.Smaller,    true)]
    [InlineData(ValueScanDataType.Int32, ValueScanType.Between,    true)]
    [InlineData(ValueScanDataType.Int32, ValueScanType.Changed,    true)]
    [InlineData(ValueScanDataType.Int32, ValueScanType.Increased,  true)]
    [InlineData(ValueScanDataType.Int32, ValueScanType.Contains,   false)]
    [InlineData(ValueScanDataType.Int32, ValueScanType.StartsWith, false)]
    [InlineData(ValueScanDataType.Int32, ValueScanType.EndsWith,   false)]
    [InlineData(ValueScanDataType.Float, ValueScanType.Contains,   false)]
    // String types: substring + Exact + Changed/Unchanged accept;
    // ordering rejects.
    [InlineData(ValueScanDataType.FString, ValueScanType.Exact,      true)]
    [InlineData(ValueScanDataType.FString, ValueScanType.Contains,   true)]
    [InlineData(ValueScanDataType.FString, ValueScanType.StartsWith, true)]
    [InlineData(ValueScanDataType.FString, ValueScanType.EndsWith,   true)]
    [InlineData(ValueScanDataType.FString, ValueScanType.Changed,    true)]
    [InlineData(ValueScanDataType.FString, ValueScanType.Unchanged,  true)]
    [InlineData(ValueScanDataType.FString, ValueScanType.Bigger,     false)]
    [InlineData(ValueScanDataType.FString, ValueScanType.Smaller,    false)]
    [InlineData(ValueScanDataType.FString, ValueScanType.Between,    false)]
    [InlineData(ValueScanDataType.FString, ValueScanType.Increased,  false)]
    [InlineData(ValueScanDataType.FString, ValueScanType.Decreased,  false)]
    [InlineData(ValueScanDataType.FName,   ValueScanType.Contains,   true)]
    [InlineData(ValueScanDataType.FName,   ValueScanType.Bigger,     false)]
    [InlineData(ValueScanDataType.FText,   ValueScanType.StartsWith, true)]
    // Vector types: ordering predicates accept; substring rejects.
    [InlineData(ValueScanDataType.FVector,  ValueScanType.Exact,      true)]
    [InlineData(ValueScanDataType.FVector,  ValueScanType.Bigger,     true)]
    [InlineData(ValueScanDataType.FVector,  ValueScanType.Between,    true)]
    [InlineData(ValueScanDataType.FVector,  ValueScanType.Changed,    true)]
    [InlineData(ValueScanDataType.FVector,  ValueScanType.Contains,   false)]
    [InlineData(ValueScanDataType.FRotator, ValueScanType.Smaller,    true)]
    [InlineData(ValueScanDataType.FRotator, ValueScanType.EndsWith,   false)]
    public void IsScanTypeValidFor_PartitionsCorrectlyPerDataType(
        ValueScanDataType dt, ValueScanType st, bool expected)
    {
        Assert.Equal(expected, ValueSearchViewModel.IsScanTypeValidFor(dt, st));
    }

    [Fact]
    public void IsStringDataType_OnlyMatchesStringFamily()
    {
        Assert.True(ValueSearchViewModel.IsStringDataType(ValueScanDataType.FString));
        Assert.True(ValueSearchViewModel.IsStringDataType(ValueScanDataType.FName));
        Assert.True(ValueSearchViewModel.IsStringDataType(ValueScanDataType.FText));
        Assert.False(ValueSearchViewModel.IsStringDataType(ValueScanDataType.Int32));
        Assert.False(ValueSearchViewModel.IsStringDataType(ValueScanDataType.FVector));
    }

    [Fact]
    public void IsVectorDataType_OnlyMatchesVectorFamily()
    {
        Assert.True(ValueSearchViewModel.IsVectorDataType(ValueScanDataType.FVector));
        Assert.True(ValueSearchViewModel.IsVectorDataType(ValueScanDataType.FRotator));
        Assert.True(ValueSearchViewModel.IsVectorDataType(ValueScanDataType.FTransform));
        Assert.False(ValueSearchViewModel.IsVectorDataType(ValueScanDataType.Int32));
        Assert.False(ValueSearchViewModel.IsVectorDataType(ValueScanDataType.FString));
    }

    [Theory]
    // Numeric DataTypes: dropdown excludes substring predicates.
    [InlineData(ValueScanDataType.Int32,   8 /* Exact..Decreased */)]
    [InlineData(ValueScanDataType.Float,   8)]
    // String DataTypes: 6 predicates (Exact, Contains, StartsWith,
    // EndsWith, Changed, Unchanged).
    [InlineData(ValueScanDataType.FString, 6)]
    [InlineData(ValueScanDataType.FName,   6)]
    [InlineData(ValueScanDataType.FText,   6)]
    // Vector DataTypes: same 8 as numerics.
    [InlineData(ValueScanDataType.FVector, 8)]
    [InlineData(ValueScanDataType.FRotator,8)]
    public void VisibleScanTypeOptions_ReflectsDataType(ValueScanDataType dt, int expectedCount)
    {
        var (vm, _) = MakeVm();
        vm.SelectedDataType = dt;
        Assert.Equal(expectedCount, vm.VisibleScanTypeOptions.Count);
    }

    [Fact]
    public void SelectedScanType_ResetsToExact_WhenSwitchingToIncompatibleDataType()
    {
        // User starts with Int32 + Bigger, then switches to FString.
        // Bigger is invalid for FString -> the VM must snap to Exact
        // so the dropdown stays in a consistent state.
        var (vm, _) = MakeVm();
        vm.SelectedDataType = ValueScanDataType.Int32;
        vm.SelectedScanType = ValueScanType.Bigger;
        vm.SelectedDataType = ValueScanDataType.FString;
        Assert.Equal(ValueScanType.Exact, vm.SelectedScanType);
    }

    [Fact]
    public void SupportsCaseSensitive_OnlyForStringTypes()
    {
        var (vm, _) = MakeVm();
        vm.SelectedDataType = ValueScanDataType.Int32;
        Assert.False(vm.SupportsCaseSensitive);
        vm.SelectedDataType = ValueScanDataType.FString;
        Assert.True(vm.SupportsCaseSensitive);
        vm.SelectedDataType = ValueScanDataType.FName;
        Assert.True(vm.SupportsCaseSensitive);
        vm.SelectedDataType = ValueScanDataType.FText;
        Assert.True(vm.SupportsCaseSensitive);
        vm.SelectedDataType = ValueScanDataType.FVector;
        Assert.False(vm.SupportsCaseSensitive);
    }

    [Fact]
    public void SupportsTolerance_AlsoForVectorTypes()
    {
        // Tolerance is enabled for Float/Double + Vector/Rotator/Transform.
        var (vm, _) = MakeVm();
        vm.SelectedDataType = ValueScanDataType.FVector;
        Assert.True(vm.SupportsTolerance);
        vm.SelectedDataType = ValueScanDataType.FRotator;
        Assert.True(vm.SupportsTolerance);
        vm.SelectedDataType = ValueScanDataType.FTransform;
        Assert.True(vm.SupportsTolerance);
        vm.SelectedDataType = ValueScanDataType.FString;
        Assert.False(vm.SupportsTolerance);
    }

    // ------------------------------------------------------------------
    // build 794 — multi-numeric (NumericNoByte) meta type
    // ------------------------------------------------------------------

    [Fact]
    public void NumericNoByte_IsOfferedInDropdown()
    {
        var (vm, _) = MakeVm();
        Assert.Contains(ValueScanDataType.NumericNoByte, vm.DataTypeOptions);
    }

    [Fact]
    public void NumericNoByte_IsNeitherStringNorVector()
    {
        // The meta type must classify as a plain numeric so the existing
        // numeric scan-type + (no) case-sensitive gating applies.
        Assert.False(ValueSearchViewModel.IsStringDataType(ValueScanDataType.NumericNoByte));
        Assert.False(ValueSearchViewModel.IsVectorDataType(ValueScanDataType.NumericNoByte));
    }

    [Fact]
    public void NumericNoByte_SupportsTolerance_ButNotCaseSensitive()
    {
        // Tolerance is meaningful (float/double members); case-sensitive
        // is string-only so it must stay off.
        var (vm, _) = MakeVm();
        vm.SelectedDataType = ValueScanDataType.NumericNoByte;
        Assert.True(vm.SupportsTolerance);
        Assert.False(vm.SupportsCaseSensitive);
    }

    [Theory]
    // Behaves like a numeric: ordering predicates accept, substring reject.
    [InlineData(ValueScanType.Exact,      true)]
    [InlineData(ValueScanType.Bigger,     true)]
    [InlineData(ValueScanType.Smaller,    true)]
    [InlineData(ValueScanType.Between,    true)]
    [InlineData(ValueScanType.Changed,    true)]
    [InlineData(ValueScanType.Increased,  true)]
    [InlineData(ValueScanType.Contains,   false)]
    [InlineData(ValueScanType.StartsWith, false)]
    [InlineData(ValueScanType.EndsWith,   false)]
    public void NumericNoByte_ScanTypeValidity_MirrorsNumeric(ValueScanType st, bool expected)
    {
        Assert.Equal(expected,
            ValueSearchViewModel.IsScanTypeValidFor(ValueScanDataType.NumericNoByte, st));
    }

    [Fact]
    public void NumericNoByte_VisibleScanTypes_ExcludeSubstring()
    {
        // Same 8 ordering predicates as a single numeric type.
        var (vm, _) = MakeVm();
        vm.SelectedDataType = ValueScanDataType.NumericNoByte;
        Assert.Equal(8, vm.VisibleScanTypeOptions.Count);
        Assert.DoesNotContain(ValueScanType.Contains, vm.VisibleScanTypeOptions);
    }

    [Fact]
    public async Task BeginValueScanAsync_SendsNumericNoByteWireName_AndAttachesTolerance()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 5UL,
                ["data_type"]       = "NumericNoByte",
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            ValueScanDataType.NumericNoByte, ValueScanType.Exact, "100",
            tolerance: 0.5,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("NumericNoByte", captured!["data_type"]?.GetValue<string>());
        // Tolerance rides along (it applies to the float/double members).
        Assert.True(captured.ContainsKey("tolerance"));
        Assert.Equal(0.5, captured["tolerance"]?.GetValue<double>());
    }

    [Fact]
    public async Task BeginValueScanAsync_OmitsCaseSensitiveForNumericNoByte()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = "NumericNoByte",
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            ValueScanDataType.NumericNoByte, ValueScanType.Exact, "100",
            caseSensitive: true,   // user set it, but type isn't a string
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured!.ContainsKey("case_sensitive"));
    }

    // ------------------------------------------------------------------
    // build 796 — multi-numeric with-byte variant (NumericAll) + warning
    // ------------------------------------------------------------------

    [Fact]
    public void NumericAll_IsOfferedInDropdown_AndClassifiedMultiNumeric()
    {
        var (vm, _) = MakeVm();
        Assert.Contains(ValueScanDataType.NumericAll, vm.DataTypeOptions);
        Assert.True(ValueSearchViewModel.IsMultiNumericDataType(ValueScanDataType.NumericAll));
        Assert.True(ValueSearchViewModel.IsMultiNumericDataType(ValueScanDataType.NumericNoByte));
        Assert.False(ValueSearchViewModel.IsMultiNumericDataType(ValueScanDataType.Int32));
        // Still a plain numeric for scan-type / case gating purposes.
        Assert.False(ValueSearchViewModel.IsStringDataType(ValueScanDataType.NumericAll));
        Assert.False(ValueSearchViewModel.IsVectorDataType(ValueScanDataType.NumericAll));
    }

    [Fact]
    public void NumericAll_SupportsTolerance_ButNotCaseSensitive()
    {
        var (vm, _) = MakeVm();
        vm.SelectedDataType = ValueScanDataType.NumericAll;
        Assert.True(vm.SupportsTolerance);
        Assert.False(vm.SupportsCaseSensitive);
    }

    [Fact]
    public void DataTypeWarning_OnlyShownForNumericAll()
    {
        // The result-volume caution fires for NumericAll (1-byte fields
        // flood on small values) and is empty for everything else.
        var (vm, _) = MakeVm();
        vm.SelectedDataType = ValueScanDataType.NumericAll;
        Assert.NotEmpty(vm.DataTypeWarning);
        Assert.Contains("1-byte", vm.DataTypeWarning);

        vm.SelectedDataType = ValueScanDataType.NumericNoByte;
        Assert.Empty(vm.DataTypeWarning);
        vm.SelectedDataType = ValueScanDataType.Int32;
        Assert.Empty(vm.DataTypeWarning);
        vm.SelectedDataType = ValueScanDataType.Float;
        Assert.Empty(vm.DataTypeWarning);
    }

    [Fact]
    public void DataTypeWarning_RaisesPropertyChanged_OnDataTypeSwitch()
    {
        var (vm, _) = MakeVm();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SelectedDataType = ValueScanDataType.NumericAll;
        Assert.Contains(nameof(vm.DataTypeWarning), raised);
    }

    [Theory]
    [InlineData(ValueScanType.Exact,    true)]
    [InlineData(ValueScanType.Bigger,   true)]
    [InlineData(ValueScanType.Between,  true)]
    [InlineData(ValueScanType.Decreased,true)]
    [InlineData(ValueScanType.Contains, false)]
    public void NumericAll_ScanTypeValidity_MirrorsNumeric(ValueScanType st, bool expected)
    {
        Assert.Equal(expected,
            ValueSearchViewModel.IsScanTypeValidFor(ValueScanDataType.NumericAll, st));
    }

    [Fact]
    public async Task BeginValueScanAsync_SendsNumericAllWireName_AndAttachesTolerance()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 8UL,
                ["data_type"]       = "NumericAll",
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            ValueScanDataType.NumericAll, ValueScanType.Exact, "100",
            tolerance: 0.5,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("NumericAll", captured!["data_type"]?.GetValue<string>());
        Assert.True(captured.ContainsKey("tolerance"));
        Assert.Equal(0.5, captured["tolerance"]?.GetValue<double>());
    }

    // ------------------------------------------------------------------
    // Phase 2 wire-shape locks for DumpService
    // ------------------------------------------------------------------

    [Fact]
    public async Task BeginValueScanAsync_AttachesCaseSensitiveForFString()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = "FString",
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            ValueScanDataType.FString, ValueScanType.Contains, "Player",
            caseSensitive: true,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("FString",   captured!["data_type"]?.GetValue<string>());
        Assert.Equal("Contains",  captured["scan_type"]?.GetValue<string>());
        Assert.Equal("Player",    captured["value"]?.GetValue<string>());
        Assert.True(captured.ContainsKey("case_sensitive"));
        Assert.True(captured["case_sensitive"]?.GetValue<bool>());
    }

    [Fact]
    public async Task BeginValueScanAsync_OmitsCaseSensitiveWhenFalse()
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = "FString",
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        // CE-style default is case-insensitive -- the wire should omit
        // the flag entirely so non-string sessions stay byte-identical
        // to the pre-Phase-2 wire shape.
        await svc.BeginValueScanAsync(
            ValueScanDataType.FString, ValueScanType.Exact, "Player",
            caseSensitive: false,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured!.ContainsKey("case_sensitive"));
    }

    [Theory]
    [InlineData(ValueScanDataType.Int32)]
    [InlineData(ValueScanDataType.Float)]
    [InlineData(ValueScanDataType.FVector)]
    public async Task BeginValueScanAsync_OmitsCaseSensitiveForNonStringTypes(ValueScanDataType dt)
    {
        // Even when the caller explicitly passes caseSensitive=true,
        // non-string DataTypes must NOT carry the flag on the wire --
        // the DLL ignores it for those sessions and omitting keeps the
        // wire shape minimal.
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = dt.ToString(),
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            dt, ValueScanType.Exact,
            dt == ValueScanDataType.FVector ? "0,0,0" : "0",
            caseSensitive: true,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured!.ContainsKey("case_sensitive"),
            $"case_sensitive must not appear on the wire for {dt}");
    }

    [Theory]
    [InlineData(ValueScanDataType.FVector)]
    [InlineData(ValueScanDataType.FRotator)]
    [InlineData(ValueScanDataType.FTransform)]
    public async Task BeginValueScanAsync_AttachesToleranceForVectorTypes(ValueScanDataType dt)
    {
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = dt.ToString(),
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            dt, ValueScanType.Exact, "100,200,300",
            tolerance: 0.5,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(0.5, captured!["tolerance"]?.GetValue<double>());
    }

    [Theory]
    [InlineData(ValueScanDataType.FString)]
    [InlineData(ValueScanDataType.FName)]
    [InlineData(ValueScanDataType.FText)]
    public async Task BeginValueScanAsync_OmitsToleranceForStringTypes(ValueScanDataType dt)
    {
        // Strings ignore tolerance DLL-side; omitting keeps the wire
        // shape tight for the common case.
        var svc = MakeService(out var pipe);
        JsonObject? captured = null;
        pipe.SetHandler(req =>
        {
            captured = (JsonObject)req.DeepClone();
            return new JsonObject
            {
                ["id"]              = req["id"]?.GetValue<int>() ?? 0,
                ["ok"]              = true,
                ["session_id"]      = 1UL,
                ["data_type"]       = dt.ToString(),
                ["total"]           = 0,
                ["scanned_classes"] = 0,
                ["scanned_objects"] = 0,
                ["duration_ms"]     = 1L,
                ["deadline_hit"]    = false,
                ["candidates"]      = new JsonArray(),
            };
        });

        await svc.BeginValueScanAsync(
            dt, ValueScanType.Contains, "Player",
            tolerance: 5.0,   // explicitly non-zero, should still be dropped
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.False(captured!.ContainsKey("tolerance"),
            $"tolerance must not appear on the wire for {dt}");
    }

    [Fact]
    public async Task ViewModel_CaseSensitive_PassesThroughForFString()
    {
        var (vm, fake) = MakeVm();
        fake.NextBeginResult = new ValueScanBeginResult { SessionId = 1UL };
        vm.SelectedDataType = ValueScanDataType.FString;
        vm.SelectedScanType = ValueScanType.Contains;
        vm.Value = "Health";
        vm.CaseSensitive = true;

        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.Single(fake.Begins);
        var (_, _, _, _, _, _, _, cs) = fake.Begins[0];
        Assert.True(cs);
    }

    [Fact]
    public async Task ViewModel_CaseSensitive_IgnoredForNonStringTypes()
    {
        // The VM applies SupportsCaseSensitive gating before pushing
        // to the service -- even with CaseSensitive=true the
        // non-string scan must see false.
        var (vm, fake) = MakeVm();
        fake.NextBeginResult = new ValueScanBeginResult { SessionId = 1UL };
        vm.SelectedDataType = ValueScanDataType.Int32;
        vm.SelectedScanType = ValueScanType.Exact;
        vm.Value = "100";
        vm.CaseSensitive = true;   // user set it, but type is Int32

        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.Single(fake.Begins);
        var (_, _, _, _, _, _, _, cs) = fake.Begins[0];
        Assert.False(cs);
    }

    [Fact]
    public async Task FirstScan_RejectsIncompatibleScanTypeForDataType()
    {
        // FString + Bigger is a legal-individually pair but illegal in
        // combination. The VM must catch it before hitting the DLL so
        // the user gets a clean error.
        var (vm, fake) = MakeVm();
        vm.SelectedDataType = ValueScanDataType.FString;
        // Bigger isn't in VisibleScanTypeOptions, but a misbehaving
        // caller could set it directly. Verify the FirstScan guard.
        vm.SelectedScanType = ValueScanType.Bigger;
        vm.Value = "anything";

        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.False(vm.HasSession);
        Assert.Empty(fake.Begins);
        Assert.Contains("not valid for", vm.ErrorMessage);
    }

    // ------------------------------------------------------------------
    // ViewModel-level: a fake IDumpService records calls and feeds
    // pre-baked results so we can verify the workflow contract.
    // ------------------------------------------------------------------

    private sealed class FakeDumpService : StubDumpService
    {
        public ValueScanBeginResult NextBeginResult { get; set; } = new();
        public ValueScanRefineResult NextRefineResult { get; set; } = new();
        // (dataType, scanType, value, value2, gameOnly, maxResults, tolerance, caseSensitive)
        public List<(ValueScanDataType, ValueScanType, string, string?, bool, int, double, bool)> Begins { get; } = new();
        // (sessionId, scanType, value, value2, tolerance, caseSensitive)
        public List<(ulong, ValueScanType, string?, string?, double, bool)> Refines { get; } = new();
        public List<ulong> Ends { get; } = new();

        public override Task<ValueScanBeginResult> BeginValueScanAsync(
            ValueScanDataType dataType, ValueScanType scanType,
            string value, string? value2 = null, bool gameOnly = true,
            int maxResults = 50000, double tolerance = 0.0,
            bool caseSensitive = false,
            CancellationToken ct = default)
        {
            Begins.Add((dataType, scanType, value, value2, gameOnly, maxResults, tolerance, caseSensitive));
            return Task.FromResult(NextBeginResult);
        }

        public override Task<ValueScanRefineResult> RefineValueScanAsync(
            ulong sessionId, ValueScanType scanType,
            string? value = null, string? value2 = null,
            double tolerance = 0.0,
            bool caseSensitive = false,
            CancellationToken ct = default)
        {
            Refines.Add((sessionId, scanType, value, value2, tolerance, caseSensitive));
            return Task.FromResult(NextRefineResult);
        }

        public override Task EndValueScanAsync(ulong sessionId, CancellationToken ct = default)
        {
            Ends.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    private static (ValueSearchViewModel vm, FakeDumpService fake) MakeVm()
    {
        var fake = new FakeDumpService();
        var vm = new ValueSearchViewModel(fake, new MockLoggingService());
        return (vm, fake);
    }

    [Fact]
    public async Task FirstScan_PopulatesCandidates_AndOpensSession()
    {
        var (vm, fake) = MakeVm();
        fake.NextBeginResult = new ValueScanBeginResult
        {
            SessionId = 42UL,
            DataType  = "Int32",
            Total     = 1,
            Candidates =
            {
                new ValueCandidate
                {
                    Addr = "0x1000", InstanceAddr = "0x2000",
                    ClassName = "BP_Player_C", FieldName = "Health",
                    FieldType = "IntProperty", Value = "100"
                }
            }
        };
        vm.SelectedDataType = ValueScanDataType.Int32;
        vm.SelectedScanType = ValueScanType.Exact;
        vm.Value = "100";

        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.Equal(42UL, vm.SessionId);
        Assert.True(vm.HasSession);
        Assert.Single(vm.Candidates);
        Assert.Single(fake.Begins);
        var (dt, st, val, val2, _, _, _, _) = fake.Begins[0];
        Assert.Equal(ValueScanDataType.Int32, dt);
        Assert.Equal(ValueScanType.Exact,     st);
        Assert.Equal("100",                   val);
        Assert.Null(val2);
    }

    [Fact]
    public async Task FirstScan_RejectsPrevValueScanType()
    {
        var (vm, fake) = MakeVm();
        vm.SelectedScanType = ValueScanType.Decreased;
        vm.Value = "100";

        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.False(vm.HasSession);
        Assert.Empty(fake.Begins);
        Assert.Contains("First Scan supports targeted predicates only", vm.ErrorMessage);
    }

    [Fact]
    public async Task FirstScan_RejectsEmptyValue()
    {
        var (vm, fake) = MakeVm();
        vm.SelectedScanType = ValueScanType.Exact;
        vm.Value = "";

        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.Empty(fake.Begins);
        Assert.Contains("Value is required", vm.ErrorMessage);
    }

    [Fact]
    public async Task FirstScan_BetweenRequiresValue2()
    {
        var (vm, fake) = MakeVm();
        vm.SelectedScanType = ValueScanType.Between;
        vm.Value = "10";
        vm.Value2 = "";

        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.Empty(fake.Begins);
        Assert.Contains("Between requires", vm.ErrorMessage);
    }

    [Fact]
    public async Task NextScan_PrevValueType_SendsNullValue()
    {
        var (vm, fake) = MakeVm();
        fake.NextBeginResult = new ValueScanBeginResult { SessionId = 7UL };
        vm.SelectedScanType = ValueScanType.Exact;
        vm.Value = "100";
        await vm.FirstScanCommand.ExecuteAsync(null);
        Assert.True(vm.HasSession);

        fake.NextRefineResult = new ValueScanRefineResult { SessionId = 7UL, Total = 5 };
        vm.SelectedScanType = ValueScanType.Changed;
        // Value field intentionally left at "100" — Changed must ignore it.

        await vm.NextScanCommand.ExecuteAsync(null);

        Assert.Single(fake.Refines);
        var (sid, st, val, val2, _, _) = fake.Refines[0];
        Assert.Equal(7UL, sid);
        Assert.Equal(ValueScanType.Changed, st);
        Assert.Null(val);
        Assert.Null(val2);
    }

    [Fact]
    public async Task NewScan_EndsSession_AndClearsCandidates()
    {
        var (vm, fake) = MakeVm();
        fake.NextBeginResult = new ValueScanBeginResult
        {
            SessionId = 9UL,
            Candidates = { new ValueCandidate { Addr = "0x1000" } }
        };
        vm.SelectedScanType = ValueScanType.Exact;
        vm.Value = "1";
        await vm.FirstScanCommand.ExecuteAsync(null);
        Assert.Single(vm.Candidates);

        await vm.NewScanCommand.ExecuteAsync(null);

        Assert.False(vm.HasSession);
        Assert.Equal(0UL, vm.SessionId);
        Assert.Empty(vm.Candidates);
        Assert.Single(fake.Ends);
        Assert.Equal(9UL, fake.Ends[0]);
    }

    [Fact]
    public async Task FirstScan_AutoEndsExistingSession_BeforeNewBegin()
    {
        // If the user clicks First Scan again without explicitly ending
        // the prior session, the VM must end it to avoid the DLL
        // accumulating orphan sessions until 5-min idle expiry.
        var (vm, fake) = MakeVm();
        fake.NextBeginResult = new ValueScanBeginResult { SessionId = 1UL };
        vm.SelectedScanType = ValueScanType.Exact;
        vm.Value = "1";
        await vm.FirstScanCommand.ExecuteAsync(null);
        Assert.Equal(1UL, vm.SessionId);

        fake.NextBeginResult = new ValueScanBeginResult { SessionId = 2UL };
        vm.Value = "2";
        await vm.FirstScanCommand.ExecuteAsync(null);

        Assert.Equal(2UL, vm.SessionId);
        Assert.Single(fake.Ends);
        Assert.Equal(1UL, fake.Ends[0]);
    }

    // ------------------------------------------------------------------
    // UX rule: ValueSearchPanel.axaml MUST surface the native-C++-fields
    // limitation. This is locked in by reading the AXAML source file at
    // test time and asserting the literal English text is still there.
    //
    // The wording lives in en.axaml — the panel uses StaticResource
    // str.VS.Banner. We check BOTH files so a rename of the resource
    // key without updating the panel still fails.
    //
    // Why a literal-text test: this is a project-memory UX rule (memory:
    // project_value_search_caveats). Without the assertion a future
    // refactor could "tidy up" the banner and silently strip the
    // limitation disclosure -- the user wouldn't know their scan was
    // blind to native fields.
    // ------------------------------------------------------------------

    private const string BannerExpected =
        "Native C++ fields (non-UPROPERTY) cannot be found here";

    private static string ReadProjectFile(string relativePath)
    {
        // Walk up from the test-bin directory to the repo root.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; ++i)
        {
            var candidate = Path.Combine(dir, "ui", "UE5DumpUI", relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            $"Could not locate ui/UE5DumpUI/{relativePath} from {AppContext.BaseDirectory}");
    }

    [Fact]
    public void Banner_LiteralText_IsPresentInEnAxaml()
    {
        var en = ReadProjectFile(Path.Combine("Resources", "Strings", "en.axaml"));
        Assert.Contains(BannerExpected, en);
        Assert.Contains("Use Cheat Engine's raw memory scan", en);
    }

    [Fact]
    public void Banner_IsReferencedByValueSearchPanel()
    {
        var panel = ReadProjectFile(Path.Combine("Views", "ValueSearchPanel.axaml"));
        Assert.Contains("str.VS.Banner", panel);
    }
}
