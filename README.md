# Idle Spiral 加速補丁（OpenSpeedy 100–1000×）

讓 Steam 版 **Idle Spiral** 在 **OpenSpeedy** 開到 100×～1000× 時不再卡死，並且順暢運作。

> 📦 **本專案已封存（2026-10-24）**，不再更新。最後狀態請以 [Release `archive-2026-10-24`](https://github.com/swear01/idle-spiral-openspeedy-patch/releases/tag/archive-2026-10-24) 為準。

## 這是什麼？

OpenSpeedy 用加快時鐘的方式讓遊戲變快。Idle Spiral 開到 100× 以上時，Steam、網路和畫面的計時也會跟著被加快，常常卡住好幾秒甚至凍結，倍率也跑不上去。

這個補丁在遊戲**執行時**，從記憶體裡調整遊戲的行為：只讓遊戲模擬加速，其他部分維持正常速度，並修正加速後才會出現的問題。

- 不修改遊戲檔案，也不改存檔格式。
- 每次從 Steam 啟動時自動載入；移除後遊戲就回到原狀。
- 戰鬥、收益、掉落、Steam 成就、Google 登入和雲端存檔都照常運作。

## 補丁修了什麼？

| 問題 | 補丁的處理 |
|---|---|
| 開到 100× 以上，每隔一陣子卡住 7～14 秒，因為 Steam 的等待也被加速了 | 只有遊戲模擬加速；Steam、網路、畫面維持正常速度 |
| 倍率跑不上去：設 1000×，實際曾只有約 450×～800× | 合併模擬步、減少每幀的多餘工作；實測 200×、400× 約 59 FPS，1000× 實際約 998× |
| 只加速模擬之後，照真實時間計時的功能（自動購買、自動轉生）會變慢 | 改成跟著倍率加速 |
| 視窗切到背景後，遊戲把自己限制在 20 FPS，畫面一卡一卡 | 背景也維持設定的幀率 |
| 戰鬥「自動開始」的難度數字一直亂跳，畫面很卡 | 戰鬥面板每幀只更新一次 |
| Daily Spiral：直接加速時鐘會把未來的日期寫進存檔，鎖住好幾天 | 每 86400 遊戲秒（1000× 約 86 秒）多重置一次，重置倒數也跟著倍率；存檔只寫入真實日期 |
| Theory「Pythagorean theorem」的 Auto Study 每真實秒只升 100 級 | 加速時，每秒把資源買得起的等級一次買完，不會卡頓 |
| 其他 9 個 Theory 沒有自動升級 | 每個 Theory 都有 Auto Study 開關，預設開啟；EXP 滿了就自動升級 |

## 需要什麼

- Windows 上的 Steam 版 Idle Spiral **3.0.82 build 117**
- [OpenSpeedy](https://github.com/game1024/OpenSpeedy) **3.3.8**，安裝在 `C:\Program Files\OpenSpeedy\`
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

> ⚠️ 遊戲或 OpenSpeedy 更新到其他版本後，補丁會因為檔案雜湊不符而**拒絕載入**，遊戲以原本的樣子執行，不會被弄壞。

## 安裝

1. 到 [Releases](https://github.com/swear01/idle-spiral-openspeedy-patch/releases) 下載 `idle-spiral-openspeedy-patch-package.zip`，解壓縮。
2. **完全關閉遊戲。**
3. 在解壓縮出來的資料夾開 PowerShell，執行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-SteamAutoload.ps1 -Action Install
   ```

   遊戲不在 `E:\SteamLibrary\steamapps\common\Idle Spiral` 的話，加上 `-GameDirectory "你的遊戲資料夾"`。OpenSpeedy 不在預設位置的話，加上 `-OpenSpeedyDll "speedpatch64.dll 的完整路徑"`。

4. 從 Steam 按「開始遊戲」。之後每次啟動都會自動載入，不必再執行任何指令。

## 怎麼用

1. 遊戲啟動後一律是 **1×**。
2. 打開 OpenSpeedy，在列表找到 `IdleSpiral.exe`，打開它的 **Speed 開關**。
3. 用拉桿調整倍率，最高 1000×；也可以點速度數字直接輸入，例如 `400`。改了立刻生效。
4. 要停止加速：**先關掉開關，再關閉 OpenSpeedy**。直接關閉 OpenSpeedy 的話，遊戲會停在最後的倍率。
5. Theory 面板：每個 Theory 下方都有 Auto Study 勾選框，取消勾選就會停止那個 Theory 的自動升級。

## 升級、移除、查看狀態

每次執行前都要先關閉遊戲，`Status` 除外。

```powershell
# 升級到新版本
powershell -ExecutionPolicy Bypass -File .\Install-SteamAutoload.ps1 -Action Upgrade

# 移除（只刪除本工具建立且內容沒變的檔案；存檔與原遊戲檔案不會動）
powershell -ExecutionPolicy Bypass -File .\Install-SteamAutoload.ps1 -Action Remove

# 查看目前狀態
powershell -ExecutionPolicy Bypass -File .\Install-SteamAutoload.ps1 -Action Status
```

狀態檔放在遊戲資料夾裡的 `IdleSpiralSpeedPatch\`。看到任何 `*-error.txt`，表示那一部分載入失敗，請先看裡面的錯誤訊息。

## 已知限制

- **Pythagorean 會把資源花光**：加速時，Auto Study 每秒都會用掉當下全部能用的 Inspiration 和 Theory 材料。想保留資源，就先把它的 Auto Study 取消勾選。
- **戰鬥自動開始的特例**：目標難度低於 Nuke 的起始難度時，每贏一批就會重開一場。1000× 下這種情況約 21 FPS，這是遊戲規則本身的成本。
- **時鐘偏差**：OpenSpeedy 的時間 hook 可能讓 `DateTime.Now` 固定快 15～18 分鐘。遊戲自己的日曆（Daily Spiral、自動存檔間隔等）已改用真實時間，不受影響。
- **未完整驗證**：新成就的解鎖與上傳沒有做完整的端到端驗證。卡死問題是實測後的緩解，不保證所有情況都永不凍結。

## 運作原理（給想看程式碼的人）

```text
Steam 啟動遊戲
  └─ Doorstop（winhttp.dll）啟動隱藏的 .NET 8 小程式
       └─ 等遊戲初始化完成（至少 2 秒，最多 90 秒）
            └─ 依序把以下 Harmony 補丁注入遊戲，完成後小程式就結束
```

| 資料夾 | 產出 DLL | 作用 |
|---|---|---|
| `Isolation/` | ClockIsolation | **核心**。讓 OpenSpeedy 的原生時鐘 hook 只回報 1×，遊戲模擬改由這裡套用一次倍率；遊戲還是讀 OpenSpeedy 的開關與倍率 |
| `Runtime/` | SpeedPatch.SteamGuard | 載入 OpenSpeedy DLL、限制每幀模擬量的基礎補丁 |
| `MacroStep/` | MacroStepGuard | 倍率 >100× 時，每次模擬合併多個 0.02 秒步，最多 0.4 遊戲秒 |
| `Housekeeping/` | HousekeepingGuard2 | 暫停分析回報、限制 Steam 回呼頻率、高倍率的模擬預算、背景幀率 |
| `Automation/` | AutomationGuard | 自動購買／轉生、戰鬥面板、Daily Spiral、Theory 自動升級 |
| `DesignCount/` | DesignCountGuard | 輸入相同時，重用設計解鎖統計的計算結果 |
| `Render/` | RenderGuard | 隱藏中央螺旋（角度與成就照常） |
| `Theory/` | TheoryGuard | 同一次 Theory 更新內，共用重複的進度讀取 |
| `Adaptive/` | AdaptiveGuard | 低倍率時的模擬時間預算 |
| `Auth/`、`Clock/` | AuthGuard、CalendarGuard | 登入憑證與遊戲日曆維持真實時間 |
| `Bootstrap/`、`Injector/`、`MonoHost/` | Autoload、PatchLoader | 自動載入與注入工具 |

其他資料夾（`*Probe`、`HookAudit`、`NativeDiagnostic`、`Formula`、`Production`、`Recurrence`、`CurrencyTick`、`AuthDiagnostic`）是開發時用的量測工具或沒採用的實驗，沒有收進安裝包。

每個補丁載入前都會比對遊戲與 OpenSpeedy 的檔案雜湊；不符就拒絕載入。原生程式碼完全沒有修改：ClockIsolation 只改了 OpenSpeedy 內部一個資料指標，移除時會還原。

更多說明：

- [使用說明.md](使用說明.md)：各項功能細節與狀態檔說明
- [PERFORMANCE.md](PERFORMANCE.md)：每項修正的根因、量測數據與驗證紀錄

## 授權

- [UnityDoorstop 4.5.0](https://github.com/NeighTools/UnityDoorstop/releases/tag/v4.5.0)：LGPL-2.1。安裝包附官方原始碼 zip，未修改。
- [Harmony 2.4.3](https://github.com/pardeike/Harmony)：MIT。
- `vendor/` 的 SharpMonoInjector 程式碼：MIT，見 `vendor/LICENSE-SharpMonoInjector.txt`。
- OpenSpeedy 不隨本專案散布，請自行安裝。
- 本 repo 自己的程式碼沒有附授權條款。
