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
    int VolumePercent);
