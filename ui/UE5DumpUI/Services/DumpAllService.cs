using System.Text;
using System.Text.Json;
using UE5DumpUI.Core;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Streams a full classes + properties + functions dump as JSON Lines
/// (one JSON object per line). Used as input for offline analysis —
/// Python scripts under <c>scripts/analysis/</c> aggregate across
/// multiple game dumps to inform keyword tables, class-location
/// bonuses, and threshold calibration. Replaces hand-curated guesses
/// with empirically-grounded patterns.
///
/// All work is orchestrated client-side via the existing pipe
/// endpoints (<c>get_object_list</c> + <c>walk_class</c> +
/// <c>walk_functions</c>); no new DLL command is required. The
/// trade-off is per-class round-trips — ~30-60 seconds for a
/// 3-5k-class game — vs. zero DLL maintenance burden.
///
/// **BPGC inclusion**: unlike <c>SearchProperties</c> (build 671
/// `IsClassLikeMeta` fix) the dumper accepts every class-flavoured
/// metaclass (Class + BlueprintGeneratedClass + AnimBPGC +
/// WidgetBPGC + DynamicClass) so game-specific BP classes — where
/// almost all cheat-relevant properties live — are NOT dropped.
/// </summary>
public static class DumpAllService
{
    /// <summary>
    /// Class-flavoured metaclass names accepted by the dumper. Mirrors
    /// <c>Aura::IsClassLikeMeta</c> on the DLL side — keep in sync if
    /// new UClass subclasses appear in future UE versions.
    /// </summary>
    private static readonly HashSet<string> ClassLikeMetas = new(StringComparer.Ordinal)
    {
        "Class",
        "BlueprintGeneratedClass",
        "AnimBlueprintGeneratedClass",
        "WidgetBlueprintGeneratedClass",
        "DynamicClass",
    };

    /// <summary>
    /// Default chunk size for the batched walk_class fan-out. Internal
    /// so tests can hit chunk-boundary edge cases at (n-1)/n/(n+1)
    /// counts. Matches <see cref="SdkExportService.FullSdkBatchChunkSize"/>.
    /// </summary>
    internal const int WalkClassBatchChunkSize = 200;

    /// <summary>
    /// Engine-package path prefixes (mirrors the DLL's IsEnginePackage
    /// list). Used only when <see cref="DumpOptions.GameOnly"/> is true
    /// to skip /Script/Engine.*, /Script/CoreUObject.*, etc.
    /// </summary>
    private static readonly string[] EnginePathPrefixes =
    {
        "/Script/Engine.",
        "/Script/CoreUObject.",
        "/Script/CoreOnline.",
        "/Script/UMG.",
        "/Script/Slate.",
        "/Script/SlateCore.",
        "/Script/InputCore.",
        "/Script/PhysicsCore.",
        "/Script/NavigationSystem.",
        "/Script/AIModule.",
        "/Script/Niagara.",
        "/Script/MovieScene.",
        "/Script/LevelSequence.",
        "/Script/Landscape.",
        "/Script/Foliage.",
        "/Script/AnimGraphRuntime.",
        "/Script/AudioMixer.",
        "/Script/GameplayTags.",
        "/Script/GameplayTasks.",
        "/Script/GameplayAbilities.",
        // Common UE built-in modules; not exhaustive — analysis scripts
        // can further filter via the class_path field in the dump.
    };

    /// <summary>
    /// Generate a JSON-Lines dump and stream it to
    /// <paramref name="output"/>. Output schema (one object per line):
    /// <list type="bullet">
    ///   <item><c>{"kind":"meta", ...}</c> — first line; UE version,
    ///     module, object count, build, options.</item>
    ///   <item><c>{"kind":"class", "name":..., "props":[...],
    ///     "funcs":[...]}</c> — one per class-like object.</item>
    ///   <item><c>{"kind":"error", "addr":..., "msg":...}</c> — when a
    ///     specific class walk fails; iteration continues.</item>
    ///   <item><c>{"kind":"summary", ...}</c> — last line; counters.</item>
    /// </list>
    /// </summary>
    public static async Task GenerateAsync(
        IDumpService dump,
        EngineState engineState,
        Stream output,
        DumpOptions? options = null,
        IProgress<DumpProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new DumpOptions();

        await using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = false,
        };

        // ----- Meta line -----
        await WriteMetaLineAsync(writer, engineState, options, ct);

        // ----- Pass 1 (optional): count live instances per class name -----
        Dictionary<string, int>? instanceCounts = null;
        int total = 0;
        if (options.IncludeInstanceCounts)
        {
            instanceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            int offset = 0;
            const int pageSize = 5000;
            do
            {
                ct.ThrowIfCancellationRequested();
                var page = await dump.GetObjectListAsync(offset, pageSize, ct);
                total = page.Total;
                foreach (var obj in page.Objects)
                {
                    if (string.IsNullOrEmpty(obj.ClassName)) continue;
                    instanceCounts.TryGetValue(obj.ClassName, out var c);
                    instanceCounts[obj.ClassName] = c + 1;
                }
                offset += page.Scanned > 0 ? page.Scanned : page.Objects.Count;
                progress?.Report(new DumpProgress(
                    Phase: "Counting instances",
                    Done: offset,
                    Total: total));
                if (offset >= total) break;
            } while (true);
        }

        // ----- Pass 2: walk every class-like object -----
        //
        // Classes are walked in chunks of WalkClassBatchChunkSize via
        // walk_class_batch (build 693). The DLL batch is a trivial loop
        // over Ubel::WalkClassEx — same function the single walk_class
        // path uses — so each batch element is byte-identical to a
        // single-call result. WalkFunctions stays single-call per
        // emitted class for now (separate optimisation candidate).
        // Any batch failure (pipe exception, unexpected element count)
        // is caught and the chunk is replayed via single WalkClassAsync
        // calls so per-class error attribution is preserved.
        int classesEmitted = 0;
        int classesSkipped = 0;
        int errors = 0;
        int scannedObjects = 0;
        {
            int offset = 0;
            const int pageSize = 5000;
            var chunkBuffer = new List<UObjectNode>(WalkClassBatchChunkSize);

            do
            {
                ct.ThrowIfCancellationRequested();
                var page = await dump.GetObjectListAsync(offset, pageSize, ct);
                total = page.Total;

                foreach (var obj in page.Objects)
                {
                    scannedObjects++;
                    if (!ClassLikeMetas.Contains(obj.ClassName))
                    {
                        continue;
                    }
                    if (options.GameOnly && IsEnginePath(obj.FullPath))
                    {
                        classesSkipped++;
                        continue;
                    }

                    chunkBuffer.Add(obj);
                    if (chunkBuffer.Count >= WalkClassBatchChunkSize)
                    {
                        var (emitted, errs) = await FlushClassChunkAsync(
                            dump, writer, chunkBuffer, instanceCounts, options,
                            classesEmitted, progress, ct);
                        classesEmitted += emitted;
                        errors         += errs;
                        chunkBuffer.Clear();
                    }
                }

                offset += page.Scanned > 0 ? page.Scanned : page.Objects.Count;
                if (offset >= total) break;
            } while (true);

            // Final partial chunk.
            if (chunkBuffer.Count > 0)
            {
                var (emitted, errs) = await FlushClassChunkAsync(
                    dump, writer, chunkBuffer, instanceCounts, options,
                    classesEmitted, progress, ct);
                classesEmitted += emitted;
                errors         += errs;
                chunkBuffer.Clear();
            }
        }

        // ----- Summary line -----
        await WriteSummaryLineAsync(writer, classesEmitted, classesSkipped, errors, scannedObjects, ct);
        await writer.FlushAsync();

        progress?.Report(new DumpProgress(
            Phase: $"Done — {classesEmitted} classes",
            Done: classesEmitted,
            Total: classesEmitted));
    }

    /// <summary>
    /// Walk a chunk of class objects and emit one class line per entry.
    /// Tries the batched walk_class_batch path first; on any failure
    /// (pipe exception, unexpected result count) falls back to single
    /// WalkClassAsync calls so per-class error attribution survives.
    /// WalkFunctions is invoked single-call per emitted class — that
    /// half of the per-class round-trip cost is a separate batching
    /// candidate (build 693 only batches walk_class).
    ///
    /// Returns (emittedDelta, errorsDelta) so the caller can maintain
    /// the cumulative <c>classesEmitted</c> / <c>errors</c> counters
    /// the summary line reports.
    /// </summary>
    private static async Task<(int emitted, int errors)> FlushClassChunkAsync(
        IDumpService dump,
        TextWriter writer,
        List<UObjectNode> chunk,
        Dictionary<string, int>? instanceCounts,
        DumpOptions options,
        int cumulativeEmittedBefore,
        IProgress<DumpProgress>? progress,
        CancellationToken ct)
    {
        if (chunk.Count == 0) return (0, 0);

        // Try the batched path first.
        var addrs = new string[chunk.Count];
        for (int i = 0; i < chunk.Count; i++) addrs[i] = chunk[i].Address;

        List<ClassInfoModel>? batchResult = null;
        try
        {
            var fetched = await dump.WalkClassesBatchAsync(addrs, ct);
            if (fetched.Count == chunk.Count)
                batchResult = fetched;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // batchResult stays null → fallback below preserves
            // per-class error rows (kind=error) the way the pre-batch
            // path did.
        }

        int emittedThisChunk = 0;
        int errorsThisChunk = 0;

        for (int i = 0; i < chunk.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var obj = chunk[i];
            try
            {
                var classInfo = batchResult != null
                    ? batchResult[i]
                    : await dump.WalkClassAsync(obj.Address, ct);

                List<FunctionInfoModel>? functions = null;
                if (options.IncludeFunctions)
                {
                    functions = await dump.WalkFunctionsAsync(obj.Address, ct);
                }

                int instCount = 0;
                if (instanceCounts != null)
                {
                    instanceCounts.TryGetValue(classInfo.Name, out instCount);
                }

                await WriteClassLineAsync(writer, obj, classInfo, functions, instCount, ct);
                emittedThisChunk++;

                // Preserves the pre-batch path's per-50-class progress
                // granularity, measured against the cumulative emit
                // count (not the chunk-local count). Matches old
                // behaviour exactly so progress messages line up.
                int cumulative = cumulativeEmittedBefore + emittedThisChunk;
                if ((cumulative % 50) == 0)
                {
                    progress?.Report(new DumpProgress(
                        Phase: "Walking classes",
                        Done: cumulative,
                        Total: -1));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorsThisChunk++;
                await WriteErrorLineAsync(writer, obj.Address, obj.Name, ex.Message, ct);
            }
        }

        return (emittedThisChunk, errorsThisChunk);
    }

    // ------------------------------------------------------------------
    // Internal: writers
    // ------------------------------------------------------------------

    private static async Task WriteMetaLineAsync(
        TextWriter w, EngineState es, DumpOptions opts, CancellationToken ct)
    {
        var sb = new StringBuilder(512);
        sb.Append("{\"kind\":\"meta\"");
        sb.Append(",\"ue_version\":").Append(es.UEVersion);
        sb.Append(",\"ue_version_detected\":").Append(es.VersionDetected ? "true" : "false");
        sb.Append(",\"is_user_override\":").Append(es.IsUserOverride ? "true" : "false");
        AppendJsonString(sb, ",\"module\":", es.ModuleName ?? "");
        AppendJsonString(sb, ",\"module_base\":", es.ModuleBase ?? "");
        AppendJsonString(sb, ",\"gobjects\":", es.GObjectsAddr ?? "");
        AppendJsonString(sb, ",\"gnames\":",   es.GNamesAddr ?? "");
        AppendJsonString(sb, ",\"gworld\":",   es.GWorldAddr ?? "");
        sb.Append(",\"object_count\":").Append(es.ObjectCount);
        AppendJsonString(sb, ",\"pe_hash\":", es.PeHash ?? "");
        AppendJsonString(sb, ",\"publisher_thumbprint\":", es.PublisherThumbprint ?? "");
        sb.Append(",\"dumper_build\":").Append(opts.DumperBuildNumber);
        AppendJsonString(sb, ",\"dumper_commit\":", opts.DumperCommit ?? "");
        AppendJsonString(sb, ",\"dumped_at\":", DateTime.UtcNow.ToString("o"));
        sb.Append(",\"options\":{");
        sb.Append("\"game_only\":").Append(opts.GameOnly ? "true" : "false");
        sb.Append(",\"include_functions\":").Append(opts.IncludeFunctions ? "true" : "false");
        sb.Append(",\"include_instance_counts\":").Append(opts.IncludeInstanceCounts ? "true" : "false");
        sb.Append("}}");
        await w.WriteLineAsync(sb.ToString().AsMemory(), ct);
    }

    private static async Task WriteClassLineAsync(
        TextWriter w,
        UObjectNode obj,
        ClassInfoModel classInfo,
        List<FunctionInfoModel>? functions,
        int instanceCount,
        CancellationToken ct)
    {
        var sb = new StringBuilder(classInfo.Fields.Count * 100 + 256);
        sb.Append("{\"kind\":\"class\"");
        AppendJsonString(sb, ",\"name\":", classInfo.Name);
        AppendJsonString(sb, ",\"addr\":", obj.Address);
        AppendJsonString(sb, ",\"path\":", classInfo.FullPath);
        AppendJsonString(sb, ",\"meta\":", obj.ClassName);  // "Class" or "BlueprintGeneratedClass" etc.
        AppendJsonString(sb, ",\"super\":", classInfo.SuperName);
        AppendJsonString(sb, ",\"super_addr\":", classInfo.SuperAddress);
        sb.Append(",\"is_bpgc\":").Append(obj.ClassName != "Class" ? "true" : "false");
        sb.Append(",\"props_size\":").Append(classInfo.PropertiesSize);
        sb.Append(",\"instance_count\":").Append(instanceCount);
        sb.Append(",\"props\":[");
        for (int i = 0; i < classInfo.Fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var f = classInfo.Fields[i];
            sb.Append('{');
            AppendJsonString(sb, "\"name\":", f.Name);
            AppendJsonString(sb, ",\"type\":", f.TypeName);
            sb.Append(",\"offset\":").Append(f.Offset);
            sb.Append(",\"size\":").Append(f.Size);
            if (!string.IsNullOrEmpty(f.StructType))
                AppendJsonString(sb, ",\"struct_type\":", f.StructType);
            if (!string.IsNullOrEmpty(f.InnerType))
                AppendJsonString(sb, ",\"inner_type\":", f.InnerType);
            if (!string.IsNullOrEmpty(f.ObjClassName))
                AppendJsonString(sb, ",\"obj_class\":", f.ObjClassName);
            if (!string.IsNullOrEmpty(f.EnumName))
                AppendJsonString(sb, ",\"enum\":", f.EnumName);
            sb.Append('}');
        }
        sb.Append(']');

        if (functions != null)
        {
            sb.Append(",\"funcs\":[");
            for (int i = 0; i < functions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var fn = functions[i];
                sb.Append('{');
                AppendJsonString(sb, "\"name\":", fn.Name);
                AppendJsonString(sb, ",\"addr\":", fn.Address);
                AppendJsonString(sb, ",\"return_type\":", fn.ReturnType);
                sb.Append(",\"num_parms\":").Append(fn.NumParms);
                sb.Append(",\"parms_size\":").Append(fn.ParmsSize);
                sb.Append(",\"flags\":\"0x").Append(fn.FunctionFlags.ToString("X")).Append('"');
                sb.Append('}');
            }
            sb.Append(']');
        }
        sb.Append('}');
        await w.WriteLineAsync(sb.ToString().AsMemory(), ct);
    }

    private static async Task WriteErrorLineAsync(
        TextWriter w, string addr, string name, string msg, CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"kind\":\"error\"");
        AppendJsonString(sb, ",\"addr\":", addr);
        AppendJsonString(sb, ",\"name\":", name);
        AppendJsonString(sb, ",\"msg\":", msg);
        sb.Append('}');
        await w.WriteLineAsync(sb.ToString().AsMemory(), ct);
    }

    private static async Task WriteSummaryLineAsync(
        TextWriter w, int emitted, int skipped, int errors, int scanned, CancellationToken ct)
    {
        var sb = new StringBuilder(128);
        sb.Append("{\"kind\":\"summary\"");
        sb.Append(",\"classes_emitted\":").Append(emitted);
        sb.Append(",\"classes_skipped_engine\":").Append(skipped);
        sb.Append(",\"errors\":").Append(errors);
        sb.Append(",\"objects_scanned\":").Append(scanned);
        sb.Append('}');
        await w.WriteLineAsync(sb.ToString().AsMemory(), ct);
    }

    /// <summary>
    /// JSON-escape <paramref name="value"/> and append <paramref name="prefix"/>
    /// + the encoded string to <paramref name="sb"/>. Uses
    /// <see cref="JsonEncodedText"/> so the escape rules match
    /// System.Text.Json exactly (covers control chars, quotes, backslash,
    /// non-BMP UTF-16 correctly).
    /// </summary>
    private static void AppendJsonString(StringBuilder sb, string prefix, string value)
    {
        sb.Append(prefix);
        sb.Append('"');
        sb.Append(JsonEncodedText.Encode(value ?? "").ToString());
        sb.Append('"');
    }

    /// <summary>Check whether <paramref name="fullPath"/> belongs to one
    /// of the known engine packages. Case-sensitive (UE paths are
    /// case-sensitive by convention).</summary>
    internal static bool IsEnginePath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return false;
        foreach (var prefix in EnginePathPrefixes)
        {
            if (fullPath.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Whitelist check — exposed for tests.</summary>
    internal static bool IsClassLikeMetaName(string meta) => ClassLikeMetas.Contains(meta);
}

/// <summary>Options controlling what the dumper emits.</summary>
public sealed record DumpOptions(
    bool GameOnly = false,
    bool IncludeFunctions = true,
    bool IncludeInstanceCounts = true,
    int DumperBuildNumber = 0,
    string? DumperCommit = null);

/// <summary>Progress payload — Done/Total of -1 means "indeterminate".</summary>
public sealed record DumpProgress(string Phase, int Done, int Total);
