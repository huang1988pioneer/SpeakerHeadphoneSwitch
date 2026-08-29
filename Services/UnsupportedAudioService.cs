using SpeakerHeadphoneSwitch.Models;

namespace SpeakerHeadphoneSwitch.Services;

internal sealed class UnsupportedAudioService : IAudioService
{
    private readonly string _platform;

    public UnsupportedAudioService(string platform)
    {
        _platform = platform;
    }

    public string PlatformDescription => _platform;

    public Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        throw new AudioServiceException(
            "目前只支援 macOS（CoreAudio）與 Linux（PulseAudio/PipeWire pactl）。");
    }

    public Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default)
    {
        throw new AudioServiceException("此作業系統尚未提供音量控制實作。");
    }

    public Task SwitchOutputAsync(AudioOutputDevice device, CancellationToken cancellationToken = default)
    {
        throw new AudioServiceException("此作業系統尚未提供輸出裝置切換實作。");
    }
}
