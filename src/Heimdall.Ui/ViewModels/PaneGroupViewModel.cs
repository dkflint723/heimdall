using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rove.Core.Session;

namespace Rove.Ui.ViewModels;

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
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
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
    };

    public void DisposeAll()
    {
        foreach (var pane in Tabs) pane.Dispose();
        Tabs.Clear();
    }
}
