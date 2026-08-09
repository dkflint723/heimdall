using Avalonia.Controls;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

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
