using Heimdall.Core.Settings;

namespace Heimdall.Core.FileSystem;

/// <summary>What a sweep did. Reported rather than silent, because the whole
/// feature is the application deleting files with nobody watching.</summary>
public sealed record TrashSweepResult
{
    public int Removed { get; init; }

    public long BytesFreed { get; init; }

    /// <summary>
    /// Entries left alone because their deletion date could not be read.
    /// **Unreadable is never treated as old.** A malformed or missing
    /// <c>.trashinfo</c> means we do not know when something was deleted, and
    /// "do not know" must not become "delete it".
    /// </summary>
    public int Skipped { get; init; }

    /// <summary>
    /// The size limit is exceeded and the policy said to warn rather than
    /// delete. The caller surfaces this; nothing was removed for it.
    /// </summary>
    public bool OverLimit { get; init; }

    public static readonly TrashSweepResult Nothing = new();
}

/// <summary>
/// Expiry and size limits for the trash.
///
/// Platform-specific because the trash itself is: freedesktop's spec on Linux,
/// the recycle bin on Windows. Separate from <see cref="IFileOperations"/>
/// because moving one file to the trash and unattended bulk deletion are very
/// different risks, and a caller should have to reach for this one deliberately.
/// </summary>
public interface ITrashMaintenance
{
    /// <summary>
    /// Applies the policy. Does nothing at all when neither expiry nor a size
    /// limit is enabled — the disabled state is not "sweep with defaults".
    /// </summary>
    ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct);
}
