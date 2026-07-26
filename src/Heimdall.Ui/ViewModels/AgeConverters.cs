using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Heimdall.Ui.ViewModels;

/// <summary>
/// Shades the modified column by how long ago a file changed, so what moved
/// recently stands out at a glance.
///
/// A **single-hue lightness ramp**, deliberately not a red-to-green heat map:
/// hue alone would carry the entire meaning and be invisible to a large
/// fraction of people. Lightness is the one channel everyone reads, and the
/// absolute date stays in the same cell, so the information is never colour-only.
/// </summary>
public static class AgeConverters
{
    private static readonly TimeSpan[] Steps =
    [
        TimeSpan.FromHours(1),
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(30),
        TimeSpan.FromDays(365),
    ];

    /// <summary>
    /// Six brushes, freshest first, supplied by the theme.
    ///
    /// Previously these were fixed pale blues — which vanish entirely on a
    /// light colour scheme. Now the ramp is derived from the desktop's own text
    /// and dim-text colours, so it stays a legible lightness ramp whatever the
    /// scheme, which is the whole point of using lightness rather than hue.
    /// </summary>
    private static IBrush[] _ramp =
    [
        new SolidColorBrush(Color.Parse("#B4DCF2")),
        new SolidColorBrush(Color.Parse("#93BDD4")),
        new SolidColorBrush(Color.Parse("#7A9CB0")),
        new SolidColorBrush(Color.Parse("#647E8C")),
        new SolidColorBrush(Color.Parse("#55656F")),
        new SolidColorBrush(Color.Parse("#4A555C")),
    ];

    public static void SetRamp(IBrush[] ramp)
    {
        if (ramp.Length == 6) _ramp = ramp;
    }

    public static readonly IValueConverter Brush =
        new FuncValueConverter<DateTimeOffset, IBrush>(modified =>
        {
            var age = DateTimeOffset.Now - modified;

            // A clock skew or a file dated in the future reads as brand new,
            // which is closer to the truth than treating it as ancient.
            if (age < TimeSpan.Zero) return _ramp[0];

            for (var i = 0; i < Steps.Length; i++)
                if (age < Steps[i]) return _ramp[i];

            return _ramp[^1];
        });

    /// <summary>
    /// The same fact as words, for the tooltip — so the shading is a shortcut
    /// to something already stated rather than the only way to learn it.
    /// </summary>
    public static readonly IValueConverter Description =
        new FuncValueConverter<DateTimeOffset, string?>(modified =>
        {
            // Gated here rather than in markup: a null Tip shows no tooltip, so
            // the preference costs one line and no binding gymnastics. Read live,
            // so turning it off takes effect on the next hover.
            if (!Settings.AppSettings.Current.General.ShowTooltips) return null;

            var age = DateTimeOffset.Now - modified;

            return age switch
            {
                { TotalMinutes: < 1 } => "just now",
                { TotalHours: < 1 } => $"{(int)age.TotalMinutes} minutes ago",
                { TotalDays: < 1 } => $"{(int)age.TotalHours} hours ago",
                { TotalDays: < 7 } => $"{(int)age.TotalDays} days ago",
                { TotalDays: < 60 } => $"{(int)(age.TotalDays / 7)} weeks ago",
                { TotalDays: < 730 } => $"{(int)(age.TotalDays / 30)} months ago",
                _ => $"{(int)(age.TotalDays / 365)} years ago",
            };
        });
}
