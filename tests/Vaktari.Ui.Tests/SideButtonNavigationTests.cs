using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Vaktari.Ui.Input;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The two buttons under the thumb, which navigate back and forward.
///
/// Two separate things are pinned here, and the second is the one that would
/// actually break.
///
/// **Which button means what.** Getting the pair the wrong way round produces
/// an application that works perfectly and feels wrong, and nothing in a build
/// would say a word. The nearer button is back, everywhere.
///
/// **That the press reaches us at all.** A side button has to arrive as
/// PointerUpdateKind.XButton1Pressed on the tunnel, before a listing treats the
/// press as a selection gesture. That is Avalonia's behaviour rather than this
/// application's — precisely the kind of thing that is true until a framework
/// upgrade quietly makes it false, which is the same reason
/// <see cref="RightClickSelectionTests"/> exists.
/// </summary>
public sealed class SideButtonNavigationTests
{
    [Theory]
    [InlineData(PointerUpdateKind.XButton1Pressed, SideButtonAction.Back)]
    [InlineData(PointerUpdateKind.XButton2Pressed, SideButtonAction.Forward)]
    [InlineData(PointerUpdateKind.LeftButtonPressed, SideButtonAction.None)]
    [InlineData(PointerUpdateKind.MiddleButtonPressed, SideButtonAction.None)]
    [InlineData(PointerUpdateKind.RightButtonPressed, SideButtonAction.None)]
    public void The_nearer_button_goes_back(PointerUpdateKind kind, SideButtonAction expected)
    {
        Assert.Equal(expected, SideButtons.For(kind));
    }

    /// <summary>
    /// Built the same way <see cref="RightClickSelectionTests"/> builds its
    /// window, and for the same reason: a real listing under the pointer is the
    /// thing that would otherwise swallow the press.
    /// </summary>
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

        window.Measure(new Size(300, 400));
        window.Arrange(new Rect(0, 0, 300, 400));

        return (window, list);
    }

    [AvaloniaTheory]
    [InlineData(MouseButton.XButton1, SideButtonAction.Back)]
    [InlineData(MouseButton.XButton2, SideButtonAction.Forward)]
    public void A_side_button_arrives_on_the_tunnel_as_a_navigation(
        MouseButton button, SideButtonAction expected)
    {
        var (window, list) = Build();

        var seen = SideButtonAction.None;

        // The tunnel, which is where the real handler sits — a listing treats a
        // press as a selection gesture, so seeing it first is what keeps a side
        // button from moving the selection as well as the folder.
        window.AddHandler(
            InputElement.PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) =>
            {
                seen = SideButtons.For(e.GetCurrentPoint(window).Properties.PointerUpdateKind);

                if (seen is not SideButtonAction.None) e.Handled = true;
            },
            RoutingStrategies.Tunnel);

        var container = (Control)list.ContainerFromIndex(1)!;
        var point = container.TranslatePoint(
            new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)
            ?? new Point(0, 0);

        window.MouseDown(point, button);
        window.MouseUp(point, button);

        Assert.Equal(expected, seen);

        // And the row it landed on is not selected: handling it on the way down
        // is what stops a navigation button from also moving the selection.
        Assert.Null(list.SelectedItem);

        window.Close();
    }

    /// <summary>An ordinary click still selects, so the guard above is not
    /// swallowing everything that reaches it.</summary>
    [AvaloniaFact]
    public void An_ordinary_click_is_left_alone()
    {
        var (window, list) = Build();

        window.AddHandler(
            InputElement.PointerPressedEvent,
            (object? _, PointerPressedEventArgs e) =>
            {
                if (SideButtons.For(e.GetCurrentPoint(window).Properties.PointerUpdateKind)
                    is not SideButtonAction.None)
                    e.Handled = true;
            },
            RoutingStrategies.Tunnel);

        var container = (Control)list.ContainerFromIndex(1)!;
        var point = container.TranslatePoint(
            new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)
            ?? new Point(0, 0);

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Assert.Equal("two", list.SelectedItem);

        window.Close();
    }
}
