using Heimdall.Core;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Settings;

namespace Heimdall.Windows;

/// <summary>
/// Listing, restoring and emptying the Recycle Bin.
///
/// **Returning null for this was the honest answer for one release and is no
/// longer.** WINDOWS.md recorded the Recycle Bin as needing COM, and COM under
/// NativeAOT as the risky combination that would fail at runtime rather than at
/// compile time. That was an assumption, and it was wrong: a source-generated
/// IShellItem enumeration of the bin runs correctly in a published AOT binary.
/// Measuring it also showed the shell was not the right tool here anyway — see
/// <see cref="RecycleBin"/> for why the metadata format wins on the three
/// things this interface actually asks for.
///
/// The semantics are XdgTrashMaintenance's, deliberately. Both systems keep a
/// payload and a sidecar, so "restore alongside rather than clobber, and say
/// where it landed" means the same thing on each.
/// </summary>
public sealed class WindowsTrashMaintenance : ITrashMaintenance
{
    public IReadOnlyList<TrashedItem> List()
        => RecycleBin.List()
            .Select(e => new TrashedItem(
                // The metadata path is the key, not the bare filename: a
                // volume's bin can hold $IABC123.txt while another holds the
                // same name, and Restore has to know which one it was handed.
                TrashName: e.InfoPath,
                OriginalPath: e.OriginalPath,
                Payload: e.PayloadPath,
                Deleted: e.Deleted,
                Size: e.Size,
                IsDirectory: e.IsDirectory))
            .ToList();

    /// <summary>
    /// Puts one item back, and answers with where it actually went.
    ///
    /// The order matters and is the same as the Linux side's: move first, drop
    /// the metadata second. A crash between them leaves an orphaned $I file,
    /// which lists as nothing and is harmless. The reverse order would lose the
    /// only record of where the payload belonged while the payload still
    /// existed — recoverable bytes with no memory of their home.
    /// </summary>
    public string Restore(string trashName)
    {
        var entry = RecycleBin.Read(trashName)
            ?? throw new FileNotFoundException("Nothing in the Recycle Bin for " + trashName);

        var target = entry.OriginalPath;

        // Something has taken the name back since. Restore beside it rather
        // than over it: the file being restored is the one the user asked for,
        // and the one in the way is one they may not know is there.
        if (File.Exists(target) || Directory.Exists(target)) target = Deduplicate(target);

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        if (entry.IsDirectory) Directory.Move(entry.PayloadPath, target);
        else File.Move(entry.PayloadPath, target);

        try { File.Delete(entry.InfoPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The payload is home, which is what was asked for. A metadata file
            // left behind lists as nothing, because Read requires its payload.
            Quiet.Swallowed("trash", e);
        }

        return target;
    }

    /// <summary>
    /// "notes.txt" becomes "notes (1).txt" — Windows' own phrasing for the same
    /// situation, and the same rule WindowsFileOperations uses when a copy
    /// collides. A restore should not invent a naming convention of its own.
    /// </summary>
    private static string Deduplicate(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 1; n < 10_000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }

        throw new IOException("Could not find a free name beside " + path);
    }

    /// <summary>
    /// Applies the policy, and does nothing when neither half of it is on. The
    /// disabled state is not "sweep with defaults" — this deletes files with
    /// nobody watching, so it acts only when asked.
    /// </summary>
    public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
    {
        if (!policy.DeleteOldFiles && !policy.LimitSize)
            return ValueTask.FromResult(TrashSweepResult.Nothing);

        var entries = RecycleBin.List();

        var removed = 0;
        long freed = 0;

        if (policy.DeleteOldFiles)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, policy.DeleteAfterDays));

            foreach (var entry in entries.Where(e => e.Deleted < cutoff))
            {
                ct.ThrowIfCancellationRequested();
                if (Purge(entry)) { removed++; freed += entry.Size; }
            }
        }

        // **Reported, not enforced, unless the policy says otherwise.** Over the
        // allowance the choice is the user's: Warn hands the fact back and
        // deletes nothing.
        var overLimit = false;

        if (policy.LimitSize)
        {
            var total = RecycleBin.List().Sum(e => e.Size);
            var allowance = Allowance(policy.MaximumPercentOfDisk);

            if (allowance > 0 && total > allowance)
            {
                if (policy.WhenLimitReached == TrashLimitAction.Warn) overLimit = true;
                else
                {
                    // Oldest first, until it fits. Deleting newest-first would
                    // take the thing most likely to be wanted back.
                    foreach (var entry in RecycleBin.List().OrderBy(e => e.Deleted))
                    {
                        if (total <= allowance) break;
                        ct.ThrowIfCancellationRequested();

                        if (Purge(entry)) { removed++; freed += entry.Size; total -= entry.Size; }
                    }
                }
            }
        }

        return ValueTask.FromResult(new TrashSweepResult
        {
            Removed = removed,
            BytesFreed = freed,
            OverLimit = overLimit,
        });
    }

    /// <summary>The size the bin is allowed, as a share of the system volume.</summary>
    private static long Allowance(int percent)
    {
        if (percent is <= 0 or > 100) return 0;

        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
            if (string.IsNullOrEmpty(root)) return 0;

            return (long)(new DriveInfo(root).TotalSize * (percent / 100.0));
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
    {
        var removed = 0;
        long freed = 0;

        foreach (var entry in RecycleBin.List())
        {
            ct.ThrowIfCancellationRequested();
            if (Purge(entry)) { removed++; freed += entry.Size; }
        }

        return ValueTask.FromResult(new TrashSweepResult { Removed = removed, BytesFreed = freed });
    }

    /// <summary>
    /// Deletes one entry for good: payload first, then its metadata.
    ///
    /// **Not SHEmptyRecycleBin**, which would be one call for EmptyAsync and no
    /// use at all for SweepAsync — it empties everything or nothing, and a
    /// policy sweep is by definition selective. One code path for both means
    /// the dangerous one is the one that gets exercised every time.
    /// </summary>
    private static bool Purge(RecycleEntry entry)
    {
        try
        {
            if (entry.IsDirectory) Directory.Delete(entry.PayloadPath, recursive: true);
            else File.Delete(entry.PayloadPath);

            File.Delete(entry.InfoPath);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A locked or protected item stays. Reporting fewer removals than
            // expected is right; failing the sweep over one file is not.
            Quiet.Swallowed("trash", e);
            return false;
        }
    }
}
