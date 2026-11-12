using System.Collections.ObjectModel;
using System.ComponentModel;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Tree;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Presentation.Panes;

/// <summary>One drive row of the This PC list (ADR-0015).</summary>
/// <param name="Root">Drive root, e.g. "C:\".</param>
/// <param name="Kind">Drive kind.</param>
/// <param name="Name">Display name, as in the tree (<see cref="TreeRoots.NameOf(DriveRoot)"/>).</param>
/// <param name="Type">Localized drive type ("Local Disk").</param>
/// <param name="TotalBytes">Total size; null while unknown or when the drive is not ready.</param>
/// <param name="FreeBytes">Free space for the user; null while unknown or when the drive is not ready.</param>
public sealed record DriveItem(string Root, DriveKind Kind, string Name, string Type, long? TotalBytes, long? FreeBytes);

/// <summary>
/// The drive list a pane shows at This PC (ADR-0015, PC2): the tree's local drives (<see cref="TreeRoots.Drives"/>) in
/// letter order, with total and free space read per drive on the thread pool. Reloads on every This PC commit and on drive
/// changes while shown; a size that arrives after a reload or after the pane left This PC is dropped. UI thread.
/// </summary>
public sealed class DriveListViewModel : IDisposable
{
    private readonly PaneViewModel _pane;
    private readonly TreeRoots _roots;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<string, (long Total, long Free)?> _space;
    private readonly ILogger _logger;
    private long _version;
    private bool _disposed;

    /// <summary>Creates the list of <paramref name="pane"/>.</summary>
    /// <param name="pane">The pane.</param>
    /// <param name="roots">Shared tree roots (names and the drive list).</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    /// <param name="space">Size reader; worker thread (default: <see cref="Drives.TryGetSpace"/>).</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public DriveListViewModel(PaneViewModel pane, TreeRoots roots, IUiDispatcher dispatcher, Func<string, (long Total, long Free)?>? space = null, ILoggerFactory? loggerFactory = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _space = space ?? Drives.TryGetSpace;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DriveListViewModel>();
        _pane.ThisPcShown += OnThisPcShown;
        _pane.PropertyChanged += OnPaneChanged;
        _roots.Changed += OnRootsChanged;

        // CF-1: a pane restored or added at This PC committed before its view (and this list) existed.
        if (_pane.IsThisPc)
        {
            Reload();
        }
    }

    /// <summary>Drive rows in letter order; a row is replaced when its sizes arrive.</summary>
    public ObservableCollection<DriveItem> Items { get; } = [];

    /// <summary>A drive root to select and focus (Up from that drive). UI thread.</summary>
    public event Action<string>? SelectionRequested;

    /// <summary>Localized type of a drive kind ("Local Disk").</summary>
    /// <param name="kind">Drive kind.</param>
    /// <returns>The type text.</returns>
    public static string TypeOf(DriveKind kind) => Localizer.Current[kind switch
    {
        DriveKind.Fixed => "thispc.type.fixed",
        DriveKind.Removable => "thispc.type.removable",
        DriveKind.CdRom => "thispc.type.cdRom",
        DriveKind.Ram => "thispc.type.ram",
        _ => "thispc.type.other",
    }];

    /// <summary>
    /// Opens a drive in the pane (double-click, Enter, the menu's Open). A drive that fails (not ready: an empty card
    /// reader) leaves the pane on This PC with the error in the status bar (CF-2, §7 Leave); no fallback to an ancestor.
    /// </summary>
    /// <param name="drive">A row of <see cref="Items"/>.</param>
    /// <returns>The navigation.</returns>
    public Task<NavigationResult> OpenAsync(DriveItem drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        return _pane.NavigateAndSelectAsync(drive.Root, null);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _version++;
        _pane.ThisPcShown -= OnThisPcShown;
        _pane.PropertyChanged -= OnPaneChanged;
        _roots.Changed -= OnRootsChanged;
    }

    private void OnThisPcShown(string? select)
    {
        Reload();
        if (select is not null)
        {
            SelectionRequested?.Invoke(select);
        }
    }

    // Leaving This PC drops the sizes still on their way.
    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.IsThisPc) && !_pane.IsThisPc)
        {
            _version++;
        }
    }

    private void OnRootsChanged()
    {
        if (_pane.IsThisPc)
        {
            Reload();
        }
    }

    private void Reload()
    {
        if (_disposed)
        {
            return;
        }

        long version = ++_version;

        // A row keeps its last sizes until the new read lands (no blink when a label or Shell name arrives).
        var rows = new List<DriveItem>();
        foreach (DriveRoot d in _roots.Drives)
        {
            DriveItem? old = Find(d.Root) is int at and >= 0 ? Items[at] : null;
            rows.Add(new DriveItem(d.Root, d.Kind, TreeRoots.NameOf(d), TypeOf(d.Kind), old?.TotalBytes, old?.FreeBytes));
        }

        // In place, so a row that stays keeps its container (and the view its selection).
        for (int i = 0; i < rows.Count; i++)
        {
            if (i < Items.Count)
            {
                Items[i] = rows[i];
            }
            else
            {
                Items.Add(rows[i]);
            }
        }

        while (Items.Count > rows.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        // Optical drives too, unlike TreeRoots' labels: Explorer shows a disc's size here, the read runs only while This PC
        // is shown, on its own pool task, so a spinning-up drive delays only its own row.
        foreach (DriveItem row in rows)
        {
            _ = Task.Run(() => ReadSpace(row.Root, version));
        }
    }

    private int Find(string root)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (string.Equals(Items[i].Root, root, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Pool thread: one drive; a slow or not-ready drive delays or blanks only its own row.
    private void ReadSpace(string root, long version)
    {
        (long Total, long Free)? space = null;
        try
        {
            space = _space(root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the size of {Root} failed", root);
        }

        // Posted even when null: the sizes a reload carried over must go blank once the media is gone (cr R2-1).
        _dispatcher.Post(() =>
        {
            if (_disposed || version != _version)
            {
                return; // reloaded or left This PC meanwhile
            }

            int i = Find(root);
            if (i >= 0 && (Items[i].TotalBytes != space?.Total || Items[i].FreeBytes != space?.Free))
            {
                Items[i] = Items[i] with { TotalBytes = space?.Total, FreeBytes = space?.Free };
            }
        });
    }
}
