using Rove.Core.FileSystem;
using Rove.Core.Places;
using Rove.Core.Search;

namespace Rove.Core;

/// <summary>
/// Everything the application needs from the operating system, in one object.
///
/// A single composition seam rather than eight separate ones: the UI takes an
/// IPlatform and never names a platform type, so the whole OS-specific surface
/// is chosen in exactly one guarded place. That is also what lets the platform
/// assemblies be annotated Linux-only or Windows-only without the analyser
/// complaining at every call site.
/// </summary>
public interface IPlatform
{
    string Name { get; }

    IFileSystemProvider FileSystem { get; }
    IFileOperations Operations { get; }
    IApplicationLauncher Launcher { get; }
    IPlacesProvider Places { get; }
    ISearchProvider Search { get; }
    IThumbnailProvider Thumbnails { get; }
    IFileMetadataProvider Metadata { get; }
    IPropertiesProvider Properties { get; }

    /// <summary>Null where the platform exposes nothing editable.</summary>
    IAccessEditor? AccessEditor { get; }

    IScriptRunner Scripts { get; }

    ITagStore Tags { get; }

    /// <summary>Null where the desktop exposes no readable theme.</summary>
    IThemeProvider? Theme { get; }

    /// <summary>Null where the desktop ships no icon theme we can read.</summary>
    IIconThemeProvider? Icons { get; }
}
