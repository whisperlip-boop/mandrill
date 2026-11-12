using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Core.Localization;

/// <summary>
/// User-facing strings from flat dotted-key JSON (Locales\en.json, Locales\ko.json; ADR-0013).
/// Lookup: chosen language → English → the key itself; each fallback is logged once per key.
/// Lookups are thread-safe and do no I/O.
/// </summary>
public sealed partial class Localizer
{
    /// <summary>English (always loaded as the fallback table).</summary>
    public const string English = "en";

    /// <summary>Korean.</summary>
    public const string Korean = "ko";

    private const ushort LangKorean = 0x12;
    private readonly FrozenDictionary<string, string> _primary;
    private readonly FrozenDictionary<string, string> _english;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    private Localizer(string language, IReadOnlyDictionary<string, string> primary, IReadOnlyDictionary<string, string> english, ILogger logger)
    {
        Language = language;
        _primary = primary.ToFrozenDictionary(StringComparer.Ordinal);
        _english = english.ToFrozenDictionary(StringComparer.Ordinal);
        _logger = logger;
    }

    /// <summary>The process-wide instance used by views (<c>{m:Tr}</c>). Until set, keys are returned as-is.</summary>
    public static Localizer Current { get; private set; } = FromTables(English, new Dictionary<string, string>(), new Dictionary<string, string>(), NullLogger.Instance);

    /// <summary>Active language code ("ko" or "en").</summary>
    public string Language { get; }

    /// <summary>Looks up a string: language → English → key.</summary>
    /// <param name="key">Flat dotted key, e.g. "menu.file".</param>
    /// <returns>The localized text, or the key when no table has it.</returns>
    public string this[string key]
    {
        get
        {
            if (_primary.TryGetValue(key, out string? text))
            {
                return text;
            }

            if (_english.TryGetValue(key, out text))
            {
                if (Language != English)
                {
                    ReportOnce(key, "Missing locale key {Key} in {Language}; using English", Language);
                }

                return text;
            }

            ReportOnce(key, "Missing locale key {Key} in {Language} and English; showing the key", Language);
            return key;
        }
    }

    /// <summary>Installs the process-wide instance. Call once at startup before any view is created.</summary>
    /// <param name="localizer">The loaded localizer.</param>
    public static void SetCurrent(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Current = localizer;
    }

    /// <summary>Resolves the language setting: "ko"/"en" as given; anything else ("auto") follows the OS UI language.</summary>
    /// <param name="setting">"auto", "ko" or "en" (case-insensitive).</param>
    /// <param name="uiLanguageId">OS UI LANGID (GetUserDefaultUILanguage).</param>
    /// <returns>"ko" or "en".</returns>
    public static string ResolveLanguage(string? setting, ushort uiLanguageId)
    {
        if (string.Equals(setting, Korean, StringComparison.OrdinalIgnoreCase))
        {
            return Korean;
        }

        if (string.Equals(setting, English, StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        return (uiLanguageId & 0x3FF) == LangKorean ? Korean : English;
    }

    /// <summary>Loads Locales\en.json and Locales\&lt;language&gt;.json. Worker thread only. Unreadable files are logged and treated as empty.</summary>
    /// <param name="localesDirectory">Directory containing the JSON files.</param>
    /// <param name="language">"ko" or "en".</param>
    /// <param name="logger">Logger for load errors and missing keys.</param>
    /// <returns>The localizer.</returns>
    public static Localizer Load(string localesDirectory, string language, ILogger logger)
    {
        var english = ReadTable(Path.Combine(localesDirectory, English + ".json"), logger);
        var primary = language == English ? english : ReadTable(Path.Combine(localesDirectory, language + ".json"), logger);
        logger.LogInformation("Locale {Language} loaded ({Count} keys, {EnglishCount} English)", language, primary.Count, english.Count);
        return new Localizer(language, primary, english, logger);
    }

    /// <summary>Creates a localizer from in-memory tables (tests).</summary>
    /// <param name="language">Language code.</param>
    /// <param name="primary">Table for <paramref name="language"/>.</param>
    /// <param name="english">English fallback table.</param>
    /// <param name="logger">Logger for missing keys.</param>
    /// <returns>The localizer.</returns>
    public static Localizer FromTables(string language, IReadOnlyDictionary<string, string> primary, IReadOnlyDictionary<string, string> english, ILogger logger)
        => new(language, primary, english, logger);

    /// <summary>Parses a flat JSON object of string values.</summary>
    /// <param name="json">UTF-8 JSON text.</param>
    /// <returns>Key → text.</returns>
    /// <exception cref="JsonException">The text is not a flat object of strings.</exception>
    public static Dictionary<string, string> ParseTable(ReadOnlySpan<byte> json)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        json = json.StartsWith(Encoding.UTF8.Preamble) ? json[Encoding.UTF8.Preamble.Length..] : json; // a locale file saved by Notepad has a BOM
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Locale root must be an object");
        }

        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"Locale value for \"{p.Name}\" must be a string");
            }

            if (!table.TryAdd(p.Name, p.Value.GetString()!))
            {
                throw new JsonException($"Duplicate locale key \"{p.Name}\"");
            }
        }

        return table;
    }

    /// <summary>Looks up <paramref name="key"/> and replaces named placeholders such as {count}.</summary>
    /// <param name="key">Locale key.</param>
    /// <param name="args">Placeholder names and values (formatted with the current culture).</param>
    /// <returns>The formatted text.</returns>
    public string Format(string key, params (string Name, object Value)[] args)
    {
        string text = this[key];
        if (args.Length == 0)
        {
            return text;
        }

        // Single pass, so a value that itself contains "{name}" is never substituted again.
        var values = new Dictionary<string, string?>(args.Length, StringComparer.Ordinal);
        foreach (var (name, value) in args)
        {
            values[name] = Convert.ToString(value, CultureInfo.CurrentCulture);
        }

        return Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out string? v) ? v ?? string.Empty : m.Value);
    }

    /// <summary>Plural form: "&lt;key&gt;.one" when <paramref name="n"/> is 1, else "&lt;key&gt;.other"; {count} is filled in.</summary>
    /// <param name="key">Base key.</param>
    /// <param name="n">Quantity.</param>
    /// <param name="args">Additional placeholders.</param>
    /// <returns>The formatted text.</returns>
    public string Plural(string key, long n, params (string Name, object Value)[] args)
    {
        var all = new (string, object)[args.Length + 1];
        all[0] = ("count", n);
        args.CopyTo(all, 1);
        return Format(key + (n == 1 ? ".one" : ".other"), all);
    }

    private static Dictionary<string, string> ReadTable(string path, ILogger logger)
    {
        try
        {
            return ParseTable(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(ex, "Cannot load locale file {File}", path);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Placeholder();

    private void ReportOnce(string key, string message, string language)
    {
        if (_reported.TryAdd(key, 0))
        {
#pragma warning disable CA2254 // Message templates are the two fixed strings above.
            _logger.LogWarning(message, key, language);
#pragma warning restore CA2254
        }
    }
}
