using Avalonia.Controls;
using Avalonia.Interactivity;
using SpeakerHeadphoneSwitch.ViewModels;

namespace SpeakerHeadphoneSwitch.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel && viewModel.CanSave)
        {
            viewModel.Save();
            Close(true);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
