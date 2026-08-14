namespace Vaktari.Core.FileSystem;

/// <summary>
/// A decoded icon, as raw pixels.
///
/// **Pixels rather than a path, unlike <see cref="IIconThemeProvider"/>**, and
/// the difference is not a preference. A freedesktop theme is files on disk, so
/// a path is the natural answer and a miss costs nothing. The Windows shell has
/// no such file: an icon is composed on demand from a resource in some DLL, an
/// overlay, and possibly a thumbnail, and the only thing it hands back is a
/// bitmap. There is nothing to point at.
/// </summary>
/// <param name="Bgra">Premultiplied BGRA, top row first — what both the shell
/// and Avalonia use, so nothing has to be swizzled in between.</param>
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

/// <summary>
/// The desktop's own icon for one particular file.
///
/// Distinct from the icon THEME provider, which answers "what does this desktop
/// call the icon for a text file" and is the same answer for every text file.
/// This answers "what does this desktop draw for THIS file", which on Windows
/// can differ per file: an executable carries its own icon, a shortcut gets an
/// overlay, a folder can be given a custom one.
///
/// Null where the platform has no such notion, which is the freedesktop world —
/// there the theme provider already is the answer.
/// </summary>
public interface IFileIconProvider
{
    /// <summary>
    /// The icon this desktop would draw, at about this size, or null.
    ///
    /// **Never throws and never blocks for long.** It is called once per
    /// visible row while a listing is being drawn, so a slow answer is a slow
    /// listing, and an exception is a listing that does not appear at all.
    /// </summary>
    IconPixels? IconFor(string path, bool isDirectory, int size);
}
