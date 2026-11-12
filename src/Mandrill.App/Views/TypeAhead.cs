using Mandrill.Core.IO;

namespace Mandrill.App.Views;

/// <summary>
/// Type-ahead in the file list (ui-spec §2): characters typed within 1 s form a prefix; the first matching name from the
/// top is selected. Repeating one character cycles through the names starting with it (Explorer behaviour).
/// </summary>
public sealed class TypeAhead
{
    /// <summary>Time after the last character that ends the prefix.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private string _prefix = string.Empty;
    private long _lastMs = long.MinValue / 2;

    /// <summary>True while a prefix is being typed (Backspace must not navigate back meanwhile, ui-spec §5).</summary>
    /// <param name="nowMs">Current time in milliseconds.</param>
    /// <returns>True within <see cref="Window"/> of the last character.</returns>
    public bool IsActive(long nowMs) => nowMs - _lastMs < (long)Window.TotalMilliseconds;

    /// <summary>Adds <paramref name="c"/> and finds the entry to select.</summary>
    /// <param name="c">Typed character.</param>
    /// <param name="nowMs">Current time in milliseconds.</param>
    /// <param name="items">Shown entries in view order.</param>
    /// <param name="current">Index of the focused entry, or -1.</param>
    /// <returns>The index to select, or -1 when nothing matches.</returns>
    public int Next(char c, long nowMs, IReadOnlyList<FileEntry> items, int current)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (char.IsControl(c))
        {
            return -1;
        }

        bool continuing = IsActive(nowMs);
        if (!continuing && c == ' ')
        {
            return -1; // Explorer ignores a leading space (the list uses Space for selection)
        }

        _lastMs = nowMs;
        _prefix = continuing ? _prefix + c : c.ToString();

        // One character, or the same one repeated ("aaa"), cycles through the names starting with it from the focused entry.
        if (_prefix.All(ch => char.ToUpperInvariant(ch) == char.ToUpperInvariant(_prefix[0])))
        {
            string single = _prefix[..1];
            return Find(items, single, IsMatch(items, current, single) ? current + 1 : 0);
        }

        return Find(items, _prefix, 0);
    }

    /// <summary>Forgets the prefix (navigation, focus loss).</summary>
    public void Reset()
    {
        _prefix = string.Empty;
        _lastMs = long.MinValue / 2;
    }

    private static bool IsMatch(IReadOnlyList<FileEntry> items, int index, string prefix)
        => (uint)index < (uint)items.Count && items[index].Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase);

    private static int Find(IReadOnlyList<FileEntry> items, string prefix, int start)
    {
        for (int i = 0; i < items.Count; i++)
        {
            int k = (start + i) % items.Count;
            if (items[k].Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return k;
            }
        }

        return -1;
    }
}
