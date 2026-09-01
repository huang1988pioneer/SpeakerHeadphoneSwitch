using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SpeakerHeadphoneSwitch.Converters;

public sealed class AssetImageConverter : IValueConverter
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var uri = path.StartsWith("avares://", StringComparison.Ordinal)
            ? new Uri(path)
            : new Uri($"avares://SpeakerHeadphoneSwitch{path}");

        var bitmap = new Bitmap(AssetLoader.Open(uri));
        Cache[path] = bitmap;
        return bitmap;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
