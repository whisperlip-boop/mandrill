using System.Reflection;
using Mandrill.Core.Localization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mandrill.App.Views;

/// <summary>
/// About dialog: name, version, license, third-party components (each under its own license, MIT for all listed here), and
/// "Open log folder" as the secondary button (the caller opens it through the shell launcher).
/// </summary>
public sealed partial class AboutDialog : ContentDialog
{
    /// <summary>Third-party components shipped with Mandrill (names are not translated).</summary>
    public static IReadOnlyList<string> ThirdParty { get; } =
    [
        "Windows App SDK / WinUI 3 — MIT",
        "CommunityToolkit.WinUI.Controls.Sizers, CommunityToolkit.Mvvm — MIT",
        "Microsoft.Extensions.Logging — MIT",
        "NReco.Logging.File — MIT",
    ];

    /// <summary>Creates the dialog.</summary>
    /// <param name="iconPath">Absolute path of the app icon (<c>AppPaths.Resource</c>), or null for no icon.</param>
    public AboutDialog(string? iconPath = null)
    {
        InitializeComponent();
        VersionText.Text = Localizer.Current.Format("dialog.about.version", ("version", Version));
        ThirdPartyText.Text = string.Join(Environment.NewLine, ThirdParty);
        if (iconPath is not null)
        {
            AppIcon.Source = new BitmapImage(new Uri(iconPath));
        }
    }

    /// <summary>Product version (informational version without build metadata).</summary>
    public static string Version
    {
        get
        {
            var assembly = typeof(AboutDialog).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string version = informational ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            int plus = version.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? version[..plus] : version;
        }
    }
}
