using Mandrill.App.Platform;
using Mandrill.Core.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Mandrill.App.Views;

/// <summary>
/// The modeless search popup of one pane (ui-spec §3, architecture §3 WP-2.2b): 760 × 480, resizable, centred over the
/// main window and owned by it (stays above it, minimizes with it). Esc on an idle search closes it. Placement is not
/// persisted.
/// </summary>
public sealed class SearchWindow : Window
{
    private const int Width = 760;
    private const int Height = 480;
    private bool _focusPending = true; // ui-spec §3: every open focuses the query, previous query selected
    private bool _active;

    /// <summary>Creates the window around <paramref name="view"/>.</summary>
    /// <param name="view">The search content (attached by the caller).</param>
    /// <param name="owner">The main window.</param>
    /// <param name="title">Window title.</param>
    /// <param name="iconPath">The title bar icon (the toolbar magnifier).</param>
    public SearchWindow(SearchView view, Window owner, string title, string iconPath)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(owner);
        View = view;
        Content = view;
        Title = title;
        AppWindow.SetIcon(iconPath);
        AppTheme.Apply(this, AppTheme.Of(owner.Content?.XamlRoot)); // the main window's Theme setting

        // Physical pixels: scale the DIP size by the owner's display scale, centre over the owner, keep it on that display.
        double scale = owner.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var size = new SizeInt32((int)Math.Round(Width * scale), (int)Math.Round(Height * scale));
        PointInt32 ownerPosition = owner.AppWindow.Position;
        SizeInt32 ownerSize = owner.AppWindow.Size;
        RectInt32? workArea = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
        AppWindow.MoveAndResize(WindowPlacementRules.CentreOver(new RectInt32(ownerPosition.X, ownerPosition.Y, ownerSize.Width, ownerSize.Height), size, workArea));
        IsOwned = WindowOwner.Set(WinRT.Interop.WindowNative.GetWindowHandle(this), WinRT.Interop.WindowNative.GetWindowHandle(owner));

        view.CloseRequested += (_, _) => Close();
        Activated += (_, e) =>
        {
            _active = e.WindowActivationState != WindowActivationState.Deactivated;
            FocusQueryIfPending();
        };
        view.Loaded += (_, _) => FocusQueryIfPending();
    }

    /// <summary>The search content.</summary>
    public SearchView View { get; }

    /// <summary>False when the main window could not be made the owner (the popup then neither stays above it nor minimizes with it).</summary>
    public bool IsOwned { get; }

    /// <summary>Brings the window to the front and focuses the query (Ctrl+F or the Search button again).</summary>
    public void Raise()
    {
        // An owned window has no taskbar button: a popup the user minimized on its own must come back here.
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        _focusPending = true;
        Activate();
        FocusQueryIfPending(); // already active: no Activated event follows
    }

    // The first Activated comes before the content is loaded, where focusing does nothing (owner round 6 #5): focus once
    // the window is both active and loaded, whichever comes last, and not again on a later Alt+Tab back.
    private void FocusQueryIfPending()
    {
        if (_focusPending && _active && View.IsLoaded)
        {
            _focusPending = false;
            View.FocusQuery();
        }
    }
}
