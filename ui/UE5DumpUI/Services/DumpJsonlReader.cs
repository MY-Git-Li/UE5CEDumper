using System.Text;
using System.Text.Json;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Reads a "Dump All" JSON-Lines file (see <see cref="DumpAllService"/>) back
/// into a flattened, searchable corpus for the Dump Explorer panel. Every
/// class line is expanded into one class row + one row per property + one row
/// per function, all carrying the owning class's object path and dump-time
/// address so a single per-class live match classifies the whole family.
///
/// Purely client-side and offline: parsing a dump requires no live game. The
/// live-match / jump features (which DO need a connected game) live in the
/// ViewModel; this reader only turns the file into rows.
/// </summary>
public static class DumpJsonlReader
{
    /// <summary>
    /// Parse the JSON-Lines file at <paramref name="filePath"/>. Malformed
    /// lines are skipped (a dump can be truncated mid-write if the game exited);
    /// the meta header and every well-formed class line are returned.
    /// </summary>
    public static async Task<DumpFileModel> ReadAsync(
        string filePath,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        DumpMetaLine? meta = null;
        var entries = new List<DumpEntry>();
        int classCount = 0, propCount = 0, funcCount = 0;

        await using var fs = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        int lineNo = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            lineNo++;
            if (line.Length == 0) continue;

            DumpClassLine? probe;
            try
            {
                probe = JsonSerializer.Deserialize(line, DumpJsonlContext.Default.DumpClassLine);
            }
            catch (JsonException)
            {
                continue;   // skip a corrupt/partial line
            }
            if (probe is null) continue;

            switch (probe.Kind)
            {
                case "class":
                    AppendClass(entries, probe, ref classCount, ref propCount, ref funcCount);
                    break;
                case "meta":
                    try
                    {
                        meta = JsonSerializer.Deserialize(line, DumpJsonlContext.Default.DumpMetaLine);
                    }
                    catch (JsonException) { /* leave meta null */ }
                    break;
                // "error" / "summary" lines carry no browsable metadata.
            }

            if ((lineNo & 0x3FF) == 0)
                progress?.Report(entries.Count);
        }

        return new DumpFileModel
        {
            Meta = meta,
            Entries = entries,
            ClassCount = classCount,
            PropertyCount = propCount,
            FunctionCount = funcCount,
        };
    }

    private static void AppendClass(
        List<DumpEntry> entries, DumpClassLine c,
        ref int classCount, ref int propCount, ref int funcCount)
    {
        var path = c.Path ?? "";
        var classAddr = c.Addr ?? "";

        // Class row.
        var superInfo = string.IsNullOrEmpty(c.Super) ? "" : $": {c.Super}";
        entries.Add(new DumpEntry
        {
            Kind = DumpEntryKind.Class,
            Name = c.Name,
            OwnerClass = "",
            TypeInfo = superInfo,
            Offset = -1,
            Path = path,
            ClassAddr = classAddr,
            Haystack = BuildHaystack(c.Name, c.Meta, superInfo, path),
        });
        classCount++;

        // Property rows.
        if (c.Props is { Count: > 0 })
        {
            foreach (var p in c.Props)
            {
                var typeInfo = ComposePropType(p);
                entries.Add(new DumpEntry
                {
                    Kind = DumpEntryKind.Property,
                    Name = p.Name,
                    OwnerClass = c.Name,
                    TypeInfo = typeInfo,
                    Offset = p.Offset,
                    Path = path,
                    ClassAddr = classAddr,
                    Haystack = BuildHaystack(p.Name, c.Name, typeInfo, path),
                });
                propCount++;
            }
        }

        // Function rows.
        if (c.Funcs is { Count: > 0 })
        {
            foreach (var f in c.Funcs)
            {
                var sig = string.IsNullOrEmpty(f.ReturnType)
                    ? $"({f.NumParms})"
                    : $"{f.ReturnType} ({f.NumParms})";
                entries.Add(new DumpEntry
                {
                    Kind = DumpEntryKind.Function,
                    Name = f.Name,
                    OwnerClass = c.Name,
                    TypeInfo = sig,
                    Offset = -1,
                    Path = path,
                    ClassAddr = classAddr,
                    FuncAddr = f.Addr ?? "",
                    Haystack = BuildHaystack(f.Name, c.Name, sig, path),
                });
                funcCount++;
            }
        }
    }

    /// <summary>Human-readable type string: base type plus struct/inner/enum/obj-class detail.</summary>
    internal static string ComposePropType(DumpPropLine p)
    {
        var sb = new StringBuilder(p.Type);
        if (!string.IsNullOrEmpty(p.StructType))
            sb.Append('<').Append(p.StructType).Append('>');
        if (!string.IsNullOrEmpty(p.InnerType))
        {
            sb.Append('<').Append(p.InnerType);
            if (!string.IsNullOrEmpty(p.InnerStructType))
                sb.Append(':').Append(p.InnerStructType);
            sb.Append('>');
        }
        if (!string.IsNullOrEmpty(p.ObjClass))
            sb.Append(':').Append(p.ObjClass);
        if (!string.IsNullOrEmpty(p.Enum))
            sb.Append('(').Append(p.Enum).Append(')');
        return sb.ToString();
    }

    /// <summary>Space-joined, lower-cased once at parse time so live filtering is a cheap Contains.</summary>
    private static string BuildHaystack(string a, string b, string c, string d)
    {
        var sb = new StringBuilder(a.Length + b.Length + c.Length + d.Length + 3);
        sb.Append(a).Append(' ').Append(b).Append(' ').Append(c).Append(' ').Append(d);
        return sb.ToString().ToLowerInvariant();
    }
}
