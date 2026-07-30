using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Heimdall.Core;
using Heimdall.Core.FileSystem;
using Heimdall.Core.Sharing;
using Heimdall.Core.Places;
using Heimdall.Core.Search;
using Heimdall.Core.Session;

namespace Heimdall.Ui.ViewModels;

/// <summary>
/// Owns one or two pane groups. Deliberately thin — it decides which side is
/// active and nothing else; all the behaviour lives in PaneViewModel, which is
/// what made split view an addition rather than a rewrite.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly IFileSystemProvider _fs;
    private readonly IFileOperations? _ops;
    private readonly IApplicationLauncher? _launcher;
    private readonly IClipboardService? _clipboard;
    private readonly IScriptRunner? _scripts;
    private readonly ITagStore? _tags;
    private readonly ITemplateProvider? _templates;
    private readonly IFileSharing? _sharing;
    private readonly ISessionStore? _store;
    private bool _restoring;
    private bool _started;

    /// <summary>
    /// The right side as it was when last closed. Reopening restores it, so
    /// toggling the split off is not a way to silently lose where you were.
    /// </summary>
    private PaneState? _rememberedRight;

    public ShellViewModel(
        IFileSystemProvider fs,
        IFileOperations? ops = null,
        ISessionStore? store = null,
        IPlacesProvider? places = null,
        IApplicationLauncher? launcher = null,
        IClipboardService? clipboard = null,
        ISearchProvider? search = null,
        IScriptRunner? scripts = null,
        ITagStore? tags = null,
        ITemplateProvider? templates = null,
        IFileSharing? sharing = null)
    {
        _sharing = sharing;

        if (sharing is not null)
        {
            sharing.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                RefreshShares();

                // Discovery re-runs after an install, so availability can change
                // while the app is open.
                OnPropertyChanged(nameof(CanShare));
                OnPropertyChanged(nameof(CanInstallSharing));
            });
        }

        _scripts = scripts;
        _tags = tags;
        _templates = templates;
        _fs = fs;
        _ops = ops;
        _store = store;
        _launcher = launcher;
        _clipboard = clipboard;

        Sidebar = new SidebarViewModel(places, search, () => ActiveTab?.CurrentPath);

        // A chosen result navigates the active tab to its folder and selects it,
        // rather than opening the file — search is for finding, not launching.
        // A tag click narrows whichever pane has focus.
        // Same arrangement as tags: the store holds the data, the shell decides
        // what a click does. Attached here rather than in MainWindow because
        // this is the only place that knows which pane is active.
        Sidebar.AttachNavigation(path => _ = ActiveTab?.NavigateAsync(path));

        Sidebar.AttachTags(tags, tag =>
        {
            if (ActiveTab is { } pane) _ = pane.FilterByTagAsync(tag);
        });

        Sidebar.Search.ResultChosen += (_, entry) =>
        {
            var folder = entry.IsDirectory
                ? entry.FullPath
                : Path.GetDirectoryName(entry.FullPath);

            if (folder is null || ActiveTab is not { } pane) return;

            _ = pane.NavigateAsync(folder).ContinueWith(
                _ => Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => pane.SelectedEntry = entry),
                TaskScheduler.Default);
        };

        Left = CreateGroup();
        ActiveGroup = Left;

        Sidebar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SidebarViewModel.ActivePanel)
                               or nameof(SidebarViewModel.Rail)
                               or nameof(SidebarViewModel.Width))
                MarkDirty();
        };
    }

    public SidebarViewModel Sidebar { get; }

    public PaneGroupViewModel Left { get; private set; }

    /// <summary>Null unless split. The XAML binds its column's visibility to this.</summary>
    [ObservableProperty] private PaneGroupViewModel? _right;

    [ObservableProperty] private PaneGroupViewModel _activeGroup = null!;

    [ObservableProperty] private double _splitRatio = 0.5;

    /// <summary>
    /// Multiplies the whole type scale and every metric derived from it. Exists
    /// as a user control rather than a constant because "the text is too small"
    /// is an accessibility problem, and the right size depends on the display
    /// and the person, not on a value picked at build time.
    /// </summary>
    [ObservableProperty] private double _fontScale = 1.0;
    [ObservableProperty] private double _iconScale = 1.0;

    /// <summary>Set by the view so scale changes can re-write the resources.</summary>
    public Action<double, double>? ScaleApplier { get; set; }

    partial void OnFontScaleChanged(double value) => ApplyScales();
    partial void OnIconScaleChanged(double value) => ApplyScales();

    private void ApplyScales()
    {
        // Application-level defaults only: the sidebar, status bar and
        // properties window. Panes carry their own scale and set their own
        // TextScale, so nothing here reaches into them.
        ScaleApplier?.Invoke(FontScale, IconScale);
        MarkDirty();
    }

    private static double Step(double value, double delta)
        => Math.Round(Math.Clamp(value + delta, 0.7, 2.5), 2);

    /// <summary>
    /// Scaling applies to ONE pane — whichever is active, or whichever the
    /// pointer is over for the wheel. A reference listing beside a working one
    /// wants different sizes, which is the point of having two.
    /// </summary>
    public void ScalePane(PaneViewModel? pane, double fontDelta, double iconDelta)
    {
        if (pane is null) return;

        if (fontDelta != 0) pane.FontScale = Step(pane.FontScale, fontDelta);
        if (iconDelta != 0) pane.IconScale = Step(pane.IconScale, iconDelta);

        MarkDirty();
    }

    [RelayCommand] private void FontLarger()  => ScalePane(ActiveTab, 0.1, 0);
    [RelayCommand] private void FontSmaller() => ScalePane(ActiveTab, -0.1, 0);
    [RelayCommand] private void IconsLarger()  => ScalePane(ActiveTab, 0, 0.15);
    [RelayCommand] private void IconsSmaller() => ScalePane(ActiveTab, 0, -0.15);

    /// <summary>Ctrl+0 puts both back, since one control resetting only half of
    /// the sizing would be a puzzle rather than a reset.</summary>
    [RelayCommand]
    private void ZoomReset() => ResetPaneScale(ActiveTab);

    /// <summary>
    /// Back to default for one pane. Separate from the command so the wheel
    /// click can reset whichever pane the pointer is over, matching how
    /// Ctrl+wheel already targets by position rather than by focus.
    /// </summary>
    public void ResetPaneScale(PaneViewModel? pane)
    {
        if (pane is null) return;

        pane.FontScale = 1.0;
        pane.IconScale = 1.0;
        MarkDirty();
    }

    /// <summary>
    /// Combined zoom moves BOTH axes — it was stepping only the font, which
    /// made it identical to FontLarger and meant icons never grew with it.
    /// Icons step further per notch because their range is wider.
    /// </summary>
    [RelayCommand] private void ZoomIn()  => ScalePane(ActiveTab, 0.1, 0.15);
    [RelayCommand] private void ZoomOut() => ScalePane(ActiveTab, -0.1, -0.15);

    // ---- network sharing -------------------------------------------------

    public ObservableCollection<ShareSession> Shares { get; } = new();

    public bool HasShares => Shares.Count > 0;

    public bool CanShare => _sharing?.IsAvailable == true;

    /// <summary>A backend exists for this platform, but is not installed yet.</summary>
    public bool CanInstallSharing => _sharing is { IsAvailable: false } && !IsInstalling;

    [ObservableProperty] private bool _isInstalling;

    partial void OnIsInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallSharing));
    }

    /// <summary>
    /// Installs the sharing backend on request. Not automatic on first share:
    /// putting software on someone's machine should be something they chose,
    /// and a half-finished install in the middle of sharing a folder is a
    /// confusing place to discover a network problem.
    /// </summary>
    [RelayCommand]
    private async Task InstallSharingAsync()
    {
        if (_sharing is null || _sharing.IsAvailable || IsInstalling) return;

        IsInstalling = true;

        var pane = ActiveTab;
        var progress = new Progress<string>(line =>
        {
            if (pane is not null) pane.Status = line;
        });

        try
        {
            await _sharing.InstallAsync(progress, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (pane is not null) pane.Status = $"install failed: {ex.Message}";
        }
        finally
        {
            IsInstalling = false;
            OnPropertyChanged(nameof(CanShare));
            OnPropertyChanged(nameof(CanInstallSharing));
        }
    }

    private void RefreshShares()
    {
        Shares.Clear();
        foreach (var share in _sharing?.Active ?? []) Shares.Add(share);

        OnPropertyChanged(nameof(HasShares));
    }

    /// <summary>
    /// Serves the current folder read-only. Read-only is not a setting here on
    /// purpose — writable sharing is a separate, explicit command, because the
    /// difference is "people can look" versus "people can overwrite".
    /// </summary>
    [RelayCommand]
    private Task ShareFolderAsync() => ShareAsync(writable: false);

    [RelayCommand]
    private Task ShareFolderWritableAsync() => ShareAsync(writable: true);

    private async Task ShareAsync(bool writable)
    {
        if (ActiveTab is not { } pane) return;

        if (_sharing is not { IsAvailable: true })
        {
            pane.Status = _sharing?.UnavailableReason ?? "sharing is not available";
            return;
        }

        // The folder that was right-clicked, not the one being listed. Sharing
        // the parent when a subfolder was selected exposes every sibling too,
        // which is both surprising and a much larger surface than intended.
        var target = pane.SelectedEntry is { IsDirectory: true } selected
            ? selected.FullPath
            : pane.CurrentPath;

        try
        {
            await ShareFolderAsync(target, writable).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            pane.Status = $"could not share: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopShareAsync(ShareSession? session)
    {
        if (_sharing is null || session is null) return;

        await _sharing.StopAsync(session).ConfigureAwait(true);

        if (ActiveTab is { } pane) pane.Status = $"stopped sharing {session.Label}";
    }

    // ---- remote mounts ---------------------------------------------------

    private IRemoteMounts? _remotes;

    public void UseRemotes(IRemoteMounts? remotes)
    {
        _remotes = remotes;
        Sidebar.UseRemotes(remotes);
    }

    public bool CanConnect => _remotes?.IsAvailable == true;

    // ---- network discovery -----------------------------------------------

    private INetworkDiscovery? _discovery;

    public ObservableCollection<DiscoveredService> Discovered { get; } = new();

    public bool HasDiscovered => Discovered.Count > 0;

    [ObservableProperty] private bool _isBrowsing;

    public bool CanBrowseNetwork => _discovery?.IsAvailable == true && !IsBrowsing;

    partial void OnIsBrowsingChanged(bool value) => OnPropertyChanged(nameof(CanBrowseNetwork));

    public void UseDiscovery(INetworkDiscovery? discovery)
    {
        _discovery = discovery;
        OnPropertyChanged(nameof(CanBrowseNetwork));
    }

    /// <summary>
    /// Sweeps the network on demand rather than continuously — it costs a
    /// couple of seconds and multicast traffic, and nobody wants either
    /// happening in the background forever.
    /// </summary>
    [RelayCommand]
    private async Task BrowseNetworkAsync()
    {
        if (_discovery is null || IsBrowsing) return;

        var pane = ActiveTab;

        if (!_discovery.IsAvailable)
        {
            if (pane is not null) pane.Status = _discovery.UnavailableReason ?? "discovery unavailable";
            return;
        }

        IsBrowsing = true;
        if (pane is not null) pane.Status = "looking for servers on the network…";

        try
        {
            var found = await _discovery.BrowseAsync(CancellationToken.None).ConfigureAwait(true);

            Discovered.Clear();
            foreach (var service in found) Discovered.Add(service);

            OnPropertyChanged(nameof(HasDiscovered));

            if (pane is not null)
            {
                pane.Status = found.Count == 0
                    ? "no servers announced themselves"
                    : $"found {found.Count} server(s)";
            }
        }
        catch (Exception ex)
        {
            if (pane is not null) pane.Status = $"discovery failed: {ex.Message}";
        }
        finally
        {
            IsBrowsing = false;
        }
    }

    /// <summary>Asks the view to show connection details; the shell owns no windows.</summary>
    public event EventHandler<ConnectionInfoViewModel>? ConnectionInfoRequested;

    [RelayCommand]
    private async Task DisconnectRemoteAsync(RemoteMount? mount)
    {
        if (_remotes is null || mount is null) return;

        var pane = ActiveTab;

        try
        {
            var ok = await _remotes.UnmountAsync(mount, CancellationToken.None).ConfigureAwait(true);

            Sidebar.RefreshRemotes();

            if (pane is not null)
            {
                pane.Status = ok
                    ? $"disconnected {mount.Label}"
                    : $"could not disconnect {mount.Label} — something may still be using it";
            }
        }
        catch (Exception ex)
        {
            if (pane is not null) pane.Status = ex.Message;
        }
    }

    [RelayCommand]
    private void ShowRemoteInfo(RemoteMount? mount)
    {
        if (mount is null) return;

        var info = new ConnectionInfoViewModel(
            mount.Label,
            [
                new("Protocol", mount.Protocol),
                new("Status", mount.Reachable ? "connected" : "offline — the far end is not answering"),
                new("Local path", mount.Path),
            ],
            mount.Path,
            disconnect: () => DisconnectRemoteAsync(mount),
            copy: text => CopyTextRequested?.Invoke(this, text));

        ConnectionInfoRequested?.Invoke(this, info);
    }

    [RelayCommand]
    private void ShowServiceInfo(DiscoveredService? service)
    {
        if (service is null) return;

        var info = new ConnectionInfoViewModel(
            service.Name,
            [
                new("Service", service.Friendly),
                new("Announced as", service.ServiceType),
                new("Host", service.Host),
                new("Address", service.Address),
                new("Port", service.Port.ToString()),
                new("Connects as", service.MountUri),
            ],
            service.MountUri,

            // Nothing to disconnect: this has been seen, not mounted.
            disconnect: null,
            copy: text => CopyTextRequested?.Invoke(this, text));

        ConnectionInfoRequested?.Invoke(this, info);
    }

    [RelayCommand]
    private void CopyRemotePath(RemoteMount? mount)
    {
        if (mount is null) return;

        CopyTextRequested?.Invoke(this, mount.Path);
        if (ActiveTab is { } pane) pane.Status = $"copied {mount.Path}";
    }

    /// <summary>Mounts a discovered service and opens it.</summary>
    [RelayCommand]
    private async Task OpenDiscoveredAsync(DiscoveredService? service)
    {
        if (service is null) return;

        await ConnectToAsync(service.MountUri).ConfigureAwait(true);
    }

    /// <summary>Asks the view for a URI; the shell owns no dialogs.</summary>
    public event EventHandler? ConnectRequested;

    [RelayCommand]
    private void Connect() => ConnectRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Mounts a URI and navigates to wherever the desktop put it.
    /// </summary>
    public async Task ConnectToAsync(string uri)
    {
        if (_remotes is null || ActiveTab is not { } pane) return;

        uri = uri.Trim();
        if (uri.Length == 0) return;

        pane.Status = $"connecting to {uri}…";

        try
        {
            var mount = await _remotes.MountAsync(uri, CancellationToken.None).ConfigureAwait(true);

            Sidebar.RefreshRemotes();
            await pane.NavigateAsync(mount.Path).ConfigureAwait(true);

            pane.Status = $"connected to {mount.Label}";
        }
        catch (Exception ex)
        {
            pane.Status = ex.Message;
        }
    }

    /// <summary>Asks the view for the share dialog; the shell owns no windows.</summary>
    public event EventHandler<ShareRequestViewModel>? ShareDialogRequested;

    /// <summary>
    /// Sharing without a right-click: pick any folder, by typing or browsing.
    /// Starts at the folder currently open, which is usually the answer.
    /// </summary>
    [RelayCommand]
    private void RequestShare()
    {
        if (_sharing is null) return;

        if (!_sharing.IsAvailable)
        {
            if (ActiveTab is { } tab) tab.Status = _sharing.UnavailableReason ?? "sharing is not available";
            return;
        }

        var start = ActiveTab?.SelectedEntry is { IsDirectory: true } selected
            ? selected.FullPath
            : ActiveTab?.CurrentPath ?? "";

        var request = new ShareRequestViewModel(start, ShareFolderAsync);

        ShareDialogRequested?.Invoke(this, request);
    }

    /// <summary>Shared by the dialog and the context menu, so both behave alike.</summary>
    private async Task ShareFolderAsync(string path, bool writable)
    {
        if (_sharing is null) return;

        var session = await _sharing.StartAsync(path, writable, CancellationToken.None)
                                    .ConfigureAwait(true);

        if (ActiveTab is { } pane)
        {
            pane.Status = writable
                ? $"sharing {session.Label} read-write at {session.Url}"
                : $"sharing {session.Label} at {session.Url}";
        }
    }

    /// <summary>Nothing served should outlive the window that started it.</summary>
    public Task StopAllSharesAsync() => _sharing?.StopAllAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void CopyShareUrl(ShareSession? session)
    {
        if (session is null) return;

        CopyTextRequested?.Invoke(this, session.Url);

        if (ActiveTab is { } pane) pane.Status = $"copied {session.Url}";
    }

    /// <summary>The view owns the clipboard, so commands just ask.</summary>
    public event EventHandler<string>? CopyTextRequested;

    /// <summary>
    /// F11 toggles the panel on the side that has focus, so the shortcut and
    /// the per-side buttons do the same thing to the same place.
    /// </summary>
    [RelayCommand]
    private void ToggleInfo() => ActiveGroup?.ToggleInfoCommand.Execute(null);

    /// <summary>Hands the provider to both sides; each builds its own panel.</summary>
    public void UseProperties(IPropertiesProvider? properties)
    {
        Left?.UseProperties(properties);
        Right?.UseProperties(properties);
        _properties = properties;
    }

    private IPropertiesProvider? _properties;

    public bool IsSplit => Right is not null;

    // Hiding a control does not give its column back — an invisible pane in a
    // "*" column still reserves half the window. The definitions themselves
    // have to collapse, so they are driven from here.
    public GridLength LeftColumnWidth
        => new(IsSplit ? Math.Clamp(SplitRatio, 0.1, 0.9) : 1, GridUnitType.Star);

    public GridLength RightColumnWidth
        => IsSplit ? new GridLength(1 - Math.Clamp(SplitRatio, 0.1, 0.9), GridUnitType.Star)
                   : new GridLength(0);

    /// <summary>
    /// The fraction of the content width this group receives. 1 when not split.
    ///
    /// Clamped exactly as the column definitions clamp, so the answer matches
    /// what the layout will actually do rather than what SplitRatio says.
    /// </summary>
    private double ShareOf(object? group)
    {
        if (!IsSplit) return 1.0;

        var ratio = Math.Clamp(SplitRatio, 0.1, 0.9);

        return ReferenceEquals(group, Left) ? ratio : 1 - ratio;
    }

    private void NotifyColumns()
    {
        OnPropertyChanged(nameof(LeftColumnWidth));
        OnPropertyChanged(nameof(RightColumnWidth));
    }

    /// <summary>
    /// The active tab of the active side. Everything outside this class —
    /// toolbar, key bindings, context menu — binds through here and never needs
    /// to know whether the window is split.
    /// </summary>
    public PaneViewModel? ActiveTab => ActiveGroup?.ActiveTab;

    /// <summary>
    /// The status line, named with the folder it describes. In a split, a bare
    /// "21 items" does not say which of two identical listings it counted.
    /// </summary>
    /// <summary>Item and selection counts, separate from the transient status
    /// so a passing message never hides them.</summary>
    public string ActiveSummary => ActiveTab?.Summary ?? "";

    public string ActiveStatus => ActiveTab is { } pane && IsSplit
        ? $"{pane.Title} — {pane.Status}"
        : ActiveTab?.Status ?? "";

    /// <summary>The other side, when split. Where "copy to other pane" sends things.</summary>
    public PaneGroupViewModel? OtherGroup
        => Right is null ? null : ReferenceEquals(ActiveGroup, Left) ? Right : Left;

    public event EventHandler<PaneViewModel>? PaneCreated;

    /// <summary>The view owns window creation, so the command just asks.</summary>
    public event EventHandler? PropertiesRequested;

    public event EventHandler? BatchRenameRequested;

    [RelayCommand]
    private void BatchRename() => BatchRenameRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowProperties() => PropertiesRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Emptying the trash goes through the window, not straight to the store,
    /// because it needs the confirm bar — and the prompt lives in the window,
    /// which is the only thing that owns real buttons. Same arrangement as
    /// properties and settings.
    /// </summary>
    /// <summary>Widen the window by this many pixels, to make room for a panel
    /// that would not otherwise fit.</summary>
    public event EventHandler<double>? GrowRequested;

    public event EventHandler? EmptyTrashRequested;

    [RelayCommand]
    private void EmptyTrash() => EmptyTrashRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Status bar visibility, straight off the preferences. Re-raised rather
    /// than stored, so there is one source of truth and no copy to fall out of
    /// step with the file.
    /// </summary>
    public bool ShowStatusBar => Settings.AppSettings.Current.General.ShowStatusBar;

    public bool ShowFreeSpace => Settings.AppSettings.Current.General.ShowFreeSpace;

    // ---- tag maintenance ---------------------------------------------------

    /// <summary>
    /// Removes a tag from whatever is selected in the active pane. Reachable by
    /// right-clicking the tag in the sidebar, so the tag itself is the handle.
    /// </summary>
    [RelayCommand]
    private void RemoveTagFromSelection(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || ActiveTab is not { } pane) return;

        _ = pane.RemoveTagAsync(tag);
    }

    /// <summary>
    /// Stops offering a tag. Files keep it — see ITagStore.ForgetKnown for why
    /// this is not "delete everywhere".
    /// </summary>
    [RelayCommand]
    private void ForgetTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;

        _tags?.ForgetKnown(tag);
    }

    // ---- context menu visibility ------------------------------------------
    //
    // Straight off the preferences, like the status bar above. Bound with
    // IsVisible on the MenuItems, which is how Dolphin's Services page works:
    // the commands all still exist and keep their shortcuts, the menu just
    // stops listing them.

    private static Core.Settings.ContextMenuSettings Menu => Settings.AppSettings.Current.ContextMenu;

    public bool ShowCopyToInMenu => Menu.ShowCopyTo;
    public bool ShowMoveToInMenu => Menu.ShowMoveTo;
    public bool ShowSortByInMenu => Menu.ShowSortBy;
    public bool ShowDuplicateInMenu => Menu.ShowDuplicate;
    public bool ShowOpenInNewTabInMenu => Menu.ShowOpenInNewTab;
    public bool ShowAddToPlacesInMenu => Menu.ShowAddToPlaces;
    public bool ShowCopyLocationInMenu => Menu.ShowCopyLocation;

    /// <summary>
    /// The selection's path on the clipboard. Reuses CopyTextRequested, which
    /// already exists for share URLs and mount paths — the view owns the
    /// clipboard, so the shell asks rather than reaches.
    /// </summary>
    [RelayCommand]
    private void CopyLocation()
    {
        var path = ActiveTab?.SelectedEntry?.FullPath ?? ActiveTab?.CurrentPath;

        if (!string.IsNullOrEmpty(path)) CopyTextRequested?.Invoke(this, path);
    }

    /// <summary>
    /// Pins the selected folder rather than the current one, which is what a
    /// context menu on a row should mean. Falls back to the current folder when
    /// the click was on empty space.
    /// </summary>
    [RelayCommand]
    private void AddSelectionToPlaces()
    {
        var path = ActiveTab?.SelectedEntry is { IsDirectory: true } entry
            ? entry.FullPath
            : ActiveTab?.CurrentPath;

        if (path is { Length: > 0 }) _ = Sidebar.PinAsync(path);
    }

    /// <summary>
    /// Called when preferences change. Most settings are read at the moment
    /// they matter and so need nothing; sorting is the exception, because a
    /// listing already on screen was ordered under the old rule.
    /// </summary>
    /// <summary>
    /// Keeps the sidebar's highlight on the place the active pane is showing.
    /// The shell is the only thing that knows which pane that is, which is the
    /// same reason it owns the navigation callback.
    /// </summary>
    public void SyncSidebarLocation() => Sidebar.SetCurrentPath(ActiveTab?.CurrentPath);

    public void OnSettingsChanged()
    {
        // The tile and cell metrics are computed from the pane's scale AND the
        // global spacing settings, but only the scale raises a notification.
        // Without this, a spacing change would reach only the panes that
        // happened to rescale afterwards — which is the trap the old
        // application-level filter was trying to avoid, solved at the right end.
        foreach (var group in new[] { Left, Right })
            if (group is not null)
                foreach (var tab in group.Tabs)
                {
                    tab.RefreshScale();
                    tab.RefreshDecorations();
                }

        // The narrow-panel behaviour changes whether the toggle may be pressed,
        // and that is computed rather than stored — so it has to be re-raised or
        // a greyed button stays greyed until the next resize.
        foreach (var group in new[] { Left, Right })
            group?.RefreshInfoFit();

        OnPropertyChanged(nameof(ShowStatusBar));
        OnPropertyChanged(nameof(ShowFreeSpace));

        OnPropertyChanged(nameof(ShowCopyToInMenu));
        OnPropertyChanged(nameof(ShowMoveToInMenu));
        OnPropertyChanged(nameof(ShowSortByInMenu));
        OnPropertyChanged(nameof(ShowDuplicateInMenu));
        OnPropertyChanged(nameof(ShowOpenInNewTabInMenu));
        OnPropertyChanged(nameof(ShowAddToPlacesInMenu));
        OnPropertyChanged(nameof(ShowCopyLocationInMenu));

        // Left and Right, not a Groups collection — this view model has no such
        // thing, and inventing one for a loop would be the tail wagging the dog.
        foreach (var group in new[] { Left, Right })
        {
            if (group is null) continue;

            foreach (var tab in group.Tabs)
                tab.RefreshCommand.Execute(null);
        }
    }

    public event EventHandler? SettingsRequested;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    public Func<WindowSession>? GeometryProvider { get; set; }

    [ObservableProperty] private string _operationStatus = "";
    [ObservableProperty] private IOperationHandle? _activeOperation;

    // ---- construction --------------------------------------------------

    private PaneGroupViewModel CreateGroup()
    {
        var group = new PaneGroupViewModel(NewPane);

        group.LocationChanged += (_, _) => SyncSidebarLocation();

        // Forwarded rather than handled: only the window can change its own
        // width, and the group has no business knowing a window exists.
        //
        // But the ARITHMETIC belongs here, because only the shell knows the
        // window is split. The columns are STAR lengths driven by SplitRatio, so
        // growing the window by the group's shortfall hands that side only its
        // SHARE of the extra — which is why the window grew and the panel still
        // did not appear. Dividing by the share makes one resize enough.
        group.GrowRequested += (sender, needed) =>
            GrowRequested?.Invoke(this, needed / ShareOf(sender));

        // A split created later must get the provider too, or its panel would
        // silently have nothing to show.
        group.UseProperties(_properties);
        group.PropertyChanged += OnGroupChanged;
        return group;
    }

    private PaneViewModel NewPane()
    {
        // A new tab inherits the sizes of the one it was opened from, rather
        // than snapping back to default mid-session.
        var pane = new PaneViewModel(_fs, _ops, _launcher, _clipboard, _scripts, _tags, _templates)
        {
            FontScale = ActiveTab?.FontScale ?? FontScale,
            IconScale = ActiveTab?.IconScale ?? IconScale,
        };

        pane.ScaleChanged += (_, _) => MarkDirty();
        pane.OperationStarted += OnOperationStarted;
        pane.PropertyChanged += OnPaneChanged;
        PaneCreated?.Invoke(this, pane);
        return pane;
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PaneGroupViewModel.ActiveTab)) return;

        if (ReferenceEquals(sender, ActiveGroup))
        {
            OnPropertyChanged(nameof(ActiveTab));
            OnPropertyChanged(nameof(ActiveStatus));
            OnPropertyChanged(nameof(ActiveSummary));

        }

        MarkDirty();
    }

    partial void OnActiveGroupChanged(PaneGroupViewModel? oldValue, PaneGroupViewModel newValue)
    {
        if (oldValue is not null) oldValue.IsActiveGroup = false;
        newValue.IsActiveGroup = true;

        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(ActiveStatus));
        OnPropertyChanged(nameof(OtherGroup));


        MarkDirty();
    }

    partial void OnRightChanged(PaneGroupViewModel? value)
    {
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(OtherGroup));
        NotifyColumns();
        MarkDirty();
    }

    partial void OnSplitRatioChanged(double value)
    {
        NotifyColumns();
        MarkDirty();
    }

    // ---- split ---------------------------------------------------------

    /// <summary>
    /// F3, matching Dolphin. Opening a split clones the current location so the
    /// second side starts somewhere useful rather than at home.
    /// </summary>
    [RelayCommand]
    public void ToggleSplit()
    {
        if (Right is null)
        {
            var right = CreateGroup();

            // Populated before being assigned, so the column never flashes empty.
            if (_rememberedRight is { Tabs.Count: > 0 } remembered)
                Restore(right, remembered);
            else
                right.AddTab(ActiveTab?.CurrentPath
                             ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            Right = right;
            ActiveGroup = right;
        }
        else
        {
            // Closing the split always keeps the left side, so which half
            // survives is predictable rather than depending on focus.
            var closing = Right;

            // Heimdall's default keeps what the closing side was showing, so
            // reopening the split lands back in place — closing a split should
            // not be a quiet way to lose a location. Dolphin discards it, and
            // people used to that can have it.
            _rememberedRight = Settings.AppSettings.Current.General.ClosingSplitDiscardsOtherPane
                ? null
                : closing.ToPaneState();

            Right = null;
            ActiveGroup = Left;
            closing.DisposeAll();
        }
    }

    /// <summary>Tab, matching Dolphin.</summary>
    [RelayCommand]
    public void FocusOtherPane()
    {
        if (OtherGroup is { } other) ActiveGroup = other;
    }

    [RelayCommand]
    public void CopyToOtherPane() => TransferToOther(move: false);

    [RelayCommand]
    public void MoveToOtherPane() => TransferToOther(move: true);

    /// <summary>
    /// Somewhere to send files that is not the other pane. Built from the same
    /// Places the sidebar shows, so the destinations offered are the ones the
    /// user already keeps — no separate list to maintain, and pinning a folder
    /// makes it a transfer target for free.
    /// </summary>
    public IReadOnlyList<PlaceItemViewModel> TransferTargets =>
        Sidebar.Groups
            .SelectMany(g => g.Places)

            // An unmounted volume or unreachable share would look like a valid
            // destination and fail on use.
            .Where(p => p.IsAvailable && !string.IsNullOrEmpty(p.Path))

            // Sending a folder into itself is the one destination that is never
            // meaningful.
            .Where(p => !string.Equals(p.Path.TrimEnd('/'),
                                       ActiveTab?.CurrentPath.TrimEnd('/'),
                                       StringComparison.Ordinal))
            .ToList();

    private void NotifyTransferTargets() => OnPropertyChanged(nameof(TransferTargets));

    [RelayCommand]
    private void CopySelectionTo(PlaceItemViewModel? place) => TransferTo(place, move: false);

    [RelayCommand]
    private void MoveSelectionTo(PlaceItemViewModel? place) => TransferTo(place, move: true);

    private void TransferTo(PlaceItemViewModel? place, bool move)
    {
        if (place is null || ActiveTab is not { } source) return;

        var paths = SelectionOf(source);
        if (paths.Count == 0) { source.Status = "nothing selected"; return; }

        if (!Directory.Exists(place.Path))
        {
            source.Status = $"{place.Label} is not reachable";
            return;
        }

        // Routed through a pane already showing the destination when there is
        // one, so its listing refreshes itself; otherwise through the same
        // helper, which keeps the conflict policy in exactly one place.
        var open = new[] { Left, Right }
            .Where(g => g is not null)
            .SelectMany(g => g!.Tabs)
            .FirstOrDefault(t => string.Equals(t.CurrentPath.TrimEnd('/'),
                                               place.Path.TrimEnd('/'),
                                               StringComparison.Ordinal));

        if (open is not null) open.PasteInto(paths, move);
        else source.PasteIntoFolder(place.Path, paths, move);

        source.Status = move
            ? $"moving {paths.Count} item(s) to {place.Label}"
            : $"copying {paths.Count} item(s) to {place.Label}";
    }

    private static List<string> SelectionOf(PaneViewModel pane)
        => pane.SelectionPaths().ToList();

    private void TransferToOther(bool move)
    {
        if (_ops is null || OtherGroup?.ActiveTab is not { } target) return;
        if (ActiveTab is not { } source) return;

        var paths = SelectionOf(source);
        if (paths.Count == 0) return;

        target.PasteInto(paths, move);
    }

    // ---- tabs ----------------------------------------------------------

    [RelayCommand]
    private void NewTab()
        => ActiveGroup.AddTab(ActiveTab?.CurrentPath
                              ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [RelayCommand]
    private void OpenInNewTab(FileEntry entry)
    {
        if (entry.IsDirectory) ActiveGroup.AddTab(entry.FullPath);
    }

    /// <summary>
    /// Opens a folder by path — used when the desktop hands one over, either on
    /// the command line or from a later launch forwarded to this instance.
    ///
    /// Reuses the current tab when it is already showing that folder, so
    /// repeatedly opening the same place from elsewhere does not stack up
    /// identical tabs.
    /// </summary>
    public void OpenInNewTab(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var existing = ActiveGroup.Tabs.FirstOrDefault(
            t => string.Equals(t.CurrentPath, path, StringComparison.Ordinal));

        if (existing is not null)
        {
            ActiveGroup.ActiveTab = existing;
            return;
        }

        ActiveGroup.AddTab(path);
    }

    [RelayCommand]
    private void CloseTab(PaneViewModel? pane)
    {
        // Closing the last tab of the right side collapses the split rather
        // than refusing, which is what the user actually means.
        if (Right is not null && ActiveGroup.Tabs.Count <= 1 &&
            (pane is null || ActiveGroup.Tabs.Contains(pane)))
        {
            if (ReferenceEquals(ActiveGroup, Right)) { ToggleSplit(); return; }

            // Closing the last left tab: promote the right side to be the only one.
            var survivor = Right;
            Left.DisposeAll();
            Left = survivor;
            Right = null;
            ActiveGroup = Left;
            OnPropertyChanged(nameof(Left));
            return;
        }

        ActiveGroup.CloseTab(pane);
    }

    [RelayCommand] private void SelectTab(PaneViewModel? pane) { if (pane is not null) ActiveGroup.ActiveTab = pane; }
    [RelayCommand] private void NextTab() => ActiveGroup.Cycle(1);
    [RelayCommand] private void PreviousTab() => ActiveGroup.Cycle(-1);
    [RelayCommand] private void CancelOperation() => ActiveOperation?.Cancel();

    public void SelectTabByIndex(int index) => ActiveGroup.SelectTabByIndex(index);

    public void ActivateGroup(PaneGroupViewModel group)
    {
        if (!ReferenceEquals(ActiveGroup, group)) ActiveGroup = group;
    }

    // ---- places --------------------------------------------------------

    [RelayCommand]
    private void GoToPlace(string? path)
    {
        if (!string.IsNullOrEmpty(path)) _ = ActiveTab?.NavigateAsync(path);
    }

    [RelayCommand]
    private void PinCurrent()
    {
        if (ActiveTab?.CurrentPath is { Length: > 0 } path) _ = Sidebar.PinAsync(path);
    }

    // ---- operations ----------------------------------------------------

    private void OnOperationStarted(object? sender, IOperationHandle handle)
    {
        ActiveOperation = handle;

        handle.Progressed += (_, p) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                OperationStatus = p.ItemsTotal <= 1 && p.BytesTotal == 0
                    ? p.CurrentItem ?? ""
                    : $"{p.ItemsDone}/{p.ItemsTotal}  {ByteSize.Format(p.BytesDone)}/{ByteSize.Format(p.BytesTotal)}  {p.CurrentItem}");

        _ = handle.Completion.ContinueWith(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // A failure stays on screen; only success clears silently.
                if (handle.State == OperationState.Failed && handle.Error is { } error)
                {
                    OperationStatus = $"failed: {error.Message}";
                }
                else
                {
                    OperationStatus = "";
                    ActiveOperation = null;
                }
            }), TaskScheduler.Default);
    }

    // ---- session -------------------------------------------------------

    /// <summary>
    /// <paramref name="state"/> null means "do not restore" — the caller has
    /// already applied the startup setting and decided the session should be
    /// ignored. <paramref name="openFolder"/> is where to start instead; null
    /// means home, which is what this always did.
    ///
    /// The decision lives in the caller rather than here because the caller is
    /// the only place that holds both stores, and because a view model that
    /// reaches for preferences to decide whether to use its own argument is
    /// harder to reason about than one that is simply told.
    /// </summary>
    public void Start(SessionState? state, string? openFolder = null)
    {
        if (_started) return;
        _started = true;

        var home = string.IsNullOrWhiteSpace(openFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : openFolder;
        var window = state?.Windows.FirstOrDefault();

        if (window is not null)
        {
            Sidebar.ActivePanel = window.ActiveSidebarPanel;
            Sidebar.Width = window.SidebarWidth;
            Sidebar.Rail = window.Rail;
            SplitRatio = window.SplitRatio;
            FontScale = window.FontScale <= 0 ? 1.0 : window.FontScale;
            IconScale = window.IconScale <= 0 ? 1.0 : window.IconScale;
        }

        _ = Sidebar.InitializeAsync();

        var panes = window?.Panes;

        _restoring = true;
        try
        {
            if (panes is null || panes.Count == 0 || panes[0].Tabs.Count == 0)
            {
                Left.AddTab(home);
            }
            else
            {
                Restore(Left, panes[0]);

                if (panes.Count > 1 && panes[1].Tabs.Count > 0)
                {
                    var right = CreateGroup();
                    Restore(right, panes[1]);
                    Right = right;
                }
                else
                {
                    // Split was closed at save time; keep what it was showing
                    // so reopening after a restart lands back in place.
                    _rememberedRight = window?.RememberedRightPane;
                }
            }

            var activeIndex = window?.ActivePaneIndex ?? 0;
            ActiveGroup = activeIndex == 1 && Right is not null ? Right : Left;
        }
        finally
        {
            _restoring = false;
        }

        // Assigned while suppressed, so it never triggered its own load.
        ActiveGroup.ActiveTab?.RefreshIfUnloaded();
    }

    private static void Restore(PaneGroupViewModel group, PaneState state)
    {
        group.RestoreFrom(state);

        foreach (var tab in state.Tabs) group.AddRestoredTab(tab);
        group.ActiveTab = group.Tabs[Math.Clamp(state.ActiveTabIndex, 0, group.Tabs.Count - 1)];
    }

    public SessionState ToSessionState()
    {
        var geometry = GeometryProvider?.Invoke() ?? new WindowSession();

        var panes = Right is null
            ? new List<PaneState> { Left.ToPaneState() }
            : [Left.ToPaneState(), Right.ToPaneState()];

        return new SessionState
        {
            Version = SessionState.CurrentVersion,
            Windows =
            [
                geometry with
                {
                    ActiveSidebarPanel = Sidebar.ActivePanel,
                    SidebarWidth = Sidebar.Width,
                    Rail = Sidebar.Rail,
                    SplitRatio = SplitRatio,
                    FontScale = FontScale,
                    IconScale = IconScale,
                    RememberedRightPane = Right is null ? _rememberedRight : null,
                    Panes = panes,
                    ActivePaneIndex = ReferenceEquals(ActiveGroup, Right) ? 1 : 0,
                }
            ],
        };
    }

    public void NotifyWindowChanged() => MarkDirty();

    private void MarkDirty()
    {
        // Nothing before Start() is worth saving, and property setters fire
        // during construction while Sidebar and the groups are still null.
        if (!_started || _restoring || _store is null) return;
        _store.NotifyChanged(ToSessionState());
    }

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.CurrentPath):
                // The destination list excludes the folder you are already in,
                // so it changes whenever the pane navigates.
                NotifyTransferTargets();
                MarkDirty();
                break;

            case nameof(PaneViewModel.Sort):
            case nameof(PaneViewModel.SortDescending):
            case nameof(PaneViewModel.ShowHidden):
                MarkDirty();
                break;

            case nameof(PaneViewModel.Status):
            case nameof(PaneViewModel.Title):
                OnPropertyChanged(nameof(ActiveStatus));
                break;


            case nameof(PaneViewModel.Summary):
                OnPropertyChanged(nameof(ActiveSummary));

                break;
        }
    }
}
