using SpeakerHeadphoneSwitch.Models;

namespace SpeakerHeadphoneSwitch.Services;

/// <summary>以裝置名稱關鍵字判斷耳機或喇叭，作為各平台硬體資訊之外的共用判斷。</summary>
internal static class DeviceNameClassifier
{
    private static readonly string[] HeadphoneKeywords =
        ["headphone", "headset", "earphone", "earbuds", "airpods", "耳機", "耳机"];

    private static readonly string[] SpeakerKeywords =
        ["speaker", "喇叭", "揚聲器", "扬声器"];

    public static bool LooksBluetooth(string name) =>
        name.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) || name.Contains("藍牙") || name.Contains("蓝牙");

    public static DeviceKind Classify(string name)
    {
        var lower = name.ToLowerInvariant();
        if (HeadphoneKeywords.Any(lower.Contains))
            return DeviceKind.Headphone;
        if (SpeakerKeywords.Any(lower.Contains))
            return DeviceKind.Speaker;
        return DeviceKind.Unknown;
    }
}
