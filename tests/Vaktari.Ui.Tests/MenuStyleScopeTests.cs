using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Why the menu styles are anchored on a name.
///
/// **A style applies to the control it is declared on, not only to that
/// control's descendants.** A `MenuItem &gt; MenuItem` selector written inside
/// a MenuItem's own Styles, to reach the item containers it generates, also
/// matches that MenuItem itself the moment it sits inside another one.
///
/// In the real menu the setter is a Command typed to the submenu's item and a
/// CommandParameter bound to the DataContext, so the host ended up holding a
/// command that expected a NewFileKind and a parameter that was the pane
/// group. Avalonia calls CanExecute on every child when a submenu opens, so
/// clicking "New" killed the process. It shipped in 0.7.0.
///
/// MarkupRulesTests keeps the real markup anchored; this keeps the reason
/// honest, by pinning the framework behaviour the anchoring exists to dodge.
/// If a future Avalonia stops matching the host, this test fails and the
/// anchoring can go.
/// </summary>
public sealed class MenuStyleScopeTests
{
    [AvaloniaFact]
    public void An_anchored_style_reaches_the_items_but_not_their_host()
    {
        var host = new MenuItem { Name = "Host", Header = "New file" };
        host.ItemsSource = new[] { "a", "b" };

        host.Styles.Add(new Style(x => x.OfType<MenuItem>().Name("Host").Child().OfType<MenuItem>())
        {
            Setters = { new Setter(Control.TagProperty, "matched") },
        });

        var parent = new MenuItem { Name = "Parent", Header = "New" };
        parent.Items.Add(host);

        var menu = new ContextMenu();
        menu.Items.Add(parent);

        var target = new Border { ContextMenu = menu };
        var window = new Window { Width = 400, Height = 300, Content = target };
        window.Show();
        window.Measure(new Avalonia.Size(400, 300));
        window.Arrange(new Avalonia.Rect(0, 0, 400, 300));

        menu.Open(target);
        parent.Open();

        window.Measure(new Avalonia.Size(400, 300));
        window.Arrange(new Avalonia.Rect(0, 0, 400, 300));

        // If this is "matched", the style set a property on the HOST — which in
        // the real menu means the host gets a Command expecting one of its own
        // items and a CommandParameter that is the pane group, and Avalonia
        // calls CanExecute on it the moment the parent submenu opens.
        Assert.Null(host.Tag);

        // And it must still reach the generated items, or the fix is "matches
        // nothing" and every entry in the submenu quietly loses its command.
        // The containers do not exist until the host's own submenu opens.
        host.Open();
        window.Measure(new Avalonia.Size(400, 300));
        window.Arrange(new Avalonia.Rect(0, 0, 400, 300));
        var first = (MenuItem)host.ContainerFromIndex(0)!;
        Assert.Equal("matched", first.Tag);

        menu.Close();
        window.Close();
    }
}
