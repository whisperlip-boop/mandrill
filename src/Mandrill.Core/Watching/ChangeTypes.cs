namespace Mandrill.Core.Watching;

/// <summary>Kind of a raw file-system change.</summary>
public enum ChangeKind
{
    /// <summary>A file or folder was created.</summary>
    Created,

    /// <summary>A file or folder was deleted.</summary>
    Deleted,

    /// <summary>Contents, size or last-write time changed.</summary>
    Changed,

    /// <summary>A file or folder was renamed (<see cref="RawChange.OldFullPath"/> holds the old path).</summary>
    Renamed,
}

/// <summary>One change as reported by the drive-root watcher.</summary>
/// <param name="Kind">Change kind.</param>
/// <param name="FullPath">Affected path (the new path for a rename).</param>
/// <param name="OldFullPath">Old path for <see cref="ChangeKind.Renamed"/>; otherwise null.</param>
public sealed record RawChange(ChangeKind Kind, string FullPath, string? OldFullPath = null);

/// <summary>Coalesced change notification for one watched folder (architecture §7).</summary>
/// <param name="Dir">Watched folder.</param>
/// <param name="Names">Changed child names (≤200); null = re-list the whole folder.</param>
/// <param name="Gone">The folder or one of its ancestors was deleted or renamed away.</param>
public sealed record DirChange(string Dir, IReadOnlyCollection<string>? Names, bool Gone);
