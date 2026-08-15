using Avalonia.Input;

namespace Vaktari.Ui.Input;

/// <summary>What a mouse's extra buttons ask for.</summary>
public enum SideButtonAction
{
    None,
    Back,
    Forward,
}

/// <summary>
/// The two buttons under the thumb.
///
/// **A convention old enough that the buttons are usually unlabelled.** Explorer
/// navigates on them, so does every browser, and the nearer one is always back —
/// which is the whole reason this is a named function rather than two lines
/// inside an event handler. Getting the pair the wrong way round produces
/// something that works perfectly and feels wrong, and no build or type check
/// would say a word.
/// </summary>
public static class SideButtons
{
    /// <summary>
    /// **PointerUpdateKind, not the IsXButtonNPressed flags.** Those report the
    /// current state of a button, which is a different question from "which
    /// button raised this press" — the same distinction the middle-click reset
    /// already depends on.
    /// </summary>
    public static SideButtonAction For(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.XButton1Pressed => SideButtonAction.Back,
        PointerUpdateKind.XButton2Pressed => SideButtonAction.Forward,
        _ => SideButtonAction.None,
    };
}
