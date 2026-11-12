using System.Collections.Specialized;
using System.ComponentModel;
using Mandrill.App.Icons;
using Mandrill.App.Platform;
using Mandrill.Core.Localization;
using Mandrill.Core.State;
using Mandrill.Presentation.Favorites;
using Mandrill.Presentation.Layout;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Mandrill.App.Views;

/// <summary>
/// The favorites bar (ui-spec §1/§4, AC-1.28–1.32). One flat button per favorite (folder glyph and name, at most 160 px,
/// elided; tooltip with the name and folders); left click restores, right click or Shift+F10 opens Rename…/Delete.
/// Buttons that do not fit move into the "»" menu, recomputed on every resize and change; the bar never wraps.
/// </summary>
public sealed partial class FavoritesBar : UserControl
{
    /// <summary>Widest favorite button (ui-spec §1).</summary>
    public const double ButtonMaxWidth = 160;

    private const string FolderGlyph = "\uE8B7";
    private readonly List<(Favorite Favorite, Button Button)> _buttons = [];
    private FavoritesViewModel? _vm;
    private ILogger _logger = NullLogger.Instance;
    private bool _reflowPending;

    /// <summary>Creates the bar; call <see cref="Attach"/> before use.</summary>
    public FavoritesBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ScheduleReflow();
        AddButton.Click += (_, _) => _ = AddAsync();
    }

    /// <summary>Favorite buttons in bar order, visible or not (tests).</summary>
    public IReadOnlyList<Button> Buttons => [.. _buttons.Select(b => b.Button)];

    /// <summary>The "»" overflow button (tests).</summary>
    public DropDownButton Overflow => OverflowButton;

    /// <summary>Entries of the "»" menu (tests).</summary>
    public IList<MenuFlyoutItemBase> OverflowItems => OverflowMenu.Items;

    /// <summary>Binds the bar to the favorites.</summary>
    /// <param name="vm">Favorites view model.</param>
    /// <param name="icons">Icon provider (star button).</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="InvalidOperationException">Already attached.</exception>
    public void Attach(FavoritesViewModel vm, IconProvider icons, ILogger logger)
    {
        if (_vm is not null)
        {
            throw new InvalidOperationException("The favorites bar is already attached");
        }

        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        ArgumentNullException.ThrowIfNull(icons);
        _logger = logger ?? NullLogger.Instance;
        var star = icons.CreateElement(IconId.Favorite, size: 16);
        AddButton.Content = star;
        OverflowButton.Content = IconProvider.CreateGlyph(IconProvider.MoreGlyph); // owner: the system "More" glyph
        Describe(AddButton, Localizer.Current["favorite.add"]);
        Describe(OverflowButton, Localizer.Current["favorite.more"]);
        AutomationProperties.SetName(Root, Localizer.Current["favorite.bar"]); // the group's name; no visible label (owner #4)
        vm.Items.CollectionChanged += OnItemsChanged;
        vm.PropertyChanged += OnViewModelChanged;
        Visibility = vm.IsBarVisible ? Visibility.Visible : Visibility.Collapsed;
        Rebuild();
    }

    /// <summary>Stops following the view model (window closed; a replaced view model after the session bootstrap).</summary>
    public void Detach()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.Items.CollectionChanged -= OnItemsChanged;
        _vm.PropertyChanged -= OnViewModelChanged;
        _vm = null;
        ButtonPanel.Children.Clear();
        _buttons.Clear();
        OverflowMenu.Items.Clear();
    }

    /// <summary>Recomputes which buttons fit now (tests; normally after layout).</summary>
    public void Reflow()
    {
        _reflowPending = false;
        // Grid spacing applies at every column boundary (2 for 3 columns), also next to the collapsed "»" column.
        double available = Root.ActualWidth - AddButton.ActualWidth - ((Root.ColumnDefinitions.Count - 1) * Root.ColumnSpacing);
        var widths = new double[_buttons.Count];
        double total = 0;
        for (int i = 0; i < _buttons.Count; i++)
        {
            Button button = _buttons[i].Button;
            button.Visibility = Visibility.Visible;
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths[i] = button.DesiredSize.Width;
            total += widths[i] + (i > 0 ? ButtonPanel.Spacing : 0);
        }

        OverflowMenu.Items.Clear();
        if (total <= available)
        {
            OverflowButton.Visibility = Visibility.Collapsed;
            return;
        }

        // Room for the chevron first, then buttons in bar order until one does not fit; the rest go to "»".
        OverflowButton.Visibility = Visibility.Visible;
        OverflowButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double room = available - OverflowButton.DesiredSize.Width;
        double used = 0;
        bool full = false;
        for (int i = 0; i < _buttons.Count; i++)
        {
            double need = widths[i] + (used > 0 ? ButtonPanel.Spacing : 0);
            if (!full && used + need <= room)
            {
                used += need;
                continue;
            }

            full = true;
            var (favorite, button) = _buttons[i];
            button.Visibility = Visibility.Collapsed;
            var item = new MenuFlyoutItem { Text = favorite.Name, Icon = new FontIcon { Glyph = FolderGlyph } };
            ToolTipService.SetToolTip(item, FavoritesViewModel.TooltipOf(favorite));
            item.Click += (_, _) => _ = RestoreAsync(favorite.Name);
            OverflowMenu.Items.Add(item);
        }
    }

    private static void Describe(FrameworkElement element, string text)
    {
        ToolTipService.SetToolTip(element, text);
        AutomationProperties.SetName(element, text);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FavoritesViewModel.IsBarVisible) && _vm is not null)
        {
            Visibility = _vm.IsBarVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Rebuild()
    {
        ButtonPanel.Children.Clear();
        _buttons.Clear();
        OverflowMenu.Items.Clear(); // stale until the next reflow otherwise
        if (_vm is null)
        {
            return;
        }

        foreach (Favorite favorite in _vm.Items)
        {
            Button button = CreateButton(favorite);
            _buttons.Add((favorite, button));
            ButtonPanel.Children.Add(button);
        }

        ScheduleReflow();
    }

    private Button CreateButton(Favorite favorite)
    {
        var text = new TextBlock { Text = favorite.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new FontIcon { Glyph = FolderGlyph, FontSize = 14 }, text },
            },
            MaxWidth = ButtonMaxWidth,
            Height = 28,
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // hit-testable across the whole button
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, FavoritesViewModel.TooltipOf(favorite));
        AutomationProperties.SetName(button, favorite.Name);
        button.Click += (_, _) => _ = RestoreAsync(favorite.Name);

        var rename = new MenuFlyoutItem { Text = Localizer.Current["favorite.rename"] };
        rename.Click += (_, _) => _ = RenameAsync(favorite);
        var delete = new MenuFlyoutItem { Text = Localizer.Current["favorite.delete"] };
        delete.Click += (_, _) => _ = DeleteAsync(favorite.Name);
        button.ContextFlyout = new MenuFlyout { Items = { rename, delete } };
        return button;
    }

    // Layout first (the label and buttons need their sizes), then split.
    private void ScheduleReflow()
    {
        if (_reflowPending)
        {
            return;
        }

        _reflowPending = true;
        if (DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, WinUiDispatcher.Guarded(Reflow, _logger)) != true)
        {
            _reflowPending = false; // shutting down: nothing will run it
        }
    }

    private async Task AddAsync()
    {
        if (_vm is not { } vm || XamlRoot is null)
        {
            return;
        }

        try
        {
            FavoriteDraft draft = vm.CaptureDraft();
            var answer = await FavoriteDialogs.AskNameAsync(XamlRoot, Localizer.Current["favorite.add"], vm.NextDefaultName(), draft.Summary, vm.Exists);
            if (answer is { } a)
            {
                FavoriteSaveOutcome outcome = await vm.SaveAsync(a.Name, draft, a.Replace);
                LogIfNotSaved(a.Name, outcome);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adding a favorite failed");
        }
    }

    private async Task RenameAsync(Favorite favorite)
    {
        if (_vm is not { } vm || XamlRoot is null)
        {
            return;
        }

        try
        {
            string layoutName = LayoutText(favorite.Layout);
            string summary = Localizer.Current.Plural("favorite.summary", favorite.Panes.Count, ("layout", layoutName));
            var answer = await FavoriteDialogs.AskNameAsync(
                XamlRoot,
                Localizer.Current["favorite.renameTitle"],
                favorite.Name,
                summary,
                name => vm.Exists(name) && !Favorite.NameComparer.Equals(name, favorite.Name));
            if (answer is { } a)
            {
                FavoriteSaveOutcome outcome = await vm.RenameAsync(favorite.Name, a.Name, a.Replace);
                LogIfNotSaved(a.Name, outcome);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Renaming a favorite failed");
        }
    }

    // The dialog validated the name already: a different outcome means the dialog and the view model disagree.
    private void LogIfNotSaved(string name, FavoriteSaveOutcome outcome)
    {
        if (outcome != FavoriteSaveOutcome.Saved)
        {
            _logger.LogWarning("Favorite {Name} not saved: {Outcome}", name, outcome);
        }
    }

    private async Task DeleteAsync(string name)
    {
        if (_vm is not { } vm || XamlRoot is null)
        {
            return;
        }

        try
        {
            if (await FavoriteDialogs.ConfirmDeleteAsync(XamlRoot, name))
            {
                await vm.DeleteAsync(name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting a favorite failed");
        }
    }

    private async Task RestoreAsync(string name)
    {
        try
        {
            if (_vm is { } vm)
            {
                await vm.RestoreAsync(name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restoring favorite {Name} failed", name);
        }
    }

    private static string LayoutText(string key)
        => LayoutCatalog.TryFromKey(key, out LayoutId id) ? Localizer.Current[LayoutCatalog.Get(id).TextKey] : key;
}
