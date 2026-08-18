using System.Runtime.CompilerServices;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A fact that only runs on Windows.
///
/// The same attribute Vaktari.Core.Tests defines, and for the same reason it
/// gives: a conditional expectation — <c>expected = IsWindows() ? x : y</c> —
/// asserts that the code does whatever it currently does, which is not a test.
/// Assertions about drive letters are about Windows, so they say so and run
/// there.
///
/// Written out rather than shared with the other test project: that one is on
/// xunit v2 and this one on v3, and v3 wants the source position passed through
/// so a skipped test still reports where it came from.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Asserts Windows path shapes; runs on Windows only.";
    }
}
