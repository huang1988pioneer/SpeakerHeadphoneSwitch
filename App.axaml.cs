using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SpeakerHeadphoneSwitch.Models;
using SpeakerHeadphoneSwitch.Services;
using SpeakerHeadphoneSwitch.ViewModels;
using SpeakerHeadphoneSwitch.Views;

namespace SpeakerHeadphoneSwitch;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Environment.GetCommandLineArgs().Contains("--diagnose-audio", StringComparer.Ordinal))
            {
                desktop.MainWindow = new Window
                {
                    Width = 1,
                    Height = 1,
                    ShowInTaskbar = false,
                    Opacity = 0,
                };
            }
            else
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                };
            }
        }

        base.OnFrameworkInitializationCompleted();

        if (Environment.GetCommandLineArgs().Contains("--diagnose-audio", StringComparer.Ordinal)
            && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime diagnosticDesktop)
        {
            _ = RunAudioDiagnosticAsync(diagnosticDesktop);
        }
    }

    private static async Task RunAudioDiagnosticAsync(
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        const string path = "/tmp/SpeakerHeadphoneSwitch-diagnostic.log";
        try
        {
            File.WriteAllText(path, $"{DateTime.Now:O} started{Environment.NewLine}");
            var service = AudioServiceFactory.Create();
            var before = await service.GetSnapshotAsync();
            WriteDiagnostic(path, before, "before");

            var target = before.Devices.FirstOrDefault(device => device.Id != before.CurrentDevice?.Id);
            if (target is not null)
            {
                await service.SetVolumeAsync(0);
                await service.SwitchOutputAsync(target);
                await service.SetDeviceMuteStateAsync(target, false);
                await service.SetDeviceVolumeAsync(target, 33);
                await service.SetDeviceMuteStateAsync(target, false);

                for (var attempt = 1; attempt <= 6; attempt++)
                {
                    await Task.Delay(250);
                    WriteDiagnostic(path, await service.GetSnapshotAsync(), $"after-{attempt}");
                }
            }
        }
        catch (Exception exception)
        {
            File.AppendAllText(path, $"error={exception}{Environment.NewLine}");
        }
        finally
        {
            desktop.Shutdown();
        }
    }

    private static void WriteDiagnostic(
        string path,
        AudioSnapshot snapshot,
        string label)
    {
        var current = snapshot.CurrentDevice;
        File.AppendAllText(
            path,
            $"{DateTime.Now:O} {label} current={current?.Name ?? "<none>"} id={current?.Id ?? "<none>"} volume={snapshot.VolumePercent}% muted={snapshot.IsMuted} devices={string.Join(",", snapshot.Devices.Select(device => $"{device.Id}:{device.Name}"))}{Environment.NewLine}");
    }
}
