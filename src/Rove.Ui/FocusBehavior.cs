using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Rove.Ui;

/// <summary>
/// Focuses a control when it becomes visible.
///
/// Needed because per-pane controls live inside a DataTemplate, so there is no
/// generated field to call Focus() on from code-behind — the same reason the
/// path box handles Enter through a command rather than a KeyDown handler.
/// </summary>
public static class FocusBehavior
{
    public static readonly AttachedProperty<bool> FocusOnVisibleProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FocusOnVisible", typeof(FocusBehavior));

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Hooked", typeof(FocusBehavior));

    public static void SetFocusOnVisible(Control target, bool value)
        => target.SetValue(FocusOnVisibleProperty, value);

    public static bool GetFocusOnVisible(Control target)
        => target.GetValue(FocusOnVisibleProperty);

    static FocusBehavior()
    {
        FocusOnVisibleProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            if (args.NewValue is not true) return;
            if (control.GetValue(HookedProperty)) return;

            control.SetValue(HookedProperty, true);

            // Plain property-changed rather than an observable: Avalonia's
            // Subscribe(Action<T>) lives behind its own reactive extensions,
            // and this needs no extra dependency to say the same thing.
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property != Visual.IsVisibleProperty) return;
                if (e.NewValue is not true) return;

                // Posted: at the instant visibility flips the control is not
                // yet laid out, and focusing an unrealized control silently
                // does nothing.
                Dispatcher.UIThread.Post(() => control.Focus());
            };
        });
    }
}
