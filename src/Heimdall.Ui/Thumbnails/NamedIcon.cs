using Avalonia;
using Avalonia.Controls;
using Heimdall.Core.FileSystem;

namespace Heimdall.Ui.Thumbnails;

/// <summary>
/// An icon looked up by NAME rather than by file, for the sidebar.
///
/// <see cref="RowIcon"/> answers "what icon does this file get", which needs a
/// path and a mime lookup. The sidebar asks a different question: Place already
/// carries an <c>Icon</c> token, and that token has been populated since the
/// places provider was written while **nothing has ever rendered it** — the
/// same shape of defect as a store event with no subscriber.
///
/// **Resolution is synchronous and that is deliberate.** There are a dozen or so
/// sidebar rows, they are created once, and
/// <see cref="IIconThemeProvider.Resolve"/> is a directory lookup against a
/// cached theme index — not the per-row mime work that made a folder listing
/// take 44 seconds. Doing it off-thread would mean marshalling back to build the
/// drawable, since Avalonia objects must be constructed on the UI thread.
/// </summary>
public static class NamedIcon
{
    public static readonly AttachedProperty<string?> NameProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Name", typeof(NamedIcon));

    public static readonly AttachedProperty<int> SizeProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("Size", typeof(NamedIcon), 16);

    static NamedIcon()
    {
        NameProperty.Changed.AddClassHandler<Image>((image, _) => Apply(image));
        SizeProperty.Changed.AddClassHandler<Image>((image, _) => Apply(image));
    }

    public static void SetName(Image image, string? value) => image.SetValue(NameProperty, value);
    public static string? GetName(Image image) => image.GetValue(NameProperty);

    public static void SetSize(Image image, int value) => image.SetValue(SizeProperty, value);
    public static int GetSize(Image image) => image.GetValue(SizeProperty);

    private static void Apply(Image image)
    {
        var token = image.GetValue(NameProperty);

        if (string.IsNullOrEmpty(token))
        {
            image.Source = null;
            return;
        }

        var size = Math.Max(8, image.GetValue(SizeProperty));
        var file = IconLoader.Provider?.Resolve(Candidates(token), size);

        // Null rather than a fallback glyph. A sidebar row whose icon is missing
        // should read as a row with no icon, not as a row for a broken thing —
        // and a wrong icon is worse than no icon, which is the rule the SVG
        // renderer already follows.
        image.Source = file is null ? null : IconLoader.Load(file);
    }

    /// <summary>
    /// Heimdall's own icon tokens mapped to freedesktop icon names, most
    /// specific first.
    ///
    /// **Several candidates per token on purpose.** Icon naming is a convention
    /// and every theme covers a different subset — the provider takes a list for
    /// exactly this reason. The tokens themselves are Heimdall's (they read like
    /// a UI icon set), so this is the one place that knows how they translate.
    /// </summary>
    private static IReadOnlyList<string> Candidates(string token) => token switch
    {
        "home" => ["user-home", "folder-home", "go-home"],
        "desktop" => ["user-desktop", "folder-desktop"],
        "download" => ["folder-download", "folder-downloads", "download"],
        "file-text" => ["folder-documents", "folder-document", "document"],
        "photo" => ["folder-pictures", "folder-images", "folder-picture"],
        "music" => ["folder-music", "folder-sound"],
        "video" => ["folder-videos", "folder-video"],
        "bookmark" => ["folder-bookmark", "bookmarks", "user-bookmarks"],
        "server" => ["network-server", "network-workgroup", "folder-network"],
        "usb" => ["drive-removable-media-usb", "drive-removable-media", "media-removable"],
        "device-desktop" => ["drive-harddisk", "drive-harddisk-system", "drive-multidisk"],
        "trash" => ["user-trash", "user-trash-full"],

        // The two virtual listings. Dolphin uses these same names for its own
        // Recent entries, which is why they look right beside everything else.
        "recent-files" => ["document-open-recent", "document-open-recent-symbolic", "clock"],
        "recent-locations" => ["folder-open-recent", "document-open-recent", "folder-recent"],

        // An unknown token is not an error — it is a place kind nobody has
        // mapped yet, and it should show as a plain row rather than a wrong one.
        _ => [token],
    };
}
