using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Heimdall.Core.FileSystem;

namespace Heimdall.Ui.ViewModels;

/// <summary>A single access flag, made two-way bindable for a checkbox.</summary>
public sealed partial class AccessToggleViewModel : ObservableObject
{
    private readonly AccessToggle _model;

    public AccessToggleViewModel(AccessToggle model)
    {
        _model = model;
        _value = model.Value;
    }

    public string Key => _model.Key;
    public string Group => _model.Group;
    public string Label => _model.Label;

    [ObservableProperty] private bool _value;

    public AccessToggle ToModel() => _model with { Value = Value };
}

public sealed partial class PropertiesViewModel : ObservableObject
{
    private readonly IPropertiesProvider _provider;
    private readonly IReadOnlyList<string> _paths;
    private CancellationTokenSource? _measureCts;

    private readonly IAccessEditor? _access;

    public PropertiesViewModel(
        IPropertiesProvider provider, IReadOnlyList<string> paths, IAccessEditor? access = null)
    {
        _provider = provider;
        _paths = paths;
        _access = access;
    }

    // ---- permissions ---------------------------------------------------

    public ObservableCollection<AccessToggleViewModel> Access { get; } = new();

    [ObservableProperty] private bool _canEditAccess;
    [ObservableProperty] private bool _canRecurse;
    [ObservableProperty] private bool _applyRecursively;
    [ObservableProperty] private string _accessSummary = "";
    [ObservableProperty] private string _accessStatus = "";

    private async Task LoadAccessAsync(string path, bool isDirectory)
    {
        if (_access is not { CanEdit: true }) return;

        var state = await _access.GetAccessAsync(path, CancellationToken.None).ConfigureAwait(false);
        if (state is null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Access.Clear();
            foreach (var toggle in state.Toggles) Access.Add(new AccessToggleViewModel(toggle));

            AccessSummary = state.Summary;
            CanEditAccess = true;
            CanRecurse = isDirectory;
        });
    }

    [RelayCommand]
    private async Task ApplyAccessAsync()
    {
        if (_access is null || _paths.Count == 0) return;

        AccessStatus = "applying…";

        var toggles = Access.Select(a => a.ToModel()).ToList();
        var progress = new Progress<int>(done => AccessStatus = $"{done:N0} entries…");

        try
        {
            foreach (var path in _paths)
            {
                await _access.SetAccessAsync(
                    path, toggles, ApplyRecursively, progress, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Read back rather than trusting what we sent — the filesystem may
            // have refused part of it, and showing the request as if it were
            // the result would be a lie.
            await LoadAccessAsync(_paths[0], CanRecurse).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => AccessStatus = "applied");
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => AccessStatus = ex.Message);
        }
    }

    public ObservableCollection<PropertyGroup> Groups { get; } = new();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _location = "";
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _canMeasure;
    [ObservableProperty] private bool _isMeasuring;

    public async Task LoadAsync()
    {
        if (_paths.Count == 0) return;

        if (_paths.Count > 1)
        {
            await LoadManyAsync().ConfigureAwait(false);
            return;
        }

        var details = await _provider.GetAsync(_paths[0], CancellationToken.None)
                                     .ConfigureAwait(false);

        await LoadAccessAsync(_paths[0], details.IsDirectory).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Title = details.Name;
            Location = Path.GetDirectoryName(details.FullPath) ?? details.FullPath;
            Kind = details.Kind;
            SizeText = details.IsDirectory ? "not measured" : Human(details.Size);
            CanMeasure = details.IsDirectory;

            var general = new List<PropertyRow>();

            if (details.SymlinkTarget is { } target)
                general.Add(new PropertyRow("symlink to", target));

            if (details.Modified is { } modified)
                general.Add(new PropertyRow("modified", modified.ToString("yyyy-MM-dd HH:mm:ss")));
            if (details.Accessed is { } accessed)
                general.Add(new PropertyRow("accessed", accessed.ToString("yyyy-MM-dd HH:mm:ss")));
            if (details.Created is { } created)
                general.Add(new PropertyRow("created", created.ToString("yyyy-MM-dd HH:mm:ss")));

            Groups.Clear();
            if (general.Count > 0) Groups.Add(new PropertyGroup("general", general));
            foreach (var group in details.Groups) Groups.Add(group);
        });
    }

    private async Task LoadManyAsync()
    {
        long total = 0;
        var files = 0;
        var folders = 0;

        foreach (var path in _paths)
        {
            if (Directory.Exists(path)) folders++;
            else if (File.Exists(path)) { files++; total += new FileInfo(path).Length; }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Title = $"{_paths.Count} items";
            Location = Path.GetDirectoryName(_paths[0]) ?? "";
            Kind = "multiple selection";

            // Folders are counted but not walked — measuring is opt-in here too.
            SizeText = folders > 0
                ? $"{Human(total)} in {files} file(s), plus {folders} folder(s) unmeasured"
                : $"{Human(total)} in {files} file(s)";

            CanMeasure = folders > 0;
        });
    }

    [RelayCommand]
    private async Task MeasureAsync()
    {
        if (IsMeasuring)
        {
            _measureCts?.Cancel();
            return;
        }

        _measureCts?.Dispose();
        _measureCts = new CancellationTokenSource();
        var ct = _measureCts.Token;

        IsMeasuring = true;

        var progress = new Progress<SizeProgress>(p =>
            SizeText = $"{Human(p.Bytes)} · {p.Files:N0} files · {p.Folders:N0} folders…");

        try
        {
            long bytes = 0;
            var files = 0;
            var folders = 0;

            foreach (var path in _paths.Where(Directory.Exists))
            {
                var result = await _provider.MeasureAsync(path, progress, ct).ConfigureAwait(false);
                bytes += result.Bytes;
                files += result.Files;
                folders += result.Folders;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                SizeText = $"{Human(bytes)} · {files:N0} files · {folders:N0} folders");
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SizeText += " (cancelled)");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsMeasuring = false);
        }
    }

    private static string Human(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
