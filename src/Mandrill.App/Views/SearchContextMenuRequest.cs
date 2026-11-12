using Mandrill.Core.Search;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Mandrill.App.Views;

/// <summary>
/// A right-click, Shift+F10 or menu key on a search result (WP-3.5, AC-3.5): the results the menu is for, the one it was
/// opened on, where the native menu goes (window client pixels) and where the fallback menu goes (the list and a point in it).
/// </summary>
/// <param name="Hits">The selected results (the right-clicked row joined or replaced the selection, as in Explorer).</param>
/// <param name="Focused">The row the menu was opened on (Open, Open containing folder).</param>
/// <param name="SearchId">The search the rows belong to (a choice for rows of an earlier search is dropped).</param>
/// <param name="Anchor">The list (the fallback flyout is shown on it).</param>
/// <param name="AnchorPoint">The point in <paramref name="Anchor"/>.</param>
/// <param name="ClientX">X in the popup's client area, physical pixels.</param>
/// <param name="ClientY">Y in the popup's client area, physical pixels.</param>
/// <param name="Extended">Shift+right-click: extended verbs.</param>
public sealed record SearchContextMenuRequest(IReadOnlyList<SearchHit> Hits, SearchHit Focused, long SearchId, UIElement Anchor, Point AnchorPoint, int ClientX, int ClientY, bool Extended);
