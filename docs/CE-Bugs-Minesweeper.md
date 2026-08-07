# CE Bugs — Minesweeper

CE-specific quirks and undocumented behaviours hit while building against Cheat Engine.

> **Version coordinates (re-verified 2026-08-07 — every line number below was re-grepped, not trusted)**
>
> | What | Value |
> |------|-------|
> | CE **source tree** read | `D:\Github\cheat-engine`, tag **`7.5-195`**, HEAD `4178e037` |
> | Last **public** upstream commit | `upstream/master` `ec45d5f4` (2025-04-19) — the tree is level with it |
> | CE **binaries** installed | `C:\Program Files\Cheat Engine\`, **7.7.0.10568** (ProductVersion 7.7) |
> | Plugin SDK version | `CESDK_VERSION 6` (`cepluginsdk.h:16`) — unchanged in 7.7 |
>
> Two things this split means, and they are not the same claim:
> - **The public source is 7.5-era; the shipping binary is 7.7.** CE's GitHub repo lags its
>   releases, so "present in the source" does **not** prove "present in 7.7", and "absent from the
>   source" does not prove it was fixed. Each item below says which one it was checked against.
> - HEAD is **1 commit ahead of upstream** — the local `Assemblerunit.pas` fix in §4. That fix is
>   **not** in upstream and therefore not in any official build.
>
> ⚠ Line numbers here are DERIVED from an external tree and `tools/check_derived_counts.py`
> cannot guard them (it only derives from this repo). Re-grep the identifier before trusting one.
> Deeper background on the plugin SDK: [ce-plugin-api-reference.md](ce-plugin-api-reference.md)
> and [ce-plugin-sdk-notes.md](ce-plugin-sdk-notes.md).

---

## 1. [Bug Report] CE Plugin SDK — Type 7 specialstringpointer is ignored

**Checked against:** source `7.5-195` — **all five line numbers below still land exactly.**
**Originally observed on:** CE 7.6 public.

Dev environment: making a Native C++ plugin using **Plugin SDK v6, Type 7 (Disassembler Line Renderer)**.

### The specialstringpointer is broken
Even change the `specialstringpointer` in Type 7 callback, CE never render comments in the Comment column.

**Root Cause:**
In `disassemblerviewlinesunit.pas`, the logic order is wrong:
1. Line 451: CE builds `specialstring` (Delphi string) with own comments.
2. **Line 496: CE copies `specialstring` to `specialstrings` (TStringList).** <-- PROBLEM!
3. Line 946–952: CE gives `pspecialstring` pointer to our Type 7 plugin.
4. Line 995–1001: CE draws the Comment column from the **TStringList** (already copied in Step 2).

So, when plugin modifies the string in Step 3, CE already finished the copy in Step 2. It just draws the old data. Other strings like `opcodestring` or `addressString` are OK because they are drawn directly from pointers.

> **Re-verified 2026-08-07 against `7.5-195`** — every step still holds at the stated line:
> `:451 specialstring:=d.DecodeLastParametersToString;` → `:496 specialstrings.text:=specialstring;`
> → `:946 pspecialstring:=@specialstring[1];` → `:952 handledisassemblerplugins(...)` (which forwards
> to the plugin handler via `:1266`/`:1270`) → `:995-1001` draws the Comment column from
> `specialstrings[i]`. The contrast is visible in the same procedure: the other three columns go
> through `DrawTextRectWithColor(..., paddressString / pbytestring / popcodestring)` at `:956`,
> `:960` and `:964` — straight from the pointers the plugin was handed, which is exactly why only
> the Comment column is dead. `specialstrings` is also what sizes the row (`:526-533`), so the
> height is fixed before the plugin runs too.

**Code Reference:**
```delphi
// disassemblerviewlinesunit.pas

// Step 2: TStringList is filled BEFORE plugins run
specialstrings.text := specialstring;  // line 496

// Step 3: Call plugin (Too late! Already copied)
pspecialstring := @specialstring[1];   // line 946
handledisassemblerplugins(..., @pspecialstring, ...); 

// Step 4: CE draws from TStringList, NOT from our modified pointer
for i := 0 to specialstrings.Count-1 do
  fcanvas.TextRect(..., specialstrings[i]); 
```

**Suggested Fix:**
Move the `specialstrings.text` assignment to after the plugin callback:

handledisassemblerplugins(@paddressString, @pbytestring, @popcodestring, @pspecialstring, @textcolor);

// Re-read specialstring from modified pointer
if pspecialstring <> nil then
  specialstrings.text := pspecialstring
else
  specialstrings.text := '';

---

## 2. `{` Character in DrawTextRectWithColor

**Checked against:** source `7.5-195`. **Corrected:** the brace handling is not at 1075.

`DrawTextRectWithColor` treats `{` as a colour-control escape. If plugin output contains a `{` in
opcode/address/bytes, the rendering becomes very messy with random colour blocks. This is not in
the SDK doc.

`:1075` is only where the function is *declared* — the `'{':` case is at **`:1177`**, and the
control letters it accepts are wider than the three originally listed:

| After `{` | Effect | Line |
|-----------|--------|------|
| `N` | reset both foreground and background to normal | `:1200-1204` |
| `H` | hex colour (`colorcode:=1`) | `:1205` |
| `R` | register colour (`colorcode:=2`) | `:1206` |
| `S` | symbol colour (`colorcode:=3`) | `:1207` |
| `B` + `RRGGBB` | **background** RGB — consumes 6 chars | `:1208-1213` |
| `C` + `RRGGBB` | **foreground** RGB — consumes 6 chars | `:1214-1219` |
| `}` | end of the escape | `:1220-1224` |

Two details that make this worse than "one reserved character", both visible in that block:

- **An unknown letter is swallowed, not rejected.** The `else raise exception.create(rsInvalidDisassembly)`
  is **commented out** at `:1226`, so the parser simply keeps consuming until it finds a `}`.
  A stray `{` therefore eats the rest of your string looking for a terminator.
- **`B` and `C` blindly consume 6 characters** (`inc(i,6)` at `:1212`/`:1218`) whether or not they
  are valid hex — `trystrtoint` failing only means the colour is not applied, not that the six
  characters come back.

**Workaround:** append Type 7 output to `opcodestringpointer` and do not emit `{`. If you cannot
guarantee that, escaping is not available — strip or replace the character.

---

## 3. Value-type numbering: the SDK header comment is wrong (and 7.7 fixed it)

**Checked against:** source `7.5-195` **and** the installed 7.7 header — the two differ, which is
the whole point of this entry.
**Originally observed on:** CE 7.6 public.

> **Reframed 2026-08-07 — the original entry named an artifact that does not exist.** It said the
> "SDK Header order" was wrong, which reads as though `cepluginsdk.h` declares a value-type enum.
> **It does not.** `grep -n "enum" cepluginsdk.h` returns exactly two: `PluginType` (`:18`) and
> `AutoAssemblerPhase` (`:19`). The wrong numbering was a **`//` comment** on one struct field,
> and CE has since corrected it.

CE's internal `TVariableType` (`commontypedefs.pas:15`) is the real numbering. The SDK header never
declares it in C at all — a plugin author has to write that enum themselves, which is exactly how
the wrong numbers got copied around.

The artifact that carried the bad numbering is `PLUGINTYPE0_RECORD.valuetype`, and it changed
between the version this repo reads and the version installed here:

```diff
  // cepluginsdk.h:35
- // 7.5 source tree  — WRONG from index 3 onward
- char valuetype; //0=byte, 1=word, 2=dword, 3=float, 4=double, 5=bit, 6=int64, 7=string
+ // 7.7 installed   — corrected, and now agrees with TVariableType for 0-9
+ char valuetype; //0=byte, 1=word, 2=dword, 3=int64, 4=float, 5=double, 6=string, 7=widestring, 8=bytearray, 9=binary
```

That one line is the **only** difference between the two headers; `cepluginsdk.pas` is
byte-identical, so the plugin C ABI itself is unchanged between 7.5 and 7.7.

**The authoritative list** (`commontypedefs.pas:15`) has **17** members, not the 8 the old comment
implied:

```
vtByte=0, vtWord=1, vtDword=2, vtQword=3, vtSingle=4, vtDouble=5, vtString=6,
vtUnicodeString=7, vtByteArray=8, vtBinary=9, vtAll=10, vtAutoAssembler=11,
vtPointer=12, vtCustom=13, vtGrouped=14, vtByteArrays=15, vtCodePageString=16
```

**Why it bites.** `MainUnit.pas:9743` fills `valuetype` straight from the record's `VarType`, and
`:9767-9768` writes whatever you leave there **back into the cheat-table entry** when your Type 0
callback returns TRUE. Writing `3` meaning "float" silently retypes the row as an 8-byte **Qword**;
writing `5` meaning "bit" gives **Double**.

**Workaround:** ignore the header comment in either version and use `TVariableType`. In CE Lua the
same ordering applies — `mr.Type == 11` is an AA-script row, and 7.7's shipped `defines.lua`
confirms `vtQword=3` / `vtAutoAssembler=11` independently.

See [ce-plugin-sdk-notes.md](ce-plugin-sdk-notes.md) §10 and
[ce-plugin-api-reference.md](ce-plugin-api-reference.md) §11 for the full treatment.

---

## 4. r/m16, imm8 sign-extension bug for value 0x80–0xFF

> **Status (2026-08-07): still present upstream; fixed only in this fork.**
> `upstream/master` (`ec45d5f4`, 2025-04-19) still reads `if vtype=16 then` at
> `Assemblerunit.pas:6845`. The fix below is local commit `4178e037`, which is the single commit
> `D:\Github\cheat-engine` is ahead by — it has **not** been upstreamed, so no official CE build
> contains it.
> **Not verified against the 7.7 binary.** The public source lags the release, so the source
> alone cannot settle whether 7.7.0.10568 still mis-encodes this. A 10-second check settles it:
> in CE, open **Memory View → Tools → Auto Assemble**, type `cmp bx, AA`, and look at the bytes.
> `66 83 FB AA` = still buggy; `66 81 FB AA 00` = fixed upstream since.

### Problem
When we use instructions like `cmp bx, AA`, the assembler is wrong. It use `r/m16, imm8` (opcode `83`) encoding. The CPU will do sign-extend for `0xAA` and it become `0xFFAA` (-86), but user actually want `0x00AA` (170).

#### Reproduction
```asm
cmp bx, AA    ; user want: compare BX with 170 (0x00AA)
```

#### Actual (Wrong)

```
66 83 FB AA        cmp bx, FFAA    ; sign-extended 0xAA -> 0xFFAA = -86
```

#### Expected (Correct)

```
66 81 FB AA 00     cmp bx, 00AA    ; use 16-bit immediate = 170
```

Now, if we write `cmp bx, 00AA` (add zero in front), it is OK because `StringValueToType` will see length and use `vtype=16`. But we think just `AA` should also work correctly.

---

### Root Cause

In `Assemblerunit.pas` line 6845, the `r/m16, par_imm8` handler only check `vtype` to decide upgrade to `imm16` or not:

```pascal
if vtype=16 then    // <- only check string length type
```

For the value `AA`:
* `ConvertHexStrToRealStr("AA")` -> `"$AA"`
* `StringValueToType("$AA")` -> length is 3 -> **vtype=8**
* `SignedValueToType(170)` -> 170 > 127 -> **signedvtype=16**

Because `vtype=8` (not 16), the assembler skip the upgrade. Then it send `byte(0xAA)` as `imm8`, so CPU do sign-extend to `0xFFAA`.

I check 32-bit code (`r/m32, par_imm8` at line 6982), it is already correct:

```pascal
if (vtype>8) or (opcodes[j].signed and (signedvtype>8)) then    // <- this is correct
```

So 16-bit path just forgot to check the `signed` flag.

---

### Fix

**Change line 6845**. Just add the `signed` and `signedvtype` check like 32-bit path:

```pascal
// Old code:
if vtype=16 then

// New code:
if (vtype=16) or (opcodes[j].signed and (signedvtype>8)) then
```

This means we will upgrade `imm8` to `imm16` when:

1. User write 16-bit string (like `00AA`).
2. **OR** Opcode has `signed: true` AND the value is bigger than signed-byte range (> 127 or < -128).

---

### Affected Instructions

All `r/m16, imm8` with `signed: true` have this problem (the ALU group):

| Mnemonic | Opcode Line | Encoding |
| --- | --- | --- |
| ADD | 182 | 66 83 /0 |
| ADC | 166 | 66 83 /2 |
| AND | 213 | 66 83 /4 |
| CMP | 351 | 66 83 /7 |
| OR | 1027 | 66 83 /1 |
| SBB | 1576 | 66 83 /3 |
| SUB | 1703 | 66 83 /5 |
| XOR | 2658 | 66 83 /6 |

These all have same bug: if immediate value is 0x80–0xFF, it will become 0xFF80–0xFFFF in 16-bit register.

---

### Verification

I tested these cases, now they are all correct:

| Input | Before (Wrong) | After (Correct) |
| --- | --- | --- |
| `cmp bx, AA` | `66 83 FB AA` (FFAA) | `66 81 FB AA 00` (00AA) |
| `cmp bx, 80` | `66 83 FB 80` (FF80) | `66 81 FB 80 00` (0080) |
| `cmp bx, FF` | `66 83 FB FF` (FFFF) | `66 81 FB FF 00` (00FF) |
| `cmp bx, 7F` | `66 83 FB 7F` (007F) | `66 83 FB 7F` (No change, safe) |
| `cmp bx, 05` | `66 83 FB 05` (0005) | `66 83 FB 05` (No change, safe) |
| `add bx, C0` | `66 83 C3 C0` (FFC0) | `66 81 C3 C0 00` (00C0) |

---

## 5. Embedded table Lua file stays cached after re-embed (no reload until CE restart)
**Tested CE Version:** 7.6, 7.7

When a table Lua file (e.g. `ue5_invoke_helper.lua`, added via **Table → Add File…**) has already been `load()`-ed in the current session, **swapping it for an updated copy does NOT take effect** — even if you remove the old file and re-add the new one. CE keeps the previously-loaded globals in the main Lua engine for the rest of the session.

> **Mechanism, corrected 2026-08-07 (source `7.5-195`): nothing caches the FILE.** The title says
> "cached" and that is the wrong mental model, which matters because it sends you looking for a
> cache to invalidate. `findTableFile` (`LuaTableFile.pas:115`, registered at `:189`) really does
> hand back the new blob and `load()` really does compile it. What persists is the **Lua state**:
> CE has exactly one, and it never rebuilds it when a table file is added, removed or the table is
> closed — the only thing that makes a new state is the Lua-callable `resetLuaState`
> (`LuaHandler.pas:5108`, whose own comment reads *"this creates a NEW lua state (cut doesn't
> destroy the current one)"*). So the globals from the first `load()` simply stay, and point 1
> below is what stops the fresh source from overwriting them. **The file is not cached; your
> globals are still alive and your own guard is refusing to replace them.**

Two things compound this:
1. **Helpers use a re-declaration guard.** Our `ue5_invoke_helper.lua` wraps its functions in `if not setDebugCamera then … end` so multiple AA Scripts loading the helper don't redefine it. Once the function exists as a global, a later `load()` of the file's source runs the guard, sees the global, and **skips the redefinition** — so the stale function persists.
2. **`findTableFile` returns the embedded blob**, and `load()` compiles fresh source, but (1) means the fresh source's definitions are never installed over the already-present globals.

**Symptom we hit:** after fixing `setDebugCamera` (executeCodeEx → mailbox) and re-exporting + re-embedding the helper, the generated record still ran the OLD function and returned `state=nil`. Deleting and re-adding the file did nothing.

**Workarounds:**
- **`resetLuaState()` from the Lua console** — cheapest fix, and it was missed originally. It is a
  registered CE Lua function (`LuaHandler.pas:16613`; documented in 7.7's `celua.txt:138`) that
  installs a brand-new Lua state, so every stale global disappears and the next `load()` takes.
  **Caveat, and CE says it out loud:** it does not destroy the old state — `celua.txt` calls it a
  memory leak. Fine for an iteration loop, not something to wire into a script.
- **Fully restart Cheat Engine** (closing just the table or the Lua engine window is not always enough — a full CE restart reliably clears the cached globals).
- **Or** make the generated script self-contained so it doesn't depend on the embedded helper at all (what we did for "Copy CE Script": inline the mailbox round-trip, no `findTableFile`). This sidesteps the cache entirely.
- A helper could also force-reload by clearing its own globals before redefining (e.g. drop the `if not …` guard, or set the functions to `nil` first), but that defeats the multi-load guard, so the self-contained route is preferred.
