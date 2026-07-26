using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Rove.Core.FileSystem;
using Rove.Core.Places;
using Rove.Core.Search;
using Rove.Core.Session;

namespace Rove.Ui.ViewModels;

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
        IFileSystemProvider fs,
        IPlacesProvider? places,
        ISearchProvider? search = null,
        Func<string?>? currentPath = null)
    {
        _places = places;
        Tree = new FolderTreeViewModel(fs);
        Search = new SearchViewModel(search, currentPath ?? (() => null));

        if (places is not null)
            places.PlacesChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = ReloadAsync());
    }

    public ObservableCollection<PlaceGroupViewModel> Groups { get; } = new();
    public FolderTreeViewModel Tree { get; }
    public SearchViewModel Search { get; }

    [ObservableProperty] private string _activePanel = "places";
    [ObservableProperty] private RailState _rail = RailState.Full;
    [ObservableProperty] private double _width = 210;

    public bool IsPlacesVisible => Rail == RailState.Full && ActivePanel == "places";
    public bool IsTreeVisible   => Rail == RailState.Full && ActivePanel == "tree";
    public bool IsSearchVisible => Rail == RailState.Full && ActivePanel == "search";
    public bool IsRailVisible   => Rail != RailState.Hidden;
    public bool IsPanelVisible  => Rail == RailState.Full;

    partial void OnActivePanelChanged(string value) => NotifyVisibility();
    partial void OnRailChanged(RailState value) => NotifyVisibility();

    private void NotifyVisibility()
    {
        OnPropertyChanged(nameof(IsPlacesVisible));
        OnPropertyChanged(nameof(IsTreeVisible));
        OnPropertyChanged(nameof(IsSearchVisible));
        OnPropertyChanged(nameof(IsRailVisible));
        OnPropertyChanged(nameof(IsPanelVisible));
    }

    /// <summary>Ctrl+B cycles rather than toggles, so the icon rail is a state
    /// of the sidebar rather than a separate design.</summary>
    [RelayCommand]
    public void CycleRail() => Rail = Rail switch
    {
        RailState.Full => RailState.RailOnly,
        RailState.RailOnly => RailState.Hidden,
        _ => RailState.Full,
    };

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

public sealed class PlaceItemViewModel(Place place)
{
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
        ? $"{Human(free)} free"
        : "";

    private static string Human(long bytes) => bytes switch
    {
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}
