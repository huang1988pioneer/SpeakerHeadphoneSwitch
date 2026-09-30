using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SpeakerHeadphoneSwitch.Models;

namespace SpeakerHeadphoneSwitch.Services;

/// <summary>
/// 以 macOS CoreAudio HAL 實作：列舉／查詢輸出裝置、設定裝置音量、切換預設輸出裝置。
/// 裝置 Id 使用 CoreAudio 的裝置 UID（重新連線後仍穩定），而不是會變動的 AudioObjectID。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacAudioService : IAudioService
{
    private const string CoreAudioLibrary = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string AudioToolboxLibrary = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint SystemObject = 1;          // kAudioObjectSystemObject
    private const uint UnknownObject = 0;         // kAudioObjectUnknown
    private const uint ElementMain = 0;           // kAudioObjectPropertyElementMain

    private static readonly uint ScopeGlobal = FourCC("glob");
    private static readonly uint ScopeOutput = FourCC("outp");

    private static readonly uint HardwareDevices = FourCC("dev#");
    private static readonly uint DefaultOutputDevice = FourCC("dOut");
    private static readonly uint DefaultSystemOutputDevice = FourCC("sOut");
    private static readonly uint ObjectName = FourCC("lnam");
    private static readonly uint DeviceUid = FourCC("uid ");
    private static readonly uint DeviceStreams = FourCC("stm#");
    private static readonly uint DeviceIsHidden = FourCC("hidn");
    private static readonly uint DeviceTransportType = FourCC("tran");
    private static readonly uint DeviceDataSource = FourCC("ssrc");
    private static readonly uint DeviceVolumeScalar = FourCC("volm");
    private static readonly uint DeviceMute = FourCC("mute");
    private static readonly uint DevicePreferredStereoChannels = FourCC("dch2");
    // kAudioHardwareServiceDeviceProperty_VirtualMainVolume：與選單列／系統設定的音量滑桿相同。
    private static readonly uint VirtualMainVolume = FourCC("vmvc");

    private static readonly uint TransportBuiltIn = FourCC("bltn");
    private static readonly uint TransportBluetooth = FourCC("blue");
    private static readonly uint TransportBluetoothLe = FourCC("blea");
    private static readonly uint TransportHdmi = FourCC("hdmi");
    private static readonly uint TransportDisplayPort = FourCC("dprt");
    private static readonly uint TransportAirPlay = FourCC("airp");

    private static readonly uint DataSourceInternalSpeaker = FourCC("ispk");
    private static readonly uint DataSourceHeadphones = FourCC("hdpn");

    public string SystemName => "macOS";

    public AudioDevice? GetDefaultOutputDevice()
    {
        var address = new PropertyAddress(DefaultOutputDevice, ScopeGlobal, ElementMain);
        if (!TryGetUInt32(SystemObject, address, out var deviceId) || deviceId == UnknownObject)
            return null;
        return Describe(deviceId);
    }

    public IReadOnlyList<AudioDevice> GetActiveOutputDevices() =>
        GetOutputDeviceIds()
            .Select(Describe)
            .OfType<AudioDevice>()
            .ToList();

    // macOS 沒有 Windows 的 session 音量概念；後備與主音量相同。
    public float? TryGetVolumePercent(string deviceId) => TryGetEndpointVolumePercent(deviceId);

    public bool TrySetVolumePercent(string deviceId, float percent) => TrySetEndpointVolumePercent(deviceId, percent);

    public float? TryGetEndpointVolumePercent(string deviceId)
    {
        if (!TryFindDevice(deviceId, out var objectId))
            return null;

        var level = ReadVolumeLevel(objectId);
        // 靜音時選單列滑桿顯示為 0；回報 0% 才能讓「確認 50%」的驗證反映實際聽到的音量。
        return level is not null && IsMuted(objectId) ? 0f : level;
    }

    private static float? ReadVolumeLevel(uint objectId)
    {
        var virtualMain = new PropertyAddress(VirtualMainVolume, ScopeOutput, ElementMain);
        if (TryHardwareServiceGetFloat(objectId, virtualMain, out var level))
            return level * 100f;

        var mainScalar = new PropertyAddress(DeviceVolumeScalar, ScopeOutput, ElementMain);
        if (AudioObjectHasProperty(objectId, ref mainScalar) && TryGetFloat(objectId, mainScalar, out level))
            return level * 100f;

        // 部分裝置只有逐聲道音量：取左右聲道平均。
        var channelLevels = GetStereoChannels(objectId)
            .Select(channel => new PropertyAddress(DeviceVolumeScalar, ScopeOutput, channel))
            .Select(address => TryGetFloat(objectId, address, out var value) ? value : (float?)null)
            .OfType<float>()
            .ToList();
        return channelLevels.Count > 0 ? channelLevels.Average() * 100f : null;
    }

    public bool TrySetEndpointVolumePercent(string deviceId, float percent)
    {
        if (!TryFindDevice(deviceId, out var objectId))
            return false;

        var level = Math.Clamp(percent, 0f, 100f) / 100f;
        if (!WriteVolumeLevel(objectId, level))
            return false;

        // 音量拉到 0 時 macOS 會自動靜音，之後只寫入音量仍然聽不到；非 0 音量必須一併解除靜音。
        return level <= 0f || TrySetMuted(objectId, false);
    }

    private static bool WriteVolumeLevel(uint objectId, float level)
    {
        var virtualMain = new PropertyAddress(VirtualMainVolume, ScopeOutput, ElementMain);
        if (TryHardwareServiceSetFloat(objectId, virtualMain, level))
            return true;

        var mainScalar = new PropertyAddress(DeviceVolumeScalar, ScopeOutput, ElementMain);
        if (IsSettable(objectId, mainScalar) && TrySetFloat(objectId, mainScalar, level))
            return true;

        var applied = 0;
        foreach (var channel in GetStereoChannels(objectId))
        {
            var address = new PropertyAddress(DeviceVolumeScalar, ScopeOutput, channel);
            if (IsSettable(objectId, address) && TrySetFloat(objectId, address, level))
                applied++;
        }

        return applied > 0;
    }

    /// <summary>主聲道靜音，或（沒有主聲道靜音時）所有左右聲道都靜音。</summary>
    private static bool IsMuted(uint objectId)
    {
        var main = new PropertyAddress(DeviceMute, ScopeOutput, ElementMain);
        if (AudioObjectHasProperty(objectId, ref main))
            return TryGetUInt32(objectId, main, out var muted) && muted != 0;

        var channelStates = GetStereoChannels(objectId)
            .Select(channel => new PropertyAddress(DeviceMute, ScopeOutput, channel))
            .Where(address => AudioObjectHasProperty(objectId, ref address))
            .Select(address => TryGetUInt32(objectId, address, out var muted) && muted != 0)
            .ToList();
        return channelStates.Count > 0 && channelStates.All(muted => muted);
    }

    /// <summary>設定靜音狀態；裝置沒有靜音控制時視為成功（不存在靜音就不會被靜音擋住）。</summary>
    private static bool TrySetMuted(uint objectId, bool muted)
    {
        uint value = muted ? 1u : 0u;
        var main = new PropertyAddress(DeviceMute, ScopeOutput, ElementMain);
        if (AudioObjectHasProperty(objectId, ref main))
            return IsSettable(objectId, main)
                   && AudioObjectSetPropertyData(objectId, ref main, 0, IntPtr.Zero, sizeof(uint), ref value) == 0;

        foreach (var channel in GetStereoChannels(objectId))
        {
            var address = new PropertyAddress(DeviceMute, ScopeOutput, channel);
            if (AudioObjectHasProperty(objectId, ref address)
                && (!IsSettable(objectId, address)
                    || AudioObjectSetPropertyData(objectId, ref address, 0, IntPtr.Zero, sizeof(uint), ref value) != 0))
                return false;
        }

        return true;
    }

    public void SetDefaultOutputDevice(string deviceId)
    {
        if (!TryFindDevice(deviceId, out var objectId))
            throw new InvalidOperationException("找不到目標輸出裝置，可能已中斷連線。");

        var output = new PropertyAddress(DefaultOutputDevice, ScopeGlobal, ElementMain);
        var status = AudioObjectSetPropertyData(SystemObject, ref output, 0, IntPtr.Zero, sizeof(uint), ref objectId);
        if (status != 0)
            throw new InvalidOperationException($"CoreAudio 無法設定預設輸出裝置（{FormatStatus(status)}）。");

        // 系統提示音裝置一併切換，對應 Windows 同時設定三種角色；部分裝置不接受時略過。
        var systemOutput = new PropertyAddress(DefaultSystemOutputDevice, ScopeGlobal, ElementMain);
        AudioObjectSetPropertyData(SystemObject, ref systemOutput, 0, IntPtr.Zero, sizeof(uint), ref objectId);
    }

    private static IEnumerable<uint> GetOutputDeviceIds()
    {
        var address = new PropertyAddress(HardwareDevices, ScopeGlobal, ElementMain);
        if (AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, IntPtr.Zero, out var size) != 0 || size == 0)
            return Array.Empty<uint>();

        var ids = new uint[size / sizeof(uint)];
        if (AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, ids) != 0)
            return Array.Empty<uint>();

        return ids.Take((int)(size / sizeof(uint))).Where(IsVisibleOutputDevice).ToArray();
    }

    private static bool IsVisibleOutputDevice(uint objectId)
    {
        var streams = new PropertyAddress(DeviceStreams, ScopeOutput, ElementMain);
        if (AudioObjectGetPropertyDataSize(objectId, ref streams, 0, IntPtr.Zero, out var size) != 0 || size == 0)
            return false;

        var hidden = new PropertyAddress(DeviceIsHidden, ScopeGlobal, ElementMain);
        return !(TryGetUInt32(objectId, hidden, out var isHidden) && isHidden != 0);
    }

    private static bool TryFindDevice(string uid, out uint objectId)
    {
        foreach (var candidate in GetOutputDeviceIds())
        {
            if (ReadString(candidate, DeviceUid) == uid)
            {
                objectId = candidate;
                return true;
            }
        }

        objectId = UnknownObject;
        return false;
    }

    private static AudioDevice? Describe(uint objectId)
    {
        var uid = ReadString(objectId, DeviceUid);
        if (string.IsNullOrEmpty(uid))
            return null;

        var name = ReadString(objectId, ObjectName);
        if (string.IsNullOrWhiteSpace(name))
            name = uid;

        return new AudioDevice(uid, name, Classify(objectId, name));
    }

    /// <summary>依名稱關鍵字（優先）、內建輸出的資料來源與傳輸方式判斷是耳機還是喇叭。</summary>
    private static DeviceKind Classify(uint objectId, string name)
    {
        var byName = DeviceNameClassifier.Classify(name);
        if (byName != DeviceKind.Unknown)
            return byName;

        var transportAddress = new PropertyAddress(DeviceTransportType, ScopeGlobal, ElementMain);
        TryGetUInt32(objectId, transportAddress, out var transport);

        if (transport == TransportBuiltIn)
        {
            // Intel Mac 的內建輸出在插入耳機時只切換資料來源，不會出現新裝置。
            var sourceAddress = new PropertyAddress(DeviceDataSource, ScopeOutput, ElementMain);
            if (TryGetUInt32(objectId, sourceAddress, out var source))
            {
                if (source == DataSourceHeadphones)
                    return DeviceKind.Headphone;
                if (source == DataSourceInternalSpeaker)
                    return DeviceKind.Speaker;
            }

            return DeviceKind.Speaker;
        }

        if (transport == TransportBluetooth || transport == TransportBluetoothLe)
            return DeviceKind.Headphone;

        if (transport == TransportHdmi || transport == TransportDisplayPort || transport == TransportAirPlay)
            return DeviceKind.Speaker;

        return DeviceKind.Unknown;
    }

    private static IEnumerable<uint> GetStereoChannels(uint objectId)
    {
        var address = new PropertyAddress(DevicePreferredStereoChannels, ScopeOutput, ElementMain);
        var channels = new uint[2];
        uint size = sizeof(uint) * 2;
        if (AudioObjectHasProperty(objectId, ref address)
            && AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, channels) == 0)
            return channels.Where(channel => channel != 0).Distinct().ToArray();

        return new uint[] { 1, 2 };
    }

    private static bool IsSettable(uint objectId, PropertyAddress address) =>
        AudioObjectHasProperty(objectId, ref address)
        && AudioObjectIsPropertySettable(objectId, ref address, out var settable) == 0
        && settable;

    private static bool TryGetUInt32(uint objectId, PropertyAddress address, out uint value)
    {
        uint size = sizeof(uint);
        return AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, out value) == 0;
    }

    private static bool TryGetFloat(uint objectId, PropertyAddress address, out float value)
    {
        uint size = sizeof(float);
        return AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, out value) == 0;
    }

    private static bool TrySetFloat(uint objectId, PropertyAddress address, float value) =>
        AudioObjectSetPropertyData(objectId, ref address, 0, IntPtr.Zero, sizeof(float), ref value) == 0;

    private static bool TryHardwareServiceGetFloat(uint objectId, PropertyAddress address, out float value)
    {
        value = 0;
        try
        {
            uint size = sizeof(float);
            return AudioHardwareServiceHasProperty(objectId, ref address)
                   && AudioHardwareServiceGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, out value) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryHardwareServiceSetFloat(uint objectId, PropertyAddress address, float value)
    {
        try
        {
            return AudioHardwareServiceHasProperty(objectId, ref address)
                   && AudioHardwareServiceIsPropertySettable(objectId, ref address, out var settable) == 0
                   && settable
                   && AudioHardwareServiceSetPropertyData(objectId, ref address, 0, IntPtr.Zero, sizeof(float), ref value) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static string? ReadString(uint objectId, uint selector)
    {
        var address = new PropertyAddress(selector, ScopeGlobal, ElementMain);
        uint size = (uint)IntPtr.Size;
        if (AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, out IntPtr cfString) != 0
            || cfString == IntPtr.Zero)
            return null;

        try
        {
            var length = CFStringGetLength(cfString);
            var buffer = new char[length];
            CFStringGetCharacters(cfString, new CFRange(0, length), buffer);
            return new string(buffer);
        }
        finally
        {
            CFRelease(cfString);
        }
    }

    private static uint FourCC(string code) =>
        (uint)(code[0] << 24 | code[1] << 16 | code[2] << 8 | code[3]);

    private static string FormatStatus(int status)
    {
        var bytes = BitConverter.GetBytes(status);
        Array.Reverse(bytes);
        return bytes.All(b => b is >= 0x20 and < 0x7F)
            ? $"'{System.Text.Encoding.ASCII.GetString(bytes)}'"
            : status.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyAddress(uint selector, uint scope, uint element)
    {
        public readonly uint Selector = selector;
        public readonly uint Scope = scope;
        public readonly uint Element = element;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CFRange(nint location, nint length)
    {
        public readonly nint Location = location;
        public readonly nint Length = length;
    }

    [DllImport(CoreAudioLibrary)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AudioObjectHasProperty(uint objectId, ref PropertyAddress address);

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectIsPropertySettable(
        uint objectId, ref PropertyAddress address, [MarshalAs(UnmanagedType.U1)] out bool settable);

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectGetPropertyDataSize(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, out uint data);

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, out float data);

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, out IntPtr data);

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, [Out] uint[] data);

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectSetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, uint dataSize, ref uint data);

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectSetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, uint dataSize, ref float data);

    [DllImport(AudioToolboxLibrary)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AudioHardwareServiceHasProperty(uint objectId, ref PropertyAddress address);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioHardwareServiceIsPropertySettable(
        uint objectId, ref PropertyAddress address, [MarshalAs(UnmanagedType.U1)] out bool settable);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioHardwareServiceGetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, out float data);

    [DllImport(AudioToolboxLibrary)]
    private static extern int AudioHardwareServiceSetPropertyData(
        uint objectId, ref PropertyAddress address, uint qualifierSize, IntPtr qualifier, uint dataSize, ref float data);

    [DllImport(CoreFoundationLibrary)]
    private static extern nint CFStringGetLength(IntPtr cfString);

    [DllImport(CoreFoundationLibrary, CharSet = CharSet.Unicode)]
    private static extern void CFStringGetCharacters(IntPtr cfString, CFRange range, [Out] char[] buffer);

    [DllImport(CoreFoundationLibrary)]
    private static extern void CFRelease(IntPtr cf);
}
