using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Vaktari.Ui.Tests.TestApp))]

// **One application and one UI thread, so one test at a time.**
//
// xunit runs test CLASSES in parallel by default, and that was never sound
// here: every headless test in this assembly shares a single Application, and
// ThemeApplierTests reads back Application.Current.RequestedThemeVariant and
// its resources — application-wide state another class can be writing at the
// same moment. It survived only because nothing yielded the UI thread
// mid-test; the first test that pumped the dispatcher turned it into a race
// that failed twice and then would not reproduce, which is the worst kind of
// test failure to be handed.
//
// The suite runs in about half a second, so serialising it costs nothing worth
// weighing against that.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Vaktari.Ui.Tests;

/// <summary>
/// The application the headless tests run inside.
///
/// **Deliberately not Vaktari.Ui's own App.** That one builds a platform, a
/// session store and a shell view model on startup — it wants a real machine
/// with real folders, and a test that needs all of it can only ever be an
/// end-to-end test. What these tests exercise is narrower and more valuable:
/// the markup and the theme rules, which is where this project's bugs have
/// actually been.
///
/// FluentTheme is loaded because it is half the subject. Two of the faults
/// these tests pin came from Fluent styling a control's TEMPLATE, where a local
/// value on the control cannot reach — a test against a bare Avalonia would
/// have passed while the shipped application was wrong.
/// </summary>
public class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
