using System.ComponentModel;
using Mucka.ViewModels;
using MudSharp.Combat;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Mucka.Rendering;

/// <summary>
/// The Combat Rail's render surface: a single Skia canvas drawing a bounded set of fixed-layout
/// primitives - never a native `Label`/`FormattedString` (the measured failure mode this design
/// exists to fix: an 11-NPC pack fight rebuilt 200+ native WinUI spans per event and stalled the UI
/// thread 2-3s) and never a second window.
///
/// <para><b>Why this was rebuilt a second time.</b> The previous version already WAS a real canvas,
/// and still reviewed as "a shit load of tables lazily thrown together and trying to emulate some
/// kind of ascii art display. For example, there's a progress bar made of #s vs .s." That is an
/// accurate description of what it did: it drew a terminal readout onto a canvas. Monospace columns
/// standing in for a table, and a stamina meter spelled out in '#' and '.' characters, are what you
/// build when text is the only primitive available - and on an SKCanvas it is not. Nothing about the
/// old layout needed a grid of glyphs; it needed rectangles.</para>
///
/// <para><b>So this version draws.</b> The stamina meter is a filled rounded rect. The opposition is
/// a row of pips, one per enemy, filled for live and hollow for down - readable at a glance in a
/// 14-rat fight, where a name list never was. The exchange is bars: your hit rate against theirs,
/// your damage per hit against theirs, on a shared scale, each with a tick mark at the historical
/// median (see <see cref="CombatMeasure"/>). That tick is what deleted the "now"/"usual" numeric
/// matrix - the comparison those two columns existed to support is now the one thing the eye does
/// first, with no arithmetic.</para>
///
/// <para><b>Scaling.</b> Geometry is authored in device-independent pixels and multiplied by the
/// canvas scale at paint time, the same way <c>TerminalView</c> does it. The previous version used
/// raw pixel constants, so every size it picked was only correct at 100% display scaling.</para>
///
/// <para><b>Colour.</b> Still <see cref="TerminalTheme.Palette"/> by index only - no new hex value
/// anywhere in this view. Fills and tints are those same palette entries at reduced alpha, which
/// keeps a section band unmistakably a dimmer member of the theme rather than a new colour.</para>
///
/// <para><b>Focus.</b> No gesture recognizers, no `EnableTouchEvents`, nothing bindable for input -
/// this view cannot take keyboard focus and never needs Invariant #0's `RequestFocus` pattern.</para>
/// </summary>
public sealed class CombatPanelCanvasView : SKCanvasView
{
    // Authored in dips; every use is multiplied by the paint-time scale.
    private const float PadDip = 10f;
    private const float RowGapDip = 3f;
    private const float SectionGapDip = 9f;
    private const float HeroSizeDip = 17f;
    private const float BodySizeDip = 11f;
    private const float LabelSizeDip = 9f;
    private const float BarHeightDip = 9f;
    private const float SlimBarHeightDip = 6f;
    private const float PipSizeDip = 7f;
    private const float PipGapDip = 3f;

    private TerminalFont? _hero;
    private TerminalFont? _body;
    private TerminalFont? _label;
    private float _fontScale;

    public CombatPanelCanvasView()
    {
        PaintSurface += OnPaintSurface;
    }

    public static readonly BindableProperty SidePanelProperty = BindableProperty.Create(
        nameof(SidePanel), typeof(SidePanelViewModel), typeof(CombatPanelCanvasView), null,
        propertyChanged: OnSidePanelChanged);

    public SidePanelViewModel? SidePanel
    {
        get => (SidePanelViewModel?)GetValue(SidePanelProperty);
        set => SetValue(SidePanelProperty, value);
    }

    private static void OnSidePanelChanged(BindableObject obj, object oldValue, object newValue)
    {
        var view = (CombatPanelCanvasView)obj;
        if (oldValue is SidePanelViewModel oldVm) oldVm.PropertyChanged -= view.OnPanelPropertyChanged;
        if (newValue is SidePanelViewModel newVm) newVm.PropertyChanged += view.OnPanelPropertyChanged;
        view.InvalidateSurface();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Every property this view reads is only ever raised alongside the others in one batch
        // (SidePanelViewModel.RefreshCombatDisplay/RefreshCombatSignals) or on the panel's own
        // visibility toggle, so any notification arriving here means "something this view draws
        // changed" and no name filtering is needed.
        if (Handler is null)
            return;
        InvalidateSurface();
    }

    /// <summary>Deterministic teardown, matching the discipline the old ClogPage/PulseLayer both
    /// document: unsubscribe and dispose as soon as the handler goes null so a torn-down page never
    /// keeps rendering into (or animating) objects the platform has already destroyed.</summary>
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler is null)
        {
            if (SidePanel is not null)
                SidePanel.PropertyChanged -= OnPanelPropertyChanged;
            DisposeFonts();
        }
    }

    private void DisposeFonts()
    {
        _hero?.Dispose();
        _body?.Dispose();
        _label?.Dispose();
        _hero = _body = _label = null;
        _fontScale = 0f;
    }

    // Fonts are sized in real pixels, so they must be rebuilt whenever the canvas-to-dip ratio
    // changes (display scaling change, or a move to a monitor with different DPI).
    private void EnsureFonts(float scale)
    {
        if (_hero is not null && Math.Abs(scale - _fontScale) < 0.001f)
            return;
        DisposeFonts();
        _hero = new TerminalFont(HeroSizeDip * scale, lineHeightFactor: 1.1f);
        _body = new TerminalFont(BodySizeDip * scale, lineHeightFactor: 1.2f);
        _label = new TerminalFont(LabelSizeDip * scale, lineHeightFactor: 1.25f);
        _fontScale = scale;
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var vm = SidePanel;
        if (vm is null || e.Info.Width <= 0 || e.Info.Height <= 0 || Width <= 0)
            return;

        var scale = (float)(e.Info.Width / Width);
        if (scale <= 0 || float.IsNaN(scale))
            scale = 1f;
        EnsureFonts(scale);

        var pad = PadDip * scale;
        var left = pad;
        var right = e.Info.Width - pad;
        var maxY = e.Info.Height - pad;
        var y = pad;
        var live = vm.Live;

        y = DrawHeader(canvas, live, left, right, y, scale);

        if (!live.HasEncounter)
        {
            DrawText(canvas, "no fight in progress", left, y, _body!, TerminalTheme.Palette[8]);
            y += _body!.CellHeight + RowGapDip * scale;
        }
        else
        {
            y = DrawLive(canvas, vm, live, left, right, y, maxY, scale);
        }

        // The review tail (exchange table, weapon table, session totals) is deliberately NOT drawn
        // while a fight is live. Mid-fight this panel has one job - is this going badly, and why -
        // and the numeric tables were actively in the way of it. Between fights the space is free
        // and there is nothing to compete with, so the detail comes back rather than being lost.
        if (!live.InCombat && y <= maxY && vm.ClogLines.Count > 0)
        {
            y += SectionGapDip * scale;
            DrawReviewTail(canvas, vm, left, right, y, maxY, scale);
        }
    }

    private float DrawHeader(SKCanvas canvas, CombatLiveView live, float left, float right, float y, float scale)
    {
        DrawText(canvas, "COMBAT", left, y, _label!, TerminalTheme.Palette[8]);
        if (live.HasEncounter && live.EncounterDuration > TimeSpan.Zero)
            DrawTextRight(canvas, Duration(live.EncounterDuration.TotalSeconds), right, y, _label!, TerminalTheme.Palette[8]);

        y += _label!.CellHeight + RowGapDip * scale;
        DrawRule(canvas, left, right, y, scale);
        return y + SectionGapDip * scale * 0.7f;
    }

    private float DrawLive(
        SKCanvas canvas, SidePanelViewModel vm, CombatLiveView live,
        float left, float right, float y, float maxY, float scale)
    {
        y = DrawThreatBand(canvas, live, left, right, y, scale);
        if (y > maxY) return y;

        if (live.StaminaCurrent is int sta)
        {
            y = DrawStamina(canvas, sta, live.StaminaMax, left, right, y, scale);
            if (y > maxY) return y;
        }

        y += SectionGapDip * scale;
        y = DrawMatchup(canvas, live, left, right, y, scale);
        if (y > maxY) return y;

        y += SectionGapDip * scale;
        y = DrawOpposition(canvas, live, left, right, y, maxY, scale);
        if (y > maxY) return y;

        if (live.TargetDamageDone is double done && live.TargetEstimatedPool is double pool && pool > 0)
        {
            y += SectionGapDip * scale;
            y = DrawKillProgress(canvas, done, pool, left, right, y, scale);
            if (y > maxY) return y;
        }

        if (live.Measures.Count > 0)
        {
            y += SectionGapDip * scale;
            y = DrawMeasures(canvas, live.Measures, left, right, y, maxY, scale);
            if (y > maxY) return y;
        }

        return DrawNotes(canvas, vm, live, left, right, y, maxY, scale);
    }

    /// <summary>
    /// The threat indicator: a tinted band with a solid accent edge, not a bare line of text. The
    /// escalation the owner asked for ("gently glowing at first getting angrier as it gets likelier")
    /// reads off the band's own weight - a Safe band is a barely-there wash, a Critical one is a
    /// saturated block - which carries at the edge of vision in a way a coloured word does not.
    /// The pulse itself stays where it was, on the shared Composition layer driven by PulseTier
    /// (see Pages/GamePage.xaml.cs); this view never animates.
    /// </summary>
    private float DrawThreatBand(SKCanvas canvas, CombatLiveView live, float left, float right, float y, float scale)
    {
        if (live.Threat.Level == ThreatLevel.Idle || string.IsNullOrWhiteSpace(live.Threat.Label))
            return y;

        var color = ThreatColor(live.Threat.Level);
        var height = _hero!.CellHeight + PadDip * scale;
        var accent = 3f * scale;
        var radius = 3f * scale;

        using (var fill = new SKPaint { Color = color.WithAlpha(TintAlpha(live.Threat.Level)), IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(left, y, right, y + height), radius, radius, fill);
        using (var edge = new SKPaint { Color = color, IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(left, y, left + accent, y + height), radius, radius, edge);

        var textY = y + (height - _hero.CellHeight) / 2f;
        DrawText(canvas, live.Threat.Label, left + accent + PadDip * scale * 0.8f, textY, _hero, color, bold: true);

        return y + height + RowGapDip * scale;
    }

    // A stronger wash as the reading escalates, so the band's presence tracks the danger even
    // before the words are read.
    private static byte TintAlpha(ThreatLevel level) => level switch
    {
        ThreatLevel.Critical => 70,
        ThreatLevel.Danger => 52,
        ThreatLevel.Caution => 36,
        _ => 22,
    };

    /// <summary>The stamina meter - the single most important number on the panel per the owner's
    /// account of the fight that started this rebuild ("I had no idea how close I was to dying...
    /// sta down to 20"), and previously drawn as sixteen '#' and '.' characters.</summary>
    private float DrawStamina(SKCanvas canvas, int current, int? max, float left, float right, float y, float scale)
    {
        var fraction = max is int m && m > 0 ? Math.Clamp(current / (double)m, 0.0, 1.0) : 1.0;
        var color = fraction <= 0.25 ? TerminalTheme.Palette[9]
                  : fraction <= 0.50 ? TerminalTheme.Palette[11]
                  : TerminalTheme.Palette[10];

        DrawText(canvas, "STAMINA", left, y, _label!, TerminalTheme.Palette[8]);
        DrawTextRight(canvas, max is int mm ? $"{current} / {mm}" : current.ToString(),
            right, y, _label!, color, bold: true);
        y += _label!.CellHeight + RowGapDip * scale * 0.7f;

        DrawBar(canvas, left, right, y, BarHeightDip * scale, (float)fraction, color, scale);
        return y + BarHeightDip * scale + RowGapDip * scale;
    }

    /// <summary>Weapon versus target. Unarmed is called out hard because MUD2 needs a separate
    /// action to bring a weapon to bear and it is easy to be swinging bare-handed without noticing -
    /// a standing requirement from the owner, and the case their own transcript caught.</summary>
    private float DrawMatchup(SKCanvas canvas, CombatLiveView live, float left, float right, float y, float scale)
    {
        var target = live.Roster.Rows.Count > 0 ? live.Roster.Rows[0].Name : "--";

        var cursor = left;
        cursor += DrawText(canvas, live.WeaponText, cursor, y, _body!,
            live.IsUnarmed ? TerminalTheme.Palette[9] : TerminalTheme.Palette[6], bold: live.IsUnarmed);
        cursor += DrawText(canvas, "  vs  ", cursor, y, _body!, TerminalTheme.Palette[8]);
        DrawText(canvas, target, cursor, y, _body!, TerminalTheme.Palette[1]);
        y += _body!.CellHeight + RowGapDip * scale;

        // The NPC's own weapon, once confirmed. NPCs picking things up mid-fight measurably changes
        // their output and the per-tick hit line never names a weapon, so this is the only place it
        // can be seen at all.
        if (!string.IsNullOrWhiteSpace(live.CurrentTargetNpcWeapon))
        {
            DrawText(canvas, "armed with " + CombatHistoryFormatter.DisplayName(live.CurrentTargetNpcWeapon),
                left, y, _label!, TerminalTheme.Palette[9]);
            y += _label!.CellHeight + RowGapDip * scale;
        }

        return y;
    }

    /// <summary>
    /// The opposition, as pips rather than a name list: one square per enemy, filled and bright while
    /// it is still up, hollow and dim once it is down. The owner's report from a 14-rat fight was
    /// that they could not tell how many were still coming; a count plus a row of pips answers that
    /// without reading, where a truncated roster of "rat3, rat7, and 9 more" never could.
    /// </summary>
    private float DrawOpposition(
        SKCanvas canvas, CombatLiveView live, float left, float right, float y, float maxY, float scale)
    {
        var roster = live.Roster;
        if (roster.TotalCount == 0)
            return y;

        // A one-on-one fight says nothing about counts at all. "1 enemy  1 live  0 down" was the
        // owner's example of noise: three numbers to restate what the single named target above
        // already made obvious. Counting only starts earning its space once there is a pack, which
        // is exactly the case the owner could not read.
        if (roster.TotalCount > 1)
        {
            DrawText(canvas, "ENEMIES", left, y, _label!, TerminalTheme.Palette[8]);
            var summary = roster.LiveCount > 0
                ? $"{roster.LiveCount} of {roster.TotalCount} up"
                : "all down";
            DrawTextRight(canvas, summary, right, y, _label!,
                roster.LiveCount > 0 ? TerminalTheme.Palette[9] : TerminalTheme.Palette[10], bold: roster.LiveCount > 0);
            y += _label!.CellHeight + RowGapDip * scale;

            y = DrawPips(canvas, roster, left, right, y, scale);
            if (y > maxY) return y;
        }

        // Name only what is still standing, and only in a pack. In a duel the matchup line above
        // already names the target, so repeating it here is the same redundancy as the count line.
        // Once something is down its identity stops being actionable and the pips carry the tally.
        var shown = 0;
        foreach (var row in roster.Rows)
        {
            if (roster.TotalCount == 1 || !row.IsLive || shown >= 3 || y > maxY) continue;
            var color = row.IsCurrentTarget ? TerminalTheme.Palette[9] : TerminalTheme.Palette[1];
            var marker = row.IsCurrentTarget ? "> " : "  ";
            DrawText(canvas, marker + row.Name, left, y, _body!, color, bold: row.IsCurrentTarget);
            y += _body!.CellHeight + RowGapDip * scale * 0.6f;
            shown++;
        }

        return y;
    }

    private float DrawPips(SKCanvas canvas, RosterPlan roster, float left, float right, float y, float scale)
    {
        var size = PipSizeDip * scale;
        var gap = PipGapDip * scale;
        var perRow = Math.Max(1, (int)((right - left + gap) / (size + gap)));
        // Pips stand for every participant, including any the roster's own row list capped away -
        // the count is the point, so it must not inherit that cap.
        var total = roster.TotalCount;
        var liveRemaining = roster.LiveCount;
        var drawn = 0;

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, 1f * scale),
            Color = TerminalTheme.Palette[8],
        };

        while (drawn < total)
        {
            var col = drawn % perRow;
            if (col == 0 && drawn > 0)
                y += size + gap;

            var x = left + col * (size + gap);
            var rect = new SKRect(x, y, x + size, y + size);
            var radius = size * 0.25f;

            // Live pips first so the bright block reads as "what is left", not a scatter.
            if (drawn < liveRemaining)
            {
                fill.Color = TerminalTheme.Palette[9];
                canvas.DrawRoundRect(rect, radius, radius, fill);
            }
            else
            {
                canvas.DrawRoundRect(rect, radius, radius, stroke);
            }

            drawn++;
        }

        return y + size + RowGapDip * scale;
    }

    /// <summary>
    /// Progress toward the kill, against this creature kind's empirically estimated stamina pool.
    /// MUD2 never reports NPC health, so this is a genuine estimate and is labelled as one - the
    /// pool comes from the median damage dealt across fights that ENDED IN A KILL (right-censored on
    /// purpose: a survivor only proves its pool exceeds what we dealt).
    /// </summary>
    private float DrawKillProgress(
        SKCanvas canvas, double done, double pool, float left, float right, float y, float scale)
    {
        var fraction = (float)Math.Clamp(done / pool, 0.0, 1.0);

        DrawText(canvas, "KILL PROGRESS", left, y, _label!, TerminalTheme.Palette[8]);
        DrawTextRight(canvas, $"~{Num(done)} of ~{Num(pool)}", right, y, _label!, TerminalTheme.Palette[8]);
        y += _label!.CellHeight + RowGapDip * scale * 0.7f;

        DrawBar(canvas, left, right, y, SlimBarHeightDip * scale, fraction, TerminalTheme.Palette[14], scale);
        return y + SlimBarHeightDip * scale + RowGapDip * scale;
    }

    /// <summary>
    /// The exchange, as paired bars on a shared scale with a tick at the historical median. This is
    /// the element that replaced the numeric "now"/"usual" matrix outright.
    /// </summary>
    private float DrawMeasures(
        SKCanvas canvas, IReadOnlyList<CombatMeasure> measures,
        float left, float right, float y, float maxY, float scale)
    {
        // Measures arrive in fixed pairs (hit rate you/them, then damage you/them), so the headings
        // are positional rather than carried on every row.
        for (var i = 0; i < measures.Count; i++)
        {
            if (y > maxY) return y;

            if (i == 0)
            {
                DrawText(canvas, "HIT RATE", left, y, _label!, TerminalTheme.Palette[8]);
                y += _label!.CellHeight + RowGapDip * scale * 0.7f;
            }
            else if (i == 2)
            {
                y += RowGapDip * scale;
                DrawText(canvas, "DAMAGE PER HIT", left, y, _label!, TerminalTheme.Palette[8]);
                y += _label!.CellHeight + RowGapDip * scale * 0.7f;
            }

            y = DrawMeasureRow(canvas, measures[i], left, right, y, scale);
        }

        return y;
    }

    private float DrawMeasureRow(SKCanvas canvas, CombatMeasure measure, float left, float right, float y, float scale)
    {
        var labelWidth = _label!.CellWidth * 5f;
        var valueWidth = _label.CellWidth * 6f;
        var barLeft = left + labelWidth;
        var barRight = right - valueWidth;
        var color = measure.IsPlayerSide ? TerminalTheme.Palette[6] : TerminalTheme.Palette[1];

        DrawText(canvas, measure.Label, left, y, _label, TerminalTheme.Palette[8]);

        if (measure.Now is double now && barRight > barLeft)
        {
            var fraction = measure.FullScale > 0 ? (float)Math.Clamp(now / measure.FullScale, 0.0, 1.0) : 0f;
            DrawBar(canvas, barLeft, barRight, y + _label.CellHeight * 0.2f, SlimBarHeightDip * scale,
                fraction, color, scale);

            // The historical median as a tick on the same track. Drawn only with a real sample
            // behind it, so an absent tick honestly means "nothing on file yet" rather than "zero".
            if (measure.Usual is double usual && measure.SampleSize > 0 && measure.FullScale > 0)
            {
                var tickFraction = (float)Math.Clamp(usual / measure.FullScale, 0.0, 1.0);
                var tickX = barLeft + (barRight - barLeft) * tickFraction;
                using var tick = new SKPaint
                {
                    Color = TerminalTheme.Palette[15],
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(1f, 1.5f * scale),
                };
                var top = y + _label.CellHeight * 0.2f - 1.5f * scale;
                canvas.DrawLine(tickX, top, tickX, top + SlimBarHeightDip * scale + 3f * scale, tick);
            }

            DrawTextRight(canvas, measure.IsPercentage ? PercentText(now) : Num(now),
                right, y, _label, color, bold: true);
        }
        else
        {
            DrawTextRight(canvas, "--", right, y, _label, TerminalTheme.Palette[8]);
        }

        return y + _label.CellHeight + RowGapDip * scale;
    }

    /// <summary>The one-line advisories: why this is going badly, the current stat load, and at most
    /// one flee line. Each renders only when it has something to say.</summary>
    private float DrawNotes(
        SKCanvas canvas, SidePanelViewModel vm, CombatLiveView live,
        float left, float right, float y, float maxY, float scale)
    {
        var deficits = CombatHistoryFormatter.BuildDeficitsLine(new CombatStatDeficits(
            live.StrengthDelta, live.DexterityDelta, live.StaminaCurrent, live.StaminaMax,
            live.WeightCarriedGrams, live.ObjectsCarried));

        var any = vm.WhyLine is not null || deficits is not null || vm.FleeSummaryLine is not null;
        if (!any || y > maxY)
            return y;

        y += SectionGapDip * scale;
        DrawRule(canvas, left, right, y, scale);
        y += RowGapDip * scale * 2f;

        if (vm.WhyLine is { } why && y <= maxY)
            y = DrawClogLine(canvas, why, left, y, _label!, false, scale);
        if (deficits is not null && y <= maxY)
            y = DrawClogLine(canvas, deficits, left, y, _label!, vm.EncumbranceTier == CombatTier.T2, scale);
        if (vm.FleeSummaryLine is { } flee && y <= maxY)
            y = DrawClogLine(canvas, flee, left, y, _body!, false, scale);

        return y;
    }

    private float DrawReviewTail(
        SKCanvas canvas, SidePanelViewModel vm, float left, float right, float y, float maxY, float scale)
    {
        DrawRule(canvas, left, right, y, scale);
        y += RowGapDip * scale * 2f;

        var bright = vm.EncumbranceTier == CombatTier.T2;
        foreach (var line in vm.ClogLines)
        {
            if (y > maxY) return y;
            y = DrawClogLine(canvas, line, left, y, _label!, bright, scale);
        }

        return y;
    }

    // ---- primitives ----------------------------------------------------------

    /// <summary>A track plus its fill, both rounded. The track is the theme's own dim grey at low
    /// alpha rather than a new colour, so an empty bar reads as an empty version of the same object
    /// instead of a separate element.</summary>
    private static void DrawBar(
        SKCanvas canvas, float left, float right, float top, float height, float fraction, SKColor color, float scale)
    {
        if (right <= left)
            return;

        var radius = height / 2f;
        var rect = new SKRect(left, top, right, top + height);

        using (var track = new SKPaint { Color = TerminalTheme.Palette[8].WithAlpha(60), IsAntialias = true })
            canvas.DrawRoundRect(rect, radius, radius, track);

        var width = (right - left) * Math.Clamp(fraction, 0f, 1f);
        if (width <= 0)
            return;

        // A sliver still has to look like a bar, not a dot: never draw narrower than the cap.
        width = Math.Max(width, height);
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(left, top, left + width, top + height), radius, radius, fill);
    }

    private static void DrawRule(SKCanvas canvas, float left, float right, float y, float scale)
    {
        using var paint = new SKPaint
        {
            Color = TerminalTheme.Palette[8].WithAlpha(90),
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1f, scale),
        };
        canvas.DrawLine(left, y, right, y, paint);
    }

    private float DrawClogLine(
        SKCanvas canvas, ClogLine line, float x, float y, TerminalFont font, bool encumbranceBright, float scale)
    {
        if (line.Spans.Count == 0)
            return y + font.CellHeight * 0.5f;   // ClogLine.Blank: half-row spacer, not a full row

        var cursor = x;
        foreach (var span in line.Spans)
        {
            if (span.Text.Length == 0) continue;
            cursor += DrawText(canvas, span.Text, cursor, y, font,
                ToneColor(span.Tone, encumbranceBright), strike: span.Strike);
        }
        return y + font.CellHeight + RowGapDip * scale;
    }

    /// <summary>Draws left-aligned text at (x, y-as-row-top) and returns the advance width.
    /// <paramref name="bold"/> fakes bold via stroke-and-fill at the font's own
    /// <see cref="TerminalFont.BoldStrokeWidth"/> - the same technique <c>Rendering/TerminalView.cs</c>
    /// already uses for intense terminal text (the only registered face is Regular, so this is the
    /// one way bold is achievable at all).</summary>
    private static float DrawText(
        SKCanvas canvas, string text, float x, float rowTop, TerminalFont font, SKColor color,
        bool bold = false, bool strike = false)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        if (bold)
        {
            paint.Style = SKPaintStyle.StrokeAndFill;
            paint.StrokeWidth = font.BoldStrokeWidth;
        }

        var baseline = rowTop + font.Baseline;
        canvas.DrawText(text, x, baseline, font.Font, paint);
        var width = font.Font.MeasureText(text);
        if (strike)
        {
            var strikeY = baseline - font.Font.Metrics.XHeight / 2f;
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawLine(x, strikeY, x + width, strikeY, paint);
        }
        return width;
    }

    private static void DrawTextRight(
        SKCanvas canvas, string text, float rightEdge, float rowTop, TerminalFont font, SKColor color,
        bool bold = false)
        => DrawText(canvas, text, rightEdge - font.Font.MeasureText(text), rowTop, font, color, bold);

    /// <summary>Colours come from <see cref="TerminalTheme.Palette"/> by index only, promoted
    /// normal-to-bright the same way bold text already promotes in the terminal.</summary>
    private static SKColor ToneColor(ClogTone tone, bool encumbranceBright) => tone switch
    {
        ClogTone.Dim => TerminalTheme.Palette[8],
        ClogTone.Value => TerminalTheme.Palette[7],
        ClogTone.Friendly => TerminalTheme.Palette[6],
        ClogTone.Hostile => TerminalTheme.Palette[1],
        ClogTone.Good => TerminalTheme.Palette[2],
        ClogTone.Warn => TerminalTheme.Palette[3],
        ClogTone.Heading => TerminalTheme.Palette[5],
        // Danger IS the promoted/bright state of Hostile - there is no separate "normal Danger",
        // the tone itself only ever means the escalated reading.
        ClogTone.Danger => TerminalTheme.Palette[9],
        ClogTone.Load => TerminalTheme.Palette[encumbranceBright ? 13 : 5],
        _ => TerminalTheme.Palette[7],
    };

    private static SKColor ThreatColor(ThreatLevel level) => level switch
    {
        ThreatLevel.Critical => TerminalTheme.Palette[9],
        ThreatLevel.Danger => TerminalTheme.Palette[9],
        ThreatLevel.Caution => TerminalTheme.Palette[11],
        ThreatLevel.Safe => TerminalTheme.Palette[10],
        _ => TerminalTheme.Palette[7],
    };

    private static string Duration(double? seconds)
    {
        if (seconds is null)
            return "--";
        var total = (int)Math.Round(seconds.Value, MidpointRounding.AwayFromZero);
        return $"{total / 60}:{total % 60:00}";
    }

    private static string Num(double value)
        => value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static string PercentText(double fraction)
        => Math.Round(fraction * 100, MidpointRounding.AwayFromZero)
            .ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
