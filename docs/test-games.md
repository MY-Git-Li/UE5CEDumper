# Recommended Test Games

> Moved from CLAUDE.md. A curated list of games used to validate UE5CEDumper across UE versions.

-----

| Game | UE Version | Notes |
|------|-----------|-------|
| EverSpace 2 | UE5.5 (PE: 505) | GNames via pointer-scan fallback. Stride 24, 1.16M objects. GWorld ✅ (build 1.0.0.27) |
| Titan Quest II | UE5.7 (PE: 507) | CasePreservingName + DynOff. Stride 16. 486,782 objects. GWorld ✅ via fallback ([GWorld]=0, UWorld found in GObjects) |
| OctoPath Traveler | UE4.22 (inferred) | 406060 objects. GNames via GNAM_CT3 ✅. GObjects via GOBJ_RE2 ✅ (Flat FFixedUObjectArray, validated by "Flat" preset). GWorld via GWLD_TQ_1 ✅. Codename "Kingship". Ghidra: GObjects RVA `0x29E5C20`, GNames RVA `0x29DCF08` (TNameEntryArray stride 0x4000). GOBJ_OT_1/OT_2 also added but untested (lower priority than RE2) |
| Final Fantasy VII Rebirth (FF7Re) | UE4.26 fork | Hash-prefixed FNameEntry (hdrOff=4, stride=4) + stride 24 — fully working. GWorld ✅ |
| Final Fantasy VII Remake Intergrade (FF7R) | UE4.18 fork | Flat FFixedUObjectArray ✅. UProperty fallback ✅. 315304 objects. GWorld ✅. Version: flat+UProperty → 418. Base GameInstance (9 fields, no BP subclass) |
| DQ I&II HD-2D Remake | UE5.05 (detected) | Stride 24, 128678 objects. GWorld ✅. SE HD-2D fork uses FFieldVariant=0x10 (UE5.0 layout) despite reporting UE505 — fixed by Step 6.5 inference (Name=0x28 from Next=0x20). BP_SantiagoGameInstance_C (64 fields) |
| DQ III HD-2D Remake | UE5.05 (detected) | 126022 objects. GWorld ✅. Same SE HD-2D fork layout as DQ I&II — FFieldVariant=0x10 inference fix applied |
| DQ XI S: Echoes of an Elusive Age | UE4.22 | 350251 objects. GWorld ✅. UField::Next=+0x38 (non-standard, probed). UProperty mode. Expanded UObject layout (+0x10 shift). BP_GameInstance_C (99 fields), BP_GameFlag_C (10 fields) |
| Tower of Mask | UE4.27 | Standard UE4 indie game — full pipeline confirmed working. Stride 24. GWorld ✅ (build 1.0.0.27) |
| Hogwarts Legacy | UE4.27 (PE: 427) | GNames via pointer-scan fallback. Stride 24, 379K objects. GWorld ✅ (build 1.0.0.27) |
| IDOLM@STER STARLIT SEASON | UE4.24 | Working. GWorld ✅ (build 1.0.0.27). CDO skip fix effective |
| Romancing SaGa 2 | UE4.27 | Working (build 1.0.0.27). GWorld ✅ |
| Star Wars Jedi: Fallen Order | UE4.21 | Working — 313 887 objects, **GWorld ✅** (`0x7FF7317EBAB8`) re-verified build 704 (2026-05-12) via CE injection. EA-launcher title: `version.dll` / `dinput8.dll` proxies do NOT load (EA app restricts the DLL search path); must inject via CE after the game is running. Install at `SwGame\Binaries\Win64\` holds TWO 58.4 MB exes side-by-side (`SwGame-Win64-Shipping.exe` + `starwarsjedifallenorder.exe`); CE sees the launcher exe as the running process. |
| Ghostwire: Tokyo | UE505 detected (possibly UE4) | Working (build 1.0.0.27). 254493 objects. GWorld ✅. UE version likely incorrect. RE-UE4SS has only AOB signatures, no version override |
| Lushfoil Photography Sim | UE5.6 (PE: 506) | NEW (build 1.0.0.40). All working. 58630 objects |
| Manor Lords | UE5.5 | NEW (build 1.0.0.40). All working |
| Satisfactory | UE5.3 (PE: 503) | Working — modular UE build with separate `FactoryGameSteam-CoreUObject-Win64-Shipping.dll` under `Engine\Binaries\Win64\`. `Macht::AOBScanAllModules` falls through to the CoreUObject DLL; the 15-game dump corpus (build 678 + 687) contains its 4 868 BPGCs cleanly. **GWorld ✅** re-verified build 704 (2026-05-12); the original "GWorld fails" note was stale (pre-`AOBScanAllModules`). Proxy deploy fixed build 691 — UI now finds the launcher at `Engine\Binaries\Win64\FactoryGameSteam-Win64-Shipping.exe` (real .exe is in `Engine\`, not `FactoryGame\`). |
| Cat Island Petrichor Demo | UE5.6 | Full working. GWorld ✅ |
| Way of the Hunter 2 Demo | UE5.7 | Full working. GWorld ✅ |
| COMBAT PILOT: CARRIER QUALIFICATION Demo | UE5.5 | Full working. GWorld ✅ |
| The Artisan of Glimmith | UE4.27 (PE: 427, exe: Geri-Win64-Shipping) | Full working. Stride 24, 24K objects. GWorld ✅. **Build 648 cross-version PE-vtable verification target (2026-05-11)**: actual PE slot is `vtable+0x220` (old hardcoded `0x218` for UE 4.25-4.27 was off by 1 slot — enough to silently break invokes). Validator confirms 1260 hook fires / 1500ms. Four scenarios verified: Add_IntInt=7, Multiply_FloatFloat=12, `CharacterMovementComponent::GetMaxJumpHeight`=89.99 (instance method via game-thread dispatch), `PlayerCameraManager::GetCameraLocation` returns FVector struct. |
| Barn Finders | UE4.25 (PE: 425, exe: BarnFinders-Win64-Shipping) | User-submitted logs (build 560). Stride 24, 136,953 objects. UE5-Extended layout (strict). GWorld ✅. Standard FProperty mode. Publisher fallback (Atomic Jelly / Plug-In Digital — no thumbprint match). |
| Colossal | UE5.03 (PE: 503, exe: Colossal-Win64-Shipping, publisher: Atan) | User-submitted logs (build 560). Stride 24, 41,528 objects. UE5-Extended layout (strict). GWorld ✅. TaggedFFieldVariant (UE5.3+). FField::Next=+0x18, FField::Name=+0x20. Publisher Copyright is Epic default placeholder — no thumbprint match. |
| Extinction | UE4.15 (PE: 415, exe: Extinction.exe, dir: Blink/Binaries/Win64) | User-submitted logs (build 560). Stride 24, 230,732 objects. **Flat (non-chunked)** FFixedUObjectArray. GNames via TNameEntryArray (UE4Names=yes). UProperty mode (UE < 4.25). UField::Next=+0x28. **Lowest UE version verified end-to-end** — expands support below the previously documented 4.18+ floor. Patterns: GOBJ_RE2 (1.8s, 2 batches) / GNAM_CT3 (4.6s, 4 batches) / GWLD_G42_1 (3.3s, 3 batches). Publisher: Modus Games (no thumbprint). |

-----

## GWorld Status Summary

**Working (22/24):** TQ2, EverSpace 2, Hogwarts Legacy, IDOLM@STER, Romancing SaGa 2, Tower of Mask, Ghostwire: Tokyo, Cat Island Petrichor Demo, Way of the Hunter 2 Demo, COMBAT PILOT Demo, OctoPath Traveler, FF7R, FF7Re, DQ I&II, DQ III, DQ XI S, Lushfoil Photography Sim, Manor Lords, The Artisan of Glimmith, Barn Finders, Colossal, Extinction

**Failing (GWorld not found or untested):** Star Wars Jedi, Satisfactory

## Naming Convention

- **FF7Re** = Final Fantasy VII Rebirth (UE4.26 Square Enix fork)
- **FF7R** = Final Fantasy VII Remake Intergrade (UE4.18 Square Enix fork)
- **Geri** = The Artisan of Glimmith (UE4.27, executable `Geri-Win64-Shipping.exe` — log folder uses this name)
- **Blink** = Extinction (UE4.15, executable `Extinction.exe`, parent dir `Blink/Binaries/Win64/`)
