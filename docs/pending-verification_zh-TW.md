# 待實機驗證清單（只驗證，不改程式碼）

> **這份是 [todo.md](todo.md) 的 `## Pending live-game verification` 一節的繁體中文版。**
> 英文版是**唯一正本**：兩邊有出入時以英文版為準，改動也請先改英文版。
> 操作程序（去哪裡 grep、每個 marker 落在哪個檔案）在
> [log-verification-checklist.md](log-verification-checklist.md)。
> 這份講的是**狀態**（⬜ / ✅），那份講的是**怎麼做**。

## 開 log 之前一定要知道的兩件事

1. **沒有 log level，什麼都不會被過濾** —— 所以 `[DEBUG]` 行也算數。
2. **See-Through / Foreground-Lock 的證據落在 `init-0.log`**，不在 `walk` / `pipe`，
   因為它們的分類在 `ResolveFile` 裡沒有對應，會 fall through。

Log 根目錄：`%LOCALAPPDATA%\UE5CEDumper\Logs`

**Grep 一律用「格式字串」，絕對不要用行號。** 2026-08 做過一次普查：每一條 Genau 的行號都
偏移了 12–14 行，而字串完全正確。

-----

## 分類規則（2026-08-04 訂定）

每一個 audit #4 的修正**在出貨當下**就要歸進下面兩組之一。
**沒有分組的項目 = 沒有人能行動的項目。**

| 組別 | 意思 |
|---|---|
| **① 可從 log 取得** | 讀一次正常 session 的 log 就能證明，或讀「為此特地新增的 log」。**優先用這種**：不需要特殊技巧，而且會留下證據。如果新增的 log 很重（per-object、per-tick），加它的那個 commit 必須講明，並標記驗完就移除。 |
| **② 一定要人工操作** | 需要有人在鍵盤前做 log 無法引發的事（一連串點擊、特定遊戲、特定第三方安裝）。每一項都附完整步驟和 PASS/FAIL 判準。 |

-----

## 建議的驗證順序

不用照清單順序做。按「要花多少力氣」排：

### 第 0 步 —— 完全免費（任何一次正常遊戲 session 之後 grep 就好）

這七項不需要你為它們做任何特別的事。玩完一輪、開 log、grep。

| 項目 | Grep 什麼 | 檔案 |
|---|---|---|
| B5 被動半 | `UE5_Init:` | `init-0.log` |
| B49 | `PipeServer: Stop entry` | `pipe-0.log` |
| B29 log 半 | `is loaded but is not ours` | `init-0.log` |
| B31 | 直接 `ls` log 目錄，不用 grep | `Logs\UE5DumpUI\` |
| B34 | `DllMain AutoStart:` | `init-0.log` |
| B47 | `first-loaded-wins guard is NOT armed` | `init-0.log` |
| B38 | 跑一次 proxy cleanup Report，看檔案落在哪 | — |

### 第 1 步 —— 需要刻意操作，但答案完全在 log 裡

B28、B8、B10、B4、B18、B19。

### 第 2 步 —— 純人工，要看畫面或看遊戲

② 那一整組。其中 **B14 + R5** 最值得先做：它是 build 2389 真的當掉過的重現步驟。

-----

# ① 可從 log 取得

## ⬜ B49 —— `Fern::Stop` 不再等一個永遠不會來的 client
**build 2569**

**已經內建量測** —— 這個修正出貨時就順便加了 per-phase logging，正是為了讓它不需要特別跑。

**怎麼做**：UI 連著正常玩，然後中斷 UI 連線，再把 CE 的 record 取消勾選。
grep `pipe-0.log` 的 `PipeServer: Stop entry` 以及後面接著的各階段行。

- **PASS** = `PipeServer: Stopped` 出現在 `Stop entry` 之後約 100 ms 內，而且
  `Stop conn drain` 寫的是 `satisfied`。
- **FAIL** = 根本沒有 `Stopped` 這行（就是舊的無限等待），或某個階段行顯示好幾秒。

> 舊版本**只**會記 `Stopped`，所以 `Stop entry` 存在本身也順便確認了你跑的是新 build。

## ⬜ B29（log 半）—— CE plugin 重複注入的防護會放行外來 wrapper
**build 2577**

任何用到 CE plugin 選單的 session：grep `init-0.log` 的 `is loaded but is not ours`。

這行只存在於新程式碼裡，而且正好只在以前會誤判的那個情況觸發。

- **PASS** = 該行點名了那個外來模組，而且注入照常進行。

> 人工的另一半（真的去裝一個 wrapper）在 ② 裡。

## ⬜ B31 —— UI log 到 8 MB 會輪替，不會停住
**build 2585**

任何長時間 session 之後都免費：`ls %LOCALAPPDATA%\UE5CEDumper\Logs\UE5DumpUI\`

- **PASS** = 某個分類超過 8 MB 之後，會出現 `pipe-0_001.log`（或類似命名）跟
  `pipe-0.log` 並存，而且最新那個檔案的最後一行時間是近期的。
- **FAIL** = 只有單一一個 `pipe-0.log`，大小剛好卡在 ~8 MB，最後一行很舊 ——
  這就是「無聲停寫」的特徵。

> 最快跑到 8 MB 的方法：Teleport → Auto refresh，開著放它跑。

## ⬜ B38 —— 殘留 proxy 的報告檔落在 app 資料夾裡面
**build 2585**

跑一次 proxy cleanup 的 Report。

- **PASS** = 檔案出現在 `%LOCALAPPDATA%\UE5CEDumper\Reports\`
- **FAIL** = 出現在 `%LOCALAPPDATA%\Reports\`

> 2585 之前寫出來的檔案會留在舊位置，這是設計如此，不算 FAIL。

## ⬜ B5（被動半）—— `UE5_Init` 的鎖沒有弄壞正常初始化
**build 2592** · 任何 session 都免費

grep `init-0.log` 的 `UE5_Init:`

- **PASS** = `Starting initialization...` 和 `Complete (UE…)` 嚴格一對一交替出現，
  而且兩條新行（`init already in progress`、`shutdown was requested during the scan`）
  都沒出現。
- **FAIL** = 有 `Starting` 卻沒有對應的 `Complete`（鎖死了 —— 理論上不該有任何情況能造成
  這個，正因如此才值得每個 session grep 一次），或連續兩行 `Starting`（還在競爭）。

> ⚠ **新行沒出現只證明「這次沒發生競爭」，不證明修好了。** 刻意觸發的版本在 ② 裡。

## ⬜ B34 —— Cheat Engine 本身不會被當成遊戲來掃描
**build 2603**

任何有註冊 CE plugin 的 session 都免費：grep `init-0.log` 的 `DllMain AutoStart:`

- **PASS** = 當 host 是 CE 時，會看到 `CE plugin host — skipping auto-start`（正常路徑，
  現在走得到是因為 `CEPlugin_GetVersion` 會宣告身分），或是新增的
  `host process is '…' — Cheat Engine is never a scan target`。
- **FAIL** = `game process — calling UE5_AutoStart`，而往上兩行的
  `UE5Dumper DLL loaded | … | process:` 寫的是 `cheatengine-x86_64.exe`。

> **要重現原本的競爭**：把 plugin 註冊好但**不要勾選**，然後啟動 CE。

## ⬜ B18 —— Extra Scan 可以被取消
**build 2603**

需要一個 GObjects **無法**用 AOB 解出來的遊戲，這樣 Extra Scan 才會真的跑很久。

**怎麼做**：讓它開始掃，然後在還在掃的時候取消勾選 CE 的 record（或關掉 UI）。

- **PASS** = `pipe-0.log` 顯示 `PipeServer: Stop watches+scan joins done` 出現在
  `Stop entry` 之後大約一秒內。
- **FAIL** = 中間隔了好幾秒，或者 CE 的視窗整個凍住直到掃描結束。

> 凍住的是 **CE** 而不只是遊戲，因為 `UE5_Shutdown` 是跑在 CE 自己的執行緒上。

## ⬜ B19 —— Log 保留機制不再卡在第一個刪不掉的檔案
**build 2603**

**怎麼刻意觸發**：
1. 用某個會持續佔用檔案的程式打開 `%LOCALAPPDATA%\UE5CEDumper\Logs\<proc>\*.log` 裡任一個封存檔。
2. 確保**同一個資料夾裡**至少還有另一個封存檔的日期超過 21 天（自己改檔案時間）。
3. 啟動有 DLL 的遊戲。

- **PASS** = 那個被改舊的檔案不見了，被佔用的那個還在。
- **FAIL** = 兩個都還在 —— 表示掃描在被佔用的那個檔案就中止了。

> 而且因為列舉順序是穩定的，舊版本**每一次啟動**都會停在同一個檔案。一個被鎖住的檔案就足以
> 讓 21 天保留機制從此失效。

## ⬜ B47 —— proxy 去重的防護會講出「它其實沒生效」
**build 2603**

任何 proxy session：grep `init-0.log` 的 `first-loaded-wins guard is NOT armed`

- **PASS** = 這行**不存在**（`Local\` + PID 成功了，而 `Global\` 需要遊戲沒有的權限）。

> 這行出現**不代表這個修正失敗** —— 它就是修正本身在回報一個以前完全無聲的狀況。
> 但如果真的出現了，值得追一下。

## ⬜ B28 —— CJK FText 不再顯示成 ASCII 亂碼
**build 2599** · 不需要 log，證據直接在畫面上

只影響 **FText 型別的值**（`ReadFTextString`）；FString 走的是純 UTF-16 的讀取路徑，從來沒有這個問題。

**怎麼做**：任何有中文／日文 UI 的遊戲 —— 把遊戲語言設成 CJK，在 Live Walker 或
Property Search 裡找一個 FText property。

- **PASS** = 值顯示為正常中文／日文。
- **FAIL** = 該顯示中文的地方變成一小串 ASCII 標點湯（`,{1`、`-N?e`）。

**特別要試**：字數是**偶數**、而且含有 `U+xx00` 字元的字串（一、第…一、統一）——
這正是觸發條件。

**反向確認（很重要）**：確認修正沒有往另一邊歪掉 ——
**Star Trek Voyager（UE5.6）** 的 FText 是用 UTF-8 存的，它的中文必須仍然正確。

## ⬜ B8 —— Fly / Noclip 不再把角色留在「穿透」狀態
**build 2596**

答案完全在 log 裡，而且觸發方式就是在「失焦會 idle」的遊戲上**關掉 Fly 的正常做法**。

**怎麼做**：
1. Teleport 分頁 → Fly ON + Noclip
2. 飛穿一道牆
3. **alt-tab 切到 UI**（等超過 500 ms，讓 ProcessEvent 安靜下來）
4. 點 Disable

grep `init-0.log` 的 `Fly:`

- **PASS** = 先看到
  `Fly: DISABLED but the pawn's collision is still OFF (game thread unresponsive)`，
  然後在你點回遊戲之後看到
  `Fly: game thread resumed after N ms — pawn collision restored`。
- **FAIL** = 舊的樣子：只有一行乾淨的 `Fly: DISABLED`，之後角色會掉出世界外。

**遊戲內佐證**：走去撞牆，應該要被擋住。

**第二個、更省事的檢查**（任何 Fly session 都可以）：
`Fly: collision disable deferred` 可能會出現，但**不可以重複** —— 它被限制成每次卡住只印一次。

## ⬜ B10 —— `WalkClassEx` 的 memo，效益已經自動被量測了
**build 2596**

Snapshot capture 本來就包在 `DiagnosticsProbe` 裡，所以**不需要新增任何 log**：
grep `pipe-0.log` 的 `PERF Snapshot capture`

- **PASS** = `wall … ms` 明顯低於 2596 之前的 build 上同樣的 capture
  （這個 memo 省掉了每個 struct-array **元素**一次 100–300 個 `FieldInfo` 的深拷貝），
  而且正確性不變 —— property grid 仍然顯示 struct 型別、enum 名稱、bool mask，
  這些正是 `WalkClassEx` 在 `WalkClass` 之上加的欄位。
- **FAIL** = 那幾欄變空白（表示 memo 端出了還沒 enrich 的項目），
  或平行掃描時當掉（表示交出去的 reference 被作廢 —— 這就是 `try_emplace` 要先做的原因）。

## ⬜ B4 —— CE mailbox 在 UI client 死掉之後還能活
**build 2592**

證據那行是**冷路徑** —— 每次 latch 只印一次，所以留著完全不花成本。
需要刻意的操作順序，但答案完整在 log 裡，所以歸在這組。

**怎麼做**：
1. 連上 UI
2. 開始一個很久的操作（Property Search deep，或完整的 Instance Finder 掃描）
3. **在它跑的時候把 UI 行程砍掉**
4. 用任何 CE 端的查詢 —— `.CT` 的 Find Instance，或在一個靠 class-scan fallback 的遊戲上
   按 teleport / GodMode 熱鍵

grep `pipe-0.log` 的 `per-command cancel is latched`

- **PASS** = 該 WARN 出現，**而且**接在它後面的那個指令回報的結果數不是零。
- **FAIL** = 舊的特徵：沒有 WARN，而查詢回答 `0` 並附帶 `scanned=<full pool>` ——
  就是這個訊息讓這個 bug 看起來像「那個物件不在」。

-----

# ② 一定要人工操作

## ⬜ B2 —— Symbol-export 的 GWorld 不再宣稱自己有 AOB
**build 2581**

這個 gate 已經對著出貨的 pattern table 做過單元測試，但需要一個 GWorld 真的是透過
symbol export 解出來的遊戲 —— **Satisfactory**（`?GWorld@@3VUWorldProxy@@A`，
見 [test-games.md](test-games.md)）。

**怎麼做**：掃描，然後看 CE-export / Standalone-Trainer 的 AOB 切換鈕。

- **PASS** = 切換鈕是灰的（不提供 AOB），而且匯出的表格裡的位址透過非 AOB 路徑正常解析。
- **FAIL** = 切換鈕可以按，而且匯出表格裡每一個位址都顯示 `??`。

> 一般 RIP-pattern 的遊戲沒什麼好檢查的 —— 那裡的行為跟以前一樣，這正是重點。

## ⬜ B29（人工半）—— CE plugin 防護遇到第三方 wrapper
**build 2577**

現在是用 PE ProductName 判斷歸屬，不是檔名。

**本機已用真實檔案驗證過**（我們的 5 個 binary 寫 `UE5CEDumper`；System32 的 4 個對應檔
寫 `Microsoft® …`），但真正促成這個修正的情況，本機沒有測試素材。

**怎麼做**：安裝 ReShade（或把任何第三方的 `dxgi.dll` / `dinput8.dll` wrapper 丟進 UE
遊戲資料夾），attach CE，點 *UE5CEDumper: Inject && Connect*。

- **PASS** = 正常注入，而且 DLL log 裡有 `'dxgi.dll' is loaded but is not ours`。
- **FAIL** = 舊的 *"already loaded … no injection needed"* 訊息，之後 UI 連不上。

**順便看一眼**：含非 ASCII 字元的遊戲路徑現在必須在那個訊息裡完整顯示
（以前會變成 `EVERSPACE? 2`）。

## ⬜ B13 / B41 —— 磁碟區沒有資源回收筒時會拒絕刪除
**build 2621**

需要一個資源回收筒被關掉的磁碟區。

**怎麼做**：
1. 找一個備用的固定磁碟區，`資源回收筒內容 → 不要將檔案移到資源回收筒`
2. 在上面放一個殘留的 proxy
3. 跑 orphan scan

- **PASS** = 該列被拒絕，理由是
  *"This volume has no working Recycle Bin … a delete here would be PERMANENT"*，
  而且確認對話框從頭到尾沒有提供「移到資源回收筒」的選項。
- **FAIL** = 該列可以執行，檔案永久消失，而狀態列卻寫著「moved to the Recycle Bin」。

**做完之後把資源回收筒重新開啟，確認同一列又變成可執行** —— 這後半段才能證明這個探測不是
單純什麼都拒絕。

## ⬜ B25 —— pre-4.11 的拒絕掃描不再靠單一個 PE 欄位就開火
**build 2621**

**怎麼做**：用 UE 版本 override 觸發，或用任何 PE ProductVersion 回報 4.0–4.10 的遊戲。

grep `scan-0.log` 的 `below the … floor — NOT accepting that on its own`

- **PASS** = 該行出現，而且掃描**照樣跑下去**（tier 3 → low confidence → gate 不會武裝）。
- **FAIL** = 一個能正常運作的遊戲卻出現 `SKIPPING the scan`。

**同時確認反方向還在**：真正的 pre-UE4（UE3）binary 仍然必須被拒絕，走 marker 路徑 ——
grep `PRE-UE4 engine POSITIVELY identified`。

## ⬜ B26 —— 重複的 GameEngine record 不再互相破壞
**build 2621**

**怎麼做**：
1. Teleport → Global Pointers → *Get GameEngine*，然後**再點一次**
   - **PASS** = 第二次會說「這個 session 已經推送過」並改成複製 XML，而不是再加一筆 record。
2. 把那段 XML 貼上去，**故意**製造出第二筆 record
3. 兩筆**都勾選**，然後取消勾選**比較舊**的那一筆
   - **PASS** = 新的那筆的 `UE_GameEngine` 仍然解得出來，chain 仍然讀得到
     （設 `UE5_DEBUG=1` 可以看到
     *"another record owns UE_GameEngine now — leaving it alone"*）
   - **FAIL** = 新的那筆的位址全變成 `??`

## ⬜ B16 —— 座標表格五個沒作用的排序欄位
**build 2610**

**怎麼做**：Teleport → Coordinate Library，至少 3 列資料。點 **X**、**Y**、**Z**、
**Yaw**、**Dist** 這五個欄位標題。

- **PASS** = 每一個都會重新排序。
- **FAIL** = 標題的箭頭動了，但列沒有動。

> ⚠ **必須在 publish（AOT / trimmed）的 build 上檢查** —— 整個缺陷就是被 trim 掉的 reflection
> metadata，所以單純 `dotnet run` **不會重現**。

Label / Group / Map 本來就是好的，現在也必須還是好的。

## ⬜ B42 —— 第二次啟動會把第一個視窗叫到前面
**build 2610**

**怎麼做**：跑 `dist\UE5DumpUI.exe`，然後再跑一次（雙擊 exe 或捷徑）。

- **PASS** = 既有的視窗跑到最前面 ——**包括它原本是最小化的時候**—— 而且沒有出現第二個視窗。
- **FAIL** = 看起來什麼都沒發生，那是舊行為。

**值得在第一個實例「已連線到遊戲」的狀態下測**，因為視窗標題會帶上模組名稱，
用標題搜尋的做法正好會在這個時候失效。

## ⬜ B36 —— 沒選任何列時的 Force 子選單
**build 2610**

**怎麼做**：Property Search → 執行一次搜尋 → **在列的下方空白處按右鍵**，
或在一個你沒有左鍵點過的列上按右鍵。

- **PASS** = 沒有 Force 子選單。
- 接著左鍵點一個 BoolProperty 的列再按右鍵：只出現 Force ON / OFF。
- **FAIL** = 四個動作同時出現。

> 需要先打開 Experimental 開關，子選單才會存在。

## ⬜ B14 + R5 —— 在 hold worker 還活著的時候關掉遊戲
**build 2596** · **建議優先做這一項**

這正是 build 2389 真的產生 `0xC0000409` 的重現步驟，現在拿來對那些當時還沒防護的迴圈再跑一次。

**怎麼做**：
1. 開啟**兩個**以前 worker 是裸的 hold —— Time Dilation（Hemmung）和 Move Speed（Laufen）
2. 再加上 See-through
3. **在遊戲處於背景時關掉 See-through**，讓它的 `PendingRestoreLoop` 真的在等待
4. 從遊戲自己的視窗關掉遊戲

- **PASS** = 沒有當機、沒有 WER minidump、Windows 應用程式事件記錄檔裡什麼都沒有。
- **FAIL** = 結束代碼 `0xc0000409`，而且錯誤堆疊在 `version.dll` 上 ——
  那就是有例外從 thread entry 逃出去了。

> 如果 `init-0.log` 裡有 `tick threw (…) — skipping (game tearing down?)`，表示防護有觸發並
> 且發揮作用；**它沒出現只證明這次沒有東西丟例外**。

*為什麼本機測不了：那個例外來自「在一個正在釋放記憶體的行程裡讀 UFunction」，
除了真正的遊戲關閉流程之外沒有辦法製造出來。*

## ⬜ B5（主動半）—— 刻意製造 `UE5_Init` 併發
**build 2592** · ① 裡被動檢查的主動版

需要用 **proxy** 啟動路徑，因為那才會讓第二個呼叫者變得可達：proxy 會在**沒有掃描**的情況下
就把 pipe 開起來，所以 pipe 已經活著時兩個 cached pointer 都還是 0。

**怎麼做**：
1. 用已部署的 proxy DLL 啟動遊戲
2. 連上 UI，點 Scan
3. **在掃描還在跑的時候**觸發任何 CE 端的 mailbox 指令（勾 `.CT`，或按 teleport 熱鍵）——
   那條路徑會呼叫 `Mimic::EnsureInitialized`，那就是第二個 `UE5_Init`

- **PASS** = `init-0.log` 顯示
  `init already in progress on another thread — tid=… is waiting`，
  接著
  `resumed after waiting (first caller succeeded — returning its result, no second scan)`，
  而且**只有一行** `Starting initialization...`，然後 CE 的指令正常運作。
- **FAIL** = 兩行 `Starting`，或者在一個 drill-down 顯示所有 property type 都 unknown 的
  session 裡卻出現 `validated=yes` 的摘要 —— 那就是這個修正要防的無聲毀損。

*為什麼本機測不了：需要兩條真實執行緒在一個活的遊戲裡競爭一個好幾秒的掃描；
單元測試只能釘住旗標語意，釘不住時序。*

## ⬜ `.CT` DLL 尋找 —— `reg.exe` 最近檔案的 fallback
**build 2576**

麵包屑那一半**✅ 已驗證**（跑一次 `UE5DumpUI.exe`，從 CE 的最近檔案選單開 `.CT`，
勾 `init` → DLL 找得到）。

registry 那一半**還沒被走過**：它只有在所有便宜的 slot 都落空時才會跑。

**怎麼做**：刪掉 `%LOCALAPPDATA%\UE5CEDumper\dll-path.txt`，從最近檔案開 `.CT`，勾 `init`。

- **PASS** = console 閃一下，DLL 找得到，slot 報告（設 `UE5_DEBUG=1`）歸功於
  *"folder of the most recent UE5CEDumper.CT in CE's recent-files list"*，
  **而且 `dll-path.txt` 被重新建立**，所以第二次勾選不會再閃。
- **FAIL** = 還是找不到，或每次都閃（自我修復的寫入沒有發生）。

*為什麼本機測不了：這是 CE Lua，`CtDllDiscoveryTests` 只能釘住結構。*

-----

## 附記：不是 audit #4、但同樣待驗證的項目

英文版 todo.md 同一節底下還有一段
**「Shipped + unit-tests-pass but unproven on real games」**，
內容是更早出貨、單元測試綠但沒在真實遊戲上證明過的功能
（Dump Explorer 跨遊戲身分 gate、Solide pool-truncation badge 等）。
那部分沒有翻譯 —— 它們不屬於 audit #4，驗證條件也寫在各自的段落裡。

另外還有一個**偶發測試**（`SnapshotViewModelTests.GroupMatch_MissingValue_ShowsErrorNoCandidates`），
2026-07-23 在完整平行測試中失敗過**一次**，之後單獨跑 25/25 三輪都過。
**沒有追** —— 觀察一次不等於重現。如果再發生，記下 `GroupCandidates` 是否非空、
或 `GroupStatusText` 是否為空，這兩者指向不同的原因。
