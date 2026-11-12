using System.Runtime.InteropServices.WindowsRuntime;
using Mandrill.Core.Shell;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mandrill.App.Platform;

/// <summary>
/// UI-thread bitmaps for shell icons (architecture §5 Rendering): one <see cref="WriteableBitmap"/> per
/// <see cref="IconKey"/>, shared by every row that shows it, least recently used dropped first.
/// </summary>
public sealed class IconBitmapCache
{
    /// <summary>Default number of bitmaps kept.</summary>
    public const int DefaultCapacity = 2048;

    /// <summary>Default pixel bytes kept (thumbnails are up to 256 KB each, owner #7; the Core cache has the same bound).</summary>
    public const long DefaultMaxBytes = 64L << 20;

    private readonly int _capacity;
    private readonly long _maxBytes;
    private readonly Dictionary<IconKey, LinkedListNode<(IconKey Key, ImageSource Bitmap, int Bytes, byte[] Source)>> _map = [];
    private readonly LinkedList<(IconKey Key, ImageSource Bitmap, int Bytes, byte[] Source)> _lru = new();
    private long _bytes;

    /// <summary>Creates the cache.</summary>
    /// <param name="capacity">Bitmaps kept before the least recently used is dropped.</param>
    /// <param name="maxBytes">Pixel bytes kept before the least recently used is dropped (the newest always stays).</param>
    public IconBitmapCache(int capacity = DefaultCapacity, long maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _capacity = capacity;
        _maxBytes = maxBytes;
    }

    /// <summary>Bitmaps currently cached (tests, diagnostics).</summary>
    public int Count => _map.Count;

    /// <summary>
    /// The bitmap for <paramref name="key"/>, created from <paramref name="image"/> on first use and again when the service
    /// hands out other pixels for the same key (a drive root after another stick went in, M3). UI thread only.
    /// </summary>
    /// <param name="key">Icon key.</param>
    /// <param name="image">Rendered pixels (premultiplied BGRA, as <see cref="WriteableBitmap"/> expects).</param>
    /// <returns>The bitmap, or null when the pixel buffer does not match the size (the row keeps its placeholder).</returns>
    public ImageSource? Get(IconKey key, IconImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || image.Bgra.Length != image.Width * image.Height * 4)
        {
            return null;
        }

        if (_map.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            if (ReferenceEquals(node.Value.Source, image.Bgra))
            {
                _lru.AddFirst(node);
                return node.Value.Bitmap;
            }

            _map.Remove(key);
            _bytes -= node.Value.Bytes;
        }

        var bitmap = new WriteableBitmap(image.Width, image.Height);
        image.Bgra.AsBuffer().CopyTo(bitmap.PixelBuffer);
        bitmap.Invalidate();
        _map[key] = _lru.AddFirst((key, bitmap, image.Bgra.Length, image.Bgra));
        _bytes += image.Bgra.Length;
        while ((_map.Count > _capacity || _bytes > _maxBytes) && _lru.Last is { } last && last != _lru.First)
        {
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
        }

        return bitmap;
    }
}
