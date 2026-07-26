using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Heimdall.Core;
using Heimdall.Core.FileSystem;

namespace Heimdall.Ui.Thumbnails;

/// <summary>One tag as the row draws it: a name, and a colour derived from it.</summary>
public sealed record TagChip(string Name, IBrush Colour);

/// <summary>
/// Fills an ItemsControl with a file's tags. Same viewport-driven attached
/// property pattern as thumbnails and metadata — reading an extended attribute
/// per row in a directory of 200k files would be absurd; per *visible* row it
/// costs nothing.
/// </summary>
public static class RowTags
{
    // Okabe-Ito again, so tag colours obey the same palette as the rest of the
    // app. Colour is chosen by a stable hash of the name, which means no colour
    // assignment has to be stored anywhere and the same tag always looks the
    // same — and since the name is always drawn, the colour is decoration.
    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#E69F00")),
        new SolidColorBrush(Color.Parse("#56B4E9")),
        new SolidColorBrush(Color.Parse("#009E73")),
        new SolidColorBrush(Color.Parse("#F0E442")),
        new SolidColorBrush(Color.Parse("#0072B2")),
        new SolidColorBrush(Color.Parse("#D55E00")),
        new SolidColorBrush(Color.Parse("#CC79A7")),
    ];

    public static ITagStore? Store { get; set; }

    public static IBrush ColourFor(string tag)
    {
        // Deliberately not string.GetHashCode(): that is randomised per process
        // in .NET, so a tag would change colour between launches.
        var hash = 17;
        foreach (var c in tag) hash = hash * 31 + char.ToLowerInvariant(c);

        return Palette[Math.Abs(hash) % Palette.Length];
    }

    public static readonly AttachedProperty<FileEntry?> EntryProperty =
        AvaloniaProperty.RegisterAttached<ItemsControl, FileEntry?>("Entry", typeof(RowTags));

    static RowTags()
    {
        EntryProperty.Changed.AddClassHandler<ItemsControl>((control, e) =>
            OnEntryChanged(control, e.NewValue as FileEntry?));
    }

    public static void SetEntry(ItemsControl target, FileEntry? value)
        => target.SetValue(EntryProperty, value);

    public static FileEntry? GetEntry(ItemsControl target) => target.GetValue(EntryProperty);

    private static async void OnEntryChanged(ItemsControl target, FileEntry? entry)
    {
        try
        {
            target.ItemsSource = null;

            if (Store is null || entry is not { } value) return;

            var tags = await Store.GetAsync(value.FullPath, CancellationToken.None)
                                  .ConfigureAwait(true);

            if (tags.Count == 0) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Containers are recycled; only paint if this row still wants
                // the file we just read.
                if (GetEntry(target)?.FullPath != value.FullPath) return;

                target.ItemsSource = tags
                    .Select(t => new TagChip(t, ColourFor(t)))
                    .ToList();
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[heimdall] tags failed: {ex.Message}");
        }
    }
}

/// <summary>Exposes the tag colour to markup, so a swatch in the sidebar and a
/// chip in a row are guaranteed to agree.</summary>
public static class TagBrush
{
    public static readonly Avalonia.Data.Converters.IValueConverter ForName =
        new Avalonia.Data.Converters.FuncValueConverter<string, IBrush>(
            name => RowTags.ColourFor(name ?? ""));
}
