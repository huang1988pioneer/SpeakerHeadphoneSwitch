namespace SpeakerHeadphoneSwitch.Models;

public enum DeviceKind
{
    Unknown,
    Headphone,
    Speaker,
}

public record AudioDevice(string Id, string Name, DeviceKind Kind);
