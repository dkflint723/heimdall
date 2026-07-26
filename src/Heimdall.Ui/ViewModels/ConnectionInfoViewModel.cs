using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Heimdall.Ui.ViewModels;

public sealed record InfoRow(string Label, string Value);

/// <summary>
/// Details of a network connection — either something mounted, or something
/// merely discovered.
///
/// One view model for both because the user is asking the same question of
/// each: what is this, and where is it. The difference is only which actions
/// apply, so those are flags rather than two near-identical classes.
/// </summary>
public sealed partial class ConnectionInfoViewModel : ObservableObject
{
    private readonly Func<Task>? _disconnect;
    private readonly Action<string>? _copy;

    public ConnectionInfoViewModel(
        string title,
        IReadOnlyList<InfoRow> rows,
        string address,
        Func<Task>? disconnect,
        Action<string>? copy)
    {
        Title = title;
        Rows = rows;
        Address = address;
        _disconnect = disconnect;
        _copy = copy;
    }

    public string Title { get; }
    public IReadOnlyList<InfoRow> Rows { get; }
    public string Address { get; }

    public bool CanDisconnect => _disconnect is not null;

    [ObservableProperty] private string _status = "";

    /// <summary>Raised when the work is done, so the window can close itself.</summary>
    public event EventHandler? Finished;

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_disconnect is null) return;

        Status = "disconnecting…";

        try
        {
            await _disconnect().ConfigureAwait(true);
            Finished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void CopyAddress()
    {
        _copy?.Invoke(Address);
        Status = "address copied";
    }

    [RelayCommand]
    private void Close() => Finished?.Invoke(this, EventArgs.Empty);
}
