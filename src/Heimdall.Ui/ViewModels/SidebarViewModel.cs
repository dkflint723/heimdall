using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Heimdall.Core;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Places;
using Heimdall.Core.Search;
using Heimdall.Core.Session;

namespace Heimdall.Ui.ViewModels;

/// <summary>
/// Rail plus switchable panel — VS Code's activity bar. The rail decides what
/// the panel shows, so each panel gets full height instead of competing for it,
/// and adding one later is a registration rather than a redesign.
///
/// A panel earns a rail slot only if it is a place you navigate *from* or a
/// result set that should survive navigation. Everything else is a command.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    private readonly IPlacesProvider? _places;

    public SidebarViewModel(
        IPlacesProvider? places,
        ISearchProvider? search = null,
        Func<string?>? currentPath = null)
    {
        _places = places;
        Search = new SearchViewModel(search, currentPath ?? (() => null));

        if (places is not null)
            places.PlacesChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = ReloadAsync());
    }

    public ObservableCollection<PlaceGroupViewModel> Groups { get; } = new();

    private ITagStore? _tags;
    private Action<string>? _onTagChosen;

    /// <summary>Wired by the shell, which owns what a tag click actually does.</summary>
    public void AttachTags(ITagStore? tags, Action<string> onChosen)
    {
        _tags = tags;
        _onTagChosen = onChosen;

        // The store raises this and NOTHING was listening, so a tag created
        // after startup did not appear in the sidebar until the next launch.
        // The pane's own tag menu refreshed itself; this list did not.
        if (tags is not null)
            tags.TagsChanged += (_, _) => Dispatcher.UIThread.Post(RefreshTags);
        RefreshTags();
    }

    public void RefreshTags()
    {
        Tags.Clear();
        if (_tags is null) return;

        foreach (var tag in _tags.KnownTags)
        {
            var name = tag;
            Tags.Add(new TagOption(name, new RelayCommand(() => _onTagChosen?.Invoke(name))));
        }

        OnPropertyChanged(nameof(HasTags));
    }
    public SearchViewModel Search { get; }

    [ObservableProperty] private string _activePanel = "places";
    [ObservableProperty] private RailState _rail = RailState.Full;
    [ObservableProperty] private double _width = 210;

    // One sidebar, all sections visible at once — the point of the workspace
    // layout is that the things you organise by are never behind a toggle.
    // ActivePanel survives only as "which section is expanded".
    public bool IsPanelVisible => Rail != RailState.Hidden;

    /// <summary>The folder tree is the one section worth collapsing: it is tall,
    /// and it is the least used of the four.</summary>



    [ObservableProperty] private bool _isSearching;

    public ObservableCollection<TagOption> Tags { get; } = new();

    // ---- navigation --------------------------------------------------------
    //
    // The frequently-visited list was REMOVED at the user's request once Recent
    // files and Recent locations existed — recency covers what frequency was
    // being used for, and two ranked lists of folders in one sidebar is one too
    // many. Git has it. The visit-count store it read has since been deleted
    // too, having had no other reader.
    //
    // What survives is the callback: the shell owns what a click does, and both
    // the recent entries and tags reach it this way.

    private Action<string>? _onFolderChosen;

    /// <summary>Wired by the shell, which is the only place that knows which
    /// pane is active — the same arrangement as tags.</summary>
    public void AttachNavigation(Action<string> onChosen) => _onFolderChosen = onChosen;

    // ---- recent ------------------------------------------------------------
    //
    // Two fixed entries rather than a bound collection: they never change, so a
    // collection plus an item record would be machinery serving two buttons.
    // They are always shown, even on a first run when both listings are empty —
    // Dolphin does the same, and an entry that appears out of nowhere once you
    // have opened enough files is harder to find than one that was always there.
    //
    // Reuses _onFolderChosen, which is how tags and frequent already reach the
    // shell: the store holds the data, the shell decides what a click does.

    /// <summary>
    /// Where the active pane is, so a row can show that it is the one being
    /// viewed. Set by the shell — the sidebar has no idea which pane is active
    /// and should not learn.
    ///
    /// Compared with a trailing separator trimmed: `/home/flint` and
    /// `/home/flint/` are the same place, and a place list that quietly fails to
    /// highlight Home because of one character would be baffling.
    /// </summary>
    public void SetCurrentPath(string? path)
    {
        var wanted = Normalise(path);

        foreach (var group in Groups)
        foreach (var item in group.Places)
            item.IsCurrent = Normalise(item.Path) == wanted;

        CurrentPath = wanted;
        OnPropertyChanged(nameof(IsRecentFilesCurrent));
        OnPropertyChanged(nameof(IsRecentLocationsCurrent));
    }

    private static string Normalise(string? path)
        => path is null ? "" : path.Length > 1 ? path.TrimEnd('/') : path;

    /// <summary>The active path, for the fixed entries that are not in Groups.</summary>
    public string CurrentPath { get; private set; } = "";

    public bool IsRecentFilesCurrent => CurrentPath == VirtualPaths.Files;
    public bool IsRecentLocationsCurrent => CurrentPath == VirtualPaths.Locations;

    [RelayCommand]
    private void OpenRecentFiles() => _onFolderChosen?.Invoke(VirtualPaths.Files);

    [RelayCommand]
    private void OpenRecentLocations() => _onFolderChosen?.Invoke(VirtualPaths.Locations);

    /// <summary>
    /// Remote locations the desktop has mounted. Shown beside Devices because
    /// that is what they are from here — a path you can open, whatever protocol
    /// is behind it.
    /// </summary>
    public ObservableCollection<RemoteMount> Remotes { get; } = new();

    public bool HasRemotes => Remotes.Count > 0;

    private IRemoteMounts? _mounts;

    public void UseRemotes(IRemoteMounts? mounts)
    {
        _mounts = mounts;
        RefreshRemotes();
    }

    [RelayCommand]
    public void RefreshRemotes()
    {
        Remotes.Clear();

        foreach (var mount in _mounts?.Discover() ?? []) Remotes.Add(mount);

        // Published here because this is the one place that knows what is
        // mounted; thumbnails need it to tell a network file from a local one
        // without re-reading the mount table per row.
        Thumbnails.ThumbnailLoader.RemoteRoots = Remotes.Select(m => m.Path).ToList();

        OnPropertyChanged(nameof(HasRemotes));
    }

    public bool HasTags => Tags.Count > 0;

    /// <summary>Ctrl+F reveals the sidebar and puts the caret in its search box.</summary>
    [RelayCommand]
    private void FocusSearch()
    {
        Rail = RailState.Full;
        IsSearching = true;
    }


    partial void OnActivePanelChanged(string value) => NotifyVisibility();
    partial void OnRailChanged(RailState value) => NotifyVisibility();

    private void NotifyVisibility() => OnPropertyChanged(nameof(IsPanelVisible));

    /// <summary>Two states now, not three: with no icon rail there is nothing
    /// meaningful between "shown" and "hidden".</summary>
    [RelayCommand]
    public void CycleRail() => Rail = Rail == RailState.Hidden ? RailState.Full : RailState.Hidden;

    [RelayCommand]
    public void ShowPanel(string? panel)
    {
        if (string.IsNullOrEmpty(panel)) return;

        // Selecting the panel already showing expands a collapsed sidebar
        // rather than doing nothing.
        if (ActivePanel == panel && Rail == RailState.Full) return;

        ActivePanel = panel;
        Rail = RailState.Full;
    }

    public async Task InitializeAsync()
    {
        if (_places is null) return;

        await _places.ImportExistingAsync(CancellationToken.None).ConfigureAwait(false);
        await ReloadAsync().ConfigureAwait(false);
    }

    public async Task ReloadAsync()
    {
        if (_places is null) return;

        var groups = await _places.GetPlacesAsync(CancellationToken.None).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Groups.Clear();
            foreach (var group in groups)
                Groups.Add(new PlaceGroupViewModel(group));

            // The rows are new objects, so the current-location mark has to be
            // re-applied — a refresh would otherwise silently clear the
            // highlight and leave the sidebar looking like nothing is open.
            SetCurrentPath(CurrentPath);
        });
    }

    public Task PinAsync(string path)
        => _places?.PinAsync(path, null, CancellationToken.None).AsTask() ?? Task.CompletedTask;
}

public sealed class PlaceGroupViewModel(PlaceGroup group)
{
    public string Label { get; } = group.Label;
    public IReadOnlyList<PlaceItemViewModel> Places { get; } =
        group.Places.Select(p => new PlaceItemViewModel(p)).ToList();
}

public sealed partial class PlaceItemViewModel(Place place) : ObservableObject
{
    /// <summary>
    /// True when this place is what the active pane is showing, which draws the
    /// accent bar. Observable rather than computed once, because navigation
    /// changes it long after the list was built.
    /// </summary>
    [ObservableProperty] private bool _isCurrent;

    public string Id { get; } = place.Id;
    public string Label { get; } = place.Label;
    public string Path { get; } = place.Path;
    public string Icon { get; } = place.Icon;
    public bool IsAvailable { get; } = place.IsAvailable;

    /// <summary>Unreachable entries render dimmed and in place — never hidden,
    /// never silently dropped.</summary>
    public double Opacity => IsAvailable ? 1.0 : 0.4;

    public bool HasCapacity => place.CapacityBytes is > 0;

    public double UsedFraction => place.CapacityBytes is > 0 && place.FreeBytes is { } free
        ? 1.0 - (double)free / place.CapacityBytes.Value
        : 0;

    public string CapacityText => place.CapacityBytes is > 0 && place.FreeBytes is { } free
        ? $"{ByteSize.Format(free)} free"
        : "";

}
