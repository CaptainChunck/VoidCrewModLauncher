using System.Windows;
using System.Windows.Controls;

namespace VoidCrewModLauncher.Controls;

/// <summary>
/// Upper-case text with the wide letter spacing of the game's menu titles and news cards ("N E W  U P D A T E").
/// WPF text has no letter spacing, so thin spaces go between the letters.
/// </summary>
public sealed class SpacedText : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(SpacedText), new PropertyMetadata("", (d, e) => ((SpacedText)d).Text = Space(e.NewValue as string)));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static string Space(string? text) =>
        string.IsNullOrEmpty(text) ? "" : string.Join(' ', text.ToUpperInvariant().ToCharArray()).Replace("   ", "     ");
}
