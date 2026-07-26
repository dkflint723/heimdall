namespace Rove.Core.FileSystem;

/// <summary>One application that can open a given file.</summary>
public sealed record LaunchOption(string Name, string Id, string? Icon = null);

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
}
