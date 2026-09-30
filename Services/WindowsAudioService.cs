using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SpeakerHeadphoneSwitch.Models;

namespace SpeakerHeadphoneSwitch.Services;

/// <summary>
/// 以 Windows Core Audio API 實作：列舉／查詢輸出裝置、設定裝置音量、切換預設輸出裝置。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioService : IAudioService
{
    private const int ClsCtxAll = 0x17;
    private const int DeviceStateActive = 0x1;
    private const int StgmRead = 0;
    private const int AudioSessionStateExpired = 2;

    // IID_IAudioEndpointVolume（endpointvolume.h）。這個 GUID 對應 Windows 音量滑桿。
    private static readonly Guid AudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid AudioSessionManager2Iid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public string SystemName => "Windows";

    /// <summary>取得目前的預設輸出裝置（多媒體角色）。</summary>
    public AudioDevice? GetDefaultOutputDevice()
    {
        var enumerator = CreateEnumerator();
        var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
        if (hr < 0)
            return null;
        return Describe(device);
    }

    /// <summary>列舉所有啟用中的輸出裝置。</summary>
    public IReadOnlyList<AudioDevice> GetActiveOutputDevices()
    {
        var enumerator = CreateEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceStateActive, out var collection));

        collection.GetCount(out var count);
        var devices = new List<AudioDevice>(count);
        for (var i = 0; i < count; i++)
        {
            Marshal.ThrowExceptionForHR(collection.Item(i, out var device));
            devices.Add(Describe(device));
        }

        return devices;
    }

    /// <summary>
    /// 取得裝置音量（0-100%）。優先使用端點主音量；此系統不提供時改讀裝置預設工作階段的音量，
    /// 仍無法取得時回傳 null。
    /// </summary>
    public float? TryGetVolumePercent(string deviceId)
    {
        var endpointVolume = TryGetEndpointVolumePercent(deviceId);
        if (endpointVolume is not null)
            return endpointVolume;

        if (!TryGetDeviceById(deviceId, out var device))
            return null;

        // 後備：讀取裝置預設工作階段（session）的音量。
        if (TryActivateSessionManager(device, out var manager)
            && manager.GetSimpleAudioVolume(IntPtr.Zero, 0, out var sessionVolume) >= 0
            && sessionVolume.GetMasterVolume(out var sessionLevel) >= 0)
            return sessionLevel * 100f;

        return null;
    }

    /// <summary>
    /// 僅讀取 Windows 音訊端點主音量（0-100%），不退回工作階段音量。
    /// 這個值對應 Windows 音量滑桿；無法使用時回傳 null。
    /// </summary>
    public float? TryGetEndpointVolumePercent(string deviceId)
    {
        if (!TryGetDeviceById(deviceId, out var device))
            return null;

        if (TryActivateEndpointVolume(device, out var endpointVolume)
            && endpointVolume.GetMasterVolumeLevelScalar(out var level) >= 0)
            return level * 100f;

        return null;
    }

    /// <summary>
    /// 設定裝置音量（0-100%）。優先使用端點主音量；不支援時改設裝置的預設工作階段與所有現存
    /// 工作階段的音量。這個方法保留後備行為供診斷與一般音量操作使用。
    /// </summary>
    public bool TrySetVolumePercent(string deviceId, float percent)
    {
        if (!TryGetDeviceById(deviceId, out var device))
            return false;

        var level = Math.Clamp(percent, 0f, 100f) / 100f;

        if (TryActivateEndpointVolume(device, out var endpointVolume))
        {
            var context = Guid.Empty;
            if (endpointVolume.SetMasterVolumeLevelScalar(level, ref context) >= 0)
                return true;
        }

        return SetDefaultAndAllSessionVolumes(device, level);
    }

    /// <summary>
    /// 僅設定 Windows 音訊端點主音量（0-100%），不退回工作階段音量。
    /// 成功後 Windows 音量滑桿應反映相同數值；切換流程應使用此方法確保目標效果。
    /// </summary>
    public bool TrySetEndpointVolumePercent(string deviceId, float percent)
    {
        if (!TryGetDeviceById(deviceId, out var device)
            || !TryActivateEndpointVolume(device, out var endpointVolume))
            return false;

        var level = Math.Clamp(percent, 0f, 100f) / 100f;
        var context = Guid.Empty;
        return endpointVolume.SetMasterVolumeLevelScalar(level, ref context) >= 0;
    }

    /// <summary>將指定裝置設為預設輸出裝置（同時設定三種角色）。</summary>
    public void SetDefaultOutputDevice(string deviceId)
    {
        object comObject = new PolicyConfigComObject();
        IPolicyConfig? policyConfig = comObject as IPolicyConfig;
        IPolicyConfigVista? policyConfigVista = policyConfig is null ? comObject as IPolicyConfigVista : null;

        if (policyConfig is null && policyConfigVista is null)
            throw new InvalidOperationException("無法取得 PolicyConfig COM 介面，無法切換預設裝置。");

        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
        {
            var hr = policyConfig is not null
                ? policyConfig.SetDefaultEndpoint(deviceId, role)
                : policyConfigVista!.SetDefaultEndpoint(deviceId, role);
            // 部分驅動不支援 Communications 角色，失敗時略過。
            if (hr < 0 && role == ERole.eCommunications)
                continue;
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    private static bool TryGetDeviceById(string deviceId, out IMMDevice device)
    {
        try
        {
            var enumerator = CreateEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(deviceId, out var d));
            device = d;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            device = null!;
            return false;
        }
    }

    /// <summary>啟用端點主音量介面（IAudioEndpointVolume）。</summary>
    private static bool TryActivateEndpointVolume(IMMDevice device, out IAudioEndpointVolume volume)
    {
        try
        {
            var iid = AudioEndpointVolumeIid;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var obj));
            volume = (IAudioEndpointVolume)obj;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            volume = null!;
            return false;
        }
    }

    private static bool TryActivateSessionManager(IMMDevice device, out IAudioSessionManager2 manager)
    {
        try
        {
            var iid = AudioSessionManager2Iid;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var obj));
            manager = (IAudioSessionManager2)obj;
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            manager = null!;
            return false;
        }
    }

    /// <summary>
    /// 此系統不提供端點主音量時的後備：設定「預設工作階段」與「所有現存工作階段」的音量。
    /// 預設工作階段的設定可讓之後才開始播放的應用程式沿用；至少一個成功即回傳 true。
    /// </summary>
    private static bool SetDefaultAndAllSessionVolumes(IMMDevice device, float level)
    {
        if (!TryActivateSessionManager(device, out var manager))
            return false;

        var applied = 0;
        var context = Guid.Empty;

        // 預設工作階段：之後加入此裝置的應用程式沿用此音量。
        if (manager.GetSimpleAudioVolume(IntPtr.Zero, 0, out var defaultVolume) >= 0
            && defaultVolume.SetMasterVolume(level, ref context) >= 0)
            applied++;

        if (manager.GetSessionEnumerator(out var enumerator) >= 0
            && enumerator.GetCount(out var count) >= 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (enumerator.GetSession(i, out var session) < 0)
                    continue;
                if (session.GetState(out var state) >= 0 && state == AudioSessionStateExpired)
                    continue;
                if (session is ISimpleAudioVolume volume && volume.SetMasterVolume(level, ref context) >= 0)
                    applied++;
            }
        }

        return applied > 0;
    }

    private static AudioDevice Describe(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var id));

        var name = ReadStringProperty(device, AudioPropertyKeys.DeviceFriendlyName);
        if (string.IsNullOrWhiteSpace(name))
            name = id;

        var formFactorValue = ReadIntProperty(device, AudioPropertyKeys.AudioEndpointFormFactor);
        var formFactor = (EndpointFormFactor)formFactorValue;

        return new AudioDevice(id, name, Classify(formFactor, name), IsBluetooth(device, name));
    }

    private static string? ReadStringProperty(IMMDevice device, PropertyKey key)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StgmRead, out var store));
        var hr = store.GetValue(ref key, out var pv);
        if (hr < 0)
            return null;

        try
        {
            return pv.AsString;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    private static int ReadIntProperty(IMMDevice device, PropertyKey key)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StgmRead, out var store));
        var hr = store.GetValue(ref key, out var pv);
        if (hr < 0)
            return 0;

        try
        {
            return pv.AsInt32;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    /// <summary>依端點所屬裝置的列舉器（BTH*）判斷藍牙，取不到時以名稱關鍵字後備。</summary>
    private static bool IsBluetooth(IMMDevice device, string name)
    {
        foreach (var key in new[] { AudioPropertyKeys.DeviceEnumeratorName, AudioPropertyKeys.AudioEndpointDeviceInstance })
        {
            if (ReadStringProperty(device, key) is { } value
                && value.Contains("BTH", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return DeviceNameClassifier.LooksBluetooth(name);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    /// <summary>依裝置硬體型式（優先）與名稱關鍵字（後備）判斷是耳機還是喇叭。</summary>
    private static DeviceKind Classify(EndpointFormFactor formFactor, string name)
    {
        switch (formFactor)
        {
            case EndpointFormFactor.Headphones:
            case EndpointFormFactor.Headset:
                return DeviceKind.Headphone;
            case EndpointFormFactor.Speakers:
                return DeviceKind.Speaker;
        }

        return DeviceNameClassifier.Classify(name);
    }
}
