using Mandrill.Core.Localization;
using Mandrill.Presentation.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mandrill.App.Views;

/// <summary>
/// Help → Keyboard shortcuts / F1 (ui-spec §11): a read-only list of <see cref="ShortcutCatalog.Groups"/>, one titled
/// two-column table per group (keys, action). Built in code: it is only text.
/// </summary>
public sealed partial class ShortcutsDialog : ContentDialog
{
    private static readonly Windows.UI.Text.FontWeight SemiBold = new(600); // FontWeights statics are not activatable unpackaged (PaneTabItem)

    /// <summary>Creates the dialog.</summary>
    public ShortcutsDialog()
    {
        Title = Localizer.Current["dialog.shortcuts.title"];
        CloseButtonText = Localizer.Current["dialog.shortcuts.close"];
        DefaultButton = ContentDialogButton.Close;

        var groups = new StackPanel { MinWidth = 380, Margin = new Thickness(0, 0, 24, 0), Spacing = 16 };
        foreach (ShortcutGroup group in ShortcutCatalog.Groups)
        {
            groups.Children.Add(Table(group));
        }

        // As in Settings (owner round 5 #6): the scroll bar sits in the dialog's right padding and never covers a row.
        Content = new ScrollViewer { Margin = new Thickness(0, 0, -24, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = groups };
    }

    private static StackPanel Table(ShortcutGroup group)
    {
        var rows = new Grid { ColumnSpacing = 24, RowSpacing = 4 };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < group.Items.Count; i++)
        {
            ShortcutInfo item = group.Items[i];
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = item.Keys, FontWeight = SemiBold, TextWrapping = TextWrapping.Wrap };
            var action = new TextBlock { Text = Localizer.Current[item.TextKey], TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            rows.Children.Add(keys);
            rows.Children.Add(action);
        }

        var table = new StackPanel { Spacing = 8 };
        table.Children.Add(new TextBlock { Text = Localizer.Current[group.TitleKey], FontWeight = SemiBold, FontSize = 16 });
        table.Children.Add(rows);
        return table;
    }
}
