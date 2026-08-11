using System.Runtime.Versioning;
using Microsoft.Win32;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Clearing a folder-handler registration left behind by a previous name of
/// this application.
///
/// **This is the defect that broke a real machine.** Upgrading across the
/// rename removes the old installation and leaves its shell verb pointing at a
/// binary that no longer exists, after which every double-clicked folder fails
/// — and the error Windows shows names the missing path, not the program that
/// registered it, so there is nothing to lead anyone back to the cause.
///
/// Its own scratch subtree, like the rest of these: pointing this at the live
/// shell classes would change the machine's behaviour as a side effect of
/// running the suite.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DefaultFileManagerHealTests : IDisposable
{
    private const string Scratch = @"Software\Vaktari.HealTests\Classes";
    private const string OldBackup = @"Software\Heimdall\DefaultFileManager";
    private const string Exe = @"C:\Program Files\Vaktari\Vaktari.Ui.exe";

    private static WindowsDefaultFileManager Subject() => new(Exe, Scratch);

    private static void Claim(string cls, string verb, string command)
    {
        using (var shell = Registry.CurrentUser.CreateSubKey($@"{Scratch}\{cls}\shell"))
            shell!.SetValue(null, verb);

        using var cmd = Registry.CurrentUser.CreateSubKey(
            $@"{Scratch}\{cls}\shell\{verb}\command");

        cmd!.SetValue(null, command);
    }

    private static string? Current(string cls)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{Scratch}\{cls}\shell");
        return key?.GetValue(null) as string;
    }

    /// <summary>
    /// Saves and restores anything real under the old backup key, so a machine
    /// that still carries one is not stripped of it by running the tests. The
    /// last time a test deleted a production record it went unnoticed until the
    /// feature was needed.
    /// </summary>
    private readonly string? _existing;

    public DefaultFileManagerHealTests()
    {
        using var key = Registry.CurrentUser.OpenSubKey(OldBackup);
        _existing = key?.GetValue("Directory") as string;
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Vaktari.HealTests",
            throwOnMissingSubKey: false);

        using var key = Registry.CurrentUser.CreateSubKey(OldBackup);

        if (_existing is null) key?.DeleteValue("Directory", throwOnMissingValue: false);
        else key?.SetValue("Directory", _existing);
    }

    [Fact]
    public void A_dead_registration_from_the_old_name_is_cleared()
    {
        Claim("Directory", "OpenInHeimdall",
            "\"C:\\Program Files\\Heimdall\\NoSuchFileAnywhere.exe\" \"%1\"");

        using (var backup = Registry.CurrentUser.CreateSubKey(OldBackup))
            backup!.SetValue("Directory", "OpenInSomethingElse");

        Subject().HealPreviousName();

        Assert.Equal("OpenInSomethingElse", Current("Directory"));

        using var verb = Registry.CurrentUser.OpenSubKey(
            $@"{Scratch}\Directory\shell\OpenInHeimdall");

        Assert.Null(verb);
    }

    /// <summary>
    /// With no record of who held the class first, it is left unclaimed rather
    /// than given to an invented handler — which is what the shell looked like
    /// before anybody registered.
    /// </summary>
    [Fact]
    public void With_no_record_the_class_is_left_unclaimed()
    {
        Claim("Directory", "OpenInHeimdall", "\"C:\\Gone\\Missing.exe\" \"%1\"");

        using (var key = Registry.CurrentUser.CreateSubKey(OldBackup))
            key?.DeleteValue("Directory", throwOnMissingValue: false);

        Subject().HealPreviousName();

        Assert.True(string.IsNullOrEmpty(Current("Directory")));
    }

    /// <summary>
    /// **An old build that still works is left alone.** Somebody may be running
    /// both; dispossessing a program that is installed and functioning is not a
    /// choice this application gets to make on their behalf.
    /// </summary>
    [Fact]
    public void A_live_registration_from_the_old_name_is_left_alone()
    {
        var real = Environment.ProcessPath ?? @"C:\Windows\explorer.exe";

        Claim("Directory", "OpenInHeimdall", $"\"{real}\" \"%1\"");

        Subject().HealPreviousName();

        Assert.Equal("OpenInHeimdall", Current("Directory"));
    }

    /// <summary>A class this application holds is never touched by the heal.</summary>
    [Fact]
    public void Our_own_registration_is_untouched()
    {
        Subject().MakeDefault();

        Subject().HealPreviousName();

        Assert.True(Subject().IsDefault());
    }
}
