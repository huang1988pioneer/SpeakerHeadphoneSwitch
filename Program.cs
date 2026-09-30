using System;
using Avalonia;
using Avalonia.Media;

namespace SpeakerHeadphoneSwitch;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new FontManagerOptions
        {
            DefaultFamilyName = OperatingSystem.IsMacOS() ? "PingFang TC" : "Microsoft YaHei UI",
        })
        .LogToTrace();
}
