using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>What was chosen, and whether to stop asking.</summary>
public readonly record struct ConflictAnswer(ConflictResolution Resolution, bool ApplyToRest);

/// <summary>
/// The question nobody was ever asked.
///
/// **Every call site passed KeepBoth.** The engine has understood Overwrite,
/// Skip, KeepBoth and Cancel since it was written, and the callback that
/// chooses between them was hard-coded at all five places that build one — so
/// dropping a newer copy of a file over an older one silently produced
/// "name (1)", every time, with no way to say what was actually wanted.
///
/// Both sides are shown because the decision is a comparison. Which is newer
/// and which is larger is the whole of what somebody needs to answer
/// "replace?", and a prompt that names only the destination makes them go and
/// look.
/// </summary>
public sealed partial class ConflictViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ConflictAnswer> _answered = new();

    public ConflictViewModel(FileConflict conflict)
    {
        Name = Path.GetFileName(conflict.Target);
        IsDirectory = Directory.Exists(conflict.Target);

        Existing = Describe(conflict.Target);
        Arriving = Describe(conflict.Source);

        Destination = PathRules.Parent(conflict.Target) ?? conflict.Target;

        // Said out loud rather than left to be worked out from two timestamps.
        // "Newer" is the reason somebody overwrites, and reading it off a pair
        // of dates is exactly the small friction a prompt exists to remove.
        Verdict = Compare(conflict.Source, conflict.Target);
    }

    public string Name { get; }
    public bool IsDirectory { get; }
    public string Destination { get; }
    public string Existing { get; }
    public string Arriving { get; }
    public string Verdict { get; }

    public string Question => IsDirectory
        ? $"A folder called {Name} is already there."
        : $"A file called {Name} is already there.";

    /// <summary>
    /// **Off by default, and deliberately.** The dangerous answer is the one
    /// given once and applied to five hundred files, so applying to the rest is
    /// something to reach for rather than something to forget to turn off.
    /// </summary>
    [ObservableProperty] private bool _applyToRest;

    public Task<ConflictAnswer> Answer => _answered.Task;

    [RelayCommand] private void Overwrite() => Choose(ConflictResolution.Overwrite);
    [RelayCommand] private void KeepBoth() => Choose(ConflictResolution.KeepBoth);
    [RelayCommand] private void Skip() => Choose(ConflictResolution.Skip);

    /// <summary>Also what closing the window means: a decision not made is not
    /// a licence to overwrite.</summary>
    [RelayCommand] public void Cancel() => Choose(ConflictResolution.Cancel);

    private void Choose(ConflictResolution resolution)
    {
        // **Answered once, and Closed raised once.**
        //
        // The window closes when this fires, and closing the window answers
        // Cancel — so raising it unconditionally is a loop: choose, close,
        // cancel, close, for as long as the stack holds. The test that renders
        // this window hung outright rather than failing, which is how it was
        // found.
        //
        // Cancel stops the whole operation, so "for the rest" has no meaning
        // alongside it.
        if (!_answered.TrySetResult(new ConflictAnswer(
                resolution, resolution != ConflictResolution.Cancel && ApplyToRest)))
            return;

        Closed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Closed;

    private static string Describe(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var directory = new DirectoryInfo(path);
                var count = directory.EnumerateFileSystemInfos().Take(1000).Count();

                return $"{count} item{(count == 1 ? "" : "s")} · "
                       + $"{directory.LastWriteTime:d MMM yyyy, HH:mm}";
            }

            var file = new FileInfo(path);

            return file.Exists
                ? $"{ByteSize.Format(file.Length)} · {file.LastWriteTime:d MMM yyyy, HH:mm}"
                : "not there any more";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "could not be read";
        }
    }

    /// <summary>
    /// Which of the two is newer, or that they look identical — the one line
    /// that turns two rows of numbers into an answer.
    /// </summary>
    private static string Compare(string source, string target)
    {
        try
        {
            if (Directory.Exists(source) || Directory.Exists(target)) return "";

            var from = new FileInfo(source);
            var to = new FileInfo(target);

            if (!from.Exists || !to.Exists) return "";

            if (from.Length == to.Length && from.LastWriteTimeUtc == to.LastWriteTimeUtc)
                return "They look like the same file.";

            var difference = from.LastWriteTimeUtc - to.LastWriteTimeUtc;

            // A second either way is not a meaningful difference, and calling
            // it one would be a confident answer to a question nobody asked.
            if (difference.Duration() < TimeSpan.FromSeconds(2))
                return "Both were changed at the same time.";

            return difference > TimeSpan.Zero
                ? "The one arriving is newer."
                : "The one already there is newer.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
