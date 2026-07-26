using Avalonia.Controls;
using Heimdall.Ui.ViewModels;

namespace Heimdall.Ui;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
    }

    public PropertiesWindow(PropertiesViewModel model) : this()
    {
        DataContext = model;
        _ = model.LoadAsync();
    }
}
