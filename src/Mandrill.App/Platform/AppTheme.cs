using Mandrill.Core.State;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Mandrill.App.Platform;

/// <summary>
/// The Theme setting (owner round 5 #5) on windows and dialogs. It is set on each window's content root, so it applies at
/// once; ContentDialogs do not inherit it (measured), so each one takes <see cref="Of"/> of its XamlRoot. "High contrast"
/// is Dark plus the app's palette (Themes/HighContrast.xaml): WinUI selects its HighContrast dictionaries only for a
/// Windows contrast theme, and nothing lets an app request them.
/// </summary>
public static class AppTheme
{
    private static readonly Uri PaletteSource = new("ms-appx:///Themes/HighContrast.xaml");

    // View-level dictionaries (ExplorerItems / SubtleButtons) beat app-level keys inside their subtree, so the palette is
    // merged into those too; weak, because panes and popups come and go.
    private static readonly List<WeakReference<ResourceDictionary>> Scopes = [];

    /// <summary>Whether the app's high-contrast palette is merged (the setting is "highcontrast" and Windows is not in a contrast theme).</summary>
    public static bool PaletteActive { get; private set; }

    /// <summary>The element theme of a settings value ("system", "light", "dark", "highcontrast"; anything else follows Windows).</summary>
    /// <param name="theme">The settings.json value.</param>
    /// <returns>Light, Dark or Default; high contrast is Dark under the palette.</returns>
    public static ElementTheme Parse(string theme) => theme switch
    {
        AppSettings.ThemeLight => ElementTheme.Light,
        AppSettings.ThemeDark or AppSettings.ThemeHighContrast => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>
    /// Merges or removes the high-contrast palette in the app resources and every tracked scope. Call before
    /// <see cref="Apply"/> on each window; a Windows contrast theme wins, so then the palette is never merged.
    /// </summary>
    /// <param name="theme">The settings.json value.</param>
    /// <returns>True when the palette state changed, so open windows must re-resolve their theme resources.</returns>
    public static bool SetPalette(string theme)
    {
        // shortcut: a Windows contrast theme turned on while the app runs keeps the app palette until the next settings save or restart; re-run SetPalette + Apply from a theme-change hook if this is reported
        bool active = theme == AppSettings.ThemeHighContrast && !IsWindowsHighContrast();
        if (active == PaletteActive)
        {
            return false;
        }

        PaletteActive = active;
        Sync(Application.Current.Resources);
        foreach (var scope in Scopes)
        {
            if (scope.TryGetTarget(out var dictionary))
            {
                Sync(dictionary);
            }
        }

        return true;
    }

    /// <summary>Puts the palette into a view-level dictionary now and on every later <see cref="SetPalette"/>.</summary>
    /// <param name="scope">The Resources that merge ExplorerItems.xaml or SubtleButtons.xaml.</param>
    public static void Track(ResourceDictionary scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scopes.RemoveAll(tracked => !tracked.TryGetTarget(out _)); // closed popups and views
        Scopes.Add(new WeakReference<ResourceDictionary>(scope));
        Sync(scope);
    }

    /// <summary>Applies <paramref name="theme"/> to the window's content and its title bar.</summary>
    /// <param name="window">The window.</param>
    /// <param name="theme">Light, Dark, or Default (follow Windows).</param>
    /// <param name="refresh">The palette changed: flip the theme once so already resolved theme resources pick it up (measured).</param>
    public static void Apply(Window window, ElementTheme theme, bool refresh = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Content is FrameworkElement root)
        {
            if (refresh)
            {
                root.RequestedTheme = theme == ElementTheme.Light ? ElementTheme.Dark : ElementTheme.Light;
            }

            root.RequestedTheme = theme;
        }

        // No high-contrast title bar exists, so the app palette keeps the dark one (accepted limit).
        window.AppWindow.TitleBar.PreferredTheme = theme switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    /// <summary>The theme a dialog or window shown over <paramref name="root"/> must request.</summary>
    /// <param name="root">The XamlRoot it is shown on.</param>
    /// <returns>The root content's requested theme; Default when there is none.</returns>
    public static ElementTheme Of(XamlRoot? root) => (root?.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default;

    /// <summary>Whether Windows or the app palette shows high contrast (no dimming, selection marks kept).</summary>
    /// <returns>True in either high contrast.</returns>
    public static bool IsHighContrast() => PaletteActive || IsWindowsHighContrast();

    private static bool IsWindowsHighContrast()
    {
        try
        {
            return new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Sync(ResourceDictionary scope)
    {
        var merged = scope.MergedDictionaries;
        var palette = merged.FirstOrDefault(d => d.Source == PaletteSource);
        if (PaletteActive && palette is null)
        {
            merged.Add(new ResourceDictionary { Source = PaletteSource }); // last merged is searched first
        }
        else if (!PaletteActive && palette is not null)
        {
            merged.Remove(palette);
        }
    }
}
