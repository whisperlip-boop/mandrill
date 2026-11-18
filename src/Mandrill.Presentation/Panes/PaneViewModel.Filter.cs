using CommunityToolkit.Mvvm.ComponentModel;
using Mandrill.Core.Search;

namespace Mandrill.Presentation.Panes;

// Quick filter (ui-spec §2b): per tab, memory only. The scanner re-filters the listing it already holds (Resort, no I/O),
// so selection, sorting and watcher diffs all work on the filtered list.
public sealed partial class PaneViewModel
{
    private string _filterText = string.Empty;

    /// <summary>
    /// Quick-filter text; blank = no filter. Names match as in the search popup (<see cref="NameMatcher"/>: substring, or a
    /// whole-name <c>*</c>/<c>?</c> pattern; case-insensitive). Navigating to another folder clears it.
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsFiltered));
                Resort(); // a running load finishes first, then the shown folder is re-filtered once
            }
        }
    }

    /// <summary>Whether the filter row is open (the "Show when needed" setting); per tab.</summary>
    [ObservableProperty]
    public partial bool IsFilterOpen { get; set; }

    /// <summary>True while a non-blank filter applies.</summary>
    public bool IsFiltered => !string.IsNullOrWhiteSpace(_filterText);

    /// <summary>Clears the filter text and closes the row.</summary>
    public void CloseFilter()
    {
        FilterText = string.Empty;
        IsFilterOpen = false;
    }

    private NameMatcher? FilterMatcher => IsFiltered ? NameMatcher.Create(_filterText.Trim()) : null;

    // Without a re-sort: the caller scans (or the shown listing already matches) with the new value.
    private void SetFilterSilently(string text, bool open)
    {
        if (SetProperty(ref _filterText, text, nameof(FilterText)))
        {
            OnPropertyChanged(nameof(IsFiltered));
        }

        IsFilterOpen = open;
    }
}
