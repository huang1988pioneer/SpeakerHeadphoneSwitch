using System;
using System.IO;
using System.Text.Json;

namespace SpeakerHeadphoneSwitch.Services;

/// <summary>
/// 使用者偏好設定，存放於本機應用程式資料夾
/// （Windows：%LOCALAPPDATA%，macOS：~/Library/Application Support）。
/// </summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpeakerHeadphoneSwitch",
        "settings.json");

    /// <summary>切換至喇叭時優先使用藍牙喇叭。</summary>
    public bool UseBluetoothSpeaker { get; set; }

    /// <summary>切換至耳機時優先使用藍牙耳機。</summary>
    public bool UseBluetoothHeadphone { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 設定檔損毀或無法讀取時使用預設值，不影響切換功能。
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 無法寫入時僅影響下次啟動的預設值。
        }
    }
}
