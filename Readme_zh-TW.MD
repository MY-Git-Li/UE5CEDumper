# UE5CEDumper
<img src="./img/UE5CEDumper.jpg" alt="UE5CEDumper"/>  

一套針對 Unreal Engine 4 & 5 遊戲的**執行期結構探查工具**、配合 Cheat Engine、可更方便的開發 Table。

UE5CEDumper 是一款UE資料的互動式的檢查工具。不同於其它 Dumper，它為遊戲記憶體提供了一個**即時視窗**，讓你能即時瀏覽物件、尋找實例，並匯出支援 Cheat Engine (CE) 的結構定義。

> 本工具為「實戰型」的 Table 製作人員打造，旨在消除UE物件的偵測、識別到在 CE 中的實作之間的鴻溝。

> 這並不是一般 Dumper、無法把大量資料倒出並分析，其集中在簡單找到 UE 結構，並和 CE 開發配合使用。故請視為是一個通用型的 UE 工具。

> ### 使用範圍
>
> **僅限 Windows x64、單機／離線使用。** 這是給你**合法擁有**的遊戲、在你自己的機器上使用的檢視與除錯工具。
> 請勿用於多人、競技或線上模式 — 除了不公平之外，反作弊、帳號封鎖與法律風險實際上都集中在那裡。
> UE5CEDumper 讀取執行中 process 的記憶體；它不散布任何遊戲程式碼、資產或金鑰，也不碰 pak/IoStore 的容器加密。

---
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D4)](https://www.microsoft.com/windows)
[![UE Version](https://img.shields.io/badge/UE-4.18--5.8-orange)](https://www.unrealengine.com/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![C++](https://img.shields.io/badge/C++-23-00599C)](https://isocpp.org/)
[![Avalonia](https://img.shields.io/badge/Avalonia-UI-8B5CF6)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-CC785C?logo=claude)](https://claude.ai/code)

## 螢幕截圖 (Screenshots)
<img src="./img/MainUI.gif" alt="Live Walker"/>  
<img src="./img/Value_Search.webp" alt="Value Search"/>  

## 重點亮點 (Highlights)

快速了解你實際能用它做什麼（完整的 Table 製作功能列表請見 **[Features_zh-TW.md](Features_zh-TW.md)**）：

- **即時記憶體檢查** — 瀏覽遊戲物件、找出某類別的每個實例，並以即時數值深入 struct / class 佈局。
- **Value Search（數值搜尋）** — Cheat-Engine 風格的 First Scan / Next Scan，掃描每個 UE 屬性（數字、字串、向量、array / map / set），讓你不需知道 offset 就能找到要修改的目標。**Group 模式**可找出「同時持有多個數值」的物件（例如同一個 stats 區塊裡的 `Str + Def + Dex + Int`）— 比單值選擇性高得多；另有可選的 **Deep** 模式能深入巢狀容器找值。
- **Teleport（傳送）** — 將位置存入 3 個 marker 槽並隨後召回（BugItGo 風格），或在俯視 / 2.5D 遊戲中**傳送到滑鼠游標位置**。可自設全域熱鍵。也會讀取即時**相機 POV**（位置 / 旋轉 / FOV），讓你看出遊戲相機是跟著 pawn 還是獨立運作。¹
- **角色調整（Player tuning）** — 用 10%–3000% 滑桿切換 **Super Jump（超級跳）**、**Move Speed（移動速度）** 與 **Gravity（重力）**，外加 **God Mode（無敵）** 與 **Time Dilation（時間膨脹：全域或僅玩家的慢動作 / 凍結 / 快轉）** — 全部以反射強制設定，並對抗遊戲每 tick 的覆寫，因此重生後仍維持。可綁全域熱鍵，或匯出成 Cheat Engine 的開 / 關記錄。
- **Debug Camera（除錯相機）** — 在內建此功能的遊戲中強制開 / 關自由飛行除錯相機（即使在通常會卡住的 Shipping build 也能可靠開關）。
- **Console（主控台）** — 發現並一鍵呼叫許多遊戲保留的 `fly` / `god` / `ghost` / 遊戲特定 cheat 指令。
- **即時函式剖析器（Live Funcs）** — 邊錄邊做*一個*遊戲內動作（開商店、衝刺），就能看到遊戲實際呼叫了哪些 UFunction，依呼叫次數排名，並有 baseline 差異與雜訊過濾。當名稱搜尋找不到時，改用「行為」找函式。
- **一鍵 Cheat Engine 匯出** — 指標鏈 XML、Structure Dissect (CSX)、SDK headers、AA 腳本，以及多列 `.CT` 批次。
- **Dump Explorer（離線瀏覽）** — 離線瀏覽匯出的「Dump All」`.jsonl`，一個關鍵字同時搜尋 class + property + function，並分成 ✅ *目前遊戲裡有*（直接跳到 Live Walker）與 ⚠ *此遊戲沒有*。
- **注入免 Cheat Engine** — 把 `version.dll` **proxy DLL** 放到遊戲旁，或從 UI（**Inject into running game…**）或 **`inject-ue.ps1`** 命令列工具對正在執行的遊戲注入（遊戲以系統管理員執行時會自動提權）。Proxy Deploy 分頁會記住哪個 proxy 對哪款遊戲有效，並**逐遊戲建議正確的 proxy**（已確認可用 > 上次部署 > 注入成功記錄）。

> ¹ Marker 存檔 / 召回廣泛可用（包含泰坦任務 2，已修正某些「回報成功卻不移動 actor」的引擎 setter 的 transform 重新整理問題）。少數被大量精簡的 Shipping build（例如泰坦任務 2）無法做*游標*傳送，因為它們移除了標準的游標 / viewport / line-trace API 並改用自訂虛擬游標 — 詳見 [docs/teleport-spec.md](docs/teleport-spec.md)。

## 實測版本矩陣 (Tested Version Matrix)

| UE 版本 | GObjects | GNames | DynOff | 已驗證遊戲 |
|---|:---:|:---:|:---:|---|
| **4.15 – 4.17** | ✅ | ✅ | ✅ | *Extinction* |
| **4.18 – 4.20** | ✅ | ✅ | ✅ | *Final Fantasy VII Remake Intergrade, The Occupation* |
| **4.21 – 4.24** | ✅ | ✅ | ✅ | *《STAR WARS 絕地：組織殞落™》, 勇者鬥惡龍 XI S, 偶像大師 星耀季節, 八方旅人, 勇者鬥惡龍 I&II / III HD-2D 重製版* |
| **4.25 – 4.27** | ✅ | ✅ | ✅ | *Final Fantasy VII Rebirth, 劍星 (Stellar Blade), Tower of Mask, 霍格華茲的傳承, 復活邪神 2 七英雄的復仇, Ghostwire: Tokyo, TimeSplitters Rewind (Early Access V0.3.3), The Artisan of Glimmith, Barn Finders, 機動戰士 GUNDAM SEED 激鬥命運 復刻版, 女神異聞錄３ Reload (Persona 3 Reload — 版本字串被移除 → 工具預設 504；UE5-Extended + TaggedFFieldVariant，ProcessEvent 為 4.27 slot；視窗非前景時遊戲執行緒會停擺)* |
| **5.0 – 5.2** | ✅ | ✅ | ✅ | *Squirrel With A Gun, Caravan Sandwitch, Meltopia, Retro Rewind Demo* |
| **5.3 – 5.4** | ✅ | ✅ | ✅ | *Satisfactory (v1.1.3.1 滿意工廠), Colossal, Avowed (Obsidian — packed 20-byte FUObjectItem；static-struct GObjects AOB), 艾恩葛朗特 迴盪新聲 Demo (Echoes of Aincrad, Bandai Namco — dxgi.dll proxy), 冒險家艾略特的千年奇譚 (The Adventures of Elliot：版本字串被移除 → 偵測為 4.27，執行期校正為 UE5.4), MindsEye (Build A Rocket Boy — licensee fork：`FChunkedFixedUObjectArray` 欄位重排、`FUObjectItem` 加大為 32 bytes 且 `UObject*` 位於 +0x10、`FNameEntry` 字元經 XOR 混淆，改由讀取該 fork 自己的 key table 解碼；**僅在遊戲版本 7.3.1 驗證** — 所有 RVA 皆與 build 綁定，重推導方式見 [mindseye-fork-notes.md](docs/mindseye-fork-notes.md))* |
| **5.5 – 5.7** | ✅ | ✅* | ✅** | *泰坦任務 2, EverSpace 2, Lushfoil Photography Sim, 莊園領主 (Manor Lords), Cat Island Petrichor Demo, Way of the Hunter 2 Demo, COMBAT PILOT: CARRIER QUALIFICATION Demo, Solarpunk (太陽龐克), Pionero Capital Demo, Satisfactory (滿意工廠 v1.2.3.1), Star Trek Voyager – Across the Unknown* |

*\*GNames 在 5.5+ 版本使用 .data 指標掃描回退機制。*
*\**DynOff 支援 **CasePreservingName (FName = 16 bytes)** 佈局。

---

## 專為 Table 製作人員設計的功能

完整的 Table 製作功能列表 — 一列一功能（AOB 掃描、DynOff、Live Walker、Value Search 單值 + 群組掃描、Teleport、移動調整 + God Mode + Time Dilation、即時函式剖析器、多格式 CE 匯出，以及其餘全部）— 收錄於 **[Features_zh-TW.md](Features_zh-TW.md)**。

---

## 架構與工作流程 (Architecture & Workflow)

### 方式 A：Cheat Engine 注入

1. **注入 DLL**: 開啟 Cheat Engine 附加遊戲，確保遊戲存檔已載入。開啟 `UE5CEDumper.CT`。
2. **啟用 CE 腳本**: 先啟用 `init <== enable after process attached`，再啟用 `Inject DLL + Start Pipe Server`。DLL 自動定位引擎全域指標，並偵測 UE 版本與記憶體佈局。
3. **連接 UI**: 等待數秒讓掃描完成。啟動 **UE5DumpUI.exe** 並點擊 **Connect**。即時數據透過具名管道 (Named Pipe, JSON-RPC) 串流至 UI。
4. **瀏覽及分析**: 瀏覽 `UObject` 階層，找到目標 Class，深入容器查看元素，或由 CE 中的位址反查並匯出。

### 方式 B：Proxy DLL（推薦，免 Cheat Engine）

1. **放置 DLL**: 將 `version.dll`（由 `build.ps1 -Target ProxyDLL` 產生）複製到遊戲根目錄（與 `.exe` 同層）。
2. **啟動遊戲**: 正常啟動遊戲。Proxy DLL 會自動載入並啟動管道伺服器。
3. **載入存檔**: 進入遊戲世界，確保 UE 物件已載入記憶體。
4. **連接 + 掃描**: 啟動 **UE5DumpUI.exe**，點擊 **Connect**，再點擊 **Start Scan**。DLL 執行 AOB 掃描並將引擎資料回傳至 UI。
5. **瀏覽及分析**: 與方式 A 相同 — 瀏覽物件、尋找實例、匯出 CE 結構。

> **注意**: 請勿同時使用兩種方式。若 Proxy DLL 已放在遊戲目錄中，請勿再透過 CE 注入 `UE5Dumper.dll`。DLL 會偵測重複實例並跳過自動啟動以避免衝突。

> **該用哪個 Proxy DLL？** 先試 `version.dll`（`build.ps1 -Target ProxyDLL`）。若遊戲能啟動但 UI 無法連接，通常代表該 EXE 沒有匯入 `version.dll` — 改試 **`dxgi.dll`**（`-Target ProxyDxgi`，每款 D3D11/D3D12 UE 遊戲都會匯入），或 `dinput8.dll`（`-Target ProxyDinput8`）。`build.ps1`（預設 `-Target All`）會把三種都建置到 `dist\proxy\`，而 **Proxy Deploy** 分頁可讓你為每款遊戲挑選類型並自動部署（Steam 遊戲，或指定磁碟掃描任何位置 — Epic / GOG / 手動安裝）。只要某款遊戲曾成功用過，表格的 **Suggested proxy** 欄位就會直接告訴你該選哪個。

### 方式 C：對執行中的遊戲注入（免 CE、免重開）

把 `UE5Dumper.dll` 注入到**正在執行**的遊戲 — 最快的方式（免 Cheat Engine、免預先部署 proxy、免重開遊戲）。兩種入口、同一種技術（`CreateRemoteThread` + `LoadLibraryW`）：

- **從 UI**: Proxy Deploy 分頁 → **Inject into running game…** → 在 process picker 選遊戲 → **Inject**。UI 會自動連線。若遊戲以系統管理員執行，會跳 UAC 讓你提權注入 — 不用手動重開。
- **從命令列** — `inject-ue.ps1`（隨發佈放在 `dist\`，與 `UE5Dumper.dll` 同層）：

  ```powershell
  .\inject-ue.ps1                 # 自動：注入唯一在跑的 UE 遊戲
  .\inject-ue.ps1 -List           # 列出偵測到的 UE 遊戲
  .\inject-ue.ps1 -ProcessId 1234 # 注入指定 PID
  ```

  接著啟動 **UE5DumpUI.exe** 並 **Connect**。遇到 Access Denied（遊戲以系統管理員執行）時，腳本會自動以系統管理員身分重新啟動（跳一次 UAC）。

> **僅限 x64 遊戲。** 使用範圍請見本文件開頭的說明 — 與所有注入方式相同，`CreateRemoteThread` 可能被防毒標記，並會被 kernel 反作弊（EAC / BattlEye）擋下或封鎖。

| **Game Process (Injected)** |
| :---: |
| DLL + CE Lua Bridge（或 Proxy DLL）|
| ⬇️ |
| **Named Pipe IPC (JSON-RPC Protocol)** |
| ⬇️ |
| **External GUI (Avalonia UI App)** |

---

### 選用：與 AOBMaker CE DLL 外掛整合

AOBMaker 是用於產生 AOB 特徵碼及 CE Auto Assembler 腳本的工具。它有一個 CE DLL 外掛可以與 UE5CEDumper 通訊，共享偵測到的特徵碼與偏移量。如果你已安裝 AOBMaker，可在 UE5CEDumper UI 中啟用整合功能：
- 直接由 UI 一鍵在 CE 中瀏覽記憶體區段或對應程式碼。
- 直接產生 CE AA 腳本（含動態 GWorld AOB AA 腳本）。
- 為 UE 型別與欄位產生 CE 記憶體紀錄。
- 為 UE 結構產生 dissect data 供 CE Structure Dissect 使用。

備註：AOBMaker 整合為選用，並非 UE5CEDumper 的必要條件。UE5CEDumper 的核心功能皆可獨立運作。

AOBMaker 下載連結（需先註冊）：https://opencheattables.com/viewtopic.php?t=1788

---

## 系統需求 (Requirements)

### 編譯環境 (Build)

| 工具 | 版本要求 |
|---|---|
| Visual Studio / MSVC | 2022 v17+ (MSVC v143) |
| CMake | 3.25+ |
| Ninja | 任何近期版本 |
| .NET SDK | 10.0 |

> `build.cmd` / `build.ps1` 會透過 `vswhere` 自動定位 MSVC，無需手動設定路徑。

### 執行環境 (Runtime)

- Windows 10/11 x64
- Cheat Engine 7.6+（CE 注入方式）*或* Proxy DLL（免 CE）
- 執行中的 Unreal Engine 4 或 5 遊戲作業程序 (x64)

---

## 重要注意事項 (Important Notes)

* **自定義數據結構**: 在如《FF7 Rebirth》等遊戲中，部分關鍵數據（如 HP）存儲在標準 `UObject` 之外的自定義結構中。Live Walker 可協助探查這些區域，但無法直接自動發現。
* **GWorld 連通性**: 截至 2026-07-23，`GWorld` 遍歷在 **100% 實測遊戲中正常運作（39 / 39）**，已涵蓋 Satisfactory（modular UE，獨立 `CoreUObject-Win64-Shipping.dll`）、《STAR WARS 絕地：組織殞落》(UE 4.21，EA 啟動器 / Steam)、*Avowed*（其 `GWorld` AOB 落在誘餌位址，改以掃描 `.data` 尋找指向作用中遊戲世界的指標來回復 — 取 `OwningGameInstance` 非空的那個，並略過 World-Partition 的 `_Generated_` cell）、劍星 (Stellar Blade)、女神異聞錄３ Reload，Pionero Capital Demo，MindsEye（UE 5.4.4 licensee fork，遊戲版本 7.3.1），以及《Star Trek Voyager – Across the Unknown》（原生 UE 5.6）。如遇尚未驗證的遊戲，請改用 **Object Tree** 或 **Instance Finder** 作為主要進入點。
* **EA 啟動器遊戲的 Proxy DLL 限制**: 《STAR WARS 絕地：組織殞落》透過 EA 啟動器啟動，`version.dll` 或 `dinput8.dll` proxy 皆無法被遊戲行程載入（EA 啟動器會限制 DLL 搜尋路徑）。需待遊戲啟動後改以 CE Dll Injection 注入。掃描端本身無問題 — DLL 進入行程後 GObjects / GNames / GWorld 均能正常解析。其他 EA 啟動器標題可能也有相同情況；若遇到請開 issue 回報。
* **針對既不匯入 `version.dll` 也不匯入 `dinput8.dll` 的遊戲使用 `dxgi.dll` proxy**: 少數遊戲（例如 *The Adventures of Elliot*，一款 SQUARE ENIX D3D12 遊戲 — 其 PE 版本字串被移除而被偵測為 UE4.27，但執行期已校正為 UE5.4；*Echoes of Aincrad Demo*，一款 Bandai Namco UE5.4 D3D12 遊戲）的 EXE 根本不匯入 `version.dll` 或 `dinput8.dll`，因此 Windows 永遠不會載入那些 proxy — 與 EA 的情況不同，這是遊戲自身的 import table，而非啟動器限制。請改用 **`dxgi.dll`** proxy（`build.ps1 -Target ProxyDxgi`，或在 Proxy Deploy 分頁選 *dxgi.dll*）：每款 D3D11/D3D12 UE 遊戲都會匯入 `dxgi`，因此能可靠載入。已在 Elliot、Echoes of Aincrad Demo，以及 *Pionero Capital Demo*（一款 stock UE5.7 遊戲 — 第一款透過此 proxy 驗證的 stock-5.7 遊戲）上端到端實測（連接 + 掃描 + 物件瀏覽）。
* **切到背景就暫停的遊戲**: 部分遊戲（例如女神異聞錄３ Reload，`t.IdleWhenNotForeground=1`）在非前景視窗時會凍結遊戲執行緒 — 此時遊戲執行緒上的呼叫會逾時。本工具會**偵測停擺**（顯示琥珀色「game thread stalled」橫幅；相機 POV 讀取改走 raw 快取而不會卡住管道），而實驗性的 **Keep Foreground** 開關能直接解決根本原因，讓遊戲在背景時呼叫仍可運作。
* **容器元素限制**: Array/Map/Set 的元素讀取受可調限制值控制，避免過度記憶體存取。如需處理大型容器，請在 Live Walker 中調整 **Array Limit** 滑桿。

---

## 專案貢獻 (Contributing)

請參閱 [CONTRIBUTING.md](CONTRIBUTING.md) 以瞭解：
- **回報偵測失敗** — 需要附上哪些日誌與資訊（最有幫助！）。
- **提交 AOB 特徵碼** — 給想直接貢獻的逆向工程者。
- **程式碼貢獻** — PR 流程與程式碼風格規範。

---

## 參考專案與致謝 (References & Credits)

| 專案 | 用途 |
|---|---|
| [Encryqed/Dumper-7](https://github.com/Encryqed/Dumper-7) | 動態偏移量偵測模式、FField/FProperty 探測策略 |
| [UE4SS-RE/RE-UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) | UE5 執行時反射機制參考 |
| [Spuckwaffel/UEDumper](https://github.com/Spuckwaffel/UEDumper) | 即時編輯器 UI 架構參考 |
| [trumank/patternsleuth](https://github.com/trumank/patternsleuth) | GObjects/GNames 的額外 AOB 特徵碼 |
| [Do0ks/GSpots](https://github.com/Do0ks/GSpots) | GObjects/GNames 的 AOB 特徵碼 |
| [nlohmann/json](https://github.com/nlohmann/json) | DLL 使用的 JSON 函式庫 |
| [cheat-engine/cheat-engine](https://github.com/cheat-engine/cheat-engine) | CE Lua 腳本 API 參考 |
| **AOBMaker (內部工具)** | AOB 特徵碼產生工具，AA 腳本產生工具、快速 CE-goto 功能 (非必備) |
| UE4 Dumper.CT | Cake-san 的 Cheat Table — 額外的 UE4 AOB 特徵碼（Signatures.h 中的 CT 系列） |

---

## 使用 Claude Code 開發

本專案在 Anthropic 的 [Claude Code](https://claude.ai/code) 協助下開發。C++ DLL、C# Avalonia UI、建置腳本及文件均由開發者與 Claude Code 協作完成。

---

**授權條款**: [MIT](LICENSE) © 2026 bbfox0703