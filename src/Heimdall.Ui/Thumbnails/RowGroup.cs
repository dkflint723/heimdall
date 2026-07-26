using Avalonia;
using Avalonia.Controls;
using Heimdall.Core.FileSystem;
using Heimdall.Ui.ViewModels;

namespace Heimdall.Ui.Thumbnails;

/// <summary>
/// Shows a group header above the first row of each group.
///
/// A header per run rather than a separate item type in the collection: mixing
/// headers into <c>Entries</c> would mean the list no longer holds only
/// <see cref="FileEntry"/>, which breaks selection, the three layouts that share
/// it, and the stat-free struct the enumerator produces. The pane works out
/// which paths start a group; the row just asks.
/// </summary>
public static class RowGroup
{
    public static readonly AttachedProperty<FileEntry?> EntryProperty =
        AvaloniaProperty.RegisterAttached<Control, FileEntry?>("Entry", typeof(RowGroup));

    public static void SetEntry(Control control, FileEntry? value)
        => control.SetValue(EntryProperty, value);

    public static FileEntry? GetEntry(Control control) => control.GetValue(EntryProperty);

    static RowGroup()
    {
        EntryProperty.Changed.AddClassHandler<Control>((control, args) =>
            Apply(control, args.NewValue as FileEntry?));
    }

    private static void Apply(Control control, FileEntry? entry)
    {
        // Hidden by default: most rows are not the first of a group, and a
        // header that briefly appears on every row while scrolling would be
        // worse than none.
        control.IsVisible = false;

        if (entry is not { } value) return;

        // The pane owns the map. Walking up rather than binding to it because
        // the row template has no path to the pane that survives recycling.
        for (var node = control as Control; node is not null; node = node.Parent as Control)
        {
            if (node.DataContext is not PaneViewModel pane) continue;

            if (pane.HeaderFor(value.FullPath) is { } label)
            {
                if (control is ContentControl content) content.Content = label;
                control.IsVisible = true;
            }

            return;
        }
    }
}
