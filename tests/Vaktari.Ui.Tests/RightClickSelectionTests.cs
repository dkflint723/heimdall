using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **Does a right-click select the row it lands on?**
///
/// The whole of "hide the entries that need a selection" rests on this. One
/// context menu serves both a file row and the empty space below it, so the
/// entries that act on a selection are gated on there being one — and if a
/// right-click on an unselected file does not select it first, that gating
/// hides Open, Copy and Cut at exactly the moment somebody is asking for them.
///
/// Asserted rather than assumed. This is Avalonia's own behaviour, not
/// something this application writes, so it is precisely the kind of thing that
/// is true until a framework upgrade quietly makes it false.
/// </summary>
public sealed class RightClickSelectionTests
{
    private static (Window Window, ListBox List) Build()
    {
        var list = new ListBox
        {
            ItemsSource = new[] { "one", "two", "three" },
            Width = 200,
            Height = 300,
        };

        var window = new Window { Content = list, Width = 300, Height = 400 };
        window.Show();

        // Layout has to have run before an item has a position to click.
        window.Measure(new Size(300, 400));
        window.Arrange(new Rect(0, 0, 300, 400));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        return (window, list);
    }

    private static Point CentreOf(Control container, Visual relativeTo) =>
        container.TranslatePoint(
            new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), relativeTo)
        ?? new Point(0, 0);

    [AvaloniaFact]
    public void A_right_click_selects_the_row_under_the_pointer()
    {
        var (window, list) = Build();

        var container = (Control)list.ContainerFromIndex(1)!;
        var point = CentreOf(container, window);

        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);

        Assert.Equal("two", list.SelectedItem);
    }

    /// <summary>
    /// And the other half: a right-click on empty space below the rows must NOT
    /// invent a selection, or the gating would never hide anything.
    /// </summary>
    [AvaloniaFact]
    public void A_right_click_on_empty_space_selects_nothing()
    {
        var (window, list) = Build();

        window.MouseDown(new Point(100, 280), MouseButton.Right);
        window.MouseUp(new Point(100, 280), MouseButton.Right);

        Assert.Null(list.SelectedItem);
    }
}
