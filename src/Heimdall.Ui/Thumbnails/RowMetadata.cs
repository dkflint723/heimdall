using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Rove.Core.FileSystem;

namespace Rove.Ui.Thumbnails;

/// <summary>
/// Fills a TextBlock with a file's inline metadata, asynchronously.
///
/// Same shape as the thumbnail loader and for the same reason: the list
/// virtualizes, so attaching to the realized control makes the work
/// viewport-driven without the collection having to hold anything extra.
/// </summary>
public static class RowMetadata
{
    private const int MaxCached = 2000;

    private static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();
    private static readonly object Gate = new();

    public static IFileMetadataProvider? Provider { get; set; }

    public static readonly AttachedProperty<FileEntry?> EntryProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, FileEntry?>("Entry", typeof(RowMetadata));

    /// <summary>Same mechanism, different fact: the POSIX mode string.</summary>
    public static readonly AttachedProperty<FileEntry?> AccessProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, FileEntry?>("Access", typeof(RowMetadata));

    static RowMetadata()
    {
        EntryProperty.Changed.AddClassHandler<TextBlock>((text, e) =>
            OnEntryChanged(text, e.NewValue as FileEntry?, access: false));

        AccessProperty.Changed.AddClassHandler<TextBlock>((text, e) =>
            OnEntryChanged(text, e.NewValue as FileEntry?, access: true));
    }

    public static void SetEntry(TextBlock target, FileEntry? value)
        => target.SetValue(EntryProperty, value);

    public static FileEntry? GetEntry(TextBlock target) => target.GetValue(EntryProperty);

    public static void SetAccess(TextBlock target, FileEntry? value)
        => target.SetValue(AccessProperty, value);

    public static FileEntry? GetAccess(TextBlock target) => target.GetValue(AccessProperty);

    private static async void OnEntryChanged(TextBlock target, FileEntry? entry, bool access)
    {
        // async void: nothing may escape, or scrolling crashes the app.
        try
        {
            target.Text = "";

            if (Provider is null || entry is not { } value) return;
            if (!access && !Provider.CanDescribe(value.FullPath, value.IsDirectory)) return;

            // Prefixed so the two facts about one path do not share a slot.
            var key = (access ? "a:" : "m:") + value.FullPath;

            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    target.Text = cached ?? "";
                    return;
                }
            }

            var described = await (access
                    ? Provider.DescribeAccessAsync(value.FullPath, value.IsDirectory, CancellationToken.None)
                    : Provider.DescribeAsync(value.FullPath, value.IsDirectory, CancellationToken.None))
                .ConfigureAwait(true);

            Remember(key, described);

            // The container may have been recycled onto another file while we
            // were reading; only paint if it still wants this one.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var current = access ? GetAccess(target) : GetEntry(target);
                if (current?.FullPath == value.FullPath) target.Text = described ?? "";
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rove] metadata failed: {ex.Message}");
        }
    }

    private static void Remember(string key, string? value)
    {
        lock (Gate)
        {
            if (!Cache.TryAdd(key, value)) return;

            Order.Enqueue(key);
            while (Order.Count > MaxCached) Cache.Remove(Order.Dequeue());
        }
    }
}
