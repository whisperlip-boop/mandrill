namespace Mandrill.Core.Index;

/// <summary>Per-item flags of the index (architecture §8.1).</summary>
[Flags]
public enum IndexFlags : byte
{
    /// <summary>No flag: a file.</summary>
    None = 0,

    /// <summary>A directory.</summary>
    Directory = 1,

    /// <summary>A reparse point (junction, symlink): recorded, never descended (AC-2.2, E-2.4).</summary>
    Reparse = 2,

    /// <summary>On the exclude list: recorded, not descended (AC-2.10).</summary>
    Excluded = 4,

    /// <summary>Hidden attribute.</summary>
    Hidden = 8,

    /// <summary>System attribute.</summary>
    System = 16,

    /// <summary>Deleted since the segment was built (search skips it until compaction removes it).</summary>
    Dead = 32,
}
