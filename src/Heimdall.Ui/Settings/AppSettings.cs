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
        _current = settings;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
