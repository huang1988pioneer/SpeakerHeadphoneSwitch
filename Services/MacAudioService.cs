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
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint SystemObjectId = 1;
    private const uint PropertyDevices = 0x6465_7623; // 'dev#'
    private const uint PropertyDefaultOutputDevice = 0x644F_7574; // 'dOut'
    private const uint PropertyName = 0x6C6E_616D; // 'lnam'
    private const uint PropertyStreamConfiguration = 0x736C_6179; // 'slay'
    private const uint PropertyVolumeScalar = 0x766F_6C6D; // 'volm'
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

        return Task.FromResult(new AudioSnapshot(devices, current, volume));
    }

    public Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetVolume(ReadDefaultOutputDevice(), Math.Clamp(percent, 0, 100));
        return Task.CompletedTask;
    }

    public Task SwitchOutputAsync(
        AudioOutputDevice device,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!uint.TryParse(device.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var deviceId))
        {
            throw new AudioServiceException($"無法辨識音訊裝置識別碼：{device.Id}");
        }

        SetDefaultOutputDevice(deviceId);
        return Task.CompletedTask;
    }

    private static int ReadVolume(uint deviceId)
    {
        if (deviceId == 0)
        {
            return 0;
        }

        var values = ReadVolumeValues(deviceId);
        return values.Count == 0
            ? 0
            : Math.Clamp((int)Math.Round(values.Average() * 100, MidpointRounding.AwayFromZero), 0, 100);
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

    private static IReadOnlyList<float> ReadVolumeValues(uint deviceId)
    {
        var values = new List<float>();
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
                values.Add(Marshal.PtrToStructure<float>(data));
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }

        return values;
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

            var bufferCount = Marshal.ReadInt32(data);
            var bufferSize = Marshal.SizeOf<AudioBuffer>();
            for (var index = 0; index < bufferCount; index++)
            {
                var bufferOffset = sizeof(uint) + index * bufferSize;
                if (bufferOffset + sizeof(uint) <= ioDataSize && Marshal.ReadInt32(data, bufferOffset) > 0)
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
