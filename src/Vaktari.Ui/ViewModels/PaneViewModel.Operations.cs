using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// What a pane does TO files, as opposed to how it shows them: the clipboard,
/// paste, create, rename, trash, delete and undo.
/// </summary>
public sealed partial class PaneViewModel
{
    // Self-contained: the pane owns its clipboard rather than raising an event
    // for the window to service. The old chain had three links and no way to
    // tell which one had broken when copy silently did nothing.

    [RelayCommand]
    public Task CopySelectionToClipboardAsync() => WriteClipboardAsync(ClipboardAction.Copy);

    [RelayCommand]
    public Task CutSelectionToClipboardAsync() => WriteClipboardAsync(ClipboardAction.Cut);

    private async Task WriteClipboardAsync(ClipboardAction action)
    {
        if (_clipboard is null) { Status = "clipboard unavailable"; return; }

        var paths = SelectionPaths();
        if (paths.Count == 0) { Status = "nothing selected"; return; }

        try
        {
            var ok = await _clipboard.SetFilesAsync(action, paths).ConfigureAwait(false);
            var verb = action == ClipboardAction.Cut ? "cut" : "copied";

            await Dispatcher.UIThread.InvokeAsync(() =>
                Status = ok ? $"{paths.Count} item(s) {verb}" : "clipboard unavailable");
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"copy failed: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task PasteAsync()
    {
        if (_clipboard is null) { Status = "clipboard unavailable"; return; }

        try
        {
            var payload = await _clipboard.GetFilesAsync().ConfigureAwait(false);

            if (payload is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Status = "clipboard has no files");
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                PasteInto(payload.Paths, payload.Action == ClipboardAction.Cut));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"paste failed: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task NewFolderAsync()
    {
        var baseName = Path.Combine(CurrentPath, "New folder");
        var target = Directory.Exists(baseName) ? XdgDeduplicate(baseName) : baseName;

        try
        {
            Directory.CreateDirectory(target);
            await RefreshAsync().ConfigureAwait(true);

            // Straight into rename — the same hand-off NewFromTemplateAsync has
            // always done, and for the same reason: "New folder" is a placeholder
            // nobody wants to keep, and making them find it and press F2 is a
            // second step for something they already told us they were doing.
            BeginRenameOf(target);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = ex.Message);
        }
    }

    /// <summary>Selects a freshly created path and opens the rename prompt on
    /// it. Shared by new folder, new file and new-from-template.</summary>
    private void BeginRenameOf(string path)
    {
        var created = _all.FirstOrDefault(e => e.FullPath == path);

        if (created.FullPath is not null) RenameRequested?.Invoke(this, created);
    }

    /// <summary>
    /// Creates an empty file of the chosen kind and renames it immediately.
    /// </summary>
    [RelayCommand]
    public async Task NewFileAsync(NewFileKind? kind)
    {
        if (kind is null) return;

        try
        {
            var target = Path.Combine(CurrentPath, "New file" + kind.Extension);
            var unique = target;
            var counter = 2;

            while (File.Exists(unique) || Directory.Exists(unique))
            {
                unique = Path.Combine(CurrentPath,
                    $"New file {counter++}{kind.Extension}");
            }

            await Task.Run(() => File.Create(unique).Dispose()).ConfigureAwait(true);

            // A script nobody can run is half a file. Guarded because
            // SetUnixFileMode throws on Windows rather than being ignored.
            if (kind.Executable && OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(unique,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            await RefreshAsync().ConfigureAwait(true);

            BeginRenameOf(unique);
        }
        catch (Exception ex)
        {
            Status = $"could not create file: {ex.Message}";
        }
    }

    /// <summary>The built-in kinds, for the menu.</summary>
    public IReadOnlyList<NewFileKind> NewFileKinds => FileKinds.Common;

    private static string XdgDeduplicate(string path)
    {
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{path} {i}";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        return path + " " + Guid.NewGuid().ToString("N")[..6];
    }

    /// <summary>Copy or move into a specific folder — used when a drop lands on
    /// a folder row rather than on the listing's background.</summary>
    public void PasteIntoFolder(string destination, IReadOnlyList<string> paths, bool move)
    {
        if (_ops is null || paths.Count == 0) return;

        var handle = move
            ? _ops.Move(paths, destination, _ => ValueTask.FromResult(ConflictResolution.KeepBoth))
            : _ops.Copy(paths, destination, _ => ValueTask.FromResult(ConflictResolution.KeepBoth));

        Track(handle);
    }

    /// <summary>Runs a copy or move into this directory, from the view's paste.</summary>
    public void PasteInto(IReadOnlyList<string> paths, bool move)
    {
        if (_ops is null || paths.Count == 0) return;

        var handle = move
            ? _ops.Move(paths, CurrentPath, _ => ValueTask.FromResult(ConflictResolution.KeepBoth))
            : _ops.Copy(paths, CurrentPath, _ => ValueTask.FromResult(ConflictResolution.KeepBoth));

        Track(handle);
    }

    /// <summary>Delete key. Recoverable, so no confirmation prompt.</summary>
    [RelayCommand]
    public void TrashSelected()
    {
        if (_ops is null) return;

        var paths = SelectionPaths();
        if (paths.Count == 0) return;

        Track(_ops.Trash(paths));
    }

    /// <summary>Shift+Delete. Irreversible — the view must confirm first.</summary>
    [RelayCommand]
    public void DeleteSelected()
    {
        if (_ops is null) return;

        var paths = SelectionPaths();
        if (paths.Count == 0) return;

        Track(_ops.Delete(paths));
    }

    [RelayCommand]
    public void BeginRename()
    {
        if (SelectedEntry is { } entry) RenameRequested?.Invoke(this, entry);
    }

    public async Task RenameAsync(FileEntry entry, string newName)
    {
        if (_ops is null) return;

        try
        {
            await _ops.RenameAsync(entry.FullPath, newName, CancellationToken.None)
                      .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => _ = RefreshAsync());
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = ex.Message);
        }
    }

    [RelayCommand]
    public async Task UndoAsync()
    {
        if (_ops is null || !_ops.CanUndo) return;

        try
        {
            await _ops.UndoAsync(CancellationToken.None).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => _ = RefreshAsync());
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = ex.Message);
        }
    }

    [RelayCommand]
    public void DuplicateSelected()
    {
        if (_ops is null) return;

        var paths = SelectionPaths();
        if (paths.Count == 0) { Status = "select something to duplicate"; return; }

        Track(_ops.Copy(paths, CurrentPath,
            _ => ValueTask.FromResult(ConflictResolution.KeepBoth)));
    }
}
