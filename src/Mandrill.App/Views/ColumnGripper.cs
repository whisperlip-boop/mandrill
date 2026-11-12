using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Mandrill.App.Views;

/// <summary>
/// Divider at the right edge of a Details header cell (ui-spec §2): a visible 1 px line in an 8 px grab zone centred on
/// the column boundary, with the resize cursor. Drag resizes, double-click auto-fits.
/// </summary>
public sealed partial class ColumnGripper : Grid
{
    /// <summary>Width of the grab zone in pixels (half of it lies over the next column).</summary>
    public const double GrabWidth = 8;

    private double _pressX;
    private bool _dragging;

    /// <summary>Creates the gripper.</summary>
    /// <param name="lineStyle">Style of the visible divider line (a theme-resource fill, so it follows the theme).</param>
    public ColumnGripper(Style? lineStyle)
    {
        Width = GrabWidth;
        HorizontalAlignment = HorizontalAlignment.Right;
        Margin = new Thickness(0, 0, -GrabWidth / 2, 0);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        Line = new Rectangle
        {
            Width = 1,
            Margin = new Thickness(0, 5, 0, 5),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
            Style = lineStyle,
        };
        Children.Add(Line);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            AutoFitRequested?.Invoke();
        };
    }

    /// <summary>Raised when a drag starts (the owner remembers the column width).</summary>
    public event Action? DragStarted;

    /// <summary>
    /// Raised while dragging with the horizontal offset in pixels from where the drag started, so a drag past the column
    /// minimum and back does not drift away from the pointer.
    /// </summary>
    public event Action<double>? Resized;

    /// <summary>Raised on double-click.</summary>
    public event Action? AutoFitRequested;

    /// <summary>The visible divider line (tests).</summary>
    public Rectangle Line { get; }

    /// <summary>Starts a drag as a press does (tests, no input injection).</summary>
    public void SimulateDragStart() => DragStarted?.Invoke();

    /// <summary>Moves a started drag to <paramref name="offset"/> pixels from the press, as the pointer handler does (tests).</summary>
    /// <param name="offset">Horizontal offset from the press position.</param>
    public void SimulateDragTo(double offset) => Resized?.Invoke(offset);

    /// <summary>A whole drag by <paramref name="offset"/> pixels (tests).</summary>
    /// <param name="offset">Horizontal offset.</param>
    public void SimulateDrag(double offset)
    {
        SimulateDragStart();
        SimulateDragTo(offset);
    }

    /// <summary>Requests an auto-fit as a double-click does (tests, no input injection).</summary>
    public void SimulateDoubleClick() => AutoFitRequested?.Invoke();

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = CapturePointer(e.Pointer);
        _pressX = e.GetCurrentPoint(null).Position.X;
        if (_dragging)
        {
            DragStarted?.Invoke();
        }

        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Resized?.Invoke(e.GetCurrentPoint(null).Position.X - _pressX);

        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }
}
