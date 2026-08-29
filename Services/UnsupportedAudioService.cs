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

    public Task<bool> GetMuteStateAsync(CancellationToken cancellationToken = default)
    {
        throw new AudioServiceException("此作業系統尚未提供靜音狀態讀取實作。");
    }

    public Task SetMuteStateAsync(bool muted, CancellationToken cancellationToken = default)
    {
        throw new AudioServiceException("此作業系統尚未提供靜音控制實作。");
    }

    public Task SetDeviceVolumeAsync(
        AudioOutputDevice device,
        int percent,
        CancellationToken cancellationToken = default)
    {
        throw new AudioServiceException("此作業系統尚未提供指定輸出裝置的音量控制實作。");
    }

    public Task<bool> GetDeviceMuteStateAsync(
        AudioOutputDevice device,
        CancellationToken cancellationToken = default)
    {
        throw new AudioServiceException("此作業系統尚未提供指定輸出裝置的靜音狀態讀取實作。");
    }

    public Task SetDeviceMuteStateAsync(
        AudioOutputDevice device,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        throw new AudioServiceException("此作業系統尚未提供指定輸出裝置的靜音控制實作。");
    }

    public Task SwitchOutputAsync(AudioOutputDevice device, CancellationToken cancellationToken = default)
    {
        throw new AudioServiceException("此作業系統尚未提供輸出裝置切換實作。");
    }
}
