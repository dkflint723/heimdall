using Avalonia.Controls;
using Avalonia.Platform;

namespace Heimdall.Ui;

/// <summary>
/// The window icon, loaded once and shared by every window.
///
/// Set in code as well as in the desktop entry, because the two solve different
/// problems: the .desktop file gives the launcher its icon, while this is what
/// the title bar, Alt-Tab and the taskbar read. Relying on the desktop entry
/// alone means the icon is only right when the desktop can match the window
/// back to it — which it does by WM_CLASS, and which fails silently when the
/// two names differ.
///
/// It also travels to Windows, where there is no .desktop file at all.
/// </summary>
public static class AppIcon
{
    private static WindowIcon? _icon;
    private static bool _tried;

    public static WindowIcon? Value
    {
        get
        {
            if (_tried) return _icon;
            _tried = true;

            try
            {
                using var stream = AssetLoader.Open(
                    new Uri("avares://Heimdall.Ui/heimdall.png"));

                _icon = new WindowIcon(stream);
            }
            catch (Exception ex)
            {
                // A missing icon is cosmetic; refusing to open a window over it
                // would not be.
                Console.Error.WriteLine($"[heimdall] window icon unavailable: {ex.Message}");
            }

            return _icon;
        }
    }

    public static void Apply(Window window)
    {
        if (Value is { } icon) window.Icon = icon;
    }
}
