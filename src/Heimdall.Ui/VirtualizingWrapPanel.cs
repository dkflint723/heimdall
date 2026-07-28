using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Heimdall.Ui;

/// <summary>
/// A wrapping panel that realizes only the rows on screen.
///
/// This is the last real gap against Dolphin: `WrapPanel` realizes a container
/// for every item it is given, so the tile layouts refuse anything over
/// `UnvirtualizedLimit` (5,000) while Dolphin shows a 200,000-item folder in
/// icon view without complaint.
///
/// **Uniform tile size is the whole trick.** Every tile is exactly
/// <see cref="ItemWidth"/> by <see cref="ItemHeight"/>, so the row containing
/// item N is arithmetic rather than a measurement — nothing off screen has to be
/// measured, which is what makes wrapping virtualizable at all. Heimdall already
/// has those numbers: `PaneScale` publishes `TileWidth` and `TileHeight`.
///
/// Scrolling is driven by <see cref="Layoutable.EffectiveViewportChanged"/>
/// rather than by implementing `ILogicalScrollable`, which the base class
/// documentation offers as the simpler of the two routes.
///
/// The container lifecycle follows `ItemContainerGenerator`'s documented
/// protocol exactly: NeedsContainer, CreateContainer, PrepareItemContainer,
/// AddInternalChild, ItemContainerPrepared — and on the way out
/// ClearItemContainer then into a pool keyed by the recycle key.
/// **Recycled containers stay in the panel with IsVisible false** rather than
/// being removed. The generator's docs require it, and it is also what keeps the
/// attached-property row decoration attached.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemWidth), 100);

    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemHeight), 100);

    /// <summary>
    /// Gap around each tile, added to <see cref="ItemWidth"/> and
    /// <see cref="ItemHeight"/> to give the CELL size.
    ///
    /// Explicit because the grid's item template carries `Margin="3"`, so its
    /// desired size is six pixels larger than the tile in each direction.
    /// Arranging into a cell of exactly TileWidth would clip every tile by that
    /// margin — a quiet, uniform wrongness that is hard to spot and easy to
    /// misread as a styling problem.
    /// </summary>
    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemSpacing), 6);

    /// <summary>Remembers how each container may be pooled. An attached property
    /// rather than a dictionary, so it travels with the container.</summary>
    private static readonly AttachedProperty<object?> RecycleKeyProperty =
        AvaloniaProperty.RegisterAttached<VirtualizingWrapPanel, Control, object?>("RecycleKey");

    /// <summary>Marks an item that IS its own container. Those can never be
    /// recycled or cleared — the generator's docs are explicit about it.</summary>
    private static readonly object ItemIsItsOwnContainer = new();

    private readonly Dictionary<int, Control> _realized = [];
    private readonly Dictionary<object, Stack<Control>> _pool = [];

    private Rect _viewport;
    private int _columns = 1;

    /// <summary>
    /// The index of the most recent <see cref="ScrollIntoView"/> target, until
    /// the viewport catches up with it. TEMPORARY, and only read by the
    /// `recycle-scrolltarget` diagnostic — see the comment in
    /// <see cref="Recycle"/> for what it is testing for.
    /// </summary>
    private int _scrollTarget = -1;

    public VirtualizingWrapPanel()
    {
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>The footprint of one item, tile plus gap. Every layout decision
    /// uses this rather than the tile size.</summary>
    private double CellWidth => Math.Max(1, ItemWidth + ItemSpacing);

    private double CellHeight => Math.Max(1, ItemHeight + ItemSpacing);

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;

        // Which rows should exist is a measure concern: arrange cannot create
        // containers, only place ones that are already there.
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;

        if (items.Count == 0)
        {
            RecycleAll();
            return default;
        }

        var itemWidth = CellWidth;
        var itemHeight = CellHeight;

        _columns = Math.Max(1, (int)(availableSize.Width / itemWidth));

        var rows = (items.Count + _columns - 1) / _columns;
        var extent = new Size(_columns * itemWidth, rows * itemHeight);

        // On the first pass the viewport is still empty, so fall back to the
        // space offered. Without this nothing is realized and the panel reports
        // an extent it never fills.
        var top = _viewport.Height > 0 ? _viewport.Top : 0;
        var bottom = _viewport.Height > 0 ? _viewport.Bottom : availableSize.Height;

        // A row of slack either side, so scrolling does not expose a blank band
        // before the next measure catches up.
        var firstRow = Math.Max(0, (int)(top / itemHeight) - 1);
        var lastRow = Math.Min(rows - 1, (int)(bottom / itemHeight) + 1);

        var first = firstRow * _columns;
        var last = Math.Min(items.Count - 1, ((lastRow + 1) * _columns) - 1);

        // The viewport has caught up with the scroll target, so it is no longer
        // at risk from a stale-viewport measure.
        if (_scrollTarget >= first && _scrollTarget <= last) _scrollTarget = -1;

        Realize(first, last, items);

        var tile = new Size(itemWidth, itemHeight);

        foreach (var container in _realized.Values) container.Measure(tile);

        // GROUND TRUTH for "is this actually virtualizing".
        //
        // The tiles: timer posts at Background priority and stops when the
        // dispatcher drains, which need not coincide with realization
        // finishing — it read 35 ms and 356 ms for the same 5,000-item folder
        // on two runs. The realized COUNT cannot be ambiguous like that: if it
        // approaches the item count, nothing is being virtualized at all.
        if (items.Count > 1000)
        {
            Console.Error.WriteLine(
                $"[heimdall] wrap: items={items.Count:N0} realized={_realized.Count} "
                + $"range={first}..{last} cols={_columns} "
                + $"viewport={_viewport.Top:F0}..{_viewport.Bottom:F0} "
                + $"avail={availableSize.Width:F0}x{availableSize.Height:F0} "
                + $"extent={extent.Width:F0}x{extent.Height:F0}");
        }

        return extent;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = CellWidth;
        var itemHeight = CellHeight;

        foreach (var (index, container) in _realized)
        {
            var row = index / _columns;
            var column = index % _columns;

            container.Arrange(new Rect(
                column * itemWidth, row * itemHeight, itemWidth, itemHeight));
        }

        return finalSize;
    }

    /// <summary>
    /// Brings the realized set to exactly the given range. Anything outside goes
    /// back to the pool FIRST, so those containers are available to be reused
    /// rather than newly created.
    /// </summary>
    private void Realize(int first, int last, IReadOnlyList<object?> items)
    {
        foreach (var index in _realized.Keys.Where(i => i < first || i > last).ToList())
            Recycle(index);

        for (var index = first; index <= last; index++)
        {
            if (_realized.ContainsKey(index)) continue;

            if (GetOrCreate(items[index], index) is { } container)
                _realized[index] = container;
        }
    }

    private Control? GetOrCreate(object? item, int index)
    {
        var generator = ItemContainerGenerator;
        if (generator is null) return null;

        if (!generator.NeedsContainer(item, index, out var recycleKey))
        {
            // The item IS the container. It can never be recycled, so it is
            // prepared exactly once and afterwards merely shown again.
            if (item is not Control own) return null;

            if (own.GetValue(RecycleKeyProperty) != ItemIsItsOwnContainer)
            {
                own.SetValue(RecycleKeyProperty, ItemIsItsOwnContainer);
                generator.PrepareItemContainer(own, item, index);
                AddInternalChild(own);
                generator.ItemContainerPrepared(own, item, index);
            }

            own.IsVisible = true;
            return own;
        }

        if (recycleKey is not null
            && _pool.TryGetValue(recycleKey, out var pooled)
            && pooled.Count > 0)
        {
            var reused = pooled.Pop();

            reused.IsVisible = true;
            generator.PrepareItemContainer(reused, item, index);
            generator.ItemContainerPrepared(reused, item, index);

            return reused;
        }

        var created = generator.CreateContainer(item, index, recycleKey);

        created.SetValue(RecycleKeyProperty, recycleKey);

        generator.PrepareItemContainer(created, item, index);
        AddInternalChild(created);
        generator.ItemContainerPrepared(created, item, index);

        return created;
    }

    /// <summary>
    /// Returns a container to the pool. A focused container is KEPT unless
    /// <paramref name="force"/> — see the comment inside.
    /// </summary>
    private void Recycle(int index, bool force = false)
    {
        if (!_realized.TryGetValue(index, out var container)) return;

        // TEMPORARY — tests a gap the Avalonia decompile predicts but nothing
        // here has yet observed.
        //
        // VirtualizingStackPanel tracks TWO protected elements, not one:
        // `_focusedElement`/`_focusedIndex` AND `_scrollToElement`/
        // `_scrollToIndex`. Keeping the focused container (below) matches the
        // first. Nothing here matches the second, so a ScrollIntoView target
        // that does NOT take focus is still exposed to exactly the
        // stale-viewport recycle that broke keyboard navigation.
        //
        // The paths that scroll without focusing are the ones to watch:
        // AutoScrollToSelectedItem, which is how type-ahead moves the view, and
        // any plain BringIntoView. If this line fires, the scroll target needs
        // pinning the way Avalonia pins it. If it never fires, the focused-
        // container guard alone is sufficient here and no more machinery is
        // warranted.
        if (index == _scrollTarget && !container.IsFocused)
            Console.Error.WriteLine($"[heimdall] recycle-scrolltarget: index={index}");

        // KEEPING THE FOCUSED CONTAINER REALIZED IS REQUIRED, NOT AN
        // OPTIMISATION. Recycling hides the control and clears its item, and
        // focus dies with it — leaving the window with no focused element at
        // all, so every subsequent keystroke does nothing until the list is
        // clicked again.
        //
        // Measured 27 July 2026, and the ordering is the whole story:
        //
        //   nav-target: 99999          ScrollIntoView realized it and returned
        //                              it; the ListBox focused it
        //   recycle-focused: 99999     the very next measure recycled it
        //   wrap: range=0..47   viewport=-6..635        <- STALE viewport
        //   wrap: range=99952..99999 viewport=1631866.. <- real one, too late
        //   key: Home focus=null                       <- focus is gone
        //
        // BringIntoView only SCHEDULES the scroll, so the measure that
        // ScrollIntoView triggers still sees the old viewport and computes a
        // range that excludes the row it just realized. Two mechanisms fitted
        // that symptom — this one, and the scroll dropping focus by itself —
        // and `recycle-focused` was added to separate them. It fired, so the
        // scroll is not implicated.
        //
        // This also covers the ordinary case the nav bug happens to expose:
        // focus a tile, scroll it off screen, and focus should still be there.
        // The container is recycled by the next measure that finds it out of
        // range once focus has moved on, so nothing leaks.
        //
        // `IsFocused` rather than a focus-within test because the focused
        // element measured here IS the container (`focus=ListBoxItem`), and
        // nothing in the grid item template is focusable. Revisit if a
        // template ever gains a focusable child.
        if (!force && container.IsFocused)
        {
            Console.Error.WriteLine($"[heimdall] recycle-focused: index={index} kept=True");
            return;
        }

        _realized.Remove(index);

        // TEMPORARY, alongside nav-target. Prints only for the container that
        // holds focus, so it stays silent in normal scrolling. `kept=False`
        // means something forced the recycle past the guard above.
        if (container.IsFocused)
            Console.Error.WriteLine($"[heimdall] recycle-focused: index={index} kept=False");

        var recycleKey = container.GetValue(RecycleKeyProperty);

        // An item that is its own container may not be cleared or pooled, only
        // hidden.
        if (ReferenceEquals(recycleKey, ItemIsItsOwnContainer))
        {
            container.IsVisible = false;
            return;
        }

        ItemContainerGenerator?.ClearItemContainer(container);

        if (recycleKey is null)
        {
            RemoveInternalChild(container);
            return;
        }

        container.IsVisible = false;

        if (!_pool.TryGetValue(recycleKey, out var pooled))
            _pool[recycleKey] = pooled = new Stack<Control>();

        pooled.Push(container);
    }

    /// <summary>
    /// Forces every container back, focused or not. Used when the items
    /// themselves change: the focused item may no longer exist, so keeping its
    /// container would keep stale content on screen — a worse fault than
    /// losing focus.
    /// </summary>
    private void RecycleAll()
    {
        foreach (var index in _realized.Keys.ToList()) Recycle(index, force: true);
    }

    protected override void OnItemsChanged(
        IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        // Deliberately blunt: everything realized goes back and the next measure
        // rebuilds. Index-shuffling on insert and remove is where
        // VirtualizingStackPanel spends much of its complexity, and this panel's
        // items come from a listing that is reloaded wholesale anyway.
        RecycleAll();
        InvalidateMeasure();
    }

    // ---- the abstract contract ---------------------------------------------
    //
    // Declared `protected`, NOT `protected internal`, even though the base
    // declares them `protected internal`. Across an assembly boundary the
    // `internal` half does not apply, so C# requires the override to drop it
    // (CS0507). `GetControl` below is plain `protected` in the base and needs no
    // such adjustment.

    protected override Control? ContainerFromIndex(int index)
        => _realized.TryGetValue(index, out var container) ? container : null;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realized) in _realized)
            if (ReferenceEquals(realized, container)) return index;

        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers()
        => _realized.Values;

    protected override Control? ScrollIntoView(int index)
    {
        var items = Items;

        if (index < 0 || index >= items.Count) return null;

        _scrollTarget = index;

        var itemWidth = CellWidth;
        var itemHeight = CellHeight;
        var row = index / Math.Max(1, _columns);

        this.BringIntoView(new Rect(0, row * itemHeight, itemWidth, itemHeight));

        // Realize the target NOW rather than waiting for the layout pass that
        // BringIntoView merely schedules.
        //
        // Returning null here is not harmless: the caller has nothing to focus,
        // so the key looks like it did not register — and the unhandled key then
        // falls through to whatever else is listening, which in this window is
        // plenty. Home and End arrive as First and Last, and both jump far
        // outside the realized range, so this was the common case rather than an
        // edge one.
        //
        // A container realized here that turns out to be off screen is recycled
        // by the next measure, exactly like any other.
        if (!_realized.TryGetValue(index, out var container))
        {
            container = GetOrCreate(items[index], index);

            if (container is not null)
            {
                _realized[index] = container;
                container.Measure(new Size(itemWidth, itemHeight));

                var column = index % Math.Max(1, _columns);

                container.Arrange(new Rect(
                    column * itemWidth, row * itemHeight, itemWidth, itemHeight));
            }

            InvalidateMeasure();
        }

        return container;
    }

    /// <summary>
    /// Keyboard navigation. Left and right move by one; up and down move by a
    /// whole row — the only part that differs from a stack panel, and the reason
    /// this cannot simply reuse one.
    /// </summary>
    protected override IInputElement? GetControl(
        NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;

        var current = from is Control control ? IndexFromContainer(control) : -1;

        // TEMPORARY. Details (Avalonia's own VirtualizingStackPanel) handles
        // Home/End after clicking empty space; grid does not. First and Last
        // here ignore `current` entirely, so if the key reached this method it
        // would work regardless of selection — which suggests it does not
        // arrive. That is a guess, and this line settles it: no output means
        // the key never gets here and the problem is focus, not navigation.
        Console.Error.WriteLine(
            $"[heimdall] nav: {direction} from={from?.GetType().Name ?? "null"} "
            + $"current={current} count={count} wrap={wrap}");

        if (count == 0) return null;

        // NO ORIGIN — the list itself has focus rather than a row, which is
        // the normal state right after clicking empty space. Arithmetic on
        // `current = -1` produced item 7 for PageDown (`-1 + _columns`), which
        // reads as random. Measured 27 July 2026:
        // `nav: PageDown from=null current=-1` then `nav-target: 7`.
        //
        // Avalonia's VirtualizingStackPanel REFUSES this case outright —
        // `GetControl` returns null when `from` is null and the direction is
        // not First or Last. Selecting the first item is the friendlier
        // reading of "I clicked into the list and pressed a key", and matches
        // what Explorer does; flipping to Avalonia's behaviour is changing
        // this one return to `null`.
        if (current < 0
            && direction is not (NavigationDirection.First or NavigationDirection.Last))
            return ScrollIntoView(0);

        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Left or NavigationDirection.Previous => current - 1,
            NavigationDirection.Right or NavigationDirection.Next => current + 1,
            // PageUp/PageDown are MEASURED to be nearly dead here. Once a row
            // has focus the ScrollViewer claims them and pages the viewport by
            // its own height (viewport 635..1275, then 1275..1916, …) with no
            // `nav:` line at all — and it moves the VIEW without moving focus
            // or selection. This panel sees them only on the first press,
            // while the list itself still holds focus, and that case is now
            // handled above. Left mapped rather than removed because nothing
            // has measured what happens in a folder short enough not to
            // scroll.
            NavigationDirection.Up or NavigationDirection.PageUp => current - _columns,
            NavigationDirection.Down or NavigationDirection.PageDown => current + _columns,
            _ => -1,
        };

        if (target < 0 || target >= count)
        {
            if (!wrap) return null;

            target = target < 0 ? count - 1 : 0;
        }

        // TEMPORARY, alongside recycle-focused: the index actually returned,
        // so a focus loss can be matched to the container it was returned on
        // without deriving it from the direction.
        Console.Error.WriteLine($"[heimdall] nav-target: {target}");

        return ScrollIntoView(target);
    }
}
