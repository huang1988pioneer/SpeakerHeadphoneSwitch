namespace SpeakerHeadphoneSwitch.Models;

public enum AudioOutputKind
{
    Unknown,
    Speakers,
    Headphones,
}

public sealed record AudioOutputDevice(
    string Id,
    string Name,
    AudioOutputKind Kind)
{
    /// <summary>
    /// Whether this output uses Bluetooth transport. This is independent from
    /// <see cref="Kind"/> because a Bluetooth device can be either a speaker
    /// or headphones.
    /// </summary>
    public bool IsBluetooth { get; set; }

    /// <summary>
    /// User-facing device name. macOS may return the traditional Chinese
    /// term "揚聲器" for speakers; use the app's preferred term "喇叭"
    /// without changing the original name used by the audio APIs.
    /// </summary>
    public string DisplayName => Name
        .Replace("揚聲器", "喇叭", StringComparison.Ordinal)
        .Replace("扬声器", "喇叭", StringComparison.Ordinal);

    public string KindLabel => Kind switch
    {
        AudioOutputKind.Speakers => "喇叭",
        AudioOutputKind.Headphones => "耳機",
        _ => "其他輸出",
    };

    public string Icon => Kind == AudioOutputKind.Headphones ? "♬" : "◉";

    public static bool LooksLikeBluetooth(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.ToLowerInvariant();
        return ContainsAny(
            normalized,
            "bluetooth",
            "bluez",
            "a2dp",
            "hfp",
            "hsp",
            "handsfree",
            "hands-free",
            "藍牙",
            "蓝牙");
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
    }
}

public sealed record AudioSnapshot(
    IReadOnlyList<AudioOutputDevice> Devices,
    AudioOutputDevice? CurrentDevice,
    int VolumePercent,
    bool IsMuted = false);
