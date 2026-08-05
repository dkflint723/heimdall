using System.Runtime.Versioning;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Tests;
using Xunit;

namespace Heimdall.Windows.Tests;

/// <summary>
/// Tags are keyed by path on Windows, so every operation that changes a path has
/// to tell the index — and tell it the truth.
///
/// **The bug.** After a move, the index was updated once per *selected source*,
/// from `source` to `destination + name`, and `Retarget` follows everything
/// beneath a folder by prefix. Neither of those knows which items inside that
/// folder were actually moved. Resolve one file's conflict with Skip and its tag
/// was rewritten to a destination path with no such file at it, while the file
/// the user chose to keep — still sitting at the source — came back untagged. A
/// tag silently attaching itself to a stranger's file is the same defect seen
/// from the other end.
/// </summary>
[SupportedOSPlatform("windows")]
public class TagRetargetTests
{
    private static Func<string, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    private static async Task Finished(IOperationHandle handle)
    {
        await handle.Completion;
        Assert.Null(handle.Error);
        Assert.Equal(OperationState.Completed, handle.State);
    }

    private static (WindowsTagStore Tags, WindowsFileOperations Ops) Store(TempTree tree)
    {
        var tags = new WindowsTagStore(tree.Dir("state"));
        return (tags, new WindowsFileOperations(tags));
    }

    private static async Task<IReadOnlyList<string>> TagsOn(WindowsTagStore tags, string path)
        => await tags.GetAsync(path, CancellationToken.None);

    [WindowsFact]
    public async Task A_moved_files_tags_follow_it()
    {
        using var tree = new TempTree();
        var (tags, ops) = Store(tree);

        tree.Write("docs/b.txt");
        await tags.SetAsync(tree.At("docs", "b.txt"), ["final"], CancellationToken.None);
        tree.Dir("dst");

        await Finished(ops.Move([tree.At("docs")], tree.At("dst"), Always(ConflictResolution.Overwrite)));

        Assert.Equal(["final"], await TagsOn(tags, tree.At("dst", "docs", "b.txt")));
    }

    [WindowsFact]
    public async Task A_skipped_file_keeps_its_tags_where_it_is()
    {
        using var tree = new TempTree();
        var (tags, ops) = Store(tree);

        tree.Write("docs/a.txt", "mine");
        tree.Write("docs/b.txt", "mine");
        await tags.SetAsync(tree.At("docs", "a.txt"), ["draft"], CancellationToken.None);
        await tags.SetAsync(tree.At("docs", "b.txt"), ["final"], CancellationToken.None);

        tree.Write("dst/docs/a.txt", "theirs");

        await Finished(ops.Move([tree.At("docs")], tree.At("dst"),
            target => ValueTask.FromResult(
                PathRules.LeafName(target) == "a.txt"
                    ? ConflictResolution.Skip
                    : ConflictResolution.Overwrite)));

        Assert.Equal("mine", tree.Read("docs", "a.txt"));
        Assert.Equal(["draft"], await TagsOn(tags, tree.At("docs", "a.txt")));
    }

    /// <summary>
    /// The same event from the destination's side: the file the user chose to
    /// keep must not pick up a tag it never had.
    /// </summary>
    [WindowsFact]
    public async Task The_file_that_was_kept_does_not_inherit_a_tag()
    {
        using var tree = new TempTree();
        var (tags, ops) = Store(tree);

        tree.Write("docs/a.txt", "mine");
        await tags.SetAsync(tree.At("docs", "a.txt"), ["draft"], CancellationToken.None);
        tree.Write("dst/docs/a.txt", "theirs");

        await Finished(ops.Move([tree.At("docs")], tree.At("dst"), Always(ConflictResolution.Skip)));

        Assert.Empty(await TagsOn(tags, tree.At("dst", "docs", "a.txt")));
    }

    /// <summary>
    /// The other half of the same list: everything that did move must still be
    /// found under its new path, or the fix would have been to stop retargeting.
    /// </summary>
    [WindowsFact]
    public async Task The_files_that_did_move_are_retargeted()
    {
        using var tree = new TempTree();
        var (tags, ops) = Store(tree);

        tree.Write("docs/a.txt", "mine");
        tree.Write("docs/b.txt", "mine");
        await tags.SetAsync(tree.At("docs", "b.txt"), ["final"], CancellationToken.None);
        tree.Write("dst/docs/a.txt", "theirs");

        await Finished(ops.Move([tree.At("docs")], tree.At("dst"),
            target => ValueTask.FromResult(
                PathRules.LeafName(target) == "a.txt"
                    ? ConflictResolution.Skip
                    : ConflictResolution.Overwrite)));

        Assert.Equal(["final"], await TagsOn(tags, tree.At("dst", "docs", "b.txt")));
    }

    /// <summary>
    /// A KeepBoth landing is not `destination + name` either, so the tag has to
    /// follow the file to the deduplicated name it actually took.
    /// </summary>
    [WindowsFact]
    public async Task A_tag_follows_a_file_to_its_KeepBoth_name()
    {
        using var tree = new TempTree();
        var (tags, ops) = Store(tree);

        var file = tree.Write("src/readme.txt", "mine");
        await tags.SetAsync(file, ["draft"], CancellationToken.None);
        tree.Write("dst/readme.txt", "bystander");

        await Finished(ops.Move([file], tree.At("dst"), Always(ConflictResolution.KeepBoth)));

        Assert.Equal(["draft"], await TagsOn(tags, tree.At("dst", "readme - Copy.txt")));
        Assert.Empty(await TagsOn(tags, tree.At("dst", "readme.txt")));
    }

    [WindowsFact]
    public async Task A_renamed_files_tags_follow_it_and_come_back_on_undo()
    {
        using var tree = new TempTree();
        var (tags, ops) = Store(tree);

        var file = tree.Write("readme.txt");
        await tags.SetAsync(file, ["draft"], CancellationToken.None);

        await ops.RenameAsync(file, "README.txt", CancellationToken.None);
        Assert.Equal(["draft"], await TagsOn(tags, tree.At("README.txt")));

        await ops.UndoAsync(CancellationToken.None);
        Assert.Equal(["draft"], await TagsOn(tags, tree.At("readme.txt")));
    }
}
