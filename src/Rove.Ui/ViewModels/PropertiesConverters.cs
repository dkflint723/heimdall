using Avalonia.Data.Converters;

namespace Rove.Ui.ViewModels;

public static class PropertiesConverters
{
    /// <summary>The measure button doubles as a cancel button while running.</summary>
    public static readonly IValueConverter MeasureLabel =
        new FuncValueConverter<bool, string>(measuring => measuring ? "stop" : "measure");
}
