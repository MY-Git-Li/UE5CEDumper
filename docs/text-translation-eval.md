# In-Game Text Interception + S2T Conversion + Local-LLM Translation — EVALUATED (2026-07-24), NOT BUILT (subset shipped)

> **Status**: Full multi-agent evaluation (41 agents, adversarial verification of 12 load-bearing
> claims — **all 12 refuted or heavily qualified**). The headline "rewrite on-screen text in memory"
> feature is **rejected**. A small, honest subset is recommended instead; the FText-read half of it
> **already shipped in build 2368** (see §5, Phase 3). This is a decision document — read §1, then §7
> (what was rejected and why the offline route wins).
>
> Siblings: [teleport-coord-library-spec.md](teleport-coord-library-spec.md) (another EVALUATED-scope doc),
> [godmode-spec.md](godmode-spec.md) (the locked "no universal detection primitive" Non-Goal that also
> governs this feature), [technical-notes.md](technical-notes.md) (FText / FString layout facts),
> [lessons-learned.md](lessons-learned.md) ("validate by side-effect, not metadata").

---

## 1. 結論先講

**不要做 in-memory 文字改寫。** 五個架構提案裡沒有一個能在 shipping build 上同時做到「寫得進去」「畫得出來」「不會腐蝕 heap」「跨遊戲可用」——其中兩項是 UE 原始碼層級的硬限制，不是工程量問題。

**要做的是三塊小東西（合計 S+M，不到任何一個提案的 1/10）**：

1. **Culture switcher**（`UKismetInternationalizationLibrary::SetCurrentCulture`，已驗證在 UE4.27.2 與 5.8 都是未被 strip 的 `BlueprintCallable`）——零寫入、零配置、零字型風險，直接解決「遊戲其實有 zh-Hant，但選單不給選 / 被 launcher 鎖住」這個真實案例。
2. **字型覆蓋率探針**（`UFont::FontCacheType` + composite font graph + cmap 解析）——純 reflection，即使翻譯永遠不做也是有用的診斷。
3. **修好既有的 `Ubel::ReadFTextString` / `ReadFString`**——這兩個修正對 Value Search / Snapshot / Live Walker 是**獨立的淨收益**。✅ **build 2368 已完成**（見 §5）。

**字型問題必須先講清楚**：這是第一順位風險，且**沒有任何一個提案能解**。若目標遊戲用 Offline-cached `UFont`（pre-baked atlas，無 FreeType、無 fallback chain），寫進去的繁體字**物理上不可能被畫出來**，且失敗表現通常是**看不見的空白**（`LastResort.ttf` 在 Shipping 常沒有被 stage，缺字最終落到 FreeType `.notdef` = blank advance），不是明顯的豆腐框。這是最難 QA 的失敗模式，且**尚未在任何一款目標遊戲上量測過**——所以探針必須排在所有改寫工作之前。

**還有一個會直接翻盤的事實**：`docs/test-games.md` 裡的遊戲，列有中文名的幾款（冒險家艾略特的千年奇譚、艾恩葛朗特迴盪新聲、太陽龐克、劍星、女神異聞錄３）**全是繁體中文名**，其餘以 SQUARE ENIX / Bandai Namco / ATLUS 日系為大宗。也就是說**簡轉繁這個 headline 功能，很可能在本 repo 的測試矩陣裡連一款可驗證的對象都沒有**。這件事花 30 分鐘（逐款翻 Steam 語言表）就能查清楚，應在寫任何一行改寫程式碼之前查。

---

## 2. 這兩件事其實是兩個功能

它們不只是「範圍不同」，是**需要不同的寫回架構**，捆在一起等於同時付兩份風險。

### 2-A. 簡體 → 繁體（含台灣用詞）

| 面向 | 事實 |
|---|---|
| 轉換引擎 | OpenCC `s2twp`，Apache-2.0，微秒級，確定性，零 GPU、零網路 |
| 使用者舉的三個例子 | `硬盤→硬碟` ✅、`打印機→印表機` ✅、`鼠標` 為 one-to-many（裝置語意=`滑鼠`；指標語意=`游標`；OpenCC `TWPhrases` 預設 `滑鼠`）——正好示範**字典必須是詞組優先、最長匹配，且使用者可覆寫的外部檔** |
| 長度 | char-level 對 GB2312-encodable 來源 100% UTF-16 長度不變 → 可 in-place。但 `s2twp` 的 phrase 層 **~27% 會變長度**（`臺式機→桌上型電腦` +2、`卡塔爾→卡達` −1），台灣用詞正是變長度那一半 |
| 兩階段 | `TWPhrases` 用**轉換後的繁體形**當 key（`臺式機` 是 key，`台式機` 不是），必須先跑 stage 1 |
| 既有替代方案 | 台灣社群已有完整零程式碼流程：FModel + UnrealLocres/UE4LocalizationsTool + 繁化姬/OpenCC + loose `.locres` 或 `_P.pak`，覆蓋率 ~100%、位置正確、可換字型、重開遊戲仍在 |

**裁決：不做 live 版，改用 §附錄 的 offline 流程。** live 版唯一站得住的利基是三個窄 case——(a) pak 加密 / IoStore 無法重打包、(b) 明確不可修改遊戲檔案（anti-cheat / 線上）、(c) 調字典時要即時迭代。這三個不是本 repo 使用者的預設情境。

> **加分發現（已驗證）**：Shipping build 裡 loose file 永遠無法遮蔽 pak 內既有路徑（`bLookLooseFirst` 被 `#if !UE_BUILD_SHIPPING` 編掉），但 `.locres` **不在** `ExcludedNonPakExtensions` 裡——所以**新增**一個所有 pak 都沒有的 culture（例如對 zh-Hans-only 遊戲新增 zh-Hant）**用 loose file 就會載入**，不需要 `_P.pak`，也不受 pak signing 影響。這條規則把 offline 路線的成本又壓低了一級。

### 2-B. 本地 LLM 即時翻譯（Ollama / 遠端 GPU）

| 面向 | 事實 |
|---|---|
| Blocking 模式 | **架構上死亡。** 8B 模型在高階 GPU 上光 TTFT 就 45–89 ms（數個 frame），完整短句 ~190–330 ms；中階卡 350–570 ms。唯一實作過 blocking 寫回的 LunaTranslator，自己的文件就寫「會造成卡頓」 |
| Background 模式吞吐 | 中階 GPU / 8B ≈ 3 strings/s → 50,000 條字串 ≈ **4.6 小時**；大型 RPG 規模達數十小時。第一輪遊玩基本上是沒有翻譯的 |
| VRAM | 同機 8B Q4 常駐 ~5.5–6 GB + UE5 遊戲 → 超出顯卡時 driver 靜默 fallback 到 system RAM → **嚴重、持續、我們偵測不到**的卡頓，使用者會怪我們的 DLL。**使用者若把 LLM 放另一台機器（如 18 GB free VRAM 的 PC）即可完全規避這條** |
| 品質 | 12–14B 本地模型比雲端翻譯略低，短片語 UI label 更差。Qwen 系會漏簡體字，**必須無條件跑 OpenCC `s2twp` 後處理** |
| 長度 | MT 輸出基本上**永遠**不是等長 → 強制走 engine-allocated 寫回（Angle B）或永久 leak，也就是最貴的那條路 |
| 法務 | 一份對話量大的 RPG 的 translation cache = 該遊戲**完整劇本 + 譯文**。社群共享 cache = 未授權衍生散布。這跟本 repo 現有的「檢視你自己擁有的遊戲的記憶體」是**完全不同等級**的曝險 |

**裁決：不做 live 版。** 正確形態是「**離線 pre-pass**」：用 UnrealLocres 抽出語料 → 遊戲關閉時用滿速 GPU（本機或遠端 18 GB PC 皆可）翻完 → 產出一份翻譯過的 `.locres` → 執行期由遊戲自己讀（零查表、零 IPC、零 GPU 爭用）。**cache 就是那份 `.locres` 本身**，是標準檔案而非共享 script。見 §附錄。使用者已明確表示 Star Trek 這種文章量大的翻譯工作不適合走互動式代跑——這與「離線批次」的結論一致。

---

## 3. 關鍵技術結論（每條經對抗式驗證）

### 3-1. FString 記憶體所有權 —— 絕對不可以換 `Data` 指標

`FString` 就是 `TArray<TCHAR, TSizedDefaultAllocator<32>>`，其 allocator 的解構會呼叫 `FMemory::Free(Data)`、`ResizeAllocation` 會呼叫 `FMemory::Realloc`，UE 4.18→5.8 形狀不變。所以 engine **不是「只會讀」**：每一次 `~FString` / `operator=` / `Empty` / `Reset` / 超過 `ArrayMax` 的 append，都會把我們的指標交給遊戲的 GMalloc。

- **VirtualAlloc 指標**：通常 64 KiB 對齊 → `MallocBinned2` 走 large-alloc 分支 → 找不到 `FPoolInfo` → `Fatal「Attempt to free an unrecognized pointer」`，必崩。
- **`new` / CRT `malloc` 指標**：不對齊 → small-block 分支 → 從**我們不擁有的記憶體**讀 pool header → canary Fatal，或更糟：**靜默污染真實 free list**，之後在別處爆炸。
- **「配了永遠不 free」不是解法**：free 的是 engine，不是我們。

> 只有三種寫法安全：**(a)** 原地覆寫既有 buffer（`newNum ≤ ArrayMax`，不動 `Data`、不動 `ArrayMax`，補 NUL）；**(b)** invoke UFunction setter 讓 engine 自己配；**(c)** 透過遊戲自己的 GMalloc 配置——而本 DLL **沒有** GMalloc handle。

### 3-2. 「char-level 簡轉繁一定等長」——只在受限條件下成立

OpenCC `STCharacters` 的 4,012 條裡有 **~21% UTF-16 長度會變**（目標落在 BMP 外變 surrogate pair），含約 28 條來源在一般文字可及範圍（罕見人名用字所在）。**Code point 數不變 ≠ UTF-16 code unit 數不變。**

更關鍵：**buffer 不一定是 UTF-16**。UE 5.4/5.5+ 有 `FUtf8String` / `FAnsiString`（1 byte element），本 repo 已用 `Ubel::ReadFUtf8String` 以 1 byte 讀它們。**Star Trek Voyager（stock UE5.6）的 FText 顯示字串就是 UTF-8**（build 2368 由 CE dump 證實，見 §5）。對這些欄位做 UTF-16 寬度的寫入 = 2× 溢位。

→ 任何 in-place 快路徑都必須：判 property 型別、轉換後**重新量測**長度並在不等時放棄、以 `ArrayMax` 為界、保留終止符。

### 3-3. ProcessEvent hook 看不到 `UTextBlock::SetText` —— 三種情況都看不到

本次評估最有價值的否定結果。`SetText` 是 `FUNC_Native` UFunction：

- **Blueprint graph 呼叫**：走 `CallFunction` → `UFunction::Invoke` → `execSetText` thunk，**路徑上沒有 ProcessEvent**；只看得到外層那個 BP event 的進入點。
- **Native C++ `MyText->SetText(...)`**：純 virtual call，完全看不到。
- **UMG property binding**：`SetText` **根本不會被呼叫**（Slate 每 frame 從 `TAttribute<FText>` 拉值）。ProcessEvent 唯一沾到邊的是：當 binding target 是 reflected UFunction 時，`TBaseUFunctionDelegateInstance::Execute` 會呼叫 `ProcessEvent`——那浮現的是 **getter**（如 STVoyager Live Funcs 裡的 `TextBinding::GetTextValue`，native，每 frame 904 次），不是 `SetText`。

`Stark` 是錯的工具。本 repo 自己早就記錄過同一件事：「on DQ7R the shop opens via a native C++ call the ProcessEvent hook can't see」（[dev-log.md:1535](dev-log.md)）。**此否定結果應永久保留在 [technical-notes.md](technical-notes.md)，避免下一個人重推這條死路。**

### 3-4. 原地改字串**不會重繪**

`FTextSnapshot::IdenticalTo` 比對的是 `TextDataPtr` / `LocalizedStringPtr` / revision / flags——**從不比內容**；Slate 在它回 true 時直接 short-circuit。等長覆寫後這些欄位全不變 → widget 保留舊 glyph run → **記憶體改了，畫面沒動**。Engine 自己的做法從來不是原地改：`UpdateLiveTable` 是**換指標**再 `DirtyTextRevision()`。而乾淨的 live-table API 在 Shipping 是**編譯期不存在**的（`static_assert(!ENABLE_LOC_TESTING || !UE_BUILD_SHIPPING)`）。

### 3-5. 字型 —— 第一順位風險

三道獨立關卡，每一道都有真實遊戲會卡住：

1. **UE font cache mode 主宰一切。** Offline-cached `UFont` 是 cook 時凍結的 texture atlas，執行期無 FreeType、無 fallback chain。**任何 memory patch、換字型、fallback mod 都救不了**，只能重烤 atlas。常見於 `UTextRenderComponent`（3D 世界文字）與 Canvas/HUD。
2. **cmap 覆蓋率不能從字型名字推斷。** Google Fonts 的 **「Noto Sans SC」被 subset 成 ~8,105 字的《通用规范汉字表》**（該表本身**零個繁體字**），而「Noto Sans CJK SC」是 44,806 字——兩個名字幾乎一樣，差一個數量級。只有解析內嵌 TTF 的 cmap 才是權威。
3. **Fallback chain 會救場，但那不是「遊戲的字型有這個字」。** DroidSansFallback 通常有被 stage，但它**用簡體字形畫共用碼位**——能讀，但字形是大陸樣式、字重/metrics 與周圍 UI 不一致。而且 `FSlateFontInfo::FontFallback = FF_NoFallback` 可以逐字串關掉這張網，且它**不是 UPROPERTY**，reflection 看不到。

**失敗是靜默的**：缺字最終是 blank advance 而非豆腐框——句子中間散落看不見的空洞，最難 QA。

### 3-6. 兩個會直接否決寫回的隱藏問題

- **存檔污染（不可逆）。** 寫進 reflected `FStrProperty`/`FTextProperty` 的字串可能被序列化進存檔（自訂角色名、裝備標籤、槽名、路徑點名稱）。**工具移除後仍留在存檔裡**；若遊戲拿顯示字串當 lookup key，存讀檔可能永久壞掉。最低限度規則：**永不寫入 owner 帶 `SaveGame` flag 的 property**。
- **工具會對自己說謊。** 改寫後 Value Search 找到的是我們的繁體字而非遊戲的簡體字；Snapshot Diff 會把它報成 `Changed`；CE export 會把我們的字串當 current value 帶走。使用者開著翻譯層 debug 會從工具核心功能拿到錯答案，且沒有任何提示。

---

## 4. 架構方案比較表

| | 攔截點 | 寫回方式 | 覆蓋率 | 需新 AOB | 崩潰風險 | 工作量 | 評分 |
|---|---|---|---|---|---|---|---|
| **A · 反射輪詢 + 雙路寫回** | GObjects 分片輪詢 | 等長 in-place（§3-4 畫面不變）/ `Conv_StringToText`→`SetText`（每字串永久 leak，上限後降級） | 僅 reflected Text/StrProperty | 否 | 高 | XL | **4/10** |
| **B · engine-allocated write** | 同上輪詢 | `Conv_StringToText`→`SetText`，engine 配 100% 記憶體 | UMG 且 `TextDelegate` 未綁定者 | 否 | 中高（invoke timeout 遲到→refcount underflow→UAF） | XL | **7/10** |
| **C · ProcessEvent 參數改寫** | Stark PE hook | 改寫 param buffer | **看不到 SetText（§3-3），機制不成立** | 否 | 高 | XL | **5/10** |
| **D · loc table 改寫 (D1) + offline .locres (D2)** | D1: heap value-scan；D2: 離線 | D1 FITS-only；D2 檔案層 | 只有帶 TextId 的 localized FText | D1 名義否（實質等同 AOB）；D2 否 | D1 高（誤判=任意 heap 破壞）；**D2 零** | XL / **D2=S** | **5.5/10**（D2 單獨 = **9**） |
| **E · read-only harvester + 外部 overlay** | 分片輪詢，**不寫遊戲記憶體** | 外部視窗顯示 | 同 A 讀取面，無 §3-1/3-4/3-5 風險 | 否 | **低** | XL | **7/10**（產品定位錯：無空間對應、真全螢幕無法疊、<500ms 文字被 debounce 丟掉） |

**排名合理性**：B 是唯一有合法記憶體所有權故事的寫回設計；E 是唯一結構上不可能破壞遊戲 heap 的設計；**D2 是整份評估裡單位風險價值最高的東西，而它根本不是 in-memory 方案。**

---

## 5. 建議架構（若要做）+ build 2368 已落地部分

### 5-0. 命名

新 C++ 模組：**`Lektüre`**（レクテューレ，Denken 的亡妻，德文「閱讀」；[naming-convention.md](naming-convention.md) 標為 ⬜ available，建議用途正是 *Reader / parser*）。選它把設計約束編碼進模組名：**這個模組永遠不寫遊戲記憶體。** 若日後真要開寫回路徑，它**必須是另一個模組**——這樣 code review 時「誰可以呼叫 `Macht::WriteBytes`」是從檔名就看得出來的事。轉換引擎（OpenCC 表）住在 C# UI 端，不套 Frieren 命名。

### 5-1. Phase 對照

| Phase | 內容 | 工作量 | 狀態 |
|---|---|---|---|
| **0** | 一頁 docs 教 offline 流程（見 §附錄） | S | 📄 本文附錄 |
| **1** | **Locale switcher** —— `Lektüre` 模組雛形 + `locale_get`/`locale_set` + UI 一張卡 | S | ⬜ 最小可用切片 |
| **2** | **Font probe** —— `font_probe` / `font_check_string` + UI 燈號 | M | ⬜ |
| **3** | **讀取修正** —— FText UTF-8/UTF-16 寬度自動判別 + 指標間接層 probe；`ReadFString` torn-read | M | ✅ **build 2368** |
| **4** | Capture ledger（`Linie` 形狀）+ 建置期 OpenCC 表（僅顯示，不寫回） | L | ⬜（僅在 Phase 3 證實讀得到之後） |
| **5** | 任何 in-memory 寫回 | XL | ❌ **建議不做** |

### 5-2. build 2368 已修（Phase 3 的核心）

根因：`Ubel::ReadFTextString` 只走 UTF-16 的 `ReadFString`，而 STVoyager（stock UE5.6）的 FText 顯示字串是 **UTF-8**（CE dump `2E1097B7000` = `E5 9C A8 …` = `在维修期间继续探索星系\0`），被當 wchar_t 解成亂碼 → 回傳空。

- 新增純函式 `Utf8Helpers::DecodeFStringBuffer`（[Utf8Helpers.h](../dll/src/Utf8Helpers.h)）：用 **null 終止符位置 + interior-null 閘門**自動判別 UTF-16 vs UTF-8 寬度。UTF-8 CJK 的 null 落在 byte `[n-1]`、內部無 0x00；UTF-16 ASCII 高位 byte 全是 0x00 → 兩者可靠區分。header-only、可單元測試（8 組新測試含 STVoyager 確切 dump，全過）。
- 新增 `Ubel::TryDecodeFStringAt`：cap 提高到 8192（對話長），但**不動** `ReadFString` 的 256 cap（保護 value scan / snapshot / walk 熱路徑）。
- `ReadFTextString` 改兩段掃描：Pass 1 inline（維持舊順序，已能解的遊戲零回歸）、Pass 2 **UE5.4+ 指標間接層**（`TSharedPtr<FString>` @+0 / `FRefCountedDisplayString` @+8）。
- torn-read 修正：`ReadFString` / `ReadFUtf8String` 把 `Data`+`Count` 併成單次 12-byte `ReadBytesSafe`，避免 realloc 間隙配錯長度——對所有字串讀取者是淨收益。

> ⚠️ **build 2372**：offset 掃描已改為佈局無關的有界掃描（inline `+0x08…+0x90` + 一層指標間接 inner `+0x00…+0x18`，放寬 Max 閘門）。
>
> 🔬 **IN-GAME VERIFIED 2026-07-24 (STVoyager, stock UE5.6) — reflection 讀取路徑對此遊戲是死路，且是結構性而非 bug。** Value Search 對每個試過的字串都回空（transient UTF-8 對白 **與** persistent UTF-16 房間標籤 `科学实验室` 皆然）。CE access-scan 證實原因：字串住在 **`FTextLocalizationManager` 全域顯示字串表**，不在任何可走訪的 UObject 屬性上。該表是巢狀的：外層 key-hash 表（0x20 stride：`+0x04` 遞增 FTextId 如 `0x1261A`、`+0x08`→子表、`+0x14` 雜湊）→ 每命名空間字串表（0x20 stride：`+0x08`→UTF-16 buffer、`+0x10`=含 null 字元數）→ 0x10 對齊的 UTF-16 字串資料。消費端是 UE 的 Slate 斷行迭代器（讀 `word ptr`，比對 0x0D/0x0A/0x2028/0x2029/0x0085），即畫面 widget 透過 property-binding/native-Slate 拉值 → **沒有可讀的 FTextProperty**（§3-3 的牆，DQ7R 同一盲點）。⇒ build 2368/2372 的修正對「有掛 FText 屬性」的遊戲仍正確且有用，但**掃不到不存在的屬性**。**這就是「offline `.locres` 是此遊戲正解」的實證**——磁碟上的 `.locres` 正是這張 namespace/key→字串表。泛用地在記憶體定位此表需 per-game AOB（即已否決的 D1）；「dump 使用者用 CE 指定位址的 loc 表」逃生口僅對加密 pak 遊戲有意義，未建（pak 可解時 offline 嚴格更好）。

### 5-3. 若要續做 Phase 1/2/4（設計要點）

- **CDO + FunctionInfo 用 generation counter 快取（含快取失敗）**，照抄 `Schlacht.cpp`；每 tick 前 gate 於 `Stark::IsGameThreadResponsive()`。
- `SetCurrentCulture` 的 `FString` 參數沿用 `Fern.cpp` 現成的 CRT-malloc marshalling 與 timeout-leak 政策（唯一已驗證安全的字串輸入路徑）。
- Font probe 走 `UFont` → `FontCacheType`（reflected UPROPERTY）→ `FCompositeFont` → `FTypeface` → `FFontData` → cmap，對候選輸出字串逐字檢查覆蓋率，輸出三態（Offline 硬失敗 / Runtime 全覆蓋 / Runtime 缺 N 字）。
- Capture ledger 完全照 `Linie` 形狀：opt-in Start/Stop、專用 mutex、pull-based `get`、`Tot::MarkBackgroundWorker()`、`THREAD_PRIORITY_BELOW_NORMAL`、掃描前先用 `text_capture_probe` 量 wall time（**不要照算術估計的 tick rate 直接開發**——multipipe Phase 1 的前車之鑑）。**快取 key 一律是來源字串的 content hash，絕不是 widget 位址**（回收的 UMG widget 記憶體會讓 walk 走進垃圾）。
- 字典：**建置期從 OpenCC 的 Apache-2.0 字典資料產表**（Tier A ~2,703 組 BMP 1:1 ≈ 10.6 KB；Tier B phrase 可選），不 vendor OpenCC native lib。必須另備**使用者可編輯的手動覆寫表**（歧義字 `鐘/鍾`、`臺/檯/颱`、`復/複/覆` 在無語境短 label 上錯誤率 30–50%，演算法無解）。
- Cache（僅 Phase 4+）：檔名以 **EXE module name 為 key**（非 PE hash，否則每次遊戲更新丟光語料）；**獨立檔案**，不寄生 `snapshots.*.db`；逐字抄 `SnapshotStore.cs` 的 per-path schema gate + PRAGMA batch（PR #451 曾靜默資料遺失）；**local-only，不匯出、不匯入**。

---

## 6. 最小可用切片（一個 session、一款遊戲可驗完）

**Phase 1 的 Locale switcher。** 一個 `Lektüre::GetCultures()` + `SetCulture()`、兩個 pipe command、UI 一個下拉加一顆按鈕。驗證遊戲：**Satisfactory（stock UE5.6，多語系、易啟動、無反作弊）**或 **Star Trek Voyager（stock UE5.6）**——`locale_get` 應回一串 culture code，切到 `zh-Hant` 後選單當場變繁中。若這條在 stock UE5.6 上不通，後面所有階段的 invoke 機制假設都要重新檢討。

---

## 7. 不做 / 已否決

| 項目 | 否決理由 |
|---|---|
| **ProcessEvent params-buffer 改寫** | 機制不成立（§3-3）。抓不到 widget 文字。**此否定結果寫進 docs** |
| **`UFunction::Func` exec-thunk 轉向** | 引入全新風險類別（逐函式控制流轉向、退役 trampoline、thunk 內再進 PE），無法單元測試，覆蓋率僅 BP 派發 |
| **`FTextLocalizationManager` live table 改寫 (D1)** | 定位靠對數 GB heap 做 pointer value-scan，未驗證，誤判=任意 heap 破壞；且唯一 reflection 可及的 invalidation 會摧毀自己剛發佈的成果 |
| **原地改共用 display string** | §3-4：畫面不會變。用靜默破壞換零可見效果 |
| **Blocking 模式 LLM** | §2-B。唯一實作者自承會卡頓 |
| **vendor OpenCC native lib 進 DLL** | ~1.5 MB 加到 2.6 MB DLL 上，第三個 native 相依。建置期產表 10.6 KB 拿到大部分可見效益 |
| **繁化姬（Fanhuaji）內嵌** | 閉源託管服務、每字串一次網路往返（可作 offline 流程的手動選項，不內嵌） |
| **共享 translation cache / 匯出匯入** | 對話量大的 RPG 的 cache = 完整劇本 + 譯文 = 未授權衍生散布 |
| **auto 語言偵測預設開啟** | 短日文 label（`会話`/`装備`）無法逐字串與簡體分辨，`売却` 會被轉成不存在的混種 `売卻`。唯一健全 gate 是 session 級；**預設必須關閉，不提供 auto** |
| **`UEditableText` 改寫** | 玩家正在輸入的欄位被改寫 = 送出值被改，直通 §3-6 存檔污染 |
| **獨立 overlay 視窗（E 產品面）** | 無空間對應；真全螢幕獨佔下 DWM 被繞過=全黑無錯誤；<500 ms 文字被 debounce 丟掉 |

### offline UnrealLocres 基線：它贏了嗎？ **贏了，壓倒性地——對簡繁那一半。**

| | offline `.locres` | in-memory |
|---|---|---|
| 覆蓋率 | 已出貨語言的**全部** localized 文字 | reflection 可及的子集 |
| 位置 / 換行 | 完全正確 | 可能溢出固定寬度框 |
| 字型 | **可以換 `.ufont`** | 只能重指向已載入字型；Offline atlas 無解 |
| 重開遊戲 | 仍在 | 消失 |
| 崩潰 / 存檔污染 | 零 | §3-1 / §3-6 |
| 工作量 | **S（工具都是現成的）** | XL |

in-memory 唯一贏的三個窄 case：pak 加密無法重打包 / 明確不可改檔 / 調字典即時迭代。若這三個不是實際需求，整個 XL 沒有正當性。**應該先確認需求是不是這三個，而不是做完才發現。**

---

## 8. 風險與未解問題（按嚴重度排序 + 解決它的具體測試）

| # | 風險 | 後果 | 解決它的具體測試 |
|---|---|---|---|
| 1 | 測試矩陣可能沒有任何出簡體中文的遊戲 | 簡轉繁功能連一款可驗證對象都沒有 | 逐款翻 `docs/test-games.md` 的 Steam 語言表。**30 分鐘，零程式碼** |
| 2 | 字型 cache mode 可能是 Offline | 功能物理上不可能生效 | Phase 2 `font_probe`；手動版：Instance Finder 找 `UFont`，Live Walker 讀 `FontCacheType` |
| 3 | **FText header 落在 probe 之外** | build 2368 的讀取修正在此遊戲仍回空 | CE 對 `2E1097B7000` 做 pointer-scan / "find what accesses"，取得 ITextData→字串的 offset 鏈 |
| 4 | cmap 覆蓋率不足（Noto Sans SC 類） | 句子中間散落**看不見的空洞** | `font_check_string` 對 `髮 麵 隻 錶 闆 醜 幹 臺 機` 逐字檢查；輔助訊號：遊戲 log 的 `Could not find Glyph Index 0` |
| 5 | 存檔污染 | 使用者存檔永久損壞 | 寫回設計強制「owner 帶 `SaveGame` flag 就跳過」，在有自訂角色名的遊戲實測存讀檔 |
| 6 | 掃描成本只有算術、沒有量測 | 「比 Solide 便宜」的論證垮掉 | `text_capture_probe` 同步跑一次完整掃描回 wall time（`Sense` 已免費計時） |
| 7 | BP vs native 設字比例未知 | native 驅動 UI 的 AAA 遊戲覆蓋率可能只有 10% | **用現有 Linie**：Start → 開對話框 → Stop → 看排名有無設字類 UFunction（STVoyager 已見 `GetTextValue` 是 native getter，非 setter） |
| 8 | QA 不可自動化 | 永久人工回歸成本 | 接受並寫進 docs：驗收=繁中讀者看螢幕確認字對、位置對、字形可讀 |

---

## 9. 驗證計畫（對應 docs/test-games.md）

| Phase | 遊戲 | 看什麼 |
|---|---|---|
| **1 Locale** | Satisfactory / **Star Trek Voyager**（stock 5.6）、Hogwarts Legacy（4.27）、Stellar Blade（劍星，4.26 fork） | `locale_get` 回多個 culture code；切 `zh-Hant` 後選單當場改變。stock 5.6 是最乾淨基準 |
| **2 Font** | The Adventures of Elliot（5.4，已有繁中，正對照）、DQ III HD-2D、Persona 3 Reload（4.27 fork）、Tower of Mask（4.27 stock） | 每款 `FontCacheType`、composite sub-font 數、`DroidSansFallback` 是否存在、對 `髮 麵 隻 錶 闆 機` 逐字覆蓋率 |
| **3 FText 讀取** | FF7 Remake（4.18 fork）、Tower of Mask（4.27）、**STVoyager（5.6，UTF-8 顯示字串）**、Titan Quest II（5.7）、Solarpunk（5.7 stock） | `unresolved_ftext` 計數；跨四種 FText layout；STVoyager 上是否解出 `在维修期间继续探索星系` |
| **退化行為** | MindsEye（5.4.4 fork）、Avowed（packed item）、Persona 3 Reload（背景 idle） | 應**乾淨地回報功能不可用**，不崩潰、不靜默無作用；背景時 invoke 被 `IsGameThreadResponsive()` 擋下或提示啟用 Grausam |

---

## 附錄 — Offline 簡轉繁 / LLM 翻譯流程（零 DLL / 一頁 how-to）

對**絕大多數**使用者，這就是完整答案。全程不注入、不寫記憶體、零崩潰風險，重開遊戲仍在，且**可以連字型一起換**。

### 前置判斷

1. **引擎版本**：用本工具的版本偵測，或 FModel 開 `.pak` 看。
2. **pak 是否加密**：FModel 若要求 AES key 即為加密。有 key 才能繼續；無 key → 此路不通（也正是 in-memory 唯一勝出的窄 case 之一）。
3. **目標遊戲是否已有 zh-Hant**：決定走「新增 culture（loose file）」還是「覆蓋既有 culture（需 `_P.pak`）」。

### 步驟

1. **抽出語言包**
   - **UnrealLocres**（社群標準 CLI）：`UnrealLocres.exe export <Game>.locres` → 產出 CSV（`key,source` 兩欄）。
   - 或 **FModel** 瀏覽到 `<Game>/Content/Localization/<Game>/<culture>/<Game>.locres` 匯出。
2. **轉換 / 翻譯 source 欄**（保留 `key` 欄與所有格式佔位符 `{0}`、`<RichText>`、`\n` 不動）
   - **簡轉繁**：OpenCC `s2twp`（詞組優先、最長匹配）或繁化姬。
   - **LLM 翻譯（en/ja/zh-Hans → zh-Hant）**：把 CSV 的 source 欄批次送本機或**遠端 GPU（如 18 GB free VRAM 的另一台 PC）**的 LLM，遊戲關閉時滿速跑，無 frame budget、無 VRAM 爭用。務必：system prompt 鎖定「只翻譯、輸出等量行、保留佔位符」，並對輸出**無條件跑一次 OpenCC `s2twp` 後處理**（清掉模型漏出的簡體字）。
3. **重新匯入**：`UnrealLocres.exe import <Game>.locres translated.csv <out>.locres`。
4. **交付（二選一）**
   - **A. 遊戲原本沒有 zh-Hant** → 把 `<out>.locres` 以**新 culture 資料夾**放為 loose file：`<GameDir>/<Game>/Content/Localization/<Game>/zh-Hant/<Game>.locres`。因 `.locres` 不在 `ExcludedNonPakExtensions`，shipping 直接載入，**不需 `_P.pak`、不受 pak signing 影響**。
   - **B. 覆蓋既有 zh-Hans** → 必須打包成 mod pak `<Game>_P.pak`（shipping 下 loose file 無法遮蔽 pak 內既有路徑）。用 `UnrealPak` / repak / retoc（IoStore 遊戲）。
5. **強制語系**：`-culture=zh-Hant` 命令列、或遊戲內語言選單、或（Phase 1 若建成）本工具的 Locale switcher。
6. **字型（若缺字）**：若步驟 5 後繁體字顯示空白/豆腐，代表字型 cmap 不覆蓋 → 需一併 mod 字型（換 `.ufont` / `FSlateFontInfo` 指向的 face），這正是 offline 相對 in-memory 的決定性優勢。

### cache 即成品

翻譯過的 `.locres`（或 CSV）**就是** cache，per-game、可版本控管、可重用、可分享**給自己**。這比任何執行期記憶體快取都持久且乾淨——也是本評估對「LLM 翻譯」唯一認可的形態。

---

*Evaluated 2026-07-24 · build 2368 · 41-agent workflow, 12/12 load-bearing claims refuted or qualified · Phase 3 (FText read fix) shipped, Phases 1/2/4 designed-not-built, Phase 5 rejected.*
