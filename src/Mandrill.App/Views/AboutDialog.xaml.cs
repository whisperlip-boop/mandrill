using System.ComponentModel;
using System.Reflection;
using Mandrill.Core.Localization;
using Mandrill.Presentation.About;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mandrill.App.Views;

/// <summary>
/// About dialog: name, version, the manual update check (<see cref="UpdateCheckViewModel"/>, ADR-0016), license, third-party
/// components (each under its own license, MIT for all listed here), and "Open log folder" as the secondary button (the
/// caller opens it through the shell launcher). The caller disposes <see cref="Updates"/> after the dialog closes.
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
    /// <param name="updates">The update check shown under the version.</param>
    /// <param name="iconPath">Absolute path of the app icon (<c>AppPaths.Resource</c>), or null for no icon.</param>
    public AboutDialog(UpdateCheckViewModel updates, string? iconPath = null)
    {
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
        InitializeComponent();
        UpdateCheckButton.Command = updates.CheckCommand;
        UpdateReleaseLink.Command = updates.OpenReleasePageCommand;
        updates.PropertyChanged += OnUpdatesChanged;
        Closed += (_, _) => updates.PropertyChanged -= OnUpdatesChanged;
        // Start on the default Close button: Enter right after opening must not start the network check (ADR-0016).
        Opened += (_, _) => (GetTemplateChild("CloseButton") as Control)?.Focus(FocusState.Programmatic);
        VersionText.Text = Localizer.Current.Format("dialog.about.version", ("version", Version));
        ThirdPartyText.Text = string.Join(Environment.NewLine, ThirdParty);
        if (iconPath is not null)
        {
            AppIcon.Source = new BitmapImage(new Uri(iconPath));
        }
    }

    /// <summary>The update check (state, message, commands).</summary>
    public UpdateCheckViewModel Updates { get; }

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

    // Set in code, not bound, so the text is in place before the live-region event is raised for it.
    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UpdateCheckViewModel.Message):
                UpdateStatusText.Text = Updates.Message ?? string.Empty;
                FrameworkElementAutomationPeer.FromElement(UpdateStatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                break;
            case nameof(UpdateCheckViewModel.State):
                bool available = Updates.State == UpdateCheckState.UpdateAvailable;
                UpdateReleaseLink.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                if (available)
                {
                    AutomationProperties.SetName(UpdateReleaseLink, Localizer.Current.Format("dialog.about.update.openRelease.name", ("latest", Updates.LatestVersion ?? string.Empty)));
                }

                break;
        }
    }
}
