using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Renaming on NTFS, where "the same name" is a question with two answers.
///
/// **The bug these were written for.** `RenameAsync` short-circuited on
/// `PathRules.Same(target, path)`, and `PathRules.Same` is `OrdinalIgnoreCase`
/// on Windows — so renaming `readme.txt` to `README.txt` returned without
/// calling `File.Move` at all. The user pressed F2, typed the correction,
/// pressed Enter, and the listing did not change. Nothing failed; nothing
/// happened. The check immediately below it existed specifically to let case
/// corrections through, and was unreachable.
/// </summary>
[SupportedOSPlatform("windows")]
public class RenameTests
{
    [WindowsFact]
    public async Task A_file_can_be_renamed_to_its_own_name_in_another_case()
    {
        using var tree = new TempTree();
        var file = tree.Write("readme.txt");
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(file, "README.txt", CancellationToken.None);

        Assert.Equal(["README.txt"], tree.Names());
    }

    [WindowsFact]
    public async Task A_case_only_file_rename_can_be_undone()
    {
        using var tree = new TempTree();
        var file = tree.Write("readme.txt");
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(file, "README.txt", CancellationToken.None);
        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["readme.txt"], tree.Names());
    }

    /// <summary>
    /// The half of the bug that only appears once the first half is fixed.
    /// `Directory.Move` compares its own two paths case-insensitively and
    /// throws "source and destination path must be different", so letting a
    /// case-only rename through turned a silent no-op into a visible error
    /// until it was routed via a staging name.
    /// </summary>
    [WindowsFact]
    public async Task A_folder_can_be_renamed_to_its_own_name_in_another_case()
    {
        using var tree = new TempTree();
        var folder = tree.Dir("photos");
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(folder, "Photos", CancellationToken.None);

        Assert.Equal(["Photos"], tree.Names());
    }

    /// <summary>
    /// The staging name is a real move on disk, so this asserts the contents
    /// arrive with it rather than the folder merely being renamed empty.
    /// </summary>
    [WindowsFact]
    public async Task A_case_only_folder_rename_brings_the_contents_with_it()
    {
        using var tree = new TempTree();
        tree.Write("photos/a.txt");
        tree.Write("photos/nested/b.txt");
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(tree.At("photos"), "Photos", CancellationToken.None);

        Assert.True(tree.Exists("Photos", "a.txt"));
        Assert.True(tree.Exists("Photos", "nested", "b.txt"));
    }

    [WindowsFact]
    public async Task A_case_only_folder_rename_can_be_undone()
    {
        using var tree = new TempTree();
        tree.Write("photos/a.txt");
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(tree.At("photos"), "Photos", CancellationToken.None);
        await ops.UndoAsync(CancellationToken.None);

        Assert.Equal(["photos"], tree.Names());
        Assert.True(tree.Exists("photos", "a.txt"));
    }

    /// <summary>The staging detour must not have cost the ordinary case.</summary>
    [WindowsFact]
    public async Task An_ordinary_folder_rename_still_works()
    {
        using var tree = new TempTree();
        tree.Write("photos/a.txt");
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(tree.At("photos"), "pictures", CancellationToken.None);

        Assert.Equal(["pictures"], tree.Names());
        Assert.True(tree.Exists("pictures", "a.txt"));
    }

    /// <summary>
    /// The one case the early return is actually for: the name did not change,
    /// so neither should anything on disk.
    /// </summary>
    [WindowsFact]
    public async Task A_rename_to_the_identical_name_does_nothing()
    {
        using var tree = new TempTree();
        var file = tree.Write("readme.txt");
        var ops = new WindowsFileOperations();

        await ops.RenameAsync(file, "readme.txt", CancellationToken.None);

        Assert.Equal(["readme.txt"], tree.Names());
        Assert.False(ops.CanUndo);
    }

    [WindowsFact]
    public async Task A_rename_onto_a_name_already_in_use_is_refused()
    {
        using var tree = new TempTree();
        var file = tree.Write("readme.txt");
        tree.Write("notes.txt");
        var ops = new WindowsFileOperations();

        await Assert.ThrowsAsync<IOException>(
            async () => await ops.RenameAsync(file, "notes.txt", CancellationToken.None));

        Assert.Equal(["notes.txt", "readme.txt"], tree.Names());
    }
}
