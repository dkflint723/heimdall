using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace Heimdall.Ui.Tests;

/// <summary>
/// Rules about the window's markup, checked against the markup itself.
///
/// **Every rule here is a bug that shipped.** Each one looked correct while
/// reading it, which is the point: they are not style preferences, they are
/// three separate cases of Avalonia doing something reasonable that the markup
/// did not account for, and each survived review, a passing test suite, and
/// hands-on use.
///
/// These are structural assertions rather than headless interaction tests, and
/// that is a deliberate trade. Instantiating a row template needs the shell's
/// whole object graph — a platform, a session, a view model per pane — so a
/// test that clicks a real row is an end-to-end test wearing a unit test's
/// clothes. Reading the shape catches the same regressions for a fraction of
/// the machinery, and cannot pass for the wrong reason.
///
/// What it cannot do is notice a NEW way to make a row unclickable. If the row
/// templates are ever restructured, revisit these rather than trusting them.
/// </summary>
public class MarkupRulesTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Markup()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.axaml")
            ?? throw new InvalidOperationException("MainWindow.axaml is not embedded in the test assembly");

        return XDocument.Load(stream, LoadOptions.SetLineInfo);
    }

    private static string Where(XElement e)
        => $"line {((IXmlLineInfo)e).LineNumber} <{e.Name.LocalName}>";

    /// <summary>
    /// **A Panel with no Background is invisible to the pointer.** Avalonia hit
    /// tests against a brush, and null is not one — events pass straight
    /// through to whatever is behind.
    ///
    /// The row templates had none, so the only clickable things in a row were
    /// its TextBlocks. A filename occupies a small part of a row, so most of
    /// every row was dead, and double-clicking to open a folder appeared to be
    /// slow rather than aimed wrong: nothing happened, so you clicked again.
    /// </summary>
    [Fact]
    public void Every_row_template_root_is_hit_testable()
    {
        // The OUTERMOST Panel of each template, not every Panel in it. An inner
        // one — a thumbnail box, an icon cell — needs no background of its own:
        // with none, the pointer falls through to the row behind it, which is
        // the row, which is what should be hit. Requiring it everywhere flagged
        // four correct panels and taught nothing.
        var offenders = Markup()
            .Descendants(Avalonia + "DataTemplate")
            .Where(t => (string?)t.Attribute(X + "DataType") == "fs:FileEntry")
            .SelectMany(t => t.Descendants(Avalonia + "Panel")
                .Where(p => !p.Ancestors(Avalonia + "Panel").Any(a => a.Ancestors().Contains(t) || a == t)))
            .Where(p => p.Attribute("Background") is null)
            .Select(Where)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A row Panel with no Background cannot be clicked except where its text is:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// **Selection decoration must not be a hit target.**
    ///
    /// These borders are bound to IsSelected, so they APPEAR when the first
    /// click of a double-click selects the row. The second click then lands on
    /// a different element, and Avalonia's double-tap gesture requires both
    /// clicks on the same one — so no DoubleTapped was ever raised, and the
    /// fallback that counts two taps lost the second tap along with it.
    ///
    /// The row stayed unopenable no matter how the background was fixed. They
    /// are decoration; they were never meant to be hit.
    /// </summary>
    [Fact]
    public void Selection_decoration_is_not_a_hit_target()
    {
        var offenders = Markup()
            .Descendants()
            .Where(e => ((string?)e.Attribute("IsVisible"))?.Contains("IsSelected", StringComparison.Ordinal) == true)
            .Where(e => (string?)e.Attribute("IsHitTestVisible") != "False")
            .Select(Where)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Decoration that appears on selection changes what the pointer hits between "
            + "the two clicks of a double-click, which stops the gesture forming:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// **TextTrimming inside a horizontal StackPanel never fires.** A
    /// horizontal StackPanel measures its children with infinite width, so a
    /// TextBlock in one is never told it ran out of room. The setting is
    /// present, correct-looking, and inert; the text simply overflows its
    /// parent and is clipped mid-character with no ellipsis.
    ///
    /// Both sidebar rows that show a network location had this, and a network
    /// label — share, host and port — is the longest thing the sidebar shows.
    /// </summary>
    [Fact]
    public void Trimming_text_is_never_inside_a_horizontal_StackPanel()
    {
        // DIRECT children only. A horizontal StackPanel hands ITS children
        // infinite width, but anything between can hand back a real one — a
        // ScrollViewer, a fixed Width, a Grid column. Following the whole
        // subtree flagged a search result inside a scrolling popup, where
        // trimming works correctly. Static reading cannot settle those, so this
        // asserts only the case that is unambiguously inert, which is also both
        // of the ones that shipped.
        var offenders = Markup()
            .Descendants(Avalonia + "StackPanel")
            .Where(s => (string?)s.Attribute("Orientation") == "Horizontal")
            .SelectMany(s => s.Elements(Avalonia + "TextBlock"))
            .Where(t => t.Attribute("TextTrimming") is not null)
            .Select(Where)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A horizontal StackPanel measures children with infinite width, so TextTrimming "
            + "on these can never engage — use a DockPanel and let the text take what is left:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>A guard on the guards: if the resource stops being embedded, or
    /// the file moves, every rule above would pass against nothing.</summary>
    [Fact]
    public void The_markup_under_test_is_actually_present()
    {
        var doc = Markup();

        Assert.Equal("Window", doc.Root?.Name.LocalName);
        Assert.True(doc.Descendants(Avalonia + "DataTemplate")
            .Count(t => (string?)t.Attribute(X + "DataType") == "fs:FileEntry") >= 4,
            "expected the four FileEntry row templates — details, compact, grid and the simple list");
    }
}
