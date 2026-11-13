using Mandrill.Core.Localization;

namespace Mandrill.Presentation.Settings;

/// <summary>One cell of the language picker: a large name over a smaller secondary line.</summary>
/// <param name="Code">"auto" or a language code (the settings.json value).</param>
/// <param name="Name">First line: the native name, or "Auto (Windows language)".</param>
/// <param name="Detail">Second line: the English name, or for Auto the native name of the language it resolves to.</param>
/// <param name="IsCurrent">The saved setting (highlighted with a check).</param>
public sealed record LanguageChoice(string Code, string Name, string Detail, bool IsCurrent)
{
    /// <summary>UIA name: "first line, second line".</summary>
    public string AutomationName => $"{Name}, {Detail}";
}

/// <summary>The language picker's cells (Auto first, always; then the languages matching the search).</summary>
public static class LanguageChoices
{
    /// <summary>Builds the cells for a search text.</summary>
    /// <param name="languages">Installed languages in display order (<see cref="LanguageCatalog.All"/> with a locale file).</param>
    /// <param name="autoLanguage">The code "auto" resolves to on this PC.</param>
    /// <param name="saved">The saved setting; anything not in <paramref name="languages"/> counts as Auto.</param>
    /// <param name="query">Search text, matched case-insensitively against the native and English names.</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    /// <returns>Auto, then the matching languages.</returns>
    public static IReadOnlyList<LanguageChoice> Build(IReadOnlyList<LanguageInfo> languages, string autoLanguage, string? saved, string? query, Localizer? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var strings = localizer ?? Localizer.Current;
        string current = languages.FirstOrDefault(l => string.Equals(l.Code, saved, StringComparison.OrdinalIgnoreCase))?.Code ?? LanguageCatalog.Auto;
        string autoName = (LanguageCatalog.Find(autoLanguage) ?? LanguageCatalog.Find(Localizer.English)!).NativeName;
        string text = query?.Trim() ?? string.Empty;
        var cells = new List<LanguageChoice> { new(LanguageCatalog.Auto, strings["dialog.language.auto"], autoName, current == LanguageCatalog.Auto) };
        foreach (var language in languages)
        {
            if (text.Length == 0
                || language.NativeName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || language.EnglishName.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                cells.Add(new LanguageChoice(language.Code, language.NativeName, language.EnglishName, language.Code == current));
            }
        }

        return cells;
    }
}
