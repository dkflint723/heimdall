using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Junctions, which look exactly like folders to a recursive walk and are not.
///
/// **The bug.** `BuildPlan` enumerated with `SearchOption.AllDirectories`, whose
/// options set `AttributesToSkip = 0`, so a junction was descended into like any
/// other directory. Three things follow, in increasing order of harm: a copy
/// silently duplicates whatever tree the junction points at; a move then deletes
/// the originals out of that tree, which the user never selected and may not
/// even know is involved; and a junction resolving back to an ancestor makes the
/// walk run until the path length stops it.
///
/// The two other recursive walks in this assembly already refused to follow
/// them — `WindowsSearchProvider` skips the attribute outright, and
/// `WindowsPropertiesProvider` notes that "a junction can point at an ancestor,
/// and following one turns a measurement into a loop". This was the walk that
/// did not.
/// </summary>
[SupportedOSPlatform("windows")]
public class ReparsePointTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Overwrite
        => _ => ValueTask.FromResult(ConflictResolution.Overwrite);

    private static async Task<IOperationHandle> Finished(IOperationHandle handle)
    {
        // Generous, but bounded: the failure this guards against is a walk that
        // does not terminate, and a hung test run reports nothing useful.
        var done = await Task.WhenAny(handle.Completion, Task.Delay(TimeSpan.FromSeconds(60)));
        Assert.True(done == handle.Completion, "the operation did not finish — the walk is still recursing");

        Assert.Null(handle.Error);
        Assert.Equal(OperationState.Completed, handle.State);
        return handle;
    }

    [WindowsFact]
    public async Task A_copy_does_not_recurse_through_a_junction_that_points_at_its_own_parent()
    {
        using var tree = new TempTree();
        var source = tree.Dir("tree");
        tree.Write("tree/real.txt", "content");
        tree.Junction("tree/loop", source);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Copy([source], tree.At("dst"), Overwrite));

        Assert.Equal("content", tree.Read("dst", "tree", "real.txt"));
    }

    /// <summary>
    /// Reproduced rather than followed — and reproduced as a junction, because
    /// `Directory.CreateSymbolicLink` needs Developer Mode and would make this
    /// fail on a machine in its default configuration.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_is_copied_as_a_junction()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        var source = tree.Dir("tree");
        tree.Junction("tree/link", outside);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Copy([source], tree.At("dst"), Overwrite));

        var landed = new DirectoryInfo(tree.At("dst", "tree", "link"));
        Assert.True(landed.Exists);
        Assert.Equal(outside, landed.LinkTarget);
    }

    /// <summary>
    /// The destructive one. The junction's contents were copied to the
    /// destination and then deleted from the source side — which, through a
    /// junction, is a completely different tree on disk.
    /// </summary>
    [WindowsFact]
    public async Task A_move_does_not_delete_through_a_junction()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        tree.Write("outside/nested/also-kept.txt", "elsewhere too");
        var source = tree.Dir("tree");
        tree.Write("tree/real.txt", "mine");
        tree.Junction("tree/link", outside);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Move([source], tree.At("dst"), Overwrite));

        Assert.Equal("elsewhere", tree.Read("outside", "kept.txt"));
        Assert.Equal("elsewhere too", tree.Read("outside", "nested", "also-kept.txt"));
    }

    /// <summary>The link itself goes, since the user did ask to move it.</summary>
    [WindowsFact]
    public async Task A_moved_junction_leaves_the_source_side_clean()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt");
        var source = tree.Dir("tree");
        tree.Write("tree/real.txt");
        tree.Junction("tree/link", outside);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Move([source], tree.At("dst"), Overwrite));

        Assert.False(tree.Exists("tree"));
        Assert.Equal(outside, new DirectoryInfo(tree.At("dst", "tree", "link")).LinkTarget);
    }

    /// <summary>
    /// A junction the user selected directly, rather than one found inside a
    /// folder. `Directory.Exists` answers true for it, so it has to be tested
    /// for before the folder branch or it is walked as a folder.
    /// </summary>
    [WindowsFact]
    public async Task A_junction_selected_on_its_own_is_copied_as_a_link()
    {
        using var tree = new TempTree();
        var outside = tree.Dir("outside");
        tree.Write("outside/kept.txt", "elsewhere");
        var link = tree.Junction("link", outside);
        tree.Dir("dst");

        await Finished(new WindowsFileOperations().Copy([link], tree.At("dst"), Overwrite));

        Assert.Equal(outside, new DirectoryInfo(tree.At("dst", "link")).LinkTarget);
        Assert.Equal(["kept.txt"], tree.Names("outside"));
    }
}
