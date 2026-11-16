using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Mandrill.Core.Localization;
using Mandrill.Core.State;
using Mandrill.Presentation.Common;
using Mandrill.Presentation.Layout;
using Mandrill.Presentation.Panes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mandrill.Presentation.Favorites;

/// <summary>Result of saving or renaming a favorite.</summary>
public enum FavoriteSaveOutcome
{
    /// <summary>Stored (and written to favorites.json).</summary>
    Saved,

    /// <summary>The trimmed name is empty; nothing changed.</summary>
    EmptyName,

    /// <summary>Another favorite has this name and replacing was not confirmed; nothing changed.</summary>
    Duplicate,

    /// <summary>The favorite to rename no longer exists; nothing changed.</summary>
    NotFound,
}

/// <summary>What "Add favorite" would store, captured from the window (AC-1.28).</summary>
/// <param name="Layout">Layout key.</param>
/// <param name="Panes">Visible panes: folder and tree state, in pane order.</param>
/// <param name="Splitters">Splitter ratios of the layout.</param>
/// <param name="Summary">Localized summary line for the dialog, e.g. "Layout: 2 × 2 · 4 paths".</param>
public sealed record FavoriteDraft(string Layout, IReadOnlyList<FavoritePane> Panes, SplitterState Splitters, string Summary);

/// <summary>A status-bar message from the favorites bar.</summary>
/// <param name="Text">Localized message.</param>
/// <param name="Detail">Tooltip text (e.g. the missing paths, one per line); null when none.</param>
/// <param name="IsError">True for failures.</param>
public sealed record FavoriteStatus(string Text, string? Detail, bool IsError);

/// <summary>
/// The favorites bar (AC-1.28–1.32, ui-spec §4): the saved favorites in bar order, Add/Rename/Delete, and restore. Every
/// change is written at once (AC-1.30) through the save function supplied by the bootstrap; a failed write keeps the
/// change in memory and reports it. UI thread only.
/// </summary>
public sealed partial class FavoritesViewModel : ObservableObject
{
    private readonly LayoutManager _layout;
    private readonly Func<FavoritesDocument, CancellationToken, Task> _save;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;

    // One write at a time, newest list last (AC-1.30): changes in quick succession must not finish out of order.
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _version;
    private long _writtenVersion;

    /// <summary>Creates the view model over the loaded favorites.</summary>
    /// <param name="layout">The window's layout manager (capture and restore).</param>
    /// <param name="document">Loaded, normalized favorites.</param>
    /// <param name="save">Writes favorites.json (Core StateStore); runs on the thread pool.</param>
    /// <param name="dispatcher">UI dispatcher (status messages after background work).</param>
    /// <param name="loggerFactory">Logger factory; null disables logging.</param>
    public FavoritesViewModel(LayoutManager layout, FavoritesDocument document, Func<FavoritesDocument, CancellationToken, Task> save, IUiDispatcher dispatcher, ILoggerFactory? loggerFactory = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentNullException.ThrowIfNull(document);
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<FavoritesViewModel>();
        Items = [.. document.Favorites];
    }

    /// <summary>Raised on the UI thread with a status-bar message (restore with missing paths, write failure).</summary>
    public event Action<FavoriteStatus>? StatusRequested;

    /// <summary>Favorites in bar order.</summary>
    public ObservableCollection<Favorite> Items { get; }

    /// <summary>Settings → "Show favorites bar" (AC-1.34); hides only the favorites part of the top bar.</summary>
    [ObservableProperty]
    public partial bool IsBarVisible { get; set; } = true;

    /// <summary>Tooltip of a favorite button: the full name and its folders, one per line (ui-spec §1).</summary>
    /// <param name="favorite">The favorite.</param>
    /// <returns>The tooltip text.</returns>
    public static string TooltipOf(Favorite favorite)
    {
        ArgumentNullException.ThrowIfNull(favorite);
        return string.Join(Environment.NewLine, favorite.Panes.Select(p => p.Path).Prepend(favorite.Name));
    }

    /// <summary>Whether a favorite named <paramref name="name"/> (trimmed, case-insensitive) exists.</summary>
    /// <param name="name">Name.</param>
    /// <returns>True when it exists.</returns>
    public bool Exists(string? name) => IndexOf(name?.Trim() ?? string.Empty) >= 0;

    /// <summary>The first free default name: "Favorite 1", "Favorite 2", … (ui-spec §4).</summary>
    /// <returns>The name.</returns>
    public string NextDefaultName()
    {
        // Among Count + 1 numbers at least one is free; a translation without {n} falls back to a numbered suffix.
        for (int n = 1; n <= Items.Count + 1; n++)
        {
            string name = DefaultName(n);
            if (!Exists(name))
            {
                return name;
            }
        }

        int next = Items.Count + 1;
        return DefaultName(next) + " " + next.ToString(CultureInfo.CurrentCulture);
    }

    private static string DefaultName(int n) => Localizer.Current.Format("favorite.defaultName", ("n", n.ToString(CultureInfo.CurrentCulture)));

    /// <summary>Captures the window as a favorite: layout, visible panes' folders and tree states, splitter ratios (AC-1.28).</summary>
    /// <returns>The draft with its summary line.</returns>
    public FavoriteDraft CaptureDraft()
    {
        LayoutSnapshot snapshot = _layout.Capture();
        FavoritePane[] panes = [.. snapshot.Panes.Select(p => new FavoritePane(p.Path, p.TreeVisible))];
        string layoutName = Localizer.Current[LayoutCatalog.Get(_layout.Current).TextKey];
        string summary = Localizer.Current.Plural("favorite.summary", panes.Length, ("layout", layoutName));
        return new FavoriteDraft(snapshot.Layout, panes, _layout.GetSplitters(_layout.Current), summary);
    }

    /// <summary>Saves <paramref name="draft"/> as <paramref name="name"/> (AC-1.28). Never overwrites without <paramref name="replace"/>.</summary>
    /// <param name="name">Name (trimmed).</param>
    /// <param name="draft">What to store.</param>
    /// <param name="replace">True when the user confirmed replacing a favorite of the same name (it keeps its bar position).</param>
    /// <param name="ct">Cancellation token for the write.</param>
    /// <returns>The outcome.</returns>
    public async Task<FavoriteSaveOutcome> SaveAsync(string name, FavoriteDraft draft, bool replace, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return FavoriteSaveOutcome.EmptyName;
        }

        var favorite = new Favorite(trimmed, draft.Layout, draft.Panes, draft.Splitters);
        int existing = IndexOf(trimmed);
        if (existing >= 0 && !replace)
        {
            return FavoriteSaveOutcome.Duplicate;
        }

        if (existing >= 0)
        {
            Items[existing] = favorite;
        }
        else
        {
            Items.Add(favorite);
        }

        _logger.LogInformation("Favorite {Name} saved ({Layout}, {Count} panes)", trimmed, draft.Layout, draft.Panes.Count);
        await PersistAsync(ct).ConfigureAwait(false);
        return FavoriteSaveOutcome.Saved;
    }

    /// <summary>Renames a favorite (AC-1.30). Renaming onto another favorite's name replaces that one only when confirmed.</summary>
    /// <param name="oldName">Current name.</param>
    /// <param name="newName">New name (trimmed).</param>
    /// <param name="replace">True when the user confirmed replacing the other favorite.</param>
    /// <param name="ct">Cancellation token for the write.</param>
    /// <returns>The outcome.</returns>
    public async Task<FavoriteSaveOutcome> RenameAsync(string oldName, string newName, bool replace, CancellationToken ct = default)
    {
        int index = IndexOf(oldName?.Trim() ?? string.Empty);
        if (index < 0)
        {
            return FavoriteSaveOutcome.NotFound;
        }

        string trimmed = newName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return FavoriteSaveOutcome.EmptyName;
        }

        int other = IndexOf(trimmed);
        if (other >= 0 && other != index && !replace)
        {
            return FavoriteSaveOutcome.Duplicate;
        }

        Items[index] = Items[index] with { Name = trimmed };
        if (other >= 0 && other != index)
        {
            Items.RemoveAt(other);
        }

        _logger.LogInformation("Favorite renamed to {Name}", trimmed);
        await PersistAsync(ct).ConfigureAwait(false);
        return FavoriteSaveOutcome.Saved;
    }

    /// <summary>
    /// Shows the favorites another instance saved (architecture §18 D3), nothing written: the next change here saves a
    /// list that contains them. UI thread.
    /// </summary>
    /// <param name="document">The reloaded, normalized favorites.</param>
    public void ReplaceAll(FavoritesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Items.Clear();
        foreach (Favorite favorite in document.Favorites)
        {
            Items.Add(favorite);
        }

        _logger.LogInformation("Favorites changed by another instance: {Count}", Items.Count);
    }

    /// <summary>Deletes a favorite (AC-1.30; the view asked for confirmation).</summary>
    /// <param name="name">Name.</param>
    /// <param name="ct">Cancellation token for the write.</param>
    /// <returns>True when it existed.</returns>
    public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        int index = IndexOf(name?.Trim() ?? string.Empty);
        if (index < 0)
        {
            return false;
        }

        Items.RemoveAt(index);
        _logger.LogInformation("Favorite deleted");
        await PersistAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Restores a favorite without confirmation (AC-1.29). Folders that no longer exist open their nearest existing
    /// ancestor (else the home folder) and one status message names them (AC-1.31); the favorite is kept.
    /// </summary>
    /// <param name="name">Name.</param>
    /// <param name="ct">Stops waiting for the panes; the navigations continue.</param>
    /// <returns>The paths that no longer exist (empty when all opened); null when the favorite does not exist.</returns>
    public async Task<IReadOnlyList<string>?> RestoreAsync(string name, CancellationToken ct = default)
    {
        int index = IndexOf(name?.Trim() ?? string.Empty);
        if (index < 0)
        {
            return null;
        }

        Favorite favorite = Items[index];
        _logger.LogInformation("Restoring favorite {Name}", favorite.Name);
        NavigationResult[] results = await _layout.ApplyFavoriteAsync(favorite.Layout, favorite.Splitters, favorite.Panes, ct).ConfigureAwait(false);
        var missing = new List<string>();
        for (int i = 0; i < results.Length && i < favorite.Panes.Count; i++)
        {
            NavigationResult r = results[i];
            if (r.Outcome == NavigationOutcome.Failed || (r.Outcome == NavigationOutcome.Committed && r.RedirectedFrom is not null))
            {
                missing.Add(favorite.Panes[i].Path);
            }
        }

        if (missing.Count > 0)
        {
            _logger.LogInformation("Favorite {Name}: {Count} paths no longer exist", favorite.Name, missing.Count);
            string text = Localizer.Current.Plural("favorite.restore.missing", missing.Count, ("name", favorite.Name));
            Report(new FavoriteStatus(text, string.Join(Environment.NewLine, missing), IsError: false));
        }

        return missing;
    }

    private int IndexOf(string name)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            if (Favorite.NameComparer.Equals(Items[i].Name, name))
            {
                return i;
            }
        }

        return -1;
    }

    // Writes the current list off the UI thread, one write at a time; a write overtaken by a newer list is skipped (the
    // newer one is written after it). A failure keeps the change in memory and tells the user (lead Q4).
    private async Task PersistAsync(CancellationToken ct)
    {
        var document = new FavoritesDocument([.. Items]);
        long version = Interlocked.Increment(ref _version);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (version <= Interlocked.Read(ref _writtenVersion) || version < Interlocked.Read(ref _version))
            {
                return; // a newer list is (or will be) written
            }

            await Task.Run(() => _save(document, ct), ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _writtenVersion, version);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Writing favorites failed");
            Report(new FavoriteStatus(Localizer.Current["favorite.error.save"], null, IsError: true));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Report(FavoriteStatus status) => _dispatcher.Post(() => StatusRequested?.Invoke(status));
}
