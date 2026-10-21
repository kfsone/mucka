using System.ComponentModel;
using Mucka.ViewModels;

namespace Mucka.Pages;

/// <summary>
/// The $SCORE panel's view: the grip that resizes it, and the clamp that keeps a remembered size
/// inside whatever room the host gives it. Everything else is the view model's.
/// </summary>
public partial class ScorePanelView : ContentView
{
    private ScoreGraphViewModel? _graph;
    private VisualElement? _host;
    private double _resizeStartW, _resizeStartH;
    private bool _resizing;

    public ScorePanelView()
    {
        InitializeComponent();
        // Hidden until a view model says otherwise - see OnBindingContextChanged.
        IsVisible = false;
        // A resize or a font change can re-wrap the controls while a list is open.
        ScoreControls.SizeChanged += (_, _) => ReplaceOpenList();
#if MACCATALYST
        ScorePanel.HandlerChanged += (_, _) => InstallMacResize();
#endif
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        if (_graph is not null)
            _graph.PropertyChanged -= OnGraphPropertyChanged;
        _graph = BindingContext as ScoreGraphViewModel;
        if (_graph is not null)
            _graph.PropertyChanged += OnGraphPropertyChanged;
        IsVisible = _graph?.IsVisible ?? false;
        Clamp();
    }

    protected override void OnParentSet()
    {
        base.OnParentSet();
        if (_host is not null)
            _host.SizeChanged -= OnHostSizeChanged;
        _host = Parent as VisualElement;
        if (_host is not null)
            _host.SizeChanged += OnHostSizeChanged;
        Clamp();
    }

    private void OnHostSizeChanged(object? sender, EventArgs e) => Clamp();

    private void OnGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Shown and hidden here, so a host only sets the BindingContext: in the host's XAML a binding
        // on this element is compiled against the host's x:DataType, not the panel's view model.
        if (e.PropertyName == nameof(ScoreGraphViewModel.IsVisible) && _graph is not null)
            IsVisible = _graph.IsVisible;
        if (e.PropertyName is nameof(ScoreGraphViewModel.IsVisible)
            or nameof(ScoreGraphViewModel.PanelWidth) or nameof(ScoreGraphViewModel.PanelHeight))
            Clamp();
        if (e.PropertyName == nameof(ScoreGraphViewModel.IsCharactersOpen))
            PlaceList(CharactersList, CharactersControl);
        else if (e.PropertyName == nameof(ScoreGraphViewModel.IsServersOpen))
            PlaceList(ServersList, ServersControl);
        else if (e.PropertyName == nameof(ScoreGraphViewModel.IsScaleOpen))
            PlaceList(ScaleList, ScaleControl);
    }

    private void ReplaceOpenList()
    {
        if (_graph is null) return;
        if (_graph.IsCharactersOpen) PlaceList(CharactersList, CharactersControl);
        if (_graph.IsServersOpen) PlaceList(ServersList, ServersControl);
        if (_graph.IsScaleOpen) PlaceList(ScaleList, ScaleControl);
    }

    /// <summary>Opens a drop-down directly under the control that opened it. The controls wrap, so
    /// where a control sits is known only once it is laid out; it is read when the list opens. The
    /// lists span the controls' row and the graph's, with the controls' origin as theirs.</summary>
    private static void PlaceList(View list, View control)
        => list.Margin = new Thickness(Math.Max(0, control.X), Math.Max(0, control.Y + control.Height + control.Margin.Bottom), 0, 0);

    /// <summary>The summary table's widest: its columns' sum on a desktop.</summary>
    private const double SummaryWidth = 616;

    /// <summary>The corner grip. The panel is centred, so the width moves by twice the drag to keep
    /// its right edge under the pointer. The size is saved when the drag ends.</summary>
    private void OnScorePanelResize(object? sender, PanUpdatedEventArgs e)
        => ResizeDrag(e.StatusType, e.TotalX, e.TotalY);

    private void ResizeDrag(GestureStatus status, double totalX, double totalY)
    {
        if (_graph is null) return;
        // A click on the grip that never becomes a drag ends without starting; it saves nothing.
        if (status == GestureStatus.Started) _resizing = true;
        else if (!_resizing) return;
        if (status is GestureStatus.Completed or GestureStatus.Canceled) _resizing = false;
        switch (status)
        {
            case GestureStatus.Started:
                _resizeStartW = _graph.PanelWidth;
                _resizeStartH = _graph.PanelHeight;
                break;
            case GestureStatus.Running:
                var (maxW, maxH) = Room();
                _graph.Resize(_resizeStartW + 2 * totalX, _resizeStartH + totalY, maxW, maxH);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _graph.EndResize();
                break;
        }
    }

    /// <summary>The room the host gives the panel, less the panel's margins.</summary>
    private (double Width, double Height) Room()
    {
        var m = ScorePanel.Margin;
        return _host is { Width: > 0, Height: > 0 } h
            ? (h.Width - m.Left - m.Right, h.Height - m.Top - m.Bottom)
            : (double.MaxValue, double.MaxValue);
    }

    /// <summary>Keeps a remembered size inside a smaller window: a size saved on a wide screen is
    /// shrunk to fit here, and grows back only when the user drags it.
    ///
    /// <para>A room narrower than the panel's minimum (a phone) gets the panel at its full size,
    /// height as well as width, and no grip: there is nothing to drag it into. That size is not
    /// saved, so a desktop keeps its own.</para></summary>
    private void Clamp()
    {
        if (_graph is not { IsVisible: true } graph) return;
        var (maxW, maxH) = Room();
        var narrow = maxW < ScoreGraphViewModel.MinWidth;
        if (narrow)
            graph.Resize(maxW, maxH, maxW, maxH);
        else
            graph.Resize(graph.PanelWidth, graph.PanelHeight, maxW, maxH);
        ResizeGrip.IsVisible = !narrow;

        var inner = graph.PanelWidth - ScorePanel.Padding.Left - ScorePanel.Padding.Right;
        SummaryTable.WidthRequest = Math.Max(0, Math.Min(SummaryWidth, inner));
        SummaryRows.MaximumHeightRequest = (narrow ? MinSummaryRows : SummaryRowsFor(graph.PanelHeight)) * SummaryRowHeight;
    }

    /// <summary>The XAML's header and row height (the HeightRequest of the header grid and of the row
    /// template, and the Y2 of their rules).</summary>
    private const double SummaryRowHeight = 18;
    private const int MinSummaryRows = 3;
    private const double SummaryShareOfPanel = 0.3;

    /// <summary>How many summary rows show before the table scrolls: <see cref="MinSummaryRows"/>,
    /// or as many as fit in <see cref="SummaryShareOfPanel"/> of the panel's height once the header
    /// row is taken out. A narrow room (a phone) always gets <see cref="MinSummaryRows"/>.</summary>
    private static int SummaryRowsFor(double panelHeight)
        => Math.Max(MinSummaryRows, (int)((panelHeight * SummaryShareOfPanel - SummaryRowHeight) / SummaryRowHeight));

#if MACCATALYST
    private readonly List<UIKit.UIGestureRecognizer> _macResize = [];

    /// <summary>How far from the panel's bottom-right corner a press still starts a resize.</summary>
    private const double MacGripReach = 36;

    // MAUI's pan recognizer takes direct touches only, which a mouse is not on a Mac, so the panel
    // gets a native one that accepts the pointer. It is on the panel rather than the grip label,
    // which sits partly outside its cell (TranslationX/Y) where UIKit's hit test does not reach, and
    // it begins only for a press in the corner. The drag is measured in window coordinates: the
    // panel is centred, so the corner moves under the pointer as the panel grows.
    private void InstallMacResize()
    {
        if (ScorePanel.Handler?.PlatformView is not UIKit.UIView view) return;
        foreach (var old in _macResize)
            view.RemoveGestureRecognizer(old);
        _macResize.Clear();
        var pan = new UIKit.UIPanGestureRecognizer(g =>
        {
            var t = g.TranslationInView(g.View?.Window);
            if (g.State != UIKit.UIGestureRecognizerState.Changed)
                Mucka.Core.CrashLog.Info("score-resize", $"{g.State} {t.X:0} {t.Y:0}");
            switch (g.State)
            {
                case UIKit.UIGestureRecognizerState.Began:
                    ResizeDrag(GestureStatus.Started, 0, 0);
                    break;
                case UIKit.UIGestureRecognizerState.Changed:
                    ResizeDrag(GestureStatus.Running, t.X, t.Y);
                    break;
                case UIKit.UIGestureRecognizerState.Ended:
                    ResizeDrag(GestureStatus.Completed, t.X, t.Y);
                    break;
                case UIKit.UIGestureRecognizerState.Cancelled:
                case UIKit.UIGestureRecognizerState.Failed:
                    ResizeDrag(GestureStatus.Canceled, t.X, t.Y);
                    break;
            }
        })
        {
            AllowedTouchTypes =
            [
                Foundation.NSNumber.FromInt32((int)UIKit.UITouchType.IndirectPointer),
                Foundation.NSNumber.FromInt32((int)UIKit.UITouchType.Direct),
            ],
            CancelsTouchesInView = false,
            ShouldRecognizeSimultaneously = (_, _) => true,
        };
        pan.ShouldReceiveTouch = (_, touch) =>
        {
            if (!ResizeGrip.IsVisible) return false;
            var p = touch.LocationInView(view);
            return p.X >= view.Bounds.Width - MacGripReach && p.Y >= view.Bounds.Height - MacGripReach;
        };
        view.AddGestureRecognizer(pan);
        _macResize.Add(pan);
    }
#endif
}
