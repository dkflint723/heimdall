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

    /// <summary>
    /// Per-row cancellation, mirroring ThumbnailImage. The stale-check before
    /// painting already stopped the WRONG icon appearing; this stops the work
    /// happening at all for a row that has scrolled away. Task.Run with a token
    /// will not interrupt a lookup already running, but it does drop one still
    /// queued — which is the case that matters, because a fast scroll queues far
    /// more than it starts.
    /// </summary>
    private static readonly AttachedProperty<CancellationTokenSource?> TokenProperty =
        AvaloniaProperty.RegisterAttached<Image, CancellationTokenSource?>("Token", typeof(RowIcon));

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
        if (image.GetValue(TokenProperty) is { } previous)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        image.SetValue(TokenProperty, cts);

        // Captured before awaiting: a later call on this same Image disposes the
        // source while we are suspended, and reading .Token on a disposed source
        // throws — the token struct stays safe to query.
        var token = cts.Token;

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
                Paint(FileTypeIcon.For(value.Name, value.IsDirectory));
                return;
            }

            var size = image.GetValue(SizeProperty);

            // Only the filesystem lookup goes off-thread. Building the drawable
            // creates Avalonia objects and reads application resources, so it
            // must happen on the UI thread — doing it in the Task.Run crashed
            // the process outright.
            // The drawn glyph goes up immediately so a row is never blank while
            // the theme lookup runs.
            Paint(FileTypeIcon.For(value.Name, value.IsDirectory));

            var file = await Task.Run(
                    () => IconLoader.ResolveFile(value.FullPath, value.IsDirectory, size), token)
                                 .ConfigureAwait(true);

            if (file is null) return;

            // Default priority, deliberately.
            //
            // This was Background for a while, added while chasing a 44-second
            // navigation stall on the theory that row decoration was starving
            // the dispatcher. It was not — the cause was an xdg-mime subprocess
            // per row exhausting the thread pool — and the timings got WORSE
            // with it, not better. A change made for a reason that turned out to
            // be false does not get to stay on the grounds that it is already
            // there. Cancellation above now bounds the backlog properly.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Containers recycle while we work; only replace if this row
                // still wants the file we resolved.
                if (IconLoader.Load(file) is { } icon) Paint(icon);
            });
        }
        catch (OperationCanceledException)
        {
            // The row scrolled away. Expected, and not worth a line.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[heimdall] icon failed: {ex.Message}");
        }
    }
}
