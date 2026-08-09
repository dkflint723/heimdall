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
}
