using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Mandrill.Core.Localization;

namespace Mandrill.Core.State;

/// <summary>settings.json (architecture §10): preferences edited in the Settings dialog.</summary>
/// <param name="Language">"auto" or a code from <see cref="LanguageCatalog.All"/> (AC-1.34, language picker).</param>
/// <param name="ShowFavoritesBar">Whether the favorites bar is visible.</param>
/// <param name="LayoutDefaults">Default arrangement for 2 and 3 panes (AC-1.3b).</param>
/// <param name="Index">Index options (Phase 2; stored now so the schema does not change).</param>
/// <param name="Theme">"system", "light", "dark" or "highcontrast" (owner round 5 #5); absent in older files, so it defaults to "system".</param>
/// <param name="FilterBarAlways">Filter bar (ui-spec §2b): true = always shown in every pane, false = shown when needed (default; absent in older files).</param>
public sealed record AppSettings(string Language, bool ShowFavoritesBar, LayoutDefaults LayoutDefaults, IndexSettings Index, string Theme = AppSettings.ThemeSystem, bool FilterBarAlways = false)
    : IStateDocument<AppSettings>
{
    /// <summary>Follow the Windows UI language.</summary>
    public const string LanguageAuto = "auto";

    /// <summary>Follow the Windows light/dark setting.</summary>
    public const string ThemeSystem = "system";

    /// <summary>Always light.</summary>
    public const string ThemeLight = "light";

    /// <summary>Always dark.</summary>
    public const string ThemeDark = "dark";

    /// <summary>The app's own high-contrast palette over dark; a Windows contrast theme still wins.</summary>
    public const string ThemeHighContrast = "highcontrast";

    /// <summary>Schema version written on save.</summary>
    public static int CurrentVersion => 1;

    /// <summary>Source-generated serialization metadata.</summary>
    public static JsonTypeInfo<AppSettings> TypeInfo => MandrillJsonContext.Default.AppSettings;

    /// <summary>First-run settings: auto language, favorites bar shown, 2_lr / 3_l1r2 (AC-1.3b).</summary>
    /// <returns>A new default document.</returns>
    public static AppSettings CreateDefault() => new(LanguageAuto, true, LayoutDefaults.Default, IndexSettings.Default);

    /// <inheritdoc/>
    public AppSettings Normalize(ICollection<string> fixes)
    {
        ArgumentNullException.ThrowIfNull(fixes);
        string? language = Known(Language, [LanguageAuto, .. LanguageCatalog.All.Select(l => l.Code)]);
        string? theme = Known(Theme, ThemeSystem, ThemeLight, ThemeDark, ThemeHighContrast);
        return new AppSettings(
            Fixes.Check(fixes, "language", language ?? Language!, language is not null, LanguageAuto),
            ShowFavoritesBar,
            Fixes.Check(fixes, "layoutDefaults", LayoutDefaults, LayoutDefaults is not null, LayoutDefaults.Default).Normalize(fixes),
            Fixes.Check(fixes, "index", Index, Index is not null, IndexSettings.Default).Normalize(fixes),
            Fixes.Check(fixes, "theme", theme ?? Theme!, theme is not null, ThemeSystem),
            FilterBarAlways);
    }

    private static string? Known(string? value, params string[] allowed)
        => allowed.FirstOrDefault(a => string.Equals(a, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Default arrangement used by Add/Remove pane and the picker's quick entries (AC-1.3a, AC-1.3b).</summary>
/// <param name="TwoPanes">Layout key for 2 panes ("2_lr" or "2_tb"); JSON name "2".</param>
/// <param name="ThreePanes">Layout key for 3 panes (one of the four 3-pane keys); JSON name "3".</param>
public sealed record LayoutDefaults(
    [property: JsonPropertyName("2")] string TwoPanes,
    [property: JsonPropertyName("3")] string ThreePanes)
{
    /// <summary>First-run defaults: 2 = left/right, 3 = 1 left + 2 right (AC-1.3b).</summary>
    public static LayoutDefaults Default { get; } = new(LayoutKeys.TwoLeftRight, LayoutKeys.ThreeLeft1Right2);

    /// <summary>Default layout key for <paramref name="paneCount"/> panes.</summary>
    /// <param name="paneCount">1 to 4.</param>
    /// <returns>The saved choice for 2 and 3; the only layout for 1 and 4.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="paneCount"/> is outside 1–4.</exception>
    public string ForPaneCount(int paneCount) => paneCount switch
    {
        1 => LayoutKeys.One,
        2 => TwoPanes,
        3 => ThreePanes,
        4 => LayoutKeys.FourGrid,
        _ => throw new ArgumentOutOfRangeException(nameof(paneCount), paneCount, "Pane count must be 1 to 4"),
    };

    internal LayoutDefaults Normalize(ICollection<string> fixes) => new(
        Fixes.Check(fixes, "layoutDefaults.2", TwoPanes, IsKeyFor(TwoPanes, 2), Default.TwoPanes),
        Fixes.Check(fixes, "layoutDefaults.3", ThreePanes, IsKeyFor(ThreePanes, 3), Default.ThreePanes));

    private static bool IsKeyFor(string? key, int paneCount) => LayoutKeys.IsKnown(key) && LayoutKeys.PaneCount(key!) == paneCount;
}

/// <summary>Index options (AC-2.8, AC-2.10), edited in the Settings dialog (WP-2.6).</summary>
/// <param name="Exclude">Excluded folders.</param>
/// <param name="RescanHours">Hours between verify passes, <see cref="MinRescanHours"/>–<see cref="MaxRescanHours"/>.</param>
public sealed record IndexSettings(IReadOnlyList<string> Exclude, int RescanHours)
{
    /// <summary>Shortest rescan interval in hours.</summary>
    public const int MinRescanHours = 1;

    /// <summary>Longest rescan interval in hours (one week).</summary>
    public const int MaxRescanHours = 168;

    /// <summary>No exclusions, 24 h verify interval.</summary>
    public static IndexSettings Default { get; } = new([], 24);

    internal IndexSettings Normalize(ICollection<string> fixes)
    {
        string[] exclude = [.. (Exclude ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (Exclude is null || exclude.Length != Exclude.Count)
        {
            fixes.Add("index.exclude: removed empty or duplicate entries");
        }

        return new IndexSettings(exclude, Fixes.Check(fixes, "index.rescanHours", RescanHours, RescanHours is >= MinRescanHours and <= MaxRescanHours, Default.RescanHours));
    }
}
