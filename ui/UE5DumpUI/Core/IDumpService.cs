using UE5DumpUI.Models;

namespace UE5DumpUI.Core;

/// <summary>
/// Business logic service for interacting with the UE5 Dumper DLL via pipe.
/// </summary>
public interface IDumpService
{
    Task<EngineState> InitAsync(CancellationToken ct = default);
    Task<EngineState> GetPointersAsync(CancellationToken ct = default);

    /// <summary>
    /// Set or clear the user UE version override for the current game.
    /// version=0 clears the override; non-zero sets it. The override persists in the
    /// HintCache JSON file (per game) and survives game restarts. Returns the updated
    /// EngineState (re-fetched after the override took effect).
    /// </summary>
    Task<EngineState> SetUeVersionOverrideAsync(int version, bool persist = true, CancellationToken ct = default);

    /// <summary>
    /// Set or clear the per-game GameThreadDispatch invoke timeout in milliseconds.
    /// timeoutMs=0 clears the override (revert to Stark::kDefaultInvokeTimeoutMs = 5000ms).
    /// Persisted in the same HintCache JSON keyed by PE hash; ResetAllCache wipes it
    /// alongside everything else. Returns the updated EngineState.
    /// </summary>
    Task<EngineState> SetInvokeTimeoutAsync(int timeoutMs, bool persist = true, CancellationToken ct = default);

    Task<int> GetObjectCountAsync(CancellationToken ct = default);
    Task<ObjectListResult> GetObjectListAsync(int offset, int limit, CancellationToken ct = default);
    Task<ObjectDetail> GetObjectAsync(string addr, CancellationToken ct = default);
    Task<ObjectDetail> FindObjectAsync(string path, CancellationToken ct = default);
    Task<ObjectListResult> SearchObjectsAsync(string query, int limit = 200, CancellationToken ct = default);
    Task<ClassInfoModel> WalkClassAsync(string addr, CancellationToken ct = default);

    /// <summary>
    /// Batched class schema walk — drops N pipe round-trips down to one
    /// for callers that need to walk many classes (Full SDK export,
    /// Dump All Metadata stream). Each returned element is byte-
    /// identical to a single <see cref="WalkClassAsync"/> call: the DLL
    /// implementation is a trivial loop over <c>Ubel::WalkClassEx</c>
    /// and the wire encoding is the same JSON shape as walk_class's
    /// "class" field, wrapped in a "classes" array.
    ///
    /// Result count equals the input count, in order. Empty / invalid
    /// addresses still emit a row (mirrors the single-call behaviour
    /// where WalkClassEx on a bad address returns an empty ClassInfo).
    /// Caller should chunk to keep pipe payloads bounded (~200 addrs
    /// per call is a safe default).
    /// </summary>
    Task<List<ClassInfoModel>> WalkClassesBatchAsync(string[] addrs, CancellationToken ct = default);
    Task<byte[]> ReadMemAsync(string addr, int size, CancellationToken ct = default);
    Task WriteMemAsync(string addr, byte[] data, CancellationToken ct = default);
    Task WatchAsync(string addr, int size, int intervalMs, CancellationToken ct = default);
    Task UnwatchAsync(string addr, CancellationToken ct = default);

    // --- Live Data Walker ---
    Task<InstanceWalkResult> WalkInstanceAsync(string addr, string? classAddr = null, int arrayLimit = 64, int previewLimit = 2, bool fillGaps = false, CancellationToken ct = default);
    Task<WorldWalkResult> WalkWorldAsync(int actorLimit = 200, int arrayLimit = 64, CancellationToken ct = default);
    Task<FindInstancesResult> FindInstancesAsync(string className, bool exactMatch = false, int limit = 500, CancellationToken ct = default);
    Task<CePointerInfo> GetCePointerInfoAsync(string addr, int fieldOffset = 0, CancellationToken ct = default);

    // --- DataTable Row Browsing ---
    Task<DataTableWalkResult> WalkDataTableRowsAsync(string addr, int offset = 0, int limit = 64, CancellationToken ct = default);

    // --- Array Element Reading (Phase B) ---
    Task<ArrayElementsResult> ReadArrayElementsAsync(
        string instanceAddr, int fieldOffset,
        string innerAddr, string innerType, int elemSize,
        int offset = 0, int limit = 64, CancellationToken ct = default);

    // --- Address-to-Instance Reverse Lookup ---
    Task<AddressLookupResult> FindByAddressAsync(string addr, CancellationToken ct = default);

    // --- Reverse Reference Search (logical-owner navigation) ---
    Task<FindReferencesResult> FindReferencesToUObjectAsync(
        string addr, int maxResults = 32, CancellationToken ct = default);

    // --- Enum Enumeration ---
    Task<List<EnumDefinition>> ListEnumsAsync(CancellationToken ct = default);

    // --- Function Walking (for SDK export) ---
    Task<List<FunctionInfoModel>> WalkFunctionsAsync(string addr, CancellationToken ct = default);

    // --- Property Keyword Search ---
    Task<PropertySearchResult> SearchPropertiesAsync(
        string query, string[]? types = null, bool gameOnly = true,
        int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// Batched property search — DLL walks GObjects once and checks
    /// every property against every query. Drops the multi-keyword
    /// sweep time from ~42s (sequential pipe calls each re-walking
    /// GObjects) to ~1.5s for a 36-query / 4400-class game. Used by
    /// the Interesting Properties tab Load command.
    ///
    /// Each query gets its own dedup index + maxResults limit, returned
    /// in order inside <see cref="PropertySearchBatchResult.PerQuery"/>.
    /// Preview values are NOT resolved on the batch path (the tab
    /// doesn't display them; user opens a row in Live Walker to read
    /// the live value).
    /// </summary>
    Task<PropertySearchBatchResult> SearchPropertiesBatchAsync(
        string[] queries, string[]? types = null, bool gameOnly = true,
        int limitPerQuery = 200, CancellationToken ct = default);

    // --- Game Class List ---
    Task<ClassListResult> ListClassesAsync(
        bool gameOnly = true, int limit = 5000, CancellationToken ct = default);

    // --- Value Search (CE-style First Scan + Next Scan) ---
    //
    // Walks GObjects + UProperty metadata for every UPROPERTY field
    // matching `dataType` across all UObject instances, applying the
    // scan predicate. Returns enriched candidates + a session id for
    // follow-up RefineValueScanAsync calls.
    //
    // For ValueScanType.Between, both `value` and `value2` must be
    // populated. For ValueScanType.Exact/Bigger/Smaller `value2` is
    // ignored. Prev-value predicates (Changed/Unchanged/Increased/
    // Decreased) are NOT valid for the first scan — caller must use
    // RefineValueScanAsync for those.
    //
    // Native C++ fields (non-UPROPERTY) are NOT visible to this scan.
    // The UI's Value Search tab surfaces this limitation in a banner.
    Task<ValueScanBeginResult> BeginValueScanAsync(
        ValueScanDataType dataType,
        ValueScanType scanType,
        string value,
        string? value2 = null,
        bool gameOnly = true,
        int maxResults = 50000,
        double tolerance = 0.0,
        CancellationToken ct = default);

    Task<ValueScanRefineResult> RefineValueScanAsync(
        ulong sessionId,
        ValueScanType scanType,
        string? value = null,
        string? value2 = null,
        double tolerance = 0.0,
        CancellationToken ct = default);

    Task EndValueScanAsync(ulong sessionId, CancellationToken ct = default);

    // --- All Functions Enumeration (Interesting Functions Finder) ---
    Task<AllFunctionsResult> ListAllFunctionsAsync(
        bool gameOnly = true, int limit = 100000, CancellationToken ct = default);

    // --- Extra Scan (user-triggered aggressive fallback) ---
    Task<RescanStartResult> StartRescanAsync(CancellationToken ct = default);
    Task<RescanStatusResult> GetRescanStatusAsync(CancellationToken ct = default);
    Task<EngineState> ApplyRescanAsync(CancellationToken ct = default);

    // --- Trigger Scan (proxy DLL deferred scan) ---
    /// <summary>
    /// Start async AOB scan. Used when proxy DLL starts without scanning.
    /// Returns immediately — poll progress with GetScanStatusAsync().
    /// Also safe to call in CE/manual mode — UE5_Init is idempotent.
    /// </summary>
    Task TriggerScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Poll scan progress after TriggerScanAsync(). Returns phase, status text,
    /// and full EngineState when scan is complete (phase >= 7).
    /// </summary>
    Task<ScanStatusResult> GetScanStatusAsync(CancellationToken ct = default);

    // --- UFunction Invocation via Pipe ---

    /// <summary>
    /// Invoke a UFunction via ProcessEvent through the pipe.
    /// The DLL executes ProcessEvent in-process, bypassing CE's executeCodeEx.
    /// Works even when games block CreateRemoteThread.
    /// </summary>
    /// <param name="funcName">UFunction name to invoke.</param>
    /// <param name="instanceAddr">Hex address of target UObject instance (optional if className provided).</param>
    /// <param name="className">Class name to auto-resolve instance (optional if instanceAddr provided).</param>
    /// <param name="parmsSize">Total parameter buffer size from UFunction.</param>
    /// <param name="paramsHex">Hex-encoded param bytes (optional).</param>
    /// <param name="directCall">When true, force the DLL-side UE5_CallProcessEventDirect path
    ///     (bypass GameThreadDispatch). Caller asserts the function is FUNC_Native|FUNC_Static
    ///     (e.g. KismetMathLibrary helpers) — required by the System tab Self-Test which must
    ///     succeed on idle main-menu / loading screens where the game thread isn't pumping.</param>
    Task<InvokeFunctionResult> InvokeFunctionAsync(
        string funcName,
        string? instanceAddr = null,
        string? className = null,
        int parmsSize = 0,
        string? paramsHex = null,
        bool directCall = false,
        CancellationToken ct = default);
}
