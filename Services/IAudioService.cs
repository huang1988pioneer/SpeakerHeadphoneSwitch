using System;
using System.Collections.Generic;
using SpeakerHeadphoneSwitch.Models;

namespace SpeakerHeadphoneSwitch.Services;

/// <summary>
/// 跨平台音訊操作：列舉輸出裝置、讀寫裝置主音量、切換預設輸出裝置。
/// Windows 以 Core Audio COM 實作，macOS 以 CoreAudio HAL 實作。
/// </summary>
public interface IAudioService
{
    /// <summary>顯示在 UI 訊息中的作業系統名稱。</summary>
    string SystemName { get; }

    /// <summary>取得目前的預設輸出裝置。</summary>
    AudioDevice? GetDefaultOutputDevice();

    /// <summary>列舉所有可用的輸出裝置。</summary>
    IReadOnlyList<AudioDevice> GetActiveOutputDevices();

    /// <summary>讀取裝置音量（0-100%），允許平台特定的後備來源。</summary>
    float? TryGetVolumePercent(string deviceId);

    /// <summary>僅讀取對應系統音量滑桿的裝置主音量（0-100%）。</summary>
    float? TryGetEndpointVolumePercent(string deviceId);

    /// <summary>設定裝置音量（0-100%），允許平台特定的後備方式。</summary>
    bool TrySetVolumePercent(string deviceId, float percent);

    /// <summary>僅設定對應系統音量滑桿的裝置主音量（0-100%）。</summary>
    bool TrySetEndpointVolumePercent(string deviceId, float percent);

    /// <summary>將指定裝置設為預設輸出裝置；失敗時丟出例外。</summary>
    void SetDefaultOutputDevice(string deviceId);
}

public static class AudioServiceFactory
{
    public static IAudioService Create()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsAudioService();
        if (OperatingSystem.IsMacOS())
            return new MacAudioService();
        throw new PlatformNotSupportedException("目前只支援 Windows 與 macOS。");
    }
}
