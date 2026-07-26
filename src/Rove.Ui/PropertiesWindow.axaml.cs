using Avalonia.Controls;
using Rove.Ui.ViewModels;

namespace Rove.Ui;

public partial class PropertiesWindow : Window
{
    public PropertiesWindow()
    {
        InitializeComponent();
    }

    public PropertiesWindow(PropertiesViewModel model) : this()
    {
        DataContext = model;
        _ = model.LoadAsync();
    }
}
