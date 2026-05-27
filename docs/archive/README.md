# Archived Documentation

These documents were moved here because they no longer reflect the current
state of the code, but are preserved for historical reference.

For up-to-date information, see the active docs in [`../`](..) — start with
[`../dev-log.md`](../dev-log.md) (running milestone log) and the docs index
in [`../../CLAUDE.md`](../../CLAUDE.md).

## Files

| File | Original purpose | Why archived |
|------|------------------|--------------|
| `UE5CEDumper-UX.md` | UX design spec dated 2026-03-10, written during early Avalonia panel design | The implementation has diverged: Property Search / Game Class / Class Structure routing fixes / Find Refs / OptionalProperty / etc. landed after this spec. The shipped UI is the source of truth; refer to the live AXAML / ViewModels for current behavior. |
| `ufunction-invoker-roadmap.md` | Implementation plan for the UFunction invoker feature | **Phase I (script generation) is fully shipped** — every checkbox in the roadmap is done. Phase II (in-process ProcessEvent dispatch) was superseded by `Stark.cpp` (GameThreadDispatch + MinHook ProcessEvent hook) and the `invoke_function` pipe command. The roadmap is a snapshot of the intent, not a plan for outstanding work. |
| `todo-history-build-715.md` | The 7-item "next-session starters" block that sat at the top of `todo.md` between 2026-05-20 (build 715) and 2026-05-27 (build 780) | Superseded by the new starter block at the top of `todo.md` after the build-780 session shipped picks #3 / #4 / #5 / #6 + Value Search Phase 2. Unshipped remnants (Stage 3 / Class Family Browser / Runtime keywords.json / etc.) are still tracked in the live `todo.md` under their respective per-section backlogs. |
