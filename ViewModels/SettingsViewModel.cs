using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using SpeakerHeadphoneSwitch.Models;
using SpeakerHeadphoneSwitch.Services;

namespace SpeakerHeadphoneSwitch.ViewModels;

/// <summary>下拉選單中的一個選項；Id 為 null 代表「自動選擇」。</summary>
public sealed record DeviceOption(string? Id, string Name, string Detail)
{
    public string Label => string.IsNullOrEmpty(Detail) ? Name : $"{Name}（{Detail}）";

    public override string ToString() => Label;
}

public partial class SettingsViewModel : ObservableObject
{
    private static readonly DeviceOption AutoOption = new(null, "自動選擇", string.Empty);

    private readonly AppSettings _settings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictText), nameof(CanSave))]
    private DeviceOption _speaker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictText), nameof(CanSave))]
    private DeviceOption _headphone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictText), nameof(CanSave))]
    private DeviceOption _bluetoothSpeaker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictText), nameof(CanSave))]
    private DeviceOption _bluetoothHeadphone;

    public SettingsViewModel(IAudioService audio, AppSettings settings)
    {
        _settings = settings;

        var options = new List<DeviceOption> { AutoOption };
        options.AddRange(audio.GetActiveOutputDevices().Select(device => new DeviceOption(
            device.Id,
            device.Name,
            Describe(device, audio.TryGetEndpointVolumePercent(device.Id) is not null))));

        // 已指定但目前未連線的裝置仍列出，避免開啟設定再儲存就把指定清掉。
        foreach (var assigned in new[]
                 {
                     settings.SpeakerDevice, settings.HeadphoneDevice,
                     settings.BluetoothSpeakerDevice, settings.BluetoothHeadphoneDevice,
                 })
        {
            if (assigned is not null && options.All(option => option.Id != assigned.Id))
                options.Add(new DeviceOption(assigned.Id, assigned.Name, "未連線"));
        }

        Options = options;
        _speaker = Find(settings.SpeakerDevice);
        _headphone = Find(settings.HeadphoneDevice);
        _bluetoothSpeaker = Find(settings.BluetoothSpeakerDevice);
        _bluetoothHeadphone = Find(settings.BluetoothHeadphoneDevice);
    }

    public IReadOnlyList<DeviceOption> Options { get; }

    /// <summary>同一個裝置不能同時當喇叭又當耳機，否則無法判斷切換方向。</summary>
    public string ConflictText
    {
        get
        {
            var speakerIds = new[] { Speaker.Id, BluetoothSpeaker.Id }.OfType<string>();
            var headphoneIds = new[] { Headphone.Id, BluetoothHeadphone.Id }.OfType<string>();
            var conflict = speakerIds.Intersect(headphoneIds).FirstOrDefault();
            return conflict is null
                ? string.Empty
                : $"「{Options.First(option => option.Id == conflict).Name}」不能同時指定為喇叭與耳機。";
        }
    }

    public bool CanSave => ConflictText.Length == 0;

    public void Save()
    {
        _settings.SpeakerDevice = ToAssignment(Speaker);
        _settings.HeadphoneDevice = ToAssignment(Headphone);
        _settings.BluetoothSpeakerDevice = ToAssignment(BluetoothSpeaker);
        _settings.BluetoothHeadphoneDevice = ToAssignment(BluetoothHeadphone);
        _settings.Save();
    }

    private DeviceOption Find(DeviceAssignment? assigned) =>
        assigned is null ? AutoOption : Options.FirstOrDefault(option => option.Id == assigned.Id) ?? AutoOption;

    private static DeviceAssignment? ToAssignment(DeviceOption option) =>
        option.Id is null ? null : new DeviceAssignment { Id = option.Id, Name = option.Name };

    private static string Describe(AudioDevice device, bool hasVolume)
    {
        var kind = device.Kind switch
        {
            DeviceKind.Speaker => "喇叭",
            DeviceKind.Headphone => "耳機",
            _ => "其他",
        };
        var parts = new List<string> { device.IsBluetooth ? $"藍牙{kind}" : kind };
        if (!hasVolume)
            parts.Add("無法調整音量");
        return string.Join(" · ", parts);
    }
}
