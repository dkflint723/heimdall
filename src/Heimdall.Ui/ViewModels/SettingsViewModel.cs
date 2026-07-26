using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Settings;

namespace Heimdall.Ui.ViewModels;

/// <summary>
/// Edits a copy and commits it whole, rather than writing each control as it
/// changes. Cancel then genuinely cancels, and a half-finished set of
/// preferences never reaches disk.
///
/// Only the Startup page exists so far. The remaining five are separate pieces
/// of work, each landing with the plumbing that makes its toggles do something
/// — a control that does nothing is worse than an absent one, and this project
/// requires the UI to be usable by someone with no prior knowledge of it.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsState _original;

    public SettingsViewModel(SettingsState current)
    {
        _original = current;

        var startup = current.Startup;

        _restoreLastSession = startup.ShowOnStartup == StartupLocation.RestoreSession;
        _startInHome = startup.ShowOnStartup == StartupLocation.HomeFolder;
        _startInSpecificFolder = startup.ShowOnStartup == StartupLocation.SpecificFolder;
        _startupFolder = startup.StartupFolder ?? "";
        _beginInSplitView = startup.BeginInSplitView;
        _showFilterBar = startup.ShowFilterBar;
        _locationBarEditable = startup.LocationBarEditable;
        _showFullPathInTitleBar = startup.ShowFullPathInTitleBar;
    }

    // Three booleans rather than one enum property because Avalonia's
    // RadioButton binds IsChecked, and a converter per option would be more
    // moving parts than the thing it converts. Only the setters coordinate.

    [ObservableProperty] private bool _restoreLastSession;
    [ObservableProperty] private bool _startInHome;
    [ObservableProperty] private bool _startInSpecificFolder;

    [ObservableProperty] private string _startupFolder;
    [ObservableProperty] private bool _beginInSplitView;
    [ObservableProperty] private bool _showFilterBar;
    [ObservableProperty] private bool _locationBarEditable;
    [ObservableProperty] private bool _showFullPathInTitleBar;

    /// <summary>Set when the dialog was dismissed with Save.</summary>
    public bool Saved { get; private set; }

    public SettingsState Result { get; private set; } = new();

    /// <summary>
    /// The folder box is only meaningful for one of the three choices, so it
    /// disables with the others rather than accepting input that will be
    /// ignored.
    /// </summary>
    public bool CanEditStartupFolder => StartInSpecificFolder;

    partial void OnStartInSpecificFolderChanged(bool value)
        => OnPropertyChanged(nameof(CanEditStartupFolder));

    [RelayCommand]
    private void Save()
    {
        var location = StartInSpecificFolder ? StartupLocation.SpecificFolder
            : StartInHome ? StartupLocation.HomeFolder
            : StartupLocation.RestoreSession;

        // `with` on the whole state, so pages that are not built yet keep
        // whatever is already in the file rather than being reset to defaults
        // by a dialog that never showed them.
        Result = _original with
        {
            Startup = _original.Startup with
            {
                ShowOnStartup = location,
                StartupFolder = string.IsNullOrWhiteSpace(StartupFolder) ? null : StartupFolder.Trim(),
                BeginInSplitView = BeginInSplitView,
                ShowFilterBar = ShowFilterBar,
                LocationBarEditable = LocationBarEditable,
                ShowFullPathInTitleBar = ShowFullPathInTitleBar,
            },
        };

        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CloseRequested;
}
