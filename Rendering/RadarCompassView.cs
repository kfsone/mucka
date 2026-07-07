using System.Windows.Input;
using Mucka.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Mucka.Rendering;

/// <summary>
/// The room-exits "radar" — a printed-compass-card rendering of the 12 non-swamp exits.
/// (Swampward is drawn separately as the marshy seam below, see <see cref="SwampSeamView"/>.)
///
/// Eight ordinal wedges ring a small core carrying up/down (chevrons) and out/in (O / I).
/// A wedge tile is faintly present at rest and warms to parchment when its exit is open;
/// its letter is only painted when the exit is available. North is deep red, south deep
/// blue; cardinals are upper-case, the diagonals lower-case — all read as printing on the
/// lit parchment tile.
///
/// Geometry is authored in a 120×120 space and the canvas is scaled to the control's box —
/// non-uniformly, so a wider-than-tall box renders a horizontal oval (the compact float step).
///
/// Open exits are clickable (<see cref="MoveCommand"/> is invoked with the direction keyword),
/// and hovering an open exit highlights it so the interactivity is discoverable.
/// </summary>
public sealed class RadarCompassView : SKCanvasView
{
    // ── Geometry (120-space) ─────────────────────────────────────────────────
    private const float CX = 60, CY = 60;   // centre
    private const float R  = 50, RI = 36;   // ring outer / inner radius
    private const float LBLR = 44;          // label radius (letters sit over their wedge)
    private const float HALF = 20;          // wedge half-angle (±20° → 40° wedge)
    private const float PAD  = 3;           // breathing room around the ring (120-space units)
    private const float CONTENT = 2 * R + 2 * PAD;   // the box the rose is scaled to fill

    // ── Palette ──────────────────────────────────────────────────────────────
    private static readonly SKColor SegOff  = new(120, 110, 92, 18);     // faint tile at rest
    private static readonly SKColor SegOn    = new(183, 161, 116, 230);   // warm parchment when open
    private static readonly SKColor InkNorth = new(0x7f, 0x00, 0x00);     // deep red
    private static readonly SKColor InkSouth = new(0x00, 0x13, 0x7f);     // deep blue
    private static readonly SKColor InkOrd   = new(0x11, 0x11, 0x11);     // near-black ordinals
    private static readonly SKColor Core     = new(0xe8, 0xc4, 0x63);     // gold core glyphs (on dark centre)
    private static readonly SKColor CoreRest = new(0xe8, 0xc4, 0x63, 55); // core glyph, dim at rest
    private static readonly SKColor Hover    = new(255, 255, 255, 46);    // hover highlight overlay

    // Compass bearing (clockwise from north), label, and whether it is a diagonal.
    private static readonly (string Dir, float Deg, string Label, bool Diag)[] Ordinals =
    {
        ("north",       0, "N",  false),
        ("northeast",  45, "ne", true),
        ("east",       90, "E",  false),
        ("southeast", 135, "se", true),
        ("south",     180, "S",  false),
        ("southwest", 225, "sw", true),
        ("west",      270, "W",  false),
        ("northwest", 315, "nw", true),
    };

    // Core-glyph anchors, used for both painting and hit-testing.
    private static readonly (string Dir, float X, float Y)[] CoreAnchors =
    {
        ("up",   CX,      CY - 8),
        ("down", CX,      CY + 8),
        ("out",  CX - 15, CY),
        ("in",   CX + 15, CY),
    };

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _text = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKFont  _font = new(
        SKTypeface.FromFamilyName("Georgia", SKFontStyle.Bold) ?? SKTypeface.Default);

    private string? _hoverDir;
    private SKPoint _pressLoc;
    private bool _pressed;

    public RadarCompassView()
    {
        PaintSurface += OnPaintSurface;

        EnableTouchEvents = true;
        Touch += OnTouch;

        // Pointer hover (desktop) — highlights the exit under the cursor.
        var pointer = new PointerGestureRecognizer();
        pointer.PointerMoved += OnPointerMoved;
        pointer.PointerExited += OnPointerExited;
        GestureRecognizers.Add(pointer);
    }

    // ── Data source & command ────────────────────────────────────────────────
    public static readonly BindableProperty SidePanelProperty = BindableProperty.Create(
        nameof(SidePanel), typeof(SidePanelViewModel), typeof(RadarCompassView), null,
        propertyChanged: OnSidePanelChanged);

    public SidePanelViewModel? SidePanel
    {
        get => (SidePanelViewModel?)GetValue(SidePanelProperty);
        set => SetValue(SidePanelProperty, value);
    }

    public static readonly BindableProperty MoveCommandProperty = BindableProperty.Create(
        nameof(MoveCommand), typeof(ICommand), typeof(RadarCompassView), null);

    /// <summary>Invoked with a direction keyword ("north", "up", "swampward"…) when an open exit is clicked.</summary>
    public ICommand? MoveCommand
    {
        get => (ICommand?)GetValue(MoveCommandProperty);
        set => SetValue(MoveCommandProperty, value);
    }

    private static void OnSidePanelChanged(BindableObject obj, object oldValue, object newValue)
    {
        var view = (RadarCompassView)obj;
        if (oldValue is SidePanelViewModel oldVm) view.Unsubscribe(oldVm);
        if (newValue is SidePanelViewModel newVm) view.Subscribe(newVm);
        view.InvalidateSurface();
    }

    private void Subscribe(SidePanelViewModel vm)
    {
        foreach (var ind in Indicators(vm))
            ind.PropertyChanged += OnIndicatorChanged;
    }

    private void Unsubscribe(SidePanelViewModel vm)
    {
        foreach (var ind in Indicators(vm))
            ind.PropertyChanged -= OnIndicatorChanged;
    }

    // The twelve exits the radar draws (swampward is the seam, not a wedge).
    private static IEnumerable<ExitIndicator> Indicators(SidePanelViewModel vm)
    {
        yield return vm.ExitNorth;     yield return vm.ExitNorthEast; yield return vm.ExitEast;
        yield return vm.ExitSouthEast; yield return vm.ExitSouth;     yield return vm.ExitSouthWest;
        yield return vm.ExitWest;      yield return vm.ExitNorthWest;
        yield return vm.ExitUp;        yield return vm.ExitDown;
        yield return vm.ExitIn;        yield return vm.ExitOut;
    }

    private void OnIndicatorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExitIndicator.Present))
            InvalidateSurface();
    }

    // ── Paint ────────────────────────────────────────────────────────────────
    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var vm = SidePanel;
        if (vm is null || e.Info.Width <= 0 || e.Info.Height <= 0) return;

        canvas.Save();
        // Scale the rose to fill the box (minus a little PAD), centred. Non-uniform, so a
        // wider-than-tall box squishes it into a horizontal oval.
        canvas.Translate(e.Info.Width / 2f, e.Info.Height / 2f);
        canvas.Scale(e.Info.Width / CONTENT, e.Info.Height / CONTENT);
        canvas.Translate(-CX, -CY);

        // Wedge tiles first, so letters sit over them.
        foreach (var (dir, deg, _, _) in Ordinals)
        {
            using var path = Wedge(deg);
            _fill.Color = Present(vm, dir) ? SegOn : SegOff;
            canvas.DrawPath(path, _fill);
            if (dir == _hoverDir)
            {
                _fill.Color = Hover;
                canvas.DrawPath(path, _fill);
            }
        }

        // Ordinal letters — only when the exit is open.
        foreach (var (dir, deg, label, diag) in Ordinals)
        {
            if (!Present(vm, dir)) continue;
            var color = dir == "north" ? InkNorth : dir == "south" ? InkSouth : InkOrd;
            var (lx, ly) = P(LBLR, deg);
            float rot = diag ? (dir is "northeast" or "southwest" ? 45f : -45f) : 0f;
            DrawGlyph(canvas, label, lx, ly, diag ? 9f : 11f, color, rot);
        }

        // Core: up / down chevrons, out / in letters. Dim at rest, gold when open.
        DrawCoreHover(canvas);
        DrawChevron(canvas, up: true,  Present(vm, "up"));
        DrawChevron(canvas, up: false, Present(vm, "down"));
        DrawGlyph(canvas, "O", CX - 15, CY, 11f, Present(vm, "out") ? Core : CoreRest, 0f);
        DrawGlyph(canvas, "I", CX + 15, CY, 11f, Present(vm, "in")  ? Core : CoreRest, 0f);

        canvas.Restore();
    }

    private void DrawCoreHover(SKCanvas canvas)
    {
        if (_hoverDir is not ("up" or "down" or "in" or "out")) return;
        foreach (var (dir, ax, ay) in CoreAnchors)
        {
            if (dir != _hoverDir) continue;
            _fill.Color = Hover;
            canvas.DrawCircle(ax, ay, 10f, _fill);
        }
    }

    private static bool Present(SidePanelViewModel vm, string dir) => dir switch
    {
        "north"     => vm.ExitNorth.Present,
        "northeast" => vm.ExitNorthEast.Present,
        "east"      => vm.ExitEast.Present,
        "southeast" => vm.ExitSouthEast.Present,
        "south"     => vm.ExitSouth.Present,
        "southwest" => vm.ExitSouthWest.Present,
        "west"      => vm.ExitWest.Present,
        "northwest" => vm.ExitNorthWest.Present,
        "up"        => vm.ExitUp.Present,
        "down"      => vm.ExitDown.Present,
        "in"        => vm.ExitIn.Present,
        "out"       => vm.ExitOut.Present,
        _           => false,
    };

    // Point on the compass at radius r and bearing deg (0 = north/top, clockwise).
    private static (float x, float y) P(float r, float deg)
    {
        double a = (deg - 90) * Math.PI / 180.0;
        return (CX + r * (float)Math.Cos(a), CY + r * (float)Math.Sin(a));
    }

    // A 40°-wide wedge of the ring at bearing deg.
    private static SKPath Wedge(float deg)
    {
        var outer = new SKRect(CX - R,  CY - R,  CX + R,  CY + R);
        var inner = new SKRect(CX - RI, CY - RI, CX + RI, CY + RI);
        var (ox, oy) = P(R, deg - HALF);
        var path = new SKPath();
        path.MoveTo(ox, oy);
        path.ArcTo(outer, (deg - HALF) - 90, 2 * HALF, false);   // outer arc, clockwise
        var (ix, iy) = P(RI, deg + HALF);
        path.LineTo(ix, iy);
        path.ArcTo(inner, (deg + HALF) - 90, -2 * HALF, false);  // inner arc, back
        path.Close();
        return path;
    }

    private void DrawGlyph(SKCanvas canvas, string text, float cx, float cy, float size, SKColor color, float rotate)
    {
        _font.Size = size;
        float w = _font.MeasureText(text);
        var m = _font.Metrics;
        float baseY = cy - (m.Ascent + m.Descent) / 2f;   // vertical centre
        _text.Color = color;
        canvas.Save();
        if (rotate != 0f) canvas.RotateDegrees(rotate, cx, cy);
        canvas.DrawText(text, cx - w / 2f, baseY, _font, _text);
        canvas.Restore();
    }

    private void DrawChevron(SKCanvas canvas, bool up, bool present)
    {
        float apex = up ? CY - 12 : CY + 12;
        float baseY = up ? CY - 4 : CY + 4;
        using var tri = new SKPath();
        tri.MoveTo(CX, apex);
        tri.LineTo(CX - 6, baseY);
        tri.LineTo(CX + 6, baseY);
        tri.Close();
        _fill.Color = present ? Core : CoreRest;
        canvas.DrawPath(tri, _fill);
    }

    // ── Hit-testing (canvas pixels → direction keyword) ──────────────────────
    private string? HitTest(float pxX, float pxY)
    {
        var size = CanvasSize;   // device pixels
        if (size.Width <= 0 || size.Height <= 0) return null;
        // Inverse of the paint transform (centre-anchored, scaled to CONTENT).
        float x = CX + (pxX - (float)size.Width  / 2f) * CONTENT / (float)size.Width;
        float y = CY + (pxY - (float)size.Height / 2f) * CONTENT / (float)size.Height;
        float dx = x - CX, dy = y - CY;
        float r = (float)Math.Sqrt(dx * dx + dy * dy);

        if (r >= RI - 2 && r <= R + 2)
        {
            float deg = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI) + 90f;   // → compass bearing
            deg = (deg % 360f + 360f) % 360f;
            foreach (var (dir, bd, _, _) in Ordinals)
            {
                float diff = Math.Abs(((deg - bd + 540f) % 360f) - 180f);
                if (diff <= HALF) return dir;
            }
            return null;
        }

        if (r < RI)
        {
            string? best = null; float bestD = 12f;
            foreach (var (dir, ax, ay) in CoreAnchors)
            {
                float d = (float)Math.Sqrt((x - ax) * (x - ax) + (y - ay) * (y - ay));
                if (d < bestD) { bestD = d; best = dir; }
            }
            return best;
        }
        return null;
    }

    // ── Click ─────────────────────────────────────────────────────────────────
    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _pressLoc = e.Location;
                _pressed = true;
                e.Handled = true;
                break;
            case SKTouchAction.Released:
                if (_pressed)
                {
                    var d = e.Location - _pressLoc;
                    if (Math.Abs(d.X) < 10 && Math.Abs(d.Y) < 10)
                    {
                        // Every direction is clickable, open or not — you must be able to leave
                        // a dark room whose exits the server hasn't disclosed. A miss still fires
                        // (null) so MoveCommand can hand focus straight back to the input box.
                        var dir = HitTest(e.Location.X, e.Location.Y);
                        MoveCommand?.Execute(dir);
                    }
                }
                _pressed = false;
                e.Handled = true;
                break;
            case SKTouchAction.Cancelled:
                _pressed = false;
                break;
        }
    }

    // ── Hover ───────────────────────────────────────────────────────────────
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var pt = e.GetPosition(this);
        string? dir = null;
        if (pt is { } p && Width > 0)
        {
            // GetPosition is in DIPs; scale to device pixels for the hit-test.
            // Highlight any direction under the cursor — all are clickable, open or not.
            float density = (float)(CanvasSize.Width / Width);
            dir = HitTest((float)p.X * density, (float)p.Y * density);
        }
        if (dir != _hoverDir) { _hoverDir = dir; InvalidateSurface(); }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (_hoverDir is null) return;
        _hoverDir = null;
        InvalidateSurface();
    }
}
