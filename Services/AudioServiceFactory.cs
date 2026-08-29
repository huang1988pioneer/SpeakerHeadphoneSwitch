using System.Runtime.InteropServices;

namespace SpeakerHeadphoneSwitch.Services;

public static class AudioServiceFactory
{
    public static IAudioService Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new MacAudioService();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new PactlAudioService();
        }

        return new UnsupportedAudioService(RuntimeInformation.OSDescription);
    }
}
