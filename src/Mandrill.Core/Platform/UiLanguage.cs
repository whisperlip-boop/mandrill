using System.Globalization;
using Windows.Win32;

namespace Mandrill.Core.Platform;

/// <summary>OS user-interface language.</summary>
public static class UiLanguage
{
    /// <summary>Developer override for the language: "auto" or any code from <see cref="Localization.LanguageCatalog.All"/>; wins over the saved setting.</summary>
    public const string OverrideVariable = "MANDRILL_LANG";

    /// <summary>The culture name of the user's default UI language (GetUserDefaultUILanguage), e.g. "ko-KR". No I/O.</summary>
    /// <returns>The culture name, or empty when Windows reports a LANGID .NET does not know (a custom UI language).</returns>
    public static string UserDefaultCultureName()
    {
        ushort langId = PInvoke.GetUserDefaultUILanguage();
        try
        {
            return CultureInfo.GetCultureInfo(langId).Name;
        }
        catch (CultureNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>The effective language setting: MANDRILL_LANG if set, else "auto".</summary>
    /// <returns>The language setting ("auto" or a code).</returns>
    public static string EffectiveSetting() => EffectiveSetting(null);

    /// <summary>
    /// The effective language setting: MANDRILL_LANG (developer override) if set, else the saved Settings value
    /// (settings.json "language"), else "auto".
    /// </summary>
    /// <param name="saved">The saved setting, or null.</param>
    /// <returns>The language setting (an unknown value is resolved as "auto" by the localizer).</returns>
    public static string EffectiveSetting(string? saved)
        => Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } dev ? dev : saved ?? "auto";
}
