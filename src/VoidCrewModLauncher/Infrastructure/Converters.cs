using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VoidCrewModLauncher.Infrastructure;

/// <summary>Visible when the value is "something": true, a non-empty string, a non-empty list, a non-null object, a non-zero number.</summary>
public sealed class VisibleWhen : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool something = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            double d => d != 0,
            ICollection c => c.Count > 0,
            _ => true,
        };
        if (Invert) something = !something;
        return something ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class Not : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : Binding.DoNothing;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : Binding.DoNothing;
}

/// <summary>Upper case, as the game's headers and buttons are.</summary>
public sealed class Upper : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString()?.ToUpperInvariant() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Packs several bindings into an array, for commands that need two values.</summary>
public sealed class Pack : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) => values.ToArray();

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter (for radio-style menu checks).</summary>
public sealed class EqualsParameter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
