namespace Heimdall.Core;

/// <summary>What a request to become — or stop being — the default did.</summary>
public sealed record DefaultChange(bool Succeeded, string Message);

/// <summary>
/// Making Heimdall the program that opens a folder when you double-click one.
///
/// **The two platforms mean genuinely different things by this**, and the
/// interface deliberately does not pretend otherwise — <see cref="Caveat"/>
/// exists so the window can say what will actually happen rather than offering
/// a switch that quietly does less than it claims.
///
/// On a freedesktop desktop it is a real, complete setting: one MIME
/// association, honoured by every application that asks the desktop to open a
/// folder.
///
/// On Windows there is no such setting. Explorer is not replaceable, and
/// nothing in Settings offers to. What CAN be done is redirect the default verb
/// for the Directory and Drive classes, which is what every third-party file
/// manager on Windows does and what covers double-clicking a folder. Win+E, the
/// taskbar's File Explorer pin and Explorer's own navigation stay Explorer,
/// because those are hardcoded to it.
/// </summary>
public interface IDefaultFileManager
{
    /// <summary>Whether Heimdall opens folders today.</summary>
    bool IsDefault();

    /// <summary>
    /// Become the default, remembering whatever held the role so
    /// <see cref="Restore"/> can give it back. Never silently discards an
    /// existing handler: on a machine with another file manager installed,
    /// that handler is somebody's deliberate choice.
    /// </summary>
    DefaultChange MakeDefault();

    /// <summary>Hand the role back to whatever had it before.</summary>
    DefaultChange Restore();

    /// <summary>
    /// The honest limits, in the window, beside the control. Empty where there
    /// are none worth saying.
    /// </summary>
    string Caveat { get; }
}
