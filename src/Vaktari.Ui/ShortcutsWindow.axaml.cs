using Avalonia.Controls;
using Avalonia.Interactivity;
using Vaktari.Ui.ViewModels;

namespace Vaktari.Ui;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);

        // The list itself rather than a view model: it is a constant, and
        // wrapping a constant in an observable object would say it changes.
        Groups.ItemsSource = Shortcuts.All;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
