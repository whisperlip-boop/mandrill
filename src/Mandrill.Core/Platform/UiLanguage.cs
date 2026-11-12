using Windows.Win32;

namespace Mandrill.Core.Platform;

/// <summary>OS user-interface language.</summary>
public static class UiLanguage
{
    /// <summary>Environment override for the language ("ko"/"en"), used for testing until the Settings dialog exists.</summary>
    public const string OverrideVariable = "MANDRILL_LANG";

    /// <summary>The user's default UI LANGID (GetUserDefaultUILanguage). Pure call, no I/O.</summary>
    /// <returns>The LANGID.</returns>
    public static ushort UserDefaultLanguageId() => PInvoke.GetUserDefaultUILanguage();

    /// <summary>The effective language setting: MANDRILL_LANG if set, else "auto".</summary>
    /// <returns>"ko", "en" or "auto".</returns>
    public static string EffectiveSetting() => EffectiveSetting(null);

    /// <summary>
    /// The effective language setting: MANDRILL_LANG (developer override) if set, else the saved Settings value
    /// (settings.json "language"), else "auto".
    /// </summary>
    /// <param name="saved">The saved setting, or null.</param>
    /// <returns>"ko", "en" or "auto" (an unknown saved value is resolved as "auto" by the localizer).</returns>
    public static string EffectiveSetting(string? saved)
        => Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } dev ? dev : saved ?? "auto";
}
