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
        var (_, _, _, _, _, _, tol) = fake.Begins[0];
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
        var (_, _, _, _, _, _, tol) = fake.Begins[0];
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
    [InlineData(ValueScanType.Exact,     true,  false)]
    [InlineData(ValueScanType.Bigger,    true,  false)]
    [InlineData(ValueScanType.Smaller,   true,  false)]
    [InlineData(ValueScanType.Between,   true,  false)]
    [InlineData(ValueScanType.Changed,   false, true)]
    [InlineData(ValueScanType.Unchanged, false, true)]
    [InlineData(ValueScanType.Increased, false, true)]
    [InlineData(ValueScanType.Decreased, false, true)]
    public void ScanType_Partition_IsExhaustiveAndDisjoint(
        ValueScanType st, bool expectFirst, bool expectPrev)
    {
        Assert.Equal(expectFirst, ValueSearchViewModel.IsFirstScanType(st));
        Assert.Equal(expectPrev,  ValueSearchViewModel.IsPrevValueScanType(st));
        Assert.NotEqual(ValueSearchViewModel.IsFirstScanType(st),
                        ValueSearchViewModel.IsPrevValueScanType(st));
    }

    // ------------------------------------------------------------------
    // ViewModel-level: a fake IDumpService records calls and feeds
    // pre-baked results so we can verify the workflow contract.
    // ------------------------------------------------------------------

    private sealed class FakeDumpService : StubDumpService
    {
        public ValueScanBeginResult NextBeginResult { get; set; } = new();
        public ValueScanRefineResult NextRefineResult { get; set; } = new();
        public List<(ValueScanDataType, ValueScanType, string, string?, bool, int, double)> Begins { get; } = new();
        public List<(ulong, ValueScanType, string?, string?, double)> Refines { get; } = new();
        public List<ulong> Ends { get; } = new();

        public override Task<ValueScanBeginResult> BeginValueScanAsync(
            ValueScanDataType dataType, ValueScanType scanType,
            string value, string? value2 = null, bool gameOnly = true,
            int maxResults = 50000, double tolerance = 0.0,
            CancellationToken ct = default)
        {
            Begins.Add((dataType, scanType, value, value2, gameOnly, maxResults, tolerance));
            return Task.FromResult(NextBeginResult);
        }

        public override Task<ValueScanRefineResult> RefineValueScanAsync(
            ulong sessionId, ValueScanType scanType,
            string? value = null, string? value2 = null,
            double tolerance = 0.0,
            CancellationToken ct = default)
        {
            Refines.Add((sessionId, scanType, value, value2, tolerance));
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
        var (dt, st, val, val2, _, _, _) = fake.Begins[0];
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
        Assert.Contains("First Scan only supports", vm.ErrorMessage);
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
        var (sid, st, val, val2, _) = fake.Refines[0];
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
