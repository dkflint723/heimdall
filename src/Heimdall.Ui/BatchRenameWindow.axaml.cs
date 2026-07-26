using Avalonia.Controls;
using Rove.Ui.ViewModels;

namespace Rove.Ui;

public partial class BatchRenameWindow : Window
{
    public BatchRenameWindow()
    {
        InitializeComponent();
    }

    public BatchRenameWindow(BatchRenameViewModel model) : this()
    {
        DataContext = model;
        model.Finished += (_, _) => Close();
    }
}
