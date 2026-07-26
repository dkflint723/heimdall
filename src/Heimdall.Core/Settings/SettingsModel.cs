using System.Text.Json.Serialization;

namespace Heimdall.Core.Settings;

/// <summary>What the window shows when it opens.</summary>
public enum StartupLocation
{
    /// <summary>Folders, tabs and window state from last time. The default, and
    /// the reason this project exists — forgetting them was the original
    /// complaint about the tool it replaces.</summary>
    RestoreSession,
    HomeFolder,
    SpecificFolder,
}

public enum DateStyle { Relative, Absolute }

/// <summary>What the size column means for a folder.</summary>
public enum FolderSizeMode { ItemCount, ContentSize, None }

public enum ExecutableAction { Ask, OpenInApplication, RunScript }

public enum TrashLimitAction { Warn, DeleteOldest, DeleteLargest }

/// <summary>
/// Single or double click to open. <see cref="System"/> follows the desktop's
/// own setting, which is what Dolphin does — it does not offer its own control
/// at all, deferring to System Settings. Heimdall keeps the override because it
/// also has to run on Windows, where there is no equivalent to defer to.
/// </summary>
public enum ActivationClick { System, Single, Double }

/// <summary>
/// General behaviour. Dolphin splits this across four tabs — Behavior,
/// Previews, Confirmations, Status Bar — but those are a UI grouping rather
/// than a data one, so they are flat here and the dialog does the grouping.
///
/// **Every default below is the behaviour the application has today.** That is
/// deliberate and load-bearing: introducing this record must not change how
/// anything works, so that when it is threaded through the call sites the only
/// thing to verify is that nothing changed.
/// </summary>
public sealed record GeneralSettings
{
    // ---- sorting ----------------------------------------------------------

    /// <summary>file2 before file10. NaturalOrder does this unconditionally today.</summary>
    public bool NaturalSorting { get; init; } = true;

    /// <summary>False today: NaturalOrder.Compare upper-cases both sides.</summary>
    public bool CaseSensitiveSorting { get; init; }

    // ---- behaviour --------------------------------------------------------

    public bool ShowTooltips { get; init; } = true;

    /// <summary>
    /// Renaming happens in the prompt bar rather than a modal dialog. The
    /// setting exists for parity; Dolphin's alternative is a dialog per item.
    /// </summary>
    public bool RenameInline { get; init; } = true;

    /// <summary>Already the behaviour — Tab moves between split halves.</summary>
    public bool TabSwitchesSplitPanes { get; init; } = true;

    /// <summary>
    /// Dolphin closes the inactive pane. Heimdall keeps it in
    /// RememberedRightPane so reopening the split returns to where it was, and
    /// that difference is deliberate — closing a split should not be a quiet
    /// way to lose a location. False keeps Heimdall's behaviour.
    /// </summary>
    public bool ClosingSplitDiscardsOtherPane { get; init; }

    // ---- previews ---------------------------------------------------------

    public bool ShowPreviews { get; init; } = true;

    /// <summary>Megabytes; 0 means no limit, which is the behaviour today.</summary>
    public int MaxLocalPreviewMegabytes { get; init; }

    /// <summary>
    /// Separate from the local limit because it is the one that matters here:
    /// a thumbnail on an SMB or SFTP mount pulls the whole file over the
    /// network. 0 means no limit, which is the behaviour today.
    /// </summary>
    public int MaxRemotePreviewMegabytes { get; init; }

    // ---- confirmations ----------------------------------------------------

    /// <summary>No confirmation today — trash is reversible.</summary>
    public bool ConfirmMoveToTrash { get; init; }

    /// <summary>Confirmed today, with real buttons rather than a bare key path.</summary>
    public bool ConfirmPermanentDelete { get; init; } = true;

    public bool ConfirmClosingMultipleTabs { get; init; }

    public ExecutableAction OnOpeningExecutable { get; init; } = ExecutableAction.OpenInApplication;

    // ---- status bar -------------------------------------------------------

    public bool ShowStatusBar { get; init; } = true;

    public bool ShowFreeSpace { get; init; } = true;
}

public sealed record StartupSettings
{
    public StartupLocation ShowOnStartup { get; init; } = StartupLocation.RestoreSession;

    /// <summary>Only read when ShowOnStartup is SpecificFolder.</summary>
    public string? StartupFolder { get; init; }

    public bool BeginInSplitView { get; init; }

    public bool ShowFilterBar { get; init; }

    public bool LocationBarEditable { get; init; }

    /// <summary>
    /// A folder opened from outside becomes a tab in the running window rather
    /// than a second process. Single-instance is already enforced by a file
    /// lock; this decides what the running instance does with the request.
    /// </summary>
    public bool OpenNewFoldersInTabs { get; init; } = true;

    /// <summary>Otherwise the path is shortened against the Places entries.</summary>
    public bool ShowFullPathInLocationBar { get; init; }

    public bool ShowFullPathInTitleBar { get; init; }
}

/// <summary>Settings that apply to one layout only.</summary>
public sealed record IconsViewSettings
{
    /// <summary>Minimum width reserved for an item's label.</summary>
    public int TextWidth { get; init; } = 120;

    public int MaximumLines { get; init; } = 2;
}

public sealed record CompactViewSettings
{
    public int MaximumTextWidth { get; init; } = 180;
}

public sealed record DetailsViewSettings
{
    public FolderSizeMode FolderSize { get; init; } = FolderSizeMode.ItemCount;

    /// <summary>
    /// Depth limit for ContentSize. Unbounded recursion on a deep tree or a
    /// network mount is how a listing stops being fast.
    /// </summary>
    public int FolderSizeRecursionLimit { get; init; } = 3;

    /// <summary>AgeConverters renders relative dates today.</summary>
    public DateStyle DateStyle { get; init; } = DateStyle.Relative;
}

/// <summary>
/// Defaults a pane starts from. Panes still scale independently afterwards —
/// these are the starting values, not a cap, because per-pane scaling is an
/// accessibility feature and a global setting must not take it away.
/// </summary>
public sealed record ViewSettings
{
    /// <summary>Null means follow the desktop font from kdeglobals.</summary>
    public string? CustomFontFamily { get; init; }

    public IconsViewSettings Icons { get; init; } = new();
    public CompactViewSettings Compact { get; init; } = new();
    public DetailsViewSettings Details { get; init; } = new();
}

public sealed record NavigationSettings
{
    public ActivationClick OpenItemsWith { get; init; } = ActivationClick.System;

    /// <summary>
    /// Spring-loaded folders: hovering a folder mid-drag opens it. Not built
    /// yet; the setting arrives with the feature.
    /// </summary>
    public bool OpenFoldersDuringDrag { get; init; }
}

/// <summary>
/// Which commands appear in the context menu. Dolphin's Services page also
/// lists .desktop service menus and version-control plugins; neither applies
/// here — the scripts menu replaces the former by design, and VCS decorations
/// are not built.
/// </summary>
public sealed record ContextMenuSettings
{
    public bool ShowCopyTo { get; init; } = true;
    public bool ShowMoveTo { get; init; } = true;
    public bool ShowAddToPlaces { get; init; } = true;
    public bool ShowSortBy { get; init; } = true;
    public bool ShowViewMode { get; init; } = true;
    public bool ShowOpenInNewTab { get; init; } = true;
    public bool ShowOpenInNewWindow { get; init; } = true;
    public bool ShowCopyLocation { get; init; } = true;
    public bool ShowDuplicate { get; init; } = true;
}

/// <summary>
/// Trash limits. Heimdall implements the XDG trash spec, so these govern the
/// same directories Dolphin's do — which means enabling them here affects what
/// the rest of the desktop sees, and that is the point.
/// </summary>
public sealed record TrashSettings
{
    public bool DeleteOldFiles { get; init; }
    public int DeleteAfterDays { get; init; } = 30;

    public bool LimitSize { get; init; }
    public int MaximumPercentOfDisk { get; init; } = 10;

    public TrashLimitAction WhenLimitReached { get; init; } = TrashLimitAction.Warn;
}

/// <summary>
/// Preferences — what the user always wants — as distinct from
/// <c>SessionState</c>, which is where they happened to be last time.
///
/// The two are deliberately separate files. They have different lifetimes and
/// they conflict head-on: "restore my last folders" and "always start in Home"
/// are both startup settings, and one of them has to win. Settings are read
/// first, because <see cref="StartupSettings.ShowOnStartup"/> decides whether
/// the session is consulted at all.
/// </summary>
public sealed record SettingsState
{
    /// <summary>
    /// v1 — initial. An unrecognised version falls back to defaults rather
    /// than migrating or throwing: a settings file must never prevent startup,
    /// for the same reason a session file must not.
    /// </summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public GeneralSettings General { get; init; } = new();
    public StartupSettings Startup { get; init; } = new();
    public ViewSettings Views { get; init; } = new();
    public NavigationSettings Navigation { get; init; } = new();
    public ContextMenuSettings ContextMenu { get; init; } = new();
    public TrashSettings Trash { get; init; } = new();
}

/// <summary>
/// Persistence contract. Unlike the session there is no debounce: settings
/// change only when a person changes one, so a write per change is both rare
/// and what they expect. Atomicity and the fall-back-to-defaults rule are the
/// same.
/// </summary>
public interface ISettingsStore
{
    SettingsState Load();

    /// <summary>
    /// Synchronous on purpose. The session store is async because it writes on
    /// a timer while the user is working; this writes a few kilobytes when
    /// someone clicks OK in a dialog. Async here would add a fire-and-forget
    /// call site for no gain.
    /// </summary>
    void Save(SettingsState settings);
}

/// <summary>Source-generated — reflection-based JSON does not survive trimming.</summary>
[JsonSerializable(typeof(SettingsState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
public partial class SettingsJsonContext : JsonSerializerContext;
