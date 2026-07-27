namespace Heimdall.Core.FileSystem;

/// <summary>A folder and how often it has been opened.</summary>
public sealed record VisitedFolder(string Path, string Label, int Count);

/// <summary>
/// Counts how often each folder is opened, so the most-used ones can be offered
/// without anyone having to bookmark them.
///
/// **Deliberately a plain count, not a recency-weighted score.** A decayed score
/// is arguably better behaved, but it makes the list move for reasons the user
/// cannot see — a folder drifts down the list because time passed, not because
/// they did anything. A count only changes when you open something.
///
/// Only **user-initiated** navigation is recorded. Restoring a session, going
/// back, and refreshing are not choices about where to go, and counting them
/// would let whatever tabs you happen to leave open climb the list on their own.
/// </summary>
public interface IVisitStore
{
    void Record(string path);

    /// <summary>Most-opened first. Fewer than <paramref name="count"/> when
    /// little has been visited yet.</summary>
    IReadOnlyList<VisitedFolder> Top(int count);

    /// <summary>Forgets one folder, so a mistake can be undone.</summary>
    void Forget(string path);

    /// <summary>Raised when the counts change, so a list can re-rank itself.</summary>
    event EventHandler? Changed;
}
