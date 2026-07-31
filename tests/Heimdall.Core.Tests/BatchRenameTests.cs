using Heimdall.Core.FileSystem;
using Xunit;

namespace Heimdall.Core.Tests;

/// <summary>
/// Batch rename is the one bulk operation here with no undo worth the name, so
/// **the preview IS the plan** — what it shows is exactly what will be executed.
/// That makes the planner worth testing hard: a collision it fails to notice
/// becomes a file silently overwritten.
/// </summary>
public class BatchRenameTests
{
    private static FileEntry File(string name)
        => new(name, "/tmp/" + name, 0, DateTimeOffset.UnixEpoch, EntryFlags.None);

    private static IReadOnlyList<FileEntry> Files(params string[] names)
        => names.Select(File).ToList();

    [Fact]
    public void Hash_runs_become_a_counter_padded_to_their_length()
    {
        var plan = BatchRename.Plan(
            Files("a.txt", "b.txt", "c.txt"),
            new BatchRenameOptions { Pattern = "shot-###", StartAt = 1 });

        Assert.Equal(["shot-001.txt", "shot-002.txt", "shot-003.txt"],
            plan.Select(p => p.NewName));
    }

    [Fact]
    public void Counting_starts_where_it_is_told()
    {
        var plan = BatchRename.Plan(
            Files("a.txt", "b.txt"),
            new BatchRenameOptions { Pattern = "n-#", StartAt = 7 });

        Assert.Equal(["n-7.txt", "n-8.txt"], plan.Select(p => p.NewName));
    }

    /// <summary>
    /// The extension is preserved by default, so a numbered pattern cannot
    /// quietly strip the thing that decides how a file opens.
    /// </summary>
    [Fact]
    public void The_extension_survives_unless_asked_otherwise()
    {
        var kept = BatchRename.Plan(Files("photo.jpeg"),
            new BatchRenameOptions { Pattern = "x", KeepExtension = true });

        var dropped = BatchRename.Plan(Files("photo.jpeg"),
            new BatchRenameOptions { Pattern = "x", KeepExtension = false });

        Assert.Equal("x.jpeg", kept[0].NewName);
        Assert.Equal("x", dropped[0].NewName);
    }

    [Fact]
    public void Replace_edits_the_stem_and_leaves_the_extension()
    {
        var plan = BatchRename.Plan(
            Files("draft-one.md", "draft-two.md"),
            new BatchRenameOptions
            {
                Mode = RenameMode.Replace,
                Find = "draft",
                Replace = "final",
            });

        Assert.Equal(["final-one.md", "final-two.md"], plan.Select(p => p.NewName));
    }

    /// <summary>
    /// **The collision check is the point of the whole feature.** A pattern with
    /// no counter maps every file onto one name, and each file after the first
    /// would overwrite the last.
    /// </summary>
    [Fact]
    public void Two_files_planned_onto_one_name_is_reported()
    {
        var plan = BatchRename.Plan(
            Files("a.txt", "b.txt"),
            new BatchRenameOptions { Pattern = "same" });

        Assert.Null(plan[0].Problem);
        Assert.NotNull(plan[1].Problem);
    }

    /// <summary>
    /// A bad regular expression is a typo, not a crash — it belongs in the
    /// preview beside the file it affects.
    /// </summary>
    [Fact]
    public void A_broken_regex_becomes_a_problem_not_an_exception()
    {
        var plan = BatchRename.Plan(
            Files("a.txt"),
            new BatchRenameOptions
            {
                Mode = RenameMode.Replace,
                Find = "([unclosed",
                Replace = "x",
                UseRegex = true,
            });

        Assert.NotNull(plan[0].Problem);
        Assert.Equal("a.txt", plan[0].NewName);
    }

    /// <summary>Every entry gets a row, whether or not it can be renamed —
    /// a preview that silently omits a file is the failure mode to avoid.</summary>
    [Fact]
    public void Every_input_produces_exactly_one_preview()
    {
        var input = Files("a.txt", "b.txt", "c.txt", "d.txt");

        var plan = BatchRename.Plan(input, new BatchRenameOptions { Pattern = "n-##" });

        Assert.Equal(input.Count, plan.Count);
        Assert.Equal(input.Select(f => f.FullPath), plan.Select(p => p.FullPath));
    }
}
