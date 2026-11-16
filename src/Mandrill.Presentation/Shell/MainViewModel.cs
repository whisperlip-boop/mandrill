using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mandrill.Core.Localization;
using Mandrill.Presentation.Layout;
using Microsoft.Extensions.Logging;

namespace Mandrill.Presentation.Shell;

/// <summary>
/// Window-level commands behind the menu bar (AC-1.37). Without a <see cref="LayoutManager"/> the layout commands only
/// record state; after <see cref="AttachLayout"/> they drive it and <see cref="CurrentLayout"/> follows it (WP-1.10).
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxPanes = 4;
    private readonly ILogger<MainViewModel> _logger;
    private bool _syncingLayout;
    private long _lastNewWindow = long.MinValue / 2;

    /// <summary>Creates the view model.</summary>
    /// <param name="logger">Logger.</param>
    public MainViewModel(ILogger<MainViewModel> logger)
    {
        _logger = logger;
    }

    /// <summary>Raised when the user chose Exit; the view closes the window.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the user chose Settings…; the view opens the Settings dialog (WP-1.11).</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user chose Language…; the view opens the language picker (L1).</summary>
    public event EventHandler? LanguageRequested;

    /// <summary>Raised when the user chose About; the view opens the About dialog (WP-1.11).</summary>
    public event EventHandler? AboutRequested;

    /// <summary>Raised with a localized message for the status bar.</summary>
    public event Action<string>? StatusRequested;

    /// <summary>
    /// Starts another Mandrill window (File &gt; New window, Ctrl+N; architecture §18 D7); true when it started. Set by the
    /// app; null opens nothing.
    /// </summary>
    public Func<Task<bool>>? StartNewWindow { get; set; }

    /// <summary>Current window layout (WP-1.10 applies it to the pane grid).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPaneCommand), nameof(RemovePaneCommand))]
    public partial LayoutId CurrentLayout { get; set; } = LayoutId.TwoLeftRight;

    /// <summary>Session-only "show hidden files" override (architecture §10; applied in WP-1.7).</summary>
    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    /// <summary>Visible pane count of the current layout.</summary>
    public int PaneCount => LayoutCatalog.Get(CurrentLayout).PaneCount;

    /// <summary>The attached layout manager; null until <see cref="AttachLayout"/>.</summary>
    public LayoutManager? Layout { get; private set; }

    /// <summary>
    /// Connects the window commands to <paramref name="layout"/>: SetLayout/AddPane/RemovePane/ToggleTree/pane activation act
    /// on it, <see cref="CurrentLayout"/> follows it, and a later <see cref="ShowHidden"/> change applies to all 4 panes as
    /// their session-only override (set <see cref="ShowHidden"/> to Explorer's value before attaching).
    /// </summary>
    /// <param name="layout">The window's layout manager.</param>
    /// <exception cref="InvalidOperationException">A layout manager is already attached.</exception>
    public void AttachLayout(LayoutManager layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (Layout is not null)
        {
            throw new InvalidOperationException("A layout manager is already attached.");
        }

        Layout = layout;
        layout.LayoutChanged += OnLayoutChanged;
        OnLayoutChanged();
    }

    [RelayCommand]
    private async Task NewWindowAsync()
    {
        // A held Ctrl+N repeats: one window per second at most, not one process per key repeat.
        long now = Environment.TickCount64;
        if (now - _lastNewWindow < 1000)
        {
            return;
        }

        _lastNewWindow = now;
        _logger.LogInformation("New window requested");
        if (StartNewWindow is not { } start || !await start().ConfigureAwait(true))
        {
            StatusRequested?.Invoke(Localizer.Current["error.newWindowFailed"]);
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _logger.LogInformation("Settings requested from the menu");
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenLanguage()
    {
        _logger.LogInformation("Language picker requested from the menu");
        LanguageRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Exit()
    {
        _logger.LogInformation("Exit requested from the menu");
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SetLayout(LayoutId layout)
    {
        _logger.LogInformation("Layout {Layout} selected", LayoutCatalog.Get(layout).Key);
        CurrentLayout = layout;
    }

    [RelayCommand]
    private void ToggleTree()
    {
        if (Layout is not { } layout)
        {
            NoLayout("Toggle tree");
        }
        else if (layout.VisibleCount > 0)
        {
            layout.ActivePane.TreeVisible = !layout.ActivePane.TreeVisible;
        }
        else
        {
            _logger.LogInformation("Toggle tree ignored: no visible pane");
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddPane))]
    private void AddPane()
    {
        if (Layout is { } layout)
        {
            layout.AddPane();
        }
        else
        {
            NoLayout("Add pane");
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemovePane))]
    private void RemovePane()
    {
        if (Layout is { } layout)
        {
            layout.RemoveActivePane();
        }
        else
        {
            NoLayout("Remove pane");
        }
    }

    /// <summary>
    /// Alt+1…4: activate pane N (1-based); ignored when that pane is not shown. The parameter must be an Int32 (XAML:
    /// <c>x:Int32</c>); a string CommandParameter throws InvalidCastException.
    /// </summary>
    [RelayCommand]
    private void ActivatePane(int number) => Layout?.Activate(number - 1);

    [RelayCommand]
    private void About()
    {
        _logger.LogInformation("About requested from the menu");
        AboutRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanAddPane() => PaneCount < MaxPanes;

    private bool CanRemovePane() => PaneCount > 1;

    partial void OnCurrentLayoutChanged(LayoutId value)
    {
        OnPropertyChanged(nameof(PaneCount));
        if (Layout is { } layout && !_syncingLayout && layout.Current != value)
        {
            layout.Apply(value);
        }
    }

    partial void OnShowHiddenChanged(bool value)
    {
        _logger.LogInformation("Show hidden files: {Value}", value);
        if (Layout is { } layout)
        {
            foreach (var pane in layout.Pool)
            {
                pane.ShowHiddenOverride = value;
            }
        }
    }

    // CurrentLayout follows the manager (Add/Remove pane change it too) without applying it back.
    private void OnLayoutChanged()
    {
        _syncingLayout = true;
        try
        {
            CurrentLayout = Layout!.Current;
        }
        finally
        {
            _syncingLayout = false;
        }

        // Add/Remove CanExecute follow CurrentLayout ([NotifyCanExecuteChangedFor] on it).
    }

    private void NoLayout(string feature) => _logger.LogInformation("{Feature} ignored: no layout manager attached", feature);

}
