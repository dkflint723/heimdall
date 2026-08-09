using System.Text.RegularExpressions;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The installer's <c>AppMutex</c> and the name the application actually claims
/// have to be the same string.
///
/// **This is guarding a silent failure.** If either side is renamed, nothing
/// breaks visibly: the application still starts, the installer still builds and
/// still installs. It simply stops being able to tell that Vaktari is running,
/// and the first anyone knows is an upgrade landing on top of an open copy —
/// a half-written 28 MB executable, on a user's machine, with no clue pointing
/// back at a rename made months earlier.
///
/// Reading the .iss rather than duplicating the literal here, because a second
/// copy of the string in the test would drift in exactly the same way and prove
/// nothing.
/// </summary>
public class InstallerMutexTests
{
    /// <summary>
    /// Walks up from the test binary to the repository root. The test runs from
    /// bin/Debug/net10.0, and the depth changes with configuration and target
    /// framework, so it looks for a landmark rather than counting directories.
    /// </summary>
    private static DirectoryInfo RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vaktari.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void The_installer_watches_the_mutex_the_application_claims()
    {
        var iss = Path.Combine(RepositoryRoot().FullName, "packaging", "vaktari.iss");

        Assert.True(File.Exists(iss), $"packaging/vaktari.iss not found at {iss}");

        var text = File.ReadAllText(iss);

        var defined = Regex.Match(text, @"^#define\s+AppMutex\s+""([^""]+)""",
            RegexOptions.Multiline);

        Assert.True(defined.Success, "vaktari.iss no longer defines an AppMutex symbol");

        Assert.Equal(Vaktari.Ui.Program.InstanceMutexName, defined.Groups[1].Value);

        // And that the symbol is actually WIRED to the setting. Defining it and
        // never using it would pass the comparison above while leaving the
        // installer with no mutex to watch at all — the exact thing this file
        // exists to prevent, reachable by deleting one line.
        Assert.Matches(@"^AppMutex=\{#AppMutex\}", Regex.Match(
            text, @"^AppMutex=.*$", RegexOptions.Multiline).Value);
    }

    /// <summary>
    /// The uninstaller must hand the folder classes back before it deletes the
    /// executable that would do it.
    ///
    /// **This is a repair, not a precaution.** Becoming the default writes a
    /// shell verb pointing at the installed binary; removing the binary left
    /// the verb behind, so afterwards every double-clicked folder tried to
    /// launch a file that no longer existed. The error Windows shows names a
    /// missing path, not the program that registered it, and the only way back
    /// is the registry.
    ///
    /// Silent on both sides: rename the switch and the uninstall still
    /// succeeds, having quietly done nothing.
    /// </summary>
    [Fact]
    public void The_uninstaller_undoes_the_folder_registration()
    {
        var iss = Path.Combine(RepositoryRoot().FullName, "packaging", "vaktari.iss");
        var text = File.ReadAllText(iss);

        var run = Regex.Match(text, @"^\[UninstallRun\][^\[]*", RegexOptions.Multiline).Value;

        Assert.False(string.IsNullOrWhiteSpace(run),
            "vaktari.iss has no [UninstallRun]; nothing undoes the folder handler");

        Assert.Contains(Vaktari.Ui.Program.RestoreFileManagerFlag, run, StringComparison.Ordinal);

        // Per-user registration lives in HKCU, so an elevated uninstaller must
        // drop back to the signed-in user or it inspects the wrong hive and
        // finds nothing to undo.
        Assert.Contains("runascurrentuser", run, StringComparison.Ordinal);
    }
}
