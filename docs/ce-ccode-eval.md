# CE `{$CCODE}` adoption — EVALUATED 2026-08-07, **DO NOT ADOPT**

**Verdict: there is nothing here for `{$CCODE}` to attach to.** Not "it would be a small win", not
"the numbers do not justify it" — the repo emits **zero injection hook sites**, and a CCODE block
without a hook is dead text. The decision rests on architecture, not on a benchmark, and no
benchmark would change it.

This document exists so the question does not get re-opened from scratch, and so that if the
condition that would change the answer ever arrives, it is recognisable. Companion to
[ce-ccode-reference.md](ce-ccode-reference.md), which is the *how it works* manual; this is the
*should we* record.

> **Method.** Three independent analyses (an exhaustive hot-path census, a cost model, and an
> analyst instructed to build the strongest case FOR adoption), then a judge that weighed the
> pro-adoption case against the other two and against the CE source directly. The pro-adoption
> case was refuted on evidence, not overruled. Everything below is either verified by source
> inspection or explicitly labelled as an estimate.

---

## 1. The distinction the whole question turns on

`{$lua}` — what this repo emits — is **not** `{$LUACODE}`. They are not variants of one thing; they
run in different processes at different times.

| Directive | Runs where | Runs when | Cost per user action |
|-----------|-----------|-----------|----------------------|
| **`{$lua}`** ← *what we emit* | **CE's own process**, CE's Lua engine | At AA script **assembly** time — once per `[ENABLE]` / `[DISABLE]` | One-shot |
| `{$LUACODE}` | The **game's** process (injected) | Every time the hook site is reached | SafeCall stub **+ a call into CE's Lua VM**, per hit |
| `{$CCODE}` | The **game's** process (injected) | Every time the hook site is reached | SafeCall stub **+ compiled native code**, per hit |

**`{$CCODE}` is an alternative to `{$LUACODE}`, never to `{$lua}`.** Reading "our scripts use Lua,
CCODE is faster than Lua" as an argument for adoption is the trap this section exists to disarm —
the two Luas in that sentence are different mechanisms.

---

## 2. Why it cannot apply here (the load-bearing fact)

Verified over `ui/UE5DumpUI/Services/*.cs`, `scripts/*.lua` and `scripts/UE5CEDumper.CT`:

| Check | Result |
|-------|--------|
| `{$CCODE}` occurrences | **0** |
| `{$LUACODE}` occurrences | **0** |
| AA injection idioms (`aobscanmodule`, `newmem:`, `originalcode:`, `globalalloc`, a `jmp` into a cave) | **0** |
| `{$asm}` occurrences | **31 — every one a bare terminator** of a `{$lua}` block |
| AA directives we *do* emit | only `define(...)` + `registersymbol(...)` (`CeXmlExportService.cs:1298-1304`) — a **data** symbol |

The shape of every generated block is literally `[ENABLE]` `{$lua}` … `{$asm}` `[DISABLE]`. There is
no `alloc`, no code cave, no jump patch — **nothing is injected into the game by anything CE runs
for us.**

CCODE's contract requires a `call` instruction into its SafeCall stub
([ce-ccode-reference.md](ce-ccode-reference.md) §1). With no hook site there is no call, so the
block never executes. Adopting CCODE is therefore not a block swap — it is *"introduce code
injection into CE-emitted artifacts for the first time"*, and next to that decision the
CCODE-vs-LUACODE question is a rounding error.

> **A note on false positives.** `registerSymbol` / `allocateMemory` / `deAlloc` / `AOBScanModuleUE`
> do appear in our generators. Those are **CE-side Lua API calls**, not AA directives — they run in
> CE's process and allocate in CE or publish a symbol. They look like injection in a grep and are
> not. This is the specific misreading to guard against when re-auditing this question.

---

## 3. The cost ladder, and where our work already sits

Every CCODE (and LUACODE) hit pays a fixed **SafeCall stub** before any of your code runs — a
512-byte allocated stub setting up a 0x2A0 (672) byte frame: `pushfq`, `push rax`, 16-byte stack
realign, **`fxsave`** (512 bytes of FPU/SSE state), 15 GPR stores, the call, 15 GPR restores,
**`fxrstor`**, restore RSP, `pop rax`, `popfq`. Unconditional, every hit.

| Mechanism | Per-hit tax | Where our equivalent work runs today |
|-----------|-------------|--------------------------------------|
| **Hand-written AA asm** at a hook site | 1-2 instructions. No stub. | — |
| **`{$CCODE}`** | Full SafeCall stub, then native code | — |
| **`{$LUACODE}`** | Full SafeCall stub, **then enter CE's Lua VM** | — |
| **Our injected C++ DLL** | **No stub at all** — MinHook trampoline into MSVC-compiled code | Stark's ProcessEvent hook; the Solitar / Laufen / Hemmung / Solide / Schlacht / Dunste workers |

**Our DLL is faster than CCODE would be, not slower.** Moving any per-hit work from the DLL to
CCODE would be a regression: it would add the stub, and swap MSVC + SEH-guarded reads for TCC's
`-nostdlib`, no-C++, incomplete-C11 output.

CE's own reference agrees on fit — [ce-ccode-reference.md](ce-ccode-reference.md) §13: *"Suitable
for use in an injection hook (executed once per frame / per call). **Not suitable inside a tight
loop.**"*

### Where the emitted artifacts *do* spend time

All of it is CE-side `createTimer` polling — and **a timer callback is not a hook site**, so CCODE
cannot reach any of it by construction:

| Path | Rate | Per iteration |
|------|------|---------------|
| Freeze helper tick (`scripts/ue5_freeze_helper.lua:423-437`, timers `:457-468`) — **the hottest thing we emit** | 50 ms | up to 2048 cached instances × (readQword + typed write) |
| Freeze helper rescan | 5 s | up to 16 `CMD_LIST_INSTANCES` round-trips |
| Standalone trainer Fly (`StandaloneTrainerScriptGenerator.cs:315-346`) | 16 ms | ~13-15 cross-process ops |
| Standalone trainer knobs / GodMode | 50 ms | ~7-9 ops |

To reach these with CCODE you would first have to convert polling into hooking — a different
program with a different risk surface, not an optimisation.

---

## 4. Pros & cons

### Pros — real, and none of them applies to us today

- **Strictly dominates `{$LUACODE}` at any hook site.** Identical stub, identical parameter
  marshalling (CE literally calls the same `AddSafeCallStub` for both —
  `autoassemblercode.pas:293/295` for CCODE, `:1137` for LUACODE), but the C path runs compiled
  native code where the Lua path enters CE's Lua VM on the game's own thread. At a per-frame site
  LUACODE is unusable and CCODE is fine. **If a hook site ever exists, this choice is not close.**
- **No stack discipline to get right.** The stub aligns to 16 bytes, saves and restores every GPR
  plus RFLAGS plus FPU/SSE state, and restores RSP. You can call other functions safely from inside
  the block.
- **Registers as C variables.** `{$CCODE} health=RBX}` gives you `health` as a normal variable, and
  assigning to it writes the register back. Expressing a conditional in C beats expressing it in
  hand-written asm for anything non-trivial.
- **Real debugging.** CE extracts STAB info, so you can breakpoint inside the C and see source
  lines and locals — unless you pass `NODEBUG`.
- **No DLL needed.** It is self-contained inside a `.CT`, which matters for a
  distribute-one-file trainer.

### Cons

- **Slower than hand-written asm, always.** The stub is pure overhead an asm patch does not pay —
  `fxsave`/`fxrstor` alone is the dominant term. CE's own doc estimates ~200-400 cycles and says
  outright not to put it in a tight loop.
- **Needs a code-site AOB, per game and per patch.** This is the decisive cost for this repo. Our
  entire signature effort (`Himmel`, 158 entries) targets exactly five **data** globals
  (GObjects / GNames / GWorld / SparseDelegates / GEngine), built from a scripted 57-row sweep with
  38 full-PDB oracles. A *gameplay code-site* AOB is a categorically harder per-title problem, and
  it collides with CLAUDE.md's standing rule that offsets are dynamically verified and never
  hardcoded.
- **It inverts the failure mode from benign to fatal.** Today, a pointer that fails to resolve means
  nothing happens. A bad hook byte-patches the instruction stream and the game dies. None of the
  CE-Lua hygiene machinery — untick-on-bailout, the contract range check, `CeMailboxBailoutTests`,
  `check_mailbox_contract.py` — covers "the game crashed".
- **TCC is not a full C toolchain.** `-nostdlib` on every path, no C++, incomplete C11, and library
  functions must already be present in the target process.
- **The parser has real defects that fail silently** — see
  [CE-Bugs-Minesweeper.md](CE-Bugs-Minesweeper.md) §Appendix. Comma-separated parameters are
  dropped; a mistyped register name binds to RAX and is written back to RAX; `RBPF` lands on the
  stub's saved-RSP pointer (crash) and `RSPF` on the RBP slot (corruption); `PREFIX=` on a
  `{$CCODE}` line injects a phantom variable. None of these errors; they all just do the wrong
  thing.
- **You get a second toolchain in the artifact.** A `.CT` that carries C source compiled at enable
  time by a vendored TCC is harder to review, diff and support than one that carries Lua.

---

## 5. Two corrections to the intuitions that led here

**"CCODE was developed to make LUACODE faster."** Reasonable guess, and the chronology refutes it.
`autoassemblercode.pas` — the file implementing `{$C}`, `{$CCODE}` **and** `{$LUACODE}` — was
introduced whole by commit `8074febf` (2021-01-21), *"add c(c99'ish)-compiler to CE (Modified
TCC)"*. `git grep LUACODE` on that commit's **parent** finds nothing: LUACODE did not exist before
it. **They shipped together.**

The accurate reading is better than the guess: CE built **one register-marshalling machine** — one
parameter-pointer layout, one SafeCall stub emitter, one `AddSafeCallStub` — and hung two backends
off it, native and interpreted. The relationship is not "C fixes Lua"; it is "one mechanism, two
languages, pick per job." Which is also why the two share every bug in that layer.

**"CCODE is slower than pure AA asm but faster than Lua."** Correct on both halves, and §3 gives
the mechanism for each: the stub is the tax asm avoids, and the interpreter is the tax Lua adds on
top of the same stub.

---

## 6. The strongest case for adoption, and why it fails

Built deliberately by an analyst told to argue for it. It is worth recording because it is the
argument that will recur.

> The standalone **no-DLL** teleport has a user-confirmed snap-back (`docs/todo.md`, 2026-07-23).
> Unlike every other candidate this is not a "hold a value" problem that polling handles fine — it
> is a **one-shot race**, and a poller has no phase relationship to the game's frame, so *no* tick
> rate wins it. A hook wins by construction. And this is the one flavour where the DLL's answer
> (ProcessEvent invoke) is unavailable by design.

**It fails because the premise is a misdiagnosis.** `dll/src/Wirbel.cpp:588-591` records the actual
mechanism: the renderer and CharacterMovement read the cached world transform
`USceneComponent::ComponentToWorld`, which a raw `RelativeLocation` write does **not** refresh, and
`UpdateComponentToWorld` is not reachable by reflection. The generator's own note
(`StandaloneTrainerScriptGenerator.cs:441-448`) says the same: *"the coords change but the character
may not visibly move."*

That is **missing propagation, not timing.** Writing at a better moment cannot help, because timing
is not the variable — winning requires *calling engine code*, which is exactly what the DLL flavour
does and what the no-DLL flavour cannot.

**And the repo already has a hook-free answer for this case.** `Wirbel::DeepForceWorldPos`
(`Wirbel.cpp:597-629`) is a pure data operation: read a 0x400 window off RootComponent, find the
FVector(s) within 10 uu of the *old* world position, overwrite them with the target. No code
execution, no hook, no code-site AOB. It is the last-resort path for games that cook out every K2
setter (SE HD-2D / Octopath) and is live-proven there. The standalone trainer already has the
primitives it needs — `UE5T_rdv` / `UE5T_wrv` (`:167-172`), `rootOff`, `vecWidth` (`:85`). **Porting
it is a bounded one-shot Lua scan, and it keeps the flavour's "inject nothing" premise intact.**

So the one candidate that survived three lenses turns out to be neither a CCODE problem nor a hook
problem, with a known solution already in this codebase.

---

## 7. What would change this verdict

Two falsifiable conditions. Anything else is not an argument.

1. **A hook site comes to exist for a reason other than performance.** If this repo ever adopts an
   injected AA hook for a capability that genuinely cannot be had another way — with a per-game
   code-site AOB already being maintained for that independent reason — then at that site CCODE
   should be used unconditionally over LUACODE. **The test is ordering:** does a maintained
   code-site AOB exist *first*? CCODE is the right answer *given* a hook; it must never be the
   argument *for* one.
2. **The standalone teleport is proven to be a genuine re-assert race after the propagation fix
   lands.** Port `DeepForceWorldPos` to the standalone flavour and re-test on the failing title. If
   the character moves, the diagnosis was propagation and this case is dead permanently. If it moves
   and is then *visibly snapped back* on the following tick, the refutation in §6 is wrong and the
   question genuinely reopens — though even then the cheaper answer is a repeating write, not
   injected code (the DLL's own CMC-freeze path at `Wirbel.cpp:915` already handles that case
   hook-free).

**What would NOT change it:** a benchmark showing CE-side Lua loops are slow. That measures the
wrong thing — CCODE cannot reach a `createTimer` callback no matter how slow it is.

---

## 8. Measured vs estimated

Stated explicitly, per this repo's rule that a number without its conditions is not a measurement.
**This decision rests on architecture, not numbers. Nothing here was benchmarked.**

- **MEASURED** — exactly one figure in the whole exercise, and it is this repo's own: CE's
  `sleep(1)` = **15.47 ms**, measured 2026-08-06 on Windows 11 26200 across two deliberately
  different CPUs, documented at `CeMailboxLayout.cs:95-132`. Also from this repo's own
  instrumentation: `docs/multipipe-eval.md` §10 — ~0.119 ms fixed cost per pipe round-trip.
- **ESTIMATED** — *every* performance figure quoted anywhere in this evaluation. The "200-400
  cycles" for the SafeCall stub is **CE's own doc's estimate**; CE's source states no number. The
  per-op cross-process costs and the ops/s totals are arithmetic on verified operation counts times
  literature ranges. Order of magnitude only. The three analyses differed by up to ~3× on the same
  paths, which is itself evidence none was measured.
- **VERIFIED BY SOURCE** — and this is what the decision actually turns on, none of it a number:
  zero `{$CCODE}` / `{$LUACODE}` / AA-injection idioms in any emitted artifact; all 31 `{$asm}`
  blocks are bare terminators; the only AA directives are `define` / `registersymbol`
  (`CeXmlExportService.cs:1298-1304`); CCODE and LUACODE share `AddSafeCallStub`
  (`autoassemblercode.pas:293/295` vs `:1137`); both were introduced together in `8074febf`; the
  standalone teleport failure is missing `ComponentToWorld` propagation (`Wirbel.cpp:588-591`);
  `DeepForceWorldPos` is pure read/write (`Wirbel.cpp:597-629`).

*"There is no hook site to attach CCODE to"* is categorical. Manufacturing a benchmark would dress
a structural fact up as a close call.

---

## 9. Findings that were incidental to this evaluation

The hot-path census turned up things that have nothing to do with CCODE. Recorded here so they are
not lost, with their real owner named.

- **Three generators hand-roll the mailbox wait** instead of calling
  `CeLuaHygiene.AppendMailboxWait`, and all three count `sleep(1)` iterations against a millisecond
  constant — so `MailboxPollTimeoutMs = 10000` is a **~155 s** timeout, not 10 s
  (10000 × 15.47 ms), and `MailboxIdleWaitMs = 100` is **~1.55 s**, not 100 ms.
  `InvokeScriptGenerator.cs:114-119`, `PointerQueryScriptGenerator.cs:121-124` and `:129-132`,
  `CoordLibraryScriptGenerator.cs:257-260`. Eight generators use the shared emitter (which takes a
  real deadline from `getTickCount`); exactly these three do not. This is the defect CLAUDE.md
  records as fixed in build 2743 — *"Hand-rolling any of these is how build 2743's three defects
  reached all seven copies of the mailbox wait at once"* — surviving in the copies that were never
  converted. `PointerQueryScriptGenerator.cs:132` additionally still emits the banned guess-message
  `'(DLL not responding?)'`.
- **`CeLuaHygiene.AppendIdleWait` itself counts iterations** (`CeLuaHygiene.cs:128-132`), so the
  "100 ms" idle wait documented at `CeMailboxLayout.cs:144` is ~1.55 s everywhere. This one errs
  long, so it is benign — but the constant's doc-comment is wrong.
- The re-assert workers are **not per-frame**: `Grimoire.h:315/318/323/328` put them at 250-300 ms
  (3.3-4 Hz) on dedicated threads. Only Stark's ProcessEvent hook is per-frame.
