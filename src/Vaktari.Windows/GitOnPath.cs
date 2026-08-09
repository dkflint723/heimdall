namespace Vaktari.Windows;

/// <summary>
/// Finds Git for Windows when it is installed but not on PATH.
///
/// **This is a default, not an edge case.** The Git for Windows installer asks
/// how you want git exposed, and "Use Git from Git Bash only" — the first
/// option, and the one it describes as most cautious — deliberately leaves PATH
/// alone. GitHub Desktop never touches PATH at all and bundles its own copy.
/// Both are ordinary ways to end up with git installed, working, and invisible
/// to any program that resolves it the normal way.
///
/// The failure that causes is silent. IVersionControl asks whether git is
/// available, is told no, and every listing renders undecorated — identical to
/// a folder with nothing to report. Nobody sees an error, so nobody knows there
/// is something to fix.
///
/// **Process-local, and deliberately so.** This edits only this process's
/// environment block, which child processes inherit and nothing outlives.
/// Writing the user's persistent PATH is a system settings change, would need
/// elevation for the machine scope, and is the installer's job to offer rather
/// than a file manager's to assume.
/// </summary>
internal static class GitOnPath
{
    /// <summary>
    /// Where Git for Windows actually lands, most conventional first.
    ///
    /// <c>cmd</c> rather than <c>bin</c> or <c>mingw64\bin</c>: all three hold a
    /// git.exe, and cmd is the one Git for Windows intends for outside callers.
    /// The others assume the MSYS environment around them.
    /// </summary>
    private static IEnumerable<string> Candidates()
    {
        string[] roots =
        [
            Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs"),
        ];

        foreach (var root in roots)
            yield return Path.Combine(root, "Git", "cmd");

        // Package managers put a shim on PATH themselves, so reaching these
        // means PATH was lost rather than never set — cheap to check anyway.
        yield return Path.Combine(
            Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
            "chocolatey", "bin");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "scoop", "shims");

        // GitHub Desktop's private copy, last because it is the least
        // deliberate: it belongs to that application and its path carries a
        // version that changes under you on update. Still a complete git, and
        // for someone who installed only GitHub Desktop it is the only one
        // there is.
        foreach (var bundled in GitHubDesktopCopies()) yield return bundled;
    }

    private static IEnumerable<string> GitHubDesktopCopies()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitHubDesktop");

        string[] versions;
        try
        {
            if (!Directory.Exists(root)) yield break;
            versions = Directory.GetDirectories(root, "app-*");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        // Newest last by name, so the highest version wins the ordering below.
        Array.Sort(versions, StringComparer.OrdinalIgnoreCase);

        for (var i = versions.Length - 1; i >= 0; i--)
            yield return Path.Combine(versions[i], "resources", "app", "git", "cmd");
    }

    /// <summary>
    /// The first of these directories holding a usable git.exe, or null.
    /// Takes its candidates as an argument so a test can point it at a folder
    /// it controls rather than at whatever this machine happens to have.
    /// </summary>
    internal static string? Locate(IEnumerable<string> candidates)
    {
        foreach (var directory in candidates)
        {
            try
            {
                var candidate = Path.Combine(directory, "git.exe");

                // Length as well as existence, for the same reason Which
                // rejects an App Execution Alias: a zero-byte executable is a
                // stub standing in for something absent, not a program.
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                    return directory;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable or malformed candidate is simply not the one.
            }
        }

        return null;
    }

    /// <summary>Whether git already resolves through PATH as it stands.</summary>
    private static bool AlreadyReachable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            try
            {
                var candidate = Path.Combine(directory, "git.exe");
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0) return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Keep looking; one bad PATH entry is not an answer.
            }
        }

        return false;
    }

    /// <summary>
    /// Puts git within reach for this process if it is installed somewhere
    /// conventional. Returns the directory added, or null if nothing was
    /// needed or nothing was found.
    ///
    /// Appended rather than prepended: a git the user deliberately put on PATH
    /// outranks one found by guessing. Reaching this at all means PATH held no
    /// git, so the position only matters for whatever is added later.
    /// </summary>
    internal static string? Ensure()
    {
        if (AlreadyReachable()) return null;

        if (Locate(Candidates()) is not { } directory) return null;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";

        Environment.SetEnvironmentVariable(
            "PATH",
            path.Length == 0 ? directory : path.TrimEnd(Path.PathSeparator) + Path.PathSeparator + directory);

        return directory;
    }
}
