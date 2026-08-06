using System.Runtime.Versioning;
using Heimdall.Core.Tests;
using Xunit;

namespace Heimdall.Windows.Tests;

/// <summary>
/// Finding Git for Windows when it is installed but not on PATH.
///
/// Locate takes its candidate directories as an argument precisely so this can
/// point it at folders it creates, rather than asserting over whatever happens
/// to be installed on the machine running the suite — which would pass here,
/// fail on a CI image with no git, and prove nothing either way.
/// </summary>
[SupportedOSPlatform("windows")]
public class GitOnPathTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "heimdall-gitpath-" + Guid.NewGuid().ToString("N")[..8]);

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string DirWithGit(string name, long bytes = 4096)
    {
        var path = Dir(name);
        File.WriteAllBytes(Path.Combine(path, "git.exe"), new byte[bytes]);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* a temp dir is not worth failing over */ }
        GC.SuppressFinalize(this);
    }

    [WindowsFact]
    public void The_directory_holding_git_is_the_one_returned()
    {
        var real = DirWithGit("has-git");

        Assert.Equal(real, GitOnPath.Locate([Dir("empty"), real, DirWithGit("later")]));
    }

    /// <summary>
    /// Order is the whole contract: the list runs most conventional first, so
    /// a Program Files install beats GitHub Desktop's bundled copy.
    /// </summary>
    [WindowsFact]
    public void The_first_match_wins()
    {
        var first = DirWithGit("program-files");
        var second = DirWithGit("github-desktop");

        Assert.Equal(first, GitOnPath.Locate([first, second]));
    }

    [WindowsFact]
    public void A_directory_without_git_is_passed_over()
        => Assert.Null(GitOnPath.Locate([Dir("empty"), Dir("also-empty")]));

    /// <summary>
    /// Same reasoning as the App Execution Alias check in Which: a zero-byte
    /// executable is a stub standing in for something absent. Running one is
    /// how launching a file manager opened the Microsoft Store.
    /// </summary>
    [WindowsFact]
    public void A_zero_byte_git_is_not_a_git()
    {
        var stub = DirWithGit("stub", bytes: 0);
        var real = DirWithGit("real");

        Assert.Equal(real, GitOnPath.Locate([stub, real]));
    }

    /// <summary>
    /// A candidate that cannot be read, or is not a path at all, is not the one
    /// — and must not take the search down with it. These directories come
    /// from environment variables, which can hold anything.
    /// </summary>
    [WindowsTheory]
    [InlineData("")]
    [InlineData(@"Z:\no-such-volume\Git\cmd")]
    [InlineData("has|pipe")]
    public void An_unusable_candidate_is_survivable(string candidate)
    {
        var real = DirWithGit("real");

        Assert.Equal(real, GitOnPath.Locate([candidate, real]));
        Assert.Null(GitOnPath.Locate([candidate]));
    }

    [WindowsFact]
    public void No_candidates_at_all_is_null()
        => Assert.Null(GitOnPath.Locate([]));
}
