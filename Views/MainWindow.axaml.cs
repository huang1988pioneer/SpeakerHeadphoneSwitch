using Avalonia.Controls;
using Avalonia.Interactivity;
using SpeakerHeadphoneSwitch.ViewModels;

namespace SpeakerHeadphoneSwitch.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var dialog = new SettingsWindow { DataContext = viewModel.CreateSettingsViewModel() };
        if (await dialog.ShowDialog<bool>(this))
            viewModel.ApplySettings();
    }
}
