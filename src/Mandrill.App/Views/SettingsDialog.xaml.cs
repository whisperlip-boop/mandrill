using System.ComponentModel;
using Mandrill.App.Icons;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.State;
using Mandrill.Presentation.Layout;
using Mandrill.Presentation.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;

namespace Mandrill.App.Views;

/// <summary>
/// Settings dialog (AC-1.34, AC-1.3b, ui-spec §1): language (applies after restart, the dialog says so), theme, favorites bar,
/// default arrangements for 2 and 3 panes shown with the layout glyphs, and the index section (AC-2.10, WP-2.6): excluded
/// folders (typed path or folder picker, rejections as an inline message) and the rescan interval, edited through
/// <see cref="ExcludeListEditor"/>. The caller saves <see cref="ToSettings"/> when the user chose Save.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private static readonly (string Value, string Key)[] Languages =
    [
        (AppSettings.LanguageAuto, "dialog.settings.language.auto"),
        (AppSettings.LanguageKorean, "dialog.settings.language.ko"),
        (AppSettings.LanguageEnglish, "dialog.settings.language.en"),
    ];

    private static readonly (string Value, string Key)[] Themes =
    [
        (AppSettings.ThemeSystem, "dialog.settings.theme.system"),
        (AppSettings.ThemeLight, "dialog.settings.theme.light"),
        (AppSettings.ThemeDark, "dialog.settings.theme.dark"),
        (AppSettings.ThemeHighContrast, "dialog.settings.theme.contrast"),
    ];

    private static readonly string[] TwoPaneLayouts = [LayoutKeys.TwoLeftRight, LayoutKeys.TwoTopBottom];

    private static readonly string[] ThreePaneLayouts = [LayoutKeys.ThreeLeft1Right2, LayoutKeys.ThreeLeft2Right1, LayoutKeys.ThreeTop1Bottom2, LayoutKeys.ThreeTop2Bottom1];

    private readonly AppSettings _original;
    private readonly ExcludeListEditor _index;
    private readonly Func<Task<string?>> _pickFolder;
    private readonly CancellationTokenSource _closing = new();
    private Task<bool>? _adding;
    private string? _addingText;

    /// <summary>Creates the dialog showing <paramref name="current"/>.</summary>
    /// <param name="current">Settings in effect.</param>
    /// <param name="icons">Icon provider (layout glyphs).</param>
    /// <param name="index">Editor of the index section, created over <paramref name="current"/>'s index settings.</param>
    /// <param name="pickFolder">Shows the folder picker; null when cancelled.</param>
    public SettingsDialog(AppSettings current, IconProvider icons, ExcludeListEditor index, Func<Task<string?>> pickFolder)
    {
        _original = current ?? throw new ArgumentNullException(nameof(current));
        ArgumentNullException.ThrowIfNull(icons);
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _pickFolder = pickFolder ?? throw new ArgumentNullException(nameof(pickFolder));
        InitializeComponent();
        Fill(LanguageChoice, Languages, current.Language);
        Fill(ThemeChoice, Themes, current.Theme);
        FavoritesBarToggle.IsOn = current.ShowFavoritesBar;
        Fill(TwoPaneChoice, TwoPaneLayouts, current.LayoutDefaults.TwoPanes, icons);
        Fill(ThreePaneChoice, ThreePaneLayouts, current.LayoutDefaults.ThreePanes, icons);
        AttachIndex();
    }

    /// <summary>The settings as chosen in the dialog (unchanged parts, such as the index options, are kept).</summary>
    /// <returns>The new settings.</returns>
    public AppSettings ToSettings() => _original with
    {
        Language = Languages[Math.Clamp(LanguageChoice.SelectedIndex, 0, Languages.Length - 1)].Value,
        Theme = Themes[Math.Clamp(ThemeChoice.SelectedIndex, 0, Themes.Length - 1)].Value,
        ShowFavoritesBar = FavoritesBarToggle.IsOn,
        LayoutDefaults = new LayoutDefaults(
            TwoPaneLayouts[Math.Clamp(TwoPaneChoice.SelectedIndex, 0, TwoPaneLayouts.Length - 1)],
            ThreePaneLayouts[Math.Clamp(ThreePaneChoice.SelectedIndex, 0, ThreePaneLayouts.Length - 1)]),
        Index = IndexSettingsFromView(),
    };

    // Unchanged → the original instance (no spurious change).
    private IndexSettings IndexSettingsFromView()
    {
        IndexSettings edited = _index.ToSettings();
        IndexSettings original = _original.Index;
        return edited.RescanHours == original.RescanHours && SameFolders(edited.Exclude, original.Exclude ?? [])
            ? original
            : edited;
    }

    // Set comparison in the editor's normalized form: a removed and re-added folder, or "c:/data/" against "C:\data", is
    // no change.
    private static bool SameFolders(IReadOnlyList<string> edited, IReadOnlyList<string> original)
    {
        var before = new HashSet<string>(PathUtil.Comparer);
        foreach (string entry in original)
        {
            if (PathUtil.TryNormalize(entry, out string path, out _) && !PathUtil.IsDriveRoot(path))
            {
                before.Add(path);
            }
        }

        return before.SetEquals(edited);
    }

    private void AttachIndex()
    {
        ExcludeList.ItemsSource = _index.Items;
        RescanHoursBox.Minimum = ExcludeListEditor.MinRescanHours;
        RescanHoursBox.Maximum = ExcludeListEditor.MaxRescanHours;
        RescanHoursBox.Value = _index.RescanHours;
        RescanHoursBox.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue))
            {
                // A cleared box reads NaN: the last value stays. Clamped before the cast (an out-of-range double is undefined).
                _index.RescanHours = (int)Math.Round(Math.Clamp(e.NewValue, ExcludeListEditor.MinRescanHours, ExcludeListEditor.MaxRescanHours));
            }
        };
        ExcludeList.ContainerContentChanging += OnExcludeItem;
        _index.Items.CollectionChanged += (_, _) => ShowEmptyHint();
        _index.PropertyChanged += OnIndexChanged;
        ExcludeAdd.Click += async (_, _) => await AddTypedAsync();
        ExcludeBrowse.Click += async (_, _) => await BrowseAsync();
        ExcludePath.PreviewKeyDown += OnPathKeyDown; // tunnelling: before the dialog can treat Enter as Save
        PrimaryButtonClick += OnSaveClick;
        Closed += (_, _) =>
        {
            // Cancel only: a picker or disk check still running reads the token when it resumes.
            _index.PropertyChanged -= OnIndexChanged;
            _closing.Cancel();
        };
        ShowEmptyHint();
    }

    // Each row: the folder path and a Remove button (built in code: the row content is a plain string).
    private void OnExcludeItem(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not string path)
        {
            return;
        }

        string label = Localizer.Current["dialog.settings.index.remove"];
        var remove = new Button { Content = label, Tag = path };
        AutomationProperties.SetName(remove, $"{label} {path}"); // screen readers tell the rows apart
        remove.Click += (s, _) =>
        {
            if (s is Button { Tag: string p })
            {
                _index.Remove(p);
            }
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = path, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(text, path);
        row.Children.Add(text);
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        args.ItemContainer.Content = row;
        args.Handled = true;
    }

    private void ShowEmptyHint()
    {
        bool empty = _index.Items.Count == 0;
        ExcludeEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ExcludeList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnIndexChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ExcludeListEditor.Message)) // MessageIsError is always set first
        {
            return;
        }

        ExcludeMessage.Text = _index.Message ?? string.Empty;
        ExcludeMessage.Visibility = _index.Message is null ? Visibility.Collapsed : Visibility.Visible;
        ExcludeMessage.Foreground = (_index.MessageIsError ? ErrorSwatch : HintSwatch).Background;
    }

    // Enter adds the typed path instead of pressing the dialog's default (Save) button.
    private async void OnPathKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        await AddTypedAsync();
    }

    /// <summary>
    /// Save with a path still typed in the box (not added): it is added first, as the user evidently meant. A rejected path
    /// keeps the dialog open with its message.
    /// </summary>
    /// <returns>True when the dialog may close: the box was empty or its path was added.</returns>
    public async Task<bool> CommitTypedPathAsync()
        => string.IsNullOrWhiteSpace(ExcludePath.Text) || await AddTypedAsync();

    // Save (default button): a deferral holds the close while a typed path is checked.
    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(ExcludePath.Text))
        {
            return;
        }

        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await CommitTypedPathAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    // Enter then Save on the same text while the disk check runs: one add, one answer (a second add would be rejected as
    // a duplicate of the first and cancel the close).
    private Task<bool> AddTypedAsync()
    {
        string submitted = ExcludePath.Text;
        if (_adding is { IsCompleted: false } && string.Equals(_addingText, submitted, StringComparison.Ordinal))
        {
            return _adding;
        }

        _addingText = submitted;
        return _adding = AddCoreAsync(submitted);
    }

    private async Task<bool> AddCoreAsync(string submitted)
    {
        try
        {
            // The disk check can take seconds (a spun-down disk): text typed meanwhile stays.
            if (!await _index.AddAsync(submitted, typed: true, _closing.Token))
            {
                return false;
            }

            if (ExcludePath.Text == submitted)
            {
                ExcludePath.Text = string.Empty;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false; // the dialog closed during the disk check
        }
    }

    private async Task BrowseAsync()
    {
        try
        {
            if (await _pickFolder() is { } picked)
            {
                await _index.AddAsync(picked, typed: false, _closing.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog closed during the disk check.
        }
    }

    // One radio item per layout: its glyph and localized name.
    private static void Fill(RadioButtons choice, (string Value, string Key)[] options, string selected)
    {
        foreach (var (_, key) in options)
        {
            choice.Items.Add(Localizer.Current[key]);
        }

        choice.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Value == selected));
    }

    private static void Fill(RadioButtons choice, string[] layouts, string selected, IconProvider icons)
    {
        foreach (string key in layouts)
        {
            _ = LayoutCatalog.TryFromKey(key, out LayoutId id); // keys are LayoutKeys constants
            var glyph = icons.CreateElement(IconProvider.ForLayout(id), size: 24);
            var label = new TextBlock { Text = Localizer.Current[LayoutCatalog.Get(id).TextKey], VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(glyph);
            content.Children.Add(label);

            // Our own RadioButton, so UIA gets the layout name (a generated one has none for panel content; QA s2).
            var item = new RadioButton { Content = content };
            AutomationProperties.SetName(item, label.Text);
            choice.Items.Add(item);
        }

        choice.SelectedIndex = Math.Max(0, Array.IndexOf(layouts, selected));
    }
}
