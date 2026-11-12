using System.Globalization;
using Mandrill.Core.Localization;

namespace Mandrill.Presentation.Search;

/// <summary>Texts of the search popup (ui-spec §3 states, footer, scope, title); localized, culture-formatted numbers.</summary>
public static class SearchText
{
    private const string Separator = " · ";

    /// <summary>The footer for the popup's state, after the latest status message (a vanished row, a failed open).</summary>
    /// <param name="vm">The search view model.</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    /// <returns>The footer text.</returns>
    public static string Footer(SearchViewModel vm, Localizer? localizer = null)
    {
        ArgumentNullException.ThrowIfNull(vm);
        string footer = Footer(vm.State, vm.Found, vm.Elapsed, vm.Capped, vm.AccessDenied, vm.Partial, localizer);
        if (vm.Status is not { } status)
        {
            return footer;
        }

        // QA B5: the message and the result count together, so the count is not lost until the next search.
        string message = (localizer ?? Localizer.Current).Format(status.Key, status.Args);
        return footer.Length == 0 ? message : message + Separator + footer;
    }

    /// <summary>
    /// The footer for a state: progress, result count and time, or failure, plus the cap, skipped-folder and partial notes.
    /// Initial and No results are shown centred over the list (<see cref="Centre(SearchState, string?, Localizer?)"/>),
    /// so their footer holds only the notes.
    /// </summary>
    /// <param name="state">State.</param>
    /// <param name="found">Rows shown.</param>
    /// <param name="elapsed">Duration of the finished search.</param>
    /// <param name="capped">More results exist than the display cap.</param>
    /// <param name="accessDenied">Folders skipped for lack of access.</param>
    /// <param name="partial">Folders vanished or could not be read.</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    /// <returns>The footer text ("" when there is nothing to say).</returns>
    public static string Footer(SearchState state, int found, TimeSpan elapsed, bool capped, int accessDenied, bool partial, Localizer? localizer = null)
    {
        Localizer l = localizer ?? Localizer.Current;
        string count = found.ToString("N0", CultureInfo.CurrentCulture);
        var parts = new List<string>();
        switch (state)
        {
            case SearchState.Initial:
                return string.Empty;
            case SearchState.Error:
                return l["search.error"];
            case SearchState.Searching:
                parts.Add(l.Format("search.searching", ("count", count)));
                break;
            case SearchState.Done:
                parts.Add(l.Plural("search.done", found, ("count", count), ("seconds", elapsed.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture))));
                break;
        }

        if (capped)
        {
            parts.Add(l.Format("search.capped", ("count", SearchViewModel.DisplayCap.ToString("N0", CultureInfo.CurrentCulture))));
        }

        if (accessDenied > 0)
        {
            parts.Add(l.Plural("search.denied", accessDenied, ("count", accessDenied.ToString("N0", CultureInfo.CurrentCulture))));
        }

        if (partial)
        {
            parts.Add(l["search.partial"]);
        }

        return string.Join(Separator, parts);
    }

    /// <summary>The grey text centred over the list: the hint before a search, "No files match …" after an empty one.</summary>
    /// <param name="state">State.</param>
    /// <param name="query">The query searched.</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    /// <returns>The text, or "" when rows (or progress) show instead.</returns>
    public static string Centre(SearchState state, string? query, Localizer? localizer = null)
    {
        Localizer l = localizer ?? Localizer.Current;
        return state switch
        {
            SearchState.Initial => l["search.hint"],
            SearchState.NoResults => l.Format("search.noResults", ("query", query ?? string.Empty)),
            _ => string.Empty,
        };
    }

    /// <summary>The window title: "Search – folder".</summary>
    /// <param name="scope">The pane's folder (null before it shows one).</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    /// <returns>The title.</returns>
    public static string Title(string? scope, Localizer? localizer = null)
        => (localizer ?? Localizer.Current).Format("search.title", ("folder", scope ?? string.Empty));

    /// <summary>The scope line: "Scope: folder (including subfolders)".</summary>
    /// <param name="scope">The folder searched, or the pane's folder before the first search (null before it shows one).</param>
    /// <param name="localizer">Localizer; null for <see cref="Localizer.Current"/>.</param>
    /// <returns>The text, or "" without a folder.</returns>
    public static string Scope(string? scope, Localizer? localizer = null)
        => scope is null ? string.Empty : (localizer ?? Localizer.Current).Format("search.scope", ("folder", scope));
}
