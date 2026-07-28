using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Settings;

namespace Heimdall.Ui.ViewModels;

/// <summary>
/// Edits a copy and commits it whole, rather than writing each control as it
/// changes. Cancel then genuinely cancels, and a half-finished set of
/// preferences never reaches disk.
///
/// Only the Startup page exists so far. The remaining five are separate pieces
/// of work, each landing with the plumbing that makes its toggles do something
/// — a control that does nothing is worse than an absent one, and this project
/// requires the UI to be usable by someone with no prior knowledge of it.
/// </summary>
/// <summary>
/// One entry in the font dropdown.
///
/// A typed record rather than a bare string **specifically so the dropdown can
/// have an item template**. A `DataTemplate` over `System.String` needs
/// `x:DataType` pointing at a framework type and is awkward under compiled
/// bindings; this is the ordinary pattern used everywhere else in the
/// application, and it also gives the sample somewhere to live.
///
/// <paramref name="Family"/> is built once here rather than converted in
/// markup — binding a string to a `FontFamily` property relies on a conversion
/// this codebase does not otherwise depend on.
/// </summary>
public sealed record FontOption(string Name, FontFamily Family, bool IsFollowDesktop);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsState _original;

    public SettingsViewModel(SettingsState current)
    {
        _original = current;

        var startup = current.Startup;
        var general = current.General;

        _naturalSorting = general.NaturalSorting;
        _caseSensitiveSorting = general.CaseSensitiveSorting;
        _rememberViewPerFolder = general.RememberViewPerFolder;
        _showTooltips = general.ShowTooltips;
        _tabSwitchesSplitPanes = general.TabSwitchesSplitPanes;
        _closingSplitDiscardsOtherPane = general.ClosingSplitDiscardsOtherPane;
        _showStatusBar = general.ShowStatusBar;
        _showFreeSpace = general.ShowFreeSpace;
        _showPreviews = general.ShowPreviews;
        _maxLocalPreviewMegabytes = general.MaxLocalPreviewMegabytes.ToString();
        _maxRemotePreviewMegabytes = general.MaxRemotePreviewMegabytes.ToString();
        _confirmMoveToTrash = general.ConfirmMoveToTrash;
        _confirmPermanentDelete = general.ConfirmPermanentDelete;
        _confirmClosingMultipleTabs = general.ConfirmClosingMultipleTabs;

        var views = current.Views;

        AvailableFonts = BuildFontList(views.CustomFontFamily);

        // Matched by NAME, not by reference: the configured value comes from a
        // file, and the list is built fresh. A configured font that is not
        // installed was already inserted by BuildFontList, so this cannot miss
        // and silently fall back to the sentinel — which would rewrite the
        // user's font the moment they pressed Save.
        _selectedFont = AvailableFonts.FirstOrDefault(o =>
            string.Equals(o.Name, views.CustomFontFamily, StringComparison.OrdinalIgnoreCase))
            ?? AvailableFonts[0];
        _absoluteDates = views.Details.DateStyle == Core.Settings.DateStyle.Absolute;
        _showFolderItemCounts = views.Details.FolderSize != Core.Settings.FolderSizeMode.None;

        // Blank rather than "0": the placeholder says what zero means, and an
        // empty box invites a value where a literal 0 looks like a setting
        // someone already made.
        _iconSpacing = views.Icons.Spacing > 0 ? views.Icons.Spacing.ToString() : "";
        _compactSpacing = views.Compact.Spacing > 0 ? views.Compact.Spacing.ToString() : "";

        var trash = current.Trash;

        _deleteOldTrash = trash.DeleteOldFiles;
        _deleteAfterDays = trash.DeleteAfterDays.ToString();
        _limitTrashSize = trash.LimitSize;
        _maxPercentOfDisk = trash.MaximumPercentOfDisk.ToString();
        _limitActionWarn = trash.WhenLimitReached == TrashLimitAction.Warn;
        _limitActionOldest = trash.WhenLimitReached == TrashLimitAction.DeleteOldest;
        _limitActionLargest = trash.WhenLimitReached == TrashLimitAction.DeleteLargest;

        _openWithSystem = current.Navigation.OpenItemsWith == ActivationClick.System;
        _openWithSingle = current.Navigation.OpenItemsWith == ActivationClick.Single;
        _openWithDouble = current.Navigation.OpenItemsWith == ActivationClick.Double;

        var menu = current.ContextMenu;

        _menuCopyTo = menu.ShowCopyTo;
        _menuMoveTo = menu.ShowMoveTo;
        _menuSortBy = menu.ShowSortBy;
        _menuDuplicate = menu.ShowDuplicate;
        _menuOpenInNewTab = menu.ShowOpenInNewTab;
        _menuAddToPlaces = menu.ShowAddToPlaces;
        _menuCopyLocation = menu.ShowCopyLocation;

        _restoreLastSession = startup.ShowOnStartup == StartupLocation.RestoreSession;
        _startInHome = startup.ShowOnStartup == StartupLocation.HomeFolder;
        _startInSpecificFolder = startup.ShowOnStartup == StartupLocation.SpecificFolder;
        _startupFolder = startup.StartupFolder ?? "";
        _beginInSplitView = startup.BeginInSplitView;
        _showFilterBar = startup.ShowFilterBar;
        _locationBarEditable = startup.LocationBarEditable;
        _showFullPathInTitleBar = startup.ShowFullPathInTitleBar;
    }

    // Three booleans rather than one enum property because Avalonia's
    // RadioButton binds IsChecked, and a converter per option would be more
    // moving parts than the thing it converts. Only the setters coordinate.

    [ObservableProperty] private bool _restoreLastSession;
    [ObservableProperty] private bool _startInHome;
    [ObservableProperty] private bool _startInSpecificFolder;

    [ObservableProperty] private string _startupFolder;
    [ObservableProperty] private bool _beginInSplitView;
    [ObservableProperty] private bool _showFilterBar;
    [ObservableProperty] private bool _locationBarEditable;
    [ObservableProperty] private bool _showFullPathInTitleBar;

    // ---- General ----------------------------------------------------------

    [ObservableProperty] private bool _naturalSorting;
    [ObservableProperty] private bool _caseSensitiveSorting;
    [ObservableProperty] private bool _rememberViewPerFolder;
    [ObservableProperty] private bool _showTooltips;
    [ObservableProperty] private bool _tabSwitchesSplitPanes;
    [ObservableProperty] private bool _closingSplitDiscardsOtherPane;
    [ObservableProperty] private bool _showStatusBar;
    [ObservableProperty] private bool _showFreeSpace;

    /// <summary>
    /// Natural order compares case-insensitively by construction, so the case
    /// choice only means anything with it off. Disabled rather than hidden, so
    /// the relationship between the two is visible instead of mysterious.
    /// </summary>
    public bool CanSetCaseSensitivity => !NaturalSorting;

    partial void OnNaturalSortingChanged(bool value)
        => OnPropertyChanged(nameof(CanSetCaseSensitivity));

    /// <summary>Free space sits inside the status bar, so it goes with it.</summary>
    public bool CanSetFreeSpace => ShowStatusBar;

    partial void OnShowStatusBarChanged(bool value)
        => OnPropertyChanged(nameof(CanSetFreeSpace));

    [ObservableProperty] private bool _showPreviews;
    [ObservableProperty] private bool _confirmMoveToTrash;
    [ObservableProperty] private bool _confirmPermanentDelete;
    [ObservableProperty] private bool _confirmClosingMultipleTabs;

    // Text rather than int: a spinner for "0 means unlimited" reads as a
    // quantity when it is really a switch with a quantity attached, and an
    // empty box is a clearer "no limit" than a zero.
    [ObservableProperty] private string _maxLocalPreviewMegabytes;
    [ObservableProperty] private string _maxRemotePreviewMegabytes;

    public bool CanSetPreviewLimits => ShowPreviews;

    partial void OnShowPreviewsChanged(bool value)
        => OnPropertyChanged(nameof(CanSetPreviewLimits));

    /// <summary>Anything unparseable, negative or blank means no limit.</summary>
    private static int Megabytes(string text)
        => int.TryParse(text, out var value) && value > 0 ? value : 0;

    // ---- Context menu -----------------------------------------------------
    //
    // Seven of Dolphin's nine. "Open in new window" needs multi-window support,
    // which this application does not have — App.axaml.cs creates exactly one
    // MainWindow. "View mode" lives in the toolbar and the view flyout rather
    // than the context menu, so there is nothing for a toggle to hide.

    [ObservableProperty] private bool _menuCopyTo;
    [ObservableProperty] private bool _menuMoveTo;
    [ObservableProperty] private bool _menuSortBy;
    [ObservableProperty] private bool _menuDuplicate;
    [ObservableProperty] private bool _menuOpenInNewTab;
    [ObservableProperty] private bool _menuAddToPlaces;
    [ObservableProperty] private bool _menuCopyLocation;

    // ---- View modes -------------------------------------------------------
    //
    // Three of the six. Icons.TextWidth, Icons.MaximumLines and
    // Compact.MaximumTextWidth stay out: they are structural metrics that would
    // have to feed PaneScale.Compute, and that pipeline is double-typed while
    // MaxLines is an int. Details.FolderSize's "size of contents" option needs
    // recursive summing in the metadata provider, which does not exist.

    /// <summary>
    /// The first entry, and the default. A sentinel string rather than a null
    /// item because a ComboBox showing an empty row reads as a bug.
    /// </summary>
    private const string FollowDesktop = "Follow the desktop font";

    public IReadOnlyList<FontOption> AvailableFonts { get; }

    [ObservableProperty] private FontOption _selectedFont;

    /// <summary>Extra gap between grid tiles, in pixels. Blank means none.</summary>
    [ObservableProperty] private string _iconSpacing;

    /// <summary>Extra gap around compact cells, in pixels. Blank means none.</summary>
    [ObservableProperty] private string _compactSpacing;

    /// <summary>
    /// Installed families, sorted, with the follow-the-desktop sentinel first.
    ///
    /// <paramref name="configured"/> is added even when it is not installed:
    /// silently dropping a font someone chose — because they are on a different
    /// machine, or uninstalled it — would rewrite their settings the moment they
    /// opened this dialog and pressed Save.
    /// </summary>
    private static IReadOnlyList<FontOption> BuildFontList(string? configured)
    {
        var names = new List<string> { FollowDesktop };

        var installed = FontManager.Current.SystemFonts
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        names.AddRange(installed);

        if (configured is { Length: > 0 }
            && !names.Contains(configured, StringComparer.OrdinalIgnoreCase))
            names.Insert(1, configured);

        // Traced because "my font is not in the list" has two very different
        // causes — the font is not installed, or Avalonia's font manager does
        // not enumerate what fontconfig knows about — and the count alone
        // separates them. Compare with: fc-list : family | sort -u | wc -l
        // Prefixed "fontlist", not "font": ThemeApplier already logs
        // "[heimdall] font: configured=… applied=…" and one grep matched both,
        // which sent a diagnostic session off in the wrong direction. Two
        // different facts get two different prefixes.
        Console.Error.WriteLine(
            $"[heimdall] fontlist: {names.Count - 1} families enumerated");

        if (Environment.GetEnvironmentVariable("HEIMDALL_FONT_DEBUG") == "1")
            foreach (var name in names.Skip(1))
                Console.Error.WriteLine($"[heimdall] fontlist: {name}");

        return names
            .Select(n => new FontOption(n, new FontFamily(n), n == FollowDesktop))
            .ToList();
    }
    [ObservableProperty] private bool _absoluteDates;
    [ObservableProperty] private bool _showFolderItemCounts;

    // ---- Navigation -------------------------------------------------------
    //
    // One setting. Dolphin has no control of its own here at all — it points at
    // System Settings — but Heimdall keeps an override because it also has to
    // run on Windows, where there is nothing to defer to.
    //
    // "Open folders during drag" (spring-loaded folders) is the other item on
    // Dolphin's page and is not built, so it is not offered.

    [ObservableProperty] private bool _openWithSystem;
    [ObservableProperty] private bool _openWithSingle;
    [ObservableProperty] private bool _openWithDouble;

    // ---- Trash ------------------------------------------------------------

    [ObservableProperty] private bool _deleteOldTrash;
    [ObservableProperty] private string _deleteAfterDays;
    [ObservableProperty] private bool _limitTrashSize;
    [ObservableProperty] private string _maxPercentOfDisk;
    [ObservableProperty] private bool _limitActionWarn;
    [ObservableProperty] private bool _limitActionOldest;
    [ObservableProperty] private bool _limitActionLargest;

    public bool CanSetTrashAge => DeleteOldTrash;
    public bool CanSetTrashSize => LimitTrashSize;

    partial void OnDeleteOldTrashChanged(bool value)
        => OnPropertyChanged(nameof(CanSetTrashAge));

    partial void OnLimitTrashSizeChanged(bool value)
        => OnPropertyChanged(nameof(CanSetTrashSize));

    /// <summary>
    /// Clamped, and a bad value disables rather than defaults. Zero days would
    /// mean "delete everything immediately", which is not a plausible thing to
    /// have meant by typing badly.
    /// </summary>
    private static int Days(string text)
        => int.TryParse(text, out var value) && value > 0 ? value : 0;

    /// <summary>
    /// Spacing is clamped rather than rejected: unlike a trash age, zero is a
    /// perfectly sensible answer here — it is the default — so a bad value
    /// falls back to none instead of disabling anything.
    /// </summary>
    private static int Spacing(string text)
        => int.TryParse(text, out var value) && value > 0 ? Math.Min(value, 48) : 0;

    private static int Percent(string text)
        => int.TryParse(text, out var value) && value is > 0 and <= 100 ? value : 0;

    /// <summary>Set when the dialog was dismissed with Save.</summary>
    public bool Saved { get; private set; }

    public SettingsState Result { get; private set; } = new();

    /// <summary>
    /// The folder box is only meaningful for one of the three choices, so it
    /// disables with the others rather than accepting input that will be
    /// ignored.
    /// </summary>
    public bool CanEditStartupFolder => StartInSpecificFolder;

    partial void OnStartInSpecificFolderChanged(bool value)
        => OnPropertyChanged(nameof(CanEditStartupFolder));

    [RelayCommand]
    private void Save()
    {
        var location = StartInSpecificFolder ? StartupLocation.SpecificFolder
            : StartInHome ? StartupLocation.HomeFolder
            : StartupLocation.RestoreSession;


        // `with` on the whole state, so pages that are not built yet keep
        // whatever is already in the file rather than being reset to defaults
        // by a dialog that never showed them.
        Result = _original with
        {
            General = _original.General with
            {
                NaturalSorting = NaturalSorting,
                CaseSensitiveSorting = CaseSensitiveSorting,
                RememberViewPerFolder = RememberViewPerFolder,
                ShowTooltips = ShowTooltips,
                TabSwitchesSplitPanes = TabSwitchesSplitPanes,
                ClosingSplitDiscardsOtherPane = ClosingSplitDiscardsOtherPane,
                ShowStatusBar = ShowStatusBar,
                ShowFreeSpace = ShowFreeSpace,
                ShowPreviews = ShowPreviews,
                MaxLocalPreviewMegabytes = Megabytes(MaxLocalPreviewMegabytes),
                MaxRemotePreviewMegabytes = Megabytes(MaxRemotePreviewMegabytes),
                ConfirmMoveToTrash = ConfirmMoveToTrash,
                ConfirmPermanentDelete = ConfirmPermanentDelete,
                ConfirmClosingMultipleTabs = ConfirmClosingMultipleTabs,
            },

            Views = _original.Views with
            {
                CustomFontFamily = SelectedFont is null || SelectedFont.IsFollowDesktop
                    ? null
                    : SelectedFont.Name,

                Icons = _original.Views.Icons with { Spacing = Spacing(IconSpacing) },
                Compact = _original.Views.Compact with { Spacing = Spacing(CompactSpacing) },

                Details = _original.Views.Details with
                {
                    DateStyle = AbsoluteDates
                        ? Core.Settings.DateStyle.Absolute
                        : Core.Settings.DateStyle.Relative,

                    // Only two of the three modes are reachable from here, so
                    // the third is preserved rather than overwritten by a
                    // control that never showed it.
                    FolderSize = ShowFolderItemCounts
                        ? (_original.Views.Details.FolderSize == Core.Settings.FolderSizeMode.None
                            ? Core.Settings.FolderSizeMode.ItemCount
                            : _original.Views.Details.FolderSize)
                        : Core.Settings.FolderSizeMode.None,
                },
            },

            Trash = _original.Trash with
            {
                // A field that will not parse turns the feature OFF rather than
                // falling back to a default. Guessing a number here means
                // deleting files against something the user did not type.
                DeleteOldFiles = DeleteOldTrash && Days(DeleteAfterDays) > 0,
                DeleteAfterDays = Days(DeleteAfterDays) is > 0 and var d
                    ? d
                    : _original.Trash.DeleteAfterDays,

                LimitSize = LimitTrashSize && Percent(MaxPercentOfDisk) > 0,
                MaximumPercentOfDisk = Percent(MaxPercentOfDisk) is > 0 and var p
                    ? p
                    : _original.Trash.MaximumPercentOfDisk,

                WhenLimitReached = LimitActionOldest ? TrashLimitAction.DeleteOldest
                    : LimitActionLargest ? TrashLimitAction.DeleteLargest
                    : TrashLimitAction.Warn,
            },

            Navigation = _original.Navigation with
            {
                OpenItemsWith = OpenWithSingle ? ActivationClick.Single
                    : OpenWithDouble ? ActivationClick.Double
                    : ActivationClick.System,
            },

            ContextMenu = _original.ContextMenu with
            {
                ShowCopyTo = MenuCopyTo,
                ShowMoveTo = MenuMoveTo,
                ShowSortBy = MenuSortBy,
                ShowDuplicate = MenuDuplicate,
                ShowOpenInNewTab = MenuOpenInNewTab,
                ShowAddToPlaces = MenuAddToPlaces,
                ShowCopyLocation = MenuCopyLocation,
            },

            Startup = _original.Startup with
            {
                ShowOnStartup = location,
                StartupFolder = string.IsNullOrWhiteSpace(StartupFolder) ? null : StartupFolder.Trim(),
                BeginInSplitView = BeginInSplitView,
                ShowFilterBar = ShowFilterBar,
                LocationBarEditable = LocationBarEditable,
                ShowFullPathInTitleBar = ShowFullPathInTitleBar,
            },
        };

        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CloseRequested;
}
