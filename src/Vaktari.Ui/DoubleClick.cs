using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Vaktari.Ui;

/// <summary>
/// Runs a command on double-click.
///
/// Avalonia has no command binding for DoubleTapped, and the controls that need
/// it here live inside a DataTemplate, so there is no generated field for
/// code-behind to attach to — the same reason the path box handles Enter
/// through a KeyBinding rather than a handler.
/// </summary>
public static class DoubleClick
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(DoubleClick));

    public static void SetCommand(Control control, ICommand? value)
        => control.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(Control control) => control.GetValue(CommandProperty);

    static DoubleClick()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            // Detached and reattached rather than accumulating: templates reuse
            // their controls, so subscribing on every change would fire the
            // command once per rebind.
            control.DoubleTapped -= OnDoubleTapped;

            if (args.NewValue is ICommand) control.DoubleTapped += OnDoubleTapped;
        });
    }

    /// <summary>
    /// Double-click ONLY, and deliberately not subject to the single-click
    /// preference. This attached property has exactly one user — the path bar's
    /// edit layer — and single-click-to-edit there was tried before and removed:
    /// the first click replaced the crumbs with the box, so the second landed
    /// somewhere else and double-click went flaky. Opening files is a different
    /// path entirely, handled at the window.
    /// </summary>
    private static void OnDoubleTapped(object? sender, TappedEventArgs e) => Run(sender, e);

    private static void Run(object? sender, TappedEventArgs e)
    {
        if (sender is not Control control) return;
        if (GetCommand(control) is not { } command) return;
        if (!command.CanExecute(null)) return;

        command.Execute(null);

        // Claimed, so an ancestor does not act on the same gesture.
        e.Handled = true;
    }
}
