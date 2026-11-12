namespace Mandrill.Core.State;

/// <summary>Path-box history rules (AC-1.18): most recent first, de-duplicated case-insensitively, at most 20 entries.</summary>
public static class PathHistoryList
{
    /// <summary>Maximum number of entries per pane.</summary>
    public const int MaxEntries = 20;

    /// <summary>Path equality used for de-duplication (Windows paths are case-insensitive).</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Drops empty entries and later duplicates, then keeps the first <see cref="MaxEntries"/>.</summary>
    /// <param name="items">Entries, most recent first (null = none).</param>
    /// <returns>A new normalized list.</returns>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? items)
    {
        var result = new List<string>();
        if (items is null)
        {
            return result;
        }

        var seen = new HashSet<string>(Comparer);
        foreach (string? item in items)
        {
            if (!string.IsNullOrWhiteSpace(item) && seen.Add(item))
            {
                result.Add(item);
                if (result.Count == MaxEntries)
                {
                    break;
                }
            }
        }

        return result;
    }
}
