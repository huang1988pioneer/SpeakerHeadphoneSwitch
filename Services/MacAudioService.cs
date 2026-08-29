using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SpeakerHeadphoneSwitch.Models;

namespace SpeakerHeadphoneSwitch.Services;

/// <summary>
/// macOS audio implementation. The default output device and its output volume
/// are both changed through CoreAudio.
/// </summary>
internal sealed class MacAudioService : IAudioService
{
    private const int DeviceSwitchRetryCount = 20;
    private const int VolumeSetRetryCount = 8;
    private const int VolumeTolerancePercent = 1;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan VolumeReadbackDelay = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan StableVolumeDelay = TimeSpan.FromMilliseconds(175);

    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    private const uint SystemObjectId = 1;
    private const uint PropertyDevices = 0x6465_7623; // 'dev#'
    private const uint PropertyDefaultOutputDevice = 0x644F_7574; // 'dOut'
    private const uint PropertyName = 0x6C6E_616D; // 'lnam'
    private const uint PropertyStreamConfiguration = 0x736C_6179; // 'slay'
    private const uint PropertyVolumeScalar = 0x766F_6C6D; // 'volm'
    private const uint PropertyVirtualMainVolume = 0x766D_7663; // 'vmvc'
    private const uint PropertyMute = 0x6D75_7465; // 'mute'
    private const uint ScopeGlobal = 0x676C_6F62; // 'glob'
    private const uint ScopeOutput = 0x6F75_7470; // 'outp'
    private const uint ElementMain = 0;
    private const uint Utf8Encoding = 0x0800_0100;

    public string PlatformDescription => "macOS CoreAudio";

    public Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var devices = ReadOutputDevices();
        var currentId = ReadDefaultOutputDevice();
        var current = devices.FirstOrDefault(device => device.Id == currentId.ToString(CultureInfo.InvariantCulture));
        var volume = ReadVolume(currentId);
        var isMuted = ReadMute(currentId);

        return Task.FromResult(new AudioSnapshot(devices, current, volume, isMuted));
    }

    public async Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var deviceId = ReadDefaultOutputDevice();
        await SetVolumeForDeviceAsync(deviceId, percent, cancellationToken);
    }

    public Task<bool> GetMuteStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadMute(ReadDefaultOutputDevice()));
    }

    public async Task SetMuteStateAsync(
        bool muted,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var deviceId = ReadDefaultOutputDevice();
        await SetMuteForDeviceAsync(deviceId, muted, cancellationToken);
    }

    public async Task SetDeviceVolumeAsync(
        AudioOutputDevice device,
        int percent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!uint.TryParse(device.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var deviceId))
        {
            throw new AudioServiceException($"無法辨識音訊裝置識別碼：{device.Id}");
        }

        await SetVolumeForDeviceAsync(deviceId, percent, cancellationToken);
    }

    public Task<bool> GetDeviceMuteStateAsync(
        AudioOutputDevice device,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var deviceId = ParseDeviceId(device);
        return Task.FromResult(ReadMute(deviceId));
    }

    public async Task SetDeviceMuteStateAsync(
        AudioOutputDevice device,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await SetMuteForDeviceAsync(ParseDeviceId(device), muted, cancellationToken);
    }

    private static async Task SetMuteForDeviceAsync(
        uint deviceId,
        bool muted,
        CancellationToken cancellationToken)
    {
        if (deviceId == 0)
        {
            throw new AudioServiceException("目前沒有可控制的 macOS 輸出裝置。");
        }

        var actualMuted = ReadMute(deviceId);
        if (actualMuted == muted)
        {
            return;
        }

        for (var attempt = 0; attempt < VolumeSetRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetMute(deviceId, muted);
            actualMuted = ReadMute(deviceId);

            if (actualMuted == muted)
            {
                return;
            }

            if (attempt + 1 < VolumeSetRetryCount)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        throw new AudioServiceException(
            $"macOS {(muted ? "靜音" : "解除靜音")}未成功。");
    }

    private static void SetMute(uint deviceId, bool muted)
    {
        var changedProperties = 0;
        var lastStatus = 0;
        var target = muted ? 1 : 0;

        for (uint element = 0; element <= 32; element++)
        {
            var address = new AudioObjectPropertyAddress(
                PropertyMute,
                ScopeOutput,
                element);

            if (!AudioObjectHasProperty(deviceId, ref address))
            {
                continue;
            }

            if (AudioObjectIsPropertySettable(deviceId, ref address, out var isSettable) != 0 || !isSettable)
            {
                continue;
            }

            var data = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(data, target);
                var status = AudioObjectSetPropertyData(
                    deviceId,
                    ref address,
                    0,
                    IntPtr.Zero,
                    sizeof(uint),
                    data);
                if (status == 0)
                {
                    changedProperties++;
                }
                else
                {
                    lastStatus = status;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        if (changedProperties == 0)
        {
            throw new AudioServiceException(
                $"設定 macOS {(muted ? "靜音" : "解除靜音")}失敗（CoreAudio OSStatus: 0x{lastStatus:X8}）。");
        }
    }

    private static bool ReadMute(uint deviceId)
    {
        if (deviceId == 0)
        {
            return false;
        }

        try
        {
            return ReadMuteValues(deviceId).Any(value => value.Muted);
        }
        catch (AudioServiceException)
        {
            // Some USB/virtual outputs do not expose a software mute control.
            // Treat that as "not muted" so volume and device switching remain
            // available; a mute write is attempted only after a true read.
            return false;
        }
    }

    private static IReadOnlyList<MuteValue> ReadMuteValues(uint deviceId)
    {
        var values = new List<MuteValue>();
        for (uint element = 0; element <= 32; element++)
        {
            var address = new AudioObjectPropertyAddress(
                PropertyMute,
                ScopeOutput,
                element);

            if (!AudioObjectHasProperty(deviceId, ref address))
            {
                continue;
            }

            var dataSize = GetPropertyDataSize(deviceId, ref address, "讀取 macOS 靜音狀態");
            if (dataSize < sizeof(uint))
            {
                continue;
            }

            var data = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                var ioDataSize = (uint)sizeof(uint);
                CheckStatus(
                    AudioObjectGetPropertyData(
                        deviceId,
                        ref address,
                        0,
                        IntPtr.Zero,
                        ref ioDataSize,
                        data),
                    "讀取 macOS 靜音狀態");
                values.Add(new MuteValue(
                    element,
                    Marshal.ReadInt32(data) != 0));
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        return values;
    }

    private static uint ParseDeviceId(AudioOutputDevice device)
    {
        if (!uint.TryParse(device.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var deviceId))
        {
            throw new AudioServiceException($"無法辨識音訊裝置識別碼：{device.Id}");
        }

        return deviceId;
    }

    private static async Task SetVolumeForDeviceAsync(
        uint deviceId,
        int percent,
        CancellationToken cancellationToken)
    {
        if (deviceId == 0)
        {
            throw new AudioServiceException("目前沒有可控制的 macOS 輸出裝置。");
        }

        var targetPercent = Math.Clamp(percent, 0, 100);
        var actualPercent = ReadVolume(deviceId);
        var actualMuted = ReadMute(deviceId);

        for (var attempt = 0; attempt < VolumeSetRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetVolume(deviceId, targetPercent);

            // Raising a device from zero does not necessarily clear its mute
            // bit on macOS. Make the requested non-zero level audible too.
            if (targetPercent > 0 && ReadMute(deviceId))
            {
                SetMute(deviceId, muted: false);
            }

            // CoreAudio publishes both device switching and volume changes
            // asynchronously. A same-tick read can observe the value just
            // written before macOS restores the device's saved state.
            await Task.Delay(VolumeReadbackDelay, cancellationToken);
            actualPercent = ReadVolume(deviceId);
            actualMuted = ReadMute(deviceId);

            if (IsRequestedVolume(actualPercent, actualMuted, targetPercent))
            {
                // Read again after the device-state propagation window. This
                // prevents a delayed per-device restore from changing 33%
                // immediately after this method reports success.
                await Task.Delay(StableVolumeDelay, cancellationToken);
                actualPercent = ReadVolume(deviceId);
                actualMuted = ReadMute(deviceId);
                if (IsRequestedVolume(actualPercent, actualMuted, targetPercent))
                {
                    return;
                }
            }

            if (attempt + 1 < VolumeSetRetryCount)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }

        throw new AudioServiceException(
            $"macOS 音量未能調整至 {targetPercent}%（目前讀回 {actualPercent}%{(actualMuted ? "，仍為靜音" : string.Empty)}）。");
    }

    private static bool IsRequestedVolume(int actualPercent, bool actualMuted, int targetPercent)
    {
        return Math.Abs(actualPercent - targetPercent) <= VolumeTolerancePercent
            && (targetPercent == 0 || !actualMuted);
    }

    public async Task SwitchOutputAsync(
        AudioOutputDevice device,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!uint.TryParse(device.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var deviceId))
        {
            throw new AudioServiceException($"無法辨識音訊裝置識別碼：{device.Id}");
        }

        SetDefaultOutputDevice(deviceId);

        // CoreAudio can acknowledge the write before the new default output
        // is observable. Give the system a short window to publish the new
        // default, while the following operation still targets the device ID
        // explicitly and therefore cannot affect the previous device.
        for (var attempt = 0; attempt < DeviceSwitchRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ReadDefaultOutputDevice() == deviceId)
            {
                break;
            }

            await Task.Delay(RetryDelay, cancellationToken);
        }

        // Leave a small settling window for macOS to finish applying the
        // target device's persisted state before its volume is overwritten.
        await Task.Delay(VolumeReadbackDelay, cancellationToken);

        // The set operation above succeeded. Some CoreAudio devices publish
        // the new default ID asynchronously, so do not abort the operation
        // merely because the property readback is still stale. The next step
        // addresses the target device by ID directly and remains safe even in
        // that short synchronization window.
    }

    private static int ReadVolume(uint deviceId)
    {
        if (deviceId == 0)
        {
            return 0;
        }

        if (TryReadVirtualMainVolume(deviceId, out var virtualScalar, out _))
        {
            return ScalarToPercent(virtualScalar);
        }

        var values = ReadVolumeValues(deviceId);
        if (values.Count == 0)
        {
            return 0;
        }

        // A device can expose a read-only master value alongside writable
        // per-channel values. Prefer values that are actually controllable so
        // readback verifies the same volume endpoint that SetVolume updates.
        var readableValues = values.Where(value => value.IsSettable).ToArray();
        if (readableValues.Length == 0)
        {
            readableValues = values.ToArray();
        }

        var hasMaster = readableValues.Any(value => value.Element == ElementMain);
        var scalar = hasMaster
            ? readableValues.First(value => value.Element == ElementMain).Scalar
            : readableValues.Average(value => value.Scalar);

        return ScalarToPercent(scalar);
    }

    private static int ScalarToPercent(float scalar)
    {
        return float.IsNaN(scalar) || float.IsInfinity(scalar)
            ? 0
            : Math.Clamp((int)Math.Round(scalar * 100, MidpointRounding.AwayFromZero), 0, 100);
    }

    private static void SetVolume(uint deviceId, int percent)
    {
        if (deviceId == 0)
        {
            throw new AudioServiceException("目前沒有可控制的 macOS 輸出裝置。");
        }

        var target = Math.Clamp(percent, 0, 100) / 100f;
        var changedProperties = 0;
        var lastStatus = 0;

        // This is the volume endpoint represented by the macOS menu-bar
        // slider. It applies the requested level to the device's virtual
        // master while preserving channel balance.
        var virtualAddress = new AudioObjectPropertyAddress(
            PropertyVirtualMainVolume,
            ScopeOutput,
            ElementMain);
        if (AudioHardwareServiceHasProperty(deviceId, ref virtualAddress)
            && AudioHardwareServiceIsPropertySettable(deviceId, ref virtualAddress, out var virtualSettable) == 0
            && virtualSettable)
        {
            var data = Marshal.AllocHGlobal(sizeof(float));
            try
            {
                Marshal.Copy(BitConverter.GetBytes(target), 0, data, sizeof(float));
                var status = AudioHardwareServiceSetPropertyData(
                    deviceId,
                    ref virtualAddress,
                    0,
                    IntPtr.Zero,
                    sizeof(float),
                    data);
                if (status == 0)
                {
                    return;
                }

                lastStatus = status;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        // Element 0 is the master channel. Some devices expose only their
        // individual channels, so also inspect the first 32 channel elements.
        for (uint element = 0; element <= 32; element++)
        {
            var address = new AudioObjectPropertyAddress(
                PropertyVolumeScalar,
                ScopeOutput,
                element);

            if (!AudioObjectHasProperty(deviceId, ref address))
            {
                continue;
            }

            if (AudioObjectIsPropertySettable(deviceId, ref address, out var isSettable) != 0 || !isSettable)
            {
                continue;
            }

            var data = Marshal.AllocHGlobal(sizeof(float));
            try
            {
                Marshal.Copy(BitConverter.GetBytes(target), 0, data, sizeof(float));
                var status = AudioObjectSetPropertyData(
                    deviceId,
                    ref address,
                    0,
                    IntPtr.Zero,
                    sizeof(float),
                    data);
                if (status == 0)
                {
                    changedProperties++;
                }
                else
                {
                    lastStatus = status;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        if (changedProperties == 0)
        {
            throw new AudioServiceException(
                $"設定 macOS 音量失敗（CoreAudio OSStatus: 0x{lastStatus:X8}，裝置可能不支援軟體音量）。");
        }
    }

    private static IReadOnlyList<VolumeValue> ReadVolumeValues(uint deviceId)
    {
        var values = new List<VolumeValue>();
        for (uint element = 0; element <= 32; element++)
        {
            var address = new AudioObjectPropertyAddress(
                PropertyVolumeScalar,
                ScopeOutput,
                element);

            if (!AudioObjectHasProperty(deviceId, ref address))
            {
                continue;
            }

            var dataSize = GetPropertyDataSize(deviceId, ref address, "讀取 macOS 音量");
            if (dataSize < sizeof(float))
            {
                continue;
            }

            var data = Marshal.AllocHGlobal(sizeof(float));
            try
            {
                var ioDataSize = (uint)sizeof(float);
                CheckStatus(
                    AudioObjectGetPropertyData(
                        deviceId,
                        ref address,
                        0,
                        IntPtr.Zero,
                        ref ioDataSize,
                        data),
                    "讀取 macOS 音量");
                var isSettable = AudioObjectIsPropertySettable(
                    deviceId,
                    ref address,
                    out var settable) == 0 && settable;
                values.Add(new VolumeValue(
                    element,
                    Marshal.PtrToStructure<float>(data),
                    isSettable));
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        return values;
    }

    private static bool TryReadVirtualMainVolume(
        uint deviceId,
        out float scalar,
        out bool isSettable)
    {
        scalar = 0;
        isSettable = false;
        var address = new AudioObjectPropertyAddress(
            PropertyVirtualMainVolume,
            ScopeOutput,
            ElementMain);
        // The vmvc selector is provided by Audio Hardware Service and must be
        // queried through its API; AudioObject* calls do not expose it reliably.
        if (!AudioHardwareServiceHasProperty(deviceId, ref address))
        {
            return false;
        }

        var data = Marshal.AllocHGlobal(sizeof(float));
        try
        {
            var ioDataSize = (uint)sizeof(float);
            var status = AudioHardwareServiceGetPropertyData(
                deviceId,
                ref address,
                0,
                IntPtr.Zero,
                ref ioDataSize,
                data);
            if (status != 0 || ioDataSize < sizeof(float))
            {
                return false;
            }

            scalar = Marshal.PtrToStructure<float>(data);
            isSettable = AudioHardwareServiceIsPropertySettable(
                deviceId,
                ref address,
                out var settable) == 0 && settable;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static IReadOnlyList<AudioOutputDevice> ReadOutputDevices()
    {
        var address = new AudioObjectPropertyAddress(PropertyDevices, ScopeGlobal, ElementMain);
        var dataSize = GetPropertyDataSize(SystemObjectId, ref address, "列舉音訊裝置");

        if (dataSize < sizeof(uint))
        {
            return Array.Empty<AudioOutputDevice>();
        }

        var data = Marshal.AllocHGlobal(checked((int)dataSize));
        try
        {
            var ioDataSize = dataSize;
            CheckStatus(
                AudioObjectGetPropertyData(
                    SystemObjectId,
                    ref address,
                    0,
                    IntPtr.Zero,
                    ref ioDataSize,
                    data),
                "讀取音訊裝置");

            var deviceCount = (int)(ioDataSize / sizeof(uint));
            var devices = new List<AudioOutputDevice>(deviceCount);

            for (var index = 0; index < deviceCount; index++)
            {
                var deviceId = unchecked((uint)Marshal.ReadInt32(data, index * sizeof(uint)));
                if (deviceId == 0 || !HasOutputChannels(deviceId))
                {
                    continue;
                }

                var name = ReadDeviceName(deviceId);
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"Audio device {deviceId}";
                }

                devices.Add(new AudioOutputDevice(
                    deviceId.ToString(CultureInfo.InvariantCulture),
                    name,
                    Classify(name)));
            }

            return devices;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static bool HasOutputChannels(uint deviceId)
    {
        var address = new AudioObjectPropertyAddress(
            PropertyStreamConfiguration,
            ScopeOutput,
            ElementMain);

        if (!AudioObjectHasProperty(deviceId, ref address))
        {
            return false;
        }

        var dataSize = GetPropertyDataSize(deviceId, ref address, "讀取輸出聲道");
        if (dataSize < sizeof(uint))
        {
            return false;
        }

        var data = Marshal.AllocHGlobal(checked((int)dataSize));
        try
        {
            var ioDataSize = dataSize;
            CheckStatus(
                AudioObjectGetPropertyData(
                    deviceId,
                    ref address,
                    0,
                    IntPtr.Zero,
                    ref ioDataSize,
                    data),
                "讀取輸出聲道");

            var bufferCount = unchecked((uint)Marshal.ReadInt32(data));
            var bufferSize = Marshal.SizeOf<AudioBuffer>();
            // AudioBuffer contains a pointer and is therefore 8-byte aligned
            // on macOS.  AudioBufferList has padding after mNumberBuffers;
            // use the managed layout to find mBuffers instead of assuming it
            // starts immediately at offset 4.
            var firstBufferOffset = Marshal.OffsetOf<AudioBufferListLayout>(
                nameof(AudioBufferListLayout.FirstBuffer)).ToInt32();

            for (var index = 0u; index < bufferCount; index++)
            {
                var bufferOffset = checked(firstBufferOffset + (int)index * bufferSize);
                if (bufferOffset < 0 || (uint)(bufferOffset + sizeof(uint)) > ioDataSize)
                {
                    break;
                }

                if (Marshal.ReadInt32(data, bufferOffset) > 0)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static string ReadDeviceName(uint deviceId)
    {
        var address = new AudioObjectPropertyAddress(PropertyName, ScopeGlobal, ElementMain);
        var data = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            var dataSize = (uint)IntPtr.Size;
            CheckStatus(
                AudioObjectGetPropertyData(
                    deviceId,
                    ref address,
                    0,
                    IntPtr.Zero,
                    ref dataSize,
                    data),
                "讀取音訊裝置名稱");

            var cfString = Marshal.ReadIntPtr(data);
            if (cfString == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                var utf8Pointer = CFStringGetCStringPtr(cfString, Utf8Encoding);
                if (utf8Pointer != IntPtr.Zero)
                {
                    return Marshal.PtrToStringUTF8(utf8Pointer) ?? string.Empty;
                }

                var buffer = new StringBuilder(512);
                return CFStringGetCString(cfString, buffer, buffer.Capacity, Utf8Encoding)
                    ? buffer.ToString()
                    : string.Empty;
            }
            finally
            {
                CFRelease(cfString);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static uint ReadDefaultOutputDevice()
    {
        var address = new AudioObjectPropertyAddress(
            PropertyDefaultOutputDevice,
            ScopeGlobal,
            ElementMain);
        var data = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            var dataSize = (uint)sizeof(uint);
            CheckStatus(
                AudioObjectGetPropertyData(
                    SystemObjectId,
                    ref address,
                    0,
                    IntPtr.Zero,
                    ref dataSize,
                    data),
                "讀取目前輸出裝置");

            return unchecked((uint)Marshal.ReadInt32(data));
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static void SetDefaultOutputDevice(uint deviceId)
    {
        var address = new AudioObjectPropertyAddress(
            PropertyDefaultOutputDevice,
            ScopeGlobal,
            ElementMain);
        var data = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(data, unchecked((int)deviceId));
            CheckStatus(
                AudioObjectSetPropertyData(
                    SystemObjectId,
                    ref address,
                    0,
                    IntPtr.Zero,
                    sizeof(uint),
                    data),
                "切換 macOS 輸出裝置失敗");
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private static uint GetPropertyDataSize(
        uint objectId,
        ref AudioObjectPropertyAddress address,
        string operation)
    {
        CheckStatus(
            AudioObjectGetPropertyDataSize(
                objectId,
                ref address,
                0,
                IntPtr.Zero,
                out var dataSize),
            operation);
        return dataSize;
    }

    private static AudioOutputKind Classify(string name)
    {
        var value = name.ToLowerInvariant();

        if (ContainsAny(value, "headphone", "headset", "airpods", "earphone", "earbud", "beats", "耳機", "耳塞"))
        {
            return AudioOutputKind.Headphones;
        }

        if (ContainsAny(value, "speaker", "built-in output", "built-in speakers", "internal", "喇叭", "揚聲器", "內建輸出", "內建喇叭"))
        {
            return AudioOutputKind.Speakers;
        }

        return AudioOutputKind.Unknown;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
    }

    private static void CheckStatus(int status, string operation)
    {
        if (status != 0)
        {
            throw new AudioServiceException(
                $"{operation}（CoreAudio OSStatus: 0x{status:X8}）。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct AudioObjectPropertyAddress
    {
        public AudioObjectPropertyAddress(uint selector, uint scope, uint element)
        {
            Selector = selector;
            Scope = scope;
            Element = element;
        }

        public uint Selector { get; }
        public uint Scope { get; }
        public uint Element { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct AudioBuffer
    {
        public readonly uint NumberChannels;
        public readonly uint DataByteSize;
        public readonly IntPtr Data;
    }

    private readonly record struct VolumeValue(uint Element, float Scalar, bool IsSettable);

    private readonly record struct MuteValue(uint Element, bool Muted);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct AudioBufferListLayout
    {
        public readonly uint NumberBuffers;
        public readonly AudioBuffer FirstBuffer;
    }

    [DllImport(CoreAudio)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AudioObjectHasProperty(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyDataSize(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        out uint outDataSize);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        IntPtr outData);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectSetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        uint inDataSize,
        IntPtr inData);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectIsPropertySettable(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        [MarshalAs(UnmanagedType.I1)] out bool outIsSettable);

    [DllImport(AudioToolbox)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AudioHardwareServiceHasProperty(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress);

    [DllImport(AudioToolbox)]
    private static extern int AudioHardwareServiceIsPropertySettable(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        [MarshalAs(UnmanagedType.I1)] out bool outIsSettable);

    [DllImport(AudioToolbox)]
    private static extern int AudioHardwareServiceGetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        ref uint ioDataSize,
        IntPtr outData);

    [DllImport(AudioToolbox)]
    private static extern int AudioHardwareServiceSetPropertyData(
        uint inObjectID,
        ref AudioObjectPropertyAddress inAddress,
        uint inQualifierDataSize,
        IntPtr inQualifierData,
        uint inDataSize,
        IntPtr inData);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringGetCStringPtr(
        IntPtr theString,
        uint encoding);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(
        IntPtr theString,
        [Out] StringBuilder buffer,
        int bufferSize,
        uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cfTypeRef);
}
