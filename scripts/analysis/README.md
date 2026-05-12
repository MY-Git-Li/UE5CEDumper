# scripts/analysis/

Offline-analysis tooling that consumes the JSONL dumps produced by
**Export → Dump All Metadata (.jsonl)** in UE5DumpUI.

Goal: replace hand-curated keyword guesses in
`ui/UE5DumpUI/Services/PropertyScoringTable.cs` and
`ui/UE5DumpUI/Services/ClassLocationScorer.cs` with empirically-grounded
tables derived from real-game data.

## Workflow

1. Launch a UE4/5 game, attach UE5DumpUI as usual.
2. **Export → Dump All Metadata (.jsonl)** — saves
   `<game>-dump-<timestamp>.jsonl` (50–500 MB depending on game size).
3. Repeat for 3–6 games spanning UE versions and genres for cross-game
   signal.
4. Run the analyzer:
   ```bash
   python scripts/analysis/analyze_dumps.py dump1.jsonl dump2.jsonl dump3.jsonl
   ```
   Produces `analysis-report.md` with four sections:
   - **Dump summary** — UE version / object count / BPGC count per game.
   - **Top N property names** — exact field names, ranked by total hits.
   - **Top N property tokens** — KeywordTokenizer-tokenized buckets.
     Cross-referenced against existing category buckets so you spot
     candidates not yet in any keyword table.
   - **Candidate 'Unusual Location' class tokens** — class tokens
     hosting cheat-relevant properties but not in ClassLocationScorer
     yet (the build 671 `LocalPlayer`/`GameViewportClient`/`HUD`
     entries were hand-picked from this style of analysis).
5. Read the report. Cherry-pick the high-confidence rows back into the
   C# tables. Add a unit test per change so the addition is
   regression-proof.

## Dump file format (JSON Lines)

Each line is a self-contained JSON object with a `kind` discriminator:

| `kind` | Notes |
|---|---|
| `meta` | Always first. UE version, module name, object count, dumper build, options snapshot. |
| `class` | One per class-like UObject (`Class` + BPGC variants). Embeds `props[]` + `funcs[]`. |
| `error` | One per class walk failure. Iteration continues. |
| `summary` | Always last. Counters: classes_emitted / skipped / errors / scanned. |

Per-class record (excerpt):
```json
{
  "kind": "class",
  "name": "AnimMan_Player_C",
  "addr": "0x16FC4BA250",
  "path": "/Game/AdvancedLocomotionV4/.../AnimMan_Player_C",
  "meta": "BlueprintGeneratedClass",
  "super": "AnimMan_Enemy_C",
  "is_bpgc": true,
  "props_size": 2120,
  "instance_count": 1,
  "props": [
    {"name":"Health","type":"FloatProperty","offset":1724,"size":4},
    {"name":"Max_Health","type":"FloatProperty","offset":1728,"size":4},
    {"name":"IsDead","type":"BoolProperty","offset":1732,"size":1}
  ],
  "funcs": [...]
}
```

## Privacy

The dump contains class names, property names + offsets, and function
signatures — UE reflection metadata. It does **not** contain:
- Player save data
- Runtime UObject instance contents
- Memory snapshots
- Game asset content

Safe to share publicly for analysis purposes.

## Future expansions

- Compare two dumps from the same game across patches — surface field
  layout changes.
- Diff scoring table outputs across games to validate calibration.
- Cluster classes by property-name set similarity — surface
  "Inventory-like" classes that don't follow the naming convention.

These aren't built yet; PRs welcome.
