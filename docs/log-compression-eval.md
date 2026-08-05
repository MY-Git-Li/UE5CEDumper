# Log compression — EVALUATED (2026-08-05). Verdict: **`compact /c /exe:LZX`, not zip/gz.**

> **Status: evaluated + measured, NOT built.** Every number below is from a real run on the
> maintainer's own `%LOCALAPPDATA%\UE5CEDumper\Logs` (698 files / 111.9 MB), on a copy in the
> scratchpad — the live log folder was not modified. Re-measure before trusting these on a
> different machine; the method is at the bottom.

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

## 5. Recommended design (if built)

**Manual only, System tab, next to "Open Log Folder".** The point of the feature is to not
touch the disk while a game runs — so the user picks the moment. No startup hook.

- **Eligibility (pure, testable, zero `System.IO`)** — same split as `AppDataRetentionPolicy` /
  `ProxyOrphanScanner`: given `(name, length, lastWriteUtc, onDiskBytes)` decide compress /
  skip-too-small / skip-too-fresh / skip-already-done / skip-live. Rules: `Length > 4096`,
  `now - LastWriteUtc >= 1h`, name is not `*-0.log`, `onDiskBytes >= Length`.
- **Execution** — batch ~40 **quoted** paths per `compact.exe /C /EXE:LZX /Q` invocation, on a
  background thread, `Process.PriorityClass = Idle`, cancellable between batches. Report
  `files, before → after, saved` from `GetCompressedFileSizeW`, not from compact's stdout.
- **Do NOT add I/O-priority throttling.** CPU-idle is enough at 2.8 s for a full 102 MB
  backlog, and this repo has already been burned by exactly that reflex: multipipe "Phase 0"
  dropped the scan thread's priority, starved scans ~20×, and was reverted (build 1840,
  [multipipe-eval.md](multipipe-eval.md) §8).
- **Platform boundary** — the `compact.exe` spawn and the `GetCompressedFileSizeW` P/Invoke go
  behind an interface in `Core` (repo rule); the eligibility policy stays pure.
- **No retention change at all.** §1.

Effort: **S–M** · Risk: **low** (worst case is a file that fails to compress).

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
