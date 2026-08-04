using Heimdall.Core;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Sharing;
using Heimdall.Core.Places;
using Heimdall.Core.Search;

namespace Heimdall.Windows;

/// <summary>
/// The Windows composition root. Everything OS-specific is built here, so the UI
/// project holds exactly one reference to a platform type — inside one
/// OperatingSystem.IsWindows() check.
///
/// **All eleven required members are real**, because the UI reads every one of
/// them while constructing the main window: ShellViewModel takes Operations,
/// Launcher, Search, Scripts and Templates alongside the obvious ones, so a
/// throwing stub anywhere here means no window at all. That is why step 3 built
/// more than the two providers WINDOWS.md §7 names.
///
/// **Real does not mean complete.** Trash fails rather than deleting, tags store
/// nothing pending a design decision, and the open-with list is empty — each
/// documented on the class that does it.
///
/// The seven nullable members still return null, which is the interface working
/// as designed rather than a gap being papered over.
/// </summary>
public sealed class WindowsPlatform : IPlatform
{
    private readonly WindowsPropertiesProvider _properties = new();

    public WindowsPlatform(string stateDirectory)
    {
        StateDirectory = stateDirectory;
        Places = new WindowsPlacesProvider(stateDirectory);
        Scripts = new WindowsScriptRunner(stateDirectory);
    }

    /// <summary>Where this application's own per-user state lives.</summary>
    public string StateDirectory { get; }

    public string Name => "windows";

    // ---- Required. ---------------------------------------------------------

    public IFileSystemProvider FileSystem { get; } = new WindowsFileSystemProvider();
    public IFileOperations Operations { get; } = new WindowsFileOperations();
    public IApplicationLauncher Launcher { get; } = new WindowsLauncher();
    public IPlacesProvider Places { get; }
    public ISearchProvider Search { get; } = new WindowsSearchProvider();
    public IThumbnailProvider Thumbnails { get; } = new WindowsThumbnailProvider();
    public IFileMetadataProvider Metadata { get; } = new WindowsMetadataProvider();

    public IPropertiesProvider Properties => _properties;

    public IScriptRunner Scripts { get; }

    public ITagStore Tags { get; } = new WindowsTagStore();

    public ITemplateProvider Templates { get; } = new WindowsTemplates();

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
}
