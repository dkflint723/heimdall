namespace Vaktari.Core.FileSystem;

/// <summary>What a recent entry is, which is also which list it appears in.</summary>
public enum RecentKind { File, Folder }

/// <summary>One thing opened, and when.</summary>
public sealed record RecentEntry(string Path, RecentKind Kind, DateTimeOffset When);

/// <summary>
/// The most recently opened files and folders, newest first.
///
/// **Deliberately separate from the visit-count store that used to feed the
/// sidebar's frequent-folders list.**
/// Frequency and recency are different questions and their retention rules
/// contradicted each other: its <c>Top</c> excluded anything visited once,
/// because a list of things you touched once is noise — but a folder you opened
/// once a minute ago is exactly what belongs at the top of a *recent* list. Its
/// bound trims to the strongest entries **by count**, which would evict that
/// same folder first. One store cannot serve both without one of them
/// compromising, and neither list is worth compromising.
///
/// Recent *files* need this store regardless: nothing else records that a file
/// was opened, and putting files into the visit counts would make them show up
/// as frequent *folders* unless every reader filtered them out.
///
/// Only **user-initiated** opening is recorded:
/// back, forward, refresh and session restore are not choices about where to go.
/// </summary>
public interface IRecentStore
{
    /// <summary>Records an open, or moves an existing entry to the top.</summary>
    void Record(string path, RecentKind kind);

    /// <summary>Newest first. Fewer than <paramref name="count"/> when little
    /// has been opened yet.</summary>
    IReadOnlyList<RecentEntry> Recent(RecentKind kind, int count);

    /// <summary>Drops one entry, so a mistake can be undone. Mirrors Dolphin's
    /// "Forget" action, which is a real part of the feature rather than a
    /// nicety — a recent list you cannot edit is a log, not a tool.</summary>
    void Forget(string path);

    /// <summary>Raised when the lists change, so the sidebar can re-rank.</summary>
    event EventHandler? Changed;
}
