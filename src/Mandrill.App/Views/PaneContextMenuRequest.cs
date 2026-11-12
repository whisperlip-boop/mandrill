using Mandrill.Core.Shell;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Mandrill.App.Views;

/// <summary>
/// A right-click, Shift+F10 or menu key in a pane's list (WP-3.1): what the menu is for, where the native menu goes (window
/// client pixels) and where the fallback menu goes (the list and a point in it).
/// </summary>
/// <param name="Target">The selected items, or the folder background.</param>
/// <param name="Anchor">The list (the fallback flyout is shown on it).</param>
/// <param name="AnchorPoint">The point in <paramref name="Anchor"/>.</param>
/// <param name="ClientX">X in the window's client area, physical pixels.</param>
/// <param name="ClientY">Y in the window's client area, physical pixels.</param>
/// <param name="Extended">Shift was held: extended verbs.</param>
/// <param name="FromTree">A folder tree node (WP6): the item is not in the pane's list, so no own commands and no rename.</param>
public sealed record PaneContextMenuRequest(ContextMenuTarget Target, UIElement Anchor, Point AnchorPoint, int ClientX, int ClientY, bool Extended, bool FromTree = false);
