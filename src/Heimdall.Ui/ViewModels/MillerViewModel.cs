using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using Heimdall.Core.FileSystem;

namespace Heimdall.Ui.ViewModels;

/// <summary>
/// One column: a single directory level. Deliberately much lighter than
/// PaneViewModel — no history, no watcher, no filter. A deep path can mean
/// eight of these on screen, so they hold only what a column needs.
/// </summary>
public sealed partial class MillerColumnViewModel : ObservableObject, IDisposable
{
    private readonly IFileSystemProvider _fs;
    private readonly Func<bool> _showHidden;
    private readonly Comparison<FileEntry> _compare;
    private CancellationTokenSource? _cts;

    public MillerColumnViewModel(
        IFileSystemProvider fs, string path,
        Func<bool> showHidden, Comparison<FileEntry> compare)
    {
        _fs = fs;
        Path = path;
        _showHidden = showHidden;
        _compare = compare;
    }

    public string Path { get; }

    public string Label => System.IO.Path.GetFileName(Path.TrimEnd('/')) is { Length: > 0 } n
        ? n
        : "/";

    public BulkObservableCollection<FileEntry> Entries { get; } = new();

    [ObservableProperty] private FileEntry? _selectedEntry;
    [ObservableProperty] private bool _isLoading;

    /// <summary>Raised when the user picks something, not when we set it ourselves.</summary>
    public event EventHandler<MillerColumnViewModel>? SelectionChosen;

    private bool _settingSelection;

    partial void OnSelectedEntryChanged(FileEntry? value)
    {
        if (!_settingSelection) SelectionChosen?.Invoke(this, this);
    }

    /// <summary>Selects without treating it as a user action — used while
    /// rebuilding the chain, which would otherwise cascade.</summary>
    public void SelectQuietly(FileEntry? entry)
    {
        _settingSelection = true;
        try { SelectedEntry = entry; }
        finally { _settingSelection = false; }
    }

    public async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsLoading = true;

        var all = new List<FileEntry>();
        var options = new ListingOptions { IncludeHidden = _showHidden(), BatchSize = 500 };

        try
        {
            await foreach (var batch in _fs.EnumerateAsync(Path, options, ct).ConfigureAwait(false))
                all.AddRange(batch);

            all.Sort(_compare);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Entries.ReplaceAll(all);
                IsLoading = false;
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}

/// <summary>
/// Miller columns — one column per level of the path, selection in each driving
/// the next.
///
/// The important consequence is that you are viewing a *path chain* rather than
/// a directory, so "current folder" becomes the deepest selected directory.
/// That value is pushed back onto the pane, which is what lets every existing
/// operation — paste, new folder, rename, the whole context menu — keep working
/// without knowing this view exists.
/// </summary>
public sealed partial class MillerViewModel : ObservableObject
{
    private readonly IFileSystemProvider _fs;
    private readonly Func<bool> _showHidden;
    private readonly Comparison<FileEntry> _compare;
    private bool _building;

    public MillerViewModel(
        IFileSystemProvider fs, Func<bool> showHidden, Comparison<FileEntry> compare)
    {
        _fs = fs;
        _showHidden = showHidden;
        _compare = compare;
    }

    public ObservableCollection<MillerColumnViewModel> Columns { get; } = new();

    /// <summary>The deepest selected directory — what operations act on.</summary>
    public event EventHandler<string>? DirectoryChanged;

    /// <summary>The highlighted entry, so the pane's selection stays in step.</summary>
    public event EventHandler<FileEntry?>? EntryChanged;

    /// <summary>Rebuilds the whole chain from the filesystem root down to <paramref name="path"/>.</summary>
    public async Task ShowAsync(string path)
    {
        _building = true;
        try
        {
            Clear();

            // Every ancestor becomes a column, so you can step sideways out of
            // a deep path without navigating back up first.
            var levels = new List<string>();
            for (var current = path; !string.IsNullOrEmpty(current);
                 current = System.IO.Path.GetDirectoryName(current) ?? "")
            {
                levels.Add(current);
                if (current == "/") break;
            }

            levels.Reverse();
            if (levels.Count == 0 || levels[0] != "/") levels.Insert(0, "/");

            foreach (var level in levels)
            {
                var column = NewColumn(level);
                Columns.Add(column);
                await column.LoadAsync().ConfigureAwait(true);
            }

            // Highlight the child that continues the chain in each column.
            for (var i = 0; i < Columns.Count - 1; i++)
            {
                var childPath = Columns[i + 1].Path;
                var match = Columns[i].Entries.FirstOrDefault(e => e.FullPath == childPath);
                if (match.FullPath is not null) Columns[i].SelectQuietly(match);
            }
        }
        finally
        {
            _building = false;
        }

        DirectoryChanged?.Invoke(this, path);
    }

    private MillerColumnViewModel NewColumn(string path)
    {
        var column = new MillerColumnViewModel(_fs, path, _showHidden, _compare);
        column.SelectionChosen += OnSelectionChosen;
        return column;
    }

    private void OnSelectionChosen(object? sender, MillerColumnViewModel column)
    {
        if (_building) return;

        var index = Columns.IndexOf(column);
        if (index < 0) return;

        // Everything to the right of the changed column is now wrong.
        TruncateAfter(index);

        if (column.SelectedEntry is { IsDirectory: true } directory)
        {
            var next = NewColumn(directory.FullPath);
            Columns.Add(next);
            _ = next.LoadAsync();

            // A new column at the end of a deep chain is off-screen; the view
            // watches this to bring it into view.
            Focused = next;

            DirectoryChanged?.Invoke(this, directory.FullPath);
        }
        else
        {
            DirectoryChanged?.Invoke(this, column.Path);
        }

        EntryChanged?.Invoke(this, column.SelectedEntry);
    }

    private void TruncateAfter(int index)
    {
        while (Columns.Count > index + 1)
        {
            var last = Columns[^1];
            last.SelectionChosen -= OnSelectionChosen;
            last.Dispose();
            Columns.RemoveAt(Columns.Count - 1);
        }
    }

    /// <summary>
    /// Left and right move between columns, which is the whole keyboard idiom
    /// for this view — without it the chain can only be driven by mouse.
    /// Returns the column that should take focus, or null at either end.
    /// </summary>
    public MillerColumnViewModel? Step(MillerColumnViewModel from, int delta)
    {
        var index = Columns.IndexOf(from);
        if (index < 0) return null;

        var target = index + delta;
        if (target < 0 || target >= Columns.Count) return null;

        var column = Columns[target];

        // Moving right into a column with nothing chosen yet lands on its first
        // entry, so the chain keeps extending rather than stalling.
        if (delta > 0 && column.SelectedEntry is null && column.Entries.Count > 0)
            column.SelectedEntry = column.Entries[0];

        Focused = column;
        return column;
    }

    /// <summary>The column the keyboard is driving; the view scrolls it in.</summary>
    [ObservableProperty] private MillerColumnViewModel? _focused;

    public void Clear()
    {
        foreach (var column in Columns)
        {
            column.SelectionChosen -= OnSelectionChosen;
            column.Dispose();
        }

        Columns.Clear();
    }
}
