namespace Mandrill.Core.Localization;

/// <summary>A UI language Mandrill supports: its locale file code and its fixed display names (never translated).</summary>
/// <param name="Code">Locale code, the name of <c>Locales\&lt;code&gt;.json</c> and the settings.json value.</param>
/// <param name="NativeName">Name in the language itself (Windows' display name).</param>
/// <param name="EnglishName">Name in English.</param>
/// <param name="Tag">BCP-47 tag for the UI framework (XAML font fallback and built-in control strings).</param>
public sealed record LanguageInfo(string Code, string NativeName, string EnglishName, string Tag);

/// <summary>The supported UI languages (docs/i18n.md): adding a language = one line here + one locale file.</summary>
public static class LanguageCatalog
{
    /// <summary>The setting that follows the Windows UI language.</summary>
    public const string Auto = "auto";

    /// <summary>Every supported language, sorted by English name (the picker's order).</summary>
    public static IReadOnlyList<LanguageInfo> All { get; } = new LanguageInfo[]
    {
        new("en", "English (United States)", "English (United States)", "en-US"),
        new("ja", "日本語 (日本)", "Japanese (Japan)", "ja-JP"),
        new("ko", "한국어(대한민국)", "Korean (South Korea)", "ko-KR"),
        new("zh-Hans", "中文 (简体)", "Chinese (Simplified)", "zh-CN"),
        new("zh-Hant", "中文 (繁體)", "Chinese (Traditional)", "zh-TW"),
    }.OrderBy(l => l.EnglishName, StringComparer.Ordinal).ToArray();

    /// <summary>The supported language with this code (case-insensitive), or null.</summary>
    /// <param name="code">Locale code, e.g. "zh-hans".</param>
    /// <returns>The language, or null for "auto", empty or unknown codes.</returns>
    public static LanguageInfo? Find(string? code)
        => All.FirstOrDefault(l => string.Equals(l.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps a Windows UI culture name to a supported code: ko-* → ko, ja-* → ja, zh-CN/SG/Hans* → zh-Hans,
    /// zh-TW/HK/MO/Hant* → zh-Hant, anything else → en.
    /// </summary>
    /// <param name="cultureName">Culture name such as "ko-KR" or "zh-Hant-TW"; empty when unknown.</param>
    /// <returns>A code from <see cref="All"/>.</returns>
    public static string FromCultureName(string? cultureName)
    {
        string[] parts = (cultureName ?? string.Empty).Trim().Split('-');
        return (parts[0].ToUpperInvariant(), parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty) switch
        {
            ("KO", _) => "ko",
            ("JA", _) => "ja",
            ("ZH", "CN" or "SG" or "HANS") => "zh-Hans",
            ("ZH", "TW" or "HK" or "MO" or "HANT") => "zh-Hant",
            _ => Localizer.English,
        };
    }
}
