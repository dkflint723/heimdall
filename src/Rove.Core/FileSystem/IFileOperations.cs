namespace Rove.Core.FileSystem;

public enum ConflictResolution { Overwrite, Skip, KeepBoth, Cancel }

public enum OperationState { Queued, Running, Paused, Completed, Failed, Cancelled }

public readonly record struct OperationProgress(
    long BytesDone,
    long BytesTotal,
    int ItemsDone,
    int ItemsTotal,
    string? CurrentItem);

/// <summary>
/// A running or queued operation. Handles are surfaced by the transfer queue
/// panel — which is why this exists as a type rather than operations being bare
/// awaitable calls. Pause and reorder are impossible to retrofit onto a Task.
/// </summary>
public interface IOperationHandle
{
    Guid Id { get; }
    OperationState State { get; }
    IProgress<OperationProgress> Progress { get; }

    void Pause();
    void Resume();
    void Cancel();

    Task Completion { get; }
}

/// <summary>
/// Mutating operations. On Windows every one of these routes through
/// IFileOperation — recycle bin semantics, collision dialogs, UAC elevation and
/// undo all live there, and a hand-rolled copy loop forfeits the lot. On Linux
/// this is our own engine plus the XDG trash spec.
/// </summary>
public interface IFileOperations
{
    IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
        Func<string, ValueTask<ConflictResolution>> onConflict);

    IOperationHandle Move(IReadOnlyList<string> sources, string destination,
        Func<string, ValueTask<ConflictResolution>> onConflict);

    /// <summary>Moves to recycle bin / XDG trash. Recoverable by the user.</summary>
    IOperationHandle Trash(IReadOnlyList<string> paths);

    /// <summary>Irreversible. Only ever from an explicit, distinct user action.</summary>
    IOperationHandle Delete(IReadOnlyList<string> paths);

    ValueTask RenameAsync(string path, string newName, CancellationToken ct);

    bool CanUndo { get; }
    ValueTask UndoAsync(CancellationToken ct);
}
