using Avalonia.Controls;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

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
