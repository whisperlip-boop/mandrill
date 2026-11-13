using Microsoft.UI.Xaml;

namespace Mandrill.App.Views;

/// <summary>
/// Explorer's divider lines are one device pixel wide. A 1 DIP line is 1.5 px at 150 % and draws 2 px wide (owner WP-2:
/// "too thick"), so the header dividers and the status-bar separators take 1 / scale instead.
/// </summary>
public static class HairLine
{
    /// <summary>Keeps <paramref name="line"/> one device pixel wide, also after a scale change (another monitor).</summary>
    /// <param name="line">The divider element.</param>
    public static void Snap(FrameworkElement line)
    {
        ArgumentNullException.ThrowIfNull(line);
        XamlRoot? tracked = null;
        void Apply() => line.Width = line.XamlRoot?.RasterizationScale is double scale and > 0 ? 1 / scale : 1;
        void OnChanged(XamlRoot sender, XamlRootChangedEventArgs e) => Apply();

        // Loaded can repeat without Unloaded, so the old subscription goes first.
        void Track(XamlRoot? root)
        {
            if (tracked is not null)
            {
                tracked.Changed -= OnChanged;
            }

            tracked = root;
            if (tracked is not null)
            {
                tracked.Changed += OnChanged;
            }
        }

        line.Loaded += (_, _) =>
        {
            Track(line.XamlRoot);
            Apply();
        };
        line.Unloaded += (_, _) => Track(null);
    }
}
