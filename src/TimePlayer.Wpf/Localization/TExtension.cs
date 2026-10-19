using System.Windows.Markup;
using TimePlayer.Core.Localization;

namespace TimePlayer.Wpf.Localization;

/// <summary>
/// Translated text in XAML: <c>Content="{l:T 'Settings (F2)'}"</c>. The language is chosen at
/// startup, so the text is looked up once when the window loads.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}
