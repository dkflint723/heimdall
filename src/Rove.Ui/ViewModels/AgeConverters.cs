using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Rove.Ui.ViewModels;

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
    private static readonly (TimeSpan Within, Color Colour)[] Ramp =
    [
        (TimeSpan.FromHours(1),   Color.Parse("#B4DCF2")),
        (TimeSpan.FromDays(1),    Color.Parse("#93BDD4")),
        (TimeSpan.FromDays(7),    Color.Parse("#7A9CB0")),
        (TimeSpan.FromDays(30),   Color.Parse("#647E8C")),
        (TimeSpan.FromDays(365),  Color.Parse("#55656F")),
    ];

    private static readonly IBrush Ancient = new SolidColorBrush(Color.Parse("#4A555C"));

    private static readonly Dictionary<Color, IBrush> Brushes = Ramp
        .ToDictionary(step => step.Colour, step => (IBrush)new SolidColorBrush(step.Colour));

    public static readonly IValueConverter Brush =
        new FuncValueConverter<DateTimeOffset, IBrush>(modified =>
        {
            var age = DateTimeOffset.Now - modified;

            // A clock skew or a file dated in the future reads as brand new,
            // which is closer to the truth than treating it as ancient.
            if (age < TimeSpan.Zero) return Brushes[Ramp[0].Colour];

            foreach (var (within, colour) in Ramp)
                if (age < within) return Brushes[colour];

            return Ancient;
        });

    /// <summary>
    /// The same fact as words, for the tooltip — so the shading is a shortcut
    /// to something already stated rather than the only way to learn it.
    /// </summary>
    public static readonly IValueConverter Description =
        new FuncValueConverter<DateTimeOffset, string>(modified =>
        {
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
