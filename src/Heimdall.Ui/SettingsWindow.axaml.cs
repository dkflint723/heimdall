using Avalonia.Controls;
using Heimdall.Ui.ViewModels;

namespace Heimdall.Ui;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
    }

    public SettingsWindow(SettingsViewModel model) : this()
    {
        DataContext = model;
        model.CloseRequested += (_, _) => Close();
    }
}
