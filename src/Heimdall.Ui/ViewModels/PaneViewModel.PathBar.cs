using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Heimdall.Core;
using Heimdall.Core.FileSystem;

namespace Heimdall.Ui.ViewModels;

/// <summary>
/// The path bar: the clickable ancestors, and the text field it becomes when
/// you type into it.
/// </summary>
public sealed partial class PaneViewModel
{
    // ---- breadcrumbs ---------------------------------------------------

    /// <summary>
    /// The path as clickable ancestors, Dolphin-style. Navigating two levels up
    /// is one click rather than two, and the shape of the location is readable
    /// without parsing a string.
    /// </summary>
    public ObservableCollection<PathSegment> Breadcrumbs { get; } = new();

    /// <summary>
    /// Extends the typed path to the next matching folder. Bound to Tab while
    /// the path box is open.
    /// </summary>
    [RelayCommand]
    public void CompletePath()
    {
        if (!IsPathEditing) return;

        if (_completer.Complete(PathText ?? "") is not { } completed)
        {
            Status = "no matching folder";
            return;
        }

        // Set through the field so OnPathTextChanged does not treat our own
        // write as the user typing and reset the cycle.
        _completingPath = true;
        try { PathText = completed; }
        finally { _completingPath = false; }
    }

    partial void OnPathTextChanged(string value)
    {
        // Typing invalidates the candidate list; completing does not.
        if (!_completingPath) _completer.Reset();
    }

    [RelayCommand]
    public void BeginEditPath()
    {
        _completer.Reset();

        PathText = CurrentPath;
        IsPathEditing = true;
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        if (string.IsNullOrEmpty(CurrentPath)) return;

        // A recent listing has no hierarchy to walk up, so it gets one crumb
        // naming itself. Splitting it on '/' would produce "heimdall:recent"
        // and "files" as if they were folders.
        if (VirtualPaths.IsVirtual(CurrentPath))
        {
            Breadcrumbs.Add(new PathSegment(
                VirtualPaths.Label(CurrentPath), CurrentPath,
                new RelayCommand(() => { }), true));
            return;
        }

        // Ancestors already answers this on both platforms — it starts at the
        // root, "/" or "C:\", and walks down to the path itself.
        //
        // It replaces a split on '/' that prefixed a hardcoded "/" crumb. On
        // Windows that produced "/ / C:\Users\flint": the split found no '/' to
        // break on, so the whole path stayed one unclickable crumb, behind a
        // root that does not exist there. Linux is unchanged — Ancestors("/x/y")
        // is ["/", "/x", "/x/y"], which is the same three crumbs as before.
        var levels = PathRules.Ancestors(CurrentPath);

        for (var i = 0; i < levels.Count; i++)
        {
            var target = levels[i];

            // LeafName, not the raw segment: it gives a root back as itself, so
            // the first crumb reads "/" or "C:\" rather than blank.
            Breadcrumbs.Add(new PathSegment(
                PathRules.LeafName(target), target,
                new RelayCommand(() => Detached(NavigateAsync(target), "navigate")),
                i == levels.Count - 1));
        }
    }

    /// <summary>
    /// Enter in the path box. A command rather than a code-behind KeyDown
    /// handler because there is now one path box per split side, and named
    /// controls inside a template cannot be reached from code-behind.
    /// </summary>
    [RelayCommand]
    public Task NavigateToPathText()
    {
        IsPathEditing = false;
        return string.IsNullOrWhiteSpace(PathText) ? Task.CompletedTask : NavigateAsync(PathText.Trim());
    }

    /// <summary>
    /// Escape, or clicking away: put back what is actually being shown.
    ///
    /// Guarded, because it is now reachable from lost-focus as well as Escape.
    /// NavigateToPathText clears IsPathEditing before it reads PathText, so an
    /// unguarded revert would fire in that gap and overwrite the path the user
    /// just typed — Enter would appear to navigate nowhere.
    /// </summary>
    [RelayCommand]
    public void RevertPathText()
    {
        if (!IsPathEditing) return;

        PathText = CurrentPath;
        IsPathEditing = false;
    }
}
