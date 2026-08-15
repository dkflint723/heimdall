using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Being asked what to do when something is already there.
///
/// **Nobody ever was.** Copy and move have understood Overwrite, Skip, KeepBoth
/// and Cancel since they were written, and all five callers passed KeepBoth
/// outright — so a newer file dropped over an older one silently became
/// "name (1)", with no way to say what was actually wanted. These pin the
/// asking, and the one place where not asking is still correct.
/// </summary>
public sealed class ConflictPromptTests : IDisposable
{
    private readonly string _root;

    public ConflictPromptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vaktari-conflict-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        PaneViewModel.AskConflict = null;

        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private string Write(string name, string content, DateTime? written = null)
    {
        var path = Path.Combine(_root, name);

        File.WriteAllText(path, content);

        if (written is { } stamp) File.SetLastWriteTimeUtc(path, stamp);

        return path;
    }

    // ---- what the prompt says ---------------------------------------------

    /// <summary>
    /// **Both sides, because the decision is a comparison.** Which is newer and
    /// which is larger is the whole of what anybody needs to answer "replace?",
    /// and the callback used to carry only the destination — so a prompt built
    /// on it could not have shown this even if one had existed.
    /// </summary>
    [Fact]
    public void It_describes_both_files_and_says_which_is_newer()
    {
        var older = Write("target.txt", "old", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var newer = Write("source.txt", "much longer content",
            new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));

        var model = new ConflictViewModel(new FileConflict(newer, older));

        Assert.Contains("target.txt", model.Question, StringComparison.Ordinal);
        Assert.Contains("3 B", model.Existing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("19 B", model.Arriving, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("The one arriving is newer.", model.Verdict);
    }

    [Fact]
    public void It_says_when_the_one_already_there_is_newer()
    {
        var newer = Write("target.txt", "a", new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        var older = Write("source.txt", "b", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            "The one already there is newer.",
            new ConflictViewModel(new FileConflict(older, newer)).Verdict);
    }

    /// <summary>Same size, same timestamp: worth saying, because it means the
    /// answer probably does not matter.</summary>
    [Fact]
    public void It_says_when_the_two_look_identical()
    {
        var stamp = new DateTime(2026, 3, 3, 9, 0, 0, DateTimeKind.Utc);

        var a = Write("target.txt", "same", stamp);
        var b = Write("source.txt", "same", stamp);

        Assert.Equal("They look like the same file.", new ConflictViewModel(new FileConflict(b, a)).Verdict);
    }

    // ---- what the answer does ---------------------------------------------

    [Theory]
    [InlineData(ConflictResolution.Overwrite)]
    [InlineData(ConflictResolution.Skip)]
    [InlineData(ConflictResolution.KeepBoth)]
    public async Task A_choice_is_reported_with_whether_to_keep_asking(ConflictResolution chosen)
    {
        var a = Write("target.txt", "x");
        var b = Write("source.txt", "y");

        var model = new ConflictViewModel(new FileConflict(b, a)) { ApplyToRest = true };

        switch (chosen)
        {
            case ConflictResolution.Overwrite: model.OverwriteCommand.Execute(null); break;
            case ConflictResolution.Skip: model.SkipCommand.Execute(null); break;
            default: model.KeepBothCommand.Execute(null); break;
        }

        Assert.True(model.Answer.IsCompleted);
        Assert.Equal(new ConflictAnswer(chosen, true), await model.Answer);
    }

    /// <summary>
    /// **Cancel stops the operation, so "for the rest" is meaningless.**
    /// Reporting it as remembered would leave the closure holding Cancel and
    /// answering it for items that will never be reached.
    /// </summary>
    [Fact]
    public async Task Cancel_is_never_remembered()
    {
        var a = Write("target.txt", "x");
        var b = Write("source.txt", "y");

        var model = new ConflictViewModel(new FileConflict(b, a)) { ApplyToRest = true };

        model.CancelCommand.Execute(null);

        Assert.Equal(new ConflictAnswer(ConflictResolution.Cancel, false), await model.Answer);
    }

    /// <summary>Closing the window answers Cancel rather than nothing — an
    /// operation is waiting on this from a background thread.</summary>
    [Fact]
    public async Task An_unanswered_prompt_that_goes_away_cancels()
    {
        var a = Write("target.txt", "x");
        var b = Write("source.txt", "y");

        var model = new ConflictViewModel(new FileConflict(b, a));

        Assert.False(model.Answer.IsCompleted);

        model.Cancel();

        Assert.Equal(ConflictResolution.Cancel, (await model.Answer).Resolution);
    }

    // ---- how often it asks -------------------------------------------------

    /// <summary>
    /// **Asked once per clash, until told to stop.** The dangerous answer is
    /// the one given once and applied to five hundred files, so the memory is
    /// opt-in and lasts exactly one operation.
    /// </summary>
    [AvaloniaFact]
    public async Task It_asks_every_time_unless_told_to_apply_to_the_rest()
    {
        var asked = 0;

        PaneViewModel.AskConflict = _ =>
        {
            asked++;

            return ValueTask.FromResult(new ConflictAnswer(ConflictResolution.Overwrite, false));
        };

        var settle = Conflicts();

        for (var i = 0; i < 3; i++)
            Assert.Equal(ConflictResolution.Overwrite, await settle(Clash(i)));

        Assert.Equal(3, asked);

        asked = 0;
        PaneViewModel.AskConflict = _ =>
        {
            asked++;

            return ValueTask.FromResult(new ConflictAnswer(ConflictResolution.Skip, true));
        };

        settle = Conflicts();

        for (var i = 0; i < 3; i++)
            Assert.Equal(ConflictResolution.Skip, await settle(Clash(i)));

        Assert.Equal(1, asked);
    }

    /// <summary>
    /// **A remembered answer belongs to one operation.** Otherwise "overwrite
    /// the rest", said once about a folder of duplicates, would silently apply
    /// to the next paste an hour later.
    /// </summary>
    [AvaloniaFact]
    public async Task A_remembered_answer_does_not_outlive_its_operation()
    {
        var asked = 0;

        PaneViewModel.AskConflict = _ =>
        {
            asked++;

            return ValueTask.FromResult(new ConflictAnswer(ConflictResolution.Overwrite, true));
        };

        var first = Conflicts();
        await first(Clash(1));
        await first(Clash(2));

        var second = Conflicts();
        await second(Clash(3));

        Assert.Equal(2, asked);
    }

    /// <summary>With nothing to ask with — a headless run — the behaviour is
    /// what the application did before there was a prompt, which is the answer
    /// that destroys nothing.</summary>
    [AvaloniaFact]
    public async Task With_no_way_to_ask_nothing_is_overwritten()
    {
        PaneViewModel.AskConflict = null;

        Assert.Equal(ConflictResolution.KeepBoth, await Conflicts()(Clash(1)));
    }

    private FileConflict Clash(int i) =>
        new(Path.Combine(_root, $"s{i}.txt"), Path.Combine(_root, $"t{i}.txt"));

    /// <summary>Reaches the per-operation closure the pane builds.</summary>
    private static Func<FileConflict, ValueTask<ConflictResolution>> Conflicts() =>
        (Func<FileConflict, ValueTask<ConflictResolution>>)typeof(PaneViewModel)
            .GetMethod("Conflicts", System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Static)!
            .Invoke(null, null)!;
}
