namespace SpeakerHeadphoneSwitch.Models;

public enum DeviceKind
{
    Unknown,
    Headphone,
    Speaker,
}

/// <param name="IsBluetooth">是否為藍牙裝置；用來區分藍牙喇叭與有線／內建喇叭。</param>
public record AudioDevice(string Id, string Name, DeviceKind Kind, bool IsBluetooth = false);
