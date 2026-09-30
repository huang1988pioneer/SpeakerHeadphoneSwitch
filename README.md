# SpeakerHeadphoneSwitch（喇叭／耳機切換器）

以 Avalonia UI 製作的 Windows 桌面工具：按一下按鈕，在喇叭與耳機之間安全切換預設輸出裝置。

## 功能

- 按鈕文字依目前裝置變化：目前是耳機 →「切換至喇叭」；目前是喇叭 →「切換至耳機」。
- 流程固定為：目前裝置端點音量 **0%** → 切換 Windows 預設輸出 → 目標裝置端點音量 **50%**。
- 每次音量寫入都會從 Windows Core Audio 端點讀回確認；未確認成功時不會顯示「切換完成」。
- 若無法先把目前端點音量驗證為 0%，流程會停止，不會冒險切換。
- 預設端點切換或目標音量驗證失敗時，會盡力恢復原本的裝置與音量。
- 視窗顯示目前裝置名稱、輸出種類、目前端點音量、目標種類與執行狀態。
- 內建重新讀取按鈕、鍵盤 Enter 操作、切換中的忙碌狀態與清楚的錯誤提示。

## 建置與執行

```bash
dotnet build -c Debug
bin\Debug\net8.0-windows\SpeakerHeadphoneSwitch.exe
```

需要 Windows 10/11 與 .NET 8 SDK（僅建置時需要）。

## 技術架構

- **UI**：Avalonia 11（Fluent 主題）＋ CommunityToolkit.Mvvm；深色高對比操作面板與向量圖示。
- **切換預設裝置**：`IPolicyConfig::SetDefaultEndpoint`（未公開 COM）。優先使用 Windows 10/11 介面
  （`CA286FC3-...`），不支援時自動退回 Windows 7/8 介面（`F8679F50-...`）；三種角色
  （Console / Multimedia / Communications）一起設定。
- **裝置音量**：使用 `IAudioEndpointVolume` 的端點主音量，這才是 Windows 設定中的裝置音量滑桿。
  切換流程不以工作階段（session）音量冒充端點音量；端點 API 不可用或讀回值不符時會明確回報失敗。
- **裝置分類**：先看 `PKEY_AudioEndpoint_FormFactor`（Headphones/Headset → 耳機、Speakers → 喇叭），
  再以名稱關鍵字（耳機/headphone/headset、喇叭/speaker）後備判斷。

### 切換時間與驗證

音量端點在切換後若尚未完成初始化，程式會以固定次數短暫重試；預設裝置也會等待 Windows 回報目標端點。
正常情況下整個流程遠低於 3 秒，且只有在「目前端點讀回 0%」與「目標端點讀回 50%」都成立時才顯示完成。

### 切換錯誤處理

UI 會將切換拆成三個可辨識的步驟：先靜音、切換輸出、設定 50%。音量設定與裝置切換在背景工作執行，避免 COM 操作卡住視窗；UI 只在每個步驟完成後更新狀態。

## 實機回歸測試

可在 Windows 上執行端到端驗證工具：

```bash
dotnet run --project tools\EndpointSwitchProbe\EndpointSwitchProbe.csproj -- --verify-switch
```

工具會短暫執行「0% → 切換 → 50%」，以獨立的端點 COM 讀取確認結果，並在結束時盡力恢復測試前的裝置與音量。

## 工具

- `tools\AudioSwitchTest`：主控台診斷程式，列舉輸出裝置、讀寫音量、測試預設裝置切換。
- `tools\EndpointSwitchProbe`：實際 Windows 端點音量切換的回歸驗證工具。
- `tools\repair-audio-volume.ps1`：以系統管理員重啟音訊服務並驗證音量 API。
