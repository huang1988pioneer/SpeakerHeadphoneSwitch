# SpeakerHeadphoneSwitch（喇叭／耳機切換器）

以 Avalonia UI 製作的 Windows 桌面工具：按一下按鈕，在喇叭與耳機之間安全切換預設輸出裝置。

## 功能

- 按鈕文字依目前裝置變化：目前是耳機 →「切換至喇叭」；目前是喇叭 →「切換至耳機」。
- 流程固定為：目前裝置音量 **0%** → 切換 Windows 預設輸出 → 目標裝置音量 **33%**。
- 若無法先把目前音量設為 0%，流程會停止，不會冒險切換。
- 預設端點切換失敗時，會盡力恢復原本的裝置與音量。
- 視窗顯示目前裝置名稱、輸出種類、目前音量、目標種類與執行狀態。
- 內建重新讀取按鈕、鍵盤 Enter 操作、切換中的忙碌狀態與清楚的錯誤提示。

## 建置與執行

```bash
dotnet build -c Debug
bin\Debug\net8.0-windows\SpeakerHeadphoneSwitch.exe
```

需要 Windows 10/11 與 .NET 8 SDK(僅建置時需要)。

## 技術架構

- **UI**:Avalonia 11(Fluent 主題)+ CommunityToolkit.Mvvm；深色高對比操作面板與向量圖示。
- **切換預設裝置**:`IPolicyConfig::SetDefaultEndpoint`(未公開 COM)。優先使用 Windows 10/11 介面
  (`CA286FC3-...`),不支援時自動退回 Windows 7/8 介面(`F8679F50-...`);三種角色
  (Console / Multimedia / Communications)一起設定。
- **音量**:優先使用 `IAudioEndpointVolume`(即系統設定中的「裝置主音量」)。
  若系統拒絕該介面,退回 `IAudioSessionManager2`:設定「預設工作階段」與「所有現存工作階段」
  的音量(對正在發聲的應用程式聽覺效果等同裝置音量)。
- **裝置分類**:先看 `PKEY_AudioEndpoint_FormFactor`(Headphones/Headset → 耳機,Speakers → 喇叭),
  再以名稱關鍵字(耳機/headphone/headset、喇叭/speaker)後備判斷。

### 切換錯誤處理

UI 會將切換拆成三個可辨識的步驟：先靜音、切換輸出、設定 33%。音量設定與裝置切換在背景工作執行，避免 COM 操作卡住視窗；UI 只在每個步驟完成後更新狀態。

## 本機(測試機)已知問題

這台 Windows 11(組建 26200)對**一般程序**啟用 `IAudioEndpointVolume` 一律回傳
`E_NOINTERFACE`(喇叭、耳機、HDMI、麥克風全部一樣),但系統自身的音量鍵/滑桿正常。
因此 App 在這台機器上只能調整「工作階段音量」,**無法直接搬動系統設定中的裝置音量滑桿**。

修復建議(依序嘗試):

1. 重開機(音訊服務重新初始化)。
2. 以系統管理員執行 `tools\repair-audio-volume.ps1`(重啟音訊服務後自動測試音量 API)。
3. 重建/更新音效驅動(Realtek UAD),或執行 Windows Update 修補。

在正常的 Windows 系統上,本 App 會直接使用 `IAudioEndpointVolume`,行為完全符合預期。

## 工具

- `tools\AudioSwitchTest`:主控台診斷程式,列舉輸出裝置、讀寫音量、測試預設裝置切換。
- `tools\repair-audio-volume.ps1`:以系統管理員重啟音訊服務並驗證音量 API(見上)。
