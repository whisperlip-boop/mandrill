using System.ComponentModel;
using Mandrill.App.Icons;
using Mandrill.Presentation.Panes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Mandrill.App.Views;

/// <summary>
/// One pane tab (ui-spec §2a): a trapezoid (8 px slant, 6 px top radius) 22 px tall when active, 20 px otherwise, over a
/// full-height hit shape; title with ellipsis (bold when active), hover x, two-tone keyboard focus ring. Built in code by
/// <see cref="PaneTabStrip"/>, which owns its colours, focus and actions.
/// </summary>
public sealed partial class PaneTabItem : ContentControl
{
    /// <summary>Band height: the tab's bottom edge sits on the 2 px strip.</summary>
    public const double BandHeight = 22;

    /// <summary>Top of an inactive tab (2 px lower than the active one).</summary>
    public const double InactiveTop = 2;

    /// <summary>Horizontal slant per side.</summary>
    public const double Slant = 8;

    private const double Radius = 6;
    // Text and x stay clear of the 16 px a neighbour overlaps (QA F4/F5): the left neighbour's slant covers up to x 16, the
    // right neighbour's from W − 16 on, and the active tab must stay topmost there.
    private const double TextLeft = 2 * Slant + 4;
    private const double CloseRight = 2 * Slant + 4;
    private const double CloseReserve = CloseRight + 20 + 4;
    private const double NarrowReserve = 2 * Slant + 4;
    private const double NarrowWidth = 120;

    private readonly PaneTabStrip _strip;
    private readonly Path _hit = new() { Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Path _shape = new() { IsHitTestVisible = false };
    private readonly Path _ringOuter = new() { IsHitTestVisible = false, StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Path _ringInner = new() { IsHitTestVisible = false, StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _text = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close;
    private readonly Grid _band;
    private readonly IconElement _closeGlyph;
    private bool _hover;
    private bool _pressed;
    private double _shapeWidth;
    private double _shapeTop = -1;

    internal PaneTabItem(PaneTab tab, PaneTabStrip strip, IconProvider icons)
    {
        Tab = tab;
        _strip = strip;
        Height = BandHeight;
        Padding = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        UseSystemFocusVisuals = false; // the ring follows the trapezoid
        IsTabStop = false;              // roving: the strip makes one tab the group's stop

        _text.FontFamily = (FontFamily)Application.Current.Resources["MandrillListFontFamily"];
        _text.FontSize = (double)Application.Current.Resources["MandrillListFontSize"];
        _closeGlyph = icons.CreateElement(IconId.Close, size: 10);
        _close = new Button
        {
            Width = 20,
            Height = 20,
            Padding = new Thickness(5),
            Margin = new Thickness(0, InactiveTop, CloseRight, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(0),
            IsTabStop = false,
            Content = _closeGlyph,
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAccessibilityView(_close, AccessibilityView.Raw); // mouse-only; the menu has Close tab
        AutomationProperties.SetAccessibilityView(_text, AccessibilityView.Raw);  // the tab item carries the name
        _close.Click += (_, _) => _strip.Close(this);

        // The text band is y 2…22, so its centre (y 12) does not move when the tab grows to 22 px on activation.
        _band = new Grid { Margin = new Thickness(TextLeft, InactiveTop, CloseReserve, 0), Height = BandHeight - InactiveTop, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        _band.Children.Add(_text);
        var root = new Grid();
        root.Children.Add(_hit);
        root.Children.Add(_shape);
        root.Children.Add(_band);
        root.Children.Add(_ringOuter);
        root.Children.Add(_ringInner);
        root.Children.Add(_close);
        Content = root;

        PointerEntered += (_, _) => SetPointer(hover: true, _pressed);
        PointerExited += (_, _) => SetPointer(hover: false, pressed: false);
        RenderTransform = Shift; // drag: the dragged tab follows the pointer, its neighbours slide aside
        PointerPressed += OnPointerPressed;
        PointerReleased += (_, _) => SetPointer(_hover, pressed: false);
        PointerCaptureLost += (_, _) => SetPointer(_hover, pressed: false);
        GotFocus += (_, _) => _strip.OnTabFocused(this);
        LostFocus += (_, _) => Refresh();
        SizeChanged += (_, _) => Refresh();
        tab.PropertyChanged += OnTabChanged;
        Describe();
    }

    /// <summary>The tab shown.</summary>
    public PaneTab Tab { get; }

    /// <summary>Whether the x is shown (hover or keyboard focus, never for the last tab; tests).</summary>
    public bool IsCloseVisible => _close.Visibility == Visibility.Visible;

    /// <summary>The title's text block (tests: font fit).</summary>
    public TextBlock TitleBlock => _text;

    /// <summary>The visible shape's top: 0 when active, 2 otherwise (tests).</summary>
    public double ShapeTop => _shapeTop;

    internal TabVisualState VisualState => _pressed ? TabVisualState.Pressed : _hover ? TabVisualState.Hover : TabVisualState.Rest;

    /// <summary>Horizontal offset from the tab's slot while a drag is in progress.</summary>
    internal TranslateTransform Shift { get; } = new();

    /// <summary>Whether this tab is being dragged (2 px edge).</summary>
    internal bool IsDragging { get; set; }

    /// <summary>Stops following the tab (the strip removed it).</summary>
    internal void Detach() => Tab.PropertyChanged -= OnTabChanged;

    /// <summary>Re-applies geometry, colours and the x after a state, theme or pane change.</summary>
    internal void Refresh()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width;
        if (double.IsNaN(width) || width <= 2 * Slant)
        {
            return;
        }

        double top = Tab.IsActive ? 0 : InactiveTop;
        if (width != _shapeWidth || top != _shapeTop)
        {
            _shapeWidth = width;
            _shapeTop = top;
            _hit.Data = Trapezoid(width, 0, 0, Radius);
            _shape.Data = Trapezoid(width, top, 0, Radius);
            _ringOuter.Data = Trapezoid(width, top, 1, Radius);
            _ringInner.Data = Trapezoid(width, top, 2.5, Radius);
        }

        var look = _strip.LookOf(this);
        _shape.Fill = look.Fill;
        _shape.Stroke = look.Edge;
        _shape.StrokeThickness = look.EdgeThickness;
        _text.Foreground = look.Text;
        _closeGlyph.Foreground = look.Text;
        _close.Foreground = look.Text;
        _close.Background = look.Fill; // narrow tabs: the x covers the text end on a solid tab fill
        _band.Margin = new Thickness(TextLeft, InactiveTop, width >= NarrowWidth ? CloseReserve : NarrowReserve, 0);
        _text.FontWeight = new Windows.UI.Text.FontWeight(Tab.IsActive ? (ushort)700 : (ushort)400); // FontWeights statics are not activatable unpackaged (measured)

        bool keyboardFocus = FocusState == FocusState.Keyboard;
        _ringOuter.Visibility = _ringInner.Visibility = keyboardFocus ? Visibility.Visible : Visibility.Collapsed;
        _ringOuter.Stroke = look.FocusOuter;
        _ringInner.Stroke = look.FocusInner;
        _close.Visibility = (_hover || keyboardFocus) && _strip.CanClose ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc/>
    protected override AutomationPeer OnCreateAutomationPeer() => new PaneTabItemAutomationPeer(this, _strip);

    // The visible trapezoid from y = top to the band's bottom, inset by d (the focus ring stays inside the bounds). The top
    // corners are quadratic curves through the corner point: the 6 px radius drawn without arc tangent arithmetic.
    private static PathGeometry Trapezoid(double width, double top, double inset, double radius)
    {
        double bottom = BandHeight;
        double h = bottom - top;
        double side = Math.Sqrt((Slant * Slant) + (h * h));
        double yTop = top + inset;
        double yBottom = bottom - inset;
        double X(double y) => (Slant * (bottom - y) / h) + (inset * side / h);
        var bottomLeft = new Point(X(yBottom), yBottom);
        var topLeft = new Point(X(yTop), yTop);
        var topRight = new Point(width - topLeft.X, yTop);
        var bottomRight = new Point(width - bottomLeft.X, yBottom);
        double r = Math.Max(radius - inset, 0);
        double len = Math.Sqrt(Math.Pow(bottomLeft.X - topLeft.X, 2) + Math.Pow(bottomLeft.Y - topLeft.Y, 2));
        double ux = (bottomLeft.X - topLeft.X) / len;
        double uy = (bottomLeft.Y - topLeft.Y) / len;

        var figure = new PathFigure { StartPoint = bottomLeft, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(topLeft.X + (ux * r), topLeft.Y + (uy * r)) });
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = topLeft, Point2 = new Point(topLeft.X + r, yTop) });
        figure.Segments.Add(new LineSegment { Point = new Point(topRight.X - r, yTop) });
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = topRight, Point2 = new Point(topRight.X - (ux * r), topRight.Y + (uy * r)) });
        figure.Segments.Add(new LineSegment { Point = bottomRight });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            SetPointer(_hover, pressed: true); // the strip activates on press and may start a drag
        }
    }

    private void SetPointer(bool hover, bool pressed)
    {
        _hover = hover;
        _pressed = pressed;
        Refresh();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneTab.Title) or nameof(PaneTab.ToolTip))
        {
            Describe();
        }
    }

    private void Describe()
    {
        _text.Text = Tab.Title;
        ToolTipService.SetToolTip(this, Tab.ToolTip);
        AutomationProperties.SetName(this, Tab.Title);
        AutomationProperties.SetHelpText(this, Tab.ToolTip);
    }
}

/// <summary>Colours of one tab for its current state (computed by <see cref="PaneTabStrip.LookOf"/>).</summary>
/// <param name="Fill">Shape fill.</param>
/// <param name="Text">Title and x colour.</param>
/// <param name="Edge">Outline.</param>
/// <param name="EdgeThickness">Outline width.</param>
/// <param name="FocusOuter">Outer focus ring.</param>
/// <param name="FocusInner">Inner focus ring.</param>
internal sealed record TabLook(Brush Fill, Brush Text, Brush Edge, double EdgeThickness, Brush FocusOuter, Brush FocusInner);
