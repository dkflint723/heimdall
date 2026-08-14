using System.Runtime.Versioning;
using Vaktari.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Reading the machine's own context menu.
///
/// **These assert against whatever is installed on the machine running them**,
/// which is unusual here and is the point: the entries under test are supplied
/// by third-party shell extensions, so a fixture proving we can read a menu we
/// built ourselves would prove nothing about the thing that actually breaks.
/// What is asserted is therefore structural — that the shell answered, that the
/// rows have text and ids, that submenus came back — rather than "7-Zip is
/// present", which would fail on a machine without it.
///
/// The one exception is the diagnostic test, which writes what it found to the
/// test output so the list can be read by eye. That is how this was verified in
/// the first place.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellContextMenuTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _folder;
    private readonly string _file;

    public ShellContextMenuTests(ITestOutputHelper output)
    {
        _output = output;

        // Its own directory, made here and removed after: the shell is being
        // asked about these paths and a handler may well look at them.
        _folder = Path.Combine(Path.GetTempPath(), "vaktari-shellmenu-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_folder);

        _file = Path.Combine(_folder, "sample.txt");
        File.WriteAllText(_file, "sample");
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    [Fact]
    public void The_shell_offers_a_menu_for_a_file()
    {
        using var menu = ShellContextMenu.For([_file]);

        Assert.NotNull(menu);
        Assert.NotEmpty(menu!.Entries);
    }

    [Fact]
    public void The_shell_offers_a_menu_for_a_folder()
    {
        using var menu = ShellContextMenu.For([_folder]);

        Assert.NotNull(menu);
        Assert.NotEmpty(menu!.Entries);
    }

    /// <summary>
    /// Every row is usable: a label to draw and an id to invoke. A blank row or
    /// a negative id would be an entry the user can see and click to no effect,
    /// which is the failure mode this whole feature has to avoid.
    /// </summary>
    [Fact]
    public void Every_entry_has_something_to_draw_and_something_to_invoke()
    {
        using var menu = ShellContextMenu.For([_file]);
        Assert.NotNull(menu);

        foreach (var entry in menu!.Entries.Where(e => !e.IsSeparator))
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Label), "an entry had no text");
            Assert.True(entry.Id >= 0, $"'{entry.Label}' had id {entry.Id}");
        }
    }

    /// <summary>
    /// The shell's menu is built to be merged into another one, so it happily
    /// starts or ends with a rule and puts two together. Avalonia draws every
    /// separator it is given and collapses none — a defect this project already
    /// shipped once in its own menu, and there is no reason to ship it again
    /// with somebody else's.
    /// </summary>
    [Fact]
    public void No_rule_is_left_drawing_against_nothing()
    {
        using var menu = ShellContextMenu.For([_file]);
        Assert.NotNull(menu);

        var entries = menu!.Entries;

        Assert.False(entries.Count > 0 && entries[0].IsSeparator, "leads with a rule");
        Assert.False(entries.Count > 0 && entries[^1].IsSeparator, "ends with a rule");

        for (var i = 1; i < entries.Count; i++)
            Assert.False(entries[i].IsSeparator && entries[i - 1].IsSeparator, $"two rules at {i}");
    }

    /// <summary>
    /// Ampersands are the shell's accelerator marks, not text. Left in, every
    /// other label would read "Cu&amp;t" — and this is the sort of thing that is
    /// obvious in a screenshot and invisible in a passing test.
    /// </summary>
    [Fact]
    public void Accelerator_marks_are_not_shown_as_text()
    {
        using var menu = ShellContextMenu.For([_file]);
        Assert.NotNull(menu);

        Assert.All(menu!.Entries, e => Assert.DoesNotContain('&', e.Label));
    }

    /// <summary>
    /// A selection of several must reach the handlers as several, or "add to
    /// archive" on eight files quietly makes an archive of one.
    /// </summary>
    [Fact]
    public void A_selection_of_several_still_produces_a_menu()
    {
        var second = Path.Combine(_folder, "another.txt");
        File.WriteAllText(second, "another");

        using var menu = ShellContextMenu.For([_file, second]);

        Assert.NotNull(menu);
        Assert.NotEmpty(menu!.Entries);
    }

    [Fact]
    public void Nothing_selected_means_no_menu()
    {
        Assert.Null(ShellContextMenu.For([]));
    }

    /// <summary>
    /// Not an assertion — a readout. Run with the test output visible to see
    /// exactly what this machine's shell hands back, which is how the feature
    /// was verified and how it should be checked again after any change.
    /// </summary>
    [Fact]
    public void What_this_machine_offers()
    {
        using var menu = ShellContextMenu.For([_file]);
        Assert.NotNull(menu);

        foreach (var entry in menu!.Entries)
        {
            _output.WriteLine(entry.IsSeparator
                ? "  ---"
                : $"  [{entry.Id,3}] {entry.Label}{(entry.HasChildren ? " >" : "")}");

            foreach (var child in entry.Items)
                _output.WriteLine($"          [{child.Id,3}] {child.Label}");
        }
    }
}
