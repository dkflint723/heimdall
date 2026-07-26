using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Rove.Core.FileSystem;
using Rove.Core.Places;
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

        // Providers are constructed directly for now. This is the seam that
        // becomes DI resolution once Rove.Windows exists — the window must
        // never name a platform type beyond these lines.
        IFileSystemProvider fs = new LinuxFileSystemProvider();
        IFileOperations ops = new LinuxFileOperations();
        IPlacesProvider places = new LinuxPlacesProvider(JsonSessionStore.DefaultDirectory());
        IApplicationLauncher launcher = new LinuxLauncher();
        IClipboardService clipboard = ClipboardService.ForWindow(this);

        _store = new JsonSessionStore(JsonSessionStore.DefaultDirectory());

        // Loaded synchronously so geometry is applied before first paint. An
        // async load would restore size and position after the window is
        // already on screen — a visible jump on every launch.
        var state = _store.Load();
        ApplyGeometry(state);

        _shell = new ShellViewModel(fs, ops, _store, places, launcher, clipboard)
        {
            GeometryProvider = CaptureGeometry,
        };
        _shell.PaneCreated += (_, pane) => WirePane(pane);
        DataContext = _shell;

        PathBox.KeyDown += OnPathBoxKeyDown;
        PromptInput.KeyDown += OnPromptKeyDown;

        // Handled at the window because the list lives inside a DataTemplate,
        // so there is no named control to attach to.
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble);

        // Clicking anywhere in a side makes it the active one. Tunnelling so it
        // runs before the ListBox handles the press for selection — otherwise
        // the first click on an inactive side only moves focus.
        AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, RoutingStrategies.Tunnel);

        // Dragging the splitter writes straight to the ColumnDefinitions, so
        // the ratio is read back out afterwards — otherwise the persisted
        // SplitRatio would never reflect where the divider actually sits.
        SplitHandle.DragCompleted += (_, _) => CaptureSplitRatio();

        Closing += OnClosing;
        Resized += (_, _) => _shell.NotifyWindowChanged();
        PositionChanged += (_, _) => _shell.NotifyWindowChanged();

        _shell.Start(state);

        // Build stamp. When a symptom and the code disagree, this is the one
        // line that says whether the running binary contains the fix.
        Console.Error.WriteLine(
            $"[rove] build {BuildStamp()}  clipboard=yes  split={_shell.IsSplit}");
    }

    private static string BuildStamp()
    {
        try
        {
            // AppContext.BaseDirectory rather than Assembly.Location, which is
            // empty in a single-file or AOT publish.
            var dll = Path.Combine(AppContext.BaseDirectory, "Rove.Ui.dll");
            return File.Exists(dll)
                ? File.GetLastWriteTime(dll).ToString("HH:mm:ss")
                : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Walks up from whatever was clicked to find which pane group owns it.
    /// The two sides are the same template, so there is no named control to
    /// compare against — the DataContext is the only distinguishing thing.
    /// </summary>
    private void OnPointerPressedAnywhere(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        for (var control = e.Source as Control; control is not null;
             control = control.Parent as Control)
        {
            if (control.DataContext is PaneGroupViewModel group)
            {
                _shell.ActivateGroup(group);
                return;
            }
        }
    }

    private void CaptureSplitRatio()
    {
        if (!_shell.IsSplit) return;

        var left = SplitGrid.ColumnDefinitions[0].ActualWidth;
        var right = SplitGrid.ColumnDefinitions[2].ActualWidth;
        var total = left + right;

        if (total > 1) _shell.SplitRatio = Math.Clamp(left / total, 0.1, 0.9);
    }

    // ---- geometry ------------------------------------------------------

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

        try
        {
            await _store.FlushAsync(CancellationToken.None);
            await _store.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rove] session flush failed: {ex.Message}");
        }

        _closeApproved = true;
        Close();
    }

    // ---- per-pane wiring -----------------------------------------------

    private void WirePane(PaneViewModel pane)
    {
        pane.RenameRequested -= OnRenameRequested;
        pane.RenameRequested += OnRenameRequested;

        pane.PropertyChanged -= OnPaneFilterToggled;
        pane.PropertyChanged += OnPaneFilterToggled;
    }

    private void OnPaneFilterToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneViewModel.IsFilterVisible)) return;
        if (sender is not PaneViewModel pane || !pane.IsFilterVisible) return;

        Dispatcher.UIThread.Post(() => FilterBox?.Focus());
    }

    // ---- inline prompt -------------------------------------------------

    private enum PromptMode { None, Rename, ConfirmDelete }

    private PromptMode _prompt = PromptMode.None;
    private FileEntry _renameTarget;

    private void OnRenameRequested(object? sender, FileEntry entry)
    {
        if (PromptBar is null || PromptInput is null) return;

        _prompt = PromptMode.Rename;
        _renameTarget = entry;

        PromptLabel.Text = "rename to";
        PromptInput.Text = entry.Name;
        PromptInput.IsVisible = true;
        PromptHint.Text = "enter to confirm · esc to cancel";
        PromptBar.IsVisible = true;

        PromptInput.Focus();
        PromptInput.SelectAll();
    }

    private void AskConfirmDelete()
    {
        if (PromptBar is null) return;
        if (_shell.ActiveTab is not { } pane) return;

        var count = pane.SelectedEntries.Count > 0
            ? pane.SelectedEntries.Count
            : pane.SelectedEntry is null ? 0 : 1;

        if (count == 0) return;

        _prompt = PromptMode.ConfirmDelete;

        PromptLabel.Text = $"permanently delete {count} item(s)? this cannot be undone";
        PromptInput.IsVisible = false;
        PromptHint.Text = "enter to delete · esc to cancel";
        PromptBar.IsVisible = true;
        PromptBar.Focus();
    }

    private void ClosePrompt()
    {
        _prompt = PromptMode.None;
        if (PromptBar is not null) PromptBar.IsVisible = false;
        if (PromptInput is not null) PromptInput.IsVisible = false;
    }

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_prompt != PromptMode.Rename) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                var name = PromptInput?.Text ?? "";
                ClosePrompt();
                if (!string.IsNullOrWhiteSpace(name) && name != _renameTarget.Name)
                    _ = _shell.ActiveTab?.RenameAsync(_renameTarget, name);
                break;

            case Key.Escape:
                e.Handled = true;
                ClosePrompt();
                break;
        }
    }

    // ---- input ---------------------------------------------------------

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
        if (_shell is null) return;

        // The prompt owns the keyboard while it is open.
        if (_prompt == PromptMode.ConfirmDelete)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ClosePrompt();
                _shell.ActiveTab?.DeleteSelectedCommand.Execute(null);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClosePrompt();
            }
            return;
        }

        if (_prompt == PromptMode.Rename) return;

        // Pattern matching, never direct dereference. These are XAML-generated
        // fields; if markup and code-behind ever drift, a plain `.IsFocused`
        // throws on every single keypress and takes the process with it.
        if (PathBox is { IsFocused: true }) return;

        if (FilterBox is { IsFocused: true })
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _shell.ActiveTab?.ToggleFilterCommand.Execute(null);
            }
            return;
        }

        // Ctrl+1..9 jumps to a tab, browser-style.
        if (e.KeyModifiers == KeyModifiers.Control &&
            e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            e.Handled = true;
            _shell.SelectTabByIndex(e.Key - Key.D1);
            return;
        }

        // Tab moves between sides rather than traversing focus, matching
        // Dolphin. Only when split, so it keeps its normal meaning otherwise.
        if (e.Key == Key.Tab && _shell.IsSplit && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _shell.FocusOtherPaneCommand.Execute(null);
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

            // Delete trashes, which is recoverable and needs no prompt.
            // Shift+Delete is irreversible and always confirms first.
            case Key.Delete when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                AskConfirmDelete();
                break;

            case Key.Delete:
                e.Handled = true;
                pane.TrashSelectedCommand.Execute(null);
                break;

            // Deliberately duplicated from Window.KeyBindings. This handler is
            // known to run — it is where the crash surfaced — so routing the
            // clipboard through it too means copy cannot fail silently just
            // because a KeyBinding didn't resolve.
            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                pane.CopySelectionToClipboardCommand.Execute(null);
                break;

            case Key.X when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                pane.CutSelectionToClipboardCommand.Execute(null);
                break;

            case Key.V when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                pane.PasteCommand.Execute(null);
                break;
        }
    }
}
