using System.Collections.Concurrent;
using Vaktari.Core.FileSystem;

namespace Vaktari.Linux;

/// <summary>
/// Copy, move, trash, delete and rename for Linux.
///
/// Everything destructive routes through here so there is exactly one place to
/// get right. Deletion means the XDG trash by default — recoverable from
/// Dolphin, from any trash browser, and from our own undo.
/// </summary>
public sealed class LinuxFileOperations : IFileOperations
{
    private const int BufferSize = 1 << 20;

    private readonly ConcurrentStack<IUndoable> _undo = new();

    public bool CanUndo => !_undo.IsEmpty;

    public IOperationHandle Copy(
        IReadOnlyList<string> sources, string destination,
        Func<string, ValueTask<ConflictResolution>> onConflict)
        => Run(sources, destination, onConflict, move: false);

    public IOperationHandle Move(
        IReadOnlyList<string> sources, string destination,
        Func<string, ValueTask<ConflictResolution>> onConflict)
        => Run(sources, destination, onConflict, move: true);

    public IOperationHandle Trash(IReadOnlyList<string> paths)
    {
        var handle = new OperationHandle();

        _ = Task.Run(async () =>
        {
            var restored = new List<(string TrashName, string Original)>();

            try
            {
                handle.Begin(paths.Count, totalBytes: 0);

                foreach (var path in paths)
                {
                    handle.Token.ThrowIfCancellationRequested();
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);

                    handle.ItemStarted(path);
                    var name = XdgTrash.Trash(path);
                    restored.Add((name, path));
                    handle.ItemFinished();
                }

                if (restored.Count > 0)
                    _undo.Push(new UndoTrash(restored));

                handle.Complete();
            }
            catch (OperationCanceledException) { handle.Cancelled(); }
            catch (Exception ex) { handle.Failed(ex); }
        });

        return handle;
    }

    /// <summary>
    /// Irreversible. Only ever reached from an explicit, separate user action —
    /// never as the default for the Delete key.
    /// </summary>
    public IOperationHandle Delete(IReadOnlyList<string> paths)
    {
        var handle = new OperationHandle();

        _ = Task.Run(async () =>
        {
            try
            {
                handle.Begin(paths.Count, totalBytes: 0);

                foreach (var path in paths)
                {
                    handle.Token.ThrowIfCancellationRequested();
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);

                    handle.ItemStarted(path);

                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else File.Delete(path);

                    handle.ItemFinished();
                }

                handle.Complete();
            }
            catch (OperationCanceledException) { handle.Cancelled(); }
            catch (Exception ex) { handle.Failed(ex); }
        });

        return handle;
    }

    public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/'))
            throw new ArgumentException("A name cannot be empty or contain a separator.", nameof(newName));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var target = Path.Combine(directory, newName);

        if (target == path) return ValueTask.CompletedTask;

        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"'{newName}' already exists here.");

        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target, overwrite: false);

        _undo.Push(new UndoRename(target, path));
        return ValueTask.CompletedTask;
    }

    public async ValueTask UndoAsync(CancellationToken ct)
    {
        if (_undo.TryPop(out var action))
            await action.UndoAsync(ct).ConfigureAwait(false);
    }

    private IOperationHandle Run(
        IReadOnlyList<string> sources, string destination,
        Func<string, ValueTask<ConflictResolution>> onConflict, bool move)
    {
        var handle = new OperationHandle();

        _ = Task.Run(async () =>
        {
            try
            {
                // Enumerating first means the progress bar is honest from the
                // start rather than discovering the total as it goes.
                var plan = BuildPlan(sources, destination, handle.Token);
                handle.Begin(plan.Count, plan.Sum(p => p.Length));

                var created = new List<string>();

                foreach (var item in plan)
                {
                    handle.Token.ThrowIfCancellationRequested();
                    await handle.WaitIfPausedAsync().ConfigureAwait(false);

                    var target = item.Target;

                    if (File.Exists(target) || Directory.Exists(target))
                    {
                        switch (await onConflict(target).ConfigureAwait(false))
                        {
                            case ConflictResolution.Skip:
                                handle.ItemFinished();
                                continue;
                            case ConflictResolution.KeepBoth:
                                target = XdgTrash.Deduplicate(target);
                                break;
                            case ConflictResolution.Cancel:
                                throw new OperationCanceledException();
                            case ConflictResolution.Overwrite:
                                break;
                        }
                    }

                    handle.ItemStarted(item.Source);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    if (item.IsDirectory)
                    {
                        Directory.CreateDirectory(target);
                    }
                    else
                    {
                        await CopyFileAsync(item.Source, target, handle).ConfigureAwait(false);
                        if (move) File.Delete(item.Source);
                    }

                    created.Add(target);
                    handle.ItemFinished();
                }

                // Directories are removed only after their contents moved, so a
                // cancelled move never deletes a folder it hasn't emptied.
                if (move)
                {
                    foreach (var source in sources.Where(Directory.Exists).Reverse())
                        if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any())
                            Directory.Delete(source);
                }

                // Copies are not undoable: undoing one means deleting files,
                // and an undo that deletes is not a safe default.
                if (move && created.Count > 0)
                    _undo.Push(new UndoMove(sources, destination));

                handle.Complete();
            }
            catch (OperationCanceledException) { handle.Cancelled(); }
            catch (Exception ex) { handle.Failed(ex); }
        });

        return handle;
    }

    private static List<PlannedItem> BuildPlan(
        IReadOnlyList<string> sources, string destination, CancellationToken ct)
    {
        var plan = new List<PlannedItem>();

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(source);
            var name = Path.GetFileName(full);

            if (Directory.Exists(full))
            {
                var root = Path.Combine(destination, name);
                plan.Add(new PlannedItem(full, root, 0, IsDirectory: true));

                foreach (var dir in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories))
                    plan.Add(new PlannedItem(dir, Path.Combine(root, Path.GetRelativePath(full, dir)), 0, true));

                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    var length = new FileInfo(file).Length;
                    plan.Add(new PlannedItem(
                        file, Path.Combine(root, Path.GetRelativePath(full, file)), length, false));
                }
            }
            else if (File.Exists(full))
            {
                plan.Add(new PlannedItem(
                    full, Path.Combine(destination, name), new FileInfo(full).Length, false));
            }
        }

        return plan;
    }

    private static async Task CopyFileAsync(string source, string target, OperationHandle handle)
    {
        var buffer = new byte[BufferSize];

        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using var output = new FileStream(
            target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        int read;
        while ((read = await input.ReadAsync(buffer, handle.Token).ConfigureAwait(false)) > 0)
        {
            await handle.WaitIfPausedAsync().ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, read), handle.Token).ConfigureAwait(false);
            handle.BytesCopied(read);
        }
    }

    private readonly record struct PlannedItem(
        string Source, string Target, long Length, bool IsDirectory);

    private interface IUndoable
    {
        ValueTask UndoAsync(CancellationToken ct);
    }

    private sealed class UndoRename(string current, string original) : IUndoable
    {
        public ValueTask UndoAsync(CancellationToken ct)
        {
            if (Directory.Exists(current)) Directory.Move(current, original);
            else if (File.Exists(current)) File.Move(current, original, overwrite: false);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UndoTrash(List<(string TrashName, string Original)> items) : IUndoable
    {
        public ValueTask UndoAsync(CancellationToken ct)
        {
            foreach (var (trashName, _) in items)
                XdgTrash.Restore(trashName);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UndoMove(
        IReadOnlyList<string> sources, string destination) : IUndoable
    {
        public ValueTask UndoAsync(CancellationToken ct)
        {
            foreach (var source in sources)
            {
                var name = Path.GetFileName(Path.GetFullPath(source));
                var moved = Path.Combine(destination, name);

                if (File.Exists(moved) || Directory.Exists(moved))
                    XdgTrash.MoveAcrossDevices(moved, source);
            }

            return ValueTask.CompletedTask;
        }
    }
}
