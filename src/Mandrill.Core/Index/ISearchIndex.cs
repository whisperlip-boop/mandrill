namespace Mandrill.Core.Index;

/// <summary>
/// What search needs from the index (architecture §3, WP-2.2a): the published drive segments, a pause for the builder
/// while a search walks them, and a way to report a hit whose file no longer exists. Until WP-2.3b the App wires an empty
/// one, so search lists live everywhere.
/// </summary>
public interface ISearchIndex
{
    /// <summary>Published drive segments: an immutable snapshot, swapped atomically.</summary>
    IReadOnlyList<DriveSegment> Segments { get; }

    /// <summary>Pauses the builder at its next checkpoint until the returned handle is disposed (ref-counted).</summary>
    /// <returns>The pause handle.</returns>
    IDisposable PauseForSearch();

    /// <summary>A search found that <paramref name="path"/> no longer exists: the index thread deletes it.</summary>
    /// <param name="path">Full path.</param>
    void ReportMissing(string path);
}
