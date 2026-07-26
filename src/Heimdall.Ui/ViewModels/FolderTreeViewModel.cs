using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using Rove.Core.FileSystem;

namespace Rove.Ui.ViewModels;

/// <summary>
/// The tree panel. Children load on expand, never eagerly — walking a whole
/// home directory to populate a sidebar is exactly the kind of work that makes
/// a file manager feel slow.
/// </summary>
public sealed class FolderTreeViewModel(IFileSystemProvider fs)
{
    public ObservableCollection<FolderNode> Roots { get; } = new()
    {
        new FolderNode(fs, "/", "/"),
        new FolderNode(fs, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Home"),
    };
}

public sealed partial class FolderNode : ObservableObject
{
    private readonly IFileSystemProvider _fs;
    private bool _loaded;

    public FolderNode(IFileSystemProvider fs, string path, string? label = null)
    {
        _fs = fs;
        Path = path;
        Label = label ?? System.IO.Path.GetFileName(path.TrimEnd('/'));

        // Children stay empty until expanded; HasChildren drives the expander
        // so we never stat a directory just to decide whether to draw an arrow.
    }

    public string Path { get; }
    public string Label { get; }

    public ObservableCollection<FolderNode> Children { get; } = new();

    /// <summary>Assume expandable until proven otherwise — checking would mean
    /// a readdir per node, which is the cost this design exists to avoid.</summary>
    public bool HasChildren => !_loaded || Children.Count > 0;

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded) _ = LoadChildrenAsync();
    }

    private async Task LoadChildrenAsync()
    {
        _loaded = true;

        var found = new List<FolderNode>();

        try
        {
            var options = new ListingOptions { IncludeHidden = false, BatchSize = 200 };

            await foreach (var batch in _fs.EnumerateAsync(Path, options, CancellationToken.None)
                                           .ConfigureAwait(false))
            {
                foreach (var entry in batch)
                    if (entry.IsDirectory)
                        found.Add(new FolderNode(_fs, entry.FullPath));
            }
        }
        catch
        {
            // Unreadable directory — collapse back to nothing rather than
            // surfacing a permissions error in a tree node.
        }

        found.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Children.Clear();
            foreach (var node in found) Children.Add(node);
        });
    }
}
