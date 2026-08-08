using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Session;

namespace Heimdall.Ui.ViewModels;

/// <summary>Grouping the listing, and the header each run gets.</summary>
public sealed partial class PaneViewModel
{
    // ---- grouping ---------------------------------------------------------

    [ObservableProperty] private GroupMode _groupBy = GroupMode.None;

    public bool IsGroupedByName => GroupBy == GroupMode.Name;

    public bool IsGroupedBySize => GroupBy == GroupMode.Size;

    public bool IsGroupedByModified => GroupBy == GroupMode.Modified;

    public bool IsGroupedByKind => GroupBy == GroupMode.Kind;

    public bool IsUngrouped => GroupBy == GroupMode.None;

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

    public string? HeaderFor(string fullPath)
        => _groupHeaders.TryGetValue(fullPath, out var label) ? label : null;
}
