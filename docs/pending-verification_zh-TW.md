# 待實機驗證清單（只驗證，不改程式碼）

> **這份是 [todo.md](todo.md) 的 `## Pending live-game verification` 一節的繁體中文版。**
> 英文版是**唯一正本**：兩邊有出入時以英文版為準，改動也請先改英文版。
> 操作程序（去哪裡 grep、每個 marker 落在哪個檔案）在
> [log-verification-checklist.md](log-verification-checklist.md)。
> 這份講的是**狀態**（⬜ / ✅），那份講的是**怎麼做**。

-----

## 目前狀態（2026-08-04 第一次實機掃描，build 2622 — DQ7R / Elliot / CE 三份 log）

**（2026-08-04 → 08-05 五輪實測，build 2622 → 2645）**

**11 項 ✅ 已驗證 · 2 項 🟡 一半 · 13 項 ⬜ 尚未被觸發**

> **2026-08-05 追加：** 本檔末尾新增 **③ D 系列**（DumperTest 自建樣本挖出來的，
> 不計入上面這三個數字）。D1 / D3 已修復並驗證，**只剩 [D2](#-d2--群組掃描的那一列只顯示得下其中一種配對) 一步**
> —— 跑一次 `Changed` + `Unchanged` 群組掃描，確認列上的 `(+35)` 和 **All fields** 有作用。

| 狀態 | 項目 |
|---|---|
| ✅ 已驗證 | B49、B31、B5（被動半）、B47、B35、B42、B36、**B34**、**B14 + R5**、**B38**、乾淨掃描也產生 report |
| 🟡 一半 | **B8**（延後還原那條路徑還沒走到，而且**關遊戲永遠走不到**，見下）· **Dump Explorer**（case 1 有證據了，case 2/3 還沒） |
| ⬜ 尚未觸發 | 其餘 13 項 |

> **2026-08-05 那次 DQ7R 動到三件事，而且沒有一件是它原本要測的三項：**
> `Stop conn drain TIMEOUT` 的根因是從**早就躺在硬碟上**的一份 log 掉出來的（不需要它再發生一次）；
> **B47 之前的 ✅ 被發現是記在一個「那段程式碼根本沒被編進去」的手動注入 session 上**，
> 當天真正的 proxy session 才重新把它掙回來；
> 而 **B28 沒有測到** —— 看的那幾列是 `StrProperty`，不是 FText。
> R8 則被維護者直接推翻（見 [audit-2026-08-04-findings.md](audit-2026-08-04-findings.md)）。

### ⚠ 下一個 session 最值得先測的兩項

| 優先 | 項目 | 為什麼 |
|---|---|---|
| **1** | **B28** CJK FText 亂碼 | **唯一會讓使用者看到錯誤資料**的項目。**要找 Type 欄位寫著 `TextProperty` 的列**，`StrProperty` 不算（見下面 B28 那段）。同時要反向確認 STVoyager（UTF-8 FText）的中文還是對的 |
| **2** | **B4** CE mailbox 在 UI 死掉後仍可用 | 失敗時是**無聲的**：查詢回 0 卻寫 `scanned=<全部>`，看起來像「物件不存在」。CE-only session 會一直壞下去 |

其餘（B18、B19、B2、B25、B26、B13/B41…）都不會造成錯誤資料或當機，可以慢慢來。

### 🔍 `Stop conn drain TIMEOUT` —— invoke 那個假設已經被排除；**先不要動 code**

> **這一段一度寫成「根因已確定」。那是錯的，而收回這件事比當初那個結論有價值。**
> 當時的推論是：`teleport_get_pose`／`teleport_get_pov` 在 22:19:39.590/591 進來、
> *「從來沒回覆」*，所以連線卡在指令裡面。
> **但這份 pipe log 對任何指令都沒有「回覆」標記** —— 193 個 `Received`、0 個 `Sent`。
> 所以「沒有回覆那一行」什麼都不能證明：同一個檔案裡 78 個 `teleport_get_pov`
> 在一整段健康的 session 中全部同樣「沒有回覆」。

**log 真正能確定的事**（`pipe-20260804-221945.log`，build 2638）：
`Stop entry (conns=2)` → `cancels+wake done (0 ms)` → `conn drain TIMEOUT, 2 left (5000 ms)`。
兩條連線執行緒同時撐過了 `Tot::RequestShutdown()` 和對每一個連線 handle 的 `CancelIoEx`
（`Fern.cpp:481`、`:507-510`），然後把 5 秒預算燒完。

**讀 code 排除掉的事 —— invoke 那個假設，完全排除。**
`UE5_Shutdown`（`Frieren.cpp:587`）是先呼叫 **`Stark::Shutdown()`，才呼叫 `s_pipeServer.Stop()`**，
而 `Stark::Shutdown` 會把整個 invoke queue 排空、每個 pending promise 設成 `-7`
（`Stark.cpp:328-340`）。所以卡在 `EnqueueInvoke` 的 `future.wait_for` 的 pipe thread
**在 `Stop()` 被進入之前就已經被放掉了** —— 這個順序當初就是為了這件事，註解也是這樣寫的。

因此「讓 Stark 的 invoke 等待去看 `Tot::Requested()`」會是
**替一個在這條路徑上不可能發生的情況加一個輪詢迴圈**。2026-08-05 評估後否決。

> 另外就算單獨看它本身也該否決：去看完整的 `Tot::Requested()` 會讓
> **已經 latch 的 `g_perCommand`**（一條 lane 掉線時設起來，只有在全部斷線後重新連線才清除）
> 去中斷**另一條 lane** 上的 invoke —— 等於製造一個全新的、B4 家族的無聲失敗。
> 如果哪天真的要做，必須只看 `ShutdownRequested()`。

**剩下的部分不需要任何聰明的做法。** build 2641 已經加了 straggler 診斷
（`Fern.cpp:576-595`），它會對每一條還活著的連線印出
`INSIDE a command (cancel cannot reach it until it returns)` 或
`idle in ReadFile (the I/O cancel should have freed it)`，外加指令名稱和已經卡了幾毫秒。
**22:19:45 那筆是 build 2638，比它早，這就是問題還開著的原因。**
之後沒有再觸發過，因為 2026-08-05 兩次都沒有重現條件 ——
兩次的 `Stop entry` 都是 `conns=0`，也就是 UI 早就斷開了。

**2026-08-05 10:57 已解答 —— 第一次正確重現 straggler 就開火了，而且是另外那一半。**
重現方式就是上面寫的（UI 連著、取消勾選 CE record）：

```
10:57:00.157  Stop entry (conns=2)
10:57:00.157  Stop cancels+wake done (0 ms)
10:57:05.160  straggler: idle in ReadFile (the I/O cancel should have freed it), last cmd 'teleport_get_markers'
10:57:05.160  straggler: idle in ReadFile (the I/O cancel should have freed it), last cmd 'trigger_scan'
10:57:05.160  Stop conn drain TIMEOUT, 2 left (5002 ms)
```

兩條連線都是**閒置**（`inFlight == false`）—— 沒有任何東西卡在指令裡，
一開始那個猜測兩個方向都錯。單純就是 cancel 沒有碰到它們。

**為什麼一次性的 `CancelIoEx` 會漏掉。** `Fern::ReadLine`（`Fern.cpp:758-783`）
是**一次讀一個 byte**，所以一個 40 bytes 的指令 = 40 次獨立的 `ReadFile`、40 個空隙。
`Stop` 只在等待開始「之前」發了**一次** `CancelIoEx`。
剛好落在空隙裡的執行緒沒有 pending I/O 可以取消（`ERROR_NOT_FOUND`），
接著它會發出一個**全新的** `ReadFile`，而那個再也沒有人取消 —— 就一路停到 5 秒燒完。
Teleport 面板兩條 lane 每秒各輪詢兩次，落在空隙裡並不是罕見的競態：
`Stop entry` 距離最後一個指令進來只有 **146 ms**。

### ✅ build 2650 已修 —— 把 cancel 改成「重複主張」而不是發一次

`Fern::Stop` 現在把 5 秒的 drain 等待切成
`Grimoire::PIPE_STOP_CANCEL_REASSERT_MS`（100 ms）一段，每一段對還活著的連線重發一次
`CancelIoEx` —— 跟那六個 re-assert worker **同一個形狀**，只是用在關閉流程上。
常見情況零成本：沒有東西要 drain 時，迴圈在第一次等待就結束，re-assert 次數是 0。
在 `m_connMutex` 下做是安全的，因為連線執行緒是**先**把自己從 `m_conns` 移除、
**才** `CloseConnOnce`（`Fern.cpp:900-907`）。

另外加了一行，因為舊的 log 只能說「閒置在 ReadFile，cancel 應該要放掉它」，
**卻說不出 cancel 到底有沒有東西可以放** —— 那是兩種不同的 bug：
`Stop cancel issued: N accepted, M had nothing pending`。

**① 靠 log 就能驗，而且是同一個重現**：UI 連著、取消勾選 CE record，
然後 `grep "Stop conn drain" pipe-0.log`。

- **PASS** = `satisfied, 0 left (… ms, N cancel re-asserts)`，而且 **N ≥ 1 才是修正真的有作用的證明**
  （N = 0 只代表那次沒有連線撐過第一次 cancel，也就是沒用到這個修正）。
- **FAIL** = 還是 `TIMEOUT` + `straggler: idle in ReadFile` —— 那代表 `CancelIoEx`
  對這個 handle 根本叫不醒，而不是錯過時機；新的 `Stop cancel issued:` 那行會說是哪一種
  （`M had nothing pending` = 錯過時機；`N accepted` 但執行緒還停著 = cancel 被接受卻被忽略，要換修法）。

⬜ 尚未驗證 —— 修正是在那次擷取之後才出貨的，還沒重跑。

### 兩個失敗共同的教訓

**用「清單」寫出來的修正，拿同一份清單去驗，等於沒驗。**

- **B34** 列了三個 CE 檔名 —— 但 CE 實際的執行檔是 `cheatengine-x86_64-SSE4-AVX2.exe`，
  三個都沒中。DLL 在 CE 裡面掃了 5.8 秒，還把 pipe 開起來了。
- **B14** 列了七個 thread proc —— 但 DLL 實際上約有 15 個地方「丟出例外 = `std::terminate`」。

兩者對自己清單上的每一項都是對的，對世界是錯的。

### B14 花了三輪，而失敗的那兩輪才是真正有價值的部分

- **第 1 輪**：guard 補到那份「7 個」的清單上 —— WER dump 證明是在一條沒有 guard 的執行緒上 terminate。
- **第 2 輪**：把全部約 15 個進入點都補上 guard，**又當掉了，一模一樣**。
- **這才是答案，不是挫折**：`tick threw`、`UNCAUGHT exception` 兩代 guard 都是 0 次 ——
  **從頭到尾就沒有任何例外被丟出來**。`~std::thread()` 對一個還 joinable 的執行緒會**直接**呼叫
  `std::terminate()`；而使用者關掉遊戲時 `UE5_Shutdown` **根本不會被呼叫**，所以行程結束時
  每個 worker 都還是 joinable。
- 修法不是再列第三份清單，而是讓它變成**型別的性質**：`Routine::SafeThread`。

**額外的教訓**：修正沒生效時，先回去讀**證據**，不要急著加更多同一種修正 ——
第 2 輪的工完全花在一個從來沒有參與的機制上。

剩下 14 項請帶著這兩個教訓看。

### ⬜ 不等於「應該沒問題」

⬜ 的意思是**沒有人看過**。剩下 14 項大多單純是沒被觸發：沒裝第三方 wrapper、
沒有在指令執行中砍掉 UI、沒跑 Extra Scan。

-----

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

不用照清單順序做。按「要花多少力氣」排。**下表已反映 2026-08-04 的掃描結果。**

### 第 0 步 —— 完全免費（任何一次正常遊戲 session 之後 grep 就好）

原本七項，這次一次結掉四項。剩下三項不是「沒過」，是**這次的操作沒有觸發它們**。

| 項目 | Grep 什麼 | 檔案 | 狀態 |
|---|---|---|---|
| B5 被動半 | `UE5_Init:` | `init-0.log` | ✅ 5/5、14/14、1/1 一對一 |
| B49 | `PipeServer: Stop entry` | `pipe-0.log` | ✅ conns=0，59 ms |
| B31 | 直接 `ls` log 目錄，不用 grep | `Logs` 下的 `UE5DumpUI` | ✅ 8 MB + `_001` 已輪替 |
| B47 | `first-loaded-wins guard is NOT armed` | `init-0.log` | ✅ 0 次 |
| B34 | `DllMain AutoStart:` | `init-0.log` | 🔴 **失敗**，已重修 2628 |
| B29 log 半 | `is loaded but is not ours` | `init-0.log` | ⬜ 沒裝 wrapper，沒觸發 |
| B38 | 跑一次 proxy cleanup Report，看檔案落在哪 | — | ⬜ 沒跑過 Report |

### 第 1 步 —— 需要刻意操作，但答案完全在 log 裡

**B35 ✅ 已順帶結案**（`PERF Snapshot capture` 那行的分項算術自己就證明了）。

剩下：B28、B8、B4、B18、B19 —— 這次都沒觸發到。
**B10 卡在沒有對照組**：只有一條 `PERF Snapshot capture`，沒有 2596 之前的數字可比。

這一步真正要做的事很少：
- **B8** 只要記得**把 Noclip 打開**（這次 Fly 全程 `noclip=0`，所以那條路徑根本沒走到）
- **B4** 要在指令執行到一半時砍掉 UI
- **B18 / B19** 各自需要一個刻意佈置的前提（見各項）

### 第 2 步 —— 純人工，要看畫面或看遊戲

B42、B36 ✅ 已確認。剩下 ② 那一組。

其中 **B14 + R5 必須優先重測** —— 它這次**失敗了**，而且是唯一一個真的把遊戲弄當掉的項目。

-----

# ① 可從 log 取得

## ✅ B49 —— `Fern::Stop` 不再等一個永遠不會來的 client
**build 2569** · **已驗證（2026-08-04 三份 session log，build 2622）**

CE 那次 session 剛好命中最關鍵的條件 —— `Stop entry (conns=0)`，也就是舊版對一個同步 listen
handle 呼叫 `CloseHandle` 會**永遠卡住**的那個情況：

`cancels+wake done (0 ms)` → `conn drain satisfied, 0 left (3 ms)` → `accept join done (3 ms)`
→ `monitor join done (58 ms)` → `Stopped`。全程 59 ms，PASS 標準是 100 ms 內。

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

## ✅ B31 —— UI log 到 8 MB 會輪替，不會停住
**build 2585** · **已驗證（2026-08-04 三份 session log，build 2622）**

`Logs` 下的 `UE5DumpUI` 資料夾裡同時存在 `pipe-0.log`（**8,388,756 bytes**，正好是 8 MiB 上限）
和 `pipe-0_001.log`（4,055,182 bytes，**時間比較新** —— 21:05 vs 20:53）。輪替發生了，
而且寫入繼續進到新檔案。無聲停寫的特徵會是：只有那個 8 MB 檔案，而且最後一行很舊。

任何長時間 session 之後都免費：`ls %LOCALAPPDATA%\UE5CEDumper\Logs\UE5DumpUI\`

- **PASS** = 某個分類超過 8 MB 之後，會出現 `pipe-0_001.log`（或類似命名）跟
  `pipe-0.log` 並存，而且最新那個檔案的最後一行時間是近期的。
- **FAIL** = 只有單一一個 `pipe-0.log`，大小剛好卡在 ~8 MB，最後一行很舊 ——
  這就是「無聲停寫」的特徵。

> 最快跑到 8 MB 的方法：Teleport → Auto refresh，開著放它跑。

## ✅ B38 —— 殘留 proxy 的報告檔落在 app 資料夾裡面
**build 2585** · **已驗證（2026-08-04 22:49，build 2643）**

`leftover-proxies-20260804-224903.txt` 寫進了 `%LOCALAPPDATA%\UE5CEDumper\Reports\`，
而舊位置 `%LOCALAPPDATA%\Reports\` 裡仍然只有 2026-07-30 那個修正前的檔案。
log 也有對應行：`Leftover report written: …\UE5CEDumper\Reports\…（0 row(s), 67 folder(s) examined）`

## ✅ 乾淨掃描也會產生 report
**build 2637** · **已驗證（2026-08-04 22:49）**

由維護者提出：掃描沒找到東西時**也必須留下檔案**，否則「掃過了、什麼都沒有」和
「根本沒掃 / 掃錯地方 / 靜靜失敗了」一週之後完全分不出來。

`BuildReport` 本來就處理了空集合的情況，是 `CanWriteOrphanReport => Orphans.Count > 0`
讓那段文字永遠走不到、按鈕也是灰的。現在改成看 `OrphanScanRan`，而且空報告會寫出涵蓋範圍：
*「No leftover proxy DLLs were found. 67 folder(s) were examined.」*

> **~~待決定的 UX 問題~~ —— 維護者已於 2026-08-05 決定：維持現狀，不自動寫檔。**
> **Find leftovers** 把結果顯示在畫面上，**Report…** 才寫檔；寫檔仍然是一個明確的動作。
> 「不好發現」那一半已經在 build 2645 處理掉了 —— 掃描結果現在會逐字點名按鈕
> （*「press "Report…" to save this result as a file」*），乾淨的情況還會講出涵蓋範圍。
> **不要再重開這個討論。**

原本的測試步驟：

- **PASS** = 檔案出現在 `%LOCALAPPDATA%\UE5CEDumper\Reports\`
- **FAIL** = 出現在 `%LOCALAPPDATA%\Reports\`

> 2585 之前寫出來的檔案會留在舊位置，這是設計如此，不算 FAIL。

## ✅ B5（被動半）—— `UE5_Init` 的鎖沒有弄壞正常初始化
**build 2592** · **已驗證（2026-08-04 三份 session log，build 2622）**

三個遊戲的 `Starting initialization...` 與 `Complete (UE…)` 都嚴格一對一
（DQ7R 5/5、Elliot 14/14、CE 1/1），兩條新行也都沒出現。

⚠ 如同下面原本就寫的：**這只證明鎖沒有造成傷害，不證明競爭被修好了**。
刻意觸發的版本仍然在 ② 裡，還沒做。

grep `init-0.log` 的 `UE5_Init:`

- **PASS** = `Starting initialization...` 和 `Complete (UE…)` 嚴格一對一交替出現，
  而且兩條新行（`init already in progress`、`shutdown was requested during the scan`）
  都沒出現。
- **FAIL** = 有 `Starting` 卻沒有對應的 `Complete`（鎖死了 —— 理論上不該有任何情況能造成
  這個，正因如此才值得每個 session grep 一次），或連續兩行 `Starting`（還在競爭）。

> ⚠ **新行沒出現只證明「這次沒發生競爭」，不證明修好了。** 刻意觸發的版本在 ② 裡。

## ✅ B34 —— Cheat Engine 本身不會被當成遊戲來掃描
**build 2603** · **已驗證（build 2633）**

重測結果：`host process is 'cheatengine-x86_64-SSE4-AVX2.exe' — Cheat Engine is never a scan
target`，而且該資料夾的 `scan-0.log` 停在 121 bytes（只有標頭），對照失敗那次的 1.3 MB。

### 當初為什麼失敗（build 2622）

log 裡清楚寫著：

```
process: C:\Program Files\Cheat Engine\cheatengine-x86_64-SSE4-AVX2.exe
DllMain AutoStart: game process — calling UE5_AutoStart
```

接著是 5.8 秒的 AOB 掃描、1.3 MB 的 `scan-0.log`，而且 pipe 就開在 CE 裡面。

**原因**：那個防護是一份「精確檔名清單」，而 CE 真正的執行檔是 `-SSE4-AVX2` 這個 CPU 變體，
三個名字一個都沒中。而且 `g_isCEPlugin=0` —— 這次是手動注入，所以 `CEPlugin_GetVersion`
那一半也幫不上忙。

**現在**改成 `Grimoire::IsCheatEngineExeName`：對 `cheatengine` 這個字根做不分大小寫的
**前綴**比對，而且錨定在開頭（所以 `MyCheatEngineClone.exe` 這種遊戲不會被誤拒）。

**重測**：再手動把 DLL 注入 CE 一次。
**PASS** = 出現 `host process is '…' — Cheat Engine is never a scan target`，
而且那個資料夾裡的 `scan-0.log` 不會長大。

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

## ✅ B47 —— proxy 去重的防護會講出「它其實沒生效」
**build 2603** · **已驗證（2026-08-05，build 2645）· 08-04 那次的 ✅ 記在錯的 session 上**

> **這個更正跟 B34、B14 是同一個陷阱，所以留下來。** 08-04 的紀錄寫「DQ7R 是透過
> `version.dll` 跑的（真正的 proxy session，所以這段防護有被編進去）」—— **不是**。
> 那行在 `#ifdef UE5_PROXY_BUILD` 裡面（`Heiter.cpp:262-270`），而 08-04 的 DQ7R session
> **沒有任何一次**出現 `DllMain ProxyStart` 或 `Loaded real version.dll` —— 全部都是手動注入，
> 那段程式碼根本沒有被編進當時載入的 binary。它「不存在」什麼都證明不了。
>
> **一個東西的「缺席」只有在你先證明「會產生它的程式碼有被編進去而且有跑」之後才算證據。**
>
> **真正的證據是 2026-08-05 10:29:30 那次**，那是真的 proxy session ——
> `DllMain ProxyStart: proxy DLL mode — starting pipe server only (no scan)` →
> `Loaded real version.dll: C:\WINDOWS\system32\version.dll` —— 而
> `first-loaded-wins guard is NOT armed` 在那份 log 裡出現 **0 次**。
> `Local\…_<PID>` 成功了，而舊的 `Global\` 名稱需要遊戲沒有的權限。這次是用對的理由 PASS。

任何 proxy session：grep `init-0.log` 的 `first-loaded-wins guard is NOT armed`

- **PASS** = 這行**不存在**（`Local\` + PID 成功了，而 `Global\` 需要遊戲沒有的權限）。

> 這行出現**不代表這個修正失敗** —— 它就是修正本身在回報一個以前完全無聲的狀況。
> 但如果真的出現了，值得追一下。

## ✅ B35 —— PERF 的分項不再把自己的探針算進去
**build 2610** · **已驗證（2026-08-04 三份 session log，build 2622）**

> 這一項出貨時**根本沒有建立驗證條目** —— 是掃這批 log 的時候才發現的歸檔漏洞。

grep `PERF Snapshot capture` 得到：

```
wall 5,256.2 ms … split dll 2,733.5 / ipc 692.4 / ui 1,830.3 ms
```

三個分項加起來正好等於 wall；transport（dll+ipc = 3,425.9）**小於** wall；`ui` 是一個很大的非零值。
修正前的特徵剛好相反：transport **大於** wall，於是 `ui` 被夾到 0，而 `ipc` 把探針自己那
93–125 ms 的往返吸收掉了。這幾個數字正是 [multipipe-eval.md](multipipe-eval.md) 推論的依據。

## ⬜ B28 —— CJK FText 不再顯示成 ASCII 亂碼
**build 2599** · 不需要 log，證據直接在畫面上

> **❌ 2026-08-05 的 DQ7R 那次「沒有測到」，而且差在哪裡值得記下來，免得下次再走一樣的路。**
>
> 看到的那幾列（`Name` / `DisplayName` / `ListName` = 忘名）型別是 **`StrProperty`**，
> 也就是 FString —— 走純 UTF-16 的讀取路徑，**從來就沒有這個 bug**。
> B28 只活在 `ReadFTextString` 裡面。
>
> hex 只證明了 FString 那條路是對的，對 B28 什麼都沒說：
> `D8 5F | 0D 54 | 00 00 | 6F 00 | 78 00 | 00 00`
> = 忘(U+5FD8) 名(U+540D) NUL 'o' 'x' NUL，`ArrayNum=6`。
> 也就是遊戲存的是一個固定 6 個 TCHAR 的欄位，**第 2 個位置就是 NUL**；
> 我們的讀取在 NUL 停下來、顯示「忘名」—— 正確。
>
> **第二個沒中的地方**：忘(U+5FD8) 和 名(U+540D) 的**低位元組都不是 0x00**，
> 所以就算它真的是 FText，也踩不到觸發條件。
>
> **下次要怎麼做**：找 Type 欄位真的寫著 **`TextProperty`** 的那一列。
> DQ7R 2026-08-05 的 walk log 裡 **一次 FText 欄位讀取都沒有**
> （唯一的 `TextProperty` 字樣是類別名 `TextPropertyTestObject` 和 `TextProperty` 這個 meta-class），
> 所以要自己去找：用 Property Search 在 UI／對話／道具說明類別上找 TextProperty。
>
> **低位元組是 0x00、而且日／中文常見的觸發字**：
> **一** U+4E00 · **最** U+6700 · **言** U+8A00 · **退** U+9000 · **紀** U+7D00
> —— 而且字串長度要是**偶數**。

只影響 **FText 型別的值**（`ReadFTextString`）；FString 走的是純 UTF-16 的讀取路徑，從來沒有這個問題。

**怎麼做**：任何有中文／日文 UI 的遊戲 —— 把遊戲語言設成 CJK，在 Live Walker 或
Property Search 裡找一個 FText property。

- **PASS** = 值顯示為正常中文／日文。
- **FAIL** = 該顯示中文的地方變成一小串 ASCII 標點湯（`,{1`、`-N?e`）。

**特別要試**：字數是**偶數**、而且含有 `U+xx00` 字元的字串（一、第…一、統一）——
這正是觸發條件。

**反向確認（很重要）**：確認修正沒有往另一邊歪掉 ——
**Star Trek Voyager（UE5.6）** 的 FText 是用 UTF-8 存的，它的中文必須仍然正確。

## 🟡 B8 —— Fly / Noclip 不再把角色留在「穿透」狀態
**build 2596** · **主要路徑已驗證（Elliot 22:01）· 延後路徑仍未觸發**

已驗證的部分 —— log 顯示修正後的順序完全正確：
`Fly: worker stopped` → `Fly: SetActorEnableCollision(1) invoked` → `Fly: DISABLED`。
先 join 再還原，而且還原是從 invoke **真的執行了**才記錄狀態。

> ### ⚠ 重要：**關遊戲永遠測不到延後那條路徑**
>
> 關掉遊戲時 `UE5_Shutdown` **根本不會被呼叫**（整份 log 裡 `UE5_Shutdown: Cleaning up`
> 出現 0 次），所以 `Dunste::SetEnabled(false)` 完全沒有執行，
> `DISABLED but the pawn's collision is still OFF` 這行永遠不可能印出來。
> 22:33 那次 Elliot 就是證據：Fly 開著關掉遊戲，log 裡**連一行 `Fly: DISABLED` 都沒有**。
> 那是 B14 的測試，不是 B8 的測試。
>
> 延後路徑需要的是**在遊戲執行緒安靜的時候按下 Disable 按鈕**。
> 22:01 那次確實按了 Disable，而 `SetActorEnableCollision(1) invoked` 證明當下遊戲執行緒
> **還在跳**。所以關鍵變數不是 alt-tab 多久，而是**那款遊戲會不會在失焦時 idle**
> （`t.IdleWhenNotForeground`）—— Elliot 看起來不會。
>
> **所以這一項需要一款失焦真的會安靜下來的遊戲。** 如果手邊沒有，
> 把它結成「接受未驗證」是合理的：這條程式路徑跟 Schlacht 從 build 2364 就在跑的是同一條，
> 而且主要路徑已經驗過了。

原本的測試步驟：

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
**build 2596** · **卡在沒有「對照組」，不是卡在量測**

保留下來的 log 裡只有**一條** `PERF Snapshot capture`（`wall 5,256.2 ms`，2026-08-04，
修正之後），沒有 2596 之前的數字可以比。兩個選擇：把這個數字當成新的基準，下次對
**同一個遊戲、同一份 snapshot** 再抓一次來比；或者只驗正確性那一半
（struct 型別 / enum 名稱 / bool mask 仍然有值）。

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

## ✅ B42 —— 第二次啟動會把第一個視窗叫到前面
**build 2610** · **已驗證（2026-08-04，由維護者確認）**

**怎麼做**：跑 `dist\UE5DumpUI.exe`，然後再跑一次（雙擊 exe 或捷徑）。

- **PASS** = 既有的視窗跑到最前面 ——**包括它原本是最小化的時候**—— 而且沒有出現第二個視窗。
- **FAIL** = 看起來什麼都沒發生，那是舊行為。

**值得在第一個實例「已連線到遊戲」的狀態下測**，因為視窗標題會帶上模組名稱，
用標題搜尋的做法正好會在這個時候失效。

## ✅ B36 —— 沒選任何列時的 Force 子選單
**build 2610** · **已驗證（2026-08-04，由維護者確認）**

**怎麼做**：Property Search → 執行一次搜尋 → **在列的下方空白處按右鍵**，
或在一個你沒有左鍵點過的列上按右鍵。

- **PASS** = 沒有 Force 子選單。
- 接著左鍵點一個 BoolProperty 的列再按右鍵：只出現 Force ON / OFF。
- **FAIL** = 四個動作同時出現。

> 需要先打開 Experimental 開關，子選單才會存在。

## ✅ B14 + R5 —— 在 hold worker 還活著的時候關掉遊戲
**build 2596** · **已驗證（build 2638）**

DQ7R，bullet-time + See-through 開著，從遊戲自己的視窗關掉 —— event log 沒有任何一筆、沒有 dump。

**真正的原因跟 B14 原本的診斷無關**：`~std::thread()` 對還 joinable 的執行緒直接呼叫
`std::terminate()`，沒有任何例外被丟出來，所以兩代 exception guard 都碰不到它。
而 `UE5_Shutdown` 在使用者關遊戲時根本不會被呼叫，於是行程結束時每個 worker 都還 joinable。
修法是 `Routine::SafeThread`（解構時 detach），讓它變成型別的性質而不是第三份清單。

### 前兩輪的經過（值得留著）

DQ7R 在 21:05:06 當掉，跑的是 build 2622（所有修正都在裡面）。WER dump
（`%LOCALAPPDATA%\CrashDumps\DQ7R-Win64-Shipping.exe.55564.dmp`）顯示：

- `0xC0000409`，**param[0] = 7 = FAST_FAIL_FATAL_APP_EXIT** —— 就是 `abort()` / `std::terminate`
- 整條錯誤堆疊都在 `version.dll` 和 CRT 裡面，也就是**我們自己的程式碼**
- **任何地方都沒有 `tick threw`** —— 表示連一個 guard 都沒被碰到

當時的情境：`pipe-0.log` 最後一行是 `FindInstancesByClass` 回報 `nonNull=35109`，
而 0.3 秒前同一個查詢還是 `154964` —— 遊戲正在拆掉物件池，而我們還在走它。

**修正本身是對的，錯的是它的適用範圍。** 原本的 finding 寫「7 個 thread proc 中的 2 個」；
DLL 實際上約有 15 個地方「丟出例外 = `std::terminate`」。build 2628 把
`Routine::RunThreadGuarded` 補到全部，其中最關鍵的是 `Stark::HookedProcessEvent` ——
它跑在**遊戲自己的執行緒**上，由沒有任何 handler 的遊戲程式碼呼叫進來，而且會配置記憶體兩次。

**重測**：照下面原本的步驟。**PASS** = event log 沒有任何一筆。
如果又發生，`init-0.log` 現在會有 `UNCAUGHT exception … contained` 並指出是哪條執行緒 ——
這正是把所有進入點都走同一個 helper 換來的東西。

> 同一份 event log 裡的 Elliot 當機是 build **2567**，在 B14 出貨之前 ——
> 那是原本的 bug，不是回歸。

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

# ③ D 系列 —— DumperTest 自建樣本挖出來的

> 這幾項不屬於 audit #4，來源是 2026-08-05 自建的 UE 5.4 `DumperTest` 樣本
> （`tools/ue-sample/`，打包在 `D:\UE_Analyze_Data\For Testing\DumperTest\`）。
> **D1**（GNames 解到 `EOSSDK-Win64-Shipping.dll`）和 **D3**（`FUObjectItem` stride
> 被偵測成一半）都已 ✅ 修復並驗證；只剩 D2 的最後一步。

## ⬜ D2 —— 群組掃描的那一列，只顯示得下「其中一種配對」
**build 2719** · 需要實機操作（跑一次群組掃描）

**這一項本身不是掃描錯誤。** 一個物件通常同時以好幾種方式滿足同一組值，而結果列
只放得下一組。2026-08-05 回報的
*「`TickCount=NNN, FrozenInt=424242` 沒出現」* 就是這樣：那一場的
`ui-pipe-0.log` 顯示 slot 0（Changed）保留 `[1288, 1304]`、slot 1（Unchanged）保留
36 個含 `1308`，而 `1304` = `0x518` = TickCount、`1308` = `0x51C` = FrozenInt
（同一場的 `ui-view-*.log` 有 `ScrollToFieldByOffset: offset 0x51C -> field 'FrozenInt'`
可獨立佐證）。兩組配對都成立，列上只顯示了 `Health` 那一組。

**前三次修法都是換一組贏家 —— 那是零和的**：不管推哪一組上去，另一組看起來都像沒找到。

而且**沒有任何自動規則能挑出「TickCount + FrozenInt」這一組**：slot 1 的 36 個
unchanged 欄位裡，FrozenInt 和 `I16` / `FixedArr` / `I8_Neg` 在資料上毫無差別，
2 × 36 = 72 種合法配對，掃描器無從得知你要的是哪一種。所以 build 2719 做的是三件
讓它**可解**的事，而不是再換一次贏家：

1. **`match_count`**（2690 就在線上、但從沒被解析）→ 列上的 `(+35)`，展開列的
   `1 of 36 matching field(s)`。
2. **All fields**（`query_group_slot_leaves`）逐一具名該 slot 保留的每個欄位，
   **再按一下收合**；清單順序改成**物件自己宣告的欄位排前面**（leaf 是 base class 優先
   收集的，不排序的話開頭就是 `PrimaryActorTick.*` / `InitialLifeSpan` /
   `CustomTimeDilation`，`FrozenInt` 會在第三十幾列、捲動框外）。
3. **Filter 改成 space = AND** —— 這才是「指定配對」的方法。以前 filter 是單一子字串，
   根本沒辦法同時說「slot 0 要 TickCount、slot 1 要 FrozenInt」。現在打
   **`tickcount frozenint`**，兩個 term 各自落在一個 slot，那一列就直接變成你要的配對。
   （這也把 group filter 補回 CLAUDE.md 本來就有的 space=AND 規則 —— 它是最後一個例外。）
4. **預設不拿 0 當 witness**。0 在遊戲裡幾乎沒有實質意義（引擎的記錄欄位預設就是 0），
   而預設那一列以前常讀作 `PrimaryActorTick.TickInterval=0, InitialLifeSpan=0` ——
   合法但最沒資訊量的一組，而該物件真正的欄位其實也命中了。非零值現在在**每一條選取規則
   內部**優先，是規則內的 tie-break 而不是新規則：整個 slot 都是 0 時仍然會顯示一個
   （空白格是更糟的謊），而使用者明確 filter 出來的 0 仍然贏。

✅ **2026-08-05 已實測**：打 `tickcount frozenint` → 該列變成
`TickCount=45 (+1), FrozenInt=424242 (+35)`，Development 與 Shipping 兩個包都有結果。

**怎麼做**（DumperTest Shipping 包，注入後）：
1. Value Search → 開 **Multiple values (group)** → 兩個 slot 都 `NumericNoByte` / `Between 0 .. 1000000`
2. **Group First Scan**
3. slot 0 改 `Changed`、slot 1 改 `Unchanged` → **Group Next Scan**
4. 找到 `DumperTestActor_0` 那一列，**點選它**（往下展開，不是往右）
5. 在 slot 1 那一行按 **All fields**，再按一次確認會收合
6. **在 Filter 打 `tickcount frozenint`**

- **PASS** =
  - 主列讀作 `Health.CurrentValue=19 (+1), Health.BaseValue=100 (+35)`（數字會變，重點是有 `(+N)`）
  - 展開後每個 slot 寫著 `… — 1 of N matching field(s)`，不再是舊的
    `= unchanged: 36 candidate offset(s)`
  - **All fields** 列出 36 個具名欄位，而且 **`FrozenInt` = 424242 出現在前段**
    （不用捲到底 —— 物件自己的欄位排在繼承來的前面）；再按一次會收合
  - slot 0 的 All fields 看得到 **`TickCount`**
  - **第 6 步之後，那一列直接變成 `TickCount=… , FrozenInt=424242`** ← 這是本次最關鍵的一項
- **FAIL** = `(+N)` 沒出現（`match_count` 沒被解析）／按鈕沒出現
  （`HasHiddenLeaves` 的 gate 或 DLL 版本不對）／清單是空的或報錯
  （`query_group_slot_leaves` 沒接上 —— 錯誤會顯示在 ErrorMessage）／
  打兩個 term 之後**一列都不剩**（filter 還是把整串當單一子字串，AND 沒生效）

> **順便可以確認的兩件事**（不必特地做）：
> Snapshot / SPC Query 的群組結果列現在也會出現 `(+N)`，但**沒有** All fields 按鈕
> （那兩個分頁沒有 live session），滑鼠移到表格上會有一段不提那顆按鈕的說明。
> 另外，Deep 打開時展開列若出現某個欄位「位置」是空的（沒有 `→ 0x…`），那是**正確的**
> —— 容器元素沒有物件相對 offset，而 Snapshot 路徑也拿不到它真正的位址，
> 所以寧可不寫，也不寫一個看起來合理但錯的位址。

-----

# ④ Vendor 更新帶來的

## ⬜ Z1 —— zydis `a95bb71`：Path-2 原生反組譯還解得出 `[this+off]`
**2026-08-05 bump** · **① 靠 log 就能驗**，但需要一個刻意動作

zydis 從 `85d7518` 升到 `a95bb71`（"Decoder patch for variable-position decoder-tree filters" #638）
是 decoder 修正**加上整份 decoder table 重新生成** —— +34.9k / −45.7k 行。這正是當初 v4→v5
被判定「值得做一次 in-game 檢查」的同一種形狀，理由也一樣：**離線測試解的是我們自己寫的位元組，
而 table 重生會改變「任意遊戲程式碼」怎麼被解碼**。

**離線證據已經涵蓋的部分**（所以這一項不是重做）：5 個 `Test_Denken_*` 把真實 x64 序列餵進
Zydis 解碼且全過，含 `Test_Denken_ExcludesStackAndZeroDisp`（正是 v5 遷移動到的
`disp.size == 0` 那條）。81 + 996 全綠，DLL 建置乾淨。

**沒涵蓋的部分**：真實 UE 執行檔的編譯器輸出。

**怎麼做**：注入任一 UE 遊戲，跑一次 Path-2 property xref（Interesting Funcs 挑一個原生
getter/setter，或 Property Search 的 xref 按鈕），然後 grep **`offsets-0.log`**
（category `OARR` → `LF_Offsets`，見 `Sein.cpp` 的 `s_catMap`）：

```
AnalyzeNativeFunctionProps: 0x… exec=0x… -> N mapped props (U unmapped, I instrs, C calls)
FindPropertyXrefs: N xrefs (scanned … functions, … with script, …ms)
```

- **PASS** = `I instrs` 是合理的函式長度（v5 基準是每個函式 17–65），至少有些函式的
  `N mapped props` 非零，而且**沒有 decode error**。
- **FAIL** = `instrs` 塌到接近 0（decoder 提早放棄），或原本 v5 有數字的函式現在全部
  `-> 0 mapped props`。
- **不算 FAIL**：結果大多是空的是 Path 2 的**本質**，不是回歸 —— 只有原生的常數
  `[this+off]` getter 才映射得到，純 script 的屬性根本沒有機器碼。v5 那次驗證也是同一個結論。

*比較基準：2026-06-23 的 v5 smoke test（SEED + TQ2，皆 UE5）—— 每函式 17–65 instrs、
1–5 個 `[this+off]` access、很多 `→ 1 mapped props`、TQ2 `2 xrefs`、零 decode error。*

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
