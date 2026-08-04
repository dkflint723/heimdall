using Heimdall.Core;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Sharing;
using Heimdall.Core.Places;
using Heimdall.Core.Search;

namespace Heimdall.Windows;

/// <summary>
/// The Windows composition root, and at this stage a skeleton: it exists so the
/// scaffolding can be proved before any provider is written.
///
/// The seven nullable members of <see cref="IPlatform"/> return null, which is
/// the interface working as designed rather than a gap being papered over — the
/// UI already handles a platform that cannot theme, share, discover or trash.
/// The eleven required members throw, so the first thing anyone builds on top of
/// this fails loudly and names itself.
///
/// Replace them one at a time, in the order WINDOWS.md §7 sets out: filesystem
/// and places first, and the window lists C:\ with drives in the sidebar.
/// </summary>
public sealed class WindowsPlatform : IPlatform
{
    public WindowsPlatform(string stateDirectory) => StateDirectory = stateDirectory;

    /// <summary>
    /// Where this application's own per-user state lives. Held rather than used:
    /// the Linux places provider and tag store both take it, and their Windows
    /// counterparts will want it for pinned folders and the tag sidecar.
    /// </summary>
    public string StateDirectory { get; }

    public string Name => "windows";

    // ---- Required. Each throws until it is written. -----------------------

    public IFileSystemProvider FileSystem => throw NotYet(nameof(IFileSystemProvider));
    public IFileOperations Operations => throw NotYet(nameof(IFileOperations));
    public IApplicationLauncher Launcher => throw NotYet(nameof(IApplicationLauncher));
    public IPlacesProvider Places => throw NotYet(nameof(IPlacesProvider));
    public ISearchProvider Search => throw NotYet(nameof(ISearchProvider));
    public IThumbnailProvider Thumbnails => throw NotYet(nameof(IThumbnailProvider));
    public IFileMetadataProvider Metadata => throw NotYet(nameof(IFileMetadataProvider));
    public IPropertiesProvider Properties => throw NotYet(nameof(IPropertiesProvider));
    public IScriptRunner Scripts => throw NotYet(nameof(IScriptRunner));
    public ITagStore Tags => throw NotYet(nameof(ITagStore));
    public ITemplateProvider Templates => throw NotYet(nameof(ITemplateProvider));

    // ---- Optional. Null is a legitimate answer, now and possibly forever. --

    /// <summary>
    /// Null, and likely permanently. POSIX modes have no meaning here and NTFS
    /// ACLs are a different model, not a richer version of the same one.
    /// </summary>
    public IAccessEditor? AccessEditor => null;

    /// <summary>
    /// Null for now. copyparty runs on Windows wherever Python does, so this is
    /// reachable — but CopypartyShare lives in Heimdall.Linux and is mostly path
    /// handling, so the move to Core comes first.
    /// </summary>
    public IFileSharing? Sharing => null;

    /// <summary>
    /// Null, deliberately. Mapped network drives arrive as ordinary drive
    /// letters through <see cref="Places"/>, so there is nothing left for a
    /// separate remote-mount concept to describe.
    /// </summary>
    public IRemoteMounts? Remotes => null;

    /// <summary>Null. Avahi has no Windows equivalent worth the effort.</summary>
    public INetworkDiscovery? Discovery => null;

    /// <summary>
    /// Null for now, and the cheapest of the remaining work: dark mode and the
    /// accent colour are two registry reads, no COM.
    /// </summary>
    public IThemeProvider? Theme => null;

    /// <summary>
    /// Null, and staying that way for a while. Windows has per-file icons from
    /// the shell rather than a theme of named icons, so there is nothing for
    /// this interface to resolve against. Null falls back to the hand-drawn
    /// glyphs in IconLoader.Fallback and SidebarIcon, which is why they are
    /// drawn rather than themed.
    /// </summary>
    public IIconThemeProvider? Icons => null;

    /// <summary>
    /// Null. Listing, restoring and emptying the Recycle Bin all need the same
    /// COM surface as trashing itself, and none of it is required to browse
    /// files.
    /// </summary>
    public ITrashMaintenance? TrashMaintenance => null;

    private static NotImplementedException NotYet(string member) =>
        new($"{member} has no Windows implementation yet (WINDOWS.md §7).");
}
