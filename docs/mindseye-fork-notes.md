# MindsEye — licensee-fork notes + re-derivation playbook

> **Status:** ✅ **DONE — GObjects + GNames + GWorld all LIVE-VERIFIED end-to-end (builds 2220 / 2238).**
> Name sanity 10/10. Live Walker walks `GWorld → PersistentLevel → StormWP → EVMindsEyeGameInstance →
> LocalPlayers → LocalPlayer → BP_PlayerController_C` with correct values, classes and outer chains.
> **Game:** MindsEye (Build A Rocket Boy), `MindsEye-Win64-Shipping.exe`, `MindsEye/Binaries/Win64`,
> Steam, **UE 5.4.4 licensee fork**, `version.dll` proxy.
>
> ### ⚠ Verified on game version **7.3.1 ONLY**
>
> Every RVA, offset and byte pattern below was recovered from that one build. **Nothing here is
> guaranteed to survive a game update** — see [the fragile constants table](#the-fragile-constants-what-a-game-update-can-move)
> and the [re-derivation playbook](#re-derivation-playbook).
>
> The exe carries **no game-version resource** (`FileVersion` / `ProductVersion` are empty — the
> `UE 5.4` our scanner reports is the *engine* version), so "7.3.1" comes from Steam / the in-game
> build string and cannot be read back from the file. Pin the build by these instead, both of which
> ARE derivable from the binary:
>
> | Identifier | Value on the solved build |
> |---|---|
> | PE hash (TimeDateStamp + SizeOfImage) | `0863E3B90C993000` |
> | Build changelist dir in the `__FILE__` paths | `J:\work\e18f6e32b612e2cd\Engine\Source\...` |
> | File size / `.text` size | 206,803,536 bytes / ~145 MB |
>
> If the PE hash differs from `0863E3B90C993000`, treat every constant in this document as
> **unverified** until re-checked.

> **Purpose and scope.** These are interoperability notes for a debugging/inspection tool, written
> against a **legally purchased copy** and intended for **single-player / offline use only**. The
> goal is to locate Unreal Engine's own runtime structures — `GUObjectArray`, `FNamePool`, `GWorld` —
> in a build whose layout differs from stock UE, exactly as this project already does for ~33 other
> titles. Nothing here reproduces or redistributes any part of the game: no binaries, no assets, no
> keys, no reconstructed source. The recorded values are *facts about a binary's layout*, not its
> content. The game's pak/IoStore container encryption is deliberately **out of scope and untouched**
> — this tool only reads the memory of a process the user is already lawfully running.

**Read this first if MindsEye stops working after a game update.** Everything here was recovered
offline from the shipped binary with capstone + the PE `.pdata` table — **no Ghidra** (a 145 MB
`.text` costs hours to auto-analyse for information this recovers in minutes). The scripts are
reproducible; the constants are not.

---

## What this fork actually changes

Only three things differ from stock UE 5.4. Everything else — GWorld, SparseDelegates, FField /
FProperty / UStruct offsets, the `FNameEntryHeader` bit layout — is stock and needs no special
handling.

| # | Change | Where we handle it |
|---|--------|--------------------|
| 1 | `FChunkedFixedUObjectArray` fields reordered | `Genau.cpp` + `Aura.cpp` preset `"MindsEye-Extended"` |
| 2 | `FUObjectItem` grown to 32 bytes with `UObject*` at `+0x10` | `Aura.cpp` `LayoutPreset::itemHint`, **preset-bound** |
| 3 | `FNameEntry` gains a `u16` tag at `+0x02`; characters move to `+0x04` and are XOR-obfuscated per tag | `Genau::TryObfuscatedPool` + `Serie::InitObfuscated` |

The binary is **not** packed, has **no** Denuvo / EAC / BattlEye, and keeps its `__FILE__` anchors
(`J:\work\<changelist>\Engine\Source\Runtime\...`) in `.rdata`. The pak/IoStore container AES
(CUE4Parse `MindsEyeAes.cs`) is real but irrelevant — that is asset-load-time, not process memory.

---

## The fragile constants (what a game update can move)

RVAs are **build-specific**. Offsets within structures are far more stable — a licensee rarely
re-shuffles its own `FNameEntry` twice. Expect to re-derive the RVAs and to find the offsets
unchanged.

| Constant | Value on the solved build | Lives in | Symptom if stale |
|---|---|---|---|
| `GUObjectArray` RVA | `0x0BB139B0` | *(not hardcoded — found by AOB)* | — |
| Chunked field offsets (GUObjectArray-relative) | MaxElements `+0x10`, NumChunks `+0x14`, MaxChunks `+0x20`, NumElements `+0x24`, Objects `+0x28` | `Genau.cpp` strict preset table, `Aura.cpp` `s_ue4ExtendedPresets` | `GObjects=OK` on garbage, tiny `Count`, `named=0` |
| `FUObjectItem` stride / obj offset | `32` / `+0x10` | `Aura.cpp` `"MindsEye-Extended"` `itemHint` | `stride 16: good=N, bad=N` (a 50% alias), half the pool unreachable |
| `FNamePool` RVA | `0x0BA306C0` | *(not hardcoded — found by AOB)* | — |
| `FNameEntry` payload gap | `2` (chars at `+0x04`) | `Genau.cpp` `TryObfuscatedPool` `GAP` | GNames never accepted |
| De-obfuscator AOB | see `Sig::AOB_NAMEDECRYPT_ME1` | `Himmel.h` | `decrypt-routine pattern matched 0 time(s)` → pool refused |
| ctx-getter call offset in that match | `0x2F` | `Himmel.h` `AOB_NAMEDECRYPT_ME1_CTX_CALL_OFF` | key table resolves to junk |
| Key-table ctx RVA | `0x0BA47700` | *(not hardcoded — followed from the AOB)* | — |
| Key-table field offsets | entries `+0x10`, count `+0x18`, sentinel `+0x44`, inline buckets `+0x48`, bucket array `+0x50`, capacity `+0x58`; entry 24 B = `u16 tag` \| `+0x08 u64` (low byte = key) \| `+0x10 i32 next` | `Serie.cpp` `LookupTagKey` | every name empty, or wrong keys |

Only **two** things are hardcoded and can silently rot: the AOB pattern (+ its `0x2F`) and the two
offset tables. Everything else is derived at runtime.

---

## Re-derivation playbook

All three steps run **offline against the exe**, with the game closed. Needs `pip install capstone`.

### 0. Confirm the anchors survived

```
python find_anchors.py <exe>
```
Looks for `UnrealNames.cpp` / `UObjectArray.cpp` / `UObjectHash.cpp` / `UObjectGlobals.cpp` in
`.rdata`. If these are gone the fork has started stripping `__FILE__`, and steps 1–2 need a
different anchor (fall back to [reversing-nonstandard-ue-games.md](reversing-nonstandard-ue-games.md)).

### 1. GObjects layout — from `AllocateObjectPool`

Parse `.pdata` for exact function bounds, xref the `UObjectArray.cpp` string, disassemble the
containing functions. The init function stores the five fields in order:

```asm
lea  rcx, [rip + <"…UObjectArray.cpp">]
mov  [rip + MaxChunks],   ecx
mov  [rip + MaxElements], eax
call <malloc>
mov  [rip + Objects],     rax      ; the chunk table
mov  [rip + NumChunks],   eax
lea  rcx, [rip + ObjObjects]       ; passed as `this` to another UObjectArray.cpp function
```

`GUObjectArray = ObjObjects − 0x10`. Cross-check against `vendor/Dumper-7`'s
`FChunkedFixedUObjectArrayLayout // MindsEye` row — it matched exactly on the solved build.

### 2. `FUObjectItem` geometry — from the index→object accessors

Find functions referencing **both** `Objects` and `NumElements`; the small ones are the accessors:

```asm
mov   ecx, ebx                    ; Index
shr   rcx, 0x10                   ; ChunkIndex   = Index >> 16   (65536 items/chunk, stock)
movzx edx, bx                     ; WithinChunk  = Index & 0xFFFF
shl   rdx, 5                      ; × 0x20       <- FUObjectItem stride
add   rdx, [r9 + rcx*8]           ; + Objects[ChunkIndex]
cmp   qword ptr [rdx + 0x10], 0   ; <- UObject* offset within the item
```

The `shl` immediate is the stride; the `cmp` displacement is the object offset. Four independent
accessors agreed on the solved build.

> **Do not put a recovered stride into `Aura`'s shared `candidates[]` sweep.** 32/`+0x10` aliases
> perfectly with stride 16 — every odd 16-byte slot lands on a real object pointer — so it would
> outscore the true stride on genuine stride-16 titles (Titan Quest II, Octopath Traveler). It must
> stay bound to the preset via `itemHint`.

### 3. Name de-obfuscation — from the de-obfuscator

`Sig::AOB_NAMEDECRYPT_ME1` matches the routine's entry. If the pattern stops matching, find it
again by disassembling any function that reads a `FNamePool` block and looks like:

```asm
movzx r8d, word ptr [rcx]     ; stock 2-byte header
lea   rdx, [rcx + 4]          ; chars at entry+4   <- the fork's tell (stock is +2)
shr   r8, 6                   ; len = header >> 6
call  <memcpy>
call  <ctx getter>            ; <- follow this; its first rip-relative LEA is the key table
movzx edx, word ptr [rbx + 2] ; the u16 tag
...
xor   byte ptr [rax], dl      ; single-byte XOR (SSE xorps fast path for len >= 0x40)
```

Then re-cut the AOB from the function's first ~56 bytes, wildcarding the two `E8` rel32
displacements, and update `AOB_NAMEDECRYPT_ME1_CTX_CALL_OFF` to the byte offset of the **second**
`E8` within the match.

To confirm the table layout, disassemble the `KeyDerive` callee — the bucket mask, the
`lea rcx,[rax+rax*2] / lea rcx,[rcx*8]` (⇒ 24-byte entries), the tag compare, and the `next` field
displacement are all visible.

### Sanity check without re-deriving anything

Dump the first 96 bytes of `Blocks[0]` (the DLL already logs this when GNames validation fails) and
XOR with a candidate key. Block 0's first entries are the canonical hardcoded EName list, so the
correct key makes them read `None`, `ByteProperty`, `IntProperty`, `BoolProperty`, `FloatProperty`,
`ObjectProperty`, with every length matching `header >> 6`.

---

## Why we read the key table instead of calling the game's routine

Calling `CopyAndDecryptName` directly was designed, adversarially reviewed, and **dropped**:

* `KeyDerive` takes `RtlAcquireSRWLockShared` **before** probing. A fault inside it would unwind out
  of game frames with the lock still held and permanently wedge every later `FName::ToString` — no
  crash, no log, and `Tot` is a cooperative poll with no poll point inside game code. That breaks
  *the game*, not us, and SEH cannot save it.
* The ctx getter reads `gs:[0x58]` with a lazy per-thread init branch, so calling it from a thread
  the game never used is a second, independent hazard.

Reading the table has neither failure mode. Keep it that way.

---

## What is NOT recoverable — by design, not by defect

MindsEye ran a symbol-rename pass over its **own non-engine symbols** at build time. Game-specific
property and class names are generated 16-character all-lowercase identifiers
(`wcxugjojsqaqvers`, `eurngjogndgrjhls`, `rtmtzjmekhqtdxdg`, …).

This is proven, not inferred: those strings appear **verbatim in the exe's `.rdata`**, and the binary
holds **21,635** distinct 16-char all-lowercase tokens. Entry length comes from the stock header and
is key-independent, so a wrong key could never manufacture them. Engine symbols (`LocalPlayers`,
`NetTimeSyncComponent`, `AnalyticsComponent`, …) are untouched and read normally.

The originals exist nowhere — not in memory, not in the binary — so **no tool can recover them**
(Dumper-7 and RE-UE4SS included). Property Search / Value Search on the *values* still work; only
the human-readable name is gone. Do not spend time treating this as a decryption bug.

---

## Gotchas learned the hard way

1. **`GObjects=OK` can be a lie.** The relaxed Tier-2 validator accepted an ICU-like locale blob
   containing the ASCII text `"International"` because its `numOff` landed on the high half of an
   adjacent module pointer — `Num=32758` is literally `0x7FF6`. Any `Num` of 32758/32759 is a
   pointer's high half, never an object count.
2. **The AOB was never the problem.** For both GObjects and GNames the pattern hit the *correct*
   address and the validator rejected it. Check `ValidateGObjects: Failed at …` / `ValidateGNames:
   base=…` lines before concluding "no pattern matches".
3. **An empty `name` in a GNames log is not evidence.** Before build 2220 `ValidateGNames` memset the
   buffer *before* logging it, so every historical log showed `name=''` regardless of the game.
4. **The key is per TAG, not per block.** Blocks appeared to have one key each only because the tag
   is constant across a block's first entries.
5. **Publish a cache value and its validity flag as ONE atomic word.** Two plain stores let the
   compiler publish the flag first; another thread then read "resolved" with a still-zero key and
   XORed with 0 — `Object` rendered as `Fkclj}`, the same tag resolving to `0x09` on one thread and
   `0x00` on another in the same millisecond.
6. **`GWorld does not deref to a UWorld` at scan time is usually harmless.** `GWorld` is a `UWorld**`
   static slot; `*GWorld` is null while the game is still loading. Re-scan once in-game before
   treating it as a defect.

---

## Related

* [dev-log.md](dev-log.md) — the two shipping entries (builds 2220, 2238) with full evidence.
* [test-games.md](test-games.md) — the MindsEye row.
* [reversing-nonstandard-ue-games.md](reversing-nonstandard-ue-games.md) — the general playbook for
  forked engines; this document is a worked instance of it that needed no Ghidra.
* [avowed-gobjects-fix.md](avowed-gobjects-fix.md) — the closest prior analogue (packed 20-byte
  `FUObjectItem`).
