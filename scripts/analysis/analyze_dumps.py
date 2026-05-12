#!/usr/bin/env python3
"""
analyze_dumps.py — derive empirical keyword / class-bonus tables from
JSONL dumps produced by UE5DumpUI's "Export -> Dump All Metadata" feature.

USAGE
    python analyze_dumps.py <dump1.jsonl> [dump2.jsonl ...]
    python analyze_dumps.py *.jsonl --top 100 --keyword Health,Damage

WHAT IT DOES
    1. Loads N dumps (one per game). Each dump = meta line + class lines
       + summary line, as documented in DumpAllService.cs.
    2. Aggregates across games:
         - Property-name frequency (filter to game-only)
         - Class-name token frequency
         - Token co-occurrence: which class tokens host which property keywords?
         - Score distribution given current PropertyScoringTable
    3. Surfaces candidate keywords + class-bonus targets that the
       hand-curated tables in
       `ui/UE5DumpUI/Services/PropertyScoringTable.cs` and
       `ui/UE5DumpUI/Services/ClassLocationScorer.cs` are missing.

NOT IN SCOPE
    Doesn't modify any C# source. Outputs Markdown report + CSV.
    Author copies the actionable rows back into the C# tables by hand
    (with a sanity test added per change).

DESIGN NOTES
    - Each game has different mechanics; statistical signal needs
      ≥3 dumps to be meaningful. Single-dump runs are useful for
      sanity-checking a specific game but don't drive table changes.
    - `is_engine_class` filter mirrors the DLL's IsEnginePackage list.
      Analysis usually restricts to game classes since engine fields
      already have stable English names.
    - Tokenization matches the C# KeywordTokenizer rules so the
      derived keywords plug directly into the scoring table.
"""

from __future__ import annotations
import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Iterator


# =====================================================================
# I/O — read a JSONL dump
# =====================================================================

@dataclass
class Dump:
    """One game's dump, parsed into class records + meta."""
    path: Path
    meta: dict = field(default_factory=dict)
    classes: list[dict] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)
    summary: dict = field(default_factory=dict)

    @property
    def label(self) -> str:
        """Short human label — module name with extension trimmed."""
        m = self.meta.get("module", "")
        if not m:
            return self.path.stem
        return m.replace("-Win64-Shipping.exe", "").replace(".exe", "")


def load_dump(path: Path) -> Dump:
    d = Dump(path=path)
    with path.open(encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                rec = json.loads(line)
            except json.JSONDecodeError as e:
                print(f"  [warn] {path.name}:{lineno} bad JSON: {e}", file=sys.stderr)
                continue
            kind = rec.get("kind")
            if kind == "meta":
                d.meta = rec
            elif kind == "class":
                d.classes.append(rec)
            elif kind == "error":
                d.errors.append(rec)
            elif kind == "summary":
                d.summary = rec
    return d


# =====================================================================
# Tokenizer — mirrors C# KeywordTokenizer
# =====================================================================

_TOKEN_SPLIT_RE = re.compile(r"[_\-]+")

def tokenize(identifier: str) -> list[str]:
    """Mirror of C# KeywordTokenizer.Tokenize: split on underscores,
    hyphens, lower→upper transitions, and run-of-uppers→lower
    transitions. Lowercases output."""
    if not identifier:
        return []
    out: list[str] = []
    for part in _TOKEN_SPLIT_RE.split(identifier):
        if not part:
            continue
        cur = []
        for i, c in enumerate(part):
            # Rule 3: lower→upper split
            if cur and cur[-1].islower() and c.isupper():
                out.append("".join(cur).lower()); cur = [c]
                continue
            # Rule 4: run-of-uppers→lower (split before last upper)
            if len(cur) >= 2 and cur[-1].isupper() and cur[-2].isupper() and c.islower():
                last_upper = cur.pop()
                out.append("".join(cur).lower())
                cur = [last_upper, c]
                continue
            cur.append(c)
        if cur:
            out.append("".join(cur).lower())
    return out


# =====================================================================
# Filtering — engine vs game classes
# =====================================================================

ENGINE_PATH_PREFIXES = (
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
)

def is_engine_class(cls: dict) -> bool:
    path = cls.get("path", "")
    return any(path.startswith(p) for p in ENGINE_PATH_PREFIXES)


# =====================================================================
# Aggregations
# =====================================================================

@dataclass
class GameAggregates:
    """Per-game roll-up of property-name + class-token statistics."""
    label: str
    prop_name_freq: Counter = field(default_factory=Counter)         # exact prop name -> n
    prop_token_freq: Counter = field(default_factory=Counter)        # tokenized prop part -> n
    class_token_freq: Counter = field(default_factory=Counter)       # tokenized class part -> n
    is_bpgc_count: int = 0
    game_class_count: int = 0
    engine_class_count: int = 0
    # Co-occurrence: (class_token, prop_token) -> count
    class_x_prop: Counter = field(default_factory=Counter)


def aggregate(dump: Dump, game_only: bool = True) -> GameAggregates:
    agg = GameAggregates(label=dump.label)
    for cls in dump.classes:
        if is_engine_class(cls):
            agg.engine_class_count += 1
            if game_only:
                continue
        else:
            agg.game_class_count += 1
        if cls.get("is_bpgc"):
            agg.is_bpgc_count += 1

        class_tokens = set(tokenize(cls.get("name", "")))
        for ct in class_tokens:
            agg.class_token_freq[ct] += 1

        for prop in cls.get("props", []):
            name = prop.get("name", "")
            if not name:
                continue
            agg.prop_name_freq[name] += 1
            tokens = set(tokenize(name))
            for t in tokens:
                agg.prop_token_freq[t] += 1
            # Co-occurrence (only on game classes — engine cooccurrences
            # are dominated by base-class fields).
            if not is_engine_class(cls):
                for ct in class_tokens:
                    for pt in tokens:
                        agg.class_x_prop[(ct, pt)] += 1
    return agg


# =====================================================================
# Report generators
# =====================================================================

CHEAT_KEYWORD_HINTS = {
    # Hints for picking interesting tokens out of the noise. Each entry
    # is a *category label* the analyst can sort by; the table here is
    # NOT the keyword table — it's a UI sort for the analyst.
    "stats":     ("health", "hp", "mp", "mana", "stamina", "energy", "level",
                  "lv", "xp", "exp", "experience", "dead", "alive", "max"),
    "combat":    ("damage", "dmg", "defense", "armor", "armour", "crit",
                  "attack", "atk", "weapon", "hit", "resist"),
    "resources": ("gold", "coin", "coins", "money", "gem", "diamond", "ammo",
                  "stack", "count", "quantity", "amount"),
    "movement":  ("speed", "velocity", "jump", "walk", "sprint", "run",
                  "friction", "gravity", "dash", "climb"),
    "utility":   ("quest", "save", "load", "checkpoint", "cheat", "debug",
                  "immortal", "invincible", "godmode", "noclip"),
}

def categorize_token(tok: str) -> str | None:
    for cat, words in CHEAT_KEYWORD_HINTS.items():
        if tok in words:
            return cat
    return None


def report_top_property_names(aggs: list[GameAggregates], top: int = 100) -> str:
    """Table: property-name frequency × game (so we see which names are
    cross-game, which are single-game noise)."""
    merged = Counter()
    per_game: dict[str, dict[str, int]] = defaultdict(dict)
    for agg in aggs:
        for name, n in agg.prop_name_freq.items():
            merged[name] += n
            per_game[name][agg.label] = n

    lines: list[str] = []
    lines.append(f"# Top {top} property names across {len(aggs)} game(s)")
    lines.append("")
    lines.append("| Rank | Name | Total | Games hit | Per game (label:count) |")
    lines.append("|---:|---|---:|---:|---|")
    for rank, (name, total) in enumerate(merged.most_common(top), 1):
        per = per_game[name]
        games_hit = len(per)
        per_str = "; ".join(f"{lbl}:{n}" for lbl, n in sorted(per.items()))
        lines.append(f"| {rank} | `{name}` | {total} | {games_hit} | {per_str} |")
    return "\n".join(lines)


def report_top_prop_tokens(aggs: list[GameAggregates], top: int = 60) -> str:
    """Property TOKEN frequency — these are the candidate keywords for
    the scoring table. Cross-references against our current categorisation
    so the analyst sees which categories the tokens land in."""
    merged = Counter()
    per_game: dict[str, set[str]] = defaultdict(set)
    for agg in aggs:
        for tok, n in agg.prop_token_freq.items():
            merged[tok] += n
            per_game[tok].add(agg.label)

    lines: list[str] = []
    lines.append(f"# Top {top} property TOKENS (candidate keywords)")
    lines.append("")
    lines.append("Use this to spot tokens that look cheat-relevant but aren't yet in PropertyScoringTable's tables.")
    lines.append("")
    lines.append("| Rank | Token | Hits | Games | Existing category |")
    lines.append("|---:|---|---:|---:|---|")
    for rank, (tok, hits) in enumerate(merged.most_common(top), 1):
        cat = categorize_token(tok) or ""
        lines.append(f"| {rank} | `{tok}` | {hits} | {len(per_game[tok])} | {cat} |")
    return "\n".join(lines)


def report_unusual_locations(aggs: list[GameAggregates], min_count: int = 3) -> str:
    """Class tokens hosting Stats/Combat/Resources tokens — flag the
    ones we DON'T have in the ClassLocationScorer table as candidates
    for the Unusual list (cheat-relevant property in a non-canonical
    container)."""
    KNOWN_BONUSES = {
        # From ClassLocationScorer.PropertyRules — Expected (+) entries.
        "character", "pawn", "playercontroller", "playerstate",
        "abilitysystem", "attributeset", "inventory", "equipment", "player",
        "gamemode", "gameinstance", "savegame", "playerprofile",
        # Unusual (+ flagged) entries.
        "localplayer", "gameviewportclient", "hud", "ucheatmanager", "cheatmanager",
    }
    cheat_tokens = set().union(*CHEAT_KEYWORD_HINTS.values())

    merged: Counter = Counter()
    for agg in aggs:
        for (ct, pt), n in agg.class_x_prop.items():
            if pt not in cheat_tokens:
                continue
            if ct in KNOWN_BONUSES:
                continue
            merged[(ct, pt)] += n

    lines: list[str] = []
    lines.append(f"# Candidate 'Unusual Location' class tokens (host cheat-relevant properties, not yet in ClassLocationScorer)")
    lines.append("")
    lines.append(f"Filter: ≥ {min_count} occurrences across all dumps.")
    lines.append("")
    lines.append("| Class token | Property token | Hits |")
    lines.append("|---|---|---:|")
    for (ct, pt), n in merged.most_common(200):
        if n < min_count:
            break
        lines.append(f"| `{ct}` | `{pt}` | {n} |")
    return "\n".join(lines)


def report_meta(aggs: list[GameAggregates], dumps: list[Dump]) -> str:
    lines: list[str] = []
    lines.append("# Dump summary")
    lines.append("")
    lines.append("| Game | UE | Module | Object count | Game classes | BPGCs | Engine classes |")
    lines.append("|---|---:|---|---:|---:|---:|---:|")
    for agg, dump in zip(aggs, dumps):
        ue = dump.meta.get("ue_version", "?")
        mod = dump.meta.get("module", "")
        obj = dump.meta.get("object_count", "?")
        lines.append(f"| {agg.label} | {ue} | {mod} | {obj} | "
                     f"{agg.game_class_count} | {agg.is_bpgc_count} | "
                     f"{agg.engine_class_count} |")
    return "\n".join(lines)


# =====================================================================
# Entry
# =====================================================================

def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("dumps", nargs="+", type=Path,
                   help="JSONL dump files (one per game)")
    p.add_argument("--top", type=int, default=100,
                   help="Top N rows in frequency tables (default 100)")
    p.add_argument("--include-engine", action="store_true",
                   help="Include engine /Script/* classes in aggregates")
    p.add_argument("--output", type=Path, default=Path("analysis-report.md"),
                   help="Markdown report path (default: analysis-report.md)")
    args = p.parse_args(argv)

    dumps: list[Dump] = []
    for path in args.dumps:
        if not path.exists():
            print(f"[error] missing dump: {path}", file=sys.stderr)
            return 2
        print(f"[load] {path} ...", file=sys.stderr)
        dumps.append(load_dump(path))

    aggs = [aggregate(d, game_only=not args.include_engine) for d in dumps]

    report = "\n\n".join([
        report_meta(aggs, dumps),
        report_top_property_names(aggs, top=args.top),
        report_top_prop_tokens(aggs, top=min(args.top, 60)),
        report_unusual_locations(aggs),
    ])
    args.output.write_text(report, encoding="utf-8")
    print(f"[done] wrote {args.output} ({len(report):,} chars)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
