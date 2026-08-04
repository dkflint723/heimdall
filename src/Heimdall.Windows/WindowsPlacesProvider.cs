using System.Text.Json;
using System.Text.Json.Serialization;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Places;

namespace Heimdall.Windows;

public sealed record PinnedPlace(string Path, string Label);

[JsonSerializable(typeof(List<PinnedPlace>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class PinnedPlacesJsonContext : JsonSerializerContext;

/// <summary>
/// The sidebar's data source on Windows, and the place where the "drive letters
/// versus mount points" difference is expressed honestly rather than papered
/// over with a fake common root. Where Linux parses /proc/mounts and filters out
/// loop devices and squashfs images, this asks
/// <see cref="DriveInfo.GetDrives"/> and gets a clean list back.
///
/// **No P/Invoke.** <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>
/// covers the user folders that have a SpecialFolder, which is all of them bar
/// one — see <see cref="Downloads"/>.
/// </summary>
public sealed class WindowsPlacesProvider : IPlacesProvider
{
    private readonly string _pinsPath;
    private List<PinnedPlace> _pins;

    public event EventHandler? PlacesChanged;

    public WindowsPlacesProvider(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _pinsPath = Path.Combine(stateDirectory, "places.json");
        _pins = LoadPins();
    }

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// **The one user folder with no SpecialFolder value**, so it is assembled
    /// from the profile directory. That is wrong for anyone who has relocated
    /// Downloads — the folder is genuinely movable and its real location lives
    /// in the known-folder table, reachable only through SHGetKnownFolderPath.
    /// The entry is dropped rather than shown broken when the guess is not
    /// there, so a relocated Downloads is missing rather than dead.
    /// </summary>
    private static string Downloads => Path.Combine(Home, "Downloads");

    public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
    {
        var groups = new List<PlaceGroup>
        {
            new("places", BuildUserPlaces()),
        };

        var (devices, network) = BuildDrives();

        if (devices.Count > 0) groups.Add(new PlaceGroup("devices", devices));
        if (network.Count > 0) groups.Add(new PlaceGroup("network", network));

        return ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(groups);
    }

    private List<Place> BuildUserPlaces()
    {
        // Case-insensitively, because C:\Users\flint and c:\users\flint are one
        // folder here — the same reason PathRules.Comparison is platform-dependent.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var places = new List<Place>
        {
            new()
            {
                Id = "home", Label = "Home", Path = Home,
                Kind = PlaceKind.UserFolder, Icon = "home",
            },
        };

        seen.Add(PathRules.Normalise(Home));

        foreach (var (id, path, icon) in new[]
        {
            ("desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "desktop"),
            ("downloads", Downloads, "download"),
            ("documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "file-text"),
            ("pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "photo"),
            ("music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "music"),
            ("videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "video"),
        })
        {
            // GetFolderPath answers "" rather than throwing when a folder is not
            // configured, and Downloads is a guess — so existence is checked
            // rather than assumed.
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
            if (!seen.Add(PathRules.Normalise(path))) continue;

            places.Add(new Place
            {
                Id = id, Label = PathRules.LeafName(path), Path = path,
                Kind = PlaceKind.UserFolder, Icon = icon,
            });
        }

        foreach (var pin in _pins)
        {
            if (!seen.Add(PathRules.Normalise(pin.Path))) continue;

            places.Add(new Place
            {
                Id = "pin:" + pin.Path,
                Label = pin.Label,
                Path = pin.Path,
                Kind = PlaceKind.Bookmark,
                Icon = "bookmark",
                IsUserPinned = true,
                IsAvailable = Directory.Exists(pin.Path),
            });
        }

        // **No Trash entry, deliberately.** The Linux provider ends with one
        // pointing at heimdall:trash, but the Recycle Bin needs the COM surface
        // that IFileOperations.Trash and ITrashMaintenance both still lack. An
        // entry that opens an empty view and cannot restore anything is worse
        // than no entry: it looks like the trash is empty. Add it with the
        // implementation, not before.

        return places;
    }

    private (List<Place> Devices, List<Place> Network) BuildDrives()
    {
        var devices = new List<Place>();
        var network = new List<Place>();

        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (IOException) { return (devices, network); }

        foreach (var drive in drives)
        {
            // A card reader with no card, or an empty optical bay, is a real
            // drive letter with no filesystem behind it. Shown dimmed and in
            // place rather than dropped — a slot that disappears when empty is
            // harder to find than one that is always there.
            var ready = false;
            try { ready = drive.IsReady; }
            catch (IOException) { /* treat as not ready */ }

            var place = BuildDrive(drive, ready);

            if (drive.DriveType == DriveType.Network) network.Add(place);
            else devices.Add(place);
        }

        return (devices, network);
    }

    private static Place BuildDrive(DriveInfo drive, bool ready)
    {
        var root = drive.Name;
        var removable = drive.DriveType is DriveType.Removable or DriveType.CDRom;

        long? capacity = null, free = null;
        string? label = null;

        if (ready)
        {
            // Every one of these throws on a drive that stopped being ready
            // between the check and the read, which a USB stick can do.
            try
            {
                capacity = drive.TotalSize;
                free = drive.AvailableFreeSpace;
                label = drive.VolumeLabel;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return new Place
        {
            Id = "dev:" + root,
            // "Windows (C:)" — the volume label with its letter, which is how
            // every other Windows file manager names a drive, and unambiguous
            // when two volumes share a label.
            Label = string.IsNullOrWhiteSpace(label)
                ? DefaultLabel(drive.DriveType, root)
                : $"{label} ({root.TrimEnd(Path.DirectorySeparatorChar)})",
            Path = root,
            Kind = drive.DriveType switch
            {
                DriveType.Network => PlaceKind.Network,
                DriveType.Removable or DriveType.CDRom => PlaceKind.RemovableDevice,
                _ => PlaceKind.Device,
            },
            Icon = drive.DriveType switch
            {
                DriveType.Network => "server",
                DriveType.Removable or DriveType.CDRom => "usb",
                _ => "device-desktop",
            },
            CapacityBytes = capacity,
            FreeBytes = free,
            IsAvailable = ready,
            CanEject = removable,
        };
    }

    private static string DefaultLabel(DriveType type, string root)
    {
        var letter = root.TrimEnd(Path.DirectorySeparatorChar);

        return type switch
        {
            DriveType.CDRom => $"Optical drive ({letter})",
            DriveType.Removable => $"Removable disk ({letter})",
            DriveType.Network => $"Network drive ({letter})",
            _ => $"Local disk ({letter})",
        };
    }

    public ValueTask PinAsync(string path, string? label, CancellationToken ct)
    {
        if (_pins.Any(p => PathRules.Same(p.Path, path))) return ValueTask.CompletedTask;

        _pins.Add(new PinnedPlace(path, label ?? PathRules.LeafName(path)));
        SavePins();
        return ValueTask.CompletedTask;
    }

    public ValueTask UnpinAsync(string id, CancellationToken ct)
    {
        var path = id.StartsWith("pin:", StringComparison.Ordinal) ? id[4..] : id;
        _pins.RemoveAll(p => PathRules.Same(p.Path, path));
        SavePins();
        return ValueTask.CompletedTask;
    }

    public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct)
    {
        var order = orderedIds
            .Where(i => i.StartsWith("pin:", StringComparison.Ordinal))
            .Select(i => i[4..])
            .ToList();

        _pins = _pins
            .OrderBy(p => order.FindIndex(o => PathRules.Same(o, p.Path)) is var i && i < 0
                ? int.MaxValue
                : i)
            .ToList();

        SavePins();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Windows mounts and ejects through the shell, and a drive letter that is
    /// present is already mounted — so there is nothing here to do that
    /// <see cref="GetPlacesAsync"/> has not done. Eject needs the shell's own
    /// "safely remove" path, which is COM.
    /// </summary>
    public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc cref="MountAsync"/>
    public ValueTask EjectAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>
    /// **Nothing to import yet, and it returns 0 rather than pretending.**
    /// The Windows equivalent of Dolphin's user-places.xbel is Quick Access,
    /// which is not a file: it lives behind a shell namespace extension and is
    /// only readable through COM. The interface comment names Quick Access as
    /// the target, so this is a gap rather than a decision — the built-in user
    /// folders already cover most of what a fresh Quick Access contains.
    /// </summary>
    public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);

    private List<PinnedPlace> LoadPins()
    {
        try
        {
            if (!File.Exists(_pinsPath)) return [];
            using var stream = File.OpenRead(_pinsPath);
            return JsonSerializer.Deserialize(
                stream, PinnedPlacesJsonContext.Default.ListPinnedPlace) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SavePins()
    {
        try
        {
            var temp = _pinsPath + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, _pins, PinnedPlacesJsonContext.Default.ListPinnedPlace);

            File.Move(temp, _pinsPath, overwrite: true);
            PlacesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { /* a lost pin is not worth interrupting the user over */ }
    }
}
