# 以系統管理員執行:重啟 Windows 音訊服務,並測試裝置音量 API 是否恢復正常。
# 用法:在檔案總管對本檔右鍵 ->「以系統管理員身分執行」,
#       或在系統管理員 PowerShell 中執行: powershell -ExecutionPolicy Bypass -File repair-audio-volume.ps1
# 說明:本機對一般程序啟用 IAudioEndpointVolume(裝置主音量)一律回傳 E_NOINTERFACE,
#       但系統音量鍵卻正常,疑似音訊服務狀態異常。重啟服務有機會恢復;若無效請重開機,
#       或重建/更新音效驅動(Realtek UAD)。

Write-Host "== 重啟 Windows 音訊服務(音訊會中斷數秒)... ==" -ForegroundColor Cyan
Restart-Service AudioEndpointBuilder -Force
Start-Sleep -Seconds 3
Get-Service Audiosrv, AudioEndpointBuilder | Format-Table -AutoSize Status, Name

Write-Host "== 測試裝置音量 API(列舉裝置並把預設裝置設為 50%)==" -ForegroundColor Cyan
& "$PSScriptRoot\AudioSwitchTest\bin\Debug\net8.0-windows\AudioSwitchTest.exe"

Write-Host "== 完成。若上方輸出中的音量可正常讀寫即已修復;請重新啟動 SpeakerHeadphoneSwitch 驗證。==" -ForegroundColor Green
Pause
