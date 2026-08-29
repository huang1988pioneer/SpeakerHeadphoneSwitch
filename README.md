# 聲音快捷切換

使用 Avalonia UI 製作的喇叭／耳機一鍵切換工具。

按下主按鈕後會固定依序執行：

1. 將目前系統輸出音量調整至 0%。
2. 切換 macOS 預設輸出裝置（CoreAudio）。
3. 將新輸出裝置音量調整至 33%。

## 執行

```bash
dotnet restore --ignore-failed-sources
dotnet run
```

目前平台支援：

- macOS：使用 CoreAudio 列舉與切換輸出裝置，使用系統音量控制設定音量。
- Linux：使用 `pactl`，相容 PulseAudio 與 PipeWire 的 PulseAudio 相容層。

裝置名稱會依名稱自動辨識「喇叭」與「耳機」；macOS 回傳的「揚聲器」名稱會在介面中顯示為「喇叭」。若名稱無法辨識，會列為其他輸出，仍可作為切換候選。切換前請確認系統中已連接至少兩個輸出裝置。
