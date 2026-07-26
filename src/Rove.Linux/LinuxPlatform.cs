using Rove.Core;
using Rove.Core.FileSystem;
using Rove.Core.Places;
using Rove.Core.Search;

namespace Rove.Linux;

/// <summary>
/// The Linux composition root. Everything OS-specific is built here, so the UI
/// project holds exactly one reference to a platform type — inside one
/// OperatingSystem.IsLinux() check.
/// </summary>
public sealed class LinuxPlatform : IPlatform
{
    private readonly LinuxPropertiesProvider _properties = new();

    public LinuxPlatform(string stateDirectory)
    {
        Places = new LinuxPlacesProvider(stateDirectory);
        Tags = new LinuxTagStore(stateDirectory);
        Icons = new XdgIconTheme(Theme?.Read()?.IconTheme);
    }

    public string Name => "linux";

    public IFileSystemProvider FileSystem { get; } = new LinuxFileSystemProvider();
    public IFileOperations Operations { get; } = new LinuxFileOperations();
    public IApplicationLauncher Launcher { get; } = new LinuxLauncher();
    public IPlacesProvider Places { get; }
    public ISearchProvider Search { get; } = new LinuxSearchProvider();
    public IThumbnailProvider Thumbnails { get; } = new XdgThumbnailProvider();
    public IFileMetadataProvider Metadata { get; } = new LinuxMetadataProvider();

    public IPropertiesProvider Properties => _properties;

    // The same object serves both — reading and writing permissions share the
    // mode-bit mapping, and splitting them would duplicate it.
    public IAccessEditor? AccessEditor => _properties;

    public IScriptRunner Scripts { get; } = new LinuxScriptRunner();

    public ITagStore Tags { get; }

    public IThemeProvider? Theme { get; } = new KdeThemeProvider();

    /// <summary>
    /// Built from the theme name Plasma reports, so it follows whatever the
    /// user picked in System Settings rather than assuming Breeze.
    /// </summary>
    public IIconThemeProvider? Icons { get; }
}
