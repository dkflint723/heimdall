namespace Heimdall.Core;

/// <summary>
/// Carries a directory over from the application's previous name.
///
/// Lives in Core because both the session store (Ui) and the script runner
/// (Linux) need it, and Linux must never reference Ui — that would invert the
/// layering and make the reference circular.
/// </summary>
public static class PreviousName
{
    /// <summary>
    /// Moves <paramref name="previous"/> to <paramref name="directory"/>, once.
    ///
    /// Only when the destination does not exist, so it can never clobber
    /// current state. Failures are reported and swallowed: starting with no
    /// settings is a worse outcome than not starting at all.
    /// </summary>
    public static void Adopt(string directory, string previous)
    {
        try
        {
            if (Directory.Exists(directory) || !Directory.Exists(previous)) return;

            Directory.Move(previous, directory);
            Console.Error.WriteLine($"[heimdall] carried settings over from {previous}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[heimdall] could not carry over {previous}: {ex.Message}");
        }
    }
}
