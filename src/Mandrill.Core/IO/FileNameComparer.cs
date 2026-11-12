using Windows.Win32;

namespace Mandrill.Core.IO;

/// <summary>
/// Name order for lists and the tree (AC-1.22): exactly Explorer's order, via shlwapi StrCmpLogicalW (natural numbers,
/// case-insensitive, Explorer's placement of punctuation and of Latin before Hangul/CJK). Names Explorer considers equal
/// ("File02" / "file02") get an ordinal tie-break so the order is total and stable. Independent of the .NET globalization
/// mode (WP-1.4 measured ICU ko-KR disagreeing with Explorer on 243 of 861 pairs). Pure and thread-safe.
/// </summary>
public sealed class FileNameComparer : IComparer<string>, IComparer<FileEntry>
{
    /// <summary>The shared instance (stateless).</summary>
    public static FileNameComparer Instance { get; } = new();

    /// <summary>Compares two names.</summary>
    /// <param name="x">First name.</param>
    /// <param name="y">Second name.</param>
    /// <returns>Sort order.</returns>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int c = PInvoke.StrCmpLogical(x, y);
        return c != 0 ? c : string.CompareOrdinal(x, y);
    }

    /// <summary>Folders first, then by name.</summary>
    /// <param name="x">First entry.</param>
    /// <param name="y">Second entry.</param>
    /// <returns>Sort order.</returns>
    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (x.IsDirectory != y.IsDirectory)
        {
            return x.IsDirectory ? -1 : 1;
        }

        return Compare(x.Name, y.Name);
    }
}
