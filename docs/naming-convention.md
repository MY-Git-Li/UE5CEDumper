# Frieren Naming Convention — UE5CEDumper

All C++ DLL module/namespace/file names in UE5CEDumper use character names from
the anime **"Frieren: Beyond Journey's End"** (葬送的芙莉蓮).

**Rule**: Every Frieren-named entity MUST have a comment explaining its actual function.

> In-use assignments were informed by the **3rd Official Character Popularity Poll**
> (2026-03-29, 12.7M votes). The pool of names available for *new* modules is the full
> **[Wikipedia character roster](#full-character-roster--name-pool)** below.
> See [Sources](#sources) at the end.

---

## Design Principle

**Character personality / ability / story role ↔ Module functional nature.**

Not a mechanical 1:1 port — each mapping is chosen because the character's
narrative identity resonates with what the module *does*.

---

## DLL Module Naming

| File | Frieren Name | Character | Poll # | Actual Function | Why This Name |
|---|---|---|---|---|---|
| **Frieren.cpp** | 芙莉蓮 | Protagonist | #4 (1v1: #1) | ExportAPI: ~30 C ABI exports for CE Lua | Everyone meets her first — the sole gateway to the DLL |
| **Genau.cpp** | 葛納烏 | First-class mage examiner | **#1** | OffsetFinder: AOB signatures, GObjects/GNames/GWorld | The examiner who *screens* candidates — scans & validates every pattern |
| **Macht.cpp** | 黃金鄉馬哈特 | Seven Sages, transmutation | #5 | Memory: AOBScan, SEH reads, RIP resolution, AVX2 SIMD | Raw elemental power — direct memory manipulation |
| **Aura.cpp** | 斷頭台的阿烏拉 | Obedience Scale demon | #3 | ObjectArray: FUObjectArray slot enumeration | Weighs every soul on her scale — validates each object slot |
| **Serie.cpp** | 賽莉耶 | Living-history great mage | #6 | FNamePool: FName string resolution (UE5 pool + UE4 TNameEntry) | Remembers every mage's name across millennia — the name oracle |
| **Ubel.cpp** | 尤蓓爾 | Surgical-precision assassin | #15 | UStructWalker: FField chain traversal, property reading | "If she can visualize it, she can cut it" — surgical struct dissection |
| **Fern.cpp** | 費倫 | Frieren's apprentice | #8 | PipeServer: Named Pipe JSON IPC (~30 commands) | The communicator, messenger — bridges worlds |
| **Sein.cpp** | 贊恩 | Priest, journey chronicler | #24 | Logger: 5-category per-process file logging with rotation | The quiet observer who records everything |
| **Himmel.cpp** | 欣梅爾 | Hero, remembered forever | #2 | Signatures: 128+ AOB pattern database | The hero's *legacy* — immutable knowledge left for those who follow |
| **Flamme.cpp** | 弗蘭梅 | Ancient master, knowledge keeper | — | HintCache: per-game AOB result caching | Ancient wisdom passed down — accelerates future scans |
| **Grimoire.h** | 魔導書 | Grimoire | — | Constants, magic strings, DynOff namespace | Book of spells — the configuration tome |
| **Renge.cpp** | 蓮格 | Liaison character | #22 | PipeProtocol: IPC command/event definitions | Communication protocol — the rules of engagement |
| **Stark.cpp** | 修塔爾克 | Brave warrior, frontline | #7 | GameThreadDispatch: MinHook ProcessEvent hook | Charges into the front line — executes on the game thread |
| **Mimic.cpp** | 寶箱怪 | Chest mimic (classic gag) | #21 | Mailbox: CE Lua shared-memory interface | Disguised as an innocent exported struct — actually a secret channel |
| **Methode.cpp** | 梅特戴 | All-capable analyst mage | #16 | CEPlugin: CE Plugin Type 5 interface | Analytical entry point — examines everything |
| **Heiter.cpp** | 海塔 | Priest who started the journey | — | dllmain: DLL entry point, auto-start logic | The one who set the journey in motion — DLL_PROCESS_ATTACH |
| **Lugner.cpp** | 琉古納 | Demon master of disguise | #12 | ProxyVersion: version.dll forwarding proxy | The deceiver — pretends to be the real version.dll |
| **Scharf.h** | 夏爾夫 | Sharp-eyed, scrutinizing examinee | #17 | WalkerAlignment: FProperty offset-vs-alignment validator | Sharp eye for layout flaws — catches misaligned EnumProperty / FName that hint at a wrong FPROPERTY_OFFSET probe |
| **Wirbel.cpp** | 威亞貝爾 | Northern squad leader, pragmatic soldier | #20 | Teleport: marker save/recall + cursor teleport (BugIt-style) | Swift battlefield repositioning — the soldier who relocates first |
| **Tot.h** | 托托 | "Saint of the End", Greater Demon | — | Cancellation: cooperative cancel flag for long-running ops | The End — signals every long loop to stop (was `Cancel`) |
| **Lineal.h** | 莉涅爾 | First-class mage, 15-yr undercover spy | — | PackedItem: UE5.7+ packed FUObjectItem reconstruction | The straightedge — realigns the non-standard packed layout (was `PackedItem`) |
| **Radar.cpp** | 拉達爾 | Shadow Warrior, plateau village chief | — | ValueScan: CE-style by-value First/Next Scan | The sweep — scans every object for a matching value (was `ValueScan`) |
| **Solitar.cpp** | 索莉塔 | Greater demon studying humanity | #11 | GodMode: force AActor::bCanBeDamaged (damage immunity) + re-assert worker | Overwhelming, near-unkillable mage — invulnerability; reuses the FBoolProperty bit-write Wirbel uses for the cursor |
| **Orden.h** | 歐爾登 | Noble house head ("order") | — | GroupMatch: source-agnostic SDR/assignment core for multi-value group scan | Brings *order* to a scattered set of values — assigns each value to its leaf slot (header-only, pure) |

---

## File Rename Map

| Before | After | Header |
|---|---|---|
| `ExportAPI.h/.cpp` | `Frieren.h/.cpp` | `Frieren.h` |
| `OffsetFinder.h/.cpp` | `Genau.h/.cpp` | `Genau.h` |
| `Memory.h/.cpp` | `Macht.h/.cpp` | `Macht.h` |
| `ObjectArray.h/.cpp` | `Aura.h/.cpp` | `Aura.h` |
| `FNamePool.h/.cpp` | `Serie.h/.cpp` | `Serie.h` |
| `UStructWalker.h/.cpp` | `Ubel.h/.cpp` | `Ubel.h` |
| `PipeServer.h/.cpp` | `Fern.h/.cpp` | `Fern.h` |
| `Logger.h/.cpp` | `Sein.h/.cpp` | `Sein.h` |
| `Signatures.h` | `Himmel.h` | `Himmel.h` |
| `HintCache.h/.cpp` | `Flamme.h/.cpp` | `Flamme.h` |
| `Constants.h` | `Grimoire.h` | `Grimoire.h` |
| `PipeProtocol.h` | `Renge.h` | `Renge.h` |
| `GameThreadDispatch.h/.cpp` | `Stark.h/.cpp` | `Stark.h` |
| `Mailbox.h/.cpp` | `Mimic.h/.cpp` | `Mimic.h` |
| `CEPlugin.cpp` | `Methode.cpp` | *(no header)* |
| `dllmain.cpp` | `Heiter.cpp` | *(no header)* |
| `ProxyVersion.cpp` | `Lugner.cpp` | *(no header)* |

**Unchanged**: `BuildInfo.h.in`, `version.rc`

> **New (post-577)**: `Scharf.h` introduced for the FProperty alignment helper extracted from Ubel.cpp. No prior file rename — born Frieren-named.

---

## Namespace Structure

```
Frieren::                   // ExportAPI — the gateway (extern "C", no namespace wrapper)
Genau::                     // OffsetFinder — the examiner
Macht::                     // Memory — raw power
Aura::                      // ObjectArray — the scale
Serie::                     // FNamePool — name oracle
Ubel::                      // UStructWalker — surgical dissection
Fern::                      // PipeServer — messenger (also class name)
Sein::                      // Logger — chronicler
Himmel::                    // Signatures — hero's legacy (header-only)
Flamme::                    // HintCache — ancient wisdom
Stark::                     // GameThreadDispatch — frontline warrior
Wirbel::                    // Teleport — swift battlefield repositioning
Solitar::                   // GodMode — force AActor::bCanBeDamaged (damage immunity)
Mimic::                     // Mailbox — disguised channel
Renge::                     // PipeProtocol — liaison rules
Scharf::                    // FProperty alignment validator (header-only)
Tot::                       // Cancellation — cooperative cancel flag (header-only; was Cancel)
Lineal::                    // PackedItem — UE5.7+ packed FUObjectItem reconstruction (header-only; was PackedItem)
Radar::                     // ValueScan — CE-style by-value scan (was ValueScan)
Orden::                     // GroupMatch — source-agnostic SDR matcher (multi-value group scan; header-only)
Grimoire::                  // Constants — spell book
DynOff::                    // Dynamic offsets (in Grimoire.h, unchanged)
```

> **Note**: No `UE5::` root prefix — flat namespaces matching the original code style.

---

## Comment Format

Every Frieren-named file MUST include this header:

```cpp
// {EnglishName} — {中文名} ({meaning/title})
// {Actual function description}
```

### Examples

```cpp
// Genau — 葛納烏 (一級魔法使篩選考官 — First-Class Mage Examiner)
// OffsetFinder: AOB pattern scanning for GObjects, GNames, GWorld pointers
namespace Genau {
    // ...
}
```

```cpp
// Macht — 黃金鄉馬哈特 (萬物成金魔法 — Seven Sages, Transmutation)
// Memory: AOB scanning, SEH-protected reads/writes, RIP-relative resolution
namespace Macht {
    // ...
}
```

```cpp
// Mimic — 寶箱怪 (芙莉蓮的經典梗 — The Classic Gag)
// Mailbox: CE Lua shared-memory command interface (no CreateRemoteThread needed)
namespace Mimic {
    // ...
}
```

---

## UI Naming (No Change)

The C# UI keeps standard English names for panels/services/ViewModels.
Only internal constants reference Frieren terms:

```csharp
// Grimoire — 魔導書 — Application constants and magic strings
public static class Constants  // class name stays English for IDE discoverability
{
    public const string PipeName = @"\\.\pipe\UE5DumpBfx";  // unchanged
    // ...
}
```

---

## 3rd Popularity Poll Reference (2026-03-29)

Total votes: **12,700,122** | Voting period: 2026-03-08 ~ 2026-03-29

### Top 30 (Total Votes)

| # | Character | Votes | Used In |
|---|-----------|-------|---------|
| 1 | Genau (葛納烏) | 1,396,535 | **OffsetFinder** |
| 2 | Himmel (欣梅爾) | 1,327,500 | **Signatures** |
| 3 | Aura (阿烏拉) | 1,020,761 | **ObjectArray** |
| 4 | Frieren (芙莉蓮) | 836,891 | **ExportAPI** |
| 5 | Macht (馬哈特) | 811,841 | **Memory** |
| 6 | Serie (賽莉耶) | 707,902 | **FNamePool** |
| 7 | Stark (修塔爾克) | 383,016 | **GameThreadDispatch** |
| 8 | Fern (費倫) | 366,486 | **PipeServer** |
| 9 | Demon Attacking Rufen Region | 365,049 | — |
| 10 | Bought Skeleton (骨頭) | 339,302 | — |
| 11 | Solitär (索莉塔) | — | — |
| 12 | Lügner (琉古納) | — | **ProxyVersion** |
| 13 | Sense (乘斯) | — | — |
| 14 | Linie (莉涅) | — | — |
| 15 | Übel (尤蓓爾) | — | **UStructWalker** |
| 16 | Methode (梅特戴) | — | **CEPlugin** |
| 17 | Scharf (夏爾夫) | — | — |
| 18 | Glück (格呂克) | — | — |
| 19 | Stoltz (修托爾茲) | — | — |
| 20 | Wirbel (威亞貝爾) | — | — |
| 21 | Mimic (寶箱怪) | — | **Mailbox** |
| 22 | Renge (蓮格) | — | **PipeProtocol** |
| 23 | Hero of the South (南方勇者) | — | — |
| 24 | Sein (贊恩) | — | **Logger** |
| 25 | Denken (鄧肯) | — | **NativeDisasm** |
| 26 | Kanne (卡妮) | — | — |
| 27 | Land (蘭特) | — | — |
| 28 | Richter (里希特) | — | — |
| 29 | Rivale (利瓦雷) | — | — |
| 30 | Receptionist (櫃台人員) | — | — |

### One-Vote-Per-Person Top 7

Frieren, Himmel, Stark, Fern, Methode, Mimic, Genau

### Available for Future Use

> Superseded by the **Full Character Roster** below, which draws the available-name
> pool from the complete Wikipedia character list (not just the poll Top 30).

---

## Full Character Roster — Name Pool

Source: **[List of Frieren characters — Wikipedia](https://en.wikipedia.org/wiki/List_of_Frieren_characters)**.
This is the authoritative pool of names usable for new DLL modules.

**Legend**: 🟢 in use · 🟡 reserved (designed, not yet built) · ⬜ available

**File-name rule**: C++ identifiers strip the umlaut dots — `ä→a ö→o ü→u`
(matching the existing `Übel→Ubel`, `Lügner→Lugner` convention), e.g.
`Glück→Gluck`, `Böse→Bose`, `Löwe→Lowe`, `Dünste→Dunste`, `Lektüre→Lekture`.
German meanings are given because most names map cleanly to a module function.

### Frieren's Party & The Hero Party

| Character | File form | Meaning / role | Status | Suggested use |
|---|---|---|---|---|
| Frieren | Frieren | Protagonist, slayer-mage | 🟢 ExportAPI | — |
| Fern | Fern | Frieren's apprentice | 🟢 PipeServer | — |
| Stark | Stark | Frontline warrior | 🟢 GameThreadDispatch | — |
| Sein | Sein | Healing priest, chronicler | 🟢 Logger | — |
| Himmel | Himmel | The hero, remembered forever | 🟢 Signatures | — |
| Heiter | Heiter | Priest who started the journey | 🟢 dllmain | — |
| Eisen | Eisen | Dwarf warrior, "iron", sturdy/retired | ⬜ | Robust/stable core or legacy-stable path |

### Demons — Confidant & Seven Sages of Destruction

| Character | File form | Meaning / role | Status | Suggested use |
|---|---|---|---|---|
| Aura | Aura | Scales of Obedience (mind control) | 🟢 ObjectArray | — |
| Macht | Macht | Gold transmutation curse | 🟢 Memory | — |
| Schlacht | Schlacht | "The Omniscient", precognition | ⬜ | Prediction / lookahead / speculative scan |
| Grausam | Grausam | Master of illusion magic ("cruel") | ⬜ | Decoy / obfuscation / anti-debug counter |
| Böse | Bose | Immortal Sage, barrier magic ("evil") | ⬜ | Protection / guard / anti-tamper shield |

### Demons — Greater & Other

| Character | File form | Meaning / role | Status | Suggested use |
|---|---|---|---|---|
| Lügner | Lugner | Master of disguise / envoy | 🟢 ProxyVersion | — |
| Solitär | Solitar | Greater demon studying humanity | 🟢 GodMode | Force AActor::bCanBeDamaged via FBoolProperty bit + re-assert worker (`Solitar.cpp`, build 1251) |
| Tot | Tot | "Saint of the End", end-curse | 🟢 Cancellation | Cooperative cancel flag for long-running ops (`Tot.h`, was `Cancel`) |
| Rivale | Rivale | "Bloody God of War", forges weapons | ⬜ | Builder / generator (CT / AA script) |
| Qual | Qual | Creator of Zoltraak (universal magic) | ⬜ | Foundational engine / AOB pattern compiler |
| Linie | Linie | Reads opponent mana ("line") | ⬜ | Analysis / profiling / lineage trace |
| Draht | Draht | Lügner's assistant ("wire") | ⬜ | Wiring / binding / IPC plumbing |
| Revolte | Revolte | Four-handed general, four swords | ⬜ | Parallelism / multi-threaded dispatch |
| Hemmung | Hemmung | Mist→energy ("inhibition") | ⬜ | Throttle / rate-limit / backpressure |
| Solide | Solide | Blindfold swordsman ("solid") | ⬜ | Robust read validation |
| Jung | Jung | Curious demon child ("young") | ⬜ | Experimental / sandbox features |
| Zart | Zart | "Lingering Shadow", spatial transference | ⬜ | Memory remap/relocate (note: Wirbel owns teleport) |

### Mages — Continental Magic Association

| Character | File form | Meaning / role | Status | Suggested use |
|---|---|---|---|---|
| Serie | Serie | Living-history great mage | 🟢 FNamePool | — |
| Genau | Genau | First-Exam proctor / examiner | 🟢 OffsetFinder | — |
| Methode | Methode | All-capable analyst, detection | 🟢 CEPlugin | — |
| Sense | Sense | Second-Exam proctor ("scythe") | ⬜ | Reaping / cleanup / harvest-collection |
| Falsch | Falsch | By-the-book proctor ("false") | ⬜ | Validation / assertion / error detection |
| Lernen | Lernen | Serie's apprentice ("to learn") | ⬜ | Adaptive heuristics / calibration |
| Lineal | Lineal | 15-year undercover spy ("ruler") | 🟢 PackedItem reconstruct | UE5.7+ packed FUObjectItem split/rejoin (`Lineal.h`, was `PackedItem`) |

### Mages — First-Class Exam & Others

| Character | File form | Meaning / role | Status | Suggested use |
|---|---|---|---|---|
| Übel | Ubel | Cleaving-magic assassin | 🟢 UStructWalker | — |
| Wirbel | Wirbel | Magic Corps captain ("whirl") | 🟢 Teleport | — |
| Scharf | Scharf | Petals→steel blades ("sharp") | 🟢 WalkerAlignment | — |
| Denken | Denken | Court magician, Macht's student | 🟢 NativeDisasm | — |
| Land | Land | Creates flawless clones | ⬜ | Cloning / replication / duplicate detection |
| Kanne | Kanne | Controls water ("watering can") | ⬜ | Flow / streaming / pipelining |
| Lawine | Lawine | Freezes water ("avalanche") | ⬜ | Snapshot/freeze or bulk cascade |
| Edel | Edel | Hypnosis magic ("noble") | ⬜ | Override / control injection |
| Richter | Richter | Staff repair ("judge") | ⬜ | Scoring / judging / recovery-repair |
| Laufen | Laufen | High-speed movement ("to run") | ⬜ | Fast-path / SIMD acceleration |
| Ehre | Ehre | Controls rocks ("honor") | ⬜ | Foundation / stability |
| Blei | Blei | Edel's teammate ("lead" metal) | ⬜ | Weighting / ballast |
| Dünste | Dunste | Edel's teammate ("vapors") | ⬜ | Volatile / transient state |
| Ton | Ton | Lone-wolf exam mage ("clay") | ⬜ | Shaping / serialization / formatting |

### Northern Empire — Special Forces & Shadow Warriors

| Character | File form | Meaning / role | Status | Suggested use |
|---|---|---|---|---|
| Phrase | Phrase | Special Forces captain | ⬜ | Parsing / syntax / expression eval |
| Kanone | Kanone | Special Forces ("cannon") | ⬜ | Bulk / heavy blast scan |
| Neu | Neu | Discovers undercover ("new") | 🟢 EnumNames | UEnum::Names parse — legacy `TArray<TPair<FName,int64>>` vs the UE5.6+ FNameData struct-of-arrays disguised at the same offset (`Neu.h`, build 1266) |
| Grau | Grau | Straight-laced trooper ("gray") | ⬜ | Neutral baseline |
| Lager | Lager | Carefree trooper ("storage/depot") | ⬜ | Cache / buffer pool / storage |
| Löwe (Held) | Lowe | Governor / anti-magic ("lion") | ⬜ | Aggressive / dominant heuristic |
| Radar | Radar | Shadow Warrior chief | 🟢 ValueScan | CE-style by-value First/Next Scan (`Radar.cpp/.h`, was `ValueScan`) |
| Schritt | Schritt | Shadow Warrior ("step") | ⬜ | Stepping / single-step iteration |
| Routine | Routine | Shadow Warrior librarian | ⬜ | Scheduled / periodic subroutine |
| Kreis | Kreis | Shadow Warrior blacksmith ("circle") | ⬜ | Ring buffer / loop / cycle |
| Lore | Lore | Shadow Warrior nun ("lore") | ⬜ | Knowledge base / metadata store |
| Walross | Walross | Ex-Hero Rasen ("walrus") | ⬜ | (thematic) |
| Wolf / Iris / Klematis / Gazelle | Wolf / Iris / Klematis / Gazelle | Minor Shadow Warriors | ⬜ | (thematic pool) |

### Other Characters

| Character | File form | Meaning / role | Status | Suggested use |
|---|---|---|---|---|
| Flamme | Flamme | Ancient master, concealment | 🟢 HintCache | — |
| Gehen | Gehen | Dwarf who built a canyon bridge | ⬜ | Bridge / IPC connector (strong fit) |
| Glück | Gluck | Lord allied with Macht ("luck") | ⬜ | Lucky heuristic / fallback logic |
| Kraft | Kraft | Ancient elven monk ("force/power") | ⬜ | Heavy-compute / force utility |
| Orden | Orden | Noble house head ("order") | 🟢 GroupMatch | Source-agnostic SDR matcher for multi-value group scan (`Orden.h`, header-only, build 1276) |
| Fass | Fass | Dwarf seeking ale ("barrel/cask") | ⬜ | Container / buffer |
| Voll | Voll | Old dwarf friend ("full") | ⬜ | Capacity / completeness check |
| Milliarde | Milliarde | Old elf ("billion") | ⬜ | Large-count handling |
| Lektüre | Lekture | Denken's late wife ("reading") | ⬜ | Reader / parser |
| Lecker | Lecker | Talented cook ("delicious") | ⬜ | Presentation / formatting |
| Granat | Granat | Town graf ("garnet") | ⬜ | (thematic) |
| Stoltz | Stoltz | Stark's brother ("proud") | ⬜ | (thematic) |
| Eisen | Eisen | (see Hero Party) | ⬜ | Robust/stable core |

> **Title-only / unnamed roles excluded** (not clean identifiers): Emperor,
> Hero of the South, Sword Village Chief, Stark's Father, Sein's Older Brother.
>
> **Not on the Wikipedia roster** (kept anyway): `Mimic` (寶箱怪 chest-mimic gag),
> `Renge` (蓮格 liaison, poll #22), `Grimoire` (魔導書 — an item, not a character).

### Plain-English module migration status

| Module | File | Function | Status |
|---|---|---|---|
| Cancellation | `Tot.h` (was `Cancel.h`) | cooperative cancel flag | ✅ renamed `Cancel → Tot` |
| Packed item | `Lineal.h` (was `PackedItem.h`) | UE5.7+ packed FUObjectItem reconstruct | ✅ renamed `PackedItem → Lineal` |
| Value scan | `Radar.cpp/.h` (was `ValueScan.*`) | CE-style by-value scan | ✅ renamed `ValueScan → Radar` |
| Graph path | `GraphPath.h` | BFS shortest-path core (under `Aura::`) | ✅ kept — helper inside `Aura::`, by design |
| UTF-8 helpers | `Utf8Helpers.h` | string conversion leaf util | ✅ kept — generic utility, by design |

---

## Sources

- [List of Frieren characters — Wikipedia](https://en.wikipedia.org/wiki/List_of_Frieren_characters) — **primary name-pool roster**
- [Frieren: 8 Most Popular Characters, Officially Ranked By Japan Poll — GameRant](https://gamerant.com/frieren-most-popular-characters-third-popularity-poll/)
- [Himmel Officially Loses No. 1 Spot — CBR](https://www.cbr.com/frieren-official-character-ranking-2026-himmel-lose/)
- [Frieren Character Popularity Poll Results — Oricon](https://us.oricon-group.com/news/8194/)
- [《葬送的芙莉蓮》第三回人氣票選 — 4Gamers](https://www.4gamers.com.tw/news/detail/78111/frieren-beyond-journeys-characters-popularity-vote-2026)
- [Genau Takes Top Spot in 3rd Popularity Poll — ANIME FREAKS](https://times.abema.tv/en/articles/-/10235832)
