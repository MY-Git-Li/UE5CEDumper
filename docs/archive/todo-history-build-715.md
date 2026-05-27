# Historical: build-715 next-session starters (archived 2026-05-27)

Archived from [docs/todo.md](../todo.md) on 2026-05-27 after the
build-780 sync. The 7-item suggestion list below was the active
next-session pointer in the build 715-730 window (2026-05-20 → 26).

All seven items are still actionable in some form; their unshipped
remnants are tracked in the current `todo.md` either in the top
starter block (build 780) or further down in the per-section
backlogs. This file is **history**, not a live action list.

-----

## Original block, build 715 close-out (2026-05-20)

Session shipped 4 commits + dev→main fast-forward merge (30 commits, first
merge since build 590). Headline shipments:

- **18-game bias recheck (Frontiers, MMO/ARPG)** ([7ef5f57](https://github.com/bbfox0703/UE5CEDumper/commit/7ef5f57)) — docs-only. First MMO/ARPG-flavoured dump added (TL_* prefix, BossMonster / Pet / Affix / Dungeon / Sharpshooter). No keyword adds warranted despite landing in the predicted "out-of-genre" slot — the only promising candidate (`skill`, 8/18 games) was ~85% UI-widget noise. **Strongest robustness evidence yet** for the build-678/687 calibration.
- **Mailbox poll 10ms → 1ms** (build 707-710, [74db6b5](https://github.com/bbfox0703/UE5CEDumper/commit/74db6b5)) — `Mimic.cpp` poll thread uses `Sleep(kPollIntervalMs=1)` + `timeBeginPeriod(1)` bracket. CE-Lua tight-loop invokes save ~5ms/call of pure idle wait. Benchmark `Test_Mimic_PollLatency_OneMillisecond` in `dll_helpers_test` locks the win.
- **Invoke Stage 1 — surface UObject* expected UClass** (build 711, [024b6fd](https://github.com/bbfox0703/UE5CEDumper/commit/024b6fd)) — DLL `FunctionParam.objClassName` extracted via `ReadSubclassTypeName` for 7 pointer-flavoured types. Pipe walk_functions JSON adds `obj_class`. C# `FunctionParamModel.ObjectClassName` + InvokeParamDialog label becomes `[UObject*: AActor, 8B, off=0x10]`.
- **Invoke Stage 2 — instance picker dialog** (build 715, [515a344](https://github.com/bbfox0703/UE5CEDumper/commit/515a344)) — new `ObjectInstancePickerDialog` (no XAML, AOT-safe). Pointer-param rows in InvokeParamDialog grow `[Pick…] [null] [self]` buttons. Pick opens picker pre-filtered to expected UClass via existing `find_instances` pipe cmd. `ParamBufferBuilder.IsPickablePointerType` is the canonical 7-type contract (locked by 21 test theories).

Tests at the time: 910 → **935** (DLL self-tests 93 → 95 +mailbox latency; C# 817 → 840 +2 label tests +21 IsPickablePointerType theories).

**Stage 3 (class validation)** explicitly deferred — picker output is almost always class-correct in practice; revisit when a real crash motivates it.
Proposal B (per-row "similar BP-added properties" side panel) still **deferred indefinitely** — B' covers the workflow.

### Suggested next-session starters (the 7-item list)

1. **Live-game verification of Invoke Stage 1+2** — open InvokeParamDialog on a UFunction with a UObject* / UClass* / Soft* param: label should read `[UObject*: AActor, 8B]`; `[Pick…]` opens picker pre-filled with `AActor`, lists subclasses via substring match. Tests cover the contract; only live-game test pending. **S effort, user-driven**.
2. **More dumps for genre coverage** — 18-game corpus now spans JRPG/sim/ARPG/
   action-adventure/sandbox/racing/MMO-ARPG. **Still missing: pure-horror / fighting /
   RTS / sports-sim.** Only kind of dump that could still move the calibration needle.
   Use existing pipeline; no code change unless a new class-rule emerges. **S effort, user-driven**.
3. **`walk_functions_batch` follow-up** — sister to the build-696
   `walk_class_batch`. `DumpAllService` still does `WalkFunctionsAsync` once
   per emitted class (`IncludeFunctions=true` is default). Same trivial-loop
   pattern, same byte-equivalence safety net machinery already in place.
   Smaller win than walk_class_batch on its own (Dump All only) — skip unless
   profiling shows it as the new bottleneck. **S effort**.
4. **FString / FText / TArray input for baked AA Script** (open since build
   643-644 ES2 verification) — `KismetSystemLibrary::PrintString` is the
   obvious observable-side-effect verification target but currently unreachable
   because the helper's `writeBakedParams` only handles scalar inputs. Needs
   CE-side alloc + FString header write + free dance. See the
   `Call-UE-function feature gaps` section in the live todo.md.
   **M effort, med risk**.
5. **Invoke Stage 3 (class validation)** — DLL gains `validate_object_class(addr, expectedClassName)` (read addr→ClassPrivate→FName, walk super chain). InvokeParamDialog warns (not blocks) on mismatch before invoking. **S effort, low risk** — only worth doing if a real crash from class mismatch surfaces.
6. **Class Family Browser (Proposal C)** — bucketed view of game classes by
   inferred role (Character / Pawn / Inventory / Stats / Save / etc.). Genuine
   "where do I start exploring a new game?" entry point. The 18-game dump
   corpus would feed the heuristic-classification clustering work. **L effort,
   needs own planning round**.
7. **Runtime `keywords.json` override** — let users customise scoring tables
   without recompiling. Discussed during the anti-bias conversation; not yet
   built. Source-generated JSON serializer for AOT compat. **M effort, only
   if a user asks**.
