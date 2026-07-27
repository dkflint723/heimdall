using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Heimdall.Ui.Thumbnails;

/// <summary>
/// Attached property that loads a thumbnail into an Image asynchronously.
///
/// Attached rather than a row view model wrapper because the list virtualizes:
/// only visible rows have a realized Image, so binding the path here makes
/// loading viewport-driven for free, with no change to what the collection
/// holds.
/// </summary>
public static class ThumbnailImage
{
    public static readonly AttachedProperty<string?> PathProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Path", typeof(ThumbnailImage));

    public static readonly AttachedProperty<int> SizeProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("Size", typeof(ThumbnailImage), 32);

    private static readonly AttachedProperty<CancellationTokenSource?> TokenProperty =
        AvaloniaProperty.RegisterAttached<Image, CancellationTokenSource?>("Token", typeof(ThumbnailImage));

    static ThumbnailImage()
    {
        PathProperty.Changed.AddClassHandler<Image>((image, e) =>
            OnPathChanged(image, e.NewValue as string));
    }

    public static void SetPath(Image image, string? value) => image.SetValue(PathProperty, value);
    public static string? GetPath(Image image) => image.GetValue(PathProperty);

    public static void SetSize(Image image, int value) => image.SetValue(SizeProperty, value);
    public static int GetSize(Image image) => image.GetValue(SizeProperty);

    private static async void OnPathChanged(Image image, string? path)
    {
        // async void: nothing may escape, or a scroll turns into a crash.
        try
        {
            // Containers are recycled as you scroll, so the previous request is
            // abandoned — otherwise a fast scroll leaves the wrong picture on a
            // row.
            if (image.GetValue(TokenProperty) is { } previous)
            {
                previous.Cancel();
                previous.Dispose();
            }

            image.Source = null;

            if (string.IsNullOrEmpty(path) || !ThumbnailLoader.CanThumbnail(path))
            {
                image.SetValue(TokenProperty, null);
                image.IsVisible = false;
                return;
            }

            var cts = new CancellationTokenSource();
            image.SetValue(TokenProperty, cts);

            // Captured before awaiting. A later call on this same Image disposes
            // the source while we are still suspended, and reading .Token on a
            // disposed source throws — whereas the token struct itself stays
            // safe to query.
            var token = cts.Token;

            var bitmap = await ThumbnailLoader
                .LoadAsync(path, image.GetValue(SizeProperty), token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            // The container may have been recycled onto a different file while
            // we were decoding; only paint if it still wants this one.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (GetPath(image) != path) return;

                image.Source = bitmap;
                image.IsVisible = bitmap is not null;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[heimdall] thumbnail failed: {ex.Message}");
        }
    }
}
