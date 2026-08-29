using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpeakerHeadphoneSwitch.Models;
using SpeakerHeadphoneSwitch.Services;

namespace SpeakerHeadphoneSwitch.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    /// <summary>切換之後的預設音量(%)。</summary>
    private const float DefaultVolumePercent = 33f;

    /// <summary>切換之前將目前裝置音量歸零(%)。</summary>
    private const float OffVolumePercent = 0f;

    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#7DD3FC"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#F5B544"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#56D6A0"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#FFCF70"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF8C8C"));

    private readonly WindowsAudioService _audio = new();

    [ObservableProperty]
    private string _currentDeviceName = "讀取中…";

    [ObservableProperty]
    private string _currentKindText = "正在讀取裝置";

    [ObservableProperty]
    private string _currentVolumeText = "—";

    [ObservableProperty]
    private string _targetKindText = "等待裝置";

    [ObservableProperty]
    private string _toggleButtonText = "切換音訊裝置";

    [ObservableProperty]
    private string _statusTitle = "正在讀取";

    [ObservableProperty]
    private string _statusText = "正在讀取目前的 Windows 輸出裝置…";

    [ObservableProperty]
    private IBrush _statusBrush = InfoBrush;

    [ObservableProperty]
    private bool _isSpeaker;

    [ObservableProperty]
    private bool _isHeadphone;

    [ObservableProperty]
    private bool _isOtherOutput;

    [ObservableProperty]
    private bool _isTargetSpeaker;

    [ObservableProperty]
    private bool _isTargetHeadphone;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshStatusCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    private bool _hasCurrentDevice;

    public MainWindowViewModel()
    {
        try
        {
            if (Refresh())
            {
                SetStatus(
                    "準備就緒",
                    $"目前使用「{CurrentDeviceName}」。按一下即可安全切換輸出。",
                    InfoBrush);
            }
        }
        catch (Exception ex)
        {
            SetNoDevice();
            SetStatus("無法讀取音訊狀態", GetExceptionMessage(ex), ErrorBrush);
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync()
    {
        IsBusy = true;
        ToggleButtonText = "切換中…";

        try
        {
            SetStatus("準備切換", "正在確認目前裝置與可用的目標裝置…", InfoBrush);
            var planResult = await Task.Run(BuildSwitchPlan);
            if (planResult.Plan is null)
            {
                SetStatus("找不到可切換裝置", planResult.Error, WarningBrush);
                return;
            }

            var plan = planResult.Plan;

            SetStatus(
                "步驟 1 / 3",
                $"先將「{plan.Current.Name}」音量降至 0%，避免切換瞬間爆音。",
                AccentBrush);
            var currentVolumeSet = await Task.Run(
                () => _audio.TrySetVolumePercent(plan.Current.Id, OffVolumePercent));

            // 無法先靜音時停止流程，確保「先降至 0% 再切換」不是只停留在提示文字。
            if (!currentVolumeSet)
            {
                SetStatus(
                    "切換已停止",
                    "無法將目前裝置音量設為 0%，為避免爆音，這次未切換輸出裝置。",
                    ErrorBrush);
                return;
            }

            SetStatus("步驟 2 / 3", $"正在切換至「{plan.Target.Name}」…", AccentBrush);
            try
            {
                await Task.Run(() => _audio.SetDefaultOutputDevice(plan.Target.Id));
            }
            catch (Exception ex)
            {
                // 預設端點可能在設定多個角色時部分成功；失敗時盡力恢復原裝置與原音量。
                await Task.Run(() => RestoreAfterFailedSwitch(plan));
                SetStatus(
                    "切換失敗",
                    $"無法切換至「{plan.Target.Name}」。{GetExceptionMessage(ex)}",
                    ErrorBrush);
                return;
            }

            SetStatus("步驟 3 / 3", $"正在將「{plan.Target.Name}」音量設為 33%…", AccentBrush);
            var targetVolumeSet = await Task.Run(
                () => _audio.TrySetVolumePercent(plan.Target.Id, DefaultVolumePercent));

            Refresh();
            if (targetVolumeSet)
            {
                SetStatus(
                    "切換完成",
                    $"目前輸出為「{plan.Target.Name}」，音量已設為 33%。",
                    SuccessBrush);
            }
            else
            {
                SetStatus(
                    "已切換，但音量未確認",
                    $"目前輸出已切換至「{plan.Target.Name}」，但系統未能確認 33% 音量設定。",
                    WarningBrush);
            }
        }
        catch (Exception ex)
        {
            SetStatus("切換失敗", GetExceptionMessage(ex), ErrorBrush);
        }
        finally
        {
            IsBusy = false;
            if (!HasCurrentDevice)
            {
                ToggleButtonText = "等待音訊裝置";
            }
            else
            {
                UpdateActionText();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void RefreshStatus()
    {
        try
        {
            if (Refresh())
            {
                SetStatus(
                    "狀態已更新",
                    $"目前使用「{CurrentDeviceName}」。",
                    InfoBrush);
            }
            else
            {
                SetStatus(
                    "找不到輸出裝置",
                    "請確認 Windows 已連接或啟用輸出裝置，再重新讀取。",
                    WarningBrush);
            }
        }
        catch (Exception ex)
        {
            SetNoDevice();
            SetStatus("重新讀取失敗", GetExceptionMessage(ex), ErrorBrush);
        }
    }

    private bool CanToggle() => HasCurrentDevice && !IsBusy;

    private bool CanRefresh() => !IsBusy;

    private SwitchPlanResult BuildSwitchPlan()
    {
        var current = _audio.GetDefaultOutputDevice();
        if (current is null)
        {
            return new SwitchPlanResult(
                null,
                "找不到目前的預設輸出裝置，請先確認 Windows 音訊輸出設定。");
        }

        var targetKind = current.Kind == DeviceKind.Headphone
            ? DeviceKind.Speaker
            : DeviceKind.Headphone;
        var devices = _audio.GetActiveOutputDevices();
        var target = devices.FirstOrDefault(d => d.Id != current.Id && d.Kind == targetKind)
                     ?? devices.FirstOrDefault(d => d.Id != current.Id);

        if (target is null)
        {
            return new SwitchPlanResult(
                null,
                "找不到另一個可切換的輸出裝置，請確認喇叭與耳機都已啟用。");
        }

        return new SwitchPlanResult(
            new SwitchPlan(current, target, _audio.TryGetVolumePercent(current.Id)),
            string.Empty);
    }

    private void RestoreAfterFailedSwitch(SwitchPlan plan)
    {
        try
        {
            _audio.SetDefaultOutputDevice(plan.Current.Id);
        }
        catch
        {
            // 原始切換失敗後，恢復預設端點也可能失敗；保留原始錯誤給使用者。
        }

        if (plan.PreviousVolume is float previousVolume)
        {
            _audio.TrySetVolumePercent(plan.Current.Id, previousVolume);
        }
    }

    private bool Refresh()
    {
        var current = _audio.GetDefaultOutputDevice();
        if (current is null)
        {
            SetNoDevice();
            return false;
        }

        HasCurrentDevice = true;
        CurrentDeviceName = current.Name;
        CurrentKindText = current.Kind switch
        {
            DeviceKind.Headphone => "耳機輸出",
            DeviceKind.Speaker => "喇叭輸出",
            _ => "其他輸出",
        };
        CurrentVolumeText = _audio.TryGetVolumePercent(current.Id) is float volume
            ? $"{volume:0}%"
            : "無法讀取";

        IsHeadphone = current.Kind == DeviceKind.Headphone;
        IsSpeaker = current.Kind == DeviceKind.Speaker;
        IsOtherOutput = current.Kind == DeviceKind.Unknown;
        IsTargetSpeaker = IsHeadphone;
        IsTargetHeadphone = IsSpeaker;
        TargetKindText = current.Kind switch
        {
            DeviceKind.Headphone => "目標：喇叭",
            DeviceKind.Speaker => "目標：耳機",
            _ => "目標：下一個輸出裝置",
        };
        UpdateActionText(current.Kind);
        return true;
    }

    private void SetNoDevice()
    {
        HasCurrentDevice = false;
        CurrentDeviceName = "找不到預設輸出裝置";
        CurrentKindText = "無法判斷輸出種類";
        CurrentVolumeText = "—";
        TargetKindText = "等待裝置";
        ToggleButtonText = "等待音訊裝置";
        IsSpeaker = false;
        IsHeadphone = false;
        IsOtherOutput = false;
        IsTargetSpeaker = false;
        IsTargetHeadphone = false;
    }

    private void UpdateActionText()
    {
        var current = IsHeadphone ? DeviceKind.Headphone
            : IsSpeaker ? DeviceKind.Speaker
            : DeviceKind.Unknown;
        UpdateActionText(current);
    }

    private void UpdateActionText(DeviceKind kind)
    {
        ToggleButtonText = kind switch
        {
            DeviceKind.Headphone => "切換至喇叭",
            DeviceKind.Speaker => "切換至耳機",
            _ => "切換輸出裝置",
        };
    }

    private void SetStatus(string title, string detail, IBrush brush)
    {
        StatusTitle = title;
        StatusText = detail;
        StatusBrush = brush;
    }

    private static string GetExceptionMessage(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

    private sealed record SwitchPlan(AudioDevice Current, AudioDevice Target, float? PreviousVolume);

    private sealed record SwitchPlanResult(SwitchPlan? Plan, string Error);
}
