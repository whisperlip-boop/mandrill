using Mandrill.App.Platform;
using Mandrill.Core.IO;
using Mandrill.Core.Localization;
using Mandrill.Core.Shell;
using Mandrill.Presentation.Common;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Mandrill.App.Views;

/// <summary>
/// Explorer's permission prompt (owner F14, layout stage 4): the folder name as title; a yellow warning triangle left of
/// the blue main instruction "You don't currently have permission to access this folder." and the body line "Click
/// Continue to permanently get access to this folder."; on a footer band [shield Continue] [Cancel], plain buttons, at the
/// bottom right. A ContentDialog button takes text only, so the whole button row is content and the dialog sets no
/// built-in button (its command space stays collapsed); the band repaints that space's look. Both icons are the
/// Windows stock icons (SIID_WARNING, SIID_SHIELD) from the icon service, so they are Explorer's own artwork; they load
/// asynchronously and the prompt works without them.
/// </summary>
public static class FolderAccessDialog
{
    // ThemeResource in markup re-resolves against the dialog's own RequestedTheme; a code lookup would take the app's.
    private const string MainInstruction =
        "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TextWrapping='Wrap'"
        + " Style='{StaticResource BodyLargeTextBlockStyle}' Foreground='{ThemeResource MandrillMainInstructionBrush}' />";

    // ContentDialog's own command-space look; the negative margin undoes the content padding (ContentDialogPadding, 24)
    // so the band reaches both sides and the bottom edge, where the dialog's rounded corners clip it.
    private const string Footer =
        "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Margin='-24,0,-24,-24'"
        + " Padding='{ThemeResource ContentDialogPadding}' Background='{ThemeResource ContentDialogBackground}'"
        + " BorderBrush='{ThemeResource ContentDialogSeparatorBorderBrush}' BorderThickness='0,1,0,0' />";

    /// <summary>Shows the prompt for <paramref name="folder"/>.</summary>
    /// <param name="root">XamlRoot of the window.</param>
    /// <param name="icons">Icon service (the stock warning and shield icons).</param>
    /// <param name="bitmaps">UI-thread bitmaps of icon pixels.</param>
    /// <param name="dispatcher">UI dispatcher (IconsReady arrives off the UI thread).</param>
    /// <param name="logger">Logs a stock icon that could not be loaded.</param>
    /// <param name="folder">The denied folder.</param>
    /// <returns>True when the user chose Continue; Cancel and Esc return false.</returns>
    public static async Task<bool> ShowAsync(XamlRoot root, IIconService icons, IconBitmapCache bitmaps, IUiDispatcher dispatcher, ILogger logger, string folder)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(icons);
        ArgumentNullException.ThrowIfNull(bitmaps);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);
        Localizer l = Localizer.Current;
        bool chosen = false;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = AppTheme.Of(root),
            Title = PathUtil.LeafName(folder),
        };

        // Image lists scale with the DPI (as the file icons do): SHIL_LARGE for 32 px, SHIL_SMALL for 16 px.
        var warning = new Image { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top }; // decorative: no UIA name
        var shield = new Image { Width = 16, Height = 16 };
        (IconKey Key, Image Target)[] stock =
        [
            (new IconKey(IconKey.WarningExtId, null, IconSize.Medium), warning),
            (new IconKey(IconKey.ShieldExtId, null, IconSize.Small), shield),
        ];
        void Show(IconKey key, Image target)
        {
            if (target.Source is null && icons.TryGet(key, out IconImage? image))
            {
                if (image is not null)
                {
                    target.Source = bitmaps.Get(key, image);
                }
                else
                {
                    logger.LogWarning("Access prompt: stock icon {ExtId} could not be loaded; shown without it", key.ExtId);
                }
            }
        }

        void OnIconsReady(IReadOnlyList<IconKey> keys) => dispatcher.Post(
            () =>
            {
                foreach (var (key, target) in stock)
                {
                    if (keys.Contains(key))
                    {
                        Show(key, target);
                    }
                }
            },
            UiPriority.Low);

        string continueText = l["dialog.access.continue"];
        var continueButton = new Button
        {
            VerticalAlignment = VerticalAlignment.Stretch,
            MinWidth = 96,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { shield, new TextBlock { Text = continueText } },
            },
        };
        AutomationProperties.SetName(continueButton, continueText);
        continueButton.Click += (_, _) =>
        {
            chosen = true;
            dialog.Hide();
        };
        var cancelButton = new Button { Content = l["dialog.access.cancel"], VerticalAlignment = VerticalAlignment.Stretch, MinWidth = 96 };
        cancelButton.Click += (_, _) => dialog.Hide();

        // Explorer's default button: Continue has the focus, so Enter continues (the UAC prompt still asks before elevating).
        dialog.Opened += (_, _) => continueButton.Focus(FocusState.Programmatic);

        var main = (TextBlock)XamlReader.Load(MainInstruction);
        main.Text = l["dialog.access.message"];
        var message = new Grid { ColumnSpacing = 16, ColumnDefinitions = { new() { Width = GridLength.Auto }, new() } };
        var lines = new StackPanel
        {
            Spacing = 12,
            Children = { main, new TextBlock { Text = l["dialog.access.detail"], TextWrapping = TextWrapping.Wrap } },
        };
        Grid.SetColumn(lines, 1);
        message.Children.Add(warning);
        message.Children.Add(lines);

        message.MaxWidth = 420;
        var footer = (Border)XamlReader.Load(Footer);
        footer.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { continueButton, cancelButton },
        };
        dialog.Content = new StackPanel { Spacing = 24, Children = { message, footer } };
        dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0); // the band's top edge is the separator

        icons.IconsReady += OnIconsReady;
        try
        {
            foreach (var (key, target) in stock)
            {
                Show(key, target);
            }

            await dialog.ShowAsync();
        }
        finally
        {
            icons.IconsReady -= OnIconsReady;
        }

        return chosen;
    }
}
