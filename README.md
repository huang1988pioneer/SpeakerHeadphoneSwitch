# SpeakerHeadphoneSwitch（喇叭／耳機切換器）

以 Avalonia UI 製作的 Windows／macOS 桌面工具：按一下按鈕，在喇叭與耳機之間安全切換預設輸出裝置。

## 功能

- 按鈕文字依目前裝置變化：目前是耳機 →「切換至喇叭」；目前是喇叭 →「切換至耳機」。
- 流程固定為：目前裝置端點音量 **0%** → 切換 Windows 預設輸出 → 目標裝置端點音量 **50%**。
- 每次音量寫入都會從 Windows Core Audio 端點讀回確認；未確認成功時不會顯示「切換完成」。
- 若無法先把目前端點音量驗證為 0%，流程會停止，不會冒險切換。
- 預設端點切換或目標音量驗證失敗時，會盡力恢復原本的裝置與音量。
- 視窗顯示目前裝置名稱、輸出種類、目前端點音量、目標種類與執行狀態。
- 「喇叭使用藍牙」「耳機使用藍牙」勾選框：勾選時優先切換到藍牙裝置（例如藍牙喇叭、藍牙耳機），
  未勾選時優先使用內建／有線裝置；偏好的裝置未連線時退回同種類的其他裝置。設定會保存在
  本機應用程式資料夾（Windows `%LOCALAPPDATA%`、macOS `~/Library/Application Support`）。
- 「設定」視窗可分別指定喇叭、耳機、藍牙喇叭、藍牙耳機對應的裝置（或「自動選擇」）。指定的裝置
  優先於自動判斷，也決定該裝置被視為喇叭或耳機；指定的裝置未連線時改用自動選擇並在狀態列提示。
- 設定中的「藍牙裝置種類」可手動標注每個藍牙裝置是耳機或喇叭（名稱無法判斷時使用），
  標注優先於自動偵測。
- 沒有音量控制的裝置（例如 HDMI 螢幕）不會被選為切換目標。
- 內建重新讀取按鈕、鍵盤 Enter 操作、切換中的忙碌狀態與清楚的錯誤提示。

## 建置與執行

```bash
dotnet build -c Debug
bin\Debug\net8.0\SpeakerHeadphoneSwitch.exe
```

需要 Windows 10/11 與 .NET 8 SDK（僅建置時需要）。

### macOS 版與安裝包

```bash
packaging/macos/build-macos.sh
```

需要 macOS 12 以上與 .NET 8 SDK（僅建置時需要）。腳本會以自帶執行階段發佈，組成
`dist/macos/SpeakerHeadphoneSwitch.app`、以 ad-hoc 簽章，並產生可拖曳安裝的
`dist/macos/SpeakerHeadphoneSwitch-<版本>-<RID>.dmg`。預設依本機架構建置，也可指定
`osx-arm64` 或 `osx-x64`；`VERSION`、`CODESIGN_IDENTITY` 環境變數可覆寫版本與簽章身分。

圖示由 `swift packaging/macos/make-icon.swift` 產生：`packaging/macos/AppIcon.icns`（macOS bundle）、
`Assets/AppIcon.png`（視窗與 Dock 圖示）、`Assets/AppIcon.ico`（Windows exe 圖示）。

ad-hoc 簽章未經 Apple 公證；從其他電腦下載的 DMG 第一次開啟時，請在 Finder 中按右鍵 →「打開」。

## 技術架構

- **UI**：Avalonia 11（Fluent 主題）＋ CommunityToolkit.Mvvm；深色高對比操作面板與向量圖示。
- **切換預設裝置**：`IPolicyConfig::SetDefaultEndpoint`（未公開 COM）。優先使用 Windows 10/11 介面
  （`CA286FC3-...`），不支援時自動退回 Windows 7/8 介面（`F8679F50-...`）；三種角色
  （Console / Multimedia / Communications）一起設定。
- **裝置音量**：使用 `IAudioEndpointVolume` 的端點主音量，這才是 Windows 設定中的裝置音量滑桿。
  切換流程不以工作階段（session）音量冒充端點音量；端點 API 不可用或讀回值不符時會明確回報失敗。
- **macOS**：`MacAudioService` 以 CoreAudio HAL 列舉輸出裝置並切換 `kAudioHardwarePropertyDefaultOutputDevice`
  （同時設定系統提示音裝置）；音量優先使用 `VirtualMainVolume`（與選單列音量滑桿相同），
  不支援時退回主聲道或左右聲道的 `VolumeScalar`；
  設定非 0 音量時會一併解除靜音，靜音中的裝置一律讀為 0%，避免「數值 50% 但實際聽不到」。沒有音量控制的裝置（例如 HDMI 螢幕）不會被選為切換目標。
  分類依名稱、內建輸出的資料來源（耳機孔／內建揚聲器）與傳輸方式（藍牙視為耳機）。
- **Windows 裝置分類**：先看 `PKEY_AudioEndpoint_FormFactor`（Headphones/Headset → 耳機、Speakers → 喇叭），
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
