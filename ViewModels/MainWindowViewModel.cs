using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
    private const float DefaultVolumePercent = 50f;

    /// <summary>切換之前將目前裝置音量歸零(%)。</summary>
    private const float OffVolumePercent = 0f;

    // 系統切換端點後，驅動可能需要短暫時間才接受音量寫入；所有重試都有固定上限。
    private const int EndpointVolumeAttempts = 8;
    private const int EndpointVolumeRetryDelayMilliseconds = 40;
    private const int DefaultDeviceAttempts = 20;
    private const int DefaultDeviceRetryDelayMilliseconds = 50;
    private const float VolumeVerificationTolerancePercent = 0.5f;

    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#7DD3FC"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#F5B544"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#56D6A0"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#FFCF70"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF8C8C"));

    private readonly IAudioService _audio = AudioServiceFactory.Create();
    private readonly AppSettings _settings = AppSettings.Load();

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
    private string _statusText = "正在讀取目前的輸出裝置…";

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

    /// <summary>切換至喇叭時優先使用藍牙喇叭；未勾選時優先使用內建／有線喇叭。</summary>
    [ObservableProperty]
    private bool _useBluetoothSpeaker;

    /// <summary>切換至耳機時優先使用藍牙耳機；未勾選時優先使用有線／內建耳機。</summary>
    [ObservableProperty]
    private bool _useBluetoothHeadphone;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    private bool _hasCurrentDevice;

    public MainWindowViewModel()
    {
        // 直接寫入欄位，避免啟動時觸發儲存。
        _useBluetoothSpeaker = _settings.UseBluetoothSpeaker;
        _useBluetoothHeadphone = _settings.UseBluetoothHeadphone;

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
                $"先將「{plan.Current.Name}」裝置音量降至 0%，避免切換瞬間爆音。",
                AccentBrush);
            // 切換流程禁止使用 session fallback：只有端點主音量才等同系統音量滑桿。
            var currentVolumeSet = await Task.Run(
                () => TrySetEndpointVolumeAndVerify(plan.Current.Id, OffVolumePercent));

            // 無法先把端點音量驗證為 0% 時停止流程，確保「先降至 0% 再切換」不是只停留在提示文字。
            if (!currentVolumeSet)
            {
                SetStatus(
                    "切換已停止",
                    $"無法將目前裝置的 {_audio.SystemName} 裝置音量確認為 0%，為避免爆音，這次未切換輸出裝置。",
                    ErrorBrush);
                return;
            }

            SetStatus("步驟 2 / 3", $"正在切換至「{plan.Target.Name}」…", AccentBrush);
            try
            {
                await Task.Run(() => _audio.SetDefaultOutputDevice(plan.Target.Id));
                if (!await Task.Run(() => WaitForDefaultOutputDevice(plan.Target.Id)))
                {
                    await Task.Run(() => RestoreAfterFailedSwitch(plan));
                    Refresh();
                    SetStatus(
                        "切換失敗",
                        $"{_audio.SystemName} 未能確認目前輸出已切換至「{plan.Target.Name}」，已嘗試恢復原本設定。",
                        ErrorBrush);
                    return;
                }
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

            SetStatus("步驟 3 / 3", $"正在將「{plan.Target.Name}」裝置音量設為 50%…", AccentBrush);
            var targetVolumeSet = await Task.Run(
                () => TrySetEndpointVolumeAndVerify(plan.Target.Id, DefaultVolumePercent));

            if (!targetVolumeSet)
            {
                await Task.Run(() => RestoreAfterFailedSwitch(plan));
                Refresh();
                SetStatus(
                    "切換已回復",
                    $"無法將「{plan.Target.Name}」的 {_audio.SystemName} 裝置音量確認為 50%，已嘗試恢復原本設定。",
                    ErrorBrush);
                return;
            }

            Refresh();
            SetStatus(
                "切換完成",
                $"目前輸出為「{plan.Target.Name}」，{_audio.SystemName} 裝置音量已確認為 50%。",
                SuccessBrush);
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
                    $"請確認 {_audio.SystemName} 已連接或啟用輸出裝置，再重新讀取。",
                    WarningBrush);
            }
        }
        catch (Exception ex)
        {
            SetNoDevice();
            SetStatus("重新讀取失敗", GetExceptionMessage(ex), ErrorBrush);
        }
    }

    partial void OnUseBluetoothSpeakerChanged(bool value)
    {
        _settings.UseBluetoothSpeaker = value;
        SaveBluetoothPreference();
    }

    partial void OnUseBluetoothHeadphoneChanged(bool value)
    {
        _settings.UseBluetoothHeadphone = value;
        SaveBluetoothPreference();
    }

    private void SaveBluetoothPreference()
    {
        _settings.Save();
        if (HasCurrentDevice && !IsBusy)
            UpdateTargetText(CurrentKind);
    }

    private bool CanToggle() => HasCurrentDevice && !IsBusy;

    private DeviceKind CurrentKind => IsHeadphone ? DeviceKind.Headphone
        : IsSpeaker ? DeviceKind.Speaker
        : DeviceKind.Unknown;

    private bool CanRefresh() => !IsBusy;

    private SwitchPlanResult BuildSwitchPlan()
    {
        var current = _audio.GetDefaultOutputDevice();
        if (current is null)
        {
            return new SwitchPlanResult(
                null,
                $"找不到目前的預設輸出裝置，請先確認 {_audio.SystemName} 音訊輸出設定。");
        }

        var targetKind = current.Kind == DeviceKind.Headphone
            ? DeviceKind.Speaker
            : DeviceKind.Headphone;
        // 流程最後必須把目標音量確認為 50%，沒有可讀音量的裝置（例如 HDMI 螢幕）一定會失敗，不列入目標。
        var candidates = _audio.GetActiveOutputDevices()
            .Where(d => d.Id != current.Id && _audio.TryGetEndpointVolumePercent(d.Id) is not null)
            .ToList();
        // 依勾選決定藍牙或有線優先；偏好的連線方式不存在時退回同種類裝置，再退回任一輸出裝置。
        var wantBluetooth = WantsBluetooth(targetKind);
        var target = candidates.FirstOrDefault(d => d.Kind == targetKind && d.IsBluetooth == wantBluetooth)
                     ?? candidates.FirstOrDefault(d => d.Kind == targetKind)
                     ?? candidates.FirstOrDefault();

        if (target is null)
        {
            return new SwitchPlanResult(
                null,
                "找不到另一個可調整音量的輸出裝置，請確認喇叭與耳機都已啟用。");
        }

        // 只把端點音量當成切換前狀態；session 音量不能代表系統裝置滑桿。
        var previousVolume = _audio.TryGetEndpointVolumePercent(current.Id)
                             ?? _audio.TryGetVolumePercent(current.Id);
        return new SwitchPlanResult(
            new SwitchPlan(current, target, previousVolume),
            string.Empty);
    }

    private bool TrySetEndpointVolumeAndVerify(string deviceId, float expectedPercent)
    {
        for (var attempt = 0; attempt < EndpointVolumeAttempts; attempt++)
        {
            try
            {
                if (_audio.TrySetEndpointVolumePercent(deviceId, expectedPercent)
                    && _audio.TryGetEndpointVolumePercent(deviceId) is float actualPercent
                    && IsExpectedVolume(actualPercent, expectedPercent))
                {
                    return true;
                }
            }
            catch (COMException)
            {
                // 音訊端點剛完成切換時，驅動可能暫時拒絕 COM 呼叫；下一次嘗試仍受上限限制。
            }

            if (attempt + 1 < EndpointVolumeAttempts)
                Thread.Sleep(EndpointVolumeRetryDelayMilliseconds);
        }

        return false;
    }

    private bool WaitForDefaultOutputDevice(string expectedDeviceId)
    {
        for (var attempt = 0; attempt < DefaultDeviceAttempts; attempt++)
        {
            try
            {
                if (_audio.GetDefaultOutputDevice()?.Id == expectedDeviceId)
                    return true;
            }
            catch (COMException)
            {
                // 讓下一次輪詢等待 Core Audio 完成裝置狀態更新。
            }

            if (attempt + 1 < DefaultDeviceAttempts)
                Thread.Sleep(DefaultDeviceRetryDelayMilliseconds);
        }

        return false;
    }

    private static bool IsExpectedVolume(float actualPercent, float expectedPercent)
    {
        // 0% 必須確實是靜音；50% 允許端點硬體量化造成的小數點誤差。
        return expectedPercent == OffVolumePercent
            ? actualPercent <= 0.01f
            : Math.Abs(actualPercent - expectedPercent) <= VolumeVerificationTolerancePercent;
    }

    private void RestoreAfterFailedSwitch(SwitchPlan plan)
    {
        try
        {
            _audio.SetDefaultOutputDevice(plan.Current.Id);
            WaitForDefaultOutputDevice(plan.Current.Id);
        }
        catch
        {
            // 原始切換失敗後，恢復預設端點也可能失敗；保留原始錯誤給使用者。
        }

        if (plan.PreviousVolume is float previousVolume
            && !TrySetEndpointVolumeAndVerify(plan.Current.Id, previousVolume))
        {
            // 僅作為復原最後手段；正常切換流程絕不使用 session fallback。
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
        CurrentKindText = (current.Kind, current.IsBluetooth) switch
        {
            (DeviceKind.Headphone, true) => "藍牙耳機輸出",
            (DeviceKind.Headphone, false) => "耳機輸出",
            (DeviceKind.Speaker, true) => "藍牙喇叭輸出",
            (DeviceKind.Speaker, false) => "喇叭輸出",
            _ => "其他輸出",
        };
        // 顯示值也只讀端點主音量，避免把 session 音量誤顯示成系統裝置音量。
        CurrentVolumeText = _audio.TryGetEndpointVolumePercent(current.Id) is float volume
            ? $"{volume:0}%"
            : "無法讀取";

        IsHeadphone = current.Kind == DeviceKind.Headphone;
        IsSpeaker = current.Kind == DeviceKind.Speaker;
        IsOtherOutput = current.Kind == DeviceKind.Unknown;
        IsTargetSpeaker = IsHeadphone;
        IsTargetHeadphone = IsSpeaker;
        UpdateTargetText(current.Kind);
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

    private void UpdateActionText() => UpdateTargetText(CurrentKind);

    private bool WantsBluetooth(DeviceKind kind) => kind switch
    {
        DeviceKind.Speaker => UseBluetoothSpeaker,
        DeviceKind.Headphone => UseBluetoothHeadphone,
        _ => false,
    };

    private void UpdateTargetText(DeviceKind currentKind)
    {
        var targetName = currentKind switch
        {
            DeviceKind.Headphone => UseBluetoothSpeaker ? "藍牙喇叭" : "喇叭",
            DeviceKind.Speaker => UseBluetoothHeadphone ? "藍牙耳機" : "耳機",
            _ => null,
        };
        TargetKindText = targetName is null ? "目標：下一個輸出裝置" : $"目標：{targetName}";
        ToggleButtonText = targetName is null ? "切換輸出裝置" : $"切換至{targetName}";
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
