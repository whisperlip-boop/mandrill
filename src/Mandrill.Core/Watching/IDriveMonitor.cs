using Mandrill.Core.Platform;

namespace Mandrill.Core.Watching;

/// <summary>Current drive letters and their arrival/removal (architecture §4: polled every 2 s, no media access).</summary>
public interface IDriveMonitor
{
    /// <summary>Drives as of the last poll, in letter order.</summary>
    IReadOnlyList<DriveDescriptor> Current { get; }

    /// <summary>Raised on a thread-pool thread with the new list when a drive was added, removed or changed type.</summary>
    event Action<IReadOnlyList<DriveDescriptor>>? Changed;
}
