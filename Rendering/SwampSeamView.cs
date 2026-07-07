using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Mucka.Rendering;

/// <summary>
/// The marshy "swamp" seam: a low band of hummocks that doubles as the separator between the
/// compass and the room block. It is faint at rest and rises to full ink when a swampward exit
/// is open — the compass's thirteenth direction, rendered as ground rather than a wedge.
///
/// Authored in a 100×14 space and stretched to the control's box.
/// </summary>
public sealed class SwampSeamView : SKCanvasView
{
    private static readonly SKColor Mud   = new(0x20, 0x29, 0x1a);
    private static readonly SKColor Moss  = new(0x5f, 0x8a, 0x49);
    private static readonly SKColor Label = new(0xff, 0xff, 0xff, 179);  // white, ~0.7 alpha
    private static readonly SKColor Hover = new(0xff, 0xff, 0xff, 36);   // hover wash

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKFont  _font = new(
        SKTypeface.FromFamilyName("Georgia", SKFontStyle.Italic) ?? SKTypeface.Default) { Size = 9f };

    private bool _hover;

    public SwampSeamView()
    {
        PaintSurface += OnPaintSurface;

        var pointer = new PointerGestureRecognizer();
        pointer.PointerEntered += (_, _) => SetHover(true);
        pointer.PointerExited  += (_, _) => SetHover(false);
        GestureRecognizers.Add(pointer);
    }

    private void SetHover(bool value)
    {
        if (_hover == value) return;
        _hover = value;
        InvalidateSurface();
    }

    /// <summary>True when a swampward exit is open — the seam paints at full opacity.</summary>
    public static readonly BindableProperty IsLitProperty = BindableProperty.Create(
        nameof(IsLit), typeof(bool), typeof(SwampSeamView), false,
        propertyChanged: (o, _, _) => ((SwampSeamView)o).InvalidateSurface());

    public bool IsLit
    {
        get => (bool)GetValue(IsLitProperty);
        set => SetValue(IsLitProperty, value);
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (e.Info.Width <= 0 || e.Info.Height <= 0) return;

        float dim = IsLit ? 1f : 0.35f;

        canvas.Save();
        canvas.Scale(e.Info.Width / 100f, e.Info.Height / 14f);

        using (var mud = new SKPath())
        {
            mud.MoveTo(0, 14); mud.LineTo(0, 9);
            mud.QuadTo(7, 4, 15, 8);
            mud.QuadTo(22, 3, 30, 7);
            mud.QuadTo(39, 2, 48, 7);
            mud.QuadTo(56, 3, 65, 7);
            mud.QuadTo(74, 3, 83, 8);
            mud.QuadTo(92, 4, 100, 8);
            mud.LineTo(100, 14); mud.Close();
            _fill.Color = Mud.WithAlpha((byte)(255 * dim));
            canvas.DrawPath(mud, _fill);
        }

        using (var moss = new SKPath())
        {
            moss.MoveTo(0, 14); moss.LineTo(0, 11);
            moss.QuadTo(10, 8, 20, 11);
            moss.QuadTo(30, 8, 40, 11);
            moss.QuadTo(50, 8, 60, 11);
            moss.QuadTo(70, 8, 80, 11);
            moss.QuadTo(90, 8, 100, 11);
            moss.LineTo(100, 14); moss.Close();
            _fill.Color = Moss.WithAlpha((byte)(128 * dim));
            canvas.DrawPath(moss, _fill);
        }

        // When a swampward exit is open, name it — a faint white "swamp" over the marsh.
        if (IsLit)
        {
            const string text = "swamp";
            float w = _font.MeasureText(text);
            var m = _font.Metrics;
            float baseY = 8f - (m.Ascent + m.Descent) / 2f;   // vertical centre near the band's middle
            _fill.Color = Label;
            canvas.DrawText(text, 50f - w / 2f, baseY, _font, _fill);
        }

        // Hover wash — the swamp is clickable, open or not.
        if (_hover)
        {
            _fill.Color = Hover;
            canvas.DrawRect(0, 2, 100, 12, _fill);
        }

        canvas.Restore();
    }
}
