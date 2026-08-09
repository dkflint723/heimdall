namespace Vaktari.Core;

public sealed record ScriptCommand(string Name, string Path);

/// <summary>
/// User scripts, invoked on the current selection.
///
/// Deliberately a folder of ordinary executables rather than a plugin API:
/// nothing to learn, nothing to compile, and the scripts keep working if this
/// application goes away. Discovery is platform-specific — an execute bit here,
/// file extensions and an interpreter on Windows.
/// </summary>
public interface IScriptRunner
{
    /// <summary>Where scripts live, shown in the UI so the folder is findable.</summary>
    string ScriptsDirectory { get; }

    IReadOnlyList<ScriptCommand> Discover();

    /// <summary>
    /// Runs a script with the selected paths as arguments and the listed
    /// directory as its working directory. Returns whatever it printed, so a
    /// script can report back without needing a UI of its own.
    /// </summary>
    ValueTask<string> RunAsync(
        ScriptCommand script,
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken ct);
}
