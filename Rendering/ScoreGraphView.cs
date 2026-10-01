using Mucka.Combat;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Mucka.Rendering;

/// <summary>
/// The $SCORE graph: holds the scene, tracks the pointer, and hands both to
/// <see cref="ScoreGraphPainter"/>.
///
/// <para>The plot is rebuilt when the scene or the size changes, never per paint. The pointer only
/// repaints when the reading it snaps to changes.</para>
/// </summary>
public sealed class ScoreGraphView : SKCanvasView
{
    /// <summary>How far, in plot units, the pointer reaches for a reading to snap to.</summary>
    private const double HoverReach = 24;
    /// <summary>The same for the marks in the strip above the plot, which sit a few units apart.</summary>
    private const double MarkReach = 6;
    /// <summary>How close to a climb or drop's extent the pointer must be to pick it.</summary>
    private const double MoveReach = 5;

    private readonly ScoreGraphPainter _painter = new();
    private ScoreGraphPlot? _plot;
    private (ScoreGraphScene? Scene, double W, double H) _plotKey;
    private ScoreHover? _hover;

    /// <summary>The colour series <paramref name="index"/> is drawn in.</summary>
    public static Color SeriesColor(int index)
        => Color.FromArgb(ScoreGraphPainter.SeriesHex[index % ScoreGraphPainter.SeriesHex.Length]);

    public ScoreGraphView()
    {
        PaintSurface += OnPaintSurface;

        // A press and drag across the plot selects a stretch of time to show; a press that barely
        // moves is a click, and the hover answers it as usual.
        var pointer = new PointerGestureRecognizer();
        pointer.PointerPressed += (_, e) => DragFrom(e.GetPosition(this));
        pointer.PointerMoved += (_, e) =>
        {
            if (!DragTo(e.GetPosition(this)))
                HoverAt(e.GetPosition(this));
        };
        pointer.PointerReleased += (_, e) => DragEnd(e.GetPosition(this));
        pointer.PointerExited += (_, _) =>
        {
            _dragFrom = null;
            SetDrag(null);
            SetHover(null);
        };
        GestureRecognizers.Add(pointer);
        // Touch has no hover; a tap reads the value under the finger instead.
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, e) => HoverAt(e.GetPosition(this));
        GestureRecognizers.Add(tap);
    }

    public static readonly BindableProperty SceneProperty = BindableProperty.Create(
        nameof(Scene), typeof(ScoreGraphScene), typeof(ScoreGraphView), null,
        propertyChanged: (o, _, _) =>
        {
            var view = (ScoreGraphView)o;
            view._hover = null;
            view.InvalidateSurface();
        });

    public ScoreGraphScene? Scene
    {
        get => (ScoreGraphScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public static readonly BindableProperty SelectSpanCommandProperty = BindableProperty.Create(
        nameof(SelectSpanCommand), typeof(System.Windows.Input.ICommand), typeof(ScoreGraphView));

    /// <summary>Run with the <see cref="TimeSpanMs"/> a drag across the plot selected.</summary>
    public System.Windows.Input.ICommand? SelectSpanCommand
    {
        get => (System.Windows.Input.ICommand?)GetValue(SelectSpanCommandProperty);
        set => SetValue(SelectSpanCommandProperty, value);
    }

    /// <summary>How far, in plot units, a press must move to be a drag rather than a click.</summary>
    private const double DragSlop = 6;

    private double? _dragFrom;
    private (double X0, double X1)? _drag;

    private void DragFrom(Point? position)
    {
        _dragFrom = position is { } p && _plot is not null && p.X >= _plot.Left && p.X <= _plot.Right && p.Y >= _plot.Top && p.Y <= _plot.Bottom
            ? p.X
            : null;
    }

    /// <summary>True while a drag is under way, so the pointer is not also hovering.</summary>
    private bool DragTo(Point? position)
    {
        if (_dragFrom is not { } from || position is not { } p || _plot is null)
            return false;
        var x = Math.Clamp(p.X, _plot.Left, _plot.Right);
        if (_drag is null && Math.Abs(x - from) < DragSlop)
            return false;
        SetHover(null);
        SetDrag((Math.Min(from, x), Math.Max(from, x)));
        return true;
    }

    private void DragEnd(Point? position)
    {
        DragTo(position);
        var drag = _drag;
        _dragFrom = null;
        SetDrag(null);
        if (drag is not { } d || _plot is null || d.X1 - d.X0 < DragSlop)
            return;
        var span = new TimeSpanMs(_plot.Axis.Unmap(d.X0), _plot.Axis.Unmap(d.X1));
        if (span.EndMs > span.StartMs && SelectSpanCommand?.CanExecute(span) == true)
            SelectSpanCommand.Execute(span);
    }

    private void SetDrag((double X0, double X1)? drag)
    {
        if (Nullable.Equals(_drag, drag))
            return;
        _drag = drag;
        InvalidateSurface();
    }

    private void HoverAt(Point? position)
    {
        if (position is not { } p || _plot is null)
            return;
        if (p.X < _plot.Left - HoverReach || p.X > _plot.Right + HoverReach || p.Y > _plot.Bottom)
        {
            SetHover(null);
            return;
        }
        // Above the plot is the mark strip; the marks are small, so the reach there is tight.
        if (p.Y < _plot.Top)
        {
            SetHover(_plot.MarkNear(p.X, MarkReach) is { } mark ? new ScoreHover(mark.Series, null, mark) : null);
            return;
        }
        // On a callout, what it stands for: one change's own card, or every change gathered into it.
        if (_painter.TallyAt(p.X, p.Y) is { } tally)
        {
            SetHover(tally switch
            {
                { Moves: [var only], Buckets: [] } => new ScoreHover(tally.Series, null, null, only, only.Stairs.Count - 1, tally),
                { Moves: [], Buckets: [var bucket] } => new ScoreHover(tally.Series, bucket, null, Tally: tally),
                _ => new ScoreHover(tally.Series, null, null, Tally: tally),
            });
            return;
        }
        // On a climb or drop, its total; anywhere else on the plot, the score held at that moment.
        if (_plot.MoveAt(p.X, p.Y, MoveReach) is { } move)
        {
            SetHover(new ScoreHover(move.Series, null, null, move, ScoreGraphPlot.StairNear(move, p.X, p.Y)));
            return;
        }
        SetHover(_plot.Nearest(p.X, HoverReach) is { } hit ? new ScoreHover(hit.Series, hit.Reading, null) : null);
    }

    private void SetHover(ScoreHover? hover)
    {
        if (Nullable.Equals(_hover, hover))
            return;
        _hover = hover;
        InvalidateSurface();
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var scene = Scene;
        if (scene is null || Width <= 0 || Height <= 0 || e.Info.Width <= 0) return;

        // A paint handler that throws takes the whole app with it; a graph that cannot draw is logged
        // and left blank instead.
        try
        {
            if (_plot is null || !ReferenceEquals(_plotKey.Scene, scene) || _plotKey.W != Width || _plotKey.H != Height)
            {
                _plot = ScoreGraphPlot.Build(scene, Width, Height, TimeZoneInfo.Local);
                _plotKey = (scene, Width, Height);
                // What the pointer was on, and any drag, belong to the old layout.
                _hover = null;
                _dragFrom = null;
                _drag = null;
            }
            _painter.Paint(canvas, (float)(e.Info.Width / Width), _plot, scene, _hover, _drag);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or ArithmeticException)
        {
            _plot = null;
            Mucka.Core.CrashLog.Write("ScoreGraphView.Paint", ex);
        }
    }
}
