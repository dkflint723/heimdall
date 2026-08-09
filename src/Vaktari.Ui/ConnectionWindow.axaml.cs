using Avalonia.Controls;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

public partial class ConnectionWindow : Window
{
    public ConnectionWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
    }

    public ConnectionWindow(ConnectionInfoViewModel model) : this()
    {
        DataContext = model;
        model.Finished += (_, _) => Close();
    }
}
