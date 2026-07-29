# AOB code-block library — EVALUATED 2026-07-29, NOT BUILT (decision pending)

**The idea (maintainer's).** Extract the `.text` regions the AOB patterns actually land on —
frequently-hit **hotspots**, per-game **occasional/unique** sites, and **decoy noise** — commit them
to the repo, and add a fast pre-test script. When a new game's AOBs miss, compare against these
blocks first instead of reaching for the 40-minute sweep.

**Why it matters more than it looks.** The sweep needs `D:\Tools\GHIDRA_Projs` (120.94 GB) and a
Ghidra install. **The maintainer's second machine has neither**, plus only 1–2 UE games installed —
and Auto Analyze is 3–4 hours per project there. So on that machine the sweep does not exist, and a
committed block library would be the *only* diagnostic available. That, not speed, is the case for
building it.

-----

## 1. The copyright question — I cannot clear this, and will not pretend to

Not a legal opinion. What can be said factually:

* **Anonymising the blocks does NOT change the copyright position.** Removing the game name removes
  *attribution*, not the copyright status of the excerpt. It is still worth doing for other reasons
  (it avoids implying endorsement, and avoids the "how to cheat at game X" framing), but it must not
  be mistaken for a fix. It arguably makes one thing *worse*: it removes the ability to honour a
  targeted takedown or to show the excerpt's scope in good faith.
* **Size is genuinely tiny.** A useful window is ~64–128 bytes around a match, out of a 100–200 MB
  image. That is about as de minimis as an excerpt gets, and the use is interoperability analysis.
  But de minimis is a *defence*, not a permission.
* **Most of what would be stored is not the game studio's expression at all** — it is MSVC's codegen
  from Epic's engine source (lazy-init thunks, TSet stride math, prologues). Whose work it is, is
  exactly the kind of question that needs a lawyer, not a heuristic.

**This does not have to be resolved, because of §2.**

-----

## 2. The decisive observation: the self-built oracles already carry the diagnostic value

The corpus splits into two provenance tiers, and they are not equally hard:

| tier | binaries | copyright position |
|---|---|---|
| **Self-built** | 4.15 Flying · 4.23.1 Flying (Shipping + DebugGame) · 4.27.2 Flying (Shipping + Development + DebugGame) · StackOBot 5.7.4 (Shipping + DebugGame) · 5.8.0 (Shipping + DebugGame) · 5.8.1 (Shipping + Development) · Titan 5.8.0 DebugGame | the maintainer's **own build output**, reproducible from installed engines |
| **Third-party** | Hogwarts, FF7R/Rebirth, Avowed, Palworld, Satisfactory, Everspace 1/2, DQ7R/DQXIS/DQ12, Octopath, TQ2, Meltopia, Solarpunk, DropIn, Breeders, Maelstrom, Elliot, Nekopara, Fantasynth, HeliumRain, FreudGate, LightMaze, Grimhook, Artisan, ManorLords | third-party shipped binaries |

**Every finding of the 2026-07-29 session came from the self-built tier**, without exception:

* `GOBJ_DI427_1` is config-gated, not a 4.27 trait — proved on 4.27.2 Flying × 3 configs.
* GNames unreachable on non-Shipping UE5 — proved on 4.23.1, 4.27.2, 5.7.4, 5.8.0, 5.8.1 + Titan.
* The root cause (first `lea` targets **rbx/r15**, patterns pin rax/r8/rdx/rsi/rbp) — read straight
  off self-built bytes.

The self-built tier spans **6 engine versions × up to 3 build configs**, which is a broader
version/config matrix than most of the third-party corpus. So the proposal can be built from the
easy tier alone and still answer the questions it exists to answer.

-----

## 3. What the library can and cannot answer

**CAN** — everything shape-related, which is what actually bit us:
* "Does my new pattern match a known decoy shape?"
* "What does the true site look like on engine X, config Y?" — the register-allocation question that
  caused BOTH failures found this session (`GOBJ_V1`'s hardcoded `rcx` on DropIn; GNames' rax/r8/rdx
  vs the rbx/r15 the 5.7+ non-Shipping codegen emits).
* "Is this new game's codegen shaped like anything we have seen?"

**CANNOT** — anything density-related:
* **"How many spurious hits will this take on a 150 MB `.text`?"** That is REPORT.md §6 (hits/MB) and
  it needs whole images. A block library structurally cannot produce it.
* "Which pattern would the runtime land on first?" — needs the full priority walk over a real image.

⚠ **Therefore it is a TRIAGE tool, never an acceptance gate.** `Himmel.h` step 5 ("verify against the
corpus before trusting it") must keep meaning the sweep. The risk of building this is precisely that
it becomes a shortcut that lets an unmeasured pattern into the table — a pattern can pass every block
and still take 22,000 hits on a real game, which is exactly how `GWLD_V3` and `GNAM_V3` behave.

-----

## 4. Design, if it is built

Store structured records, not a byte blob:

```
tools/ghidra/blocks/<target>/<id>.json
{
  "id": "GNAM-TRUE-5.8.1-DEV-01",
  "target": "GNames",
  "class": "true" | "decoy" | "hotspot",
  "engine": "5.8.1", "config": "Development",
  "provenance": "self-built",
  "site_role": "FNamePool lazy-init twin-LEA (initialized path uses rbx)",
  "bytes": "74 09 48 8d 1d .. .. .. .. eb 2f 48 8d 0d .. .. .. .. e8",
  "target_offset": 13,
  "expect": { "should_match": ["GNAM_ES53_1"], "must_not_match": ["GNAM_CT3","GNAM_CT4"] }
}
```

* **`bytes` present only for `provenance: self-built`.** Third-party sites get the same record with
  `bytes` omitted and a `sha256` of the window instead — enough to record that the shape exists and
  to let anyone who owns that game regenerate it locally, without redistributing anything.
* `tools/ghidra/blocktest.py` runs every `Himmel.h` pattern against every block and asserts the
  `expect` sets. Milliseconds, no Ghidra, no corpus — **runs in CI and on the bare second machine.**
* The repo currently has **no pattern regression test at all** between `extract_patterns.py`'s
  dead-constant check and the 40-minute sweep. This would fill that gap, which is arguably a bigger
  win than the second-machine diagnostic.

Seed set (~40–60 blocks), all self-built, all from sites this session already characterised:
the GNames twin-LEA across 4.23 / 4.27 / 5.7.4 / 5.8 × Shipping-vs-non-Shipping; the GObjects
chunk-load register spread; the `check()`-fail `E8 … 90 CC` shape present/absent by config; and the
known decoys `GCoreObjectArrayForDebugVisualizers`, `GNameBlocksDebug`, and the pre-4.23
`GNAM_CT3`/`CT4`/`G42_1` convergence.

-----

## 5. Recommendation

**Worth building, in the self-built-only form, as a shape-regression test.** It is small (a few
hundred KB), needs no legal call, gives the second machine a real diagnostic, and closes a genuine
testing gap. Third-party blocks: metadata + hash only.

**Open for the maintainer, not for me:** whether to store third-party bytes at all. My input is only
that §2 means you do not have to, so the cheapest resolution is to not take the risk.
