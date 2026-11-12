using System.ComponentModel;
using Mandrill.App.Platform;
using Mandrill.Presentation.Panes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mandrill.App.Views;

/// <summary>
/// The pane's path box (AC-1.18, ui-spec §2): editable, history dropdown (most recent first), Enter navigates, Esc reverts,
/// a click selects all, an invalid path gets a red outline and keeps the previous folder. While unfocused the text is
/// scrolled to its end so the current folder name stays visible; the tooltip shows the full path.
/// </summary>
public sealed partial class PathBox : UserControl
{
    private PaneViewModel? _vm;
    private ILogger _logger = NullLogger.Instance;
    private TextBox? _editor;
    private bool _enterPressed;
    private bool _updatingItems;

    // A dropdown entry whose navigation is still running: shown instead of the old folder until it commits or fails.
    private string? _picked;
    private bool _dropDownWasOpen;

    /// <summary>Creates the box; call <see cref="Attach"/> before use.</summary>
    public PathBox()
    {
        InitializeComponent();
        Box.AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), handledEventsToo: true);

        // Owner round 5 #8: the ComboBox raises TextSubmitted for Enter before KeyDown reaches this control, so a KeyDown
        // flag came one Enter late (the first Enter reverted, the second navigated). The tunnelling event comes first.
        Box.PreviewKeyDown += (_, e) =>
        {
            _enterPressed = e.Key == VirtualKey.Enter; // any other key: an edit, not a commit
            if (Box.IsDropDownOpen && IsCloseKey(e.Key))
            {
                // F4 / Alt+Up / Alt+Down close the open list without a choice, but the ComboBox commits its highlighted
                // entry on the way (the first that starts with the text): not a pick.
                _dropDownWasOpen = false;
            }

            if (_enterPressed && !Box.IsDropDownOpen && _vm is not null)
            {
                // QA S3-7b: with the dropdown closed, the ComboBox's own Enter selects the first history entry that starts
                // with the typed text (and raises no TextSubmitted for it). Enter is ours: exactly what was typed (AC-1.18).
                e.Handled = true;
                SubmitTyped();
            }
        };
        Box.TextSubmitted += OnTextSubmitted;
        Box.SelectionChanged += OnSelectionChanged;

        // QA S3-7c: the ComboBox commits a keyboard pick while it closes (observed: Enter -> SelectionChanged with the list
        // already closed -> DropDownClosed), so remember the open dropdown until the dispatcher turn after it closed.
        Box.DropDownOpened += (_, _) => _dropDownWasOpen = true;
        Box.DropDownClosed += (_, _) => DispatcherQueue.TryEnqueue(WinUiDispatcher.Guarded(() => _dropDownWasOpen = Box.IsDropDownOpen, _logger));
        Box.GotFocus += (_, _) => DispatcherQueue.TryEnqueue(WinUiDispatcher.Guarded(() => Editor()?.SelectAll(), _logger));
        Box.LostFocus += (_, _) =>
        {
            _enterPressed = false; // a later focus loss with edited text must revert, not navigate
            RestoreTextLater();
        };
        Box.Loaded += (_, _) =>
        {
            Editor(); // finds the text box and keeps it sized (owner F10)
            ShowEnd();
        };
    }

    /// <summary>
    /// A typed This PC (name or token) committed: the pane moves the focus to its list, as a typed folder ends up there once
    /// it lists (QA S3-5).
    /// </summary>
    public event EventHandler? ListFocusRequested;

    /// <summary>True while the text box inside has keyboard focus (pane shortcuts stay silent then, AC-1.19).</summary>
    public bool IsEditing => Editor()?.FocusState is not (null or FocusState.Unfocused);

    /// <summary>The text the user sees in the box (the template's text box; tests).</summary>
    public string DisplayedText => Editor()?.Text ?? string.Empty;

    /// <summary>The selected part of <see cref="DisplayedText"/> (tests).</summary>
    public string SelectedText => Editor()?.SelectedText ?? string.Empty;

    /// <summary>Picks a history entry as the dropdown commits a click or Down+Enter while it closes (tests, no input injection).</summary>
    /// <param name="index">Index into the history (most recent first).</param>
    public void PickHistoryItem(int index)
    {
        _dropDownWasOpen = true; // as the ComboBox commits it when the dropdown closes
        Box.SelectedIndex = index;
    }

    /// <summary>Selects a history entry the way the ComboBox does by itself with its dropdown closed (tests, QA S3-7b).</summary>
    /// <param name="index">Index into the history (most recent first).</param>
    public void SelectWithDropDownClosed(int index) => Box.SelectedIndex = index;

    private static bool IsCloseKey(VirtualKey key)
        => key == VirtualKey.F4
        || (key is VirtualKey.Up or VirtualKey.Down
            && InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));

    /// <summary>Enter with the dropdown closed: navigates to exactly the typed text (also called by tests).</summary>
    public void SubmitTyped()
    {
        _enterPressed = false;
        Navigate(DisplayedText);
    }

    /// <summary>Replaces the text as typing does (tests, no input injection).</summary>
    /// <param name="text">Typed text.</param>
    public void SimulateTyping(string text)
    {
        if (Editor() is { } editor)
        {
            editor.Text = text;
        }
    }

    /// <summary>Binds the box to a pane.</summary>
    /// <param name="vm">Pane view model.</param>
    /// <param name="logger">Logger.</param>
    public void Attach(PaneViewModel vm, ILogger logger)
    {
        if (_vm is not null)
        {
            throw new InvalidOperationException("The path box is already attached to a pane");
        }

        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _logger = logger ?? NullLogger.Instance;
        vm.PropertyChanged += OnViewModelChanged;
        SyncText();
        SyncHistory();
        SyncError();
    }

    /// <summary>Focuses the box and selects its text (Ctrl+L, Alt+D).</summary>
    public void FocusAndSelect()
    {
        // The text box itself: focusing the ComboBox keeps focus on the ComboBox once its text box has a size (F10).
        if (Editor() is { } editor)
        {
            editor.Focus(FocusState.Keyboard);
            editor.SelectAll();
        }
        else
        {
            Box.Focus(FocusState.Keyboard);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaneViewModel.PathText):
                SyncText();
                break;
            case nameof(PaneViewModel.PathHistory):
                SyncHistory();
                break;
            case nameof(PaneViewModel.PathError):
                SyncError();
                break;
        }
    }

    private void SyncText()
    {
        if (_vm is null)
        {
            return;
        }

        string text = _vm.PathText;
        ToolTipService.SetToolTip(Box, string.IsNullOrEmpty(text) ? null : text);
        Box.Text = text;
        if (!IsEditing)
        {
            ShowEnd();
        }
    }

    private void SyncHistory()
    {
        if (_vm is null)
        {
            return;
        }

        _updatingItems = true;
        try
        {
            string text = Box.Text;
            Box.ItemsSource = _vm.PathHistory.ToArray();
            Box.SelectedIndex = -1;
            Box.Text = text;
        }
        finally
        {
            _updatingItems = false;
        }
    }

    private void SyncError() => ErrorOutline.Visibility = _vm?.PathError == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !Box.IsDropDownOpen && _vm is not null)
        {
            RevertText();
            e.Handled = true;
        }
    }

    /// <summary>Esc: back to the current folder, all selected (also called by tests).</summary>
    public void RevertText()
    {
        if (_vm is null)
        {
            return;
        }

        // Explicit: an unchanged PathText raises no change notification, so the typed text would stay.
        _vm.RevertPathText();
        ShowText(_vm.PathText);
        Editor()?.SelectAll();
    }

    private void OnTextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        bool enter = _enterPressed;
        _enterPressed = false;
        if (_vm is null)
        {
            return;
        }

        if (!enter)
        {
            // Focus left with edited text: Explorer reverts instead of navigating.
            _vm.RevertPathText();
            return;
        }

        Navigate(args.Text);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingItems || Box.SelectedItem is not string path)
        {
            return;
        }

        // QA S3-7b: only a pick from the dropdown (open, or closing with the pick) is a pick. With the dropdown closed the
        // ComboBox selects a history entry by itself (the first that starts with the text, when the focus leaves); undo that.
        if (!Box.IsDropDownOpen && !_dropDownWasOpen)
        {
            _updatingItems = true;
            try
            {
                Box.SelectedIndex = -1;
            }
            finally
            {
                _updatingItems = false;
            }

            RestoreTextLater();
            return;
        }

        _enterPressed = false; // Enter on a dropdown item commits here; TextSubmitted is not raised for it
        _dropDownWasOpen = Box.IsDropDownOpen; // one pick per opened dropdown

        _picked = path; // shown while it loads, so a slow folder does not look like a failed pick (QA F-4)
        Navigate(path);
        _updatingItems = true;
        try
        {
            Box.SelectedIndex = -1; // picking the same entry again must navigate again
        }
        finally
        {
            _updatingItems = false;
        }

        RestoreTextLater(); // the reset selection clears the text; picking the current folder changes no PathText
    }

    // Owner F5: the editable ComboBox shows its selected item when focus leaves or the selection is reset, and there is
    // none, so it clears the text while the view model's path stays the same (no change notification to re-sync on).
    // Put the path back after the control's own handling, unless the user is typing or browsing the open dropdown.
    private void RestoreTextLater() => DispatcherQueue.TryEnqueue(WinUiDispatcher.Guarded(RestoreText, _logger));

    private void RestoreText()
    {
        if (_vm is null || IsEditing || Box.IsDropDownOpen)
        {
            return;
        }

        ShowText(_picked ?? _vm.PathText);
        ShowEnd();
    }

    private void ShowText(string text)
    {
        if (Box.Text != text)
        {
            Box.Text = text;
        }
        else if (Editor() is { } editor && editor.Text != text)
        {
            editor.Text = text; // same ComboBox.Text: setting it again would not reach the text box
        }
    }

    private void Navigate(string text)
    {
        if (_vm is null)
        {
            return;
        }

        _ = NavigateSafeAsync(_vm, text);
    }

    private async Task NavigateSafeAsync(PaneViewModel vm, string text)
    {
        NavigationResult? result = null;
        try
        {
            // Typed: an invalid or missing path keeps the previous folder and sets PathError (AC-1.18).
            result = await vm.NavigateAsync(text, NavOrigin.Typed);
        }
        catch (ObjectDisposedException)
        {
            // Pane closed meanwhile.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Navigation from the path box failed");
        }
        finally
        {
            if (_picked == text)
            {
                // Committed (PathText is the new folder) or failed (still the old one): show the view model's path.
                _picked = null;
                RestoreTextLater();
            }
            else
            {
                // QA S3-2: the editable ComboBox empties its text after a submit it has no item for. A navigation that
                // committed at once (This PC) or failed (a bad path keeps the typed text, AC-1.18) leaves nothing to redraw it.
                // QA S3-5: then the caret must not sit at 0 in front of the text: a bad path stays selected to fix or retype,
                // and a typed This PC hands the focus to the pane's list, where a typed folder's focus ends up.
                DispatcherQueue.TryEnqueue(WinUiDispatcher.Guarded(
                    () =>
                    {
                        if (!ReferenceEquals(_vm, vm))
                        {
                            return;
                        }

                        if (DisplayedText.Length == 0)
                        {
                            ShowText(vm.PathError ? text : vm.PathText); // also when ComboBox.Text is unchanged (empty editor)
                        }

                        if (vm.PathError)
                        {
                            Editor()?.SelectAll();
                        }
                        else if (vm.IsThisPc && result?.Outcome == NavigationOutcome.Committed)
                        {
                            ListFocusRequested?.Invoke(this, EventArgs.Empty);
                        }
                    },
                    _logger));
            }
        }
    }

    // Owner F10: WinAppSDK 2.6's editable ComboBox gives its text box a local Width and Height of 0 while no item is
    // selected, so the path is set but never drawn. This box never keeps a selected item (history entries are commands),
    // so undo the zero size whenever the control sets it.
    private static void KeepSized(TextBox editor, ILogger logger)
    {
        void Restore()
        {
            if (editor.ReadLocalValue(WidthProperty) is double width && width == 0)
            {
                editor.ClearValue(WidthProperty);
            }

            if (editor.ReadLocalValue(HeightProperty) is double height && height == 0)
            {
                editor.ClearValue(HeightProperty);
            }
        }

        // Deferred, also the first time: clearing a property inside the control's own update (a change callback, or the
        // first Editor() lookup from a ComboBox event) would re-enter it. The callbacks capture only the text box and the logger,
        // so they live exactly as long as it does; no unregistering needed.
        var restore = WinUiDispatcher.Guarded(Restore, logger);
        editor.RegisterPropertyChangedCallback(WidthProperty, (_, _) => editor.DispatcherQueue.TryEnqueue(restore));
        editor.RegisterPropertyChangedCallback(HeightProperty, (_, _) => editor.DispatcherQueue.TryEnqueue(restore));
        editor.DispatcherQueue.TryEnqueue(restore);
    }

    // The ComboBox template has no public accessor for its text box; it is the only TextBox in the tree.
    private TextBox? Editor()
    {
        if (_editor is not null)
        {
            return _editor;
        }

        var stack = new Stack<DependencyObject>();
        stack.Push(Box);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ContentPresenter { Name: "ContentPresenter" } presenter)
            {
                // WinUI puts the text here on focus loss; under the kept-sized editor (transparent until focused) it is a second copy.
                presenter.Opacity = 0;
            }

            if (node is TextBox box)
            {
                _editor = box;
                KeepSized(box, _logger);

                // QA F1: the open list squares the editor's corners on its side; the error outline follows them.
                ErrorOutline.SetBinding(Border.CornerRadiusProperty, new Binding { Source = box, Path = new PropertyPath(nameof(CornerRadius)) });
                return box;
            }

            for (int i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }

        return null;
    }

    private void ShowEnd()
    {
        if (Editor() is { } editor && !IsEditing)
        {
            editor.Select(editor.Text.Length, 0);
        }
    }
}
