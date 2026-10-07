using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RdpManager.App.Converters;

/// <summary>"search" → the frozen geometry resource "Icon.search".</summary>
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string { Length: > 0 } key ? Application.Current.TryFindResource("Icon." + key) as Geometry : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is true / non-empty / non-null / non-zero. ConverterParameter "invert" flips it.</summary>
public sealed class VisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "invert") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>True when the value equals the parameter (radio buttons bound to a string); ConvertBack selects it.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value?.ToString(), parameter?.ToString());
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? parameter! : Binding.DoNothing;
}

/// <summary>Status, session and step keys → semantic brushes (text plus icon, never color alone).</summary>
public sealed class KindBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value?.ToString() ?? "").ToLowerInvariant() switch
        {
            "available" or "active" or "done" or "success" => "SuccessBrush",
            "busy" or "connecting" or "reconnecting" or "info" => "InfoBrush",
            "offline" or "failed" or "error" => "ErrorBrush",
            "warning" or "skipped" => "WarningBrush",
            "running" => "TextPrimaryBrush",
            _ => "Gray500Brush",
        };
        return Application.Current.Resources[key];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Alert kind → background or border brush (parameter "bg" or "border").</summary>
public sealed class AlertBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value?.ToString() switch { "error" => "Error", "warning" => "Warning", "success" => "Success", _ => "Info" };
        return Application.Current.Resources[kind + (parameter as string == "border" ? "BorderBrush" : "BgBrush")];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Step state → icon key of the progress dialog.</summary>
public sealed class StepIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Application.Current.TryFindResource("Icon." + ((value as string) switch { "done" => "checkCircle", "failed" => "xCircle", "skipped" => "warning", _ => "dot" }));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Columns of a card row → equal-width uniform grid; also used for "last row" corner radius.</summary>
public sealed class LastRowRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new CornerRadius(0, 0, 8, 8) : new CornerRadius(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LastRowBorderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new Thickness(1, 0, 1, 1) : new Thickness(1, 0, 1, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
