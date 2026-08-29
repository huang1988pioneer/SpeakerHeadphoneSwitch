using SpeakerHeadphoneSwitch.Models;

namespace SpeakerHeadphoneSwitch.Services;

public interface IAudioService
{
    string PlatformDescription { get; }

    Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether the current default output is muted.
    /// </summary>
    Task<bool> GetMuteStateAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Changes mute state on the current default output.
    /// </summary>
    Task SetMuteStateAsync(
        bool muted,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Sets a specific output device's volume. Implementations that cannot
    /// address a device independently may fall back to the current output.
    /// </summary>
    Task SetDeviceVolumeAsync(
        AudioOutputDevice device,
        int percent,
        CancellationToken cancellationToken = default)
        => SetVolumeAsync(percent, cancellationToken);

    /// <summary>
    /// Returns whether a particular output device is muted.
    /// </summary>
    Task<bool> GetDeviceMuteStateAsync(
        AudioOutputDevice device,
        CancellationToken cancellationToken = default)
        => GetMuteStateAsync(cancellationToken);

    /// <summary>
    /// Changes mute state on a particular output device.
    /// </summary>
    Task SetDeviceMuteStateAsync(
        AudioOutputDevice device,
        bool muted,
        CancellationToken cancellationToken = default)
        => SetMuteStateAsync(muted, cancellationToken);

    Task SwitchOutputAsync(AudioOutputDevice device, CancellationToken cancellationToken = default);
}

public sealed class AudioServiceException : Exception
{
    public AudioServiceException(string message)
        : base(message)
    {
    }

    public AudioServiceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
