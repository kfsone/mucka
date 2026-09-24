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

    public ScorePanelView()
    {
        InitializeComponent();
        // Hidden until a view model says otherwise - see OnBindingContextChanged.
        IsVisible = false;
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
    }

    /// <summary>The corner grip. The panel is centred, so the width moves by twice the drag to keep
    /// its right edge under the pointer. The size is saved when the drag ends.</summary>
    private void OnScorePanelResize(object? sender, PanUpdatedEventArgs e)
    {
        if (_graph is null) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _resizeStartW = _graph.PanelWidth;
                _resizeStartH = _graph.PanelHeight;
                break;
            case GestureStatus.Running:
                var (maxW, maxH) = Room();
                _graph.Resize(_resizeStartW + 2 * e.TotalX, _resizeStartH + e.TotalY, maxW, maxH);
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
    /// shrunk to fit here, and grows back only when the user drags it.</summary>
    private void Clamp()
    {
        if (_graph is not { IsVisible: true } graph) return;
        var (maxW, maxH) = Room();
        graph.Resize(graph.PanelWidth, graph.PanelHeight, maxW, maxH);
    }
}
