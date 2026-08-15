using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// What somebody may type in the path bar.
///
/// **These are the names Windows itself uses.** %ProgramFiles% appears in its
/// own dialogs, its documentation and every installer script ever written, and
/// Explorer accepts it in the address bar — so typing it here and being told
/// there is no such folder is Vaktari being the odd one out.
/// </summary>
public sealed class PathVariableTests
{
    [WindowsFact]
    public void An_environment_variable_becomes_the_folder_it_names()
    {
        var expected = Environment.GetEnvironmentVariable("ProgramFiles");

        Assert.NotNull(expected);
        Assert.Equal(expected, PathVariables.Expand("%ProgramFiles%"));
        Assert.Equal(
            Path.Combine(expected!, "Common Files"),
            PathVariables.Expand(@"%ProgramFiles%\Common Files"));
    }

    /// <summary>
    /// **%SystemDrive% expands to "C:", and that is not a folder.** A bare
    /// drive letter means "wherever this process is on that drive" in Windows,
    /// so it would have opened the working directory — which is the one thing
    /// nobody means by C:.
    /// </summary>
    [WindowsFact]
    public void A_drive_on_its_own_means_the_root_of_that_drive()
    {
        var drive = Environment.GetEnvironmentVariable("SystemDrive");

        Assert.NotNull(drive);

        var expanded = PathVariables.Expand("%SystemDrive%");

        Assert.Equal(drive + Path.DirectorySeparatorChar, expanded);
        Assert.True(Directory.Exists(expanded), $"'{expanded}' should be a real folder");
    }

    [Fact]
    public void The_home_shorthands_work()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(home, PathVariables.Expand("~"));
        Assert.Equal(home, PathVariables.Expand("%Home%"));

        // Separators normalised on both sides: ~/Desktop keeps the slash it was
        // typed with, and Windows accepts either.
        Assert.Equal(
            Path.Combine(home, "Desktop").Replace('/', Path.DirectorySeparatorChar),
            PathVariables.Expand("~/Desktop").Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The folders that read exactly like environment variables and are nothing
    /// of the sort — Windows keeps these in the registry, and a localised or
    /// relocated Documents is only findable through the platform.
    /// </summary>
    [WindowsTheory]
    [InlineData("%Documents%", Environment.SpecialFolder.MyDocuments)]
    [InlineData("%Pictures%", Environment.SpecialFolder.MyPictures)]
    [InlineData("%Music%", Environment.SpecialFolder.MyMusic)]
    public void The_known_folders_resolve_through_the_platform(
        string typed, Environment.SpecialFolder folder)
    {
        Assert.Equal(Environment.GetFolderPath(folder), PathVariables.Expand(typed));
    }

    /// <summary>
    /// **An unknown name is left exactly as typed.** Expanding it to nothing
    /// turns a typo into a different valid path — %ProgramFilez%\Vaktari would
    /// become \Vaktari, which is the root of the drive — and the error for a
    /// folder that does not exist is far more useful when it still shows what
    /// was actually written.
    /// </summary>
    [Fact]
    public void A_name_that_means_nothing_is_left_alone()
    {
        Assert.Equal(@"%ProgramFilez%\Vaktari", PathVariables.Expand(@"%ProgramFilez%\Vaktari"));
        Assert.Equal("$NOTHING/here", PathVariables.Expand("$NOTHING/here"));
    }

    /// <summary>
    /// A real folder may contain a percent sign or a dollar, and rewriting one
    /// would send somebody somewhere else entirely.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Reports\100% complete")]
    [InlineData(@"C:\prices\$5 menu")]
    [InlineData(@"C:\Users\flint\Documents")]
    public void An_ordinary_path_passes_through_untouched(string path)
    {
        Assert.Equal(path, PathVariables.Expand(path));
    }

    /// <summary>
    /// The bin and Recent are not paths and must not be rewritten. Spelled out
    /// rather than referenced, because those constants live in the interface
    /// assembly and this one cannot see them — which is exactly why the rule is
    /// worth pinning here rather than assumed.
    /// </summary>
    [Theory]
    [InlineData("vaktari:trash")]
    [InlineData("vaktari:recent-files")]
    public void A_virtual_listing_is_not_a_path(string virtualPath)
    {
        Assert.Equal(virtualPath, PathVariables.Expand(virtualPath));
    }

    [Fact]
    public void Nothing_typed_is_nothing_expanded()
    {
        Assert.Equal("", PathVariables.Expand(null));
        Assert.Equal("", PathVariables.Expand("   "));
    }
}
