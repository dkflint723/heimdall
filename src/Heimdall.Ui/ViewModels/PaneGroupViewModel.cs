using System.ComponentModel;
using Heimdall.Core.FileSystem;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Session;

namespace Heimdall.Ui.ViewModels;

/// <summary>
/// One side of the window: its own tab strip, its own active tab.
///
/// Splitting the window is two of these rather than a special case, which is
/// why PaneViewModel was built to own its path, history, sort and selection
/// from the beginning. The session schema has stored Panes as a list since v1
/// for the same reason.
/// </summary>
public sealed partial class PaneGroupViewModel : ObservableObject
{
    private readonly Func<PaneViewModel> _createPane;

    public PaneGroupViewModel(Func<PaneViewModel> createPane) => _createPane = createPane;

    public ObservableCollection<PaneViewModel> Tabs { get; } = new();

    [ObservableProperty] private PaneViewModel? _activeTab;

    /// <summary>Which side has focus. Drives the accent on the active side and
    /// decides where new tabs and pasted files land.</summary>
    [ObservableProperty] private bool _isActiveGroup;

    partial void OnActiveTabChanged(PaneViewModel? oldValue, PaneViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.PropertyChanged -= OnTabChanged;
        }

        if (newValue is not null)
        {
            newValue.IsActive = true;
            newValue.PropertyChanged += OnTabChanged;
        }

        RefreshInfo();
    }

    // ---- details panel ----------------------------------------------------

    /// <summary>
    /// One panel per split side, not one per window.
    ///
    /// The panel describes what you have selected, and each side has its own
    /// selection — a single shared panel would keep swapping content as focus
    /// moved between halves, which is the opposite of useful when you are
    /// comparing two folders. That comparison is the reason a split exists.
    /// </summary>
    public InfoPanelViewModel Info { get; private set; } = new(null);

    [ObservableProperty] private bool _isInfoVisible;

    [ObservableProperty] private double _infoWidth = 280;

    private IPropertiesProvider? _properties;

    public void UseProperties(IPropertiesProvider? properties)
    {
        _properties = properties;
        Info = new InfoPanelViewModel(properties);
        OnPropertyChanged(nameof(Info));
        RefreshInfo();
    }

    [RelayCommand]
    private void ToggleInfo()
    {
        IsInfoVisible = !IsInfoVisible;
        if (IsInfoVisible) RefreshInfo();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneViewModel.SelectedEntry)
                           or nameof(PaneViewModel.Summary))
            RefreshInfo();
    }

    /// <summary>
    /// Only while this side's panel is open. Statting files to fill a hidden
    /// panel is work nobody asked for, and selection changes constantly.
    /// </summary>
    public void RefreshInfo()
    {
        if (!IsInfoVisible) return;

        if (ActiveTab is not { } pane) { Info.ShowNothing(); return; }

        var selection = pane.Selection;

        if (selection.Count > 1)
        {
            long total = 0;
            foreach (var entry in selection)
                if (!entry.IsDirectory) total += entry.Length;

            Info.ShowMany(selection.Count, total);
            return;
        }

        if (selection.Count == 1) { _ = Info.ShowAsync(selection[0]); return; }

        if (pane.SelectedEntry is { } single) _ = Info.ShowAsync(single);
        else Info.ShowNothing();
    }

    /// <summary>
    /// The "+" beside this side's tab strip. Lives on the group rather than the
    /// shell so each half opens tabs into itself, regardless of which side has
    /// focus at the time.
    /// </summary>
    [RelayCommand]
    private void NewTabHere()
        => AddTab(ActiveTab?.CurrentPath
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public PaneViewModel AddTab(string path)
    {
        var pane = _createPane();
        Tabs.Add(pane);
        ActiveTab = pane;
        _ = pane.NavigateAsync(path);
        return pane;
    }

    public PaneViewModel AddRestoredTab(TabState tab)
    {
        var pane = _createPane();
        pane.RestoreFrom(tab);
        Tabs.Add(pane);
        return pane;
    }

    public void CloseTab(PaneViewModel? pane)
    {
        pane ??= ActiveTab;
        if (pane is null) return;

        // Never leave a side with zero tabs — an empty column with no way back
        // is a dead end. Closing the last tab of a split side collapses the
        // split instead, which is handled by the shell.
        if (Tabs.Count <= 1) return;

        var index = Tabs.IndexOf(pane);
        var wasActive = ActiveTab == pane;

        Tabs.Remove(pane);
        pane.Dispose();

        if (wasActive || ActiveTab is null)
            ActiveTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
    }

    public void Cycle(int delta)
    {
        if (Tabs.Count < 2 || ActiveTab is null) return;
        var i = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[i];
    }

    public void SelectTabByIndex(int index)
    {
        if (index >= 0 && index < Tabs.Count) ActiveTab = Tabs[index];
    }

    public PaneState ToPaneState() => new()
    {
        Tabs = Tabs.Select(t => t.ToTabState()).ToList(),
        ActiveTabIndex = ActiveTab is null ? 0 : Math.Max(0, Tabs.IndexOf(ActiveTab)),
        IsInfoVisible = IsInfoVisible,
        InfoWidth = InfoWidth,
    };

    public void RestoreFrom(PaneState state)
    {
        IsInfoVisible = state.IsInfoVisible;
        InfoWidth = state.InfoWidth > 0 ? state.InfoWidth : 280;
    }

    public void DisposeAll()
    {
        foreach (var pane in Tabs) pane.Dispose();
        Tabs.Clear();
    }
}
