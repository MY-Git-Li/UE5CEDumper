# 待實機驗證清單（只驗證，不改程式碼）

> **這份是 [todo.md](todo.md) 的 `## Pending live-game verification` 一節的繁體中文版。**
> 英文版是**唯一正本**：兩邊有出入時以英文版為準，改動也請先改英文版。
> 操作程序（去哪裡 grep、每個 marker 落在哪個檔案）在
> [log-verification-checklist.md](log-verification-checklist.md)。
> 這份講的是**狀態**（⬜ / 🟡），那份講的是**怎麼做**。

> ⚠ **本檔只保留「還沒做完」的項目。** 驗過了的整條刪除 —— 它們的完整經過（含失敗的那幾輪）
> 留在 [todo.md](todo.md) 和 [dev-log.md](dev-log.md) 裡，不在這裡重複。
> 這裡的每一項都直接寫成**可以照做的步驟**。

-----

## 目前狀態（2026-08-06，build 2743）

> ### 🆕 build 2743：CE Lua mailbox 三個缺陷已修，兩個留著
>
> 詳見 [dev-log.md](dev-log.md) 2026-08-06。**這三個不是待驗證項目，是已修**：
> 逾時後 CE 勾選框仍打勾（7 份手抄的 wait loop **全部**都錯）、
> 逾時訊息猜錯原因（`status` 早就有答案）、
> 以及逾時實際是 **~155 秒**而不是宣稱的 10 秒。
>
> 最後那項由使用者在 CE Lua Engine 實測定案：`sleep(1)` = **15.47 ms**。
> **而且在兩顆 TDP 差很多的 CPU 上（9950X3D 桌機 / 9955HX3D 筆電）小數點後三位相同**，
> 所以那是 Windows 的 ~64 Hz 排程 tick，不是每台機器不同的效能偏移 ——
> 代表**每一個使用者**都吃到那 155 秒，不是單一機器的現象。
>
> **Teleport 原本就是對的，不要「修」它**：它的列是 momentary，靠 deferred timer
> 無條件 untick 並抑制關窗，套上 toggle 那招（提早 `return`）反而會弄壞它。
>
> 另外**兩個 Teleport 缺陷刻意留下**（要的是產品決定不是機械改動），
> 記在 [todo.md](todo.md) 的「CE Lua」段：
> `Get camera POV` / `Get current coords` 在預設 `DEBUG == 0` 下什麼都不顯示、
> 以及 `Clear all markers` 一次點擊可能跳三個對話框。

| 組別 | 剩幾項 | 內容 |
|---|---|---|
| ① 可從 log 取得 | 6 ⬜ + 1 🟡 | B4、B29（log 半）、B18、B19、B10、B28 反向確認、B8 🟡 |
| ② 一定要人工操作 | 6 ⬜ + 1 🔴 | B2、B29（人工半）、B25、B26、B5（主動半）、`.CT` registry fallback；~~B16~~ ✅、**B13/B41 🔴 驗出缺陷已修，端到端仍 ⬜** |
| ③ DumperTest 樣本 | **0** | ~~Shipping 包的畫面心跳~~ ✅ **根本不用重 cook** |
| ④ Vendor 更新 | **0** | ~~Z1 zydis~~ ✅ |

> ### 🆕 2026-08-12 這一輪：結掉三項，而且驗出一個真缺陷
>
> **完整證據一律在英文正本 [todo.md](todo.md)，這裡只記結論。**
>
> | 項目 | 結果 |
> |---|---|
> | **B16** 五個死掉的排序欄位 | ✅ 十個狀態（五欄 × 升降冪）全部符合**事前**寫下的預測。資料集刻意讓 X/Y/Z/Yaw/Dist 與插入順序induce **六種互不相同**的排序 —— 否則一個「什麼都沒做」的排序也會看起來像通過。**未測到 Group / Map**（`+ From fields` 讓 Group 全空、Map 全同）。 |
> | **③** 樣本 Shipping 心跳 | ✅ 五行都在。**而且不用重 cook** —— 磁碟上那包本來就是 HUD commit 之後 5 分鐘建的。`TickCount` +15 / 14.2 秒，F32÷10.25、F64÷0.25、RawDouble÷0.5 **四條獨立路徑都算出 15 跳**。 |
> | **Z1** zydis | ✅ 零 decode error、有函式 mapped props 非零。`instrs` 8–33 比 v5 的 17–65 低，但**那個 9 instrs 的函式正是唯一解出來的**，所以是 stock template 的 getter 短，不是 decoder 提早放棄。 |
> | **B13/B41** | 🔴 **驗出來是 FAIL** —— 探針看不見它自己命名的那個條件。已修（build 2799）+ 18 個單元測試。端到端（實際在 UI 上看到拒絕字串）仍 ⬜。 |
>
> **三個方法論教訓，比結果本身值錢：**
>
> 1. **「這台不能編 UE」擋了 ③ 一週，而結案的那個 binary 早就在 `For Testing\` 裡。**
>    接受任何「環境做不到」的阻塞之前，先比對產物的 mtime 和那個應該被編進去的 commit。
> 2. **B13/B41 根本不用開 UI。** 閘門在 `VolumeHasRecycleBin`，在每一列的上游 ——
>    量那個函式就結案了，在 Proxy Deploy 上點半天不會改變結果。
>    **先找閘門在哪裡，再決定要不要動 UI。**
> 3. **空的 grep 不是證據。** Z1 第一次查 log 是空的，差點被記成失敗 ——
>    DLL 還沒 flush（`offsets-0.log` 之後從 6,048 長到 7,885 bytes）。
>    先確認指令有送出去（`grep find_property_xrefs ui-pipe-0.log`），再從空結果下任何結論。

其中屬於 audit #4 的 `- ⬜` 條目是 **11 項**（與英文正本的計數一致）。

> **B4 的分類要看清楚：修正早就 ship 了（build 2592），⬜ 的是「驗證」。**
> 程式碼已核對過：`Tot.h:75-76` 的獨立旗標 `t_cancelImmune` + `MarkCancelImmune()`、
> `Tot.h:80` 讓 `MarkBackgroundWorker()` 同時設兩個（既有 worker 行為不變）、
> `Mimic.cpp:218` poller 只標記 cancel-immune、`Frieren.cpp:1650` 的 `IsBackgroundWorker()`
> 仍讀**另一個**旗標。正是 finding 要求的「獨立旗標」而不是那個一行版。

**2026-08-06 第二輪（SEED BATTLE DESTINY REMASTERED，build 2738）：register 一項都沒結掉。**
那個 session 是拿來驗 Live Walker 的 spine-step Back/Forward（已通過，不屬於本清單），
順手檢查 B4 卻是空跑 —— 原因寫在下面 B4 那一節，**不要把「WARN 沒出現」讀成 FAIL**。

**2026-08-06 對照現有 log 重掃的結果 —— 這一輪不用進遊戲就結掉了三項：**

| 項目 | 原本 | 現在 | 證據 |
|---|---|---|---|
| **D3** FUObjectItem stride | ⬜ 待重跑 Development 包 | ✅ **已驗證，整條刪除** | `DumperTest\offsets-*.log` **六次**都是 `FUObjectItem size detected as 32 bytes (200 items with valid names, 200 total valid, 0 bad)`，且 `init-*.log` 六次 `Name sanity: 10/10` |
| **drain straggler** | ⬜ 跑一次重現去看 phase | 🔧 **診斷結束，不再是驗證項目** | build 2657 的 phase 儀器**已經跑過三次**，三次都是 `parked in ReadFile`，見下面「已經不是驗證項目」 |
| **B8 第二個便宜檢查** | ⬜ | ✅ 這半邊過了 | `DumperTest-Win64-Shipping\walk-20260805-125518.log`：`collision disable deferred` 只出現 **1 次**（限流有效），1.0 秒後 `SetActorEnableCollision(0) invoked` |

另外 **B25 的反方向已經有證據**（`GG2Game\scan-0.log` 2026-07-30：
`PRE-UE4 engine POSITIVELY identified (4/4 markers, 2 needed) -> sentinel 300`），
所以 B25 只剩正方向那一半要測。

### ⚠ 建議順序（按力氣排，不必照清單順序）

| 順序 | 項目 | 力氣 | 為什麼排這裡 |
|---|---|---|---|
| **1** | **B4** | 中 | **唯一還會讓使用者看到無聲錯誤資料的項目**。失敗時查詢回 0 卻寫 `scanned=<全部>`，看起來像「物件不存在」，CE-only session 會一直壞下去 |
| **2** | **B16** | 極小 | AOT 包裡點五個欄位標題，兩分鐘 |
| **3** | **B26** | 小 | 兩次點擊 + 貼一段 XML |
| **4** | **B5**（主動半） | 小 | proxy 啟動、掃描途中按一次 CE 熱鍵 |
| **5** | **`.CT` registry fallback** | 小 | 刪一個檔、勾一次 `init` |
| **6** | **Z1** | 小 | 注入任一 UE 遊戲 + 一次 Path-2 xref |
| **7** | **B18** / **B19** | 中 | 各需要一個刻意佈置的前提 |
| **8** | **B29**（兩半一起） | 中 | 要裝 ReShade 之類的第三方 wrapper |
| **9** | **B13 / B41** | 中 | 要有一個關掉資源回收筒的磁碟區 |
| **10** | **B2** / **B25** / **B28 反向** | 看手邊有沒有那款遊戲 | 各自綁死一款特定遊戲（Satisfactory / 4.0–4.10 / STVoyager） |
| **11** | **B8** · **B10** · **樣本心跳** | 卡住 | 三項都卡在外部條件，見各自段落 |

### 驗證時的兩條鐵則（B34 與 B14 各花了三輪換來的）

1. **用「清單」寫出來的修正，拿同一份清單去驗，等於沒驗。**
   B34 列了三個 CE 檔名，CE 實際叫 `cheatengine-x86_64-SSE4-AVX2.exe`；
   B14 列了七個 thread proc，DLL 實際上約有 15 個。兩者對自己的清單都是對的，對世界是錯的。
2. **一個東西「沒出現」，只有在你先證明「會產生它的程式碼有被編進去而且有跑」之後才算證據。**
   B47 的 ✅ 曾經記在一個那段程式碼根本沒被編進去的手動注入 session 上。
3. **修正沒生效時，先回去讀證據，不要急著加更多同一種修正。**
   drain straggler 連續三次都是同一個機制的變體，而三次的證據都在說「不是這個機制」。

-----

## 開 log 之前一定要知道的四件事

1. **沒有 log level，什麼都不會被過濾** —— 所以 `[DEBUG]` 行也算數。
2. **See-Through / Foreground-Lock 的證據落在 `init-0.log`**，不在 `walk` / `pipe`，
   因為它們的分類在 `ResolveFile` 裡沒有對應，會 fall through。
3. **Grep 一律用「格式字串」，絕對不要用行號。** 2026-08 做過一次普查：每一條 Genau 的行號都
   偏移了 12–14 行，而字串完全正確。
4. **檔名是由 `LOG_CAT` 決定的，不是由「感覺這屬於哪一類」決定的。**
   2026-08-06 抓到三個寫錯的（`Fly:` 一直被寫成在 `init-0.log`，實際在 **`walk-0.log`**）。
   下表是從 `Sein.cpp` 的 `s_catMap` 讀出來的，照它 grep：

| 你要找的東西 | `LOG_CAT` | **檔案** |
|---|---|---|
| `UE5_Init:` / `DllMain` / `Loaded real …` | `INIT` | `init-0.log` |
| CE plugin（`is loaded but is not ours`） | `CEP` | `init-0.log` |
| `Fly:` 全部 | `FLY` | **`walk-0.log`** |
| `ObjectArray:` / `AnalyzeNativeFunctionProps` / `FindPropertyXrefs` | `OARR` | **`offsets-0.log`** |
| `PipeServer:` / `Mailbox:` | `PIPE` | `pipe-0.log` |
| `DetectVersion:` / AOB 掃描 | `SCAN` | `scan-0.log` |
| `PERF …`（UI 端量測） | — | **`UE5DumpUI\view-0.log`**（遊戲資料夾裡也有一份 `ui-view-*.log`） |

Log 根目錄：`%LOCALAPPDATA%\UE5CEDumper\Logs`

-----

## 分類規則（2026-08-04 訂定）

每一個 audit #4 的修正**在出貨當下**就要歸進下面兩組之一。
**沒有分組的項目 = 沒有人能行動的項目。**

| 組別 | 意思 |
|---|---|
| **① 可從 log 取得** | 讀一次正常 session 的 log 就能證明，或讀「為此特地新增的 log」。**優先用這種**：不需要特殊技巧，而且會留下證據。如果新增的 log 很重（per-object、per-tick），加它的那個 commit 必須講明，並標記驗完就移除。 |
| **② 一定要人工操作** | 需要有人在鍵盤前做 log 無法引發的事（一連串點擊、特定遊戲、特定第三方安裝）。每一項都附完整步驟和 PASS/FAIL 判準。 |

-----

# ① 可從 log 取得

## ⬜ B4 —— CE mailbox 在 UI client 死掉之後還能活
**build 2592** · 力氣 **中** · **最優先**

失敗時是**無聲的**：查詢回 0 卻附帶 `scanned=<全部>`，讀起來像「那個物件不在」。
證據那行是**冷路徑**（每次 latch 只印一次），所以留著完全不花成本。

**執行步驟**
1. 啟動遊戲（DLL 已注入或走 proxy），連上 UI。
2. 開始一個會跑很久的操作 —— Property Search 打開 **Deep** 搜一個常見字，
   或 Instance Finder 對 `Actor` 做一次完整掃描。
3. **趁它還在跑**，強制殺掉 `UE5DumpUI.exe`。**唯一可靠的一行**：

   ```
   taskkill /F /IM UE5DumpUI.exe
   ```

   （`/F` 是關鍵。視窗關閉鈕、工作管理員「處理程序」分頁的「結束工作」都**不算**，
   兩者都會先給程式乾淨收尾的機會 —— 這裡要模擬的是 client 猝死。）
4. 回到 CE，做任何一個 CE 端查詢：`.CT` 的 Find Instance，
   或在一款靠 class-scan fallback 的遊戲上按 teleport / GodMode 熱鍵。
5. `grep "per-command cancel is latched" pipe-0.log`
   （完整那行是 `Mailbox: cmd=N runs while a pipe client's per-command cancel is latched
   — this thread is cancel-immune, so lookups still scan (B4)`；
   抓不到的話用 `grep "(B4)"`。）

- **PASS** = 該 WARN 出現，**而且**接在它後面的那個指令回報的結果數**不是零**。
- **FAIL** = 沒有 WARN，而查詢回答 `0` 並附帶 `scanned=<full pool>`。

> ### ⚠ 工作管理員「處理程序」分頁的「結束工作」殺不掉它 —— 2026-08-06 實測
>
> 那顆按鈕會**先送 `WM_CLOSE`**，只有在程式沒反應時才升級成強制終止。所以一個還在回應的
> UI 會**正常關閉**，`g_perCommand` 永遠不會被 latch，整個測試變成空跑。
> 要**「詳細資料」分頁 →「結束處理程序」**，或 `taskkill /F /IM UE5DumpUI.exe`。
>
> **實測證據**（SEED BATTLE DESTINY REMASTERED，build 2738，就是這樣關的）：
> - UI 仍然寫出了 `UE5DumpUI shutting down...` —— 這行 `TerminateProcess` **寫不出來**
> - 伺服器端 `Stop entry (conns=0)`、
>   `Stop conn drain satisfied, 0 left (0 ms, **0 cancel re-asserts**)`
>
> **所以「WARN 沒出現」不等於 FAIL。** 先看上面那兩行，才能分辨
> 「防護有效」和「這次根本沒測到」。
>
> 另一半條件同樣重要：UI 死掉的**當下必須有長時間操作正在跑**。
> 那個 session 最後一筆 pipe 流量在關閉前 40 秒，根本沒有指令可以讓
> disconnect monitor 去 latch 一個 cancel。

> ### ⚠ 先確認「有沒有武裝」—— `client gone mid-command` —— 2026-08-06 第二次實測
>
> latch 自己有一行 WARN，就印在設定 latch 的前一行
> （[`Fern.cpp:769`](../dll/src/Fern.cpp:769)）：
> `client gone mid-command (err=…) — aborting in-flight op`。
> **要先 grep 這一行，再去 grep B4 那一行。**
> 抓不到 ⇒ `g_perCommand` 根本沒被 latch ⇒ B4 的 WARN 不印是**正確**的，這次什麼都沒測到。
> 只有這行在的時候，「B4 那行沒出現」才有意義。
>
> ### 軸不是「久」，是「單一次呼叫卡住好幾秒」
>
> `MonitorLoop` 每 **200 ms** 輪詢一次（[`Fern.cpp:732`](../dll/src/Fern.cpp:732)），
> 而且只 peek `inFlight` 為真的連線（`:743`），所以那個指令必須在輪詢落下的**那一刻**還在跑。
> **分頁 / 串流式的操作再久都武裝不了它** —— 那是幾千個短指令，中間全是空隙。
>
> 兩個看起來最像正解、實際上都是陷阱（2026-08-06 各燒掉一次實測）：
> - **Dump All Metadata** —— `DumpAllService` 是
>   `GetObjectListAsync(offset, pageSize)` 的 `do/while`
>   （[`DumpAllService.cs:115-133`](../ui/UE5DumpUI/Services/DumpAllService.cs:115)），
>   加上每批 200 個的 `WalkClassesBatchAsync`（`:262`）。
>   **實測每頁間隔 50–80 ms**（19:45:16.124 → .201 → .249 → .323），輪詢一次都沒抓到。
>   最後是連線自己的寫入先發現 client 死了 —— 同一毫秒內
>   `Failed to write response` → `Client disconnected` —— latch 完全沒設。
> - **Snapshot capture** —— `Renge.h:161-165` 直接寫明：`begin_snapshot` + `snapshot_chunk`
>   串流 `[offset, offset+limit)`，**「like get_object_list」**。同一個形狀，同樣空跑。
>
> 要改用**單一個阻塞式掃描**。它們全在 `Aura.cpp` —— 該檔案握有 DLL 裡 30 處
> `Tot::Requested()` 檢查，正是因為這些才是被設計成會跑很久的：
>
> | 指令 | UI 位置 | 為什麼久 |
> |---|---|---|
> | `begin_value_scan` | Value Search 第一次掃描 | 每物件 × 每屬性，預設最重 |
> | `find_path_from_gworld` | 🌍 Locate in GWorld | BFS，工具列 **depth 滑桿**是直接的成本旋鈕 |
> | `find_refs_to_uobject` | Live Walker → Find Refs | 反向掃全池，含巢狀 struct/container |
> | `find_instances` | Instance Finder | 全池掃描 |
>
> 物件池小的時候（SEED BATTLE：69,688 個）連這些都可能很快跑完 ——
> 所以要看的是**那行武裝訊息**，不是碼錶。
> 四個裡面只有 **Locate in GWorld** 有可以一直往上轉到夠慢的旋鈕。

## ⬜ B29（log 半）—— CE plugin 重複注入的防護會放行外來 wrapper
**build 2577** · 力氣 **中**（要先有 wrapper，見 ② 的人工半）

**執行步驟**
1. 先照 ② 的 B29 把第三方 wrapper（ReShade / 任何 `dxgi.dll`、`dinput8.dll`）放進遊戲資料夾。
2. attach CE，用 CE 的 plugin 選單點 *UE5CEDumper: Inject && Connect*。
3. `grep "is loaded but is not ours" init-0.log`

- **PASS** = 該行點名了那個外來模組（例如 `'dxgi.dll' is loaded but is not ours`），
  而且注入照常進行。
- **FAIL** = 出現舊的 *"already loaded … no injection needed"*，之後 UI 連不上。

> 這行只存在於新程式碼裡，而且正好只在以前會誤判的那個情況觸發。
> 目前所有 log 裡 **0 次** —— 因為沒人裝過 wrapper，不是因為它壞了。

## ⬜ B18 —— Extra Scan 可以被取消
**build 2603** · 力氣 **中**（要挑對遊戲）

**前置條件**：需要一款 **GObjects 無法用 AOB 解出來**的遊戲，Extra Scan 才會真的跑很久。
（看 `scan-0.log`：GObjects 走到 data-scan fallback 的那種。）

**執行步驟**
1. 注入該遊戲，讓 Extra Scan 開始跑。
2. **在它還在掃的時候**取消勾選 CE 的 record（或直接關掉 UI）。
3. `grep -E "Stop entry|Stop watches\+scan joins done" pipe-0.log`，比對兩行的**時間差**。

- **PASS** = `PipeServer: Stop watches+scan joins done` 出現在 `Stop entry` 之後**大約一秒內**。
- **FAIL** = 中間隔了好幾秒，或者 CE 的視窗整個凍住直到掃描結束。

> ⚠ **這行的「存在」什麼都不能證明** —— 現有 27 個 log 檔都有它。
> 要測的是 `Stop entry` 到它之間的**間隔**，而且必須是在掃描進行中觸發的那一次。
> 凍住的是 **CE** 而不只是遊戲，因為 `UE5_Shutdown` 是跑在 CE 自己的執行緒上。

## ⬜ B19 —— Log 保留機制不再卡在第一個刪不掉的檔案
**build 2603** · 力氣 **中**

**執行步驟**
1. 挑一個遊戲的 log 資料夾，例如
   `%LOCALAPPDATA%\UE5CEDumper\Logs\DQ7R-Win64-Shipping\`。
2. 用一個會**持續佔用檔案**的程式打開裡面任一個封存檔
   （例如用 `powershell -c "$f=[IO.File]::Open('<路徑>','Open','Read','None'); Read-Host"`，
   保持那個視窗開著）。
3. 在**同一個資料夾裡**挑另一個封存檔，把它的時間改成 21 天以前：
   `(Get-Item '<路徑>').LastWriteTime = (Get-Date).AddDays(-30)`
4. 確認被佔用的那個檔案**排在被改舊的那個前面**（列舉順序是穩定的 —— 舊版就是停在第一個）。
5. 啟動有 DLL 的遊戲。

- **PASS** = 那個被改舊的檔案不見了，被佔用的那個還在。
- **FAIL** = 兩個都還在 —— 表示掃描在被佔用的那個檔案就中止了。

> 一個被鎖住的檔案就足以讓 21 天保留機制從此永遠失效，因為每一次啟動都會停在同一個檔案。

## ⬜ B10 —— `WalkClassEx` 的 memo
**build 2596** · 力氣 **卡住** · **卡在沒有「對照組」，不是卡在量測**

Snapshot capture 本來就包在 `DiagnosticsProbe` 裡，**不需要新增任何 log**。
問題是所有留存的量測**全部都在修正之後**，沒有 2596 以前的數字可比：

| 遊戲 | wall | 檔案 |
|---|---|---|
| Elliot | 41,733.6 ms | `ui-view-20260804-220425.log` |
| DQ7R | 5,256.2 ms | `ui-view-20260804-210254.log` |
| DumperTest | 760.2 / 509.0 ms | `ui-view-20260805-125034.log` |

**兩個可行做法，挑一個：**

- **(A) 把上表當成新基準。** 之後在**同一個遊戲、同一份 snapshot** 再抓一次，
  `grep "PERF Snapshot capture" UE5DumpUI\view-0.log` 比 `wall`。
- **(B) 只驗正確性那一半**（不需要對照組，隨時可做）：抓一份 snapshot 後打開 property grid，
  確認 **struct 型別 / enum 名稱 / bool mask** 三欄仍然有值 ——
  這三個正是 `WalkClassEx` 在 `WalkClass` 之上加的欄位。

- **PASS (A)** = `wall … ms` 明顯低於同一遊戲同一份 snapshot 在 2596 之前的數字。
- **PASS (B)** = 上述三欄都有值。
- **FAIL** = 那幾欄變空白（memo 端出了還沒 enrich 的項目），
  或平行掃描時當掉（交出去的 reference 被作廢 —— 這就是 `try_emplace` 要先做的原因）。

## 🟡 B8 —— Fly / Noclip 不再把角色留在「穿透」狀態
**build 2596** · **主要路徑 ✅ · 便宜的第二檢查 ✅（2026-08-06 補上）· 延後路徑仍 ⬜，而且卡住**

已驗證的兩半：

- **主要路徑**（Elliot 2026-08-04 22:01）：`Fly: worker stopped` →
  `Fly: SetActorEnableCollision(1) invoked` → `Fly: DISABLED`。先 join 再還原，
  而且還原是從 invoke **真的執行了**才記錄狀態。
- **限流**（DumperTest 2026-08-05 12:54）：`Fly: collision disable deferred` 只出現 **1 次**，
  1.0 秒後 `SetActorEnableCollision(0) invoked` —— 重試到遊戲執行緒回應為止，而且沒有重複洗版。

> ### ⚠ 還沒驗的那一半：**關遊戲永遠測不到**
>
> 關掉遊戲時 `UE5_Shutdown` **根本不會被呼叫**（整份 log 裡 `UE5_Shutdown: Cleaning up`
> 出現 0 次），所以 `Dunste::SetEnabled(false)` 完全沒有執行，
> `DISABLED but the pawn's collision is still OFF` 這行永遠不可能印出來。
>
> 延後路徑需要的是**在遊戲執行緒安靜的時候按下 Disable 按鈕**，
> 也就是需要一款**失焦時真的會 idle** 的遊戲（`t.IdleWhenNotForeground`）。Elliot 不會。
>
> **而且目前還卡在另一件事**：stock UE 5.4 上重現的 PE hook 誤判
> （`VALIDATION FAILED … fired 0 times`）尚未解決，**B8 卡在它後面**。

**等那兩個條件到齊之後的執行步驟**
1. Teleport 分頁 → Fly ON + Noclip
2. 飛穿一道牆
3. **alt-tab 切到 UI**，等超過 500 ms，讓 ProcessEvent 安靜下來
4. 點 Disable
5. `grep "Fly:" walk-0.log` ← **不是 `init-0.log`**

- **PASS** = 先看到
  `Fly: DISABLED but the pawn's collision is still OFF (game thread unresponsive)`，
  然後在你點回遊戲之後看到
  `Fly: game thread resumed after N ms — pawn collision restored`。
- **FAIL** = 舊的樣子：只有一行乾淨的 `Fly: DISABLED`，之後角色會掉出世界外。
- **遊戲內佐證**：走去撞牆，應該要被擋住。

> 如果一直找不到會 idle 的遊戲，把這一項結成「接受未驗證」是合理的：
> 這條程式路徑跟 Schlacht 從 build 2364 就在跑的是同一條，而且另外兩半都驗過了。

## ⬜ B28 反向確認 —— STVoyager 的 UTF-8 FText 中文仍然要正確
**build 2599** · 力氣 **小**（前提是手邊有這款遊戲）· 不需要 log，證據直接在畫面上

> **B28 本體已於 2026-08-05 在 DumperTest 樣本上 ✅ 驗證**（八個 FText 欄位全部正確，
> 含 ASCII 對照組與 `FTextHistory` 對照組），整條已從本檔刪除。
> **只剩這個反方向的對照** —— 確認修正沒有往「一律當成 UTF-16」歪過去。

**執行步驟**
1. 啟動 **Star Trek Voyager（UE5.6）**，語言設成中文，注入 DLL。
2. Property Search 找一列 Type 欄位真的寫著 **`TextProperty`** 的（UI／對話／道具說明類別）。
   ⚠ `StrProperty` 不算 —— 那是 FString，走純 UTF-16 路徑，從來沒有這個 bug。
3. 看值。

- **PASS** = 中文正常顯示。
- **FAIL** = 變成一小串 ASCII 標點湯（`,{1`、`-N?e`）。

> STVoyager 的 FText 是用 **UTF-8** 存的（licensee 特例），所以它是這個修正唯一的反向對照。

-----

# ② 一定要人工操作

## ✅ B16 —— 座標表格五個沒作用的排序欄位
**build 2610** · **已於 2026-08-12 驗證通過（AOT build 2794 + DumperTest）**

> 十個狀態全中，**但 Group / Map 這一半沒測到** —— `+ From fields` 讓 Group 全空、Map 全同，
> 沒有順序可以觀察。要補的人：在列編輯器把 Group 設成不同值再點一次。
> 完整證據見 [todo.md](todo.md)。下面的步驟保留給重測用。

> ⚠ **必須在 publish（AOT / trimmed）的 build 上檢查** —— 整個缺陷就是被 trim 掉的 reflection
> metadata，所以單純 `dotnet run` 或非 trimmed build **不會重現**。
> 用 `dist\UE5DumpUI.exe`，並先確認它是 `-Mode Publish` 出來的那一份。

**執行步驟**
1. 跑 `dist\UE5DumpUI.exe`
2. Teleport → Coordinate Library，準備**至少 3 列**資料（隨便存三個座標）
3. 依序點 **X**、**Y**、**Z**、**Yaw**、**Dist** 五個欄位標題，每個點兩次（升冪／降冪）

- **PASS** = 五個都會重新排序，而且點第二次會反向。
- **FAIL** = 標題的箭頭動了，但列沒有動。
- **同時確認沒有回歸**：Label / Group / Map 本來就是好的，現在也必須還是好的。

## ⬜ B26 —— 重複的 GameEngine record 不再互相破壞
**build 2621** · 力氣 **小**

**執行步驟**
1. Teleport → Global Pointers → *Get GameEngine*
2. **再點一次同一個按鈕**
   - **PASS** = 第二次會說「這個 session 已經推送過」並改成複製 XML，而不是再加一筆 record。
3. 把剪貼簿那段 XML 貼進 CE，**故意**製造出第二筆 record
4. 兩筆**都勾選**
5. 取消勾選**比較舊**的那一筆
   - **PASS** = 新的那筆的 `UE_GameEngine` 仍然解得出來，chain 仍然讀得到。
     （在 CE 的 Lua console 設 `UE5_DEBUG=1` 可以看到
     *"another record owns UE_GameEngine now — leaving it alone"*）
   - **FAIL** = 新的那筆的位址全變成 `??`

## ⬜ B5（主動半）—— 刻意製造 `UE5_Init` 併發
**build 2592** · 力氣 **小** · ①（被動半）已驗證並刪除，這是主動版

**前置條件**：一定要用 **proxy** 啟動路徑。proxy 會在**沒有掃描**的情況下就把 pipe 開起來，
所以 pipe 已經活著時兩個 cached pointer 都還是 0 —— 第二個呼叫者才變得可達。
手動注入**測不到**這一項。

**執行步驟**
1. 用已部署的 proxy DLL（`version.dll` / `dinput8.dll` / `dxgi.dll` / `winmm.dll`）啟動遊戲
   —— 確認 `init-0.log` 有 `DllMain ProxyStart`，否則走錯路徑了
2. 連上 UI，點 **Scan**
3. **在掃描還在跑的時候**觸發任何 CE 端的 mailbox 指令
   （勾 `.CT`，或按 teleport 熱鍵）—— 那條路徑會呼叫 `Mimic::EnsureInitialized`，
   那就是第二個 `UE5_Init`
4. `grep "UE5_Init:" init-0.log`

- **PASS** = 依序出現
  `init already in progress on another thread — tid=… is waiting`
  → `resumed after waiting (first caller succeeded — returning its result, no second scan)`，
  而且**只有一行** `Starting initialization...`，然後 CE 的指令正常運作。
- **FAIL** = 兩行 `Starting`，或者在一個 drill-down 顯示所有 property type 都 unknown 的
  session 裡卻出現 `validated=yes` 的摘要 —— 那就是這個修正要防的無聲毀損。

*為什麼本機測不了：需要兩條真實執行緒在一個活的遊戲裡競爭一個好幾秒的掃描；
單元測試只能釘住旗標語意，釘不住時序。*

## ⬜ `.CT` DLL 尋找 —— `reg.exe` 最近檔案的 fallback
**build 2576** · 力氣 **小**

麵包屑那一半**✅ 已驗證**。registry 那一半還沒被走過：它只有在**所有便宜的 slot 都落空**時才會跑。

**執行步驟**
1. 刪掉 `%LOCALAPPDATA%\UE5CEDumper\dll-path.txt`
2. 從 CE 的**最近檔案**選單開 `UE5CEDumper.CT`（不要用「開啟舊檔」去挑 —— 要走 recent-files）
3. 勾 `init`
4. 在 CE 的 Lua console 先設 `UE5_DEBUG=1` 才看得到 slot 報告

- **PASS** = console 閃一下，DLL 找得到，slot 報告歸功於
  *"folder of the most recent UE5CEDumper.CT in CE's recent-files list"*，
  **而且 `dll-path.txt` 被重新建立** —— 所以第二次勾選不會再閃。
- **FAIL** = 還是找不到，或每次都閃（自我修復的寫入沒有發生）。

*為什麼本機測不了：這是 CE Lua，`CtDllDiscoveryTests` 只能釘住結構。*

## ⬜ B29（人工半）—— CE plugin 防護遇到第三方 wrapper
**build 2577** · 力氣 **中**

現在是用 PE ProductName 判斷歸屬，不是檔名。
**本機已用真實檔案驗證過**（我們的 5 個 binary 寫 `UE5CEDumper`；System32 的 4 個對應檔
寫 `Microsoft® …`），但真正促成這個修正的情況，本機沒有測試素材。

**執行步驟**
1. 安裝 ReShade 到一款 UE 遊戲，或直接把任何第三方的 `dxgi.dll` / `dinput8.dll` wrapper
   丟進遊戲資料夾
2. 啟動遊戲，attach CE
3. 點 *UE5CEDumper: Inject && Connect*
4. 順便挑一款**路徑含非 ASCII 字元**的遊戲來做（見下）

- **PASS** = 正常注入，而且 `init-0.log` 有 `'dxgi.dll' is loaded but is not ours`。
- **FAIL** = 舊的 *"already loaded … no injection needed"* 訊息，之後 UI 連不上。
- **順便看一眼**：含非 ASCII 字元的遊戲路徑現在必須在那個訊息裡**完整顯示**
  （以前會變成 `EVERSPACE? 2`）。

## 🔴 B13 / B41 —— 磁碟區沒有資源回收筒時會拒絕刪除
**build 2621** · **2026-08-12 驗出來是 FAIL，已修（build 2799）；端到端仍 ⬜**

> **這一項根本不需要開 UI**，而這正是最值錢的教訓：閘門是
> `WindowsPlatformService.VolumeHasRecycleBin`，在每一列的**上游**。
> 它原本只靠 `SHQueryRecycleBin(root) == S_OK` 判斷，而那個 API 查的是**回收筒的內容**、
> 不是**政策** —— 在 `NukeOnDelete=1` 的固定磁碟區上照樣回 S_OK（因為 `$RECYCLE.BIN`
> 資料夾和舊項目還在），所以拒絕永遠不會觸發。
>
> 用拋棄式檔案實測（`SHFileOperation` + `FOF_ALLOWUNDO`，**與 `MoveToRecycleBin` 完全相同**）：
> `rc=0`、`aborted=false`、回收筒項目數 **5 → 5 沒變**、**檔案不見了** ——
> 呼叫端會回報「moved to the Recycle Bin」，而檔案已被永久銷毀。
>
> **修法**：改成先讀登錄檔政策（純函式 `RecycleBinPolicy`，涵蓋 Group Policy →
> `UseGlobalSettings` → per-volume `NukeOnDelete`，且**「不存在」≠ 0**），
> `SHQueryRecycleBin` 降為第二道閘門。18 個單元測試。
> 修後實測：T:（`NukeOnDelete=1`）→ 拒絕；C:/D:（=0）→ 放行，
> 而且 **D: 當時回收筒是空的** —— 「已啟用但空」必須仍讀成有回收筒，
> 任何用項目數判斷的實作都會在這裡壞掉。
>
> ⬜ **還沒做的**：實際在 UI 上種一個殘留、看到那串拒絕文字。
> 登錄檔管線已對三個真實磁碟區驗過、政策有 18 個測試，中間只剩 6 行膠水沒被走過。

**執行步驟**
1. 找一個**備用的固定磁碟區**（不要拿系統碟做），
   `資源回收筒內容 → 該磁碟區 → 不要將檔案移到資源回收筒`
2. 在上面隨便造一個假的殘留 proxy（複製一份 `version.dll` 到一個像遊戲資料夾的目錄）
3. UI → Proxy Deploy → **Find leftovers**，讓那一列被掃出來
4. 嘗試對那一列執行刪除

- **PASS** = 該列被拒絕，理由是
  *"This volume has no working Recycle Bin … a delete here would be PERMANENT"*，
  而且確認對話框從頭到尾**沒有**提供「移到資源回收筒」的選項。
- **FAIL** = 該列可以執行，檔案永久消失，而狀態列卻寫著「moved to the Recycle Bin」。

5. **做完之後把資源回收筒重新開啟，重掃，確認同一列又變成可執行**
   —— 這後半段才能證明這個探測不是單純什麼都拒絕。

## ⬜ B25 —— pre-4.11 的拒絕掃描不再靠單一個 PE 欄位就開火
**build 2621** · 力氣 **中**（正方向要有對的遊戲）

> **反方向已經有證據，不用再測**：`GG2Game\scan-0.log`（2026-07-30）有
> `DetectVersion: PRE-UE4 engine POSITIVELY identified (4/4 markers, 2 needed) -> sentinel 300.
> The AOB scan will be skipped, not attempted.` —— 真正的 UE3 仍然被正確拒絕。

**只剩正方向：一個能正常運作、但 PE ProductVersion 謊報 4.0–4.10 的遊戲。**

**執行步驟**
1. 用 UE 版本 override 觸發，或直接用任何 PE ProductVersion 報 4.0–4.10 的遊戲
2. `grep "NOT accepting that on its own" scan-0.log`

- **PASS** = 該行出現（完整是 `… below the … floor — NOT accepting that on its own`），
  而且掃描**照樣跑下去**（tier 3 → low confidence → gate 不會武裝）。
- **FAIL** = 一個能正常運作的遊戲卻出現 `SKIPPING the scan`。

## ⬜ B2 —— Symbol-export 的 GWorld 不再宣稱自己有 AOB
**build 2581** · 力氣 **看有沒有那款遊戲**

**前置條件**：需要一款 GWorld 真的是透過 **symbol export** 解出來的遊戲 ——
**Satisfactory**（`?GWorld@@3VUWorldProxy@@A`，見 [test-games.md](test-games.md)）。
一般 RIP-pattern 的遊戲沒什麼好檢查的 —— 那裡的行為跟以前一樣，這正是重點。

**執行步驟**
1. 注入 Satisfactory，等掃描完成
2. 去 CE-export 或 Standalone-Trainer，看 **AOB 切換鈕**
3. 匯出一份表格，看裡面的位址

- **PASS** = 切換鈕是**灰的**（不提供 AOB），而且匯出表格裡的位址透過非 AOB 路徑正常解析。
- **FAIL** = 切換鈕可以按，而且匯出表格裡每一個位址都顯示 `??`。

-----

# ③ DumperTest 自建樣本

> 來源是 2026-08-05 自建的 UE 5.4 `DumperTest` 樣本
> （`tools/ue-sample/`，打包在 `D:\UE_Analyze_Data\For Testing\DumperTest\`）。
> **D1**（GNames 解到 `EOSSDK-Win64-Shipping.dll`）、**D2**（群組掃描那一列的顯示）、
> **D3**（`FUObjectItem` stride 被偵測成一半）三項都已 ✅ 修復並驗證，整條刪除。
> 只剩樣本自己的一個問題。

## ✅ 樣本 Shipping 包的畫面心跳看不到
**build 2719** · **已於 2026-08-12 驗證通過 —— 而且完全不用重 cook**

> ⚠ **下面「卡住 —— 這台機器不能編 UE」的判斷是錯的，保留下來當教訓。**
> 磁碟上 `For Testing\DumperTest\Shipping\` 那包建於 2026-08-05 20:15，
> 比 HUD 的 commit `b3d8593`（20:10:50）**晚五分鐘** —— 它一直都帶著 `ADumperTestHUD`。
> 直接啟動就看到五行，`TickCount` 在 14.2 秒內 +15，
> 而且 F32÷10.25、F64÷0.25、RawDouble÷0.5 三條路徑都獨立算出同樣的 15 跳。
> **接受「環境做不到」之前，先比對產物 mtime 和那個 commit。** 完整表格見 [todo.md](todo.md)。
>
> 另外一個會浪費一個 session 的細節：**`-ExecCmds="t.MaxFPS 30"` 在 Shipping 包會被靜默忽略**
> （`Exec.h:13`：Shipping 走 `UE_ALLOW_EXEC_COMMANDS_IN_SHIPPING`），要壓 FPS 得用 Development 包。

`UEngine::AddOnScreenDebugMessage` 在 UE 5.4 是**整個函式本體**被
`#if !(UE_BUILD_SHIPPING || UE_BUILD_TEST)` 包起來（`UnrealEngine.cpp:11397`），
**沒有任何 flag 能救回來**。已改用 `ADumperTestHUD`
（`AHUD::DrawHUD` → `DrawText`，從 **Tick** 呼叫 `ClientSetHUD` 安裝，
不走 1 Hz timer，也不靠 GameMode asset）。**程式碼寫好了，但沒有 build 過。**

**執行步驟**（需要一台能編 UE 5.4 的機器）
1. 重新 cook + 重新打包 `tools/ue-sample/` 的 **Shipping** 設定
2. 執行，**不要**帶 `-DumperTestNoHud` 命令列參數
3. 看畫面

- **PASS** = 三行文字出現在 Shipping 包的畫面上，而且 `TickCount` 會往上跳。
- **FAIL** = 還是空白 —— 現在這代表 **HUD 安裝失敗**（先確認命令列沒有 `-DumperTestNoHud`），
  而不是「本來就看不到」。

> ⚠ **Shipping 畫面空白不能證明樣本有問題。** 要驗樣本本身，用 **Development** 包，
> 或直接在 Live Walker 讀 `TickCount` @ `0x518`。
> 另外 `UE_LOG(..., Warning, ...)` 在 Shipping 也不會留下來
> （`Build.h:328` 的 `NO_LOGGING`；`LogMacros.h:146-158` 只留 Fatal），
> 所以 `[DumperTest] ADumperTestActor ready at 0x…` 只有 Development 印得出來。

-----

# ④ Vendor 更新帶來的

## ✅ Z1 —— zydis `a95bb71`：Path-2 原生反組譯還解得出 `[this+off]`
**2026-08-05 bump** · **已於 2026-08-12 驗證通過（DumperTest Development，DLL 2794）**

> 八次 Path-2 分析，`instrs` 8–33，其中一個 **9 instrs 的函式解出 `1 mapped props`**，
> 全資料夾**零 decode error**。`instrs` 比 v5 的 17–65 低，但解出來的正是最短那個，
> 所以是 stock template 的 getter 短、不是 decoder 提早放棄。
>
> ⚠ **不要太早去 grep log。** 第一次點完 20 秒就查，結果是空的，差點被記成失敗 ——
> DLL 還沒 flush（`offsets-0.log` 之後從 6,048 長到 7,885 bytes）。
> **先 `grep find_property_xrefs ui-pipe-0.log` 確認指令有送出去**，
> 再從空的結果下任何結論。完整數字見 [todo.md](todo.md)。

zydis 從 `85d7518` 升到 `a95bb71`（"Decoder patch for variable-position decoder-tree filters" #638）
是 decoder 修正**加上整份 decoder table 重新生成** —— +34.9k / −45.7k 行。這正是當初 v4→v5
被判定「值得做一次 in-game 檢查」的同一種形狀，理由也一樣：**離線測試解的是我們自己寫的位元組，
而 table 重生會改變「任意遊戲程式碼」怎麼被解碼**。

**離線證據已經涵蓋的部分**（所以這一項不是重做）：5 個 `Test_Denken_*` 把真實 x64 序列餵進
Zydis 解碼且全過，含 `Test_Denken_ExcludesStackAndZeroDisp`（正是 v5 遷移動到的
`disp.size == 0` 那條）。81 + 996 全綠，DLL 建置乾淨。
**沒涵蓋的部分**：真實 UE 執行檔的編譯器輸出。

**執行步驟**
1. 注入**任何**一款 UE 遊戲（不挑）
2. 跑一次 Path-2 property xref —— Interesting Funcs 挑一個原生 getter/setter，
   或按 Property Search 的 xref 按鈕
3. `grep -E "AnalyzeNativeFunctionProps|FindPropertyXrefs" offsets-0.log`

```
AnalyzeNativeFunctionProps: 0x… exec=0x… -> N mapped props (U unmapped, I instrs, C calls)
FindPropertyXrefs: N xrefs (scanned … functions, … with script, …ms)
```

- **PASS** = `I instrs` 是合理的函式長度（v5 基準是每個函式 **17–65**），
  至少有些函式的 `N mapped props` 非零，而且**沒有 decode error**。
- **FAIL** = `instrs` 塌到接近 0（decoder 提早放棄），
  或原本 v5 有數字的函式現在全部 `-> 0 mapped props`。
- **不算 FAIL**：結果大多是空的是 Path 2 的**本質**，不是回歸 —— 只有原生的常數
  `[this+off]` getter 才映射得到，純 script 的屬性根本沒有機器碼。v5 那次驗證也是同一個結論。

*比較基準：2026-06-23 的 v5 smoke test（SEED + TQ2，皆 UE5）—— 每函式 17–65 instrs、
1–5 個 `[this+off]` access、很多 `→ 1 mapped props`、TQ2 `2 xrefs`、零 decode error。*

-----

# 已經不是「驗證」項目了

## 🔧 `Stop conn drain TIMEOUT` —— 診斷結束，剩下的是改 code

**不要再跑重現去看 phase 了 —— 已經跑過三次，三次答案都一樣。**

build 2657 的 per-connection `Phase` 儀器就是為了回答這個問題而加的，
而 2026-08-05 之後的三次擷取都已經回答了它：

```
13:38:45  straggler: parked in ReadFile (waiting for the next command) for  73871 ms, last cmd 'get_object_list'
16:42:55  straggler: parked in ReadFile (waiting for the next command) for 264184 ms, last cmd 'walk_functions'
18:43:36  straggler: parked in ReadFile (waiting for the next command) for 145063 ms, last cmd 'query_group_slot_leaves'
          Stop cancel issued: 0 accepted, 2 had nothing pending
          Stop conn drain TIMEOUT, 2 left (5030 ms, 49 cancel re-asserts)
```

**phase 是 `Reading`，而且已經停在那裡 264 秒。** 不是 `Dispatching`（卡在指令裡）、
不是 `StoppingWatches`（卡在 join watch thread）、也不是 `Writing`。
連線就是**真的**停在一個 blocking `ReadFile` 上。

**四次嘗試，四次被自己的儀器推翻：**

| # | 假設 | 被什麼推翻 |
|---|---|---|
| 1 | 卡在指令裡（invoke 沒回來） | `inFlight == false`；而且 `Stark::Shutdown` 本來就跑在 `Stop()` 之前 |
| 2 | `CancelIoEx` 錯過時機（2650 改成重複主張） | 49 次 re-assert，每一次都 `nothing pending` |
| 3 | 該用 `CancelSynchronousIo`（2651） | 呼叫了，handle 也發布了，還是 TIMEOUT |
| 4 | 「idle in ReadFile」這個字是推論不是觀測（2657 改成量 phase） | phase 證實**真的**是 `Reading` —— 前三個假設全錯 |

**根本原因**：pipe instance 建立時沒有 `FILE_FLAG_OVERLAPPED`，
所以 `CancelIoEx` 找不到任何 pending IRP（永遠 `ERROR_NOT_FOUND`）。

**剩下的選項是結構性的，不是再猜第五次：**
- 從 `Stop` 直接關掉連線 handle（讓 `ReadFile` 以錯誤返回），或
- 把 pipe 改成 overlapped I/O。

**未來那個修正上線之後才需要驗**（同樣的重現：UI 連著、取消勾選 CE record）：
`grep "Stop conn drain" pipe-0.log`
→ **PASS** = `satisfied, 0 left (… ms, N cancel re-asserts)`。

> 那個 re-assert 迴圈**留著**。它幾乎不花成本（5 秒內 49 次失敗的 syscall），
> 而且正是它快速證明了診斷是錯的 —— 只發一次會看起來像運氣不好。

-----

## 附記：不是 audit #4、但同樣待驗證的項目

英文版 todo.md 同一節底下還有一段
**「Shipped + unit-tests-pass but unproven on real games」**，
內容是更早出貨、單元測試綠但沒在真實遊戲上證明過的功能。那部分沒有翻譯，
但 2026-08-05 之後有兩個變動值得記在這裡：

- **V1a（TSet/TMap 掃描）、V1c（TOptional 掃描）、NumericAll（byte 家族）三項已 ✅**
  —— 全部由 DumperTest 樣本在 2026-08-05 結掉，分別 ⬜ 了自 build 927 / 942 / 796。
- **Dump Explorer 跨遊戲身分 gate**：case 1（同一遊戲不同 session 的 dump → 正常配對）
  **已有證據**（2026-08-05 DQ7R）。**case 2 / 3 仍待驗**：
  (2) 拿 A 遊戲的 dump 配 B 遊戲 → 必須**拒絕**並同時點名兩邊；
  (3) 拿同一遊戲**改版前**的 dump → 必須配對**但帶著** "Different build" 警語。
  case 3 需要真的等到一次遊戲改版，所以是機會財，排不進計畫。
- **Solide pool-truncation badge**（`⚠ capped`）仍 ⬜ —— 需要一個**超過 256 個活體實例**、
  而且 Force hold 有意義的類別（投射物、群眾 NPC、可破壞物件）。

另外還有一個**偶發測試**（`SnapshotViewModelTests.GroupMatch_MissingValue_ShowsErrorNoCandidates`），
2026-07-23 在完整平行測試中失敗過**一次**，之後單獨跑 25/25 三輪都過。
**沒有追** —— 觀察一次不等於重現。如果再發生，記下 `GroupCandidates` 是否非空、
或 `GroupStatusText` 是否為空，這兩者指向不同的原因。
