using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vaktari.Core.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The settings window builds and lays out.
///
/// **Compiling is not the same as running.** Avalonia checks binding paths at
/// build time, and it was a style that compiled perfectly which killed the
/// process when its menu opened in 0.7.0 — the fault only exists once controls
/// are realised. This window is now a page of them, several data-driven, and
/// nothing else in the suite has ever instantiated it.
///
/// Deliberately shallow: it asserts that the thing opens and that the theme
/// list is populated and bound. What each control does belongs in the tests for
/// the view model, which is where it can be asserted without a window.
/// </summary>
public sealed class SettingsWindowRendersTests
{
    [AvaloniaFact]
    public void It_opens_with_the_icon_theme_list_bound()
    {
        var model = new SettingsViewModel(new SettingsState());

        var window = new Vaktari.Ui.SettingsWindow { DataContext = model };

        window.Show();

        // Realises the templates: a binding that throws does so here rather
        // than at construction.
        window.Measure(new Avalonia.Size(700, 560));
        window.Arrange(new Avalonia.Rect(0, 0, 700, 560));

        var combo = window.GetVisualDescendants()
            .OfType<ComboBox>()
            .FirstOrDefault(c => ReferenceEquals(c.ItemsSource, model.IconThemeChoices));

        Assert.NotNull(combo);

        // Vaktari's own icons is always there, and is what a fresh settings
        // file selects.
        Assert.NotEmpty(model.IconThemeChoices);
        Assert.Same(model.SelectedIconTheme, combo!.SelectedItem);
        Assert.Equal("", model.SelectedIconTheme!.Folder);

        window.Close();
    }
}
