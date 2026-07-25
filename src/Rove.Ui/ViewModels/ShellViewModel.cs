using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rove.Core.FileSystem;
using Rove.Core.Session;

namespace Rove.Ui.ViewModels;

/// <summary>
/// Owns the set of open tabs. Deliberately thin — it holds panes and decides
/// which is active, and nothing else. All the behaviour lives in PaneViewModel,
/// which is what makes a split view later just two of these lists side by side
/// rather than a rewrite.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IFileSystemProvider _fs;
    private readonly IFileOperations? _ops;
    private readonly ISessionStore? _store;
    private bool _restoring;
    private bool _started;

    public ShellViewModel(
        IFileSystemProvider fs,
        IFileOperations? ops = null,
        ISessionStore? store = null)
    {
        _fs = fs;
        _ops = ops;
        _store = store;
        Tabs.CollectionChanged += OnTabsChanged;
    }

    /// <summary>Progress line for whatever operation is running, or empty.</summary>
    [ObservableProperty] private string _operationStatus = "";

    /// <summary>The running operation, so the view can offer pause and cancel.</summary>
    [ObservableProperty] private IOperationHandle? _activeOperation;

    private PaneViewModel NewPane()
    {
        var pane = new PaneViewModel(_fs, _ops);
        pane.OperationStarted += OnOperationStarted;
        return pane;
    }

    private void OnOperationStarted(object? sender, IOperationHandle handle)
    {
        ActiveOperation = handle;

        if (handle is Rove.Linux.OperationHandle concrete)
        {
            concrete.Progressed += (_, p) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    OperationStatus = p.ItemsTotal <= 1 && p.BytesTotal == 0
                        ? p.CurrentItem ?? ""
                        : $"{p.ItemsDone}/{p.ItemsTotal}  {Format(p.BytesDone)}/{Format(p.BytesTotal)}  {p.CurrentItem}");
        }

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

    public ObservableCollection<PaneViewModel> Tabs { get; } = new();

    [ObservableProperty] private PaneViewModel? _activeTab;

    /// <summary>
    /// Supplies current window geometry at save time. The window owns its own
    /// size and position; this view model only asks for them.
    /// </summary>
    public Func<WindowSession>? GeometryProvider { get; set; }

    /// <summary>
    /// Open the persisted session, or a single home tab if there isn't one.
    /// Idempotent — restoring is a once-per-process operation and must not
    /// depend on nobody calling it twice.
    /// </summary>
    public void Start(SessionState? state)
    {
        if (_started) return;
        _started = true;

        var tabs = state?.Windows.FirstOrDefault()?.Panes.FirstOrDefault()?.Tabs;

        if (tabs is null || tabs.Count == 0)
        {
            AddTab(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return;
        }

        _restoring = true;
        try
        {
            foreach (var tab in tabs)
            {
                var pane = NewPane();
                pane.RestoreFrom(tab);
                Tabs.Add(pane);
            }

            var index = state!.Windows[0].Panes[0].ActiveTabIndex;
            ActiveTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        }
        finally
        {
            _restoring = false;
        }

        // The active tab was assigned while suppressed, so it never loaded.
        ActiveTab?.RefreshIfUnloaded();
    }

    public SessionState ToSessionState()
    {
        var geometry = GeometryProvider?.Invoke() ?? new WindowSession();

        return new SessionState
        {
            Version = SessionState.CurrentVersion,
            Windows =
            [
                geometry with
                {
                    Panes =
                    [
                        new PaneState
                        {
                            Tabs = Tabs.Select(t => t.ToTabState()).ToList(),
                            ActiveTabIndex = ActiveTab is null
                                ? 0
                                : Math.Max(0, Tabs.IndexOf(ActiveTab)),
                        }
                    ],
                }
            ],
        };
    }

    /// <summary>Window moved or resized — geometry is part of the session too.</summary>
    public void NotifyWindowChanged() => MarkDirty();

    private void MarkDirty()
    {
        if (_restoring || _store is null) return;
        _store.NotifyChanged(ToSessionState());
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var pane in e.NewItems?.OfType<PaneViewModel>() ?? [])
        {
            pane.PropertyChanged -= OnPaneChanged;
            pane.PropertyChanged += OnPaneChanged;
        }

        foreach (var pane in e.OldItems?.OfType<PaneViewModel>() ?? [])
            pane.PropertyChanged -= OnPaneChanged;

        MarkDirty();
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only state worth persisting triggers a write. PathText, Status and
        // IsLoading change constantly and mean nothing across restarts.
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

    partial void OnActiveTabChanged(PaneViewModel? oldValue, PaneViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        MarkDirty();
    }

    public PaneViewModel AddTab(string path)
    {
        var pane = NewPane();
        Tabs.Add(pane);
        ActiveTab = pane;
        _ = pane.NavigateAsync(path);
        return pane;
    }

    [RelayCommand]
    private void CancelOperation() => ActiveOperation?.Cancel();

    [RelayCommand]
    private void NewTab()
        => AddTab(ActiveTab?.CurrentPath
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Opening a selected directory in a new tab, rather than a clone.</summary>
    [RelayCommand]
    private void OpenInNewTab(FileEntry entry)
    {
        if (entry.IsDirectory) AddTab(entry.FullPath);
    }

    [RelayCommand]
    private void CloseTab(PaneViewModel? pane)
    {
        pane ??= ActiveTab;
        if (pane is null) return;

        // Never leave zero tabs — an empty window with no way back is a dead end.
        if (Tabs.Count == 1) return;

        var index = Tabs.IndexOf(pane);
        var wasActive = ActiveTab == pane;

        Tabs.Remove(pane);
        pane.Dispose();

        if (wasActive || ActiveTab is null)
            ActiveTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void SelectTab(PaneViewModel? pane)
    {
        if (pane is not null) ActiveTab = pane;
    }

    [RelayCommand]
    private void NextTab() => Cycle(1);

    [RelayCommand]
    private void PreviousTab() => Cycle(-1);

    private void Cycle(int delta)
    {
        if (Tabs.Count < 2 || ActiveTab is null) return;
        var i = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[i];
    }

    public void SelectTabByIndex(int index)
    {
        if (index >= 0 && index < Tabs.Count) ActiveTab = Tabs[index];
    }
}
