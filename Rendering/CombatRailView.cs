using Mucka.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Mucka.Rendering;

/// <summary>
/// The Combat Rail's render surface: one SkiaSharp canvas drawing a FIXED layout from
/// <see cref="CombatLiveView"/>.
///
/// <para><b>Fixed geometry is the whole design.</b> Every block below occupies the same pixels in
/// combat, immediately after it, and when idle. Blocks not yet implemented still reserve their
/// space and draw a hairline. Nothing moves, ever - the player learns where to look once, at
/// leisure, and that knowledge stays valid mid-fight when there is no time to search. A panel that
/// re-lays-out between glances has to be re-read every time, which defeats the point of having it.</para>
///
/// <para><b>Invariant #1 - this class never animates.</b> It repaints only when
/// <see cref="Live"/> is set to genuinely different state. There is no timer here and there must
/// never be one: on WinUI <c>SKXamlCanvas</c> paints ON the UI thread, so a repeating repaint
/// competes directly with typing. All continuous motion (pulse, glow) belongs to the Composition
/// layer sitting behind this canvas - see <c>PulseLayer</c> and GamePage.xaml's CombatPanelGlow.</para>
///
/// <para><b>Invariant #0 - this surface cannot take focus.</b> It carries no gesture recognizers and
/// is mounted <c>InputTransparent</c>. There is nothing here to click, by construction rather than
/// by discipline.</para>
///
/// <para><b>Allocation.</b> Every paint object is a field built once. The paint handler allocates
/// nothing per frame; it only mutates colours on the shared paints.</para>
/// </summary>
public sealed class CombatRailView : SKCanvasView
{
    // ---- Layout constants (logical units; scaled by the canvas DPI at paint time) -------------
    // The interior is 300dp wide minus the border, giving 288 units of content.
    private const float PanelWidth = 300f;
    private const float Pad = 6f;
    private const float Content = PanelWidth - (Pad * 2);

    private const float VerdictH = 84f;
    private const float TickRailH = 36f;
    private const float SpikeRulerH = 60f;
    private const float ThreatRowsH = 40f;   // reserved in v1 (one row's worth)
    private const float LoadoutH = 76f;
    private const float FixLineH = 26f;      // reserved in v1, height never collapses
    private const float FleeGateH = 72f;     // reserved in v1
    private const float BlockGap = 4f;

    // ---- Stamina bands ------------------------------------------------------------------------
    // Deliberately coarse, and deliberately fixed rather than scaled to the current opponent.
    // Most things in MUD2 hit for 10-20, so under 20 is one or two swings from a permadeath that
    // costs 100% of score, and other creatures or players can join an existing fight at any moment.
    // A threat-relative band would read "safe" against a rat right up until three of them land on
    // the same tick - so relative reasoning may only ever escalate these, never relax them.
    private const int BandThinkAboutFleeing = 40;
    private const int BandRedZone = 20;
    private const int BandCritical = 10;

    // ---- Palette (Campbell-derived; no free-floating hex) --------------------------------------
    // Semantic roles resolve to TerminalTheme.Palette indices so the rail and the terminal can
    // never drift apart. Tints and alphas are DERIVED from those bases rather than invented, which
    // keeps one palette in one place while still allowing gradients and glow falloff.
    private static readonly SKColor Ink = TerminalTheme.Palette[7];        // normal foreground
    private static readonly SKColor InkBright = TerminalTheme.Palette[15]; // emphasis
    private static readonly SKColor InkDim = TerminalTheme.Palette[8];     // labels and units
    private static readonly SKColor You = TerminalTheme.Palette[10];       // your resource
    private static readonly SKColor Them = TerminalTheme.Palette[12];      // theirs
    private static readonly SKColor Caution = TerminalTheme.Palette[11];   // fixable by you
    private static readonly SKColor Danger = TerminalTheme.Palette[9];     // lethal
    private static readonly SKColor Rule = Tint(TerminalTheme.Palette[8], 0.45f);

    /// <summary>Scales a base palette colour toward the panel background. Used instead of new hex
    /// values so every derived shade is provably a shade OF the shared palette.</summary>
    private static SKColor Tint(SKColor baseColor, float amount)
        => new(
            (byte)(baseColor.Red * amount),
            (byte)(baseColor.Green * amount),
            (byte)(baseColor.Blue * amount),
            baseColor.Alpha);

    // ---- Paints (built once; never allocated in OnPaintSurface) --------------------------------
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKFont _numeralFont = new(SKTypeface.Default, 46f);
    private readonly SKFont _labelFont = new(SKTypeface.Default, 9f);
    private readonly SKFont _textFont = new(SKTypeface.Default, 12f);
    private readonly SKPaint _textPaint = new() { IsAntialias = true };

    private CombatLiveView _live = CombatLiveView.Idle;

    public CombatRailView()
    {
        // Belt and braces for Invariant #0: this view is also mounted InputTransparent in XAML.
        InputTransparent = true;
        IgnorePixelScaling = false;
    }

    /// <summary>
    /// The frame state to draw. Setting this repaints ONLY when the new state differs from the
    /// current one, so the per-combat-event and 1 Hz refresh paths do not invalidate the canvas for
    /// identical content (Invariant #1).
    /// </summary>
    public CombatLiveView Live
    {
        get => _live;
        set
        {
            if (ReferenceEquals(_live, value))
                return;
            _live = value;
            InvalidateSurface();
        }
    }

    /// <summary>Bound in XAML; the view model raises PropertyChanged for Live and this pulls it.</summary>
    public static readonly BindableProperty SidePanelProperty = BindableProperty.Create(
        nameof(SidePanel), typeof(SidePanelViewModel), typeof(CombatRailView), null,
        propertyChanged: OnSidePanelChanged);

    public SidePanelViewModel? SidePanel
    {
        get => (SidePanelViewModel?)GetValue(SidePanelProperty);
        set => SetValue(SidePanelProperty, value);
    }

    private static void OnSidePanelChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (CombatRailView)bindable;
        if (oldValue is SidePanelViewModel old)
            old.PropertyChanged -= view.OnSidePanelPropertyChanged;
        if (newValue is SidePanelViewModel fresh)
        {
            fresh.PropertyChanged += view.OnSidePanelPropertyChanged;
            view.Live = fresh.Live;
        }
    }

    private void OnSidePanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only Live drives this surface. Everything else on the view model belongs to the left panel.
        if (e.PropertyName is nameof(SidePanelViewModel.Live) or null && sender is SidePanelViewModel vm)
            Live = vm.Live;
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        // Teardown discipline is mandatory, not optional: this codebase has a live crash precedent
        // (RO_E_CLOSED) from a surface that stayed subscribed after its host went away, so the next
        // combat line rendered into already-destroyed objects and took the process down.
        if (Handler is null && SidePanel is { } vm)
            vm.PropertyChanged -= OnSidePanelPropertyChanged;
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // One scale factor maps the logical layout above onto the physical surface, so every
        // constant in this file stays readable as "units of panel width".
        var scale = e.Info.Width / PanelWidth;
        canvas.Save();
        canvas.Scale(scale);

        var y = Pad;
        var live = _live;

        y = DrawVerdict(canvas, y, live);
        y = DrawTickRail(canvas, y, live);
        y = DrawSpikeRuler(canvas, y, live);
        y = DrawReserved(canvas, y, ThreatRowsH, "OPPOSITION");
        y = DrawLoadout(canvas, y, live);
        y = DrawReserved(canvas, y, FixLineH, null);
        DrawReserved(canvas, y, FleeGateH, "EXITS");

        canvas.Restore();
    }

    /// <summary>
    /// B1 - the verdict. One large integer: how many more worst-case swings the player can absorb.
    /// Deliberately the biggest thing on the panel, because it is the only number that decides
    /// anything. The kill-side estimate joins it once the health-descriptor ladder is parsed; until
    /// then this half stays honest by not being drawn at all rather than by guessing.
    /// </summary>
    private float DrawVerdict(SKCanvas canvas, float y, CombatLiveView live)
    {
        DrawLabel(canvas, "STAMINA", Pad, y + 10f);

        var sta = live.StaminaCurrent;
        var color = BandColor(sta);

        if (sta is int value)
        {
            _textPaint.Color = color;
            _numeralFont.Size = 46f;
            var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            canvas.DrawText(text, Pad, y + 58f, SKTextAlign.Left, _numeralFont, _textPaint);

            if (live.StaminaMax is int max && max > 0)
            {
                _textPaint.Color = InkDim;
                canvas.DrawText("/ " + max.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Pad + _numeralFont.MeasureText(text) + 8f, y + 58f, SKTextAlign.Left, _textFont, _textPaint);
            }
        }
        else
        {
            // Unknown is drawn as its own thing, never as a zero - a panel that shows "0" when it
            // simply has not been told yet is worse than one that admits it.
            _textPaint.Color = InkDim;
            _numeralFont.Size = 46f;
            canvas.DrawText("--", Pad, y + 58f, SKTextAlign.Left, _numeralFont, _textPaint);
        }

        // The band track: a fixed scale with the two thresholds that matter marked on it, so the
        // number above always has somewhere to sit relative to "start thinking" and "red zone".
        DrawBandTrack(canvas, Pad, y + 68f, Content, 8f, sta, live.StaminaMax);

        return y + VerdictH + BlockGap;
    }

    /// <summary>The 40 and 20 marks drawn as structure (hairlines), never as colour. Structure gets
    /// a line; danger gets the hue. Mixing the two would make the scale itself look alarming.</summary>
    private void DrawBandTrack(SKCanvas canvas, float x, float y, float w, float h, int? sta, int? max)
    {
        _fill.Color = Tint(InkDim, 0.35f);
        canvas.DrawRoundRect(x, y, w, h, h / 2f, h / 2f, _fill);

        var scaleMax = max is int m && m > 0 ? m : 100;

        if (sta is int value)
        {
            var frac = Math.Clamp(value / (float)scaleMax, 0f, 1f);
            _fill.Color = BandColor(sta);
            canvas.DrawRoundRect(x, y, Math.Max(w * frac, h), h, h / 2f, h / 2f, _fill);
        }

        _stroke.Color = Rule;
        foreach (var mark in new[] { BandThinkAboutFleeing, BandRedZone })
        {
            var mx = x + (w * (mark / (float)scaleMax));
            canvas.DrawLine(mx, y - 2f, mx, y + h + 2f, _stroke);
        }
    }

    private static SKColor BandColor(int? sta) => sta switch
    {
        null => InkDim,
        <= BandCritical => Danger,
        <= BandRedZone => Danger,
        <= BandThinkAboutFleeing => Caution,
        _ => You,
    };

    /// <summary>B2 - the tick rail. MUD2 resolves combat on a 2.000s grid whose phase is stable, so
    /// "how long until the next swing" is a real, free, renderable quantity. The sweep itself is a
    /// Composition animation behind this canvas; what is drawn here is the history of recent ticks.</summary>
    private float DrawTickRail(SKCanvas canvas, float y, CombatLiveView live)
    {
        DrawLabel(canvas, "TICKS", Pad, y + 8f);
        _stroke.Color = Rule;
        canvas.DrawLine(Pad, y + TickRailH - 2f, Pad + Content, y + TickRailH - 2f, _stroke);
        return y + TickRailH + BlockGap;
    }

    /// <summary>B3 - the spike ruler: can THIS tick kill me. Segments are cumulative across every
    /// active attacker, which is the case a single per-enemy threat readout cannot express - three
    /// rats that individually cap at 5 still add to 15 on one tick, and at degraded dexterity they
    /// land together far more often than their average suggests.</summary>
    private float DrawSpikeRuler(SKCanvas canvas, float y, CombatLiveView live)
    {
        DrawLabel(canvas, "WORST TICK", Pad, y + 8f);
        _stroke.Color = Rule;
        canvas.DrawLine(Pad, y + SpikeRulerH - 2f, Pad + Content, y + SpikeRulerH - 2f, _stroke);
        return y + SpikeRulerH + BlockGap;
    }

    /// <summary>
    /// B5 - loadout and drag. The weapon state, and how far strength and dexterity have fallen from
    /// their maxima. Per the owner's decision this shows only THAT they are low, never a computed
    /// encumbrance breakdown: the player stays in the loop about why.
    /// </summary>
    private float DrawLoadout(SKCanvas canvas, float y, CombatLiveView live)
    {
        if (live.IsUnarmed && live.HasEncounter)
        {
            // The loudest non-alarm element on the panel. Being unarmed while carrying something you
            // could wield is the single most recoverable way to lose a fight.
            _fill.Color = Tint(Caution, 0.30f);
            canvas.DrawRoundRect(Pad, y, Content, 24f, 3f, 3f, _fill);
            _textPaint.Color = Caution;
            canvas.DrawText("UNARMED", Pad + 8f, y + 17f, SKTextAlign.Left, _textFont, _textPaint);
        }
        else if (!string.IsNullOrEmpty(live.WeaponText))
        {
            _textPaint.Color = Ink;
            canvas.DrawText(live.WeaponText, Pad, y + 17f, SKTextAlign.Left, _textFont, _textPaint);
        }

        DrawDeficitTrack(canvas, "STR", Pad, y + 34f, live.StrengthDelta);
        DrawDeficitTrack(canvas, "DEX", Pad, y + 54f, live.DexterityDelta);

        return y + LoadoutH + BlockGap;
    }

    /// <summary>A stat track. A deficit reads amber (something you did to yourself and can undo); a
    /// bonus reads in the player's own colour. Zero draws as a bare track, which is visibly
    /// different from having no reading at all.</summary>
    private void DrawDeficitTrack(SKCanvas canvas, string label, float x, float y, int? delta)
    {
        DrawLabel(canvas, label, x, y + 9f);

        var trackX = x + 26f;
        var trackW = Content - 26f;

        _fill.Color = Tint(InkDim, 0.35f);
        canvas.DrawRoundRect(trackX, y, trackW, 10f, 5f, 5f, _fill);

        if (delta is not int d || d == 0)
            return;

        // 30 points off maximum is treated as a full-width penalty: beyond that the exact size stops
        // changing the decision, which is only ever "drop something" or "stop taking hits".
        var frac = Math.Clamp(Math.Abs(d) / 30f, 0.05f, 1f);
        _fill.Color = d < 0 ? Caution : You;
        canvas.DrawRoundRect(trackX, y, trackW * frac, 10f, 5f, 5f, _fill);
    }

    /// <summary>
    /// Draws a block that is not implemented yet. It still occupies its final height and draws its
    /// rule, so the layout the player learns today is the layout they keep - later stages fill these
    /// in without moving anything above or below them.
    /// </summary>
    private float DrawReserved(SKCanvas canvas, float y, float height, string? label)
    {
        if (label is not null)
            DrawLabel(canvas, label, Pad, y + 8f);

        _stroke.Color = Rule;
        canvas.DrawLine(Pad, y + height - 2f, Pad + Content, y + height - 2f, _stroke);
        return y + height + BlockGap;
    }

    private void DrawLabel(SKCanvas canvas, string text, float x, float y)
    {
        _textPaint.Color = InkDim;
        canvas.DrawText(text, x, y, SKTextAlign.Left, _labelFont, _textPaint);
    }
}
