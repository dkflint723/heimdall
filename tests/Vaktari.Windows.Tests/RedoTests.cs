using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Putting back what an undo took away.
///
/// **Ctrl+Z existed and Ctrl+Y did not**, which is half an undo: a move
/// reversed by mistake could only be performed again by hand.
///
/// Against the real engine on real files, like the rest of these — the whole
/// risk of a redo is that it acts on a disk that has moved on since, and a fake
/// filesystem cannot show that.
/// </summary>
[SupportedOSPlatform("windows")]
public class RedoTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    private static async Task<IOperationHandle> Finished(IOperationHandle handle)
    {
        await handle.Completion;
        Assert.Null(handle.Error);
        return handle;
    }

    [Fact]
    public async Task A_move_can_be_undone_and_done_again()
    {
        using var tree = new TempTree();
        tree.Write("src/note.txt");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();

        await Finished(ops.Move(
            [tree.At("src", "note.txt")], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        Assert.True(tree.Exists("dst", "note.txt"));
        Assert.False(ops.CanRedo);

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(tree.Exists("src", "note.txt"));
        Assert.False(tree.Exists("dst", "note.txt"));
        Assert.True(ops.CanRedo);

        await ops.RedoAsync(CancellationToken.None);

        Assert.True(tree.Exists("dst", "note.txt"));
        Assert.False(tree.Exists("src", "note.txt"));

        // And the redo is itself undoable, so the two can be walked back and
        // forth rather than the history ending after one step each way.
        Assert.True(ops.CanUndo);

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(tree.Exists("src", "note.txt"));
    }

    [Fact]
    public async Task A_rename_can_be_undone_and_done_again()
    {
        using var tree = new TempTree();
        tree.Write("note.txt");

        var ops = new WindowsFileOperations();

        await ops.RenameAsync(tree.At("note.txt"), "renamed.txt", CancellationToken.None);

        Assert.True(tree.Exists("renamed.txt"));

        await ops.UndoAsync(CancellationToken.None);
        Assert.True(tree.Exists("note.txt"));

        await ops.RedoAsync(CancellationToken.None);
        Assert.True(tree.Exists("renamed.txt"));
        Assert.False(tree.Exists("note.txt"));
    }

    /// <summary>
    /// **Any new work abandons the redo.** Once the history has been departed
    /// from, putting something back would apply to a state that no longer
    /// exists — here, a redo would move a file that has since been renamed.
    /// </summary>
    [Fact]
    public async Task New_work_abandons_what_could_have_been_redone()
    {
        using var tree = new TempTree();
        tree.Write("src/note.txt");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();

        await Finished(ops.Move(
            [tree.At("src", "note.txt")], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(ops.CanRedo);

        await ops.RenameAsync(tree.At("src", "note.txt"), "other.txt", CancellationToken.None);

        Assert.False(ops.CanRedo);
    }

    /// <summary>
    /// Nothing to put back is not a failure, and must not throw from a
    /// keystroke somebody pressed hopefully.
    /// </summary>
    [Fact]
    public async Task Redoing_nothing_does_nothing()
    {
        var ops = new WindowsFileOperations();

        Assert.False(ops.CanRedo);

        await ops.RedoAsync(CancellationToken.None);
    }

    /// <summary>
    /// **A file that has gone since is not moved back onto.** The undo records
    /// where things landed; if one of them has since been deleted by hand, the
    /// redo skips it rather than failing the lot.
    /// </summary>
    [Fact]
    public async Task A_file_deleted_in_the_meantime_is_skipped()
    {
        using var tree = new TempTree();
        tree.Write("src/one.txt");
        tree.Write("src/two.txt");
        tree.Dir("dst");

        var ops = new WindowsFileOperations();

        await Finished(ops.Move(
            [tree.At("src", "one.txt"), tree.At("src", "two.txt")],
            tree.At("dst"), Always(ConflictResolution.Overwrite)));

        await ops.UndoAsync(CancellationToken.None);

        File.Delete(tree.At("src", "one.txt"));

        await ops.RedoAsync(CancellationToken.None);

        Assert.True(tree.Exists("dst", "two.txt"));
        Assert.False(tree.Exists("dst", "one.txt"));
    }
}
