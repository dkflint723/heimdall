using System.Runtime.Versioning;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Tests;
using Xunit;

namespace Heimdall.Windows.Tests;

/// <summary>
/// Permanent deletion, and the Windows attribute that refuses it.
///
/// **The bug.** A read-only file will not delete on Windows, where on Linux it
/// would: the permission is on the file rather than on its directory.
/// `Delete` knew that and cleared the attribute — on the top-level path only.
/// `Directory.Delete(recursive: true)` then stopped at the first read-only file
/// *inside* the tree and threw, leaving half the tree removed and half of it
/// standing. Deleting a single read-only file worked, which is what made it
/// look handled.
///
/// One `git clone` produces such a tree: git writes its pack files read-only.
///
/// **<see cref="IFileOperations.Trash"/> is not covered here**, and that is a
/// gap rather than an oversight. Exercising it means putting real items in the
/// Recycle Bin, and this application cannot take them back out again —
/// `ITrashMaintenance` is still null, which is the same missing half that
/// leaves it with no Trash view and no Restore. A test that permanently
/// littered the developer's bin to prove a progress counter is not worth it.
/// </summary>
[SupportedOSPlatform("windows")]
public class DeleteTests
{
    private static async Task<IOperationHandle> Finished(IOperationHandle handle)
    {
        await handle.Completion;
        Assert.Null(handle.Error);
        Assert.Equal(OperationState.Completed, handle.State);
        return handle;
    }

    /// <summary>The case that always worked, kept so the fix cannot regress it.</summary>
    [WindowsFact]
    public async Task A_read_only_file_is_deleted()
    {
        using var tree = new TempTree();
        var file = tree.WriteReadOnly("locked.txt");

        await Finished(new WindowsFileOperations().Delete([file]));

        Assert.False(tree.Exists("locked.txt"));
    }

    [WindowsFact]
    public async Task A_tree_holding_a_read_only_file_is_deleted_completely()
    {
        using var tree = new TempTree();
        tree.Write("repo/HEAD");
        tree.WriteReadOnly("repo/objects/pack/pack-abc.idx");
        tree.WriteReadOnly("repo/objects/pack/pack-abc.pack");
        tree.Write("repo/objects/loose/aa/bbbb");

        await Finished(new WindowsFileOperations().Delete([tree.At("repo")]));

        Assert.False(tree.Exists("repo"));
    }

    /// <summary>
    /// A read-only *directory* refuses to go for the same reason its files do,
    /// and is worth its own case because clearing the attribute on files alone
    /// would still leave this one standing.
    /// </summary>
    [WindowsFact]
    public async Task A_tree_holding_a_read_only_folder_is_deleted_completely()
    {
        using var tree = new TempTree();
        var inner = tree.Dir("outer", "inner");
        tree.Write("outer/inner/a.txt");
        File.SetAttributes(inner, File.GetAttributes(inner) | FileAttributes.ReadOnly);

        await Finished(new WindowsFileOperations().Delete([tree.At("outer")]));

        Assert.False(tree.Exists("outer"));
    }

    [WindowsFact]
    public async Task Deleting_a_plain_tree_still_works()
    {
        using var tree = new TempTree();
        tree.Write("a/b/c.txt");
        tree.Write("a/d.txt");

        await Finished(new WindowsFileOperations().Delete([tree.At("a")]));

        Assert.False(tree.Exists("a"));
    }
}
