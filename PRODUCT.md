# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

## Stack

delegated: existing Avalonia UI 11 desktop app on .NET 8; the current audio bridge targets Windows Core Audio because the requested device switching is a Windows operation.

## Users

主要使用者（假設）是在 Windows 桌機前工作或娛樂、需要頻繁在喇叭與耳機之間切換音訊輸出的使用者。

## Product Purpose

以一個按鈕完成喇叭／耳機預設輸出切換。切換前先把目前輸出音量設為 0%，切換完成後把目標輸出音量設為 33%，降低切換瞬間爆音的風險。

## Positioning

產品的核心機制是固定且可理解的安全切換順序：目前裝置靜音（0%）→ 設為預設輸出 → 目標裝置音量 33%。

## Operating Context

使用者通常在播放音樂、影片或遊戲時操作，期望從小型常駐視窗快速完成切換，不必打開 Windows 音效設定頁面。裝置名稱與目前音量是操作後需要立即確認的狀態。

## Capabilities and Constraints

- 主要流程只處理輸出裝置，目標優先選擇另一個已啟用且分類為耳機或喇叭的裝置。
- 目前輸出裝置音量在切換前設為 0%，目標輸出裝置在切換後設為 33%。
- 需要顯示目前預設裝置名稱、裝置種類、音量與切換結果；找不到裝置或音量 API 不可用時要明確告知。
- Windows Core Audio 的裝置分類、端點音量與預設裝置切換可能受驅動程式與作業系統版本限制。
- 具體的跨平台音訊實作尚未決定；現階段以 Windows 10/11 為交付目標。

## Brand Commitments

目前沒有既有品牌名稱、標誌或視覺資產。

## Evidence on Hand

- `SpeakerHeadphoneSwitch.csproj`：Avalonia 11、Fluent 主題、CommunityToolkit.Mvvm 與 Windows-only target。
- `Services/WindowsAudioService.cs` 與 `Services/ComInterfaces.cs`：Windows Core Audio 的裝置列舉、分類、音量與預設端點切換實作。
- `ViewModels/MainWindowViewModel.cs`：已存在切換順序與繁體中文狀態文字的初版流程。
- 沒有真實使用者研究、品牌素材或可引用的產品數據；不得捏造此類內容。

## Product Principles

1. 一次操作完成切換。
2. 先安全降音量，再切換輸出。
3. 每一步都讓使用者看得懂目前狀態。
4. 系統能力不足時清楚回報，不假裝成功。

## Accessibility & Inclusion

未指定特定標準；本次至少保留鍵盤可操作的主要按鈕、清楚的文字狀態與不依賴顏色的成功／失敗訊息。
