using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Heimdall.Ui;

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

    /// <summary>
    /// What the desktop itself is set to, when it says. Null means it did not,
    /// and this application's own default applies. Set once from the theme
    /// palette, and again whenever Plasma's settings change.
    /// </summary>
    public static bool? SystemSingleClick { get; set; }

    static DoubleClick()
    {
        CommandProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            // Detached and reattached rather than accumulating: templates reuse
            // their controls, so subscribing on every change would fire the
            // command once per rebind.
            control.DoubleTapped -= OnDoubleTapped;
            control.Tapped -= OnTapped;

            if (args.NewValue is ICommand)
            {
                // BOTH are subscribed, always, and the preference is read at
                // gesture time inside the handlers. Subscribing conditionally
                // would mean re-attaching every realized control when the
                // setting changed — and the controls live inside templates,
                // so there is no list of them to walk.
                control.DoubleTapped += OnDoubleTapped;
                control.Tapped += OnTapped;
            }
        });
    }

    /// <summary>
    /// Single click when the preference says so, or when it defers to a desktop
    /// that says so. Anything else, and this application's long-standing
    /// double-click behaviour stands.
    /// </summary>
    private static bool OpensOnSingleClick => Settings.AppSettings.Current.Navigation.OpenItemsWith
        switch
        {
            Core.Settings.ActivationClick.Single => true,
            Core.Settings.ActivationClick.Double => false,
            _ => SystemSingleClick ?? false,
        };

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (!OpensOnSingleClick) return;

        Run(sender, e);
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Otherwise the second click of a double re-runs what the first already
        // did — harmless for navigation, but it would launch an application
        // twice.
        if (OpensOnSingleClick) return;

        Run(sender, e);
    }

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
