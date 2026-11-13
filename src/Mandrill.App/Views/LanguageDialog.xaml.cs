using Mandrill.App.Icons;
using Mandrill.App.Platform;
using Mandrill.Core.Localization;
using Mandrill.Presentation.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace Mandrill.App.Views;

/// <summary>
/// Language picker (File → Language…, L1): a search box over a three-column grid whose first cell is always Auto
/// (<see cref="LanguageChoices"/>). The saved setting is the grid's selection and carries a check. Picking sets
/// <see cref="Chosen"/> and closes; the caller saves it and offers the restart.
/// </summary>
public sealed partial class LanguageDialog : ContentDialog
{
    private readonly IReadOnlyList<LanguageInfo> _languages;
    private readonly string _autoLanguage;
    private readonly string _saved;
    private bool _filling;

    /// <summary>Creates the picker.</summary>
    /// <param name="languages">Installed languages (<c>BootstrapResult.Languages</c>).</param>
    /// <param name="autoLanguage">The code "auto" resolves to on this PC.</param>
    /// <param name="saved">The saved language setting.</param>
    /// <param name="icons">Icon provider (the x).</param>
    public LanguageDialog(IReadOnlyList<LanguageInfo> languages, string autoLanguage, string saved, IconProvider icons)
    {
        _languages = languages ?? throw new ArgumentNullException(nameof(languages));
        _autoLanguage = autoLanguage ?? throw new ArgumentNullException(nameof(autoLanguage));
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        ArgumentNullException.ThrowIfNull(icons);
        InitializeComponent();
        AppTheme.Track(TitleRow.Resources); // SubtleButtons.xaml scope (the x)
        CloseButton.Content = icons.CreateElement(IconId.Close, size: 12);
        Fill(string.Empty);
        Opened += (_, _) => SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>The picked cell, or null when the picker was closed without a choice.</summary>
    public LanguageChoice? Chosen { get; private set; }

    // Rebuilt per keystroke (a handful of cells); _filling keeps the restored selection from counting as a pick.
    private void Fill(string query)
    {
        _filling = true;
        try
        {
            var choices = LanguageChoices.Build(_languages, _autoLanguage, _saved, query);
            LanguageGrid.ItemsSource = choices;
            LanguageGrid.SelectedItem = choices.FirstOrDefault(c => c.IsCurrent);
        }
        finally
        {
            _filling = false;
        }
    }

    private void Pick(object? item)
    {
        if (Chosen is null && item is LanguageChoice choice)
        {
            Chosen = choice;
            Hide();
        }
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is LanguageChoice choice)
        {
            AutomationProperties.SetName(args.ItemContainer, choice.AutomationName);
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e) => Pick(e.ClickedItem);

    // Space (and a click) select the focused cell: a new selection is a pick too.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && e.AddedItems.Count > 0)
        {
            Pick(e.AddedItems[0]);
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => Fill(SearchBox.Text);

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Down && LanguageGrid.Items.Count > 0)
        {
            e.Handled = LanguageGrid.Focus(FocusState.Keyboard);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Hide();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();
}
