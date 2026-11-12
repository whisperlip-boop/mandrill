using System.Text;

namespace Mandrill.Core.Search;

/// <summary>
/// The filename matcher of §8.4 (AC-2.16, E-2.8). Without <c>*</c>/<c>?</c> the query matches anywhere in the name;
/// with them the whole name must match the pattern (<c>*.txt</c> = names ending in ".txt"; a trailing <c>.*</c> also
/// matches names without a dot, so <c>*.*</c> is every name, as in Explorer). Case-insensitive (OrdinalIgnoreCase;
/// Ordinal when the query has no cased letter, e.g. Korean). Composed and decomposed Unicode both match: the query is
/// taken in NFC, and when its NFD form differs (Hangul, accents) names in that form match too, e.g. files from macOS or
/// a NAS. Every other character is literal: no regex, so <c>[ ( \ ^ $</c> never throw. <c>?</c> matches one UTF-16
/// unit, so in a decomposed (NFD) name it matches one jamo, not a whole Hangul syllable. A query with a path separator or
/// '\0' matches nothing (names have neither). Immutable; thread-safe.
/// </summary>
public sealed class NameMatcher
{
    private const string AnyExtension = ".*";

    private readonly string _text;
    private readonly string? _decomposed;

    private NameMatcher(string text)
    {
        _text = Normalized(text, NormalizationForm.FormC);
        string decomposed = Normalized(_text, NormalizationForm.FormD);
        _decomposed = decomposed == _text ? null : decomposed;
        IsGlob = _text.AsSpan().IndexOfAny('*', '?') >= 0;
        MatchesNothing = _text.AsSpan().IndexOfAny('\0', '\\', '/') >= 0;
        Literal = LiteralOf(_text, IsGlob);
        AlternateLiteral = _decomposed is null ? null : LiteralOf(_decomposed, IsGlob);
        Comparison = HasCasedLetter(_text) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    /// <summary>True when the query is a whole-name pattern (<c>*</c> or <c>?</c>).</summary>
    public bool IsGlob { get; }

    /// <summary>
    /// Text every match contains: the query itself, or the longest literal run of a pattern ("" when the pattern has
    /// none, e.g. <c>*</c>). Used to prefilter names with one vectorized IndexOf.
    /// </summary>
    public string Literal { get; }

    /// <summary>
    /// The <see cref="Literal"/> of the decomposed (NFD) query when it differs from the composed one: names in that form
    /// need a second prefilter pass; null otherwise.
    /// </summary>
    public string? AlternateLiteral { get; }

    /// <summary>Comparison for <see cref="Literal"/> and <see cref="AlternateLiteral"/>.</summary>
    public StringComparison Comparison { get; }

    /// <summary>True when no filename can match (a path separator or '\0' in the query).</summary>
    public bool MatchesNothing { get; }

    /// <summary>Creates a matcher.</summary>
    /// <param name="text">The query, as typed (not trimmed here).</param>
    /// <returns>The matcher.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null or empty.</exception>
    public static NameMatcher Create(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new NameMatcher(text);
    }

    /// <summary>Whether <paramref name="name"/> matches.</summary>
    /// <param name="name">A file or folder name.</param>
    /// <returns>True on a match.</returns>
    public bool IsMatch(ReadOnlySpan<char> name)
    {
        if (MatchesNothing)
        {
            return false;
        }

        return Matches(_text, name) || (_decomposed is not null && Matches(_decomposed, name));
    }

    private bool Matches(string query, ReadOnlySpan<char> name)
    {
        if (!IsGlob)
        {
            return name.IndexOf(query, Comparison) >= 0;
        }

        bool ignoreCase = Comparison == StringComparison.OrdinalIgnoreCase;
        if (GlobMatch(query, name, ignoreCase))
        {
            return true;
        }

        // "name.*" also matches "name" (no extension), so "*.*" is every name (Explorer, cmd).
        return query.EndsWith(AnyExtension, StringComparison.Ordinal)
            && name.IndexOf('.') < 0
            && GlobMatch(query.AsSpan(0, query.Length - AnyExtension.Length), name, ignoreCase);
    }

    // Two-pointer glob with backtracking to the last '*': O(pattern × name) worst case, no recursion, no allocation.
    private static bool GlobMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> name, bool ignoreCase)
    {
        int p = 0, n = 0, star = -1, resume = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = n;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern.Slice(p, 1), name.Slice(n, 1), ignoreCase)))
            {
                p++;
                n++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    // The same equality as the prefilter's IndexOf (OrdinalIgnoreCase), so index (prefiltered) and live (not) agree.
    private static bool CharEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b, bool ignoreCase)
        => ignoreCase ? a.Equals(b, StringComparison.OrdinalIgnoreCase) : a[0] == b[0];

    private static string LiteralOf(string query, bool glob)
    {
        if (!glob)
        {
            return query;
        }

        // A trailing ".*" may match nothing (see Matches), so its dot is not something every match contains.
        string pattern = query.EndsWith(AnyExtension, StringComparison.Ordinal) ? query[..^AnyExtension.Length] : query;
        int bestStart = 0, bestLength = 0, start = 0;
        for (int i = 0; i <= pattern.Length; i++)
        {
            if (i == pattern.Length || pattern[i] is '*' or '?')
            {
                if (i - start > bestLength)
                {
                    bestStart = start;
                    bestLength = i - start;
                }

                start = i + 1;
            }
        }

        return pattern.Substring(bestStart, bestLength);
    }

    private static string Normalized(string text, NormalizationForm form)
    {
        try
        {
            return text.Normalize(form);
        }
        catch (ArgumentException)
        {
            return text; // invalid UTF-16 (a lone surrogate) cannot be normalized; match it as typed
        }
    }

    private static bool HasCasedLetter(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (char.ToUpperInvariant(c) != char.ToLowerInvariant(c))
            {
                return true;
            }
        }

        return false;
    }
}
