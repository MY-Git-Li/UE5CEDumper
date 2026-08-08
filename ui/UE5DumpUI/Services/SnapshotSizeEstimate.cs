using System.Collections.Generic;
using System.Text;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Pure, dependency-free model of how many on-disk bytes a captured chunk will
/// cost in the SQLite <c>fields</c> table — used by the "Estimate size" pre-flight
/// (read-only: read a sample, never write the DB) so the user can decide
/// go/no-go before a multi-GB capture. The row shape mirrors
/// <c>SnapshotStore.ChunkInserter.Insert</c> EXACTLY (same text columns per
/// scalar / array-element row) plus the <c>ix_fields(snapshot_id, class_fqn)</c>
/// index, which stores a second copy of <c>class_fqn</c> per row. It is an
/// estimate (SQLite page slack / varint widths vary), so the constants are tuned
/// for order-of-magnitude accuracy, not byte-exactness — the goal is a reliable
/// "~X GB" go/no-go, deliberately a slight over-estimate (safer for a budget call).
/// </summary>
public static class SnapshotSizeEstimate
{
    // Per data-row fixed cost: SQLite record header (serial-type varint per column),
    // the b-tree leaf cell header (payload-length + rowid varints), the 8-byte REAL
    // numeric_value, the small integer columns (snapshot_id / prop_offset /
    // gobjects_index / elem_index), and average page slack.
    private const int TableRowOverhead = 40;
    // Per index-row fixed cost for ix_fields: the entry header + snapshot_id +
    // duplicated rowid varint + slack (class_fqn's own bytes are added separately).
    private const int IndexRowOverhead = 18;

    private static int Utf8(string? s) => string.IsNullOrEmpty(s) ? 0 : Encoding.UTF8.GetByteCount(s);

    /// <summary>Estimated stored bytes for one scalar field row of <paramref name="obj"/>.</summary>
    public static long ScalarRowBytes(SnapshotCapturedObject obj, SnapshotCapturedField f)
    {
        long text = Utf8(obj.ClassName) + Utf8(obj.Path) + Utf8(obj.OuterClassName)
                  + Utf8(f.Name) + Utf8(f.Type) + Utf8(obj.Addr) + Utf8(f.Hex);
        long index = Utf8(obj.ClassName) + IndexRowOverhead;   // ix_fields second class_fqn copy
        return text + TableRowOverhead + index;
    }

    /// <summary>Estimated stored bytes for one struct-array element field row.</summary>
    public static long ArrayRowBytes(SnapshotCapturedObject obj, SnapshotCapturedArray arr,
                                     SnapshotCapturedArrayElement el, SnapshotCapturedField f)
    {
        long text = Utf8(obj.ClassName) + Utf8(obj.Path) + Utf8(obj.OuterClassName)
                  + Utf8(f.Name) + Utf8(f.Type) + Utf8(obj.Addr) + Utf8(f.Hex)
                  + Utf8(arr.Field) + Utf8(el.KeyName) + Utf8(el.KeyValue) + Utf8(f.Name); // inner_prop_name == f.Name
        long index = Utf8(obj.ClassName) + IndexRowOverhead;
        return text + TableRowOverhead + index;
    }

    /// <summary>Sum the estimated stored bytes (scalar + array rows) across a captured chunk.</summary>
    public static long EstimateChunkBytes(IReadOnlyList<SnapshotCapturedObject> objects)
    {
        long total = 0;
        foreach (var obj in objects)
        {
            foreach (var f in obj.Fields)
                total += ScalarRowBytes(obj, f);
            foreach (var arr in obj.Arrays)
                foreach (var el in arr.Elements)
                    foreach (var f in el.Fields)
                        total += ArrayRowBytes(obj, arr, el, f);
        }
        return total;
    }

    /// <summary>Extrapolate a sample (bytes accumulated over <paramref name="sampledObjects"/>
    /// scanned GObjects indices) to the full <paramref name="totalObjects"/> count. Returns 0
    /// when the sample scanned nothing (avoids divide-by-zero).</summary>
    public static long Extrapolate(long sampledBytes, long sampledObjects, long totalObjects)
    {
        if (sampledObjects <= 0 || totalObjects <= 0) return 0;
        return (long)(sampledBytes * ((double)totalObjects / sampledObjects));
    }
}
