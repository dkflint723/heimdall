using Avalonia.Controls;
using Rove.Ui.ViewModels;

namespace Rove.Ui;

public partial class ShareWindow : Window
{
    public ShareWindow()
    {
        InitializeComponent();
    }

    public ShareWindow(ShareRequestViewModel model) : this()
    {
        DataContext = model;

        model.Finished += (_, _) => Close();

        // Double-click descends. Handled in code-behind rather than through a
        // behaviour package, since it is three lines and one more dependency
        // is not worth it.
        FolderList.DoubleTapped += (_, _) =>
        {
            if (FolderList.SelectedItem is string name) model.EnterCommand.Execute(name);
        };

        // No platform folder picker here. Avalonia's is documented to fail or
        // hang when opened from a window shown with ShowDialog on Linux
        // (AvaloniaUI/Avalonia#10998, #6589), and this window is exactly that.
        // The dialog browses using Rove's own directory listing instead, which
        // is the one thing a file manager is guaranteed to be good at.
    }
}
