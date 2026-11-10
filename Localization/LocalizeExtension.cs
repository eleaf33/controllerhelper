using Microsoft.UI.Xaml.Markup;

namespace ControllerHelper.Localization;

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;
    protected override object ProvideValue() => Text.Get(Key);
}
