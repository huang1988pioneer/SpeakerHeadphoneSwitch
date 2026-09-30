using System;
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

/// <summary>種類標注選項；Kind 為 null 代表沿用自動偵測。</summary>
public sealed record KindChoice(DeviceKind? Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>設定視窗中一個藍牙裝置的種類標注列。</summary>
public partial class BluetoothDeviceLabel : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty]
    private KindChoice? _selected;

    public BluetoothDeviceLabel(AudioDevice device, DeviceKind? label, Action changed)
    {
        Device = device;
        _changed = changed;
        Choices =
        [
            new KindChoice(null, $"自動（{SettingsViewModel.KindName(device.Kind)}）"),
            new KindChoice(DeviceKind.Headphone, "耳機"),
            new KindChoice(DeviceKind.Speaker, "喇叭"),
        ];
        _selected = Choices.First(choice => choice.Kind == label);
    }

    public AudioDevice Device { get; }

    public string Name => Device.Name;

    public IReadOnlyList<KindChoice> Choices { get; }

    /// <summary>目前生效的種類：手動標注優先，否則為自動偵測。</summary>
    public DeviceKind EffectiveKind => Selected?.Kind ?? Device.Kind;

    partial void OnSelectedChanged(KindChoice? value) => _changed();
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

    public SettingsViewModel(IAudioService audio, AppSettings settings)
    {
        _settings = settings;
        _devices = audio.GetActiveOutputDevices()
            .Select(device => (device, audio.TryGetEndpointVolumePercent(device.Id) is not null))
            .ToList();

        BluetoothLabels = _devices
            .Where(d => d.Device.IsBluetooth)
            .Select(d => new BluetoothDeviceLabel(d.Device, settings.GetKindLabel(d.Device.Id), OnLabelChanged))
            .ToList();

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

    /// <summary>目前連線的藍牙裝置，可手動標注為耳機或喇叭。</summary>
    public IReadOnlyList<BluetoothDeviceLabel> BluetoothLabels { get; }

    public bool HasBluetoothDevices => BluetoothLabels.Count > 0;

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
        foreach (var label in BluetoothLabels)
            _settings.SetKindLabel(label.Device.Id, label.Name, label.Selected?.Kind);

        _settings.SpeakerDevice = ToAssignment(Speaker);
        _settings.HeadphoneDevice = ToAssignment(Headphone);
        _settings.BluetoothSpeakerDevice = ToAssignment(BluetoothSpeaker);
        _settings.BluetoothHeadphoneDevice = ToAssignment(BluetoothHeadphone);
        _settings.Save();
    }

    internal static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Speaker => "喇叭",
        DeviceKind.Headphone => "耳機",
        _ => "其他",
    };

    /// <summary>標注改變後重建下拉選單，讓裝置立即出現在對應種類的選單中。</summary>
    private void OnLabelChanged()
    {
        // 更換清單前記下選取值，更換後以 Id 找回；已不屬於該種類的選取改回「自動選擇」。
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
        SpeakerOptions = BuildOptions(DeviceKind.Speaker, false, _settings.SpeakerDevice);
        HeadphoneOptions = BuildOptions(DeviceKind.Headphone, false, _settings.HeadphoneDevice);
        BluetoothSpeakerOptions = BuildOptions(DeviceKind.Speaker, true, _settings.BluetoothSpeakerDevice);
        BluetoothHeadphoneOptions = BuildOptions(DeviceKind.Headphone, true, _settings.BluetoothHeadphoneDevice);
    }

    private List<DeviceOption> BuildOptions(DeviceKind kind, bool bluetooth, DeviceAssignment? saved)
    {
        // 藍牙目標只列藍牙裝置、其餘只列非藍牙裝置；再依種類（含手動標注）篩選，保留無法判斷種類的裝置。
        var options = new List<DeviceOption> { AutoOption };
        options.AddRange(_devices
            .Where(d => d.Device.IsBluetooth == bluetooth)
            .Select(d => (d.Device, d.HasVolume, Kind: EffectiveKind(d.Device)))
            .Where(d => d.Kind == kind || d.Kind == DeviceKind.Unknown)
            .OrderBy(d => d.Kind == kind ? 0 : 1)
            .Select(d => new DeviceOption(
                d.Device.Id,
                d.Device.Name,
                d.HasVolume ? KindName(d.Kind) : $"{KindName(d.Kind)} · 無法調整音量")));

        // 已指定但目前未連線的裝置仍列出，避免開啟設定再儲存就把指定清掉。
        if (saved is not null
            && _devices.All(d => d.Device.Id != saved.Id)
            && options.All(option => option.Id != saved.Id))
            options.Add(new DeviceOption(saved.Id, saved.Name, "未連線"));

        return options;
    }

    private DeviceKind EffectiveKind(AudioDevice device) =>
        BluetoothLabels.FirstOrDefault(label => label.Device.Id == device.Id)?.EffectiveKind
        ?? _settings.GetKindLabel(device.Id)
        ?? device.Kind;

    private static DeviceOption Find(IReadOnlyList<DeviceOption> options, DeviceAssignment? assigned) =>
        assigned is null ? AutoOption : options.FirstOrDefault(option => option.Id == assigned.Id) ?? AutoOption;

    private static DeviceOption Reselect(IReadOnlyList<DeviceOption> options, DeviceOption? selected) =>
        options.FirstOrDefault(option => option.Id == selected?.Id) ?? AutoOption;

    private static DeviceAssignment? ToAssignment(DeviceOption? option) =>
        option?.Id is null ? null : new DeviceAssignment { Id = option.Id, Name = option.Name };
}
