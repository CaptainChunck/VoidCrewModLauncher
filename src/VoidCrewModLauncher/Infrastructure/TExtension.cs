using System.Windows.Markup;

namespace VoidCrewModLauncher.Infrastructure;

/// <summary>
/// Translated text in XAML: <c>Text="{i:T SERVER BROWSER}"</c>, or <c>{i:T 'Text, with comma'}</c>. The English text is the
/// key (see <see cref="Loc"/>). The language is chosen at start-up, so the text is looked up once.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
