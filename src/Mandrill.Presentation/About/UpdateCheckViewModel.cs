using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mandrill.Core.Localization;
using Mandrill.Core.Update;

namespace Mandrill.Presentation.About;

/// <summary>State of the About dialog's update check (architecture §16.4).</summary>
public enum UpdateCheckState
{
    /// <summary>No check yet.</summary>
    Idle,

    /// <summary>A request is in flight.</summary>
    Checking,

    /// <summary>This build is the latest (or newer).</summary>
    UpToDate,

    /// <summary>A newer release exists; the release page can be opened.</summary>
    UpdateAvailable,

    /// <summary>No release, or the check failed; <see cref="UpdateCheckViewModel.Message"/> says which.</summary>
    Failed,
}

/// <summary>
/// The manual "Check for updates" of the About dialog (ADR-0016). The check and the browser open come in as delegates;
/// disposing (the dialog closed) cancels the request and drops any late result. UI thread.
/// </summary>
public sealed partial class UpdateCheckViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, CancellationToken, Task<UpdateCheckResult>> _check;
    private readonly Func<CancellationToken, Task<bool>> _openReleasePage;
    private readonly Localizer _localizer;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    /// <summary>Creates the view model in <see cref="UpdateCheckState.Idle"/>.</summary>
    /// <param name="currentVersion">This build's version.</param>
    /// <param name="check">The update check (<see cref="UpdateChecker.CheckAsync"/>).</param>
    /// <param name="openReleasePage">Opens <see cref="UpdateChecker.ReleasePage"/>; false when it failed.</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    public UpdateCheckViewModel(
        string currentVersion,
        Func<string, CancellationToken, Task<UpdateCheckResult>> check,
        Func<CancellationToken, Task<bool>> openReleasePage,
        Localizer? localizer = null)
    {
        CurrentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _openReleasePage = openReleasePage ?? throw new ArgumentNullException(nameof(openReleasePage));
        _localizer = localizer ?? Localizer.Current;
    }

    /// <summary>This build's version.</summary>
    public string CurrentVersion { get; }

    /// <summary>Where the check stands.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReleasePageCommand))]
    public partial UpdateCheckState State { get; private set; }

    /// <summary>The newer version; set only in <see cref="UpdateCheckState.UpdateAvailable"/>.</summary>
    [ObservableProperty]
    public partial string? LatestVersion { get; private set; }

    /// <summary>The localized status line; null in <see cref="UpdateCheckState.Idle"/>.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        OpenReleasePageCommand.NotifyCanExecuteChanged();
    }

    // Always executable: disabling the button would drop keyboard focus, so a press while checking is ignored here instead.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CheckAsync()
    {
        if (_disposed || State == UpdateCheckState.Checking)
        {
            return;
        }

        State = UpdateCheckState.Checking;
        LatestVersion = null;
        Message = _localizer["dialog.about.update.checking"];
        UpdateCheckResult result;
        try
        {
            result = await _check(CurrentVersion, _lifetime.Token);
        }
        catch (Exception)
        {
            // The checker throws only on cancel (Dispose, handled below); anything else must not leave the UI in Checking.
            result = new UpdateCheckResult(UpdateCheckOutcome.Unexpected);
        }

        if (_disposed)
        {
            return;
        }

        switch (result)
        {
            case { Outcome: UpdateCheckOutcome.UpdateAvailable, Latest: { } latest }:
                LatestVersion = latest;
                Message = _localizer.Format("dialog.about.update.available", ("current", CurrentVersion), ("latest", latest));
                State = UpdateCheckState.UpdateAvailable;
                break;
            case { Outcome: UpdateCheckOutcome.UpToDate }:
                Message = _localizer.Format("dialog.about.update.upToDate", ("current", CurrentVersion));
                State = UpdateCheckState.UpToDate;
                break;
            default:
                Message = _localizer[result.Outcome switch
                {
                    UpdateCheckOutcome.NoRelease => "dialog.about.update.noRelease",
                    UpdateCheckOutcome.Offline => "dialog.about.update.error.offline",
                    UpdateCheckOutcome.TimedOut => "dialog.about.update.error.timeout",
                    UpdateCheckOutcome.RateLimited => "dialog.about.update.error.rateLimited",
                    _ => "dialog.about.update.error.other",
                }];
                State = UpdateCheckState.Failed;
                break;
        }
    }

    // Concurrent: like the check button, the link must not be disabled under the keyboard focus while the browser starts.
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanOpenReleasePage))]
    private async Task OpenReleasePageAsync()
    {
        if (!CanOpenReleasePage())
        {
            return;
        }

        bool opened;
        try
        {
            opened = await _openReleasePage(_lifetime.Token);
        }
        catch (Exception)
        {
            opened = false;
        }

        // A new check may have started while the browser was launching; its "Checking…" line wins.
        if (!opened && !_disposed && State == UpdateCheckState.UpdateAvailable)
        {
            Message = _localizer["dialog.about.update.error.openFailed"];
        }
    }

    private bool CanOpenReleasePage() => State == UpdateCheckState.UpdateAvailable && !_disposed;
}
