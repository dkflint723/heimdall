using Heimdall.Core.Settings;

namespace Heimdall.Ui.Settings;

/// <summary>
/// The live preferences, reachable from anywhere that needs them.
///
/// A static rather than constructor injection, deliberately and to match what
/// this codebase already does: <c>IconLoader.Provider</c>,
/// <c>ThumbnailLoader.Provider</c>, <c>RowMetadata.Provider</c> and
/// <c>RowTags.Store</c> are all statics for the same reason. Settings are read
/// by attached properties on realized rows, which have no constructor to inject
/// into — threading a settings object down to them would mean widening
/// <c>FileEntry</c> or passing it through every template, and the first of those
/// is explicitly forbidden.
///
/// <see cref="Changed"/> exists because some settings must take effect the
/// moment they are saved rather than at next launch. Anything that reads
/// <see cref="Current"/> more than once should subscribe.
/// </summary>
public static class AppSettings
{
    private static SettingsState _current = new();

    /// <summary>Never null. An absent or unreadable file yields defaults.</summary>
    public static SettingsState Current => _current;

    /// <summary>Raised after <see cref="Current"/> has already been swapped, so
    /// a handler reading it sees the new values rather than the old.</summary>
    public static event EventHandler? Changed;

    public static void Apply(SettingsState settings)
    {
        _current = Normalise(settings);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Guarantees every group is present, because the summary above promises it
    /// and deserialization does not deliver it.
    ///
    /// **Observed, not theoretical:** a `settings.json` written before
    /// `VcsSettings` existed produced `Current.Vcs == null` despite
    /// `SettingsState` declaring `Vcs { get; init; } = new()`. That crashed the
    /// listing, then the settings dialog, and would have crashed the save. Every
    /// group is equally exposed the next time one is added, so this is fixed at
    /// the boundary rather than at each of the dozens of read sites.
    ///
    /// **`ReferenceEquals(x, null)` rather than `x is null` or `x ?? new()`:**
    /// these properties are non-nullable reference types, so the nullable
    /// analyser may call the comparison redundant — and this project builds with
    /// warnings as errors. A method call cannot be warned about.
    /// </summary>
    private static SettingsState Normalise(SettingsState settings) => settings with
    {
        General = ReferenceEquals(settings.General, null) ? new() : settings.General,
        Startup = ReferenceEquals(settings.Startup, null) ? new() : settings.Startup,
        Views = NormaliseViews(settings.Views),
        Vcs = ReferenceEquals(settings.Vcs, null) ? new() : settings.Vcs,
        Navigation = ReferenceEquals(settings.Navigation, null) ? new() : settings.Navigation,
        ContextMenu = ReferenceEquals(settings.ContextMenu, null) ? new() : settings.ContextMenu,
        Trash = ReferenceEquals(settings.Trash, null) ? new() : settings.Trash,
    };

    /// <summary>
    /// `ViewSettings` nests three groups of its own, and they are exposed to
    /// exactly the same problem. Normalising the outer one and stopping there
    /// would look handled while `views.Icons.Spacing` still threw.
    /// </summary>
    private static ViewSettings NormaliseViews(ViewSettings views)
    {
        if (ReferenceEquals(views, null)) return new ViewSettings();

        return views with
        {
            Icons = ReferenceEquals(views.Icons, null) ? new() : views.Icons,
            Compact = ReferenceEquals(views.Compact, null) ? new() : views.Compact,
            Details = ReferenceEquals(views.Details, null) ? new() : views.Details,
        };
    }
}
