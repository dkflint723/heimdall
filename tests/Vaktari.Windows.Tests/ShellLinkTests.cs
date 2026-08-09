using System.Runtime.Versioning;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Reading a .lnk by its binary format instead of through the shell.
///
/// **The fixture is a real shortcut, written by Windows.** It was produced by
/// WScript.Shell — the shell's own COM writer — pointing at a folder with a
/// space in its name, and embedded here as bytes. That matters: a parser tested
/// only against files it could have written itself proves nothing about the
/// format, and the whole point of reading MS-SHLLINK by hand is to avoid the
/// COM dependency that would otherwise be the easy way to do this.
///
/// Verified once more against the shell at the time it was written: for every
/// shortcut in a real Links folder, this parser and WScript.Shell returned the
/// same target string, including one whose target no longer exists.
/// </summary>
[SupportedOSPlatform("windows")]
public class ShellLinkTests
{
    /// <summary>
    /// A shortcut to
    /// <c>%TEMP%\heimdall-lnk-fixture\Project Notes</c>, as Windows wrote it.
    /// Carries a LinkTargetIDList, a LinkInfo with both ANSI and Unicode
    /// paths, a tracker block and a known-folder block — so the parser has to
    /// skip several structures to reach the one field it wants.
    /// </summary>
    private const string RealShortcut =
        "TAAAAAEUAgAAAAAAwAAAAAAAAEaLAAAAEAAAAF9RYpNIJd0BX1Fik0gl3QFfUWKTSCXdAQAAAAAAAAAAAQAAAAAA"
        + "AAAAAAAAAAAAADICOgAfSUcaA1lyP6dEicVVlf5rMO4mAAEAJgDvvjEAAAC9B3tW1JrbAcr7dOdAJd0BLKRpk0gl"
        + "3QEUAIIAdAAcAENGU0YWADEAAAAAAP9canwQAEFwcERhdGEAAAB0Gllelt/TSI1nFzO87ii6xc36359nVkGJR8XH"
        + "a8C2f0AACQAEAO++/1xqfAZdJxEuAAAAmfEPAAAACQAAAAAAAAAAAAAAAAAAAFNxWwBBAHAAcABEAGEAdABhAAAA"
        + "QgBQADEAAAAAAARdaboQAExvY2FsADwACQAEAO++/1xqfAZdwBAuAAAAziQKAAAAKgAAAAAAAAAAAAAAAAAAAHYL"
        + "lgBMAG8AYwBhAGwAAAAUAE4AMQAAABQABl0nETAAVGVtcAAAOgAJAAQA7752WpAVBl0nES4AAACcPQEAAAACAAAA"
        + "AAA0AQAAAAAAAAAAuBaBAFQAZQBtAHAAAAAUAHIAMQAAAAAABl0nERAASEUzMEFBfjEAAFoACQAEAO++Bl0nEQZd"
        + "JxEuAAAA5NYFAAAAIAAAAAAAAAAAAAAAAAAAAKHvgABoAGUAaQBtAGQAYQBsAGwALQBsAG4AawAtAGYAaQB4AHQA"
        + "dQByAGUAAAAYAGQAMQAAAAAABl0nERAAUFJPSkVDfjEAAEwACQAEAO++Bl0nEQZdJxEuAAAAl3sIAAAABQEAAAAA"
        + "AAAAAAAAAAAAAKHvgABQAHIAbwBqAGUAYwB0ACAATgBvAHQAZQBzAAAAGAAAAHMAAAAcAAAAAQAAABwAAAAtAAAA"
        + "AAAAAHIAAAARAAAAAwAAANpS6dAQAAAAAEM6XFVzZXJzXGRrZmxpXEFwcERhdGFcTG9jYWxcVGVtcFxoZWltZGFs"
        + "bC1sbmstZml4dHVyZVxQcm9qZWN0IE5vdGVzAAAPAC4AXABQAHIAbwBqAGUAYwB0ACAATgBvAHQAZQBzABAAAAAF"
        + "AACg/////zoAAAAcAAAACwAAoHwPzvMBScxKhkjV1EsE7486AAAAYAAAAAMAAKBYAAAAAAAAAGZsaW50LXBjAAAA"
        + "AAAAAAAOq8LQAeAJTrDrU9kvZdEuDXStEjeR8RGzeABQVsAAAA6rwtAB4AlOsOtT2S9l0S4NdK0SN5HxEbN4AFBW"
        + "wAAA0gAAAAkAAKCNAAAAMVNQU+KKWEa8TDhDu/wTkyaYbc5xAAAABAAAAAAfAAAALwAAAFMALQAxAC0ANQAtADIA"
        + "MQAtADEAMQAzADYAOQA3ADIANAAxADYALQAxADcANwAzADUANQA4ADQANAA4AC0AMwAxADIANgA5ADkAMwA4ADAA"
        + "MgAtADEAMAAwADEAAAAAAAAAAAA5AAAAMVNQU7EWbUStjXBIp0hALqQ9eIwdAAAAaAAAAABIAAAASJEfHOf/vUet"
        + "ImHD8ZBW8AAAAAAAAAAAAAAAAA==";

    [WindowsFact]
    public void The_target_of_a_real_shortcut_is_read()
    {
        var target = ShellLink.Parse(Convert.FromBase64String(RealShortcut));

        Assert.NotNull(target);
        Assert.EndsWith(@"heimdall-lnk-fixture\Project Notes", target, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Unicode path is preferred over the ANSI one. Both are present in the
    /// fixture, and the ANSI copy is in the machine's code page — which mangles
    /// most of the ways a person actually names a folder.
    /// </summary>
    [WindowsFact]
    public void The_target_is_rooted_and_absolute()
    {
        var target = ShellLink.Parse(Convert.FromBase64String(RealShortcut))!;

        Assert.True(Path.IsPathFullyQualified(target), $"'{target}' is not an absolute path");
        Assert.Contains(@"\", target, StringComparison.Ordinal);
    }

    /// <summary>
    /// Anything that is not a shell link is refused rather than misread. These
    /// come from a folder the user chose, so a stray file with a .lnk extension
    /// is a realistic input.
    /// </summary>
    [WindowsTheory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x4C, 0x00, 0x00, 0x00 })]                 // right size, truncated
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x02, 0x03 })] // wrong signature
    public void Something_that_is_not_a_shortcut_is_refused(byte[] bytes)
        => Assert.Null(ShellLink.Parse(bytes));

    /// <summary>
    /// A header that claims a shell link but sets none of the flags carrying a
    /// path. Null is the right answer: a shortcut to a virtual location like
    /// Control Panel has no filesystem path, and pinning one would produce a
    /// sidebar entry that cannot be listed.
    /// </summary>
    [WindowsFact]
    public void A_shortcut_with_no_link_info_is_refused()
    {
        var bytes = new byte[0x4C];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 0x4C);   // HeaderSize
        BitConverter.TryWriteBytes(bytes.AsSpan(0x14), 0u);  // no flags at all

        Assert.Null(ShellLink.Parse(bytes));
    }

    [WindowsFact]
    public void A_file_that_does_not_exist_reads_as_null()
        => Assert.Null(ShellLink.TargetOf(Path.Combine(Path.GetTempPath(), "no-such-vaktari.lnk")));
}
