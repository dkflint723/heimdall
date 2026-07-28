using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Heimdall.Ui.ViewModels;

namespace Heimdall.Ui;

/// <summary>
/// Per-pane type and icon scale.
///
/// Works by exploiting the same lookup rule that once broke scaling entirely:
/// a DynamicResource resolves from the nearest dictionary outward. Writing the
/// metrics into a pane's own root control means everything inside that pane
/// resolves there, while the sidebar, status bar and the other side of a split
/// keep resolving at application level.
///
/// The formulas live in <see cref="Compute"/> and are shared with the
/// application-level defaults, so the two can never drift apart.
/// </summary>
public static class PaneScale
{
    private static readonly (string Key, double Value)[] FontMetrics =
    [
        ("FontSizeTiny", 11),
        ("FontSizeSmall", 12.5),
        ("FontSizeBase", 14),
        ("FontSizeLarge", 15.5),
    ];

    private static readonly (string Key, double Value)[] IconMetrics =
    [
        ("ThumbSize", 26),
        ("IconSize", 17),
        ("TileSize", 84),
    ];

    /// <summary>
    /// Every metric for a given pair of scales. Structural sizes are derived
    /// rather than free: a row has to fit the taller of its label and its icon,
    /// so it cannot be set independently of either.
    /// </summary>
    public static IEnumerable<(string Key, double Value)> Compute(
        double fontScale, double iconScale)
    {
        foreach (var (key, value) in FontMetrics)
            yield return (key, Math.Round(value * fontScale, 1));

        foreach (var (key, value) in IconMetrics)
            yield return (key, Math.Round(value * iconScale, 1));

        var body = 14 * fontScale;
        var thumb = 26 * iconScale;
        var tile = 84 * iconScale;

        var rowHeight = Math.Round(Math.Max(body * 2.1, thumb + 8), 1);

        // Icons.MaximumLines and the two text-width settings are NOT wired, and
        // the reason is structural rather than laziness. Every metric here is a
        // double, written into control Resources and read back by
        // DynamicResource — which assigns directly, without converting. MaxLines
        // is an int, so it cannot come down this path, and a second typed
        // pipeline is more machinery than the setting is worth today.
        //
        // Tile height would also have to follow: label lines are not free, and a
        // tile that does not grow to fit them just clips the label.
        yield return ("RowHeight", rowHeight);
        yield return ("TileWidth", Math.Round(tile + 24, 1));
        yield return ("TileHeight", Math.Round(tile + body * 2.9, 1));

        // ---- user-adjustable spacing ----------------------------------------
        //
        // Read from AppSettings rather than passed in, matching the static
        // provider convention used by IconLoader and RowMetadata. It makes this
        // method impure, which is worth naming: it now depends on when it runs,
        // so a settings save has to re-apply the metrics.
        //
        // **The six-pixel floor is not padding, it is correctness.** The grid
        // item template carries Margin="3", so a cell exactly TileWidth clips
        // every tile by that margin — uniformly, which reads as a styling fault
        // rather than a layout bug. The user's number is EXTRA on top, so zero
        // means "as it was" instead of "broken".
        var icons = Settings.AppSettings.Current.Views.Icons.Spacing;
        var compact = Settings.AppSettings.Current.Views.Compact.Spacing;

        yield return ("TileSpacing", Math.Round(6 + Math.Max(0, icons) * iconScale, 1));

        // Compact gets a CELL larger than its row, and the row is drawn inside
        // it — rather than growing the row itself, which would stretch the
        // selection highlight across the gap.
        yield return ("CompactCellWidth",
            Math.Round(210 * fontScale + Math.Max(0, compact) * iconScale, 1));
        yield return ("CompactCellHeight",
            Math.Round(rowHeight + Math.Max(0, compact) * iconScale, 1));
        yield return ("RailWidth", Math.Round(44 * fontScale, 1));

        // Compact columns are sized by the text they hold, not by the icons —
        // the mode exists to fit names on screen.
        yield return ("CompactWidth", Math.Round(210 * fontScale, 1));

        // Three rows of chain, preserved at any combination of the two scales.
        yield return ("ColumnStripHeight", Math.Round(rowHeight * 3 + 6, 1));
    }

    // ---- attached property ------------------------------------------------

    public static readonly AttachedProperty<PaneViewModel?> PaneProperty =
        AvaloniaProperty.RegisterAttached<Control, PaneViewModel?>("Pane", typeof(PaneScale));

    public static void SetPane(Control control, PaneViewModel? value)
        => control.SetValue(PaneProperty, value);

    public static PaneViewModel? GetPane(Control control) => control.GetValue(PaneProperty);

    // Subscriptions are held per control, not per pane: containers are reused
    // as tabs switch, so the previous pane's handler must come off or a pane
    // that is no longer shown keeps rewriting this control's resources.
    private static readonly ConditionalWeakTable<Control, Subscription> Live = new();

    private sealed class Subscription
    {
        public PropertyChangedEventHandler? Handler;
    }

    static PaneScale()
    {
        PaneProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            var subscription = Live.GetOrCreateValue(control);

            // Class-handler args carry object?, not Optional<T>, so this is a
            // plain type pattern rather than GetValueOrDefault.
            if (args.OldValue is PaneViewModel previous
                && subscription.Handler is not null)
                previous.PropertyChanged -= subscription.Handler;

            if (args.NewValue is not PaneViewModel pane)
            {
                subscription.Handler = null;
                return;
            }

            subscription.Handler = (_, e) =>
            {
                if (e.PropertyName is nameof(PaneViewModel.FontScale)
                    or nameof(PaneViewModel.IconScale))
                    Apply(control, pane);
            };

            pane.PropertyChanged += subscription.Handler;
            Apply(control, pane);
        });
    }

    /// <summary>
    /// Spacing is a GLOBAL preference, not a per-pane metric, so it is
    /// deliberately not written into a pane's own dictionary.
    ///
    /// A DynamicResource resolves nearest-outward. If a pane wrote its own
    /// copy, a settings change would only reach the panes that happened to
    /// rescale afterwards — every other pane would keep the old spacing with
    /// nothing to indicate why. Leaving these keys out makes the lookup fall
    /// through to the application dictionary, which the save handler updates.
    /// </summary>
    private static readonly string[] GlobalOnly =
        ["TileSpacing", "CompactCellWidth", "CompactCellHeight"];

    private static void Apply(Control control, PaneViewModel pane)
    {
        foreach (var (key, value) in Compute(pane.FontScale, pane.IconScale))
        {
            if (Array.IndexOf(GlobalOnly, key) >= 0) continue;

            control.Resources[key] = value;
        }
    }
}
