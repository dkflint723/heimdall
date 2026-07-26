using System.Text.Json.Serialization;

namespace Rove.Core.Session;

public enum SortField { Name, Size, Modified, Kind }

/// <summary>Ctrl+B cycles full → rail only → hidden.</summary>
public enum RailState { Full, RailOnly, Hidden }

/// <summary>
/// State for one tab. Deliberately only fields that are actually read and
/// written — a schema that claims to store scroll position and doesn't is worse
/// than one that never promised. Scroll offset, selection, view mode and column
/// widths come back here when the features that own them exist.
/// </summary>
public sealed record TabState
{
    public required string Path { get; init; }
    public SortField Sort { get; init; } = SortField.Name;
    public bool SortDescending { get; init; }
    public bool ShowHidden { get; init; }

    /// <summary>
    /// Back/forward stacks, oldest first. Nobody restores navigation history —
    /// which is exactly why having it is noticeable.
    /// </summary>
    public IReadOnlyList<string> BackStack { get; init; } = [];
    public IReadOnlyList<string> ForwardStack { get; init; } = [];
}

public sealed record PaneState
{
    public IReadOnlyList<TabState> Tabs { get; init; } = [];
    public int ActiveTabIndex { get; init; }
}

/// <summary>
/// Named WindowSession rather than WindowState because Avalonia's Window has a
/// WindowState property, and having both in scope in the code-behind is a trap.
/// </summary>
public sealed record WindowSession
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 1000;
    public double Height { get; init; } = 680;
    public bool IsMaximized { get; init; }

    public IReadOnlyList<PaneState> Panes { get; init; } = [];
    public int ActivePaneIndex { get; init; }

    // Re-added in v3 now that the sidebar exists. These were removed in v2
    // precisely because nothing read or wrote them.
    public string ActiveSidebarPanel { get; init; } = "places";
    public double SidebarWidth { get; init; } = 210;
    public RailState Rail { get; init; } = RailState.Full;
}

public sealed record SessionState
{
    /// <summary>
    /// v2 removed scroll/selection/view/column fields and added ShowHidden.
    /// v3 added the sidebar fields back, once there was a sidebar to store.
    /// An unrecognised version is ignored rather than migrated or thrown on —
    /// a session file must never prevent startup.
    /// </summary>
    public const int CurrentVersion = 3;

    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<WindowSession> Windows { get; init; } = [];
    public DateTimeOffset SavedAt { get; init; }
}

/// <summary>
/// Persistence contract. Implementations must:
///   1. debounce writes ~1s after any change, never save only on exit —
///      a crash or a reboot must not cost the session;
///   2. write atomically (tmp, flush, rename) and keep a .bak, because a
///      truncated file is what "it randomly forgets" actually looks like;
///   3. return null on any load failure so startup proceeds empty.
/// </summary>
public interface ISessionStore
{
    SessionState? Load();
    void NotifyChanged(SessionState state);
    ValueTask FlushAsync(CancellationToken ct);
}

/// <summary>Source-generated — reflection-based JSON does not survive trimming.</summary>
[JsonSerializable(typeof(SessionState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class SessionJsonContext : JsonSerializerContext;
