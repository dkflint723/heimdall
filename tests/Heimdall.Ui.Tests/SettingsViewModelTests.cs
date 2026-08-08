using Avalonia.Headless.XUnit;
using Heimdall.Core.Settings;
using Heimdall.Ui.ViewModels;
using Xunit;

namespace Heimdall.Ui.Tests;

/// <summary>
/// The Colour controls, tested where they actually connect.
///
/// **A settings control fails silently in one specific way**: it renders, it
/// accepts a click, it saves — and the value never reaches the thing it was
/// supposed to change. That is exactly how the font setting shipped broken, and
/// nothing in a build or a test run said so.
///
/// So these go through the real surface the markup binds to. The ComboBox binds
/// SelectedIndex to <c>ThemeModeIndex</c> and the checkbox binds IsChecked to
/// <c>FollowDesktopColours</c>; the dialog's Save button invokes SaveCommand and
/// the window reads <c>Result</c>. Both directions are checked, because loading
/// the wrong index is the failure that quietly rewrites a user's choice the
/// moment they open the dialog and press Save.
/// </summary>
public class SettingsViewModelTests
{
    private static SettingsState With(ThemeMode mode, bool followColours) => new()
    {
        Views = new ViewSettings { ThemeMode = mode, FollowDesktopColours = followColours },
    };

    [AvaloniaTheory]
    [InlineData(ThemeMode.FollowDesktop, 0)]
    [InlineData(ThemeMode.Light, 1)]
    [InlineData(ThemeMode.Dark, 2)]
    public void The_saved_mode_selects_the_matching_row(ThemeMode mode, int expected)
    {
        var vm = new SettingsViewModel(With(mode, followColours: false));

        Assert.Equal(expected, vm.ThemeModeIndex);
    }

    [AvaloniaTheory]
    [InlineData(0, ThemeMode.FollowDesktop)]
    [InlineData(1, ThemeMode.Light)]
    [InlineData(2, ThemeMode.Dark)]
    public void The_selected_row_is_what_gets_saved(int index, ThemeMode expected)
    {
        var vm = new SettingsViewModel(With(ThemeMode.FollowDesktop, followColours: false))
        {
            ThemeModeIndex = index,
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal(expected, vm.Result.Views.ThemeMode);
    }

    /// <summary>
    /// Opening the dialog and pressing Save without touching anything must give
    /// back what was there. This is the regression that matters most for a
    /// setting with no control for a long time: a value only reachable by
    /// hand-editing the file is exactly the value a round-trip is most likely to
    /// silently drop.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(ThemeMode.Light, true)]
    [InlineData(ThemeMode.Dark, false)]
    [InlineData(ThemeMode.FollowDesktop, true)]
    public void Saving_without_changing_anything_preserves_both(ThemeMode mode, bool colours)
    {
        var vm = new SettingsViewModel(With(mode, colours));

        vm.SaveCommand.Execute(null);

        Assert.Equal(mode, vm.Result.Views.ThemeMode);
        Assert.Equal(colours, vm.Result.Views.FollowDesktopColours);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_desktop_colours_switch_saves_what_it_shows(bool on)
    {
        var vm = new SettingsViewModel(With(ThemeMode.FollowDesktop, !on))
        {
            FollowDesktopColours = on,
        };

        vm.SaveCommand.Execute(null);

        Assert.Equal(on, vm.Result.Views.FollowDesktopColours);
    }
}
