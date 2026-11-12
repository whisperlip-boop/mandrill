using Mandrill.App.Platform;
using Mandrill.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mandrill.App.Views;

/// <summary>
/// The favorite dialogs (ui-spec §4): Add/Rename with the name prefilled and selected, the summary line, Save/Cancel and an
/// inline "Replace?" for a duplicate name; and the Delete confirmation. One dialog at a time (WinUI allows only one).
/// </summary>
internal static class FavoriteDialogs
{
    private static bool s_open;

    /// <summary>Asks for a favorite name.</summary>
    /// <param name="root">XamlRoot of the window.</param>
    /// <param name="title">Dialog title (Add or Rename).</param>
    /// <param name="initial">Prefilled name (fully selected).</param>
    /// <param name="summary">Read-only summary line, e.g. "Layout: 2 × 2 · 4 paths".</param>
    /// <param name="exists">Whether another favorite has this name (asks Replace? before saving).</param>
    /// <returns>The trimmed name and whether replacing was confirmed; null when cancelled or another dialog is open.</returns>
    public static async Task<(string Name, bool Replace)?> AskNameAsync(XamlRoot root, string title, string initial, string summary, Func<string, bool> exists)
    {
        if (s_open)
        {
            return null;
        }

        string save = Localizer.Current["favorite.save"];
        var box = new TextBox { Text = initial, Header = Localizer.Current["favorite.name"], Width = 320 };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = AppTheme.Of(root),
            Title = title,
            PrimaryButtonText = save,
            CloseButtonText = Localizer.Current["favorite.cancel"],
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { box, new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap }, message },
            },
        };

        bool replaceArmed = false;
        box.TextChanged += (_, _) =>
        {
            replaceArmed = false;
            message.Visibility = Visibility.Collapsed;
            dialog.PrimaryButtonText = save;
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string name = box.Text.Trim();
            if (name.Length == 0)
            {
                args.Cancel = true;
                message.Text = Localizer.Current["favorite.emptyName"];
                message.Visibility = Visibility.Visible;
            }
            else if (exists(name) && !replaceArmed)
            {
                // Never overwrite silently: the second Save (now "Replace") confirms.
                args.Cancel = true;
                replaceArmed = true;
                message.Text = Localizer.Current["favorite.duplicate"];
                message.Visibility = Visibility.Visible;
                dialog.PrimaryButtonText = Localizer.Current["favorite.replace"];
            }
        };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };

        s_open = true;
        try
        {
            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? (box.Text.Trim(), replaceArmed) : null;
        }
        finally
        {
            s_open = false;
        }
    }

    /// <summary>Asks "Delete favorite “X”?".</summary>
    /// <param name="root">XamlRoot of the window.</param>
    /// <param name="name">Favorite name.</param>
    /// <returns>True when confirmed.</returns>
    public static async Task<bool> ConfirmDeleteAsync(XamlRoot root, string name)
    {
        if (s_open)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = AppTheme.Of(root),
            Content = Localizer.Current.Format("favorite.deleteConfirm", ("name", name)),
            PrimaryButtonText = Localizer.Current["favorite.delete"],
            CloseButtonText = Localizer.Current["favorite.cancel"],
            DefaultButton = ContentDialogButton.Close,
        };

        s_open = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            s_open = false;
        }
    }
}
