using Mandrill.App.Platform;
using Mandrill.Core.IO;
using Mandrill.Core.Shell;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Mandrill.App.Views;

/// <summary>
/// The realized rows of a virtualized list and their shell icons (file list, search results; extracted in WP-2.2b).
/// Rows are filled in a handled ContainerContentChanging, so the control never sets a row's Content: the binder keeps
/// row → item. For each row it remembers the icon key it waits for and, while that icon is missing or failed, the keys
/// of its placeholders: the generic icon of the type for per-item icons (desktop.ini folder, .exe, .lnk), then the
/// unknown-file icon for a file type seen for the first time (owner F2). Placeholders are requested after the row's own
/// key, so the icon service's LIFO queue resolves them first. UI thread only.
/// </summary>
/// <typeparam name="TItem">The row item (a file entry, a search hit).</typeparam>
public sealed class RowIconBinder<TItem>
    where TItem : class
{
    private readonly IIconService _icons;
    private readonly IconBitmapCache _bitmaps;
    private readonly Dictionary<SelectorItem, TItem> _items = [];
    private readonly Dictionary<SelectorItem, (IconKey Key, IconKey? Generic, IconKey? Baseline)> _keys = [];
    private readonly Dictionary<IconKey, HashSet<SelectorItem>> _rowsByKey = [];

    /// <summary>Creates the binder.</summary>
    /// <param name="icons">Shell icons.</param>
    /// <param name="bitmaps">UI-thread icon bitmaps.</param>
    public RowIconBinder(IIconService icons, IconBitmapCache bitmaps)
    {
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _bitmaps = bitmaps ?? throw new ArgumentNullException(nameof(bitmaps));
    }

    /// <summary>Rows that asked for an icon (selection brushes, refreshes). A live view: do not resolve, track or forget while enumerating.</summary>
    public IEnumerable<SelectorItem> Rows => _keys.Keys;

    /// <summary>Remembers the item a realized row shows.</summary>
    /// <param name="row">The row.</param>
    /// <param name="item">Its item.</param>
    public void Track(SelectorItem row, TItem item) => _items[row] = item;

    /// <summary>
    /// A row went to the recycle queue: forget its item and the icons it waited for. Its own per-item icon (thumbnail,
    /// .exe) is dropped from the icon queue when no other row of this list waits for it (another list waiting for it
    /// requests it again, see <see cref="IIconService.Cancel"/>).
    /// </summary>
    /// <param name="row">The row.</param>
    public void Forget(SelectorItem row)
    {
        Unregister(row);
        _items.Remove(row);
    }

    /// <summary>Forgets every row (the list control was replaced) and drops the per-item icons they waited for.</summary>
    public void Clear()
    {
        foreach (var key in _rowsByKey.Keys)
        {
            if (key.UniquePath is not null)
            {
                _icons.Cancel(key);
            }
        }

        _items.Clear();
        _keys.Clear();
        _rowsByKey.Clear();
    }

    /// <summary>The item a realized row shows.</summary>
    /// <param name="row">The row.</param>
    /// <param name="item">The item.</param>
    /// <returns>True when the row is tracked.</returns>
    public bool TryGetItem(SelectorItem row, out TItem item)
    {
        if (_items.TryGetValue(row, out var found))
        {
            item = found;
            return true;
        }

        item = null!;
        return false;
    }

    /// <summary>The icon key a row waits for (visible-range priority).</summary>
    /// <param name="row">The row.</param>
    /// <param name="key">Its key.</param>
    /// <returns>True when the row asked for an icon.</returns>
    public bool TryGetKey(SelectorItem row, out IconKey key)
    {
        if (_keys.TryGetValue(row, out var keys))
        {
            key = keys.Key;
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>
    /// Looks up the icon of <paramref name="entry"/> for <paramref name="row"/> and registers the row for the keys it
    /// waits for. Returns the row's own icon, a placeholder image while it is missing, or null (show a glyph) when even
    /// the placeholders are not there yet.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <param name="entry">The entry shown (search hits are mapped to entries).</param>
    /// <param name="dir">Folder that contains the entry.</param>
    /// <param name="size">Image list.</param>
    /// <returns>The bitmap, or null.</returns>
    public ImageSource? Resolve(SelectorItem row, FileEntry entry, string dir, IconSize size)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(entry);
        bool resolved = _icons.TryGet(entry, dir, size, out var key, out var image);
        IconKey shownKey = key;
        IconKey? generic = null;
        IconKey? baseline = null;
        if (!resolved || image is null)
        {
            generic = key.UniquePath is not null ? key.Generic : null;
            baseline = !entry.IsDirectory && key.ExtId != 0 ? new IconKey(0, null, key.Size) : null;
            if (!TryPlaceholder(generic, ref image, ref shownKey))
            {
                TryPlaceholder(baseline, ref image, ref shownKey);
            }
        }

        Register(row, key, generic, baseline);
        return image is not null ? _bitmaps.Get(shownKey, image) : null;
    }

    /// <summary>The rows waiting for any of <paramref name="keys"/> (each once), to refresh after IconsReady.</summary>
    /// <param name="keys">Keys that were resolved.</param>
    /// <returns>The rows.</returns>
    public IReadOnlyList<SelectorItem> RowsWaitingFor(IReadOnlyList<IconKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var rows = new List<SelectorItem>();
        var seen = new HashSet<SelectorItem>();
        foreach (var key in keys)
        {
            if (_rowsByKey.TryGetValue(key, out var set))
            {
                foreach (var row in set)
                {
                    if (seen.Add(row))
                    {
                        rows.Add(row);
                    }
                }
            }
        }

        return rows;
    }

    // A placeholder image for a row whose own icon is not there; false when the placeholder is not resolved either.
    private bool TryPlaceholder(IconKey? placeholder, ref IconImage? image, ref IconKey shownKey)
    {
        if (placeholder is not { } p || !_icons.TryGet(p, out var placeholderImage) || placeholderImage is null)
        {
            return false;
        }

        image = placeholderImage;
        shownKey = p;
        return true;
    }

    private void Register(SelectorItem row, IconKey key, IconKey? generic, IconKey? baseline)
    {
        Unregister(row, keep: key);
        _keys[row] = (key, generic, baseline);
        Index(row, key);
        if (generic is { } g)
        {
            Index(row, g);
        }

        if (baseline is { } b)
        {
            Index(row, b);
        }
    }

    private void Index(SelectorItem row, IconKey key)
    {
        if (!_rowsByKey.TryGetValue(key, out var set))
        {
            set = [];
            _rowsByKey[key] = set;
        }

        set.Add(row);
    }

    // Drops the row's own per-item key from the icon queue when no row waits for it any more; not "keep",
    // which the row requested again (cancelling it would report it ready and refresh the row in a loop).
    private void Unregister(SelectorItem row, IconKey? keep = null)
    {
        if (_keys.Remove(row, out var old))
        {
            Unindex(row, old.Key);
            if (old.Generic is { } g)
            {
                Unindex(row, g);
            }

            if (old.Baseline is { } b)
            {
                Unindex(row, b);
            }

            if (old.Key.UniquePath is not null && old.Key != keep && !_rowsByKey.ContainsKey(old.Key))
            {
                _icons.Cancel(old.Key);
            }
        }
    }

    private void Unindex(SelectorItem row, IconKey key)
    {
        if (_rowsByKey.TryGetValue(key, out var set))
        {
            set.Remove(row);
            if (set.Count == 0)
            {
                _rowsByKey.Remove(key);
            }
        }
    }
}
