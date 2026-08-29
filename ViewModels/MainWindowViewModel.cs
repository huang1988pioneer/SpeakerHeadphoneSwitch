using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpeakerHeadphoneSwitch.Models;
using SpeakerHeadphoneSwitch.Services;

namespace SpeakerHeadphoneSwitch.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int TargetVolume = 33;

    private readonly IAudioService _audioService;
    private AudioOutputDevice? _targetDevice;

    public MainWindowViewModel()
        : this(AudioServiceFactory.Create())
    {
    }

    internal MainWindowViewModel(IAudioService audioService)
    {
        _audioService = audioService;
        _ = RefreshSnapshotAsync(showLoading: true);
    }

    public ObservableCollection<AudioOutputDevice> Devices { get; } = new();

    public string PlatformDescription => _audioService.PlatformDescription;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleOutputCommand))]
    private bool _hasSwitchTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleOutputCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _currentDeviceName = "讀取中…";

    [ObservableProperty]
    private string _currentDeviceType = "輸出裝置";

    [ObservableProperty]
    private string _currentVolumeText = "—";

    [ObservableProperty]
    private string _targetDeviceName = "耳機";

    [ObservableProperty]
    private string _targetDeviceType = "耳機";

    [ObservableProperty]
    private string _actionButtonText = "切換至耳機";

    [ObservableProperty]
    private string _statusText = "正在讀取音訊裝置…";

    [ObservableProperty]
    private string _statusDetail = "";

    [ObservableProperty]
    private bool _isError;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshSnapshotAsync(showLoading: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleOutput))]
    private async Task ToggleOutputAsync()
    {
        if (_targetDevice is null)
        {
            return;
        }

        var target = _targetDevice;
        IsBusy = true;
        IsError = false;
        StatusDetail = "";

        try
        {
            StatusText = "檢查目前輸出的靜音狀態…";
            if (await _audioService.GetMuteStateAsync())
            {
                StatusText = "目前輸出為靜音，先解除靜音…";
                await _audioService.SetMuteStateAsync(false);
            }

            StatusText = "第一步：將音量調整至 0…";
            await _audioService.SetVolumeAsync(0);

            StatusText = $"第二步：正在切換至{target.KindLabel}…";
            await _audioService.SwitchOutputAsync(target);

            StatusText = $"第三步：檢查{target.KindLabel}的靜音狀態…";
            if (await _audioService.GetDeviceMuteStateAsync(target))
            {
                StatusText = $"解除{target.KindLabel}靜音…";
                await _audioService.SetDeviceMuteStateAsync(target, false);
            }

            StatusText = $"第四步：將{target.KindLabel}音量調整至 33…";
            await _audioService.SetDeviceVolumeAsync(target, TargetVolume);
            // Re-apply unmute after the volume write as well. Some macOS
            // devices restore their saved mute bit after the first property
            // update, so the final state must be explicitly audible.
            await _audioService.SetDeviceMuteStateAsync(target, false);

            // Read back the system state before updating the UI. If macOS (or
            // a PulseAudio-compatible server) applies a delayed per-device
            // restore, re-apply the target settings a small number of times
            // instead of claiming success while the output is still at 0%.
            AudioSnapshot? verifiedSnapshot = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                verifiedSnapshot = await _audioService.GetSnapshotAsync();
                if (verifiedSnapshot.CurrentDevice?.Id == target.Id
                    && Math.Abs(verifiedSnapshot.VolumePercent - TargetVolume) <= 1
                    && !verifiedSnapshot.IsMuted)
                {
                    break;
                }

                if (attempt + 1 < 3)
                {
                    await _audioService.SetDeviceMuteStateAsync(target, false);
                    await _audioService.SetDeviceVolumeAsync(target, TargetVolume);
                    await Task.Delay(150);
                }
            }

            if (verifiedSnapshot is null
                || verifiedSnapshot.CurrentDevice?.Id != target.Id
                || Math.Abs(verifiedSnapshot.VolumePercent - TargetVolume) > 1
                || verifiedSnapshot.IsMuted)
            {
                var observedVolume = verifiedSnapshot?.VolumePercent.ToString() ?? "未知";
                throw new AudioServiceException(
                    $"切換後音量驗證失敗：目前為 {observedVolume}%（預期 33%）。");
            }

            StatusText = $"切換完成：{target.KindLabel}，音量 33%";
            await RefreshSnapshotAsync(showLoading: false);
            StatusText = $"切換完成：{CurrentDeviceType}，音量 {CurrentVolumeText}";
        }
        catch (Exception exception)
        {
            IsError = true;
            StatusText = "切換失敗";
            StatusDetail = exception is AudioServiceException
                ? exception.Message
                : $"發生未預期錯誤：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanToggleOutput() => !IsBusy && HasSwitchTarget;

    private async Task RefreshSnapshotAsync(bool showLoading)
    {
        if (showLoading)
        {
            IsError = false;
            StatusText = "正在讀取音訊裝置…";
            StatusDetail = "";
        }

        try
        {
            var snapshot = await _audioService.GetSnapshotAsync();
            Devices.Clear();
            foreach (var device in snapshot.Devices)
            {
                Devices.Add(device);
            }

            CurrentDeviceName = snapshot.CurrentDevice?.DisplayName ?? "找不到目前輸出裝置";
            CurrentDeviceType = snapshot.CurrentDevice?.KindLabel ?? "未辨識";
            CurrentVolumeText = snapshot.IsMuted
                ? $"靜音 · {snapshot.VolumePercent}%"
                : $"{snapshot.VolumePercent}%";
            _targetDevice = SelectTarget(snapshot);

            if (_targetDevice is null)
            {
                HasSwitchTarget = false;
                TargetDeviceName = "沒有可切換裝置";
                TargetDeviceType = "—";
                ActionButtonText = "沒有可切換裝置";
                if (showLoading)
                {
                    StatusText = "請先連接喇叭或耳機";
                    StatusDetail = "需要至少兩個可用的音訊輸出裝置。";
                }
            }
            else
            {
                HasSwitchTarget = true;
                TargetDeviceName = _targetDevice.DisplayName;
                TargetDeviceType = _targetDevice.KindLabel;
                ActionButtonText = $"一鍵切換至{_targetDevice.KindLabel}";
                if (showLoading)
                {
                    StatusText = $"準備就緒：可切換至{_targetDevice.KindLabel}";
                    StatusDetail = "必要時先解除靜音，再依序執行：音量 0 → 切換輸出 → 解除目標靜音 → 音量 33。";
                }
            }
        }
        catch (Exception exception)
        {
            HasSwitchTarget = false;
            IsError = true;
            CurrentDeviceName = "無法讀取音訊裝置";
            CurrentDeviceType = "—";
            CurrentVolumeText = "—";
            StatusText = "無法連線到系統音訊服務";
            StatusDetail = exception is AudioServiceException
                ? exception.Message
                : exception.Message;
        }
    }

    private static AudioOutputDevice? SelectTarget(AudioSnapshot snapshot)
    {
        var currentId = snapshot.CurrentDevice?.Id;
        var candidates = snapshot.Devices
            .Where(device => device.Id != currentId)
            .ToArray();

        if (snapshot.CurrentDevice?.Kind == AudioOutputKind.Headphones)
        {
            return candidates.FirstOrDefault(device => device.Kind == AudioOutputKind.Speakers)
                ?? candidates.FirstOrDefault(device => device.Kind == AudioOutputKind.Unknown);
        }

        return candidates.FirstOrDefault(device => device.Kind == AudioOutputKind.Headphones)
            ?? candidates.FirstOrDefault(device => device.Kind == AudioOutputKind.Speakers)
            ?? candidates.FirstOrDefault();
    }
}
