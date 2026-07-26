using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Heimdall.Ui;

/// <summary>
/// Scrolls a horizontal ScrollViewer to its end when the bound value changes.
///
/// Used by the Miller chain: descending adds a column past the right edge, and
/// without this the new column is off-screen and the click appears to have done
/// nothing. Attached rather than code-behind because the ScrollViewer lives
/// inside a template and has no generated field.
/// </summary>
public static class ScrollIntoView
{
    public static readonly AttachedProperty<object?> TargetProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, object?>("Target", typeof(ScrollIntoView));

    public static void SetTarget(ScrollViewer viewer, object? value)
        => viewer.SetValue(TargetProperty, value);

    public static object? GetTarget(ScrollViewer viewer) => viewer.GetValue(TargetProperty);

    static ScrollIntoView()
    {
        TargetProperty.Changed.AddClassHandler<ScrollViewer>((viewer, args) =>
        {
            if (args.NewValue is null) return;

            // Posted: the column that caused this has not been measured yet, so
            // scrolling now would target the old extent.
            Dispatcher.UIThread.Post(
                () => viewer.ScrollToEnd(), DispatcherPriority.Background);
        });
    }
}
