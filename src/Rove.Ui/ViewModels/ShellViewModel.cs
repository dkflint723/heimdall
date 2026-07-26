using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rove.Core.FileSystem;
using Rove.Core.Places;
using Rove.Core.Search;
using Rove.Core.Session;

namespace Rove.Ui.ViewModels;

/// <summary>
/// Owns one or two pane groups. Deliberately thin — it decides which side is
/// active and nothing else; all the behaviour lives in PaneViewModel, which is
/// what made split view an addition rather than a rewrite.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IFileSystemProvider _fs;
    private readonly IFileOperations? _ops;
    private readonly IApplicationLauncher? _launcher;
    private readonly IClipboardService? _clipboard;
    private readonly ISessionStore? _store;
    private bool _restoring;
    private bool _started;

    /// <summary>
    /// The right side as it was when last closed. Reopening restores it, so
    /// toggling the split off is not a way to silently lose where you were.
    /// </summary>
    private PaneState? _rememberedRight;

    public ShellViewModel(
        IFileSystemProvider fs,
        IFileOperations? ops = null,
        ISessionStore? store = null,
        IPlacesProvider? places = null,
        IApplicationLauncher? launcher = null,
        IClipboardService? clipboard = null,
        ISearchProvider? search = null)
    {
        _fs = fs;
        _ops = ops;
        _store = store;
        _launcher = launcher;
        _clipboard = clipboard;

        Sidebar = new SidebarViewModel(fs, places, search, () => ActiveTab?.CurrentPath);

        // A chosen result navigates the active tab to its folder and selects it,
        // rather than opening the file — search is for finding, not launching.
        Sidebar.Search.ResultChosen += (_, entry) =>
        {
            var folder = entry.IsDirectory
                ? entry.FullPath
                : Path.GetDirectoryName(entry.FullPath);

            if (folder is null || ActiveTab is not { } pane) return;

            _ = pane.NavigateAsync(folder).ContinueWith(
                _ => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => pane.SelectedEntry = entry),
                TaskScheduler.Default);
        };

        Left = CreateGroup();
        ActiveGroup = Left;

        Sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SidebarViewModel.ActivePanel)
                               or nameof(SidebarViewModel.Rail)
                               or nameof(SidebarViewModel.Width))
                MarkDirty();
        };
    }

    public SidebarViewModel Sidebar { get; }

    public PaneGroupViewModel Left { get; private set; }

    /// <summary>Null unless split. The XAML binds its column's visibility to this.</summary>
    [ObservableProperty] private PaneGroupViewModel? _right;

    [ObservableProperty] private PaneGroupViewModel _activeGroup = null!;

    [ObservableProperty] private double _splitRatio = 0.5;

    /// <summary>
    /// Multiplies the whole type scale and every metric derived from it. Exists
    /// as a user control rather than a constant because "the text is too small"
    /// is an accessibility problem, and the right size depends on the display
    /// and the person, not on a value picked at build time.
    /// </summary>
    [ObservableProperty] private double _uiScale = 1.0;

    /// <summary>Set by the view so scale changes can re-write the resources.</summary>
    public Action<double>? ScaleApplier { get; set; }

    partial void OnUiScaleChanged(double value)
    {
        ScaleApplier?.Invoke(value);
        MarkDirty();
    }

    [RelayCommand]
    private void ZoomIn() => UiScale = Math.Round(Math.Min(UiScale + 0.1, 2.5), 2);

    [RelayCommand]
    private void ZoomOut() => UiScale = Math.Round(Math.Max(UiScale - 0.1, 0.8), 2);

    [RelayCommand]
    private void ZoomReset() => UiScale = 1.0;

    public bool IsSplit => Right is not null;

    // Hiding a control does not give its column back — an invisible pane in a
    // "*" column still reserves half the window. The definitions themselves
    // have to collapse, so they are driven from here.
    public GridLength LeftColumnWidth
        => new(IsSplit ? Math.Clamp(SplitRatio, 0.1, 0.9) : 1, GridUnitType.Star);

    public GridLength RightColumnWidth
        => IsSplit ? new GridLength(1 - Math.Clamp(SplitRatio, 0.1, 0.9), GridUnitType.Star)
                   : new GridLength(0);

    private void NotifyColumns()
    {
        OnPropertyChanged(nameof(LeftColumnWidth));
        OnPropertyChanged(nameof(RightColumnWidth));
    }

    /// <summary>
    /// The active tab of the active side. Everything outside this class —
    /// toolbar, key bindings, context menu — binds through here and never needs
    /// to know whether the window is split.
    /// </summary>
    public PaneViewModel? ActiveTab => ActiveGroup?.ActiveTab;

    /// <summary>The other side, when split. Where "copy to other pane" sends things.</summary>
    public PaneGroupViewModel? OtherGroup
        => Right is null ? null : ReferenceEquals(ActiveGroup, Left) ? Right : Left;

    public event EventHandler<PaneViewModel>? PaneCreated;

    /// <summary>The view owns window creation, so the command just asks.</summary>
    public event EventHandler? PropertiesRequested;

    [RelayCommand]
    private void ShowProperties() => PropertiesRequested?.Invoke(this, EventArgs.Empty);

    public Func<WindowSession>? GeometryProvider { get; set; }

    [ObservableProperty] private string _operationStatus = "";
    [ObservableProperty] private IOperationHandle? _activeOperation;

    // ---- construction --------------------------------------------------

    private PaneGroupViewModel CreateGroup()
    {
        var group = new PaneGroupViewModel(NewPane);
        group.PropertyChanged += OnGroupChanged;
        return group;
    }

    private PaneViewModel NewPane()
    {
        var pane = new PaneViewModel(_fs, _ops, _launcher, _clipboard);
        pane.OperationStarted += OnOperationStarted;
        pane.PropertyChanged += OnPaneChanged;
        PaneCreated?.Invoke(this, pane);
        return pane;
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneGroupViewModel.ActiveTab)) return;

        if (ReferenceEquals(sender, ActiveGroup))
            OnPropertyChanged(nameof(ActiveTab));

        MarkDirty();
    }

    partial void OnActiveGroupChanged(PaneGroupViewModel? oldValue, PaneGroupViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActiveGroup = false;
        newValue.IsActiveGroup = true;

        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(OtherGroup));
        MarkDirty();
    }

    partial void OnRightChanged(PaneGroupViewModel? value)
    {
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(OtherGroup));
        NotifyColumns();
        MarkDirty();
    }

    partial void OnSplitRatioChanged(double value)
    {
        NotifyColumns();
        MarkDirty();
    }

    // ---- split ---------------------------------------------------------

    /// <summary>
    /// F3, matching Dolphin. Opening a split clones the current location so the
    /// second side starts somewhere useful rather than at home.
    /// </summary>
    [RelayCommand]
    public void ToggleSplit()
    {
        if (Right is null)
        {
            var right = CreateGroup();

            // Populated before being assigned, so the column never flashes empty.
            if (_rememberedRight is { Tabs.Count: > 0 } remembered)
                Restore(right, remembered);
            else
                right.AddTab(ActiveTab?.CurrentPath
                             ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            Right = right;
            ActiveGroup = right;
        }
        else
        {
            // Closing the split always keeps the left side, so which half
            // survives is predictable rather than depending on focus.
            var closing = Right;

            _rememberedRight = closing.ToPaneState();

            Right = null;
            ActiveGroup = Left;
            closing.DisposeAll();
        }
    }

    /// <summary>Tab, matching Dolphin.</summary>
    [RelayCommand]
    public void FocusOtherPane()
    {
        if (OtherGroup is { } other) ActiveGroup = other;
    }

    [RelayCommand]
    public void CopyToOtherPane() => TransferToOther(move: false);

    [RelayCommand]
    public void MoveToOtherPane() => TransferToOther(move: true);

    private void TransferToOther(bool move)
    {
        if (_ops is null || OtherGroup?.ActiveTab is not { } target) return;
        if (ActiveTab is not { } source) return;

        var paths = source.SelectedEntries.Count > 0
            ? source.SelectedEntries.Select(e => e.FullPath).ToList()
            : source.SelectedEntry is { } one ? [one.FullPath] : [];

        if (paths.Count == 0) return;

        target.PasteInto(paths, move);
    }

    // ---- tabs ----------------------------------------------------------

    [RelayCommand]
    private void NewTab()
        => ActiveGroup.AddTab(ActiveTab?.CurrentPath
                              ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [RelayCommand]
    private void OpenInNewTab(FileEntry entry)
    {
        if (entry.IsDirectory) ActiveGroup.AddTab(entry.FullPath);
    }

    [RelayCommand]
    private void CloseTab(PaneViewModel? pane)
    {
        // Closing the last tab of the right side collapses the split rather
        // than refusing, which is what the user actually means.
        if (Right is not null && ActiveGroup.Tabs.Count <= 1 &&
            (pane is null || ActiveGroup.Tabs.Contains(pane)))
        {
            if (ReferenceEquals(ActiveGroup, Right)) { ToggleSplit(); return; }

            // Closing the last left tab: promote the right side to be the only one.
            var survivor = Right;
            Left.DisposeAll();
            Left = survivor;
            Right = null;
            ActiveGroup = Left;
            OnPropertyChanged(nameof(Left));
            return;
        }

        ActiveGroup.CloseTab(pane);
    }

    [RelayCommand] private void SelectTab(PaneViewModel? pane) { if (pane is not null) ActiveGroup.ActiveTab = pane; }
    [RelayCommand] private void NextTab() => ActiveGroup.Cycle(1);
    [RelayCommand] private void PreviousTab() => ActiveGroup.Cycle(-1);
    [RelayCommand] private void CancelOperation() => ActiveOperation?.Cancel();

    public void SelectTabByIndex(int index) => ActiveGroup.SelectTabByIndex(index);

    public void ActivateGroup(PaneGroupViewModel group)
    {
        if (!ReferenceEquals(ActiveGroup, group)) ActiveGroup = group;
    }

    // ---- places --------------------------------------------------------

    [RelayCommand]
    private void GoToPlace(string? path)
    {
        if (!string.IsNullOrEmpty(path)) _ = ActiveTab?.NavigateAsync(path);
    }

    [RelayCommand]
    private void PinCurrent()
    {
        if (ActiveTab?.CurrentPath is { Length: > 0 } path) _ = Sidebar.PinAsync(path);
    }

    // ---- operations ----------------------------------------------------

    private void OnOperationStarted(object? sender, IOperationHandle handle)
    {
        ActiveOperation = handle;

        handle.Progressed += (_, p) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                OperationStatus = p.ItemsTotal <= 1 && p.BytesTotal == 0
                    ? p.CurrentItem ?? ""
                    : $"{p.ItemsDone}/{p.ItemsTotal}  {Format(p.BytesDone)}/{Format(p.BytesTotal)}  {p.CurrentItem}");

        _ = handle.Completion.ContinueWith(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                OperationStatus = "";
                ActiveOperation = null;
            }), TaskScheduler.Default);
    }

    private static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    // ---- session -------------------------------------------------------

    public void Start(SessionState? state)
    {
        if (_started) return;
        _started = true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var window = state?.Windows.FirstOrDefault();

        if (window is not null)
        {
            Sidebar.ActivePanel = window.ActiveSidebarPanel;
            Sidebar.Width = window.SidebarWidth;
            Sidebar.Rail = window.Rail;
            SplitRatio = window.SplitRatio;
            UiScale = window.UiScale <= 0 ? 1.0 : window.UiScale;
        }

        _ = Sidebar.InitializeAsync();

        var panes = window?.Panes;

        _restoring = true;
        try
        {
            if (panes is null || panes.Count == 0 || panes[0].Tabs.Count == 0)
            {
                Left.AddTab(home);
            }
            else
            {
                Restore(Left, panes[0]);

                if (panes.Count > 1 && panes[1].Tabs.Count > 0)
                {
                    var right = CreateGroup();
                    Restore(right, panes[1]);
                    Right = right;
                }
                else
                {
                    // Split was closed at save time; keep what it was showing
                    // so reopening after a restart lands back in place.
                    _rememberedRight = window?.RememberedRightPane;
                }
            }

            var activeIndex = window?.ActivePaneIndex ?? 0;
            ActiveGroup = activeIndex == 1 && Right is not null ? Right : Left;
        }
        finally
        {
            _restoring = false;
        }

        // Assigned while suppressed, so it never triggered its own load.
        ActiveGroup.ActiveTab?.RefreshIfUnloaded();
    }

    private static void Restore(PaneGroupViewModel group, PaneState state)
    {
        foreach (var tab in state.Tabs) group.AddRestoredTab(tab);
        group.ActiveTab = group.Tabs[Math.Clamp(state.ActiveTabIndex, 0, group.Tabs.Count - 1)];
    }

    public SessionState ToSessionState()
    {
        var geometry = GeometryProvider?.Invoke() ?? new WindowSession();

        var panes = Right is null
            ? new List<PaneState> { Left.ToPaneState() }
            : [Left.ToPaneState(), Right.ToPaneState()];

        return new SessionState
        {
            Version = SessionState.CurrentVersion,
            Windows =
            [
                geometry with
                {
                    ActiveSidebarPanel = Sidebar.ActivePanel,
                    SidebarWidth = Sidebar.Width,
                    Rail = Sidebar.Rail,
                    SplitRatio = SplitRatio,
                    UiScale = UiScale,
                    RememberedRightPane = Right is null ? _rememberedRight : null,
                    Panes = panes,
                    ActivePaneIndex = ReferenceEquals(ActiveGroup, Right) ? 1 : 0,
                }
            ],
        };
    }

    public void NotifyWindowChanged() => MarkDirty();

    private void MarkDirty()
    {
        // Nothing before Start() is worth saving, and property setters fire
        // during construction while Sidebar and the groups are still null.
        if (!_started || _restoring || _store is null) return;
        _store.NotifyChanged(ToSessionState());
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.CurrentPath):
            case nameof(PaneViewModel.Sort):
            case nameof(PaneViewModel.SortDescending):
            case nameof(PaneViewModel.ShowHidden):
                MarkDirty();
                break;
        }
    }
}
