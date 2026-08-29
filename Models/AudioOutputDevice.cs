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
}

public sealed record AudioSnapshot(
    IReadOnlyList<AudioOutputDevice> Devices,
    AudioOutputDevice? CurrentDevice,
    int VolumePercent,
    bool IsMuted = false);
