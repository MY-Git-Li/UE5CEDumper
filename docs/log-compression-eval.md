# Log compression — SHIPPED build 2730. Verdict: **`compact /c /exe:LZX`, not zip/gz.**

> **Status: evaluated, measured, and BUILT (build 2730).** Every number below is from a real run
> on the maintainer's own `%LOCALAPPDATA%\UE5CEDumper\Logs` (698 files / 111.9 MB), on a copy in
> the scratchpad — the live log folder was not modified. Re-measure before trusting these on a
> different machine; the method is at the bottom. §5 describes what shipped.
>
> **In-app VERIFIED (2026-08-05)**: both triggers exercised on the maintainer's machine —
> **180 log files, 85.1 MB → 6.5 MB on disk (78.6 MB saved), 0 failed**, the whole folder now
> 111.9 MB logical / 17.8 MB on disk. That run is also what exposed §5a.

The 21-day retention window (`Constants.LogMaxAgeDays` / `Grimoire::LOG_RETENTION_DAYS`) is
doing its job, and the folder still reached **111.9 MB**. Retention bounds the *age* of the
corpus, not its size — three weeks of multi-game sessions is simply a lot of text. The question
is whether to compress the archived part of it.

-----

## 1. The headline: the purge rule needs NO change

This was the open worry, and it is settled by measurement rather than by reading docs:

| | `compact /c` (LZNT1) | `compact /c /exe:LZX` |
|---|---|---|
| `LastWriteTimeUtc` | **preserved** | **preserved** — 0 of 289 files moved |
| `CreationTimeUtc` | preserved | preserved |
| `LastAccessTimeUtc` | moved to now | moved to now |
| `FileAttributes.Compressed` | **set** | **NOT set** (WOF gotcha) |
| `GetCompressedFileSizeW` | reports compressed size | **reports compressed size** |

`LoggingService.PruneAgedLogs` / `PruneOrphanedLogs` key on `File.GetLastWriteTime` and glob
`{prefix}-*.log` / `*.log`. Compression changes neither the write time nor the file name, so
**retention keeps working with zero code change.** (The same lesson as the build-2726 snapshot
sweep: last-access is noise on Windows, last-write is the signal — and here compression leaves
the signal alone while moving only the noise.)

-----

## 2. The measurements

Corpus: the 289 files that pass the proposed eligibility rule (> 4 KB, idle ≥ 1 h, not
`-0.log`) = **102.05 MB** of the 111.9 MB total.

| method | on-disk after | ratio | wall clock | notes |
|---|---|---|---|---|
| **`compact /c /exe:LZX`** | **7.98 MB** | **12.8 : 1** | **2.8 s** | 8 `compact.exe` invocations, `PriorityClass = Idle` |
| `gzip` Optimal | 6.57 MB | 15.5 : 1 | 0.6 s | in-process, single thread |
| `gzip` SmallestSize | 6.26 MB | 16.3 : 1 | 1.1 s | in-process, single thread |
| `compact /c` (LZNT1) | — | 4.4 : 1 | — | measured on one 8 MB log; LZX did 25.1 : 1 on the same file |

**LZX saves 94.1 MB of 102.05 MB in 2.8 seconds.** gz would save a further **1.7 MB — 1.6 % of
the original corpus** — and cost every one of the workflows in §3.

LZNT1 is not competitive (4.4 : 1 vs 25.1 : 1 on the same file) and additionally requires a
cluster size ≤ 4 KB. LZX is the right algorithm here: it is Windows' "Compact OS"
**write-once/read-many** compression, and an archived log is exactly that.

### Where the bytes are

| bucket | files | MB |
|---|---|---|
| ≤ 4 KB | 347 | 0.4 |
| 4 KB – 1 MB | 323 | 27.3 |
| ≥ 1 MB | **28** | **84.1** |

The proposed `> 4 KB` floor is well aimed: it skips exactly half the files for 0.4 % of the
bytes. 75 % of the corpus is 28 files.

-----

## 3. Why NOT zip/gz — the 1.6 % is not worth what it breaks

A `.log.gz` is a different file, and everything that consumes a log knows it by name:

- **The purge globs stop matching.** `PruneAgedLogs` globs `{prefix}-*.log`;
  `PruneOrphanedLogs` globs `*.log`. Both would need a second pattern — and
  [log-verification-checklist.md](log-verification-checklist.md) exists precisely because log
  sweeps drift. A second glob is a second thing to forget.
- **Grep dies.** That checklist's core instruction is *grep by FORMAT STRING, never line
  number*. `Select-String` / `rg` / Notepad all read an LZX file transparently (verified: the
  first line of a compressed 8 MB `walk` log reads back byte-identical). None of them read a
  `.gz`.
- **"Open Log Folder" stops being an answer.** The System-tab button, and every
  "send me your log" request, currently ends with the user opening a text file.
- **We would own decompression forever** — for the DLL-written per-game mirror logs too, which
  the UI does not otherwise touch.

LZX costs the extra 1.7 MB and keeps every one of those intact. That is the whole argument.

-----

## 4. Traps found while measuring (these WILL bite an implementation)

1. **Quote every path.** Per-game log folders are named after the game EXE, and those contain
   spaces (`SEED BATTLE DESTINY REMASTERED`). The first unquoted benchmark run reported success
   while silently skipping 23 files — `compact` printed
   `D:\Github\UE5CEDumper\REMASTERED\: The system cannot find the file specified.` and carried
   on. **`compact` exits 1 on partial failure but still says "compressed".** Verify per file
   with `GetCompressedFileSizeW`, never trust the exit code.
2. **`FileAttributes.Compressed` is NOT set by LZX.** It is set by `/c` (LZNT1) only. Anything
   detecting "already compressed" by attribute will re-compress every file forever. Use
   `GetCompressedFileSizeW(path) < Length` (verified: 335,872 vs 8,441,784).
   Re-running over 40 already-LZX files costs **0.07 s**, so detection is for honest reporting,
   not for performance.
3. **Appending to an LZX file fully decompresses it.** Measured: 335,872 → 8,441,799 bytes on
   disk after one `Add-Content`. Harmless (no corruption) but it means `-0.log` must never be
   compressed, and re-opening an archive for append silently gives the space back.
4. **A file held open by a logger is safely skipped.** `compact` exits 1 with *"used by another
   process"*, compresses nothing, damages nothing. The eligibility rules are a courtesy; the
   filesystem is the real guard.
5. **Compress FILES, not the directory.** `compact /c <dir>` sets "compress new files here",
   which would put every future log write through the compressor. Only ever pass file paths.
6. **NTFS only.** `DriveInfo.DriveFormat == "NTFS"` is the pre-check (ReFS/exFAT/FAT32 support
   neither algorithm). LZX additionally needs Windows 10+.

-----

## 5. What shipped (builds 2730 / 2732)

**Two triggers, one engine, different age floors.**

| | trigger | age floor | default |
|---|---|---|---|
| **"Compress Logs"** button | System tab, next to "Open Log Folder" | idle ≥ **1 h** (`LogCompressMinIdleHours`) | — |
| **"Compress logs older than 7 days at startup"** | checkbox beside it, persisted in `ui-options.json` → `System.AutoCompressLogs` | **7 days** (`LogAutoCompressMinAgeDays`) | **OFF** |

The floors differ on purpose. The button is an explicit instruction, so "compress everything
nobody is writing to right now" is what was asked for. The automatic pass runs unasked, so it
only ever touches logs that are plainly historical — a log you might still be reading about
yesterday's session is not.

### 5a. `-0.log` is a slot name, not a liveness fact (build 2732)

The first cut excluded every `*-0.log` outright. Wrong, and the first real run showed why: a game
you last played 13 days ago still owns `SEED BATTLE DESTINY REMASTERED\walk-0.log` at 3.64 MB,
and nothing will ever append to it again until that game is injected once more. Measured cost on
the real folder: **36 files / 5.03 MB permanently uncompressed** — and the set only grows, one
final log per game ever tested.

Two facts settled it:

- **`LoggingService.PurgeOrphanedLogs` already sweeps game folders on age alone.** It passes a
  live-name set only for the UI's own folder; every game folder goes through
  `PruneOrphanedLogs(dir, maxAgeDays)` with none, taking the file lock as the real guard
  (*"Locked — a running game's DLL still owns it. Retry next startup."*). We were refusing to
  **compress** files the same subsystem is willing to **delete**.
- **Compressing them is durable.** `Sein` archives a `-0.log` by RENAME on the next injection,
  and a rename preserves LZX — verified: 335,872 bytes on disk before the rename and after.

The rule now:

| file | treatment |
|---|---|
| `-0.log` in `Logs\UE5DumpUI\` | **always skipped** — a Serilog sink holds it for the whole session. Identity, not age. |
| `-0.log` anywhere else | eligible once idle ≥ `LogCompressLiveFileMinAgeDays` (**7 days**), on BOTH triggers |
| everything else | the ordinary idle window (1 h manual / 7 d auto) |

A game that really is running keeps its log's mtime fresh, so the age floor covers it; the file
lock is the backstop if it somehow does not (and a locked file is safely skipped — trap 4).

**The automatic pass is opt-in and defaults OFF.** It is cheap and reversible (`compact /u`),
but a launch that rewrites the user's files without being asked is not a default to choose for
them; the button next to it does the same work on demand.

Code map:

- `Services/LogCompressionPolicy` — **pure**, zero `System.IO`: `IsLiveLog`, `Decide`, `Plan`,
  `Batch`. Same split as `AppDataRetentionPolicy` / `ProxyOrphanScanner`, so the rules that
  decide what gets REWRITTEN are testable without a disk.
- `Core/ILogCompressionService` + `Services/WindowsLogCompressionService` — the platform
  boundary (repo rule). Spawns `compact.exe /C /EXE:LZX /Q` at `ProcessPriorityClass.Idle`,
  batched by `LogCompressBatchSize` (40) **and** `LogCompressMaxArgChars` (24,000).
- `ProcessStartInfo.ArgumentList`, never a joined string — it applies Windows quoting itself,
  which removes trap 1 below by construction rather than by remembering to quote.
- Success is decided by **re-measuring `GetCompressedFileSizeW` per file**, never by parsing
  compact's stdout (trap 1).
- **No I/O-priority throttling**, deliberately. CPU-idle is enough at 2.8 s for a full 102 MB
  backlog, and this repo has already been burned by that reflex: multipipe "Phase 0" dropped the
  scan thread's priority, starved scans ~20×, and was reverted (build 1840,
  [multipipe-eval.md](multipipe-eval.md) §8).
- **No retention change at all** (§1), and the button/checkbox hide entirely when the log volume
  is not NTFS rather than offering something that can only report "unsupported".

Test coverage: 29 tests. The load-bearing one asserts `LastWriteTimeUtc` is unchanged after a
real compression pass over a real folder whose per-game subfolder name contains spaces — if that
ever fails, compression has started deleting logs early.

-----

## 6. How these numbers were produced

On the maintainer's machine, 2026-08-05, Windows 10.0.26200, `C:` = NTFS:

1. `Copy-Item %LOCALAPPDATA%\UE5CEDumper\Logs <scratchpad> -Recurse` — **the live folder was
   never modified.**
2. Stamp a known `LastWriteTimeUtc`/`CreationTimeUtc` on a copy of one 8 MB log, run
   `compact /c /exe:lzx`, re-read both.
3. Filter the copied tree by the eligibility rule, sum `GetCompressedFileSizeW` before/after,
   run `compact.exe` in batches of 40 quoted paths at `Idle`, re-sum, and diff every file's
   `LastWriteTimeUtc`.
4. gzip: `GZipStream` over the same file set at `Optimal` and `SmallestSize`, summing
   `MemoryStream.Length` (note: pass `leaveOpen: true` — closing the `GZipStream` disposes the
   `MemoryStream` and `.Length` then reads 0, which produced a divide-by-zero on the first
   attempt).
