using System.Collections.ObjectModel;
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
    private readonly IFileOperations? _ops;
    private readonly IApplicationLauncher? _launcher;
    private readonly IClipboardService? _clipboard;
    private readonly List<FileEntry> _all = new();
    private CancellationTokenSource? _filterDebounce;
    private IDisposable? _watcher;

    /// <summary>
    /// Incremented on every load. Watcher events capture it before going async
    /// and re-check it before touching the collections: an event that passes
    /// the IsLoading check, then gets delayed by an await, would otherwise land
    /// in the middle of a later listing and insert an entry the enumeration is
    /// about to add again.
    /// </summary>
    private int _generation;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private CancellationTokenSource? _cts;
    private bool _suppressReload;

    public PaneViewModel(
        IFileSystemProvider fs,
        IFileOperations? ops = null,
        IApplicationLauncher? launcher = null,
        IClipboardService? clipboard = null)
    {
        _fs = fs;
        _ops = ops;
        _launcher = launcher;
        _clipboard = clipboard;
    }

    public BulkObservableCollection<FileEntry> Entries { get; } = new();

    /// <summary>
    /// Bound to the list's SelectedItems. Operations act on this, not on
    /// SelectedEntry — deleting one of five selected files would be a nasty
    /// surprise.
    /// </summary>
    public ObservableCollection<FileEntry> SelectedEntries { get; } = new();

    /// <summary>Applications offered by the "open with" submenu.</summary>
    public ObservableCollection<LaunchOption> OpenWithOptions { get; } = new();

    /// <summary>Raised when an operation starts, so the shell can show progress.</summary>
    public event EventHandler<IOperationHandle>? OperationStarted;

    /// <summary>Raised when a rename is requested, so the view can prompt.</summary>
    public event EventHandler<FileEntry>? RenameRequested;

    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _pathText = "";
    [ObservableProperty] private string _title = "…";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private FileEntry? _selectedEntry;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isFilterVisible;
    [ObservableProperty] private SortField _sort = SortField.Name;
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private ViewMode _view = ViewMode.Details;

    /// <summary>Highlights the pane a drop would land in.</summary>
    [ObservableProperty] private bool _isDropTarget;

    // ---- dynamic columns -----------------------------------------------

    /// <summary>
    /// Set by the view as the pane resizes. Columns drop out in priority order
    /// as space runs out rather than being squeezed or clipped — which is what
    /// makes a narrow split pane still readable.
    /// </summary>
    [ObservableProperty] private double _viewportWidth = 1000;

    public bool ShowSize => ViewportWidth >= 340;
    public bool ShowModified => ViewportWidth >= 520;
    public bool ShowPermissions => ViewportWidth >= 660;
    public bool ShowMetadata => ViewportWidth >= 800;

    partial void OnViewportWidthChanged(double value)
    {
        OnPropertyChanged(nameof(ShowSize));
        OnPropertyChanged(nameof(ShowModified));
        OnPropertyChanged(nameof(ShowPermissions));
        OnPropertyChanged(nameof(ShowMetadata));
    }

    // ---- preview -------------------------------------------------------

    [ObservableProperty] private bool _isPreviewVisible;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _previewImage;
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewDetail = "";

    public bool HasPreviewImage => PreviewImage is not null;
    public bool HasPreviewText => PreviewText.Length > 0;

    private CancellationTokenSource? _previewCts;

    [RelayCommand]
    public void TogglePreview()
    {
        IsPreviewVisible = !IsPreviewVisible;
        if (IsPreviewVisible) _ = RefreshPreviewAsync();
    }

    partial void OnPreviewImageChanged(Avalonia.Media.Imaging.Bitmap? value)
        => OnPropertyChanged(nameof(HasPreviewImage));

    partial void OnPreviewTextChanged(string value)
        => OnPropertyChanged(nameof(HasPreviewText));

    private async Task RefreshPreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        PreviewImage = null;
        PreviewText = "";

        if (SelectedEntry is not { } entry)
        {
            PreviewTitle = "";
            PreviewDetail = "nothing selected";
            return;
        }

        PreviewTitle = entry.Name;
        PreviewDetail = entry.IsDirectory
            ? "folder"
            : $"{entry.Length:N0} bytes · {entry.LastWriteTime:yyyy-MM-dd HH:mm}";

        if (entry.IsDirectory) return;

        try
        {
            var bitmap = await Thumbnails.ThumbnailLoader
                .LoadAsync(entry.FullPath, 512, ct).ConfigureAwait(false);

            if (bitmap is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => PreviewImage = bitmap);
                return;
            }

            // Not an image — show the head of the file if it looks like text.
            // Capped hard: previewing a gigabyte log should cost the same as
            // previewing a config file.
            if (entry.Length is > 0 and < 8_000_000 && LooksTextual(entry.Name))
            {
                var text = await ReadHeadAsync(entry.FullPath, 4000, ct).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => PreviewText = text);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PreviewDetail = ex.Message);
        }
    }

    private static bool LooksTextual(string name)
    {
        var ext = Path.GetExtension(name);

        if (ext.Length == 0) return true;

        return ext.ToLowerInvariant() is
            ".txt" or ".md" or ".log" or ".json" or ".xml" or ".yaml" or ".yml" or
            ".cs" or ".py" or ".sh" or ".ps1" or ".c" or ".h" or ".cpp" or ".rs" or
            ".go" or ".js" or ".ts" or ".html" or ".css" or ".ini" or ".conf" or
            ".toml" or ".csv" or ".sql" or ".axaml" or ".xaml" or ".csproj" or ".props";
    }

    private static async Task<string> ReadHeadAsync(string path, int chars, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[chars];
        var read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false);
        return new string(buffer, 0, read);
    }

    public bool IsColumnsView => View == ViewMode.Columns;
    public bool IsDetailsView => View == ViewMode.Details;

    private MillerViewModel? _miller;

    /// <summary>Built lazily — a pane that never enters column view never pays for it.</summary>
    public MillerViewModel Miller => _miller ??= CreateMiller();

    private MillerViewModel CreateMiller()
    {
        var miller = new MillerViewModel(_fs, () => ShowHidden, Compare);

        // The chain reports its deepest selected directory, and that becomes
        // CurrentPath. Every existing operation — paste, new folder, rename,
        // the context menu — therefore keeps working without knowing this view
        // exists at all.
        miller.DirectoryChanged += (_, path) =>
        {
            if (CurrentPath == path) return;

            PathText = path;

            // LoadAsync would rebuild the chain we are currently inside, so the
            // listing is loaded directly. CurrentPath is set by it.
            _ = LoadListingAsync(path);
        };

        miller.EntryChanged += (_, entry) =>
        {
            SelectedEntry = entry;
            SelectedEntries.Clear();
            if (entry is { } value) SelectedEntries.Add(value);
        };

        return miller;
    }

    partial void OnViewChanged(ViewMode value)
    {
        OnPropertyChanged(nameof(IsColumnsView));
        OnPropertyChanged(nameof(IsDetailsView));

        if (_suppressReload) return;

        if (value == ViewMode.Columns)
            _ = Miller.ShowAsync(CurrentPath);
        else
            _miller?.Clear();
    }

    [RelayCommand]
    public void ToggleView()
        => View = View == ViewMode.Details ? ViewMode.Columns : ViewMode.Details;

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
    {
        if (entry.IsDirectory) return NavigateAsync(entry.FullPath);

        _launcher?.Open(entry.FullPath);
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> SelectionPaths()
        => SelectedEntries.Count > 0
            ? SelectedEntries.Select(e => e.FullPath).ToList()
            : SelectedEntry is { } one ? [one.FullPath] : [];

    private void Track(IOperationHandle handle)
    {
        OperationStarted?.Invoke(this, handle);

        // The listing is refreshed once, at the end — refreshing per item would
        // rebuild the view thousands of times during a large copy.
        _ = handle.Completion.ContinueWith(
            _ => Dispatcher.UIThread.Post(() => _ = RefreshAsync()),
            TaskScheduler.Default);
    }

    partial void OnSelectedEntryChanged(FileEntry? value)
    {
        if (IsPreviewVisible) _ = RefreshPreviewAsync();

        OpenWithOptions.Clear();
        if (_launcher is null || value is not { IsDirectory: false } entry) return;

        // Enumeration shells out to xdg-mime, so keep it off the UI thread.
        var path = entry.FullPath;
        _ = Task.Run(() =>
        {
            var options = _launcher.GetOpenWithOptions(path);
            Dispatcher.UIThread.Post(() =>
            {
                OpenWithOptions.Clear();
                foreach (var option in options) OpenWithOptions.Add(option);
            });
        });
    }

    [RelayCommand]
    public void OpenWithApp(LaunchOption? option)
    {
        if (option is null || SelectedEntry is not { } entry) return;
        _launcher?.OpenWith(entry.FullPath, option);
    }

    [RelayCommand]
    public void OpenTerminalHere() => _launcher?.OpenTerminal(CurrentPath);

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
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = ex.Message);
        }
    }

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

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync(CurrentPath);

    [RelayCommand]
    public Task OpenSelectedAsync()
        => SelectedEntry is { } entry ? OpenAsync(entry) : Task.CompletedTask;


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
            View = tab.View;

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
        View = View,
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

    /// <summary>
    /// Debounced because filtering rebuilds the visible collection, and doing
    /// that per keystroke on a 200k listing would stutter badly.
    /// </summary>
    partial void OnFilterTextChanged(string value)
    {
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _filterDebounce = new CancellationTokenSource();
        var ct = _filterDebounce.Token;

        _ = Task.Delay(120, ct).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(ApplyFilter);
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    public void ToggleFilter()
    {
        IsFilterVisible = !IsFilterVisible;
        if (!IsFilterVisible && FilterText.Length > 0) FilterText = "";
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(FilterText)
            ? _all
            : _all.Where(e => e.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)).ToList();

        var sorted = filtered.ToList();
        sorted.Sort(Compare);
        Entries.ReplaceAll(sorted);

        Status = filtered.Count == _all.Count
            ? $"{_all.Count:N0} items"
            : $"{filtered.Count:N0} of {_all.Count:N0} items";
    }

    partial void OnSortDescendingChanged(bool value)
    {
        if (!_suppressReload) ResortInPlace();
    }

    private async Task LoadAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        if (View == ViewMode.Columns)
            await Miller.ShowAsync(path).ConfigureAwait(false);

        await LoadListingAsync(path).ConfigureAwait(false);
    }

    private async Task LoadListingAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // Cancelling the previous navigation is what stops a dead network path
        // from wedging the pane. It is not an optimisation.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var generation = ++_generation;

        CurrentPath = path;
        PathText = path;
        IsLoading = true;
        _all.Clear();
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
                    _all.AddRange(flush);
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
                    _all.AddRange(tail);
                    Entries.AddRange(tail);
                    count += tail.Count;
                });
            }

            // Sorting happens once, after enumeration, rather than per batch.
            // Entries appear in readdir order while loading and settle when the
            // listing completes — which keeps first paint at a few milliseconds.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (FilterText.Length > 0) ApplyFilter(); else ResortInPlace();
                StartWatching(path);
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

    // ---- live updates --------------------------------------------------

    /// <summary>
    /// Watches the open directory so changes made by anything else — Dolphin,
    /// a terminal, a download finishing — appear without a manual refresh.
    /// Updates are applied entry by entry; re-enumerating on every event would
    /// throw away the whole point of streaming the listing in the first place.
    /// </summary>
    private void StartWatching(string path)
    {
        _watcher?.Dispose();
        _watcher = null;

        try
        {
            var generation = _generation;
            _watcher = _fs.Watch(path, change =>
                Dispatcher.UIThread.Post(() => ApplyChange(path, generation, change)));
        }
        catch
        {
            // A directory we cannot watch still lists fine; F5 remains.
        }
    }

    private void ApplyChange(string watchedPath, int generation, FileSystemChange change)
    {
        // Events can arrive after the user has navigated away, or mid-load.
        if (IsLoading || generation != _generation || CurrentPath != watchedPath) return;

        // Direct children only — nothing nested is on screen.
        if (Path.GetDirectoryName(change.Path) != watchedPath) return;

        switch (change.Kind)
        {
            case ChangeKind.Removed:
                RemoveByPath(change.Path);
                break;

            case ChangeKind.Renamed:
                if (change.OldPath is { } old) RemoveByPath(old);
                _ = AddOrUpdateAsync(change.Path, generation);
                break;

            default:
                _ = AddOrUpdateAsync(change.Path, generation);
                break;
        }
    }

    private void RemoveByPath(string path)
    {
        var masterIndex = _all.FindIndex(e => e.FullPath == path);
        if (masterIndex >= 0) _all.RemoveAt(masterIndex);

        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].FullPath != path) continue;
            Entries.RemoveAt(i);
            break;
        }

        UpdateCountStatus();
    }

    private async Task AddOrUpdateAsync(string path, int generation)
    {
        var name = Path.GetFileName(path);
        if (!ShowHidden && name.StartsWith('.')) return;

        var entry = await _fs.GetEntryAsync(path, CancellationToken.None).ConfigureAwait(false);
        if (entry is not { } value) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Re-checked after the await: a listing may have started while we
            // were off fetching the entry, and inserting into it would duplicate
            // whatever the enumeration is about to produce.
            if (IsLoading || generation != _generation) return;

            RemoveByPathSilently(path);

            var masterAt = FindSortedIndex(_all, value);
            _all.Insert(masterAt, value);

            if (MatchesFilter(value))
            {
                var visibleAt = FindSortedIndex(Entries, value);
                Entries.Insert(visibleAt, value);
            }

            UpdateCountStatus();
        });
    }

    private void RemoveByPathSilently(string path)
    {
        var masterIndex = _all.FindIndex(e => e.FullPath == path);
        if (masterIndex >= 0) _all.RemoveAt(masterIndex);

        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].FullPath != path) continue;
            Entries.RemoveAt(i);
            break;
        }
    }

    private bool MatchesFilter(FileEntry entry)
        => string.IsNullOrWhiteSpace(FilterText)
           || entry.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    /// <summary>Binary search for the insertion point under the current sort,
    /// so a new file lands where it belongs instead of forcing a re-sort.</summary>
    private int FindSortedIndex(IList<FileEntry> list, FileEntry entry)
    {
        int low = 0, high = list.Count;

        while (low < high)
        {
            var mid = (low + high) / 2;
            if (Compare(list[mid], entry) < 0) low = mid + 1;
            else high = mid;
        }

        return low;
    }

    private void UpdateCountStatus()
        => Status = Entries.Count == _all.Count
            ? $"{_all.Count:N0} items"
            : $"{Entries.Count:N0} of {_all.Count:N0} items";

    private void ResortInPlace()
    {
        if (Entries.Count == 0) return;

        var items = _all.Count > 0 ? _all.ToList() : Entries.ToList();
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
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _miller?.Clear();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _watcher?.Dispose();
        _watcher = null;
    }
}
