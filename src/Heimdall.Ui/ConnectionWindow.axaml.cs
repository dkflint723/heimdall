using Avalonia.Controls;
using Rove.Ui.ViewModels;

namespace Rove.Ui;

public partial class ConnectionWindow : Window
{
    public ConnectionWindow()
    {
        InitializeComponent();
    }

    public ConnectionWindow(ConnectionInfoViewModel model) : this()
    {
        DataContext = model;
        model.Finished += (_, _) => Close();
    }
}
