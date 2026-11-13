using Mandrill.App.Icons;
using Mandrill.App.Platform;
using Mandrill.App.Views;
using Mandrill.Core.Diagnostics;
using Mandrill.Core.Index;
using Mandrill.Core.Localization;
using Mandrill.Core.Shell;
using Mandrill.Core.Update;
using Mandrill.Presentation.About;
using Mandrill.Presentation.Panes;
using Mandrill.Presentation.Settings;
using Mandrill.Presentation.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mandrill.App.Composition;

/// <summary>
/// Opens the Settings and About dialogs when the menu asks (MainViewModel.SettingsRequested/AboutRequested), so the window
/// itself needs no dialog code. One dialog at a time (ContentDialog rule). UI thread.
/// </summary>
public sealed class DialogLauncher
{
    private readonly Window _window;
    private readonly MainViewModel _main;
    private readonly SettingsService _settings;
    private readonly IShellLauncher _launcher;
    private readonly UpdateChecker _updates;
    private readonly AppPaths _paths;
    private readonly IconProvider _icons;
    private readonly Action<string> _showStatus;
    private readonly ILogger _logger;
    private bool _open;

    /// <summary>Subscribes to the menu requests.</summary>
    /// <param name="window">Main window (dialog XamlRoot).</param>
    /// <param name="main">Window view model.</param>
    /// <param name="settings">Settings in effect and their save.</param>
    /// <param name="launcher">Shell launcher ("Open log folder", the release page).</param>
    /// <param name="updates">The About dialog's update check.</param>
    /// <param name="paths">Application paths (log folder).</param>
    /// <param name="icons">Icon provider (layout glyphs).</param>
    /// <param name="showStatus">Status-bar message (non-modal errors, ui-spec §7).</param>
    /// <param name="logger">Logger.</param>
    public DialogLauncher(Window window, MainViewModel main, SettingsService settings, IShellLauncher launcher, UpdateChecker updates, AppPaths paths, IconProvider icons, Action<string> showStatus, ILogger<DialogLauncher> logger)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));
        _showStatus = showStatus ?? throw new ArgumentNullException(nameof(showStatus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        main.SettingsRequested += (_, _) => _ = ShowSettingsAsync();
        main.AboutRequested += (_, _) => _ = ShowAboutAsync();
    }

    private async Task ShowSettingsAsync()
    {
        if (_open || _window.Content?.XamlRoot is not { } root)
        {
            return;
        }

        _open = true;
        try
        {
            // The exclude check touches the disk: Core, on a worker (AC-2.10).
            var index = new ExcludeListEditor(_settings.Current.Index, CheckFolderAsync);
            var dialog = new SettingsDialog(_settings.Current, _icons, index, PickFolderAsync) { XamlRoot = root, RequestedTheme = AppTheme.Of(root) };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary
                && !await _settings.SaveAsync(dialog.ToSettings(), CancellationToken.None))
            {
                _showStatus(Localizer.Current["dialog.settings.error.save"]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settings dialog failed");
            _showStatus(Localizer.Current["error.unexpected"]);
        }
        finally
        {
            _open = false;
        }
    }

    // The dialog's handlers are async void: nothing but a cancellation may escape. A failed check reads as "not found".
    private async Task<ExcludeFolderProblem> CheckFolderAsync(string path, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => ExcludeFolderCheck.Check(path), ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Exclude check failed for {Path}", path);
            return ExcludeFolderProblem.NotFound;
        }
    }

    // WinAppSDK picker (works unpackaged, owned by the main window). Any failure (activation included) is logged and shown,
    // never thrown: the caller is an async void click handler.
    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(_window.AppWindow.Id);
            var result = await picker.PickSingleFolderAsync();
            return result?.Path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Folder picker failed");
            _showStatus(Localizer.Current["error.unexpected"]);
            return null;
        }
    }

    private async Task ShowAboutAsync()
    {
        if (_open || _window.Content?.XamlRoot is not { } root)
        {
            return;
        }

        _open = true;
        try
        {
            ContentDialogResult choice;
            // Disposed when the dialog closes: cancels a running check and drops its late result.
            using (var updates = new UpdateCheckViewModel(AboutDialog.Version, _updates.CheckAsync, ct => _launcher.OpenUrlAsync(UpdateChecker.ReleasePage, ct)))
            {
                var dialog = new AboutDialog(updates, _paths.AppIcon) { XamlRoot = root, RequestedTheme = AppTheme.Of(root) };
                choice = await dialog.ShowAsync();
            }

            if (choice != ContentDialogResult.Secondary)
            {
                return;
            }

            // The launcher answers a folder with NavigateTo: the log folder opens in the active pane.
            var result = await _launcher.OpenAsync(_paths.LogDirectory, CancellationToken.None);
            if (result is { Outcome: OpenOutcome.NavigateTo, Directory: { } dir } && _main.Layout?.ActivePane is { } pane)
            {
                await pane.NavigateAsync(dir, NavOrigin.Open);
            }
            else if (result.Error is not null)
            {
                _showStatus(Localizer.Current.Format("pane.error.open", ("path", _paths.LogDirectory)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "About dialog failed");
            _showStatus(Localizer.Current["error.unexpected"]);
        }
        finally
        {
            _open = false;
        }
    }
}
