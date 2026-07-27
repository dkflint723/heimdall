using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Heimdall.Core.FileSystem;

namespace Heimdall.Ui.Thumbnails;

/// <summary>
/// Fills an Image with the desktop's themed icon for a file. Same viewport-driven
/// attached-property shape as thumbnails and metadata, and the same reason:
/// only realized rows pay for it.
/// </summary>
public static class RowIcon
{
    public static readonly AttachedProperty<FileEntry?> EntryProperty =
        AvaloniaProperty.RegisterAttached<Image, FileEntry?>("Entry", typeof(RowIcon));

    public static readonly AttachedProperty<int> SizeProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("Size", typeof(RowIcon), 24);

    static RowIcon()
    {
        EntryProperty.Changed.AddClassHandler<Image>((image, e) =>
            OnEntryChanged(image, e.NewValue as FileEntry?));
    }

    public static void SetEntry(Image image, FileEntry? value) => image.SetValue(EntryProperty, value);
    public static FileEntry? GetEntry(Image image) => image.GetValue(EntryProperty);

    public static void SetSize(Image image, int value) => image.SetValue(SizeProperty, value);
    public static int GetSize(Image image) => image.GetValue(SizeProperty);

    private static async void OnEntryChanged(Image image, FileEntry? entry)
    {
        try
        {
            image.Source = null;
            image.IsVisible = false;

            if (entry is not { } value) return;

            // Always show something: the themed icon when the desktop has one,
            // otherwise the drawn glyph. One element, one source — nothing to
            // overlap.
            void Paint(IImage icon)
            {
                if (GetEntry(image)?.FullPath != value.FullPath) return;

                image.Source = icon;
                image.IsVisible = true;
            }

            if (IconLoader.Provider is null)
            {
                Paint(IconLoader.Fallback(value.IsDirectory));
                return;
            }

            var size = image.GetValue(SizeProperty);

            // Only the filesystem lookup goes off-thread. Building the drawable
            // creates Avalonia objects and reads application resources, so it
            // must happen on the UI thread — doing it in the Task.Run crashed
            // the process outright.
            // The drawn glyph goes up immediately so a row is never blank while
            // the theme lookup runs.
            Paint(IconLoader.Fallback(value.IsDirectory));

            var file = await Task.Run(() => IconLoader.ResolveFile(value.FullPath, value.IsDirectory, size))
                                 .ConfigureAwait(true);

            if (file is null) return;

            // BACKGROUND priority, and this is the fix for a 33-second
            // navigation. Row decoration is fire-and-forget per realized row,
            // so cycling the three layouts over a 300-item folder queues ~900 of
            // these — and a navigation's own dispatcher hops went to the BACK of
            // that queue. Eight files took 33.7 s to list because the UI thread
            // was busy painting icons for rows nobody was looking at any more.
            //
            // At Background these yield to navigation, which runs at Normal. The
            // work still happens; it simply stops holding the application
            // hostage. The row is never blank meanwhile — the drawn glyph above
            // went up before any of this started.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Containers recycle while we work; only replace if this row
                // still wants the file we resolved.
                if (IconLoader.Load(file) is { } icon) Paint(icon);
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[heimdall] icon failed: {ex.Message}");
        }
    }
}
