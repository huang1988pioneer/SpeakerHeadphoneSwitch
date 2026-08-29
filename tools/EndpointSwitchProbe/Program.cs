using System.Diagnostics;
using System.Runtime.InteropServices;
using SpeakerHeadphoneSwitch.Models;
using SpeakerHeadphoneSwitch.Services;

if (!args.Any(argument => string.Equals(argument, "--verify-switch", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("用法: EndpointSwitchProbe.exe --verify-switch");
    Console.WriteLine("此命令會執行一次切換驗證，結束後盡力恢復原本裝置與音量。");
    return 2;
}

var audio = new WindowsAudioService();
var original = audio.GetDefaultOutputDevice();
if (original is null)
{
    Console.WriteLine("RESULT=FAIL");
    Console.WriteLine("原因: 找不到目前的預設輸出裝置。");
    return 1;
}

var devices = audio.GetActiveOutputDevices();
var targetKind = original.Kind == DeviceKind.Headphone ? DeviceKind.Speaker : DeviceKind.Headphone;
var target = devices.FirstOrDefault(device => device.Id != original.Id && device.Kind == targetKind)
             ?? devices.FirstOrDefault(device => device.Id != original.Id);
if (target is null)
{
    Console.WriteLine("RESULT=FAIL");
    Console.WriteLine("原因: 找不到另一個可切換的輸出裝置。");
    return 1;
}

var originalVolume = audio.TryGetVolumePercent(original.Id);
var targetVolume = audio.TryGetVolumePercent(target.Id);
var originalEndpoint = EndpointVolumeProbe.Read(original.Id);
var targetEndpointBefore = EndpointVolumeProbe.Read(target.Id);

Console.WriteLine("=== Windows 端點音量切換驗證 ===");
Console.WriteLine($"目前: {original.Name}");
Console.WriteLine($"目標: {target.Name}");
PrintProbe("目前端點音量(切換前)", originalEndpoint);
PrintProbe("目標端點音量(切換前)", targetEndpointBefore);

var stopwatch = Stopwatch.StartNew();
var currentSet = false;
var defaultSet = false;
var targetSet = false;
EndpointReadResult currentAfterZero = EndpointReadResult.Failed("尚未執行");
EndpointReadResult targetAfterThirtyThree = EndpointReadResult.Failed("尚未執行");
Exception? switchException = null;

try
{
    // 使用正式服務執行寫入，再用獨立端點 COM 介面讀回，避免 fallback session 值誤判成功。
    currentSet = audio.TrySetVolumePercent(original.Id, 0f);
    currentAfterZero = ReadWithRetry(original.Id, expected: 0f);

    try
    {
        audio.SetDefaultOutputDevice(target.Id);
        defaultSet = WaitForDefault(audio, target.Id, TimeSpan.FromSeconds(1));
    }
    catch (Exception ex)
    {
        switchException = ex;
    }

    if (switchException is null)
    {
        targetSet = audio.TrySetVolumePercent(target.Id, 33f);
        targetAfterThirtyThree = ReadWithRetry(target.Id, expected: 33f);
    }
}
finally
{
    stopwatch.Stop();
    Restore(audio, original, target, originalVolume, targetVolume);
}

var elapsed = stopwatch.Elapsed;
var currentPass = currentSet && currentAfterZero.IsWithin(0f);
var defaultPass = defaultSet && switchException is null;
var targetPass = targetSet && targetAfterThirtyThree.IsWithin(33f);
var result = currentPass && defaultPass && targetPass && elapsed <= TimeSpan.FromSeconds(3);

Console.WriteLine();
Console.WriteLine("=== 驗證結果 ===");
Console.WriteLine($"步驟 1 設定目前裝置 0%: {(currentSet ? "呼叫成功" : "呼叫失敗")}");
PrintProbe("步驟 1 端點讀回", currentAfterZero);
Console.WriteLine($"步驟 2 切換預設裝置: {(defaultPass ? "成功" : "失敗")}");
if (switchException is not null)
    Console.WriteLine($"切換錯誤: {switchException.Message}");
Console.WriteLine($"步驟 3 設定目標裝置 33%: {(targetSet ? "呼叫成功" : "呼叫失敗")}");
PrintProbe("步驟 3 端點讀回", targetAfterThirtyThree);
Console.WriteLine($"完整切換耗時: {elapsed.TotalMilliseconds:0} ms (上限 3000 ms)");
Console.WriteLine($"ASSERT_BEFORE_ZERO={(currentPass ? "PASS" : "FAIL")}");
Console.WriteLine($"ASSERT_AFTER_33={(targetPass ? "PASS" : "FAIL")}");
Console.WriteLine($"ASSERT_WITHIN_3S={(elapsed <= TimeSpan.FromSeconds(3) ? "PASS" : "FAIL")}");
Console.WriteLine($"RESULT={(result ? "PASS" : "FAIL")}");

return result ? 0 : 1;

static EndpointReadResult ReadWithRetry(string deviceId, float expected)
{
    var last = EndpointReadResult.Failed("尚未讀取");
    for (var attempt = 0; attempt < 8; attempt++)
    {
        last = EndpointVolumeProbe.Read(deviceId);
        if (last.IsWithin(expected))
            return last;
        Thread.Sleep(50);
    }

    return last;
}

static bool WaitForDefault(WindowsAudioService audio, string deviceId, TimeSpan timeout)
{
    var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
    while (Stopwatch.GetTimestamp() < deadline)
    {
        if (audio.GetDefaultOutputDevice()?.Id == deviceId)
            return true;
        Thread.Sleep(25);
    }

    return false;
}

static void Restore(
    WindowsAudioService audio,
    AudioDevice original,
    AudioDevice target,
    float? originalVolume,
    float? targetVolume)
{
    try
    {
        audio.SetDefaultOutputDevice(original.Id);
    }
    catch
    {
        // 驗證失敗時仍盡力恢復，原始錯誤會在結果區顯示。
    }

    if (originalVolume is float oldVolume)
        audio.TrySetVolumePercent(original.Id, oldVolume);
    if (targetVolume is float oldTargetVolume)
        audio.TrySetVolumePercent(target.Id, oldTargetVolume);
}

static void PrintProbe(string label, EndpointReadResult result)
{
    Console.WriteLine(result.Percent is float value
        ? $"{label}: {value:0.##}%"
        : $"{label}: 無法讀取 ({result.Error})");
}

internal readonly record struct EndpointReadResult(float? Percent, string Error)
{
    public bool IsWithin(float expected) => Percent is float value && Math.Abs(value - expected) <= 0.5f;

    public static EndpointReadResult Failed(string error) => new(null, error);
}

internal static class EndpointVolumeProbe
{
    private const int ClsCtxAll = 0x17;
    private static readonly Guid DeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid DeviceEnumeratorIid = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public static EndpointReadResult Read(string deviceId)
    {
        try
        {
            var clsid = DeviceEnumeratorClsid;
            var iid = DeviceEnumeratorIid;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxAll, ref iid, out var enumeratorPointer);
            if (hr < 0)
                return EndpointReadResult.Failed($"CoCreate {FormatHResult(hr)}");

            try
            {
                var enumerator = (IProbeMMDeviceEnumerator)Marshal.GetTypedObjectForIUnknown(
                    enumeratorPointer,
                    typeof(IProbeMMDeviceEnumerator));
                hr = enumerator.GetDevice(deviceId, out var device);
                if (hr < 0)
                    return EndpointReadResult.Failed($"GetDevice {FormatHResult(hr)}");

                var endpointIid = EndpointVolumeIid;
                hr = device.Activate(ref endpointIid, ClsCtxAll, IntPtr.Zero, out var endpointPointer);
                if (hr < 0)
                    return EndpointReadResult.Failed($"Activate {FormatHResult(hr)}");

                try
                {
                    var endpoint = (IProbeAudioEndpointVolume)Marshal.GetTypedObjectForIUnknown(
                        endpointPointer,
                        typeof(IProbeAudioEndpointVolume));
                    hr = endpoint.GetMasterVolumeLevelScalar(out var level);
                    return hr < 0
                        ? EndpointReadResult.Failed($"GetMaster {FormatHResult(hr)}")
                        : new EndpointReadResult(level * 100f, string.Empty);
                }
                finally
                {
                    Marshal.Release(endpointPointer);
                }
            }
            finally
            {
                Marshal.Release(enumeratorPointer);
            }
        }
        catch (Exception ex)
        {
            return EndpointReadResult.Failed(ex.GetType().Name + ": " + ex.Message);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr outer,
        int clsContext,
        ref Guid iid,
        out IntPtr instance);

    private static string FormatHResult(int hr) => $"0x{unchecked((uint)hr):X8}";
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProbeMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(ProbeEDataFlow dataFlow, int stateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(ProbeEDataFlow dataFlow, ProbeERole role, out IntPtr endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IProbeMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProbeMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsContext, IntPtr activationParams, out IntPtr interfacePointer);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProbeAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int GetChannelCount(out uint channelCount);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);
}

internal enum ProbeEDataFlow
{
    Render,
    Capture,
    All,
}

internal enum ProbeERole
{
    Console,
    Multimedia,
    Communications,
}
