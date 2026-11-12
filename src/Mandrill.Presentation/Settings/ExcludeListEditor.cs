using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mandrill.Core.Index;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.State;

namespace Mandrill.Presentation.Settings;

/// <summary>
/// The index section of the Settings dialog (AC-2.10, WP-2.6): the excluded folders and the rescan interval. A folder is
/// added only when it is a valid absolute folder on a fixed drive, exists, and is not already covered; every rejection
/// shows a message (never a modal). Saving goes through <see cref="SettingsService"/>, whose Changed event makes the index
/// apply the list at once. UI thread.
/// </summary>
public sealed partial class ExcludeListEditor : ObservableObject
{
    /// <summary>Shortest rescan interval in hours (<see cref="IndexSettings.MinRescanHours"/>).</summary>
    public const int MinRescanHours = IndexSettings.MinRescanHours;

    /// <summary>Longest rescan interval in hours (<see cref="IndexSettings.MaxRescanHours"/>).</summary>
    public const int MaxRescanHours = IndexSettings.MaxRescanHours;

    private readonly Func<string, CancellationToken, Task<ExcludeFolderProblem>> _check;
    private readonly Localizer _localizer;
    private int _rescanHours;

    /// <summary>Creates the editor showing <paramref name="current"/>.</summary>
    /// <param name="current">Index settings in effect.</param>
    /// <param name="check">Disk check of a normalized path (runs off the UI thread; <see cref="ExcludeFolderCheck.Check"/>).</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    public ExcludeListEditor(IndexSettings current, Func<string, CancellationToken, Task<ExcludeFolderProblem>> check, Localizer? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _localizer = localizer ?? Localizer.Current;
        // Stored entries in the form the index uses (a hand-edited "c:/data/" equals "C:\data"); invalid ones, duplicates
        // and drive roots (never excluded by the index; they would mark every add on that drive "covered") are dropped.
        Items = [];
        foreach (string entry in current.Exclude ?? [])
        {
            if (PathUtil.TryNormalize(entry, out string path, out _) && !PathUtil.IsDriveRoot(path) && !Items.Any(i => PathUtil.PathEquals(i, path)))
            {
                Items.Add(path);
            }
        }

        _rescanHours = Math.Clamp(current.RescanHours, MinRescanHours, MaxRescanHours);
    }

    /// <summary>The excluded folders, in the order they were added.</summary>
    public ObservableCollection<string> Items { get; }

    /// <summary>The last message (an added folder or why one was rejected); null when there is none.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>True when <see cref="Message"/> reports a rejection.</summary>
    [ObservableProperty]
    public partial bool MessageIsError { get; private set; }

    /// <summary>Hours between verify passes, clamped to <see cref="MinRescanHours"/>–<see cref="MaxRescanHours"/>.</summary>
    public int RescanHours
    {
        get => _rescanHours;
        set => SetProperty(ref _rescanHours, Math.Clamp(value, MinRescanHours, MaxRescanHours));
    }

    /// <summary>
    /// Validates and adds a folder. A folder that contains listed entries replaces them (they are covered).
    /// </summary>
    /// <param name="text">Typed or picked path.</param>
    /// <param name="typed">True for typed text: environment variables expand as in the address bar; a picked path is taken as is.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the folder was added.</returns>
    public async Task<bool> AddAsync(string? text, bool typed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(_localizer["dialog.settings.index.error.empty"]);
        }

        if (!PathUtil.TryNormalize(text, out string path, out _, expandEnvironment: typed))
        {
            return Reject(_localizer.Format("dialog.settings.index.error.invalid", ("path", text.Trim())));
        }

        if (PathUtil.IsDriveRoot(path))
        {
            return Reject(_localizer.Format("dialog.settings.index.error.driveRoot", ("path", path)));
        }

        if (Precheck(path) is { } listed)
        {
            return Reject(listed);
        }

        ExcludeFolderProblem problem = await _check(path, ct).ConfigureAwait(true);
        switch (problem)
        {
            case ExcludeFolderProblem.NotFixedDrive:
                return Reject(_localizer.Format("dialog.settings.index.error.notFixed", ("path", path)));
            case ExcludeFolderProblem.NotFound:
                return Reject(_localizer.Format("dialog.settings.index.error.notFound", ("path", path)));
        }

        if (Precheck(path) is { } addedMeanwhile)
        {
            return Reject(addedMeanwhile); // another add finished while the disk was checked
        }

        var covered = Items.Where(e => PathUtil.IsSameOrUnder(e, path)).ToList();
        foreach (string entry in covered)
        {
            Items.Remove(entry);
        }

        Items.Add(path);
        MessageIsError = false;
        Message = covered.Count == 0
            ? _localizer.Format("dialog.settings.index.added", ("path", path))
            : _localizer.Plural("dialog.settings.index.addedCovering", covered.Count, ("path", path), ("count", covered.Count));
        return true;
    }

    /// <summary>Removes a folder from the list (the index scans it again after Save).</summary>
    /// <param name="path">A listed folder.</param>
    public void Remove(string path)
    {
        if (Items.FirstOrDefault(e => PathUtil.PathEquals(e, path)) is { } entry)
        {
            Items.Remove(entry);
            Message = null;
        }
    }

    /// <summary>The index settings as edited.</summary>
    /// <returns>The settings.</returns>
    public IndexSettings ToSettings() => new([.. Items], RescanHours);

    // Duplicates and folders already covered by a listed parent; null when neither.
    private string? Precheck(string path)
    {
        foreach (string entry in Items)
        {
            if (PathUtil.PathEquals(entry, path))
            {
                return _localizer.Format("dialog.settings.index.error.duplicate", ("path", entry));
            }

            if (PathUtil.IsSameOrUnder(path, entry))
            {
                return _localizer.Format("dialog.settings.index.error.covered", ("path", path), ("parent", entry));
            }
        }

        return null;
    }

    private bool Reject(string message)
    {
        MessageIsError = true;
        Message = message;
        return false;
    }
}
