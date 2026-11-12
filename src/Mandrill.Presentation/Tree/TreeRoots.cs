using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Platform;
using Mandrill.Core.Watching;
using Mandrill.Presentation.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Presentation.Tree;

/// <summary>A Quick access root whose folder exists (AC-1.9).</summary>
/// <param name="Folder">Known folder.</param>
/// <param name="Path">Resolved, normalized path (e.g. a redirected OneDrive folder).</param>
public sealed record QuickAccessRoot(KnownFolder Folder, string Path);

/// <summary>A drive root shown in the tree.</summary>
/// <param name="Root">Root path, e.g. "C:\".</param>
/// <param name="Kind">Drive kind.</param>
/// <param name="Label">Volume label of a fixed or removable drive ("" when it has none); null when unknown, not ready or not read (other kinds are never touched).</param>
/// <param name="ShellName">Explorer's name of a labelled drive or of a removable one with media, e.g. from autorun.inf ("Ubuntu 22.04 (E:)"); null when not read.</param>
public sealed record DriveRoot(string Root, DriveKind Kind, string? Label, string? ShellName = null);

/// <summary>
/// Tree roots shared by all panes (AC-1.6, AC-1.9): Quick access entries that exist, then local drives from
/// <see cref="IDriveMonitor"/>. Disk checks (known-folder existence, labels of fixed and removable drives) run on the thread pool
/// at <see cref="RefreshAsync"/> and after every drive change; the lists and <see cref="Changed"/> are updated on the UI
/// thread. Network drives are left out (AC-1.6: local drives).
/// </summary>
public sealed class TreeRoots : IDisposable
{
    private readonly IDriveMonitor _drives;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<IReadOnlyList<(KnownFolder Folder, string Path)>> _quickAccess;
    private readonly Func<string, VolumeInfo?> _volumeInfo;
    private readonly Func<string, CancellationToken, Task<string?>> _shellName;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private long _refreshVersion;
    private bool _disposed;

    /// <summary>Creates the roots. The drive list is available at once (without labels); call <see cref="RefreshAsync"/> once at startup.</summary>
    /// <param name="drives">Drive monitor.</param>
    /// <param name="dispatcher">UI dispatcher.</param>
    /// <param name="quickAccess">Existing Quick access folders; worker thread (default: <see cref="KnownFolders.ExistingQuickAccess"/>).</param>
    /// <param name="volumeInfo">Volume label reader for fixed and removable drives; worker thread (default: <see cref="Mandrill.Core.Platform.Drives.TryGetVolumeInfo"/>).</param>
    /// <param name="shellName">
    /// Explorer's name of a labelled drive root (<see cref="Mandrill.Core.Platform.Drives.TryGetShellDisplayName"/> on a STA);
    /// null reads none, so labelled drives are named from their volume label.
    /// </param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public TreeRoots(
        IDriveMonitor drives,
        IUiDispatcher dispatcher,
        Func<IReadOnlyList<(KnownFolder Folder, string Path)>>? quickAccess = null,
        Func<string, VolumeInfo?>? volumeInfo = null,
        Func<string, CancellationToken, Task<string?>>? shellName = null,
        ILoggerFactory? loggerFactory = null)
    {
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _quickAccess = quickAccess ?? KnownFolders.ExistingQuickAccess;
        _volumeInfo = volumeInfo ?? Mandrill.Core.Platform.Drives.TryGetVolumeInfo;
        _shellName = shellName ?? ((_, _) => Task.FromResult<string?>(null));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<TreeRoots>();
        Drives = [.. LocalDrives(drives.Current).Select(d => new DriveRoot(d.Root, d.Kind, null))];
        _drives.Changed += OnDrivesChanged;
    }

    /// <summary>Raised on the UI thread after <see cref="QuickAccess"/> or <see cref="Drives"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Existing Quick access folders in tree order (empty until the first refresh).</summary>
    public IReadOnlyList<QuickAccessRoot> QuickAccess { get; private set; } = [];

    /// <summary>Local drives in letter order.</summary>
    public IReadOnlyList<DriveRoot> Drives { get; private set; }

    /// <summary>Display name of a Quick access root.</summary>
    /// <param name="folder">Known folder.</param>
    /// <returns>Localized name.</returns>
    public static string NameOf(KnownFolder folder) => Localizer.Current[folder switch
    {
        KnownFolder.Desktop => "tree.quickAccess.desktop",
        KnownFolder.Documents => "tree.quickAccess.documents",
        KnownFolder.Downloads => "tree.quickAccess.downloads",
        KnownFolder.Pictures => "tree.quickAccess.pictures",
        _ => "tree.quickAccess.other",
    }];

    /// <summary>
    /// Display name of a drive: a labelled one as Explorer names it (its Shell name, else "System (C:)"), an unlabelled one
    /// by kind ("Removable Disk (E:)").
    /// </summary>
    /// <param name="drive">Drive root.</param>
    /// <returns>Localized name.</returns>
    public static string NameOf(DriveRoot drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        string letter = drive.Root.TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(drive.Label))
        {
            return !string.IsNullOrWhiteSpace(drive.ShellName)
                ? drive.ShellName
                : Localizer.Current.Format("tree.drive.labeled", ("label", drive.Label), ("drive", letter));
        }

        // A stick with media but no volume label: Explorer's name (an autorun.inf label, else its own kind name). An
        // unlabelled fixed drive keeps the app-language kind name, as before.
        if (drive.Kind == DriveKind.Removable && drive.Label is not null && !string.IsNullOrWhiteSpace(drive.ShellName))
        {
            return drive.ShellName;
        }

        string key = drive.Kind switch
        {
            DriveKind.Fixed => "tree.drive.fixed",
            DriveKind.Removable => "tree.drive.removable",
            DriveKind.CdRom => "tree.drive.cdRom",
            DriveKind.Ram => "tree.drive.ram",
            _ => "tree.drive.other",
        };
        return Localizer.Current.Format(key, ("drive", letter));
    }

    /// <summary>Re-resolves Quick access and the drive labels on the thread pool, then updates the lists on the UI thread.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the update has been posted.</returns>
    public Task RefreshAsync(CancellationToken ct = default) => RefreshCoreAsync(_drives.Current, ct);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _drives.Changed -= OnDrivesChanged;
        _lifetime.Cancel(); // not disposed: a refresh finishing later may still read its token
    }

    private static IEnumerable<DriveDescriptor> LocalDrives(IReadOnlyList<DriveDescriptor> drives)
        => drives.Where(d => d.Kind != DriveKind.Network);

    // Drive monitor callbacks run on a pool thread and must not block.
    private void OnDrivesChanged(IReadOnlyList<DriveDescriptor> drives) => _ = RefreshCoreAsync(drives, _lifetime.Token);

    private Task RefreshCoreAsync(IReadOnlyList<DriveDescriptor> drives, CancellationToken ct)
    {
        long version = Interlocked.Increment(ref _refreshVersion);
        return Task.Run(
            async () =>
            {
                var quickAccess = new List<QuickAccessRoot>();
                try
                {
                    foreach (var (folder, path) in _quickAccess())
                    {
                        if (PathUtil.TryNormalize(path, out string normalized, out _))
                        {
                            quickAccess.Add(new QuickAccessRoot(folder, normalized));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // AC-1.9: a missing or unreadable entry is hidden without an error shown.
                    _logger.LogWarning(ex, "Resolving Quick access folders failed");
                }

                var driveRoots = new List<DriveRoot>();
                foreach (DriveDescriptor d in LocalDrives(drives))
                {
                    // Optical drives stay untouched (a read spins one up); an empty card reader simply has no label.
                    string? label = d.Kind is DriveKind.Fixed or DriveKind.Removable ? ReadLabel(d.Root) : null;
                    // The Shell is asked only when the media is there (label not null): a not-ready drive is never touched again.
                    bool named = !string.IsNullOrWhiteSpace(label) || (label is not null && d.Kind == DriveKind.Removable);
                    string? shellName = named ? await ReadShellNameAsync(d.Root, ct).ConfigureAwait(false) : null;
                    driveRoots.Add(new DriveRoot(d.Root, d.Kind, label, shellName));
                }

                ct.ThrowIfCancellationRequested();
                _dispatcher.Post(() =>
                {
                    if (_disposed || version != Interlocked.Read(ref _refreshVersion))
                    {
                        return; // a newer refresh supersedes this one
                    }

                    QuickAccess = quickAccess;
                    Drives = driveRoots;
                    Changed?.Invoke();
                });
            },
            ct);
    }

    private async Task<string?> ReadShellNameAsync(string root, CancellationToken ct)
    {
        try
        {
            return await _shellName(root, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reading the Shell name of {Root} failed", root);
            return null;
        }
    }

    private string? ReadLabel(string root)
    {
        try
        {
            return _volumeInfo(root)?.Label;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading the volume label of {Root} failed", root);
            return null;
        }
    }
}
