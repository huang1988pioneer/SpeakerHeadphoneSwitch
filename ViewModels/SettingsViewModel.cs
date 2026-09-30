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
    private readonly IReadOnlyList<(AudioDevice Device, bool HasVolume)> _devices;

    // 下拉選單在更換清單時可能把選取值設回 null；null 一律視為「自動選擇」。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictText), nameof(CanSave))]
    private DeviceOption? _speaker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictText), nameof(CanSave))]
    private DeviceOption? _headphone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictText), nameof(CanSave))]
    private DeviceOption? _bluetoothSpeaker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictText), nameof(CanSave))]
    private DeviceOption? _bluetoothHeadphone;

    /// <summary>
    /// 預設只列出種類相符（或無法判斷）的裝置。名稱沒有關鍵字的藍牙裝置只能猜測種類，
    /// 猜錯時可勾選此項改列出所有種類。
    /// </summary>
    [ObservableProperty]
    private bool _showAllKinds;

    public SettingsViewModel(IAudioService audio, AppSettings settings)
    {
        _settings = settings;
        _devices = audio.GetActiveOutputDevices()
            .Select(device => (device, audio.TryGetEndpointVolumePercent(device.Id) is not null))
            .ToList();

        // 已指定的裝置若不在預設篩選內（例如之前以「顯示所有類型」指定），開啟時直接顯示所有類型。
        _showAllKinds = new[]
            {
                (settings.SpeakerDevice, DeviceKind.Speaker, false),
                (settings.HeadphoneDevice, DeviceKind.Headphone, false),
                (settings.BluetoothSpeakerDevice, DeviceKind.Speaker, true),
                (settings.BluetoothHeadphoneDevice, DeviceKind.Headphone, true),
            }
            .Any(slot => slot.Item1 is { } assigned
                         && _devices.Any(d => d.Device.Id == assigned.Id
                                              && !Matches(d.Device, slot.Item2, slot.Item3, showAllKinds: false)));

        RebuildOptions();
        _speaker = Find(SpeakerOptions, settings.SpeakerDevice);
        _headphone = Find(HeadphoneOptions, settings.HeadphoneDevice);
        _bluetoothSpeaker = Find(BluetoothSpeakerOptions, settings.BluetoothSpeakerDevice);
        _bluetoothHeadphone = Find(BluetoothHeadphoneOptions, settings.BluetoothHeadphoneDevice);
    }

    /// <summary>喇叭：非藍牙的喇叭與無法判斷種類的裝置。</summary>
    public IReadOnlyList<DeviceOption> SpeakerOptions { get; private set; } = [];

    /// <summary>耳機：非藍牙的耳機與無法判斷種類的裝置。</summary>
    public IReadOnlyList<DeviceOption> HeadphoneOptions { get; private set; } = [];

    /// <summary>藍牙喇叭：藍牙的喇叭與無法判斷種類的裝置。</summary>
    public IReadOnlyList<DeviceOption> BluetoothSpeakerOptions { get; private set; } = [];

    /// <summary>藍牙耳機：藍牙的耳機與無法判斷種類的裝置。</summary>
    public IReadOnlyList<DeviceOption> BluetoothHeadphoneOptions { get; private set; } = [];

    /// <summary>同一個裝置不能同時當喇叭又當耳機，否則無法判斷切換方向。</summary>
    public string ConflictText
    {
        get
        {
            var speakers = new[] { Speaker, BluetoothSpeaker }.Where(option => option?.Id is not null);
            var headphones = new[] { Headphone, BluetoothHeadphone }.Where(option => option?.Id is not null);
            var conflict = speakers.FirstOrDefault(s => headphones.Any(h => h!.Id == s!.Id));
            return conflict is null ? string.Empty : $"「{conflict.Name}」不能同時指定為喇叭與耳機。";
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

    partial void OnShowAllKindsChanged(bool value)
    {
        // 更換清單前記下選取值，更換後以 Id 找回，避免下拉選單重設造成指定遺失。
        var selected = (Speaker, Headphone, BluetoothSpeaker, BluetoothHeadphone);
        RebuildOptions();
        OnPropertyChanged(nameof(SpeakerOptions));
        OnPropertyChanged(nameof(HeadphoneOptions));
        OnPropertyChanged(nameof(BluetoothSpeakerOptions));
        OnPropertyChanged(nameof(BluetoothHeadphoneOptions));
        Speaker = Reselect(SpeakerOptions, selected.Speaker);
        Headphone = Reselect(HeadphoneOptions, selected.Headphone);
        BluetoothSpeaker = Reselect(BluetoothSpeakerOptions, selected.BluetoothSpeaker);
        BluetoothHeadphone = Reselect(BluetoothHeadphoneOptions, selected.BluetoothHeadphone);
    }

    private void RebuildOptions()
    {
        SpeakerOptions = BuildOptions(DeviceKind.Speaker, false, _settings.SpeakerDevice, Speaker);
        HeadphoneOptions = BuildOptions(DeviceKind.Headphone, false, _settings.HeadphoneDevice, Headphone);
        BluetoothSpeakerOptions = BuildOptions(DeviceKind.Speaker, true, _settings.BluetoothSpeakerDevice, BluetoothSpeaker);
        BluetoothHeadphoneOptions = BuildOptions(DeviceKind.Headphone, true, _settings.BluetoothHeadphoneDevice, BluetoothHeadphone);
    }

    private List<DeviceOption> BuildOptions(DeviceKind kind, bool bluetooth, DeviceAssignment? saved, DeviceOption? selected)
    {
        var options = new List<DeviceOption> { AutoOption };
        options.AddRange(_devices
            .Where(d => Matches(d.Device, kind, bluetooth, ShowAllKinds) || d.Device.Id == selected?.Id)
            // 種類相符的排在前面，其次是無法判斷種類的裝置。
            .OrderBy(d => d.Device.Kind == kind ? 0 : d.Device.Kind == DeviceKind.Unknown ? 1 : 2)
            .Select(d => new DeviceOption(d.Device.Id, d.Device.Name, Describe(d.Device, d.HasVolume))));

        // 已指定但目前未連線的裝置仍列出，避免開啟設定再儲存就把指定清掉。
        if (saved is not null && options.All(option => option.Id != saved.Id))
            options.Add(new DeviceOption(saved.Id, saved.Name, "未連線"));

        return options;
    }

    /// <summary>藍牙目標只列藍牙裝置、其餘只列非藍牙裝置；預設另依種類篩選（保留無法判斷的裝置）。</summary>
    private static bool Matches(AudioDevice device, DeviceKind kind, bool bluetooth, bool showAllKinds) =>
        device.IsBluetooth == bluetooth
        && (showAllKinds || device.Kind == kind || device.Kind == DeviceKind.Unknown);

    private static DeviceOption Find(IReadOnlyList<DeviceOption> options, DeviceAssignment? assigned) =>
        assigned is null ? AutoOption : options.FirstOrDefault(option => option.Id == assigned.Id) ?? AutoOption;

    private static DeviceOption Reselect(IReadOnlyList<DeviceOption> options, DeviceOption? selected) =>
        options.FirstOrDefault(option => option.Id == selected?.Id) ?? AutoOption;

    private static DeviceAssignment? ToAssignment(DeviceOption? option) =>
        option?.Id is null ? null : new DeviceAssignment { Id = option.Id, Name = option.Name };

    private static string Describe(AudioDevice device, bool hasVolume)
    {
        var kind = device.Kind switch
        {
            DeviceKind.Speaker => "喇叭",
            DeviceKind.Headphone => "耳機",
            _ => "其他",
        };
        return hasVolume ? kind : $"{kind} · 無法調整音量";
    }
}
