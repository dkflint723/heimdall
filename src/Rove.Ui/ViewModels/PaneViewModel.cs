using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Rove.Core.FileSystem;
using Rove.Core.Session;

namespace Rove.Ui.ViewModels;

/// <summary>
/// One pane: a path, its listing, its own navigation history, its own sort.
///
/// Everything about this class is self-contained on purpose. A tab is a pane, a
/// split view is two panes, a detached window is a pane — so tabs and splits
/// cost almost nothing, and the session model serialises one of these per tab
/// without reaching into the window.
/// </summary>
public sealed partial class PaneViewModel : ObservableObject, IDisposable
{
    private const int FlushIntervalMs = 100;

    private readonly IFileSystemProvider _fs;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private CancellationTokenSource? _cts;
    private bool _suppressReload;

    public PaneViewModel(IFileSystemProvider fs) => _fs = fs;

    public BulkObservableCollection<FileEntry> Entries { get; } = new();

    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _pathText = "";
    [ObservableProperty] private string _title = "…";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private FileEntry? _selectedEntry;
    [ObservableProperty] private SortField _sort = SortField.Name;
    [ObservableProperty] private bool _sortDescending;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => !string.IsNullOrEmpty(CurrentPath) && _fs.GetParent(CurrentPath) is not null;

    [RelayCommand]
    public async Task NavigateAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!string.IsNullOrEmpty(CurrentPath) && CurrentPath != path)
        {
            _back.Push(CurrentPath);
            _forward.Clear();
        }

        await LoadAsync(path).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task GoBackAsync()
    {
        if (!CanGoBack) return;
        _forward.Push(CurrentPath);
        await LoadAsync(_back.Pop()).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task GoForwardAsync()
    {
        if (!CanGoForward) return;
        _back.Push(CurrentPath);
        await LoadAsync(_forward.Pop()).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task GoUpAsync()
    {
        if (_fs.GetParent(CurrentPath) is { } parent)
            await NavigateAsync(parent).ConfigureAwait(false);
    }

    [RelayCommand]
    public Task OpenAsync(FileEntry entry)
        => entry.IsDirectory ? NavigateAsync(entry.FullPath) : Task.CompletedTask;

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync(CurrentPath);

    public Task OpenSelectedAsync()
        => SelectedEntry is { } entry ? OpenAsync(entry) : Task.CompletedTask;

    partial void OnCurrentPathChanged(string value)
    {
        var name = Path.GetFileName(value.TrimEnd('/'));
        Title = string.IsNullOrEmpty(name) ? "/" : name;
    }

    /// <summary>
    /// Restored tabs enumerate only when first activated. Recreating twenty
    /// tabs eagerly means twenty listings at startup, and one of them sitting
    /// on an unreachable share costs the whole window its SMB timeout.
    /// </summary>
    partial void OnIsActiveChanged(bool value)
    {
        if (value && !IsLoaded && !IsLoading && !string.IsNullOrEmpty(CurrentPath))
            _ = LoadAsync(CurrentPath);
    }

    /// <summary>
    /// Adopt persisted state without touching the filesystem. ShowHidden is set
    /// under suppression because its change handler triggers a reload, which is
    /// exactly what lazy restore is trying to avoid.
    /// </summary>
    public void RestoreFrom(TabState tab)
    {
        _suppressReload = true;
        try
        {
            CurrentPath = tab.Path;
            PathText = tab.Path;
            Sort = tab.Sort;
            SortDescending = tab.SortDescending;
            ShowHidden = tab.ShowHidden;

            _back.Clear();
            foreach (var p in tab.BackStack) _back.Push(p);

            _forward.Clear();
            foreach (var p in tab.ForwardStack) _forward.Push(p);
        }
        finally
        {
            _suppressReload = false;
        }

        IsLoaded = false;
        Status = "not loaded";
        NotifyNavigationState();
    }

    /// <summary>
    /// Load now if the pane was restored but never activated into a load.
    /// Start() assigns ActiveTab while change notifications are suppressed, so
    /// the usual activate-triggers-load path doesn't fire for it.
    /// </summary>
    public void RefreshIfUnloaded()
    {
        if (!IsLoaded && !IsLoading && !string.IsNullOrEmpty(CurrentPath))
            _ = LoadAsync(CurrentPath);
    }

    public TabState ToTabState() => new()
    {
        Path = CurrentPath,
        Sort = Sort,
        SortDescending = SortDescending,
        ShowHidden = ShowHidden,
        // Stacks serialise oldest-first so RestoreFrom can push in order.
        BackStack = _back.Reverse().ToList(),
        ForwardStack = _forward.Reverse().ToList(),
    };

    partial void OnShowHiddenChanged(bool value)
    {
        if (!_suppressReload) _ = LoadAsync(CurrentPath);
    }

    partial void OnSortChanged(SortField value)
    {
        if (!_suppressReload) ResortInPlace();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        if (!_suppressReload) ResortInPlace();
    }

    private async Task LoadAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // Cancelling the previous navigation is what stops a dead network path
        // from wedging the pane. It is not an optimisation.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        CurrentPath = path;
        PathText = path;
        IsLoading = true;
        Entries.Reset();
        NotifyNavigationState();

        var options = new ListingOptions { IncludeHidden = ShowHidden, BatchSize = 500 };

        var sw = Stopwatch.StartNew();
        var sinceFlush = Stopwatch.StartNew();
        var pending = new List<FileEntry>(4096);
        var count = 0;

        try
        {
            await foreach (var batch in _fs.EnumerateAsync(path, options, ct).ConfigureAwait(false))
            {
                pending.AddRange(batch);

                if (sinceFlush.ElapsedMilliseconds < FlushIntervalMs) continue;

                var flush = pending;
                pending = new List<FileEntry>(4096);
                sinceFlush.Restart();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Entries.AddRange(flush);
                    count += flush.Count;
                    Status = $"{count:N0} items…";
                });
            }

            if (pending.Count > 0)
            {
                var tail = pending;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Entries.AddRange(tail);
                    count += tail.Count;
                });
            }

            // Sorting happens once, after enumeration, rather than per batch.
            // Entries appear in readdir order while loading and settle when the
            // listing completes — which keeps first paint at a few milliseconds.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ResortInPlace();
                sw.Stop();
                Status = $"{count:N0} items · {sw.ElapsedMilliseconds:N0} ms";
                IsLoading = false;
                IsLoaded = true;
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation; the newer one owns the status.
        }
        catch (Exception ex)
        {
            // Dead paths stay visible with an explanation rather than being
            // dropped or silently redirected — silently dropping a restored tab
            // is what "it forgot" feels like.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Status = $"{ex.GetType().Name}: {ex.Message}";
                IsLoading = false;
            });
        }
    }

    private void ResortInPlace()
    {
        if (Entries.Count == 0) return;

        var items = Entries.ToList();
        items.Sort(Compare);
        Entries.ReplaceAll(items);
    }

    private int Compare(FileEntry a, FileEntry b)
    {
        // Directories first, always — the convention every file manager follows
        // and users notice immediately when it's missing.
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        // Span comparison rather than Extension.ToString(): sorting 200k entries
        // by kind would otherwise allocate a string per comparison, millions of
        // them for one sort.
        var result = Sort switch
        {
            SortField.Size     => a.Length.CompareTo(b.Length),
            SortField.Modified => a.LastWriteTime.CompareTo(b.LastWriteTime),
            SortField.Kind     => a.Extension.CompareTo(b.Extension, StringComparison.OrdinalIgnoreCase),
            _                  => 0,
        };

        if (result == 0)
            result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

        return SortDescending ? -result : result;
    }

    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
