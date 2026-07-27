using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Heimdall.Core.FileSystem;
using Heimdall.Core;
using Heimdall.Core.Places;
using Heimdall.Core.Search;
using Heimdall.Core.Session;
using Heimdall.Core.Settings;
using Heimdall.Linux;
using Heimdall.Ui.Session;
using Heimdall.Ui.Settings;
using Heimdall.Ui.ViewModels;

namespace Heimdall.Ui;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly JsonSessionStore _store;

    // Preferences, as distinct from the session. Read before it, because the
    // startup setting decides whether the session is consulted at all.
    private readonly JsonSettingsStore _settingsStore;
    private readonly SettingsState _settings;

    private ITrashMaintenance? _trashMaintenance;
    private DispatcherTimer? _trashTimer;
    private readonly IPropertiesProvider _properties;
    private readonly IThemeProvider? _theme;
    private readonly IAccessEditor? _accessEditor;
    private bool _closeApproved;

    public MainWindow()
    {
        InitializeComponent();
        AppIcon.Apply(this);

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
            Console.Error.WriteLine($"[heimdall] folder icon resolved to: {probe ?? "NOTHING"}");
        }

        // Settings BEFORE the theme, and this ordering is load-bearing rather
        // than tidy. ThemeApplier reads AppSettings.Current to decide whether a
        // configured font beats Plasma's — so loading settings afterwards meant
        // it read empty defaults and the font setting did nothing at all, even
        // across a restart. Settings are the first thing this constructor does
        // for the same reason they precede the session load below.
        _settingsStore = new JsonSettingsStore(JsonSessionStore.DefaultDirectory());
        _settings = _settingsStore.Load();
        _settingsStore.EnsureFileExists(_settings);

        AppSettings.Apply(_settings);

        // Logged at startup, not when the settings dialog opens. The count only
        // appeared on opening the dialog, which made "no line printed" mean two
        // different things and cost a diagnostic round trip. Compare with:
        //   fc-list : family | tr ',' '\n' | sort -u | wc -l
        Console.Error.WriteLine(
            $"[heimdall] fontlist: {Avalonia.Media.FontManager.Current.SystemFonts.Count} "
            + "families visible to Avalonia");

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
            platform.Scripts, platform.Tags, platform.Templates, platform.Sharing)
        {
            GeometryProvider = CaptureGeometry,
        };
        _shell.PaneCreated += (_, pane) => WirePane(pane);
        _shell.PropertiesRequested += (_, _) => ShowProperties();
        _shell.SettingsRequested += (_, _) => ShowSettings();
        _shell.BatchRenameRequested += (_, _) => ShowBatchRename();
        _shell.UseRemotes(platform.Remotes);
        _shell.UseDiscovery(platform.Discovery);
        _shell.UseProperties(platform.Properties);

        _shell.ConnectionInfoRequested += (_, info) =>
            new ConnectionWindow(info).ShowDialog(this);

        _shell.ShareDialogRequested += (_, request) =>
            new ShareWindow(request).ShowDialog(this);

        _shell.ConnectRequested += OnConnectRequested;

        // The clipboard belongs to the view, so the shell asks rather than reaches.
        _shell.CopyTextRequested += async (_, url) =>
        {
            try
            {
                if (Clipboard is { } clipboard) await clipboard.SetTextAsync(url);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[heimdall] clipboard: {ex.Message}");
            }
        };

        _shell.ScaleApplier = ApplyScales;
        DataContext = _shell;

        PromptInput.KeyDown += OnPromptKeyDown;
        PromptConfirm.Click += (_, _) => ConfirmPrompt();
        PromptCancel.Click += (_, _) => ClosePrompt();

        // Handled at the window because the list lives inside a DataTemplate,
        // so there is no named control to attach to.
        AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);

        // Its single-click twin. Both are always registered and the preference
        // is read at gesture time, because the rows live inside a DataTemplate
        // and there is no list of realized controls to re-attach when the
        // setting changes.
        AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Bubble);

        // Tab has to be caught on the way DOWN. Keyboard navigation claims it
        // before any bubble handler runs, so by the time the window sees it
        // focus has already left the box. Tunnel reaches the window first.
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);

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

        // A folder named on the command line, and any handed over by a later
        // launch. Without this the window ignored the path it was asked for,
        // which as a default file manager is the whole job.
        if (Program.Instance is { } instance)
            instance.PathsReceived += (_, paths) => OpenPaths(paths, activate: true);

        if (Program.StartupPaths.Length > 0)
            Dispatcher.UIThread.Post(() => OpenPaths(Program.StartupPaths, activate: false));

        Closing += OnClosing;
        Resized += (_, _) => _shell.NotifyWindowChanged();
        PositionChanged += (_, _) => _shell.NotifyWindowChanged();

        // Applied before Start so the first paint is already at the right size.
        var geometry = state?.Windows.FirstOrDefault();
        ApplyScales(
            geometry?.FontScale is > 0 and var f ? f : 1.0,
            geometry?.IconScale is > 0 and var i ? i : 1.0);

        // The startup setting decides whether the session is consulted at all,
        // which is the whole reason settings are loaded before it. Restoring
        // stays the default: forgetting open folders is the complaint this
        // project exists to answer.
        var startup = _settings.Startup;

        var restore = startup.ShowOnStartup == StartupLocation.RestoreSession;

        var openFolder = startup.ShowOnStartup switch
        {
            StartupLocation.SpecificFolder when
                !string.IsNullOrWhiteSpace(startup.StartupFolder)
                && Directory.Exists(startup.StartupFolder) => startup.StartupFolder,

            // A configured folder that no longer exists falls back to home
            // rather than opening nothing — an unremovable empty window would
            // be a worse failure than quietly ignoring a stale path.
            _ => null,
        };

        _shell.Start(restore ? state : null, openFolder);

        ApplyStartupPreferences(startup);

        StartTrashMaintenance(platform.TrashMaintenance);

        // Build stamp. When a symptom and the code disagree, this is the one
        // line that says whether the running binary contains the fix.
        Console.Error.WriteLine(
            $"[heimdall] build {BuildStamp()}  clipboard=yes  split={_shell.IsSplit}");
    }

    private static string BuildStamp()
    {
        try
        {
            // AppContext.BaseDirectory rather than Assembly.Location, which is
            // empty in a single-file or AOT publish.
            var dll = Path.Combine(AppContext.BaseDirectory, "Heimdall.Ui.dll");
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

        var entries = pane.Selection.Count > 0
            ? pane.Selection.ToList()
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
    /// <summary>
    /// Saving swaps AppSettings.Current and writes the file. Most of what the
    /// Startup page controls only means anything at launch, so it is applied
    /// then rather than re-run here — except the title bar, which is visible
    /// right now and would otherwise look broken until a restart.
    /// </summary>
    private void ShowSettings()
    {
        var model = new SettingsViewModel(AppSettings.Current);
        var window = new SettingsWindow(model);

        window.Closed += (_, _) =>
        {
            if (!model.Saved) return;

            AppSettings.Apply(model.Result);
            _settingsStore.Save(model.Result);

            // The font lives in the theme resources, and ThemeApplier is the
            // only thing that writes them — so a saved font does nothing until
            // this runs. It was called at startup and on a Plasma scheme change
            // and nowhere else, which is why changing the font appeared to do
            // nothing at all.
            ThemeApplier.Apply(this, _theme?.Read());

            // Most settings are read at the moment they matter. Sorting and the
            // status bar are not — a listing already on screen was ordered under
            // the old rule, and a visibility binding needs telling.
            _shell.OnSettingsChanged();

            Title = model.Result.Startup.ShowFullPathInTitleBar
                    && _shell.ActiveTab is { } pane
                ? $"{pane.CurrentPath} — Heimdall"
                : "Heimdall";
        };

        window.ShowDialog(this);
    }

    private void ShowProperties()
    {
        if (_shell.ActiveTab is not { } pane) return;

        var paths = pane.Selection.Count > 0
            ? pane.Selection.Select(x => x.FullPath).ToList()
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
    /// True while a drag that started inside Heimdall is in flight. Dragging within
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
        var paths = pane.Selection.Count > 0
            ? pane.Selection.Select(x => x.FullPath).ToList()
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
            Console.Error.WriteLine($"[heimdall] drag failed: {ex.Message}");
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

    /// <summary>
    /// Opens folders in tabs. Files resolve to the folder holding them, because
    /// "open containing folder" is the request the desktop actually sends.
    /// </summary>
    private void OpenPaths(IReadOnlyList<string> paths, bool activate)
    {
        foreach (var raw in paths)
        {
            var path = raw;

            if (File.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent)
                path = parent;

            if (!Directory.Exists(path)) continue;

            _shell.OpenInNewTab(path);
        }

        if (!activate) return;

        // Raise the existing window: the user asked to see a folder, and
        // silently loading it behind whatever they were doing is not that.
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        Activate();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved) return;

        // Asked before anything is torn down, and only when there is something
        // to lose. Off by default: the session is restored on next launch, so
        // closing a window full of tabs is not actually destructive here — which
        // is exactly why this is a preference rather than the behaviour.
        if (AppSettings.Current.General.ConfirmClosingMultipleTabs && CountOpenTabs() > 1)
        {
            e.Cancel = true;

            var confirmed = await ConfirmCloseAsync();
            if (!confirmed) return;
        }

        // Cancel, flush, then close for real. Awaiting inside an async void
        // handler does not hold the window open — the process can otherwise
        // exit with the write still in flight.
        e.Cancel = true;

        // Two independent concerns, so two try blocks. They were one, sequenced
        // shares-first: a throw from StopAllSharesAsync then skipped the flush
        // AND the dispose, and the single catch still printed "session flush
        // failed" for a flush that had never been attempted.
        //
        // Session goes first now. It is the one whose loss the user would
        // actually notice, and it cannot fail because of a subprocess.
        try
        {
            await _store.FlushAsync(CancellationToken.None);
            await _store.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[heimdall] session flush failed: {ex.Message}");
        }

        try
        {
            // Servers we started are ours to stop; a share outliving the window
            // would keep a folder on the network with nothing showing it.
            await _shell.StopAllSharesAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[heimdall] stopping shares failed: {ex.Message}");
        }

        _closeApproved = true;
        Close();
    }

    /// <summary>
    /// Startup preferences that act on the window once it exists. Separate from
    /// the restore decision above because these apply whether or not a session
    /// was restored.
    /// </summary>
    private void ApplyStartupPreferences(StartupSettings startup)
    {
        Title = startup.ShowFullPathInTitleBar && _shell.ActiveTab is { } titled
            ? $"{titled.CurrentPath} — Heimdall"
            : "Heimdall";

        if (startup.BeginInSplitView && !_shell.IsSplit)
            _shell.ToggleSplitCommand.Execute(null);

        if (_shell.ActiveTab is not { } pane) return;

        if (startup.ShowFilterBar) pane.IsFilterVisible = true;

        // Last, because BeginEditPath takes focus and anything set afterwards
        // would be fighting it for the caret.
        if (startup.LocationBarEditable) pane.BeginEditPath();
    }

    private int CountOpenTabs()
    {
        var total = _shell.Left.Tabs.Count;
        if (_shell.Right is { } right) total += right.Tabs.Count;

        return total;
    }

    /// <summary>
    /// A real dialog rather than the prompt bar: the prompt bar lives inside
    /// the window being closed, and driving a close decision from a control
    /// that is about to be destroyed is the shape of bug this project has
    /// already paid for once with Shift+Delete.
    /// </summary>
    private async Task<bool> ConfirmCloseAsync()
    {
        var dialog = new Window
        {
            Title = "Close Heimdall",
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        AppIcon.Apply(dialog);

        var result = false;

        var close = new Button { Content = "close anyway", Padding = new Thickness(14, 4) };
        var cancel = new Button { Content = "cancel", Padding = new Thickness(14, 4) };

        close.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = $"{CountOpenTabs()} tabs are open. Close anyway?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, close },
                },
            },
        };

        // Focused so Enter and Space reach a real button rather than a
        // hand-rolled key path.
        cancel.Focus();

        await dialog.ShowDialog(this);

        // Deliberately does NOT close. Calling Close() here would re-enter
        // OnClosing with _closeApproved still false and confirm forever; the
        // caller falls through to the existing flush-then-close path instead.
        return result;
    }

    /// <summary>
    /// Trash expiry, at startup and then hourly.
    ///
    /// Hourly rather than on a shorter tick because nothing here is urgent —
    /// a trash that is one hour over its age limit is not a problem — and
    /// because each sweep walks the trash to size it, which is real work to be
    /// doing behind someone's back.
    /// </summary>
    private void StartTrashMaintenance(ITrashMaintenance? maintenance)
    {
        if (maintenance is null) return;

        _trashMaintenance = maintenance;

        _ = SweepTrashAsync();

        _trashTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _trashTimer.Tick += (_, _) => _ = SweepTrashAsync();
        _trashTimer.Start();
    }

    private async Task SweepTrashAsync()
    {
        if (_trashMaintenance is not { } maintenance) return;

        try
        {
            var policy = AppSettings.Current.Trash;

            var result = await maintenance.SweepAsync(policy, CancellationToken.None);

            // Said out loud when it acted. The application deleting files with
            // nobody watching is exactly the thing that should not be silent.
            if (result.Removed > 0)
            {
                var freed = ByteSize.Format(result.BytesFreed);

                Console.Error.WriteLine(
                    $"[heimdall] trash: removed {result.Removed} item(s), freed {freed}"
                    + (result.Skipped > 0 ? $", skipped {result.Skipped} undated" : ""));

                _shell.OperationStatus = $"trash: removed {result.Removed} item(s), freed {freed}";
            }
            else if (result.OverLimit)
            {
                _shell.OperationStatus = "trash is over its size limit";
            }
        }
        catch (Exception ex)
        {
            // A failed sweep must never take the window with it.
            Console.Error.WriteLine($"[heimdall] trash sweep failed: {ex.Message}");
        }
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

    private enum PromptMode { None, Rename, ConfirmDelete, ConfirmTrash, NewTag, Connect }

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

            case PromptMode.ConfirmTrash:
                target?.TrashSelectedCommand.Execute(null);
                break;

            case PromptMode.Rename when !string.IsNullOrWhiteSpace(name) && name != entry.Name:
                _ = target?.RenameAsync(entry, name);
                break;

            case PromptMode.NewTag when !string.IsNullOrWhiteSpace(name):
                _ = target?.ToggleTagAsync(name.Trim());
                break;

            case PromptMode.Connect when !string.IsNullOrWhiteSpace(name):
                _ = _shell.ConnectToAsync(name.Trim());
                break;
        }
    }

    /// <summary>
    /// Reuses the prompt bar rather than adding a dialog: it already handles
    /// focus, Enter and Escape, and a server address is just another line of
    /// text to type.
    /// </summary>
    private void OnConnectRequested(object? sender, EventArgs e)
    {
        if (PromptBar is null || PromptInput is null) return;

        _prompt = PromptMode.Connect;

        PromptLabel.Text = "connect to";
        PromptInput.Text = "smb://";
        PromptInput.IsVisible = true;
        PromptConfirm.Content = "connect";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "smb:// · sftp:// · ftp:// · dav:// — esc to cancel";
        PromptBar.IsVisible = true;

        PromptInput.Focus();

        // Caret at the end, not a selection: the scheme is a starting point to
        // type after, not something to overwrite.
        PromptInput.CaretIndex = PromptInput.Text.Length;
    }

    private void OnNewTagRequested(object? sender, EventArgs e)
    {
        if (PromptBar is null || PromptInput is null) return;

        _prompt = PromptMode.NewTag;

        var count = _shell.ActiveTab is { } t
            ? (t.Selection.Count > 0 ? t.Selection.Count : t.SelectedEntry is null ? 0 : 1)
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

        var count = pane.Selection.Count > 0
            ? pane.Selection.Count
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

    /// <summary>
    /// Off by default, because trash is reversible and a prompt on a reversible
    /// action trains people to dismiss prompts. Dolphin offers it, so it is
    /// here for anyone who wants it.
    /// </summary>
    private void AskConfirmTrash()
    {
        if (PromptBar is null) return;
        if (_shell.ActiveTab is not { } pane) return;

        var count = pane.Selection.Count > 0
            ? pane.Selection.Count
            : pane.SelectedEntry is null ? 0 : 1;

        if (count == 0) return;

        _prompt = PromptMode.ConfirmTrash;

        PromptLabel.Text = $"move {count} item(s) to trash?";
        PromptInput.IsVisible = false;
        PromptConfirm.Content = "move to trash";
        PromptConfirm.IsVisible = true;
        PromptCancel.IsVisible = true;
        PromptHint.Text = "esc to cancel";
        PromptBar.IsVisible = true;

        // Focus the button, for the same reason the delete prompt does: a
        // focused Button takes Enter and Space itself.
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

    /// <summary>
    /// What the desktop is set to, when it says so. Null means it did not, and
    /// this application's own default (double) applies. Set from the theme
    /// palette, which is re-read on startup, on a Plasma change, and on save.
    /// </summary>
    public static bool? SystemSingleClick { get; set; }

    /// <summary>
    /// Single click when the preference says so, or when it defers to a desktop
    /// that says so.
    /// </summary>
    private static bool OpensOnSingleClick
        => AppSettings.Current.Navigation.OpenItemsWith switch
        {
            ActivationClick.Single => true,
            ActivationClick.Double => false,
            _ => SystemSingleClick ?? false,
        };

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (!OpensOnSingleClick) return;

        if ((e.Source as Control)?.DataContext is FileEntry entry)
            _ = _shell.ActiveTab?.OpenAsync(entry);
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Otherwise the second click of a double re-opens what the first
        // already did — harmless when navigating into a folder, but it would
        // launch an application twice.
        if (OpensOnSingleClick) return;

        if ((e.Source as Control)?.DataContext is FileEntry entry)
            _ = _shell.ActiveTab?.OpenAsync(entry);
    }

    /// <summary>
    /// The narrow set of keys that must be claimed before anything else sees
    /// them. Deliberately tiny: a tunnel handler runs ahead of every control in
    /// the window, so anything added here is taken away from all of them.
    /// </summary>
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None) return;

        // Only while the path box is open and focused. Tab keeps its ordinary
        // meaning everywhere else, including the other text boxes.
        if (_shell.ActiveTab is not { IsPathEditing: true } pane) return;
        if (FocusManager?.GetFocusedElement() is not TextBox) return;

        pane.CompletePathCommand.Execute(null);
        e.Handled = true;
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
        // Dolphin. Only when split, so it keeps its normal meaning otherwise —
        // and never while typing, or it would jump panes mid-edit.
        if (e.Key == Key.Tab && _shell.IsSplit && e.KeyModifiers == KeyModifiers.None
            && AppSettings.Current.General.TabSwitchesSplitPanes
            && FocusManager?.GetFocusedElement() is not TextBox)
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


            // Delete trashes, which is recoverable. Shift+Delete is
            // irreversible. Both prompts are now preferences, but they default
            // the way they always behaved: trash silently, confirm the
            // permanent one.
            case Key.Delete when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;

                if (AppSettings.Current.General.ConfirmPermanentDelete)
                    AskConfirmDelete();
                else
                    pane.DeleteSelectedCommand.Execute(null);

                break;

            case Key.Delete:
                e.Handled = true;

                if (AppSettings.Current.General.ConfirmMoveToTrash)
                    AskConfirmTrash();
                else
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
