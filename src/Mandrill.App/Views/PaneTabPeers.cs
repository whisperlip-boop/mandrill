using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Mandrill.App.Views;

/// <summary>UIA for the tab group (ui-spec §2a): control type Tab with a single, required selection (the active tab).</summary>
/// <param name="owner">The strip.</param>
public sealed partial class PaneTabStripAutomationPeer(PaneTabStrip owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    /// <inheritdoc/>
    public bool CanSelectMultiple => false;

    /// <inheritdoc/>
    public bool IsSelectionRequired => true;

    /// <inheritdoc/>
    public IRawElementProviderSimple[] GetSelection()
        => owner.ActiveItem is { } active ? [ProviderFromPeer(CreatePeerForElement(active))] : [];

    /// <inheritdoc/>
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;

    /// <inheritdoc/>
    protected override string GetClassNameCore() => nameof(PaneTabStrip);

    /// <inheritdoc/>
    protected override object? GetPatternCore(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);
}

/// <summary>UIA for one tab: TabItem with SelectionItem (selected = active; Select activates), position in the set.</summary>
/// <param name="owner">The tab.</param>
/// <param name="strip">Its strip.</param>
public sealed partial class PaneTabItemAutomationPeer(PaneTabItem owner, PaneTabStrip strip) : FrameworkElementAutomationPeer(owner), ISelectionItemProvider
{
    /// <inheritdoc/>
    public bool IsSelected => owner.Tab.IsActive;

    /// <inheritdoc/>
    public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(CreatePeerForElement(strip));

    /// <inheritdoc/>
    public void Select() => strip.Activate(owner);

    /// <inheritdoc/>
    public void AddToSelection()
    {
        if (!IsSelected)
        {
            throw new InvalidOperationException("Only one tab can be selected.");
        }
    }

    /// <inheritdoc/>
    public void RemoveFromSelection()
    {
        if (IsSelected)
        {
            throw new InvalidOperationException("A tab must stay selected.");
        }
    }

    /// <inheritdoc/>
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

    /// <inheritdoc/>
    protected override string GetClassNameCore() => nameof(PaneTabItem);

    /// <inheritdoc/>
    protected override int GetPositionInSetCore() => strip.IndexOf(owner) + 1;

    /// <inheritdoc/>
    protected override int GetSizeOfSetCore() => strip.Count;

    /// <inheritdoc/>
    protected override object? GetPatternCore(PatternInterface patternInterface)
        => patternInterface == PatternInterface.SelectionItem ? this : base.GetPatternCore(patternInterface);
}
