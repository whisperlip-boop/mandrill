using System.Diagnostics.CodeAnalysis;
using Mandrill.Core.IO;

namespace Mandrill.Core.Scanning;

/// <summary>A full (unfiltered) listing kept for instant re-display (AC-1.13).</summary>
/// <param name="Path">Normalized folder path.</param>
/// <param name="Entries">All entries, including hidden ones; never mutated.</param>
/// <param name="ListedUtc">When the listing completed.</param>
public sealed record CachedListing(string Path, FileEntry[] Entries, DateTime ListedUtc);

/// <summary>
/// Thread-safe LRU of recent folder listings, keyed with <see cref="PathUtil.Comparer"/>. Bounded by folder count and by
/// total entries; a listing larger than the entry budget is not cached. Entries are shared, immutable objects.
/// </summary>
public sealed class DirectoryCache
{
    /// <summary>Default maximum number of folders.</summary>
    public const int DefaultMaxFolders = 64;

    /// <summary>Default maximum number of entries over all folders.</summary>
    public const int DefaultMaxEntries = 500_000;

    private readonly int _maxFolders;
    private readonly int _maxEntries;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, CachedListing Listing)>> _map = new(PathUtil.Comparer);
    private readonly LinkedList<(string Key, CachedListing Listing)> _lru = new();
    private int _entries;

    /// <summary>Creates a cache.</summary>
    /// <param name="maxFolders">Folder limit.</param>
    /// <param name="maxEntries">Total entry limit.</param>
    public DirectoryCache(int maxFolders = DefaultMaxFolders, int maxEntries = DefaultMaxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFolders, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _maxFolders = maxFolders;
        _maxEntries = maxEntries;
    }

    /// <summary>Number of cached folders.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>Total cached entries.</summary>
    public int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _entries;
            }
        }
    }

    /// <summary>Looks up a folder and marks it most recently used.</summary>
    /// <param name="path">Normalized folder path.</param>
    /// <param name="listing">The cached listing when found.</param>
    /// <returns>True when found.</returns>
    public bool TryGet(string path, [NotNullWhen(true)] out CachedListing? listing)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(path, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                listing = node.Value.Listing;
                return true;
            }
        }

        listing = null;
        return false;
    }

    /// <summary>Stores or replaces a listing, evicting least recently used folders to stay within the limits.</summary>
    /// <param name="path">Normalized folder path.</param>
    /// <param name="listing">The listing.</param>
    public void Put(string path, CachedListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        lock (_gate)
        {
            RemoveLocked(path);
            if (listing.Entries.Length > _maxEntries)
            {
                return;
            }

            _map[path] = _lru.AddFirst((path, listing));
            _entries += listing.Entries.Length;
            while (_map.Count > _maxFolders || _entries > _maxEntries)
            {
                // Evict by the node's own key, which is exactly the map key.
                RemoveLocked(_lru.Last!.Value.Key);
            }
        }
    }

    /// <summary>Removes a folder (e.g. it was deleted).</summary>
    /// <param name="path">Normalized folder path.</param>
    public void Remove(string path)
    {
        lock (_gate)
        {
            RemoveLocked(path);
        }
    }

    private void RemoveLocked(string path)
    {
        if (_map.Remove(path, out var node))
        {
            _lru.Remove(node);
            _entries -= node.Value.Listing.Entries.Length;
        }
    }
}
