using System.Globalization;
using System.Text.Json;
using SpeakerHeadphoneSwitch.Models;

namespace SpeakerHeadphoneSwitch.Services;

/// <summary>
/// Linux implementation for PulseAudio and PipeWire's PulseAudio compatibility layer.
/// </summary>
internal sealed class PactlAudioService : IAudioService
{
    public string PlatformDescription => "Linux pactl";

    public async Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var sinksResult = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "-f", "json", "list", "sinks" },
            cancellationToken);
        sinksResult.ThrowIfFailed("讀取 Linux 輸出裝置失敗");

        var defaultResult = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "get-default-sink" },
            cancellationToken);
        defaultResult.ThrowIfFailed("讀取目前 Linux 輸出裝置失敗");

        var volumeResult = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "get-sink-volume", "@DEFAULT_SINK@" },
            cancellationToken);
        volumeResult.ThrowIfFailed("讀取 Linux 音量失敗");

        var muteResult = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "get-sink-mute", "@DEFAULT_SINK@" },
            cancellationToken);
        muteResult.ThrowIfFailed("讀取 Linux 靜音狀態失敗");

        var devices = ParseDevices(sinksResult.StandardOutput);
        var currentId = defaultResult.StandardOutput.Trim();
        var current = devices.FirstOrDefault(device => device.Id == currentId);
        var volume = ParseVolume(volumeResult.StandardOutput);
        var isMuted = ParseMute(muteResult.StandardOutput);

        return new AudioSnapshot(devices, current, volume, isMuted);
    }

    public async Task SetVolumeAsync(int percent, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "set-sink-volume", "@DEFAULT_SINK@", $"{Math.Clamp(percent, 0, 100)}%" },
            cancellationToken);
        result.ThrowIfFailed("設定 Linux 音量失敗");
    }

    public async Task<bool> GetMuteStateAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "get-sink-mute", "@DEFAULT_SINK@" },
            cancellationToken);
        result.ThrowIfFailed("讀取 Linux 靜音狀態失敗");
        return ParseMute(result.StandardOutput);
    }

    public async Task SetMuteStateAsync(
        bool muted,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "set-sink-mute", "@DEFAULT_SINK@", muted ? "1" : "0" },
            cancellationToken);
        result.ThrowIfFailed($"設定 Linux {(muted ? "靜音" : "解除靜音")}失敗");
    }

    public async Task SetDeviceVolumeAsync(
        AudioOutputDevice device,
        int percent,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "set-sink-volume", device.Id, $"{Math.Clamp(percent, 0, 100)}%" },
            cancellationToken);
        result.ThrowIfFailed("設定 Linux 音量失敗");
    }

    public async Task<bool> GetDeviceMuteStateAsync(
        AudioOutputDevice device,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "get-sink-mute", device.Id },
            cancellationToken);
        result.ThrowIfFailed("讀取 Linux 裝置靜音狀態失敗");
        return ParseMute(result.StandardOutput);
    }

    public async Task SetDeviceMuteStateAsync(
        AudioOutputDevice device,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "set-sink-mute", device.Id, muted ? "1" : "0" },
            cancellationToken);
        result.ThrowIfFailed($"設定 Linux 裝置 {(muted ? "靜音" : "解除靜音")}失敗");
    }

    public async Task SwitchOutputAsync(
        AudioOutputDevice device,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "pactl",
            new[] { "set-default-sink", device.Id },
            cancellationToken);
        result.ThrowIfFailed("切換 Linux 輸出裝置失敗");
    }

    private static IReadOnlyList<AudioOutputDevice> ParseDevices(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var devices = new List<AudioOutputDevice>();

            foreach (var sink in document.RootElement.EnumerateArray())
            {
                var id = ReadString(sink, "name");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var description = ReadString(sink, "description");
                var displayName = string.IsNullOrWhiteSpace(description) ? id : description;
                var metadata = $"{id} {description} {ReadPropertyValues(sink)}";
                devices.Add(new AudioOutputDevice(id, displayName, Classify(metadata))
                {
                    IsBluetooth = AudioOutputDevice.LooksLikeBluetooth(metadata),
                });
            }

            return devices;
        }
        catch (JsonException exception)
        {
            throw new AudioServiceException("Linux 音訊工具回傳了無法解析的裝置資料。", exception);
        }
    }

    private static int ParseVolume(string output)
    {
        var percentIndex = output.IndexOf('%');
        if (percentIndex < 1)
        {
            return 0;
        }

        var start = percentIndex - 1;
        while (start >= 0 && char.IsDigit(output[start]))
        {
            start--;
        }

        var value = output[(start + 1)..percentIndex];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume)
            ? Math.Clamp(volume, 0, 100)
            : 0;
    }

    private static bool ParseMute(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Mute:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return trimmed[(trimmed.IndexOf(':') + 1)..]
                .Trim()
                .Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string ReadPropertyValues(JsonElement sink)
    {
        if (!sink.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            properties.EnumerateObject().Select(property => $"{property.Name} {property.Value}"));
    }

    private static AudioOutputKind Classify(string name)
    {
        var value = name.ToLowerInvariant();

        if (ContainsAny(value, "headphone", "headset", "airpods", "earphone", "earbud", "耳機", "耳塞"))
        {
            return AudioOutputKind.Headphones;
        }

        if (ContainsAny(value, "speaker", "analog-stereo", "built-in", "hdmi", "喇叭", "揚聲器", "內建"))
        {
            return AudioOutputKind.Speakers;
        }

        return AudioOutputKind.Unknown;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));
    }
}
