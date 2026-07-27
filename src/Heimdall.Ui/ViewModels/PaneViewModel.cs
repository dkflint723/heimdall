using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Heimdall.Core;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Session;

namespace Heimdall.Ui.ViewModels;

/// <summary>One clickable ancestor in the breadcrumb bar.</summary>
public sealed record PathSegment(string Name, string FullPath, ICommand Open, bool IsLast);

/// <summary>A tag in the context menu, carrying the command that applies it.</summary>
public sealed record TagOption(string Name, ICommand Command);

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
    private readonly IScriptRunner? _scripts;
    private readonly ITagStore? _tags;
    private readonly ITemplateProvider? _templates;
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
        IClipboardService? clipboard = null,
        IScriptRunner? scripts = null,
        ITagStore? tags = null,
        ITemplateProvider? templates = null)
    {
        WatchSelections();

        _tags = tags;
        _templates = templates;
        _fs = fs;
        _ops = ops;
        _launcher = launcher;
        _clipboard = clipboard;
        _scripts = scripts;

        RefreshScripts();
        RefreshTags();
        RefreshTemplates();
    }

    // ---- tags ----------------------------------------------------------

    /// <summary>
    /// Tags offered in the menu. Each option carries its own command rather
    /// than relying on a Style setter that walks up to the window: bindings
    /// inside a ContextMenu are not compile-checked, so the less they depend
    /// on, the fewer ways they fail silently.
    /// </summary>
    public ObservableCollection<TagOption> KnownTags { get; } = new();

    public bool HasTagStore => _tags is not null;

    /// <summary>Asks the view to prompt for a new tag name.</summary>
    public event EventHandler? NewTagRequested;

    [RelayCommand]
    public void RefreshTags()
    {
        KnownTags.Clear();
        if (_tags is null) return;

        foreach (var tag in _tags.KnownTags)
            KnownTags.Add(new TagOption(tag, new RelayCommand(() => _ = ToggleTagAsync(tag))));
    }

    [RelayCommand]
    public void BeginNewTag()
    {
        // Checked here so the reason is stated up front, rather than the prompt
        // opening, being filled in, and quietly doing nothing.
        if (SelectionPaths().Count == 0)
        {
            Status = "select something to tag first";
            return;
        }

        NewTagRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Adds the tag when any selected file lacks it, removes it when they all
    /// have it — so one menu entry both tags and untags, and a mixed selection
    /// resolves toward tagging rather than silently clearing.
    /// </summary>
    public async Task ToggleTagAsync(string tag)
    {
        if (_tags is null || string.IsNullOrWhiteSpace(tag)) return;

        var paths = SelectionPaths();
        if (paths.Count == 0) { Status = "nothing selected"; return; }

        try
        {
            var store = _tags;

            var add = await Task.Run(() =>
            {
                foreach (var path in paths)
                {
                    var existing = store.GetAsync(path, CancellationToken.None)
                                        .AsTask().GetAwaiter().GetResult();

                    if (!existing.Contains(tag, StringComparer.OrdinalIgnoreCase)) return true;
                }

                return false;
            }).ConfigureAwait(false);

            await _tags.ToggleAsync(paths, tag, add, CancellationToken.None).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RefreshTags();
                Status = $"{(add ? "tagged" : "untagged")} {paths.Count} item(s): {tag}";
                _ = RefreshAsync();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"tag failed: {ex.Message}");
        }
    }



    // ---- user scripts --------------------------------------------------

    public ObservableCollection<ScriptCommand> Scripts { get; } = new();

    public bool HasScripts => Scripts.Count > 0;

    [RelayCommand]
    public void RefreshScripts()
    {
        Scripts.Clear();
        if (_scripts is null) return;

        foreach (var script in _scripts.Discover()) Scripts.Add(script);
        OnPropertyChanged(nameof(HasScripts));
    }

    [RelayCommand]
    public void OpenScriptsFolder()
    {
        if (_scripts is not null) _launcher?.Open(_scripts.ScriptsDirectory);
    }

    [RelayCommand]
    public async Task RunScriptAsync(ScriptCommand? script)
    {
        if (_scripts is null || script is null) return;

        Status = $"running {script.Name}…";

        try
        {
            var output = await _scripts
                .RunAsync(script, CurrentPath, SelectionPaths(), CancellationToken.None)
                .ConfigureAwait(false);

            // The watcher picks up whatever the script changed on disk, so the
            // listing does not need refreshing here.
            await Dispatcher.UIThread.InvokeAsync(() => Status = output);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"{script.Name}: {ex.Message}");
        }
    }

    public BulkObservableCollection<FileEntry> Entries { get; } = new();

    /// <summary>
    /// Each layout sees the entries only while it is the one on screen.
    ///
    /// Grid and compact use a WrapPanel, which Avalonia has no virtualizing
    /// form of, so every item they are given is realized — and all three lists
    /// stay alive when hidden. Binding all of them to Entries meant opening a
    /// large folder realized a container per file in TWO invisible layouts,
    /// which is exactly the cost the streaming enumerator exists to avoid.
    ///
    /// The inactive ones get an empty array: no items, no containers, no
    /// change notifications.
    /// </summary>
    private static readonly FileEntry[] NoEntries = [];

    public IEnumerable<FileEntry> DetailsEntries
        => View == ViewMode.Details ? Entries : NoEntries;

    public IEnumerable<FileEntry> GridEntries
        => View == ViewMode.Grid ? Entries : NoEntries;

    public IEnumerable<FileEntry> CompactEntries
        => View == ViewMode.Compact ? Entries : NoEntries;

    /// <summary>
    /// Above this, the un-virtualized layouts are refused rather than allowed
    /// to hang the app.
    ///
    /// WrapPanel realizes a container per item and Avalonia has no virtualizing
    /// wrap panel, so switching to grid on a large folder freezes the process
    /// outright. Refusing is ugly; truncating the listing would be worse — a
    /// file manager that silently omits files is actively dangerous, and you
    /// would have no way to know it had.
    ///
    /// Details view is virtualized and always available, so nothing becomes
    /// unreachable.
    /// </summary>
    /// <summary>
    /// Per-folder view overrides. Null until the shell supplies one.
    /// </summary>
    public static IFolderViewStore? FolderViews { get; set; }

    /// <summary>
    /// Applied on arrival, before the listing is asked for, so the folder is
    /// enumerated and sorted once under its own rules rather than sorted twice.
    /// Silent when the preference is off or the folder has no opinion.
    /// </summary>
    private void ApplyFolderView(string path)
    {
        if (!Settings.AppSettings.Current.General.RememberViewPerFolder) return;
        if (FolderViews?.Read(path) is not { } view) return;

        View = view.View;
        Sort = view.Sort;
        SortDescending = view.SortDescending;
        GroupBy = view.GroupBy;

        // Zero means the folder expressed no opinion about scale, so the pane
        // keeps whatever it had — scale is an accessibility setting and a
        // folder must not be able to shrink someone's text.
        if (view.FontScale > 0) FontScale = view.FontScale;
        if (view.IconScale > 0) IconScale = view.IconScale;
    }

    /// <summary>
    /// Records the current view against the current folder. Called when the
    /// user changes one of these, never on arrival — otherwise merely visiting
    /// a folder would give it an opinion it never had.
    /// </summary>
    public void RememberFolderView()
    {
        if (!Settings.AppSettings.Current.General.RememberViewPerFolder) return;
        if (FolderViews is null || string.IsNullOrEmpty(CurrentPath)) return;
        if (_restoringView) return;

        FolderViews.Write(CurrentPath, new FolderViewState
        {
            View = View,
            Sort = Sort,
            SortDescending = SortDescending,
            GroupBy = GroupBy,
            FontScale = FontScale,
            IconScale = IconScale,
        });
    }

    private bool _restoringView;

    public const int UnvirtualizedLimit = 5000;

    public bool CanUseTileLayouts => Entries.Count <= EffectiveTileLimit;

    /// <summary>
    /// 5,000 was a stopgap guess and has never been measured. Override it to
    /// find the real threshold on this machine:
    ///
    ///   HEIMDALL_TILE_LIMIT=20000 dotnet run --project src/Heimdall.Ui
    ///
    /// then switch to grid in a folder of that size and watch the
    /// `[heimdall] tiles:` line for how long realization actually took. The
    /// limit should be a number somebody measured, not one somebody feared.
    /// </summary>
    private static readonly int EffectiveTileLimit =
        int.TryParse(Environment.GetEnvironmentVariable("HEIMDALL_TILE_LIMIT"), out var limit)
        && limit > 0
            ? limit
            : UnvirtualizedLimit;

    private void NotifyLayoutEntries()
    {
        OnPropertyChanged(nameof(DetailsEntries));
        OnPropertyChanged(nameof(GridEntries));
        OnPropertyChanged(nameof(CompactEntries));
    }

    /// <summary>
    /// One selection collection PER LAYOUT, and the reason is not cosmetic.
    ///
    /// Details, grid and compact are three separate ListBoxes that all stay
    /// alive when hidden. Pointing their SelectedItems at a single shared
    /// collection made each one write its own idea of the selection into it, so
    /// clicking one row produced a union of whatever the other two still held —
    /// three different files selected from one click. Deduplicating does not
    /// help, because the entries genuinely differ.
    ///
    /// Separate collections mean a hidden list can only ever disturb its own.
    /// </summary>
    public ObservableCollection<FileEntry> DetailsSelection { get; } = new();
    public ObservableCollection<FileEntry> GridSelection { get; } = new();
    public ObservableCollection<FileEntry> CompactSelection { get; } = new();

    /// <summary>
    /// Subscribes to all three, not just the active one — a hidden list can
    /// still be told to sync, and the active one changes as the layout does.
    /// Without this nothing recomputed the selection count, which is why the
    /// status bar reported only the item total.
    /// </summary>
    private void WatchSelections()
    {
        DetailsSelection.CollectionChanged += (_, _) => NotifySelectionChanged();
        GridSelection.CollectionChanged += (_, _) => NotifySelectionChanged();
        CompactSelection.CollectionChanged += (_, _) => NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>The collection belonging to the layout currently on screen.</summary>
    public ObservableCollection<FileEntry> SelectedEntries => View switch
    {
        ViewMode.Grid => GridSelection,
        ViewMode.Compact => CompactSelection,
        _ => DetailsSelection,
    };

    /// <summary>What everything else should read. Never the raw collections.</summary>
    public IReadOnlyList<FileEntry> Selection => SelectedEntries.ToList();

    /// <summary>
    /// Carries the selection to the layout being switched to, so changing view
    /// does not silently drop what you had chosen.
    /// </summary>
    private void CarrySelection(ViewMode from, ViewMode to)
    {
        if (from == to) return;

        var source = from switch
        {
            ViewMode.Grid => GridSelection,
            ViewMode.Compact => CompactSelection,
            _ => DetailsSelection,
        };

        var target = to switch
        {
            ViewMode.Grid => GridSelection,
            ViewMode.Compact => CompactSelection,
            _ => DetailsSelection,
        };

        var carried = source.ToList();

        target.Clear();
        foreach (var entry in carried) target.Add(entry);

        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(Summary));
    }

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

    /// <summary>Free space on the filesystem holding this folder — Dolphin
    /// keeps it in the status bar and it is genuinely useful there.</summary>
    [ObservableProperty] private string _freeSpace = "";

    /// <summary>
    /// Off the UI thread: DriveInfo stats the filesystem, and on an unreachable
    /// NFS or SMB mount that blocks for the mount timeout — which would freeze
    /// the window on every navigation into it.
    /// </summary>
    private async Task RefreshFreeSpaceAsync(string path)
    {
        string text;

        try
        {
            text = await Task.Run(() =>
            {
                var drive = new DriveInfo(path);
                return $"{ByteSize.Format(drive.AvailableFreeSpace)} free";
            }).ConfigureAwait(false);
        }
        catch
        {
            text = "";
        }

        // Discard if we have navigated on since.
        if (CurrentPath != path) return;

        await Dispatcher.UIThread.InvokeAsync(() => FreeSpace = text);
    }

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

    /// <summary>
    /// Set from the window's UI scale. Column content grows with the type
    /// scale, so the widths at which columns stop fitting have to grow with it
    /// too — fixed thresholds meant that at 2x every column still claimed to
    /// fit while overflowing the pane.
    /// </summary>
    /// <summary>
    /// The text scale, pushed in by the shell. Column thresholds are about how
    /// much room *text* needs, so they follow the font axis — not the icon one.
    /// This was left orphaned by the font/icon split and nothing assigned it,
    /// so the thresholds silently stopped following the text size.
    /// </summary>
    [ObservableProperty] private double _textScale = 1.0;

    /// <summary>
    /// Type and icon scale for THIS pane. Per tab and per split side, because a
    /// reference listing beside a working one wants different sizes — which is
    /// the whole reason for having two panes.
    /// </summary>
    [ObservableProperty] private double _fontScale = 1.0;
    [ObservableProperty] private double _iconScale = 1.0;

    /// <summary>
    /// The bases the scales multiply. Exposed as real sizes rather than as a
    /// multiplier, because "14" is something a person can reason about and
    /// "1.15" is not.
    /// </summary>
    private const double BaseFontSize = 14;
    private const double BaseIconSize = 26;

    private const double MinScale = 0.7;
    private const double MaxScale = 2.5;

    /// <summary>
    /// What "share" would act on: the selected folder, or this one. Shown in
    /// the menu so the target is visible before clicking rather than inferred
    /// from the result afterwards.
    /// </summary>
    public string ShareTargetLabel
    {
        get
        {
            var name = SelectedEntry is { IsDirectory: true } selected
                ? selected.Name
                : Path.GetFileName(CurrentPath.TrimEnd('/'));

            return string.IsNullOrEmpty(name) ? "this folder" : name;
        }
    }

    public double FontPoints
    {
        get => Math.Round(FontScale * BaseFontSize);
        set => FontScale = Math.Clamp(value / BaseFontSize, MinScale, MaxScale);
    }

    public double IconPixels
    {
        get => Math.Round(IconScale * BaseIconSize);
        set => IconScale = Math.Clamp(value / BaseIconSize, MinScale, MaxScale);
    }

    partial void OnFontScaleChanged(double value)
    {
        OnPropertyChanged(nameof(FontPoints));
        // Column thresholds are measured in text width, so they follow the font
        // axis of the pane they belong to.
        TextScale = value;
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIconScaleChanged(double value)
    {
        OnPropertyChanged(nameof(IconPixels));
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised so the shell can persist the change.</summary>
    public event EventHandler? ScaleChanged;

    public bool ShowSize => ViewportWidth >= 340 * TextScale;
    public bool ShowModified => ViewportWidth >= 520 * TextScale;
    public bool ShowPermissions => ViewportWidth >= 680 * TextScale;
    public bool ShowMetadata => ViewportWidth >= 840 * TextScale;

    partial void OnTextScaleChanged(double value) => NotifyColumns();

    private void NotifyColumns()
    {
        OnPropertyChanged(nameof(ShowSize));
        OnPropertyChanged(nameof(ShowModified));
        OnPropertyChanged(nameof(ShowPermissions));
        OnPropertyChanged(nameof(ShowMetadata));
    }

    partial void OnViewportWidthChanged(double value) => NotifyColumns();

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

    /// <summary>An empty listing used to look identical to one still loading.</summary>
    public bool IsEmpty => IsLoaded && !IsLoading && Entries.Count == 0;

    /// <summary>Stable left-hand status: what is here and what is picked.</summary>
    /// <summary>
    /// Total size of the selection, so the status bar can report it the way
    /// Dolphin does. Directories contribute nothing — measuring them would mean
    /// walking the tree on every selection change.
    /// </summary>
    private string SelectionSize()
    {
        long total = 0;
        var files = 0;

        foreach (var entry in Selection)
        {
            if (entry.IsDirectory) continue;
            total += entry.Length;
            files++;
        }

        return files == 0 ? "" : $" ({ByteSize.Format(total)})";
    }

    public string Summary => Selection.Count switch
    {
        0 => $"{Entries.Count:N0} items",
        1 => $"{Entries.Count:N0} items · 1 selected{SelectionSize()}",
        var n => $"{Entries.Count:N0} items · {n:N0} selected{SelectionSize()}",
    };

    private void NotifyListingState()
    {
        OnPropertyChanged(nameof(CanUseTileLayouts));

        // Navigating into a huge folder while already in grid would hang just
        // as hard as switching into it. Drop back to the virtualized layout and
        // say why, rather than freezing.
        if (!CanUseTileLayouts && View != ViewMode.Details)
        {
            View = ViewMode.Details;
            // EffectiveTileLimit, not the constant: with HEIMDALL_TILE_LIMIT set
            // this message otherwise reports a limit that is not the one being
            // enforced, which is worse than saying nothing.
            Status = $"switched to list view — {Entries.Count:N0} items is beyond "
                   + $"the {EffectiveTileLimit:N0} limit for tile layouts";
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ShareTargetLabel));
    }

    public bool IsDetailsView => View == ViewMode.Details;
    public bool IsGridView => View == ViewMode.Grid;
    public bool IsCompactView => View == ViewMode.Compact;

    /// <summary>The chain is orthogonal to the layout — it can sit above either.</summary>
    [ObservableProperty] private bool _showColumnStrip;

    partial void OnShowColumnStripChanged(bool value)
    {
        if (_suppressReload) return;

        if (value) _ = Miller.ShowAsync(CurrentPath);
        else _miller?.Clear();
    }

    [RelayCommand]
    public void ToggleColumnStrip() => ShowColumnStrip = !ShowColumnStrip;

    [RelayCommand]
    public void ShowAsDetails() => View = ViewMode.Details;

    [RelayCommand]
    public void ShowAsGrid() => TrySetTileLayout(ViewMode.Grid, "grid");

    /// <summary>
    /// Dolphin's third mode: names only, flowing down and wrapping into
    /// columns. The point is density — it fits several times as many entries on
    /// screen as either other layout, which is what you want when you are
    /// looking for a name rather than inspecting files.
    /// </summary>
    [RelayCommand]
    public void ShowAsCompact() => TrySetTileLayout(ViewMode.Compact, "compact");

    /// <summary>
    /// Refuses rather than hangs. The message names the real reason and the
    /// number, so it reads as a known limit and not a malfunction.
    /// </summary>
    private void TrySetTileLayout(ViewMode mode, string label)
    {
        if (!CanUseTileLayouts)
        {
            Status = $"{label} view is limited to {UnvirtualizedLimit:N0} items "
                   + $"— this folder has {Entries.Count:N0}. Use list view.";
            return;
        }

        View = mode;
    }

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
            var active = SelectedEntries;

            active.Clear();
            if (entry is { } value) active.Add(value);
        };

        return miller;
    }

    partial void OnViewChanged(ViewMode oldValue, ViewMode newValue)
    {
        // Timed because the un-virtualized layouts realize a container per
        // item, and how bad that is at a given count is the one number the
        // guard above should be set from.
        var realizeWatch = newValue != ViewMode.Details && Entries.Count > 1000
            ? System.Diagnostics.Stopwatch.StartNew()
            : null;

        if (realizeWatch is not null)
            Dispatcher.UIThread.Post(() =>
            {
                realizeWatch.Stop();
                Console.Error.WriteLine(
                    $"[heimdall] tiles: {newValue} with {Entries.Count:N0} items "
                    + $"realized in {realizeWatch.ElapsedMilliseconds} ms "
                    + $"(limit {EffectiveTileLimit:N0})");
            }, DispatcherPriority.Background);

        // Populate the incoming layout FIRST. Its ListBox cannot hold a
        // selection for items it does not yet have, so carrying the selection
        // before the items exist would silently drop it.
        NotifyLayoutEntries();

        CarrySelection(oldValue, newValue);

        OnPropertyChanged(nameof(IsDetailsView));
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsCompactView));
        OnPropertyChanged(nameof(SelectedEntries));
    
        RememberFolderView();
    }

    [RelayCommand]
    public void ToggleView()
        => View = View == ViewMode.Details ? ViewMode.Grid : ViewMode.Details;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => !string.IsNullOrEmpty(CurrentPath) && _fs.GetParent(CurrentPath) is not null;

    [RelayCommand]
    public async Task NavigateAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Already here, already loaded: do nothing at all.
        //
        // Reloading tore the listing down and rebuilt it — and because entries
        // paint in readdir order and only sort once enumeration finishes, the
        // rebuild flashed the same files in filesystem order before they
        // settled. Clicking a place you are already viewing looked like the
        // folder briefly changed. Refreshing on purpose is F5's job; a
        // navigation to where you already are is not a request to refresh.
        if (IsLoaded && !IsLoading
            && string.Equals(CurrentPath, path, StringComparison.Ordinal))
            return;

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

    /// <summary>Paths of the selection, falling back to the focused row.</summary>
    public IReadOnlyList<string> SelectionPaths()
        => Selection.Count > 0
            ? Selection.Select(e => e.FullPath).ToList()
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
        // CurrentPath is assigned from LoadListingAsync after a ConfigureAwait,
        // so this runs on a pool thread. Breadcrumbs is bound to the UI, and
        // mutating it from here is a crash waiting for a slow directory.
        Dispatcher.UIThread.Post(RebuildBreadcrumbs);
        _ = RefreshFreeSpaceAsync(value);

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
            ShowColumnStrip = tab.ShowColumnStrip;
            GroupBy = tab.GroupBy;

            // Guarded: a session written before these existed deserialises as
            // 0, which would restore an invisible pane.
            FontScale = tab.FontScale > 0 ? tab.FontScale : 1.0;
            IconScale = tab.IconScale > 0 ? tab.IconScale : 1.0;

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
        ShowColumnStrip = ShowColumnStrip,
        GroupBy = GroupBy,
        FontScale = FontScale,
        IconScale = IconScale,
        // Stacks serialise oldest-first so RestoreFrom can push in order.
        BackStack = _back.Reverse().ToList(),
        ForwardStack = _forward.Reverse().ToList(),
    };

    partial void OnShowHiddenChanged(bool value)
    {
        if (!_suppressReload) _ = LoadAsync(CurrentPath);
    }

    partial void OnIsLoadedChanged(bool value) => NotifyListingState();
    partial void OnIsLoadingChanged(bool value) => NotifyListingState();

    partial void OnSortChanged(SortField value)
    {
        NotifySortGlyphs();
        if (!_suppressReload) ResortInPlace();
    
        RememberFolderView();
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

    // ---- sorting -------------------------------------------------------

    /// <summary>
    /// Click a column heading to sort by it; click again to reverse. The sort
    /// state was implemented, persisted per tab and completely unreachable —
    /// there was no control anywhere that set it.
    /// </summary>
    [RelayCommand]
    public void SortBy(string? field)
    {
        var target = field switch
        {
            "size" => SortField.Size,
            "modified" => SortField.Modified,
            "kind" => SortField.Kind,
            _ => SortField.Name,
        };

        if (Sort == target) SortDescending = !SortDescending;
        else { Sort = target; SortDescending = false; }

        NotifySortGlyphs();
    }

    private string Glyph(SortField field)
        => Sort != field ? "" : SortDescending ? " \u25BE" : " \u25B4";

    public string NameSortGlyph => Glyph(SortField.Name);
    public string SizeSortGlyph => Glyph(SortField.Size);
    public string ModifiedSortGlyph => Glyph(SortField.Modified);

    private void NotifySortGlyphs()
    {
        OnPropertyChanged(nameof(IsSortedByName));
        OnPropertyChanged(nameof(IsSortedBySize));
        OnPropertyChanged(nameof(IsSortedByModified));
        OnPropertyChanged(nameof(IsSortedByKind));

        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(ModifiedSortGlyph));
    }

    // ---- breadcrumbs ---------------------------------------------------

    /// <summary>
    /// The path as clickable ancestors, Dolphin-style. Navigating two levels up
    /// is one click rather than two, and the shape of the location is readable
    /// without parsing a string.
    /// </summary>
    public ObservableCollection<PathSegment> Breadcrumbs { get; } = new();

    /// <summary>Swaps the crumbs for an editable box — Ctrl+L, or clicking the
    /// empty space beside them, exactly as Dolphin does it.</summary>
    [ObservableProperty] private bool _isPathEditing;

    private readonly PathCompleter _completer = new();

    /// <summary>
    /// Extends the typed path to the next matching folder. Bound to Tab while
    /// the path box is open.
    /// </summary>
    [RelayCommand]
    public void CompletePath()
    {
        if (!IsPathEditing) return;

        if (_completer.Complete(PathText ?? "") is not { } completed)
        {
            Status = "no matching folder";
            return;
        }

        // Set through the field so OnPathTextChanged does not treat our own
        // write as the user typing and reset the cycle.
        _completingPath = true;
        try { PathText = completed; }
        finally { _completingPath = false; }
    }

    private bool _completingPath;

    partial void OnPathTextChanged(string value)
    {
        // Typing invalidates the candidate list; completing does not.
        if (!_completingPath) _completer.Reset();
    }

    [RelayCommand]
    public void BeginEditPath()
    {
        _completer.Reset();

        PathText = CurrentPath;
        IsPathEditing = true;
    }

    [RelayCommand]
    public void EndEditPath() => IsPathEditing = false;

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (string.IsNullOrEmpty(CurrentPath)) return;

        var parts = CurrentPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var accumulated = "";

        Breadcrumbs.Add(new PathSegment(
            "/", "/", new RelayCommand(() => _ = NavigateAsync("/")), parts.Length == 0));

        for (var i = 0; i < parts.Length; i++)
        {
            accumulated += "/" + parts[i];
            var target = accumulated;

            Breadcrumbs.Add(new PathSegment(parts[i], target,
                new RelayCommand(() => _ = NavigateAsync(target)),
                i == parts.Length - 1));
        }
    }

    /// <summary>
    /// Enter in the path box. A command rather than a code-behind KeyDown
    /// handler because there is now one path box per split side, and named
    /// controls inside a template cannot be reached from code-behind.
    /// </summary>
    [RelayCommand]
    public Task NavigateToPathText()
    {
        IsPathEditing = false;
        return string.IsNullOrWhiteSpace(PathText) ? Task.CompletedTask : NavigateAsync(PathText.Trim());
    }

    /// <summary>
    /// Escape, or clicking away: put back what is actually being shown.
    ///
    /// Guarded, because it is now reachable from lost-focus as well as Escape.
    /// NavigateToPathText clears IsPathEditing before it reads PathText, so an
    /// unguarded revert would fire in that gap and overwrite the path the user
    /// just typed — Enter would appear to navigate nowhere.
    /// </summary>
    [RelayCommand]
    public void RevertPathText()
    {
        if (!IsPathEditing) return;

        PathText = CurrentPath;
        IsPathEditing = false;
    }

    /// <summary>
    /// Narrows the current listing to entries carrying a tag. Scoped to the
    /// folder on screen rather than the whole home directory: tags live in
    /// extended attributes with no index behind them, so anything wider would
    /// mean walking the filesystem and reading an xattr per file.
    /// </summary>
    public async Task FilterByTagAsync(string tag)
    {
        if (_tags is null) return;

        Status = $"finding \u201c{tag}\u201d here…";

        // The whole scan in one hop rather than an await per file: this reads an
        // extended attribute for every entry in the folder, and doing that from
        // the UI thread froze the window on a large directory.
        //
        // Which also means it can take a while, and the folder can change under
        // it — so the same generation guard the listing uses applies here.
        var generation = _generation;
        var snapshot = _all.ToList();
        var store = _tags;

        var matches = await Task.Run(() =>
        {
            var found = new List<string>();

            foreach (var entry in snapshot)
            {
                if (store.GetAsync(entry.FullPath, CancellationToken.None)
                         .AsTask().GetAwaiter().GetResult()
                         .Contains(tag, StringComparer.OrdinalIgnoreCase))
                    found.Add(entry.FullPath);
            }

            return found;
        }).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Navigating away mid-scan is the failure case, and it is not
            // harmless: `set` holds paths from the folder that was being
            // scanned, while `_all` is now the new folder's entries, so the
            // Where below matches nothing and REPLACES THE NEW LISTING WITH AN
            // EMPTY ONE. The folder would simply appear empty.
            if (generation != _generation) return;

            var set = matches.ToHashSet(StringComparer.Ordinal);

            Entries.ReplaceAll(_all.Where(e => set.Contains(e.FullPath)).ToList());
            Status = $"{matches.Count} tagged \u201c{tag}\u201d · esc to clear";
            IsFilterVisible = true;
            FilterText = "";
        });
    }

    /// <summary>
    /// Copy alongside. The operations layer already resolves a name collision
    /// by keeping both, which is exactly what duplicating means — so this is a
    /// copy whose destination is where the files already are.
    /// </summary>
    // ---- templates -------------------------------------------------------

    public ObservableCollection<FileTemplate> Templates { get; } = new();

    public bool HasTemplates => Templates.Count > 0;

    /// <summary>Re-read on every menu open: a template is a file the user drops
    /// into a folder, and needing a restart to see it would be baffling.</summary>
    [RelayCommand]
    public void RefreshTemplates()
    {
        Templates.Clear();
        if (_templates is null) return;

        foreach (var template in _templates.Discover()) Templates.Add(template);
        OnPropertyChanged(nameof(HasTemplates));
    }

    [RelayCommand]
    public async Task NewFromTemplateAsync(FileTemplate? template)
    {
        if (template is null || _ops is null) return;

        try
        {
            // A copy, then straight into rename — the name is the only thing
            // the user actually wants to decide.
            var target = Path.Combine(CurrentPath, Path.GetFileName(template.Path));
            var unique = target;
            var counter = 2;

            while (File.Exists(unique) || Directory.Exists(unique))
            {
                unique = Path.Combine(CurrentPath,
                    $"{Path.GetFileNameWithoutExtension(target)} {counter++}{Path.GetExtension(target)}");
            }

            await Task.Run(() => File.Copy(template.Path, unique)).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);

            var created = _all.FirstOrDefault(e => e.FullPath == unique);
            if (created.FullPath is not null) RenameRequested?.Invoke(this, created);
        }
        catch (Exception ex)
        {
            Status = $"could not create from template: {ex.Message}";
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

    // ---- sorting, reachable from the menu as well as the headers ----------

    public bool IsSortedByName => Sort == SortField.Name;
    public bool IsSortedBySize => Sort == SortField.Size;
    public bool IsSortedByModified => Sort == SortField.Modified;
    public bool IsSortedByKind => Sort == SortField.Kind;

    [RelayCommand] private void SortByName() => SortBy("name");
    [RelayCommand] private void SortBySize() => SortBy("size");
    [RelayCommand] private void SortByModified() => SortBy("modified");

    /// <summary>Sorting by type was implemented from the start and had no way
    /// to be reached — there is no type column to click.</summary>
    [RelayCommand]
    private void SortByKind()
    {
        if (Sort == SortField.Kind) SortDescending = !SortDescending;
        else { Sort = SortField.Kind; SortDescending = false; }
    }

    [RelayCommand]
    private void ToggleSortDirection() => SortDescending = !SortDescending;

    [RelayCommand]
    public void ClearFilter()
    {
        if (FilterText.Length > 0) FilterText = "";
        else IsFilterVisible = false;
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

        _groupNow = DateTimeOffset.Now;

        var sorted = filtered.ToList();
        sorted.Sort(Compare);

        // Before the swap, so a row realized by ReplaceAll already has its
        // header available rather than reading a stale map.
        RecomputeGroups(sorted);

        Entries.ReplaceAll(sorted);

        // Only when filtering. The plain count lives in Summary, and setting
        // both made the status bar print "36 items   36 items".
        Status = filtered.Count == _all.Count
            ? ""
            : $"filtered to {filtered.Count:N0} of {_all.Count:N0}";
    }

    partial void OnSortDescendingChanged(bool value)
    {
        NotifySortGlyphs();
        if (!_suppressReload) ResortInPlace();
    
        RememberFolderView();
    }

    private async Task LoadAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        if (ShowColumnStrip)
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

        // Before CurrentPath moves, and guarded so the property setters this
        // triggers do not immediately write the folder's own state back at it.
        _restoringView = true;
        try { ApplyFolderView(path); }
        finally { _restoringView = false; }

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
                    // Cancelling the token does NOT unqueue a dispatcher
                    // callback that is already on its way. Without this check a
                    // superseded enumeration appends its batch into the list the
                    // newer navigation just cleared — which is the flash of
                    // wrong files you get from clicking a place twice.
                    if (generation != _generation) return;

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
                    if (generation != _generation) return;

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
                // The worst one to miss: a superseded run reaching here would
                // point the watcher at the folder it was loading, clear
                // IsLoading for a navigation still in flight, and sort a list
                // that now belongs to somewhere else.
                if (generation != _generation) return;

                if (FilterText.Length > 0) ApplyFilter(); else ResortInPlace();
                StartWatching(path);
                sw.Stop();

                // Cleared, NOT set to the count. Summary already shows
                // "36 items" and Status sat beside it showing the same thing,
                // so the status bar read "36 items   36 items". Status is for
                // messages; the count has an owner and this is not it.
                Status = "";
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
                // A failure in a navigation nobody is waiting for any more is
                // not worth reporting over the one they are.
                if (generation != _generation) return;

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

    /// <summary>
    /// Only says something when a filter is actually hiding rows, because that
    /// is the part Summary cannot express — Summary counts what is on screen
    /// and has no way to say "out of how many". With no filter there is nothing
    /// to add, so it says nothing rather than repeating the count.
    /// </summary>
    private void UpdateCountStatus()
        => Status = Entries.Count == _all.Count
            ? ""
            : $"{Entries.Count:N0} of {_all.Count:N0} items";

    private void ResortInPlace()
    {
        if (Entries.Count == 0) return;

        _groupNow = DateTimeOffset.Now;

        var items = _all.Count > 0 ? _all.ToList() : Entries.ToList();
        items.Sort(Compare);

        RecomputeGroups(items);
        Entries.ReplaceAll(items);
    }

    // ---- grouping ---------------------------------------------------------

    [ObservableProperty] private GroupMode _groupBy = GroupMode.None;

    public bool IsGroupedByName => GroupBy == GroupMode.Name;
    public bool IsGroupedBySize => GroupBy == GroupMode.Size;
    public bool IsGroupedByModified => GroupBy == GroupMode.Modified;
    public bool IsGroupedByKind => GroupBy == GroupMode.Kind;
    public bool IsUngrouped => GroupBy == GroupMode.None;

    [RelayCommand] private void GroupByNone() => GroupBy = GroupMode.None;
    [RelayCommand] private void GroupByName() => GroupBy = GroupMode.Name;
    [RelayCommand] private void GroupBySize() => GroupBy = GroupMode.Size;
    [RelayCommand] private void GroupByModified() => GroupBy = GroupMode.Modified;
    [RelayCommand] private void GroupByKind() => GroupBy = GroupMode.Kind;

    partial void OnGroupByChanged(GroupMode value)
    {
        OnPropertyChanged(nameof(IsUngrouped));
        OnPropertyChanged(nameof(IsGroupedByName));
        OnPropertyChanged(nameof(IsGroupedBySize));
        OnPropertyChanged(nameof(IsGroupedByModified));
        OnPropertyChanged(nameof(IsGroupedByKind));

        if (!_suppressReload) ApplyFilter();
    
        RememberFolderView();
    }

    /// <summary>
    /// The header a row should carry, or null. Computed once per rebuild rather
    /// than per row: a row cannot see its predecessor, and asking each one to
    /// work it out would be O(n) lookups on every realization.
    /// </summary>
    private readonly Dictionary<string, string> _groupHeaders = new(StringComparer.Ordinal);

    // Captured once per sort: asking for the time inside a comparison would
    // make the ordering depend on when each comparison happened.
    private DateTimeOffset _groupNow = DateTimeOffset.Now;

    public string? HeaderFor(string fullPath)
        => _groupHeaders.TryGetValue(fullPath, out var label) ? label : null;

    /// <summary>Raised when headers change, so realized rows re-read them.</summary>
    public event EventHandler? GroupingChanged;

    private void RecomputeGroups(List<FileEntry> ordered)
    {
        _groupHeaders.Clear();

        if (GroupBy != GroupMode.None)
        {
            string? previous = null;

            foreach (var entry in ordered)
            {
                var label = Grouping.Label(entry, GroupBy, _groupNow);

                // Only the first row of a run carries the header; the rest are
                // plain, which is what makes it read as a group rather than a
                // repeated label.
                if (label != previous)
                {
                    _groupHeaders[entry.FullPath] = label;
                    previous = label;
                }
            }
        }

        GroupingChanged?.Invoke(this, EventArgs.Empty);
    }

    private int Compare(FileEntry a, FileEntry b)
    {
        // Directories first, always — the convention every file manager follows
        // and users notice immediately when it's missing.
        if (a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        // The group is a PRIMARY key. Without this, grouping by size while
        // sorted by name interleaves the bands and every group holds one file.
        if (GroupBy != GroupMode.None)
        {
            var group = Grouping.CompareGroups(a, b, GroupBy, _groupNow);
            if (group != 0) return group;
        }

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

        // Natural order, so file2 comes before file10. Ordinal comparison is
        // right for bytes and wrong for names people chose — but it is now a
        // preference, because Dolphin makes it one and some people genuinely
        // want the alphabetical order their shell gives them.
        if (result == 0)
        {
            var general = Settings.AppSettings.Current.General;

            result = general.NaturalSorting
                ? NaturalOrder.Compare(a.Name, b.Name)
                : string.Compare(a.Name, b.Name, general.CaseSensitiveSorting
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase);
        }

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
