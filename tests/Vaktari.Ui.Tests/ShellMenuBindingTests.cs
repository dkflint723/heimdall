using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Handing the desktop's own menu to the interface.
///
/// The reading half is tested against the real shell in Vaktari.Windows.Tests,
/// where the entries come from whatever is installed. This is the other half:
/// what the view model does with them, which is where the lifetime rules live
/// and where getting it wrong is silent.
///
/// **The ids are offsets into one live menu.** Releasing it while the user is
/// still looking leaves every row pointing at nothing; never releasing leaks an
/// apartment thread per right-click. Both failures look like nothing at all
/// from the outside, which is why they are pinned here.
/// </summary>
public sealed class ShellMenuBindingTests : IDisposable
{
    private sealed class FakeShellMenu : IShellMenu
    {
        public FakeShellMenu(IReadOnlyList<ShellMenuEntry> entries) => Entries = entries;

        public IReadOnlyList<ShellMenuEntry> Entries { get; }
        public bool Disposed { get; private set; }
        public List<int> Invoked { get; } = [];

        public void Invoke(int id) => Invoked.Add(id);
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeProvider : IShellMenuProvider
    {
        public FakeShellMenu? Last { get; private set; }
        public int Builds { get; private set; }
        public IReadOnlyList<string> AskedFor { get; private set; } = [];

        public IShellMenu? Build(IReadOnlyList<string> paths)
        {
            Builds++;
            AskedFor = paths;

            return Last = new FakeShellMenu(
            [
                new ShellMenuEntry("Open", 1),
                new ShellMenuEntry("", 0, IsSeparator: true),
                new ShellMenuEntry("7-Zip", 2, Children:
                [
                    new ShellMenuEntry("Add to archive…", 3),
                ]),
                new ShellMenuEntry("Scan", 4, IsEnabled: false),
            ]);
        }
    }

    private sealed class InertFileSystem : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private readonly FakeProvider _provider = new();

    public ShellMenuBindingTests() => PaneViewModel.ShellMenu = _provider;

    public void Dispose() => PaneViewModel.ShellMenu = null;

    private static PaneViewModel Pane() =>
        new(new InertFileSystem()) { CurrentPath = Path.GetTempPath() };

    [AvaloniaFact]
    public async Task Opening_it_fills_the_menu()
    {
        var pane = Pane();

        await pane.OpenShellMenuAsync();

        Assert.NotEmpty(pane.ShellMenuItems);
    }

    /// <summary>
    /// **Separators arrive as real Separator controls.** Avalonia uses a Control
    /// in an ItemsSource as its own container, and that is the only way a
    /// data-driven menu draws a rule — the shell's menu leans on them heavily
    /// enough that dropping them turns twenty rows into one undifferentiated
    /// column.
    /// </summary>
    [AvaloniaFact]
    public async Task A_rule_becomes_something_the_menu_can_draw()
    {
        var pane = Pane();

        await pane.OpenShellMenuAsync();

        Assert.Contains(pane.ShellMenuItems, item => item is Separator);
        Assert.Contains(pane.ShellMenuItems, item => item is ShellMenuEntry { Label: "7-Zip" });
    }

    /// <summary>
    /// Building runs every shell extension on the machine, so it happens when
    /// the submenu opens and never as part of an ordinary right-click.
    /// </summary>
    [AvaloniaFact]
    public async Task Nothing_is_built_until_the_submenu_opens()
    {
        var pane = Pane();

        // The placeholder is there from the start; the shell has not been asked.
        Assert.Single(pane.ShellMenuItems);
        Assert.Equal(0, _provider.Builds);

        await pane.OpenShellMenuAsync();

        Assert.Equal(1, _provider.Builds);
    }

    [AvaloniaFact]
    public async Task Closing_it_releases_the_shell_objects()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();
        var built = _provider.Last!;

        pane.CloseShellMenu();

        Assert.True(built.Disposed);

        // Back to the placeholder rather than empty: Avalonia refuses to open a
        // submenu with no items and draws no chevron for one, so an emptied
        // list would make "More options" unopenable ever again.
        Assert.Single(pane.ShellMenuItems);
        Assert.Contains(pane.ShellMenuItems,
            i => i is ShellMenuEntry { IsEnabled: false });
    }

    /// <summary>
    /// Opening twice must not strand the first one: each menu owns an apartment
    /// thread, so a leak here is a thread per right-click.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_again_releases_the_one_before()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();
        var first = _provider.Last!;

        await pane.OpenShellMenuAsync();

        Assert.True(first.Disposed);
        Assert.False(_provider.Last!.Disposed);
    }

    [AvaloniaFact]
    public async Task Choosing_an_entry_invokes_its_id()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();

        pane.InvokeShellEntry(new ShellMenuEntry("Open", 1));

        Assert.Equal([1], _provider.Last!.Invoked);
    }

    /// <summary>
    /// A parent row exists to open its children. Invoking it would ask the
    /// handler to run a command it never issued — and what a third party's
    /// handler does with an id it did not hand out is not knowable from here.
    /// </summary>
    [AvaloniaFact]
    public async Task A_row_that_only_opens_a_submenu_invokes_nothing()
    {
        var pane = Pane();
        await pane.OpenShellMenuAsync();

        pane.InvokeShellEntry(pane.ShellMenuItems
            .OfType<ShellMenuEntry>()
            .First(e => e.HasChildren));

        Assert.Empty(_provider.Last!.Invoked);
    }

    /// <summary>
    /// With nothing selected the click was on empty space, so the folder itself
    /// is what the shell should be asked about — the same rule the rest of the
    /// menu follows.
    /// </summary>
    [AvaloniaFact]
    public async Task With_no_selection_the_folder_is_what_gets_asked_about()
    {
        var pane = Pane();

        await pane.OpenShellMenuAsync();

        Assert.Equal([Path.GetTempPath()], _provider.AskedFor);
    }

    /// <summary>The bin and Recent hold rows whose paths are not where the file
    /// is now, which is the hazard the whole listing already guards.</summary>
    [AvaloniaFact]
    public async Task The_bin_and_recent_offer_no_shell_menu()
    {
        Assert.False(new PaneViewModel(new InertFileSystem())
            { CurrentPath = VirtualPaths.Trash }.HasShellMenu);

        Assert.False(new PaneViewModel(new InertFileSystem())
            { CurrentPath = VirtualPaths.Files }.HasShellMenu);

        Assert.True(Pane().HasShellMenu);
    }
}
