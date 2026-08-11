namespace Vaktari.Core.FileSystem;

/// <summary>One application that can open a given file.</summary>
public sealed record LaunchOption(string Name, string Id, string? Icon = null)
{
    /// <summary>
    /// The "pick something else" row rather than an installed application.
    ///
    /// A member of the same list because the menu is driven by ItemsSource:
    /// a static sibling cannot be added beside bound items, and a chooser that
    /// sits anywhere other than the bottom of the list it belongs to is a
    /// chooser nobody finds.
    /// </summary>
    public bool IsChooser { get; init; }
}

/// <summary>
/// Handing a file to whatever the desktop thinks should open it. Deliberately
/// tiny and platform-agnostic: the desktop database and xdg-open on Linux,
/// ShellExecute and the shell's own handler list on Windows.
/// </summary>
public interface IApplicationLauncher
{
    /// <summary>Open with the user's default application for the type.</summary>
    void Open(string path);

    /// <summary>Open a terminal with its working directory set to this folder.</summary>
    void OpenTerminal(string directory);

    /// <summary>
    /// Every application registered as able to open this file, default first.
    /// Empty if the desktop provides no way to enumerate them.
    /// </summary>
    IReadOnlyList<LaunchOption> GetOpenWithOptions(string path);

    /// <summary>Open with one specific application from GetOpenWithOptions.</summary>
    void OpenWith(string path, LaunchOption option);

    /// <summary>
    /// Whether this desktop can offer to pick an application that is not in the
    /// list — Windows has its own "How do you want to open this file?" dialog,
    /// which includes browsing for an executable and remembering the choice.
    ///
    /// Defaulted off so a platform without one shows no entry, rather than an
    /// entry that does nothing.
    /// </summary>
    bool CanChooseApplication => false;

    /// <summary>Shows that chooser. False when it could not be shown.</summary>
    bool ChooseApplication(string path) => false;
}
