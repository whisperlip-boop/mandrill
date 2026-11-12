using Mandrill.Core.Localization;
using Microsoft.UI.Xaml.Markup;

namespace Mandrill.App.Markup;

/// <summary>XAML markup extension <c>{m:Tr Key=menu.file}</c>: a localized string, resolved once at load (ADR-0013).</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Tr : MarkupExtension
{
    /// <summary>Locale key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    protected override object ProvideValue() => Localizer.Current[Key];
}
