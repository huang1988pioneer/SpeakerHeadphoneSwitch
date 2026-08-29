using SpeakerHeadphoneSwitch.Models;

namespace SpeakerHeadphoneSwitch.Services;

public interface IAudioService
{
    string PlatformDescription { get; }

    Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default);

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
