using Avalonia.Controls;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

public partial class BatchRenameWindow : Window
{
    public BatchRenameWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);
    }

    public BatchRenameWindow(BatchRenameViewModel model) : this()
    {
        DataContext = model;
        model.Finished += (_, _) => Close();
    }
}
