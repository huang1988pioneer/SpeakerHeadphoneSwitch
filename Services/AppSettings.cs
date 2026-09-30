using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SpeakerHeadphoneSwitch.Models;

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

    // 四種目標各自指定的裝置；null 表示自動選擇。
    public DeviceAssignment? SpeakerDevice { get; set; }
    public DeviceAssignment? HeadphoneDevice { get; set; }
    public DeviceAssignment? BluetoothSpeakerDevice { get; set; }
    public DeviceAssignment? BluetoothHeadphoneDevice { get; set; }

    /// <summary>手動標注的裝置種類（例如名稱無法判斷的藍牙裝置）；優先於自動偵測。</summary>
    public List<DeviceKindLabel> KindLabels { get; set; } = [];

    public DeviceKind? GetKindLabel(string deviceId) =>
        KindLabels.FirstOrDefault(label => label.Id == deviceId)?.Kind;

    /// <summary>設定或清除（kind 為 null）某裝置的手動種類。</summary>
    public void SetKindLabel(string deviceId, string name, DeviceKind? kind)
    {
        KindLabels.RemoveAll(label => label.Id == deviceId);
        if (kind is DeviceKind value)
            KindLabels.Add(new DeviceKindLabel { Id = deviceId, Name = name, Kind = value });
    }

    public DeviceAssignment? GetAssignment(DeviceKind kind, bool bluetooth) => (kind, bluetooth) switch
    {
        (DeviceKind.Speaker, false) => SpeakerDevice,
        (DeviceKind.Speaker, true) => BluetoothSpeakerDevice,
        (DeviceKind.Headphone, false) => HeadphoneDevice,
        (DeviceKind.Headphone, true) => BluetoothHeadphoneDevice,
        _ => null,
    };

    /// <summary>若裝置被指定到某個目標，回傳該目標的種類與是否為藍牙；否則回傳 null。</summary>
    public (DeviceKind Kind, bool Bluetooth)? FindAssignedSlot(string deviceId)
    {
        if (SpeakerDevice?.Id == deviceId) return (DeviceKind.Speaker, false);
        if (BluetoothSpeakerDevice?.Id == deviceId) return (DeviceKind.Speaker, true);
        if (HeadphoneDevice?.Id == deviceId) return (DeviceKind.Headphone, false);
        if (BluetoothHeadphoneDevice?.Id == deviceId) return (DeviceKind.Headphone, true);
        return null;
    }

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

    public static string SlotName(DeviceKind kind, bool bluetooth) =>
        (bluetooth ? "藍牙" : string.Empty) + (kind == DeviceKind.Speaker ? "喇叭" : "耳機");

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

/// <summary>指定給某個目標的裝置。保留名稱，裝置未連線時仍可在設定與訊息中顯示。</summary>
public sealed class DeviceAssignment
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>手動標注某裝置是喇叭或耳機。</summary>
public sealed class DeviceKindLabel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DeviceKind Kind { get; set; }
}
