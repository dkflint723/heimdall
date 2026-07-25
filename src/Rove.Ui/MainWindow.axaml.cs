using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Rove.Core.FileSystem;
using Rove.Core.Session;
using Rove.Linux;
using Rove.Ui.Session;
using Rove.Ui.ViewModels;

namespace Rove.Ui;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly JsonSessionStore _store;
    private bool _closeApproved;

    public MainWindow()
    {
        InitializeComponent();

        // Provider is constructed directly for now. This is the seam that
        // becomes DI resolution once Rove.Windows exists — the window must
        // never name a platform type beyond this line.
        IFileSystemProvider fs = new LinuxFileSystemProvider();

        _store = new JsonSessionStore(JsonSessionStore.DefaultDirectory());

        // Loaded synchronously so geometry is applied before first paint.
        // An async load would restore size and position after the window is
        // already on screen — a visible jump on every launch.
        var state = _store.Load();
        ApplyGeometry(state);

        _shell = new ShellViewModel(fs, _store) { GeometryProvider = CaptureGeometry };
        DataContext = _shell;

        PathBox.KeyDown += OnPathBoxKeyDown;

        // Handled at the window because the list lives inside a DataTemplate,
        // so there is no named control to attach to.
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble);

        Closing += OnClosing;
        Resized += (_, _) => _shell.NotifyWindowChanged();
        PositionChanged += (_, _) => _shell.NotifyWindowChanged();

        _shell.Start(state);
    }

    private void ApplyGeometry(SessionState? state)
    {
        if (state?.Windows.FirstOrDefault() is not { } w) return;

        if (w.Width > 200) Width = w.Width;
        if (w.Height > 200) Height = w.Height;

        if (w.X != 0 || w.Y != 0)
            Position = new PixelPoint((int)w.X, (int)w.Y);

        if (w.IsMaximized)
            WindowState = Avalonia.Controls.WindowState.Maximized;
    }

    private WindowSession CaptureGeometry()
    {
        var maximized = WindowState == Avalonia.Controls.WindowState.Maximized;

        return new WindowSession
        {
            // While maximized the live bounds are the screen, not the size to
            // return to, so the stored values are left alone.
            X = maximized ? 0 : Position.X,
            Y = maximized ? 0 : Position.Y,
            Width = maximized ? 1000 : Width,
            Height = maximized ? 680 : Height,
            IsMaximized = maximized,
        };
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;

        // Cancel, flush, then close for real. Awaiting inside an async void
        // handler does not hold the window open — the process can otherwise
        // exit with the write still in flight.
        e.Cancel = true;

        await _store.FlushAsync(CancellationToken.None);
        await _store.DisposeAsync();

        _closeApproved = true;
        Close();
    }

    private void OnPathBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell.ActiveTab is not { } pane) return;

        switch (e.Key)
        {
            // PathText is the box's own buffer, deliberately separate from
            // CurrentPath. Navigation is the only thing that may move
            // CurrentPath — history, refresh and the session file all trust it.
            case Key.Enter:
                e.Handled = true;
                _ = pane.NavigateAsync(pane.PathText);
                break;

            case Key.Escape:
                e.Handled = true;
                pane.PathText = pane.CurrentPath;
                break;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is FileEntry entry)
            _ = _shell.ActiveTab?.OpenAsync(entry);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (PathBox.IsFocused) return;

        // Ctrl+1..9 jumps to a tab, browser-style.
        if (e.KeyModifiers == KeyModifiers.Control &&
            e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            e.Handled = true;
            _shell.SelectTabByIndex(e.Key - Key.D1);
            return;
        }

        if (_shell.ActiveTab is not { } pane) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = pane.OpenSelectedAsync();
                break;

            case Key.Back:
                e.Handled = true;
                _ = pane.GoBackAsync();
                break;
        }
    }
}
