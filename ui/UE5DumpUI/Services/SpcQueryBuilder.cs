using System.Text;
using UE5DumpUI.Models;

namespace UE5DumpUI.Services;

/// <summary>
/// Pure, AOT-safe compiler from an <see cref="SpcQuery"/> to a single indexed
/// SQLite statement. The query is an N-way self-join over the <c>fields</c>
/// table: the oldest snapshot (<c>f0</c>) is the anchor, every later snapshot
/// <c>f{i}</c> inner-joins to it on the chosen identity key (so only fields
/// present in ALL selected snapshots survive — the candidate intersection), and
/// the directional predicate chain becomes <c>WHERE</c> clauses comparing
/// <c>f{i}</c> vs <c>f{i-1}</c>. Pushing the predicates into SQL (rather than
/// post-filtering in C#) lets a selective chain like "decreased, decreased"
/// collapse a million-row intersection to a handful using the ix_* indexes.
///
/// Kept separate from <see cref="SnapshotStore"/> so the SQL shape is unit
/// testable without a live database. Snapshot ids and the row limit are inlined
/// (validated <see cref="long"/>/<see cref="int"/> — injection-safe); only the
/// two LIKE filters are parameterised. See
/// docs/experimental-snapshot-spc-pivot.md §"Phase B" + §6 (schema/indexes).
/// </summary>
public static class SpcQueryBuilder
{
    /// <summary>A compiled SPC statement plus the LIKE parameter values the
    /// caller must bind (null = the filter was empty and its clause omitted).</summary>
    public sealed record Compiled(string Sql, string? ClassLike, string? PropLike);

    /// <summary>Identity key columns (besides class_fqn + prop_name, which are
    /// always join keys) for each join mode. See
    /// docs/experimental-snapshot-spc-pivot.md §5.</summary>
    public static string[] ExtraKeyColumns(SpcJoinMode mode) => mode switch
    {
        SpcJoinMode.Strict    => new[] { "norm_path", "prop_offset" },
        SpcJoinMode.Loose     => new[] { "outer_chain" },
        SpcJoinMode.InSession => new[] { "gobjects_index" },
        _                     => new[] { "norm_path", "prop_offset" },
    };

    public static Compiled Compile(SpcQuery q)
    {
        int n = q.SnapshotIds.Count;
        if (n < 2)
            throw new ArgumentException("SPC needs at least two snapshots.", nameof(q));
        if (q.Predicates.Count != n)
            throw new ArgumentException(
                "Predicate count must equal snapshot count.", nameof(q));

        var ids = q.SnapshotIds;
        var keyCols = ExtraKeyColumns(q.JoinMode);
        int last = n - 1;
        int limit = (q.MaxRows > 0 ? q.MaxRows : 50000) + 1;  // +1 detects truncation

        var sb = new StringBuilder(512);

        // --- SELECT: stable identity from the anchor, display columns from the
        //     newest snapshot (its obj_addr is the live CE-export target), and
        //     one hex column per snapshot for rendering the value sequence. ---
        sb.Append("SELECT f0.class_fqn")
          .Append(", f").Append(last).Append(".norm_path")
          .Append(", f0.prop_name")
          .Append(", f").Append(last).Append(".prop_offset")
          .Append(", f").Append(last).Append(".declared_type")
          .Append(", f").Append(last).Append(".obj_addr");
        for (int i = 0; i < n; i++)
            sb.Append(", f").Append(i).Append(".hex");

        // --- FROM / JOIN: anchor + one inner join per later snapshot. The
        //     vfields VIEW reconstructs the denormalised (identity + value) shape
        //     from the normalised objects+fields tables (schema v2). ---
        sb.Append(" FROM vfields f0");
        for (int i = 1; i < n; i++)
        {
            sb.Append(" JOIN vfields f").Append(i)
              .Append(" ON f").Append(i).Append(".snapshot_id=").Append(ids[i])
              .Append(" AND f").Append(i).Append(".class_fqn=f0.class_fqn")
              .Append(" AND f").Append(i).Append(".prop_name=f0.prop_name");
            foreach (var col in keyCols)
                sb.Append(" AND f").Append(i).Append('.').Append(col)
                  .Append("=f0.").Append(col);
            sb.Append(" AND f").Append(i).Append(".array_field IS NULL");
        }

        // --- WHERE: anchor scope + directional predicate chain + filters. ---
        sb.Append(" WHERE f0.snapshot_id=").Append(ids[0])
          .Append(" AND f0.array_field IS NULL");

        for (int i = 1; i < n; i++)
            AppendPredicate(sb, q.Predicates[i], i);

        string? classLike = null, propLike = null;
        string cls = q.ClassContains.Trim();
        if (cls.Length > 0)
        {
            sb.Append(" AND f0.class_fqn LIKE $cls");
            classLike = $"%{cls}%";
        }
        string prop = q.PropContains.Trim();
        if (prop.Length > 0)
        {
            sb.Append(" AND f0.prop_name LIKE $prop");
            propLike = $"%{prop}%";
        }

        sb.Append(" LIMIT ").Append(limit).Append(';');
        return new Compiled(sb.ToString(), classLike, propLike);
    }

    // Predicate i compares snapshot i against i-1. Index 0 is the baseline and
    // never produces a clause (treated as Any). Unchanged/Changed compare raw
    // hex (type-exact); Increased/Decreased compare numeric_value by the field's
    // declared width — no byte-reinterpret false hits.
    private static void AppendPredicate(StringBuilder sb, SpcPredicateKind kind, int i)
    {
        int p = i - 1;
        switch (kind)
        {
            case SpcPredicateKind.Unchanged:
                sb.Append(" AND f").Append(i).Append(".hex=f").Append(p).Append(".hex");
                break;
            case SpcPredicateKind.Changed:
                sb.Append(" AND f").Append(i).Append(".hex<>f").Append(p).Append(".hex");
                break;
            case SpcPredicateKind.Increased:
                sb.Append(" AND f").Append(i).Append(".numeric_value IS NOT NULL")
                  .Append(" AND f").Append(p).Append(".numeric_value IS NOT NULL")
                  .Append(" AND f").Append(i).Append(".numeric_value>f").Append(p).Append(".numeric_value");
                break;
            case SpcPredicateKind.Decreased:
                sb.Append(" AND f").Append(i).Append(".numeric_value IS NOT NULL")
                  .Append(" AND f").Append(p).Append(".numeric_value IS NOT NULL")
                  .Append(" AND f").Append(i).Append(".numeric_value<f").Append(p).Append(".numeric_value");
                break;
            case SpcPredicateKind.Any:
            default:
                break;  // no constraint
        }
    }
}
