using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Vaktari.Core.Places;

namespace Vaktari.Linux;

public sealed record PinnedPlace(string Path, string Label);

[JsonSerializable(typeof(List<PinnedPlace>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class PinnedPlacesJsonContext : JsonSerializerContext;

/// <summary>
/// The sidebar's data source on Linux, and the place where the "drive letters
/// versus mount points" difference is expressed honestly rather than papered
/// over with a fake common root.
/// </summary>
public sealed class LinuxPlacesProvider : IPlacesProvider
{
    private readonly string _pinsPath;
    /// <summary>
    /// **Replaced, never mutated in place** — the same reason as the Windows
    /// provider: building the places list reads this off the UI thread while
    /// pinning writes it on the UI thread, and an Add mid-enumeration throws
    /// from a task nobody awaits. Copy-on-write rather than a lock; a reader
    /// finishes against the list it started with.
    /// </summary>
    private List<PinnedPlace> _pins = [];

    public event EventHandler? PlacesChanged;

    public LinuxPlacesProvider(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _pinsPath = Path.Combine(stateDirectory, "places.json");
        _pins = LoadPins();
    }

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
    {
        var groups = new List<PlaceGroup>
        {
            new("places", BuildUserPlaces()),
        };

        var (devices, network) = BuildMounts();

        if (devices.Count > 0) groups.Add(new PlaceGroup("devices", devices));
        if (network.Count > 0) groups.Add(new PlaceGroup("network", network));

        return ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(groups);
    }

    private List<Place> BuildUserPlaces()
    {
        // Imported Dolphin and GTK bookmarks routinely point at the same
        // folders as the XDG user dirs, so without this every one of Home,
        // Documents, Downloads and friends appears twice.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        static string Normalise(string path) => path.TrimEnd('/');

        var places = new List<Place>
        {
            new()
            {
                Id = "home", Label = "Home", Path = Home,
                Kind = PlaceKind.UserFolder, Icon = "home",
            },
        };

        seen.Add(Normalise(Home));

        // XDG user dirs rather than hardcoded English names — this file is also
        // what makes the folder names correct in a localised install.
        foreach (var (key, icon) in new[]
        {
            ("XDG_DESKTOP_DIR", "desktop"),
            ("XDG_DOWNLOAD_DIR", "download"),
            ("XDG_DOCUMENTS_DIR", "file-text"),
            ("XDG_PICTURES_DIR", "photo"),
            ("XDG_MUSIC_DIR", "music"),
            ("XDG_VIDEOS_DIR", "video"),
        })
        {
            if (ReadUserDir(key) is { } path && Directory.Exists(path)
                && seen.Add(Normalise(path)))
            {
                places.Add(new Place
                {
                    Id = key, Label = Path.GetFileName(path), Path = path,
                    Kind = PlaceKind.UserFolder, Icon = icon,
                });
            }
        }

        // Captured once: this runs off the UI thread and pinning runs on it.
        var pins = _pins;

        foreach (var pin in pins)
        {
            if (!seen.Add(Normalise(pin.Path))) continue;

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

        // Trash last, matching Dolphin, and pointing at a VIRTUAL path rather
        // than ~/.local/share/Trash/files. The payload directory holds
        // deduplicated names with no record of where anything came from, so
        // browsing it directly would show files you could not restore.
        // IsAvailable stays true even when empty: a Trash entry that vanishes
        // when there is nothing in it is harder to find than one that is always
        // there and simply lists nothing.
        places.Add(new Place
        {
            Id = "trash",
            Label = "Trash",
            Path = "vaktari:trash",
            Kind = PlaceKind.Bookmark,
            Icon = "trash",
            IsAvailable = true,
        });

        return places;
    }

    private static string? ReadUserDir(string key)
    {
        var config = Path.Combine(Home, ".config", "user-dirs.dirs");
        if (!File.Exists(config)) return null;

        foreach (var line in File.ReadLines(config))
        {
            if (!line.StartsWith(key, StringComparison.Ordinal)) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;

            var value = line[(eq + 1)..].Trim().Trim('"');
            return value.Replace("$HOME", Home);
        }

        return null;
    }

    /// <summary>Volume label to device, reversed from /dev/disk/by-label.</summary>
    private static Dictionary<string, string> ReadVolumeLabels()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            foreach (var link in Directory.EnumerateFileSystemEntries("/dev/disk/by-label"))
            {
                var target = new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                    map[target.FullName] = Path.GetFileName(link);
            }
        }
        catch { /* no by-label directory — fall back to mount point names */ }

        return map;
    }

    private (List<Place> Devices, List<Place> Network) BuildMounts()
    {
        var devices = new List<Place>();
        var network = new List<Place>();

        if (!File.Exists("/proc/mounts")) return (devices, network);

        var labels = ReadVolumeLabels();
        var seenDevices = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines("/proc/mounts"))
        {
            var parts = line.Split(' ');
            if (parts.Length < 3) continue;

            var source = parts[0];
            var mountPoint = parts[1].Replace("\\040", " ");
            var fsType = parts[2];

            if (IsNetworkFs(fsType))
            {
                // gvfs is a control mount, not somewhere anyone navigates.
                if (mountPoint.Contains("/gvfs", StringComparison.Ordinal)) continue;

                network.Add(new Place
                {
                    Id = "net:" + mountPoint,
                    Label = Path.GetFileName(mountPoint.TrimEnd('/')) is { Length: > 0 } n ? n : mountPoint,
                    Path = mountPoint,
                    Kind = PlaceKind.Network,
                    Icon = "server",
                });
                continue;
            }

            if (!IsRealVolume(source, mountPoint, fsType)) continue;

            // One physical volume, one entry. btrfs subvolumes mount the same
            // device several times and would otherwise appear as separate
            // drives all reporting identical free space.
            if (!seenDevices.Add(source)) continue;

            var removable = mountPoint.StartsWith("/run/media", StringComparison.Ordinal)
                         || mountPoint.StartsWith("/media", StringComparison.Ordinal);

            // Optical media, by the filesystem it carries or the device it came
            // from. Both are needed: a data CD is iso9660 and a video disc is
            // udf, while a blank or audio disc may present neither — /dev/sr* is
            // what the kernel calls an optical device regardless.
            //
            // Removable too, whatever the mount point says. An optical disc is
            // the most ejectable thing there is, and CanEject follows this flag.
            var optical = fsType is "iso9660" or "udf"
                       || source.StartsWith("/dev/sr", StringComparison.Ordinal);

            removable |= optical;

            long? capacity = null, free = null;
            try
            {
                var drive = new DriveInfo(mountPoint);
                capacity = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
            catch { /* unreadable mount — show it without a capacity bar */ }

            devices.Add(new Place
            {
                Id = "dev:" + mountPoint,
                Label = LabelFor(source, mountPoint, labels),
                Path = mountPoint,
                Kind = removable ? PlaceKind.RemovableDevice : PlaceKind.Device,
                Icon = optical ? "disc" : removable ? "usb" : "device-desktop",
                CapacityBytes = capacity,
                FreeBytes = free,
                CanEject = removable,
            });
        }

        return (devices, network);
    }

    /// <summary>
    /// Snap and flatpak mount squashfs images through loop devices, which live
    /// under /dev/ and so pass a naive "is it a block device" test — that is
    /// how a sidebar ends up listing a dozen entries named after revision
    /// numbers, all reporting zero bytes free.
    /// </summary>
    private static bool IsRealVolume(string source, string mountPoint, string fsType)
    {
        if (!source.StartsWith("/dev/", StringComparison.Ordinal)) return false;
        if (source.StartsWith("/dev/loop", StringComparison.Ordinal)) return false;
        if (source.StartsWith("/dev/zram", StringComparison.Ordinal)) return false;

        if (fsType is "squashfs" or "overlay" or "tmpfs" or "devtmpfs" or "iso9660") return false;

        if (mountPoint.StartsWith("/boot", StringComparison.Ordinal)) return false;
        if (mountPoint.StartsWith("/snap", StringComparison.Ordinal)) return false;
        if (mountPoint.StartsWith("/var/lib/docker", StringComparison.Ordinal)) return false;

        return true;
    }

    private static string LabelFor(
        string source, string mountPoint, Dictionary<string, string> labels)
    {
        if (mountPoint == "/")
            return labels.TryGetValue(source, out var rootLabel) ? rootLabel : "System";

        if (labels.TryGetValue(source, out var label)) return label;

        var name = Path.GetFileName(mountPoint.TrimEnd('/'));
        return string.IsNullOrEmpty(name) ? source : name;
    }

    private static bool IsNetworkFs(string fsType) => fsType
        is "cifs" or "smb3" or "nfs" or "nfs4" or "fuse.sshfs" or "fuse.kio" or "fuse.gvfsd-fuse";

    public ValueTask PinAsync(string path, string? label, CancellationToken ct)
    {
        if (_pins.Any(p => p.Path == path)) return ValueTask.CompletedTask;

        _pins = [.. _pins, new PinnedPlace(path, label ?? Path.GetFileName(path.TrimEnd('/')))];
        SavePins();
        return ValueTask.CompletedTask;
    }

    public ValueTask UnpinAsync(string id, CancellationToken ct)
    {
        var path = id.StartsWith("pin:", StringComparison.Ordinal) ? id[4..] : id;
        _pins = _pins.Where(p => p.Path != path).ToList();
        SavePins();
        return ValueTask.CompletedTask;
    }

    public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct)
    {
        var order = orderedIds
            .Where(i => i.StartsWith("pin:", StringComparison.Ordinal))
            .Select(i => i[4..])
            .ToList();

        _pins = _pins.OrderBy(p => order.IndexOf(p.Path) is var i && i < 0 ? int.MaxValue : i).ToList();
        SavePins();
        return ValueTask.CompletedTask;
    }

    public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask EjectAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>
    /// First-run import from Dolphin and GTK. Coming up with the user's real
    /// shortcuts already in place matters more for whether they keep using this
    /// than any individual feature does.
    /// </summary>
    /// <summary>Paths already offered as built-in entries, so importing a
    /// bookmark to one of them adds nothing.</summary>
    private HashSet<string> BuiltInPaths()
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { Home.TrimEnd('/') };

        foreach (var key in new[]
        {
            "XDG_DESKTOP_DIR", "XDG_DOWNLOAD_DIR", "XDG_DOCUMENTS_DIR",
            "XDG_PICTURES_DIR", "XDG_MUSIC_DIR", "XDG_VIDEOS_DIR",
        })
        {
            if (ReadUserDir(key) is { } path) set.Add(path.TrimEnd('/'));
        }

        return set;
    }

    public ValueTask<int> ImportExistingAsync(CancellationToken ct)
    {
        var before = _pins.Count;

        var builtIn = BuiltInPaths();

        ImportXbel(Path.Combine(Home, ".local", "share", "user-places.xbel"), builtIn);
        ImportGtkBookmarks(Path.Combine(Home, ".config", "gtk-3.0", "bookmarks"), builtIn);

        // Anything previously imported that duplicates a built-in is dropped
        // too, so an existing places.json is repaired rather than preserved.
        _pins = _pins.Where(pin => !builtIn.Contains(pin.Path.TrimEnd('/'))).ToList();

        if (_pins.Count != before || builtIn.Overlaps(_pins.Select(p => p.Path.TrimEnd('/'))))
        {
            SavePins();
            PlacesChanged?.Invoke(this, EventArgs.Empty);
        }

        return ValueTask.FromResult(_pins.Count - before);
    }

    private void ImportXbel(string path, HashSet<string> builtIn)
    {
        if (!File.Exists(path)) return;

        try
        {
            foreach (var bookmark in XDocument.Load(path).Descendants("bookmark"))
            {
                var href = bookmark.Attribute("href")?.Value;
                if (href is null || !href.StartsWith("file://", StringComparison.Ordinal)) continue;

                var dir = Uri.UnescapeDataString(href[7..]);
                if (!Directory.Exists(dir)) continue;

                var title = bookmark.Element("title")?.Value
                            ?? Path.GetFileName(dir.TrimEnd('/'));

                if (builtIn.Contains(dir.TrimEnd('/'))) continue;

                if (!_pins.Any(p => p.Path == dir))
                    _pins = [.. _pins, new PinnedPlace(dir, title)];
            }
        }
        catch { /* a malformed bookmarks file is not worth failing startup over */ }
    }

    private void ImportGtkBookmarks(string path, HashSet<string> builtIn)
    {
        if (!File.Exists(path)) return;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (!line.StartsWith("file://", StringComparison.Ordinal)) continue;

                var space = line.IndexOf(' ');
                var uri = space < 0 ? line : line[..space];
                var label = space < 0 ? null : line[(space + 1)..];

                var dir = Uri.UnescapeDataString(uri[7..]);
                if (!Directory.Exists(dir)) continue;

                if (builtIn.Contains(dir.TrimEnd('/'))) continue;

                if (!_pins.Any(p => p.Path == dir))
                    _pins = [.. _pins,
                        new PinnedPlace(dir, label ?? Path.GetFileName(dir.TrimEnd('/')))];
            }
        }
        catch { /* same */ }
    }

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
