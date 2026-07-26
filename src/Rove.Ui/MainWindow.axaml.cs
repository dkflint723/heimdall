using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Rove.Core.FileSystem;
using Rove.Core;
using Rove.Core.Places;
using Rove.Core.Search;
using Rove.Core.Session;
using Rove.Linux;
using Rove.Ui.Session;
using Rove.Ui.ViewModels;

namespace Rove.Ui;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly JsonSessionStore _store;
    private readonly IPropertiesProvider _properties;
    private readonly IThemeProvider? _theme;
    private readonly IAccessEditor? _accessEditor;
    private bool _closeApproved;

    public MainWindow()
    {
        InitializeComponent();

        // The one and only place a platform type is named, and the one guard
        // the analyser needs — the platform assemblies are annotated
        // Linux-only, so everything inside them is free of per-call checks.
        IPlatform platform;

        if (OperatingSystem.IsLinux())
            platform = new LinuxPlatform(JsonSessionStore.DefaultDirectory());
        else
            throw new PlatformNotSupportedException(
                "No platform implementation for this operating system yet.");

        _properties = platform.Properties;
        _accessEditor = platform.AccessEditor;

        Thumbnails.ThumbnailLoader.Provider = platform.Thumbnails;
        Thumbnails.RowMetadata.Provider = platform.Metadata;
        Thumbnails.RowTags.Store = platform.Tags;
        Thumbnails.IconLoader.Provider = platform.Icons;

        if (platform.Icons is { } icons)
        {
            var probe = icons.Resolve(["inode-directory", "folder"], 32);
            Console.Error.WriteLine($"[rove] folder icon resolved to: {probe ?? "NOTHING"}");
        }

        // Applied before anything else paints, and re-applied whenever Plasma's
        // scheme changes, so the window follows the desktop live.
        _theme = platform.Theme;
        var platformIcons = platform.Icons;
        ThemeApplier.Apply(this, _theme?.Read());

        if (_theme is not null)
        {
            _theme.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                // Plasma rewrites kdeglobals in pieces; a short settle avoids
                // reading it mid-write and picking up half a scheme.
                Task.Delay(150).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                {
                    var palette = _theme.Read();

                    ThemeApplier.Apply(this, palette);

                    // Icons follow the desktop too: the resolved paths belong to
                    // the old icon theme and every cached drawable has the old
                    // text colour baked into its currentColor.
                    platformIcons?.Reload(palette?.IconTheme);
                    Thumbnails.IconLoader.Invalidate();
                }));
            });
        }

        // Not platform-specific: the clipboard comes from the toolkit.
        IClipboardService clipboard = ClipboardService.ForWindow(this);

        _store = new JsonSessionStore(JsonSessionStore.DefaultDirectory());

        // Loaded synchronously so geometry is applied before first paint. An
        // async load would restore size and position after the window is
        // already on screen — a visible jump on every launch.
        var state = _store.Load();
        ApplyGeometry(state);

        _shell = new ShellViewModel(
            platform.FileSystem, platform.Operations, _store,
            platform.Places, platform.Launcher, clipboard, platform.Search,
            platform.Scripts, platform.Tags, platform.Templates)
        {
            GeometryProvider = CaptureGeometry,
        };
        _shell.PaneCreated += (_, pane) => WirePane(pane);
        _shell.PropertiesRequested += (_, _) => ShowProperties();
        _shell.BatchRenameRequested += (_, _) => ShowBatchRename();
        _shell.ScaleApplier = ApplyScales;
        DataContext = _shell;

        PromptInput.KeyDown += OnPromptKeyDown;
        PromptConfirm.Click += (_, _) => ConfirmPrompt();
        PromptCancel.Click += (_, _) => ClosePrompt();

        // Handled at the window because the list lives inside a DataTemplate,
        // so there is no named control to attach to.
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble);

        // Clicking anywhere in a side makes it the active one. Tunnelling so it
        // runs before the ListBox handles the press for selection — otherwise
        // the first click on an inactive side only moves focus.
        AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedAnywhere, RoutingStrategies.Tunnel);

        // Tunnel, so the gesture is claimed before the listing's ScrollViewer
        // sees it — otherwise the view zooms and scrolls at the same time.
        AddHandler(PointerWheelChangedEvent, OnWheelAnywhere, RoutingStrategies.Tunnel);

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // Dragging the splitter writes straight to the ColumnDefinitions, so
        // the ratio is read back out afterwards — otherwise the persisted
        // SplitRatio would never reflect where the divider actually sits.
        SplitHandle.DragCompleted += (_, _) => CaptureSplitRatio();

        Closing += OnClosing;
        Resized += (_, _) => _shell.NotifyWindowChanged();
        PositionChanged += (_, _) => _shell.NotifyWindowChanged();

        // Applied before Start so the first paint is already at the right size.
        var geometry = state?.Windows.FirstOrDefault();
        ApplyScales(
            geometry?.FontScale is > 0 and var f ? f : 1.0,
            geometry?.IconScale is > 0 and var i ? i : 1.0);

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
        // Ctrl + wheel click resets the pane under the pointer, completing the
        // gesture: wheel to scale, click to undo. Claimed before anything else
        // sees the press, or the listing treats it as a selection.
        //
        // PointerUpdateKind, not IsMiddleButtonPressed: the latter reports the
        // *current state* of that button, which is not the same question as
        // "which button raised this press".
        var properties = e.GetCurrentPoint(this).Properties;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && properties.PointerUpdateKind
               is Avalonia.Input.PointerUpdateKind.MiddleButtonPressed)
        {
            _shell.ResetPaneScale(PaneAt(e.Source) ?? _shell.ActiveTab);

            // The accumulator would otherwise carry leftover travel from before
            // the reset into the next scroll.
            _zoomTravel = 0;

            e.Handled = true;
            return;
        }

        // Recorded here so a drag can start on the first move past the
        // threshold rather than on the press itself.
        _dragOrigin = e.GetPosition(this);
        _dragSource = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            ? PaneAt(e.Source)
            : null;
        _dragTrigger = _dragSource is null ? null : e;

        for (var control = e.Source as Control; control is not null;
             control = control.Parent as Control)
        {
            if (control.DataContext is PaneGroupViewModel group)
            {
                _shell.ActivateGroup(group);
                break;
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

    // ---- ui scale ------------------------------------------------------

    /// <summary>
    /// Base metrics at scale 1.0. Everything in the markup is a DynamicResource
    /// pointing at these, so re-writing them here restyles the whole window
    /// without touching a single control.
    /// </summary>


    /// <summary>
    /// Text and icons scale on separate axes; everything structural is derived
    /// from whichever of the two drives it. A row has to fit the taller of its
    /// label and its thumbnail, so its height cannot be a third free setting —
    /// it would only ever be set wrong.
    /// </summary>
    /// <summary>
    /// Application-level defaults, used by everything outside a pane — the
    /// sidebar, the status bar, the properties window. Each pane overrides
    /// these with its own dictionary via PaneScale.
    /// </summary>
    private void ApplyScales(double fontScale, double iconScale)
    {
        var target = Application.Current?.Resources ?? Resources;

        foreach (var (key, value) in PaneScale.Compute(fontScale, iconScale))
            target[key] = value;
    }

    /// <summary>
    /// Modal, unlike properties: a rename changes the very listing behind it,
    /// so letting the window sit open over a view that is mutating underneath
    /// would show a plan built from names that no longer exist.
    /// </summary>
    private void ShowBatchRename()
    {
        if (_shell.ActiveTab is not { } pane) return;

        var entries = pane.SelectedEntries.Count > 0
            ? pane.SelectedEntries.ToList()
            : pane.SelectedEntry is { } one ? [one]
            : new List<FileEntry>();

        if (entries.Count == 0)
        {
            pane.Status = "select something to rename first";
            return;
        }

        var model = new BatchRenameViewModel(entries,
            (entry, name) => pane.RenameAsync(entry, name));

        new BatchRenameWindow(model).ShowDialog(this);
    }

    /// <summary>
    /// Non-modal on purpose: you frequently want to compare two files, and a
    /// modal dialog makes that impossible without closing it first.
    /// </summary>
    private void ShowProperties()
    {
        if (_shell.ActiveTab is not { } pane) return;

        var paths = pane.SelectedEntries.Count > 0
            ? pane.SelectedEntries.Select(x => x.FullPath).ToList()
            : pane.SelectedEntry is { } one ? [one.FullPath]
            : new List<string> { pane.CurrentPath };

        if (paths.Count == 0) return;

        // Theme and metrics are application-scoped, so this inherits them.
        new PropertiesWindow(new PropertiesViewModel(_properties, paths, _accessEditor)).Show(this);
    }

    /// <summary>Feeds the pane its own width so columns can drop out in
    /// priority order rather than being squeezed.</summary>
    private void OnListSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control { DataContext: PaneViewModel pane })
            pane.ViewportWidth = e.NewSize.Width;
    }

    // ---- drag and drop -------------------------------------------------

    private Point _dragOrigin;
    private PaneViewModel? _dragSource;
    private bool _dragging;

    // The press that began the gesture, held until the move threshold is
    // crossed.
    //
    // This looks like retaining event args past their handler, and it is — but
    // DragDrop.DoDragDropAsync takes PointerPressedEventArgs specifically, not
    // the PointerEventArgs the move handler receives, so a drag cannot be
    // started from the move without it. Starting from the press instead would
    // mean no movement threshold, and every click on a row would begin a drag.
    // The alternative is worse than the constraint.
    private PointerPressedEventArgs? _dragTrigger;

    /// <summary>
    /// True while a drag that started inside Rove is in flight. Dragging within
    /// a file manager conventionally means move; dragging in from another
    /// application means copy. Ctrl and Shift override either way.
    /// </summary>
    private bool _internalDrag;

    /// <summary>Walks up from whatever was hit to the pane that owns it.</summary>
    private static PaneViewModel? PaneAt(object? source)
    {
        for (var control = source as Control; control is not null;
             control = control.Parent as Control)
        {
            if (control.DataContext is PaneViewModel pane) return pane;
        }

        return null;
    }

    /// <summary>The folder row under the pointer, if the drop should go into it
    /// rather than into the directory being listed.</summary>
    private static string? FolderRowAt(object? source)
    {
        for (var control = source as Control; control is not null;
             control = control.Parent as Control)
        {
            if (control.DataContext is FileEntry { IsDirectory: true } entry)
                return entry.FullPath;
        }

        return null;
    }

    /// <summary>
    /// Accumulated wheel travel. A mouse notch is a whole 1.0, but a trackpad
    /// sends a stream of fractions — stepping on each one would race from
    /// smallest to largest in a single swipe.
    /// </summary>
    private double _zoomTravel;

    private void OnWheelAnywhere(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0) return;

        // Claimed even when the accumulator has not tripped yet: releasing it
        // would scroll the list mid-zoom.
        e.Handled = true;

        // Direction reversal starts over, so a small overshoot does not need to
        // be unwound before the other direction responds.
        if (Math.Sign(e.Delta.Y) != Math.Sign(_zoomTravel)) _zoomTravel = 0;

        _zoomTravel += e.Delta.Y;

        while (Math.Abs(_zoomTravel) >= 1.0)
        {
            var up = _zoomTravel > 0;
            _zoomTravel -= up ? 1.0 : -1.0;

            // The pane under the pointer, not the active one: reaching over to
            // scale the other side without clicking into it first is the whole
            // reason the wheel gesture is nicer than the buttons.
            var pane = PaneAt(e.Source) ?? _shell.ActiveTab;

            // Shift narrows it to the icons, which is the axis people mean when
            // they say "zoom" — the labels usually want to stay put.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _shell.ScalePane(pane, 0, up ? 0.15 : -0.15);
            else
                _shell.ScalePane(pane, up ? 0.1 : -0.1, up ? 0.15 : -0.15);
        }
    }

    private void OnPointerMovedAnywhere(object? sender, PointerEventArgs e)
    {
        if (_dragging || _dragSource is null) return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragSource = null;
            _dragTrigger = null;
            return;
        }

        // A threshold, or every click on a row would begin a drag and the list
        // would become impossible to select in.
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragOrigin.X) < 6 &&
            Math.Abs(position.Y - _dragOrigin.Y) < 6) return;

        if (_dragTrigger is not null) _ = BeginDragAsync(_dragSource, _dragTrigger);
    }

    private async Task BeginDragAsync(PaneViewModel pane, PointerPressedEventArgs trigger)
    {
        var paths = pane.SelectedEntries.Count > 0
            ? pane.SelectedEntries.Select(x => x.FullPath).ToList()
            : pane.SelectedEntry is { } one ? [one.FullPath] : [];

        if (paths.Count == 0) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        _dragging = true;
        _internalDrag = true;

        try
        {
            // DataFormat.File is what other applications actually read; Avalonia
            // serialises it to text/uri-list on X11, the same route the
            // clipboard takes.
            var data = new DataTransfer();

            foreach (var path in paths)
            {
                IStorageItem? item = Directory.Exists(path)
                    ? await storage.TryGetFolderFromPathAsync(path)
                    : await storage.TryGetFileFromPathAsync(path);

                if (item is not null) data.Add(DataTransferItem.CreateFile(item));
            }

            if (data.Items.Count == 0) return;

            // Not disposed — the drag system takes ownership.
            await DragDrop.DoDragDropAsync(
                trigger, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[rove] drag failed: {ex.Message}");
        }
        finally
        {
            _dragging = false;
            _internalDrag = false;
            _dragSource = null;
            _dragTrigger = null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (!e.DataTransfer.Formats.Contains(DataFormat.File) || PaneAt(e.Source) is not { } pane)
        {
            e.DragEffects = DragDropEffects.None;
            HighlightDropTarget(null);
            return;
        }

        var destination = FolderRowAt(e.Source) ?? pane.CurrentPath;

        // Refuse a drop that would achieve nothing, so the cursor says so
        // before the click rather than a duplicate appearing after it.
        if (Meaningful(e.DataTransfer, destination).Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            HighlightDropTarget(null);
            return;
        }

        e.DragEffects = EffectFor(e.KeyModifiers);
        HighlightDropTarget(pane);
    }

    /// <summary>
    /// The dragged paths that would actually go somewhere. A file dropped into
    /// the folder it already lives in is a no-op, whether copying or moving —
    /// the previous guard only applied to copies, so a move produced "name (1)".
    /// </summary>
    private static List<string> Meaningful(IDataTransfer data, string destination)
    {
        var paths = (data.TryGetFiles() ?? [])
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .ToList();

        paths.RemoveAll(p =>
            p == destination ||
            string.Equals(Path.GetDirectoryName(p), destination, StringComparison.Ordinal));

        return paths;
    }

    private DragDropEffects EffectFor(KeyModifiers modifiers)
    {
        // Explicit modifiers win; otherwise moving within the app and copying
        // from outside it is what every desktop file manager does.
        if (modifiers.HasFlag(KeyModifiers.Control)) return DragDropEffects.Copy;
        if (modifiers.HasFlag(KeyModifiers.Shift)) return DragDropEffects.Move;

        return _internalDrag ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => HighlightDropTarget(null);

    private void HighlightDropTarget(PaneViewModel? pane)
    {
        foreach (var group in new[] { _shell.Left, _shell.Right })
        {
            if (group is null) continue;
            foreach (var tab in group.Tabs) tab.IsDropTarget = ReferenceEquals(tab, pane);
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        HighlightDropTarget(null);

        if (PaneAt(e.Source) is not { } pane) return;
        if (!e.DataTransfer.Formats.Contains(DataFormat.File)) return;

        // Dropping onto a folder row means into that folder, not into the
        // directory being listed — that is what the pointer was over.
        var target = FolderRowAt(e.Source);
        var destination = target ?? pane.CurrentPath;

        var paths = Meaningful(e.DataTransfer, destination);
        if (paths.Count == 0) return;

        var move = EffectFor(e.KeyModifiers) == DragDropEffects.Move;

        if (target is not null)
            pane.PasteIntoFolder(target, paths, move);
        else
            pane.PasteInto(paths, move);

        e.Handled = true;
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

        pane.NewTagRequested -= OnNewTagRequested;
        pane.NewTagRequested += OnNewTagRequested;

        pane.PropertyChanged -= OnPaneFilterToggled;
        pane.PropertyChanged += OnPaneFilterToggled;
    }

    /// <summary>Focus now happens through FocusBehavior.FocusOnVisible in the
    /// markup, since there is no field to focus from here.</summary>
    private void OnPaneFilterToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneViewModel.IsFilterVisible)) return;
        if (sender is not PaneViewModel pane || !pane.IsFilterVisible) return;


    }

    // ---- inline prompt -------------------------------------------------

    private enum PromptMode { None, Rename, ConfirmDelete, NewTag }

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
        PromptConfirm.Content = "rename";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "enter to confirm · esc to cancel";
        PromptBar.IsVisible = true;

        PromptInput.Focus();
        PromptInput.SelectAll();
    }

    /// <summary>
    /// The single place a confirmed prompt is acted on, so the button and the
    /// keyboard cannot drift apart.
    /// </summary>
    private void ConfirmPrompt()
    {
        var mode = _prompt;
        var target = _shell.ActiveTab;

        // Read before closing: the action must not depend on UI state that the
        // closing itself tears down.
        var name = PromptInput?.Text ?? "";
        var entry = _renameTarget;

        ClosePrompt();

        switch (mode)
        {
            case PromptMode.ConfirmDelete:
                target?.DeleteSelectedCommand.Execute(null);
                break;

            case PromptMode.Rename when !string.IsNullOrWhiteSpace(name) && name != entry.Name:
                _ = target?.RenameAsync(entry, name);
                break;

            case PromptMode.NewTag when !string.IsNullOrWhiteSpace(name):
                _ = target?.ToggleTagAsync(name.Trim());
                break;
        }
    }

    private void OnNewTagRequested(object? sender, EventArgs e)
    {
        if (PromptBar is null || PromptInput is null) return;

        _prompt = PromptMode.NewTag;

        var count = _shell.ActiveTab is { } t
            ? (t.SelectedEntries.Count > 0 ? t.SelectedEntries.Count : t.SelectedEntry is null ? 0 : 1)
            : 0;

        PromptLabel.Text = $"tag {count} selected item(s)";
        PromptInput.Text = "";
        PromptInput.IsVisible = true;
        PromptConfirm.Content = "apply tag";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "applied to the selection · esc to cancel";
        PromptBar.IsVisible = true;

        PromptInput.Focus();
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
        PromptConfirm.Content = "delete permanently";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        // Focus the button, not the bar: a focused Button takes Enter and Space
        // itself, which is a route nothing else can swallow.
        PromptConfirm.Focus();
    }

    private void ClosePrompt()
    {
        _prompt = PromptMode.None;

        if (PromptBar is not null) PromptBar.IsVisible = false;
        if (PromptInput is not null) PromptInput.IsVisible = false;
        if (PromptConfirm is not null) PromptConfirm.IsVisible = false;
        if (PromptCancel is not null) PromptCancel.IsVisible = false;
    }

    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_prompt != PromptMode.Rename) return;

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ConfirmPrompt();
                break;

            case Key.Escape:
                e.Handled = true;
                ClosePrompt();
                break;
        }
    }

    // ---- input ---------------------------------------------------------

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
                ConfirmPrompt();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClosePrompt();
            }
            return;
        }

        if (_prompt is PromptMode.Rename or PromptMode.NewTag) return;

        // Any focused text box owns the keyboard. Checking the type rather
        // than named controls, because the path and filter boxes now live
        // inside a per-pane template and have no generated fields — and it
        // is the more honest rule anyway. Escape and Enter inside those
        // boxes are handled by their own KeyBindings in the markup.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        // Ctrl+1..9 jumps to a tab, browser-style.
        if (e.KeyModifiers == KeyModifiers.Control &&
            e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            e.Handled = true;
            _shell.SelectTabByIndex(e.Key - Key.D1);
            return;
        }

        // No Ctrl+arrow zoom. It was tried and removed: this handler is on the
        // bubble phase, so a focused ListBox — which is the normal state —
        // takes arrow keys first and moves the selection instead. Winning the
        // keystroke would mean tunnelling and stealing a key the listing has a
        // legitimate claim to. Ctrl+wheel and Ctrl +/- cover it.

        // Left/Right walk the Miller chain when it is showing and the listing
        // does not own the keystroke. Without this the strip is mouse-only,
        // which is the opposite of the point of a column view.
        if (_shell.ActiveTab is { ShowColumnStrip: true } chained
            && e.KeyModifiers == KeyModifiers.None
            && e.Key is Key.Left or Key.Right
            && FocusManager?.GetFocusedElement() is not TextBox)
        {
            var current = chained.Miller.Focused ?? chained.Miller.Columns.LastOrDefault();

            if (current is not null
                && chained.Miller.Step(current, e.Key == Key.Right ? 1 : -1) is not null)
            {
                e.Handled = true;
                return;
            }
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
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                e.Handled = true;
                ShowProperties();
                break;

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
