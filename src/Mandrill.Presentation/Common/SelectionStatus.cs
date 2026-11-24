using Mandrill.Core.Platform;
using Mandrill.Core.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Presentation.Common;

/// <summary>
/// The selection part of a status bar, shared by the panes and the search popup: <see cref="Summary"/> ("N selected
/// (size)", plus the drive's free space for two or more items, owner WP4) and, for exactly one item, its Shell info tip
/// on one line (<see cref="Tip"/>, owner WP3). Both extras arrive later; a new selection cancels or outdates the previous
/// read and a late result for an older selection is dropped (a request id for tips, the shown folder for free space). The
/// tip text is never logged or stored. UI thread.
/// </summary>
public sealed class SelectionStatus : IDisposable
{
    private static readonly char[] TipBreaks = ['\r', '\n', '\t'];

    // While the selection stays at 2+ in one folder, a selection change re-reads the free space at most this often: writes
    // elsewhere on the drive show up, and a Ctrl+A burst or a dead share still costs one read (code review M1, m4).
    private const long SpaceMaxAgeMs = 2000;

    private readonly IInfoTipService? _tips;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Func<string, (long Total, long Free)?> _space;
    private CancellationTokenSource? _tipCts;
    private string? _tipPath;
    private int _tipRequest;
    private string? _spacePath;
    private long? _free;
    private string? _spaceReading; // folder of the read in flight: no second read starts for it (QA M1, a dead share blocks ~5.5 s)
    private long _spaceReadAt;
    private string? _spaceWarned; // the folder whose unreadable free space was last logged at Warning
    private int _count;
    private long _bytes;
    private bool _disposed;

    /// <summary>Creates the status.</summary>
    /// <param name="tips">Shell info tips; null shows none.</param>
    /// <param name="dispatcher">UI dispatcher (tips are read on a Shell STA, free space on the thread pool).</param>
    /// <param name="logger">Logs a free space that could not be read; null disables logging.</param>
    /// <param name="space">Reads a drive's space (worker thread); null for <see cref="Drives.TryGetSpace"/>.</param>
    public SelectionStatus(IInfoTipService? tips, IUiDispatcher dispatcher, ILogger? logger = null, Func<string, (long Total, long Free)?>? space = null)
    {
        _tips = tips;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? NullLogger.Instance;
        _space = space ?? Drives.TryGetSpace;
    }

    /// <summary>Raised when a tip or the free space arrived; the view re-reads both texts.</summary>
    public event Action? Changed;

    /// <summary>"N selected (size)", with the free space for two or more items once known; "" with no selection.</summary>
    public string Summary => SelectionText.Summary(_count, _bytes, _count >= 2 ? _free : null);

    /// <summary>The single selected item's info tip on one line, or "" (none yet, none at all, or not one item).</summary>
    public string Tip { get; private set; } = string.Empty;

    /// <summary>
    /// Sets the selection; cheap to repeat. The tip is read once per item; the free space when the selection grows to two
    /// or more items or its folder changes, and again on a later change once the last read is 2 s old.
    /// </summary>
    /// <param name="count">Selected items.</param>
    /// <param name="bytes">Their total size (folders count 0).</param>
    /// <param name="singlePath">Full path of the item when exactly one is selected, else null.</param>
    /// <param name="folder">The folder whose drive's free space is shown (pane folder, search scope); null for none.</param>
    public void Update(int count, long bytes, string? singlePath, string? folder)
    {
        _count = count;
        _bytes = bytes;
        UpdateTip(count == 1 ? singlePath : null);
        UpdateSpace(count >= 2 ? folder : null);
    }

    /// <summary>One line: the handler's line breaks and tabs become single spaces.</summary>
    /// <param name="tip">Tip as the handler returned it.</param>
    /// <returns>The flattened tip, "" for null.</returns>
    public static string Flatten(string? tip)
        => tip is null ? string.Empty : string.Join(' ', tip.Split(TipBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Cancels a pending tip read and drops a pending free space.</summary>
    public void Dispose()
    {
        _disposed = true;
        _tipRequest++;
        _tipCts?.Cancel();
        _tipCts?.Dispose();
        _tipCts = null;
    }

    private void UpdateTip(string? path)
    {
        // shortcut: keyed by path only, so a file written while selected keeps its first tip (default tips carry size and
        // date), and a read failed behind a hung handler is not retried for the item still selected; reselecting reads again.
        if (string.Equals(path, _tipPath, StringComparison.Ordinal))
        {
            return;
        }

        _tipCts?.Cancel();
        _tipCts?.Dispose();
        _tipCts = null;
        _tipPath = path;
        Tip = string.Empty;
        int request = ++_tipRequest;
        if (path is not null && _tips is not null && !_disposed)
        {
            _tipCts = new CancellationTokenSource();
            _ = ReadTipAsync(_tips, path, request, _tipCts.Token);
        }
    }

    private void UpdateSpace(string? folder)
    {
        bool sameFolder = string.Equals(folder, _spacePath, StringComparison.OrdinalIgnoreCase);
        if (!sameFolder)
        {
            _spacePath = folder;
            _free = null; // a re-read of the same folder keeps the old figure on screen until the new one lands
        }

        // shortcut: one in-flight folder, so A pending -> B -> back to A starts a second read for A; fine for a status bar.
        if (folder is null || _disposed
            || string.Equals(folder, _spaceReading, StringComparison.OrdinalIgnoreCase)
            || (sameFolder && Environment.TickCount64 - _spaceReadAt < SpaceMaxAgeMs))
        {
            return;
        }

        _spaceReading = folder;
        _spaceReadAt = Environment.TickCount64;
        // The folder, not its root: GetDiskFreeSpaceEx then follows mount points and takes UNC shares as they are.
        _ = Task.Run(() => ReadSpace(folder));
    }

    private async Task ReadTipAsync(IInfoTipService tips, string path, int request, CancellationToken ct)
    {
        string? tip;
        try
        {
            tip = await tips.GetInfoTipAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // the selection moved on
        }

        string line = Flatten(tip);
        _dispatcher.Post(() =>
        {
            if (request != _tipRequest || line.Length == 0)
            {
                return; // a newer selection (or disposal) superseded this read
            }

            Tip = line;
            Changed?.Invoke();
        });
    }

    // Pool thread: a disconnected network drive may block here. The result counts only if its folder is still the one shown
    // (any selection of 2+ there, also after 2+ -> 1 -> 2+); a folder change or a selection below 2 drops it.
    private void ReadSpace(string folder)
    {
        long? free = null;
        Exception? error = null;
        try
        {
            free = _space(folder)?.Free;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // Posted on failure too: a re-read that fails must not leave the older figure up.
        _dispatcher.Post(() =>
        {
            if (string.Equals(folder, _spaceReading, StringComparison.OrdinalIgnoreCase))
            {
                _spaceReading = null;
            }

            LogSpaceResult(folder, free, error);

            if (_disposed || !string.Equals(folder, _spacePath, StringComparison.OrdinalIgnoreCase) || free == _free)
            {
                return;
            }

            _free = free;
            Changed?.Invoke();
        });
    }

    // One Warning per unreadable folder until a read of it succeeds or another folder fails, then Debug, so a dead share
    // cannot fill the log (QA m1). UI thread.
    private void LogSpaceResult(string folder, long? free, Exception? error)
    {
        bool known = string.Equals(folder, _spaceWarned, StringComparison.OrdinalIgnoreCase);
        if (free is not null)
        {
            if (known)
            {
                _spaceWarned = null;
            }
        }
        else if (known)
        {
            _logger.LogDebug(error, "Free space of {Folder} is still not available", folder);
        }
        else
        {
            _spaceWarned = folder;
            _logger.LogWarning(error, "Free space of {Folder} is not available", folder);
        }
    }
}
