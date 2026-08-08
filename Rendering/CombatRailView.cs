using MudSharp.Combat;
using Mucka.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Mucka.Rendering;

/// <summary>
/// The Combat Rail's render surface: one SkiaSharp canvas drawing a fixed layout from
/// <see cref="CombatLiveView"/>.
///
/// <para><b>Only drawn when known.</b> A block appears when it has something to say and is absent
/// otherwise. An earlier pass reserved every future block's space with a label and a rule so the
/// geometry would be stable from day one; in practice that read as a column of empty boxes, which is
/// clutter with no compensating benefit while there is nothing yet to memorise. Blocks are added
/// back as they gain content.</para>
///
/// <para><b>Invariant #1 - this class never animates.</b> It repaints only when <see cref="Live"/>
/// becomes genuinely different state. There is no timer here and there must never be one: on WinUI
/// <c>SKXamlCanvas</c> paints ON the UI thread, so a repeating repaint competes directly with
/// typing. Continuous motion belongs to the Composition layer behind this canvas (PulseLayer).</para>
///
/// <para><b>Invariant #0 - this surface cannot take focus.</b> No gesture recognizers, mounted
/// <c>InputTransparent</c>. There is nothing to click, by construction.</para>
///
/// <para><b>Allocation.</b> Paint objects are fields built once; the paint handler allocates nothing
/// per frame beyond the strings it must format.</para>
/// </summary>
public sealed class CombatRailView : SKCanvasView
{
    // ---- Layout (logical units, scaled to the surface at paint time) --------------------------
    private const float PanelWidth = 300f;
    private const float Pad = 10f;
    private const float Content = PanelWidth - (Pad * 2);
    private const float BlockGap = 14f;

    // ---- Palette ------------------------------------------------------------------------------
    // Campbell, by index, so this panel and the terminal can never drift. Derived shades are scaled
    // FROM these bases rather than invented, which keeps one palette in one place while still
    // allowing tints and fills.
    private static readonly SKColor Ink = TerminalTheme.Palette[7];
    private static readonly SKColor InkBright = TerminalTheme.Palette[15];
    private static readonly SKColor InkDim = TerminalTheme.Palette[8];
    private static readonly SKColor Hostile = TerminalTheme.Palette[9];
    private static readonly SKColor Caution = TerminalTheme.Palette[11];
    private static readonly SKColor Dead = TerminalTheme.Palette[8];
    private static readonly SKColor Rule = Tint(TerminalTheme.Palette[8], 0.40f);

    // Clio's colorcode() ladder, ported so the rail's stamina bar agrees with the status strip at
    // the top of the window. The strip colours stamina from MUD2's own colour hint
    // (GameStatsSnapshot.StaminaColor); this is the same ladder that hint follows, so the two read
    // as one instrument instead of two opinions. Deliberately NOT the client's own flee doctrine:
    // the 40/20 thresholds the player actually acts on drive the ALARM (the glow layer and the band
    // marks), never the readout's own colour. A readout reports; an alarm interprets.
    private static SKColor RatioColor(int value, int max)
    {
        if (value <= 0 || max <= 0)
            return TerminalTheme.Palette[10];
        var ratio = value * 100 / max;
        if (ratio >= 100) return TerminalTheme.Palette[10];  // bright green
        if (ratio >= 76) return TerminalTheme.Palette[2];    // green
        if (ratio >= 36) return TerminalTheme.Palette[11];   // bright yellow
        if (ratio >= 16) return TerminalTheme.Palette[3];    // yellow
        if (ratio >= 6) return TerminalTheme.Palette[1];     // red
        return TerminalTheme.Palette[9];                     // bright red
    }

    private static SKColor Tint(SKColor c, float amount)
        => new((byte)(c.Red * amount), (byte)(c.Green * amount), (byte)(c.Blue * amount), c.Alpha);

    // ---- Paints (built once) -------------------------------------------------------------------
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly SKFont _staminaFont = new(SKTypeface.Default, 30f);
    private readonly SKFont _labelFont = new(SKTypeface.Default, 9f);
    private readonly SKFont _bodyFont = new(SKTypeface.Default, 13f);
    private readonly SKFont _smallFont = new(SKTypeface.Default, 11f);

    private CombatLiveView _live = CombatLiveView.Idle;

    public CombatRailView()
    {
        InputTransparent = true;
    }

    /// <summary>The frame state to draw. Repaints only when the reference actually changes, so the
    /// per-event and 1 Hz refresh paths do not invalidate for identical content (Invariant #1).</summary>
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
        if (e.PropertyName is nameof(SidePanelViewModel.Live) or null && sender is SidePanelViewModel vm)
            Live = vm.Live;
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        // Mandatory teardown: this codebase has a live crash precedent (RO_E_CLOSED) from a surface
        // that stayed subscribed after its host was destroyed.
        if (Handler is null && SidePanel is { } vm)
            vm.PropertyChanged -= OnSidePanelPropertyChanged;
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        canvas.Save();
        canvas.Scale(e.Info.Width / PanelWidth);

        var live = _live;
        var y = Pad + 6f;

        y = DrawStamina(canvas, y, live);
        y = DrawOpposition(canvas, y, live);
        DrawWeapon(canvas, y, live);

        canvas.Restore();
    }

    /// <summary>
    /// Stamina: the value and its bar on one line, so the number sits against the thing it measures
    /// instead of floating above it. The bar's colour follows the same ladder as the status strip at
    /// the top of the window (see <see cref="RatioColor"/>) - two readouts of one quantity must never
    /// disagree about its colour.
    /// </summary>
    private float DrawStamina(SKCanvas canvas, float y, CombatLiveView live)
    {
        DrawLabel(canvas, "STAMINA", Pad, y);
        y += 8f;

        var sta = live.StaminaCurrent;
        var max = live.StaminaMax is int m && m > 0 ? m : 100;
        var color = sta is int s ? RatioColor(s, max) : InkDim;

        var valueText = sta is int v
            ? v.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "--";

        _text.Color = color;
        canvas.DrawText(valueText, Pad, y + 24f, SKTextAlign.Left, _staminaFont, _text);
        var valueWidth = _staminaFont.MeasureText(valueText);

        _text.Color = InkDim;
        canvas.DrawText("/" + max.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Pad + valueWidth + 3f, y + 24f, SKTextAlign.Left, _smallFont, _text);

        // The bar takes the rest of the line. It starts after the widest value this readout can
        // show, not after the current one, so the bar does not jump left and right as stamina
        // crosses 100 or 10.
        var barX = Pad + 74f;
        var barW = Content - 74f;
        DrawStaminaBar(canvas, barX, y + 12f, barW, 12f, sta, max, color);

        return y + 30f + BlockGap;
    }

    private void DrawStaminaBar(SKCanvas canvas, float x, float y, float w, float h, int? sta, int max, SKColor color)
    {
        _fill.Color = Tint(InkDim, 0.30f);
        canvas.DrawRoundRect(x, y, w, h, h / 2f, h / 2f, _fill);

        if (sta is int value && value > 0)
        {
            var frac = Math.Clamp(value / (float)max, 0f, 1f);
            _fill.Color = color;
            canvas.DrawRoundRect(x, y, Math.Max(w * frac, h), h, h / 2f, h / 2f, _fill);
        }

        // The two thresholds the player actually acts on - 40 "start thinking about leaving" and 20
        // "red zone" - drawn as structure, never as colour. Structure gets a hairline; danger gets
        // the hue. Colouring the scale itself would make the ruler look like the alarm.
        _stroke.Color = Rule;
        foreach (var mark in new[] { 40, 20 })
        {
            var mx = x + (w * (mark / (float)max));
            canvas.DrawLine(mx, y - 2f, mx, y + h + 2f, _stroke);
        }
    }

    /// <summary>
    /// Who is actually fighting you. This is the first thing the panel owes the player and the first
    /// thing it draws: a fight with no named opponent is not a readout of anything.
    /// </summary>
    private float DrawOpposition(SKCanvas canvas, float y, CombatLiveView live)
    {
        if (!live.HasEncounter)
            return y;

        var roster = live.Roster;
        if (roster.Rows.Count == 0 && roster.TotalCount == 0)
            return y;

        DrawLabel(canvas, live.InCombat ? "FIGHTING" : "LAST FIGHT", Pad, y);

        // The live/dead split, right-aligned against the label. In a pack fight "how many are still
        // up" is the number that matters, and a capped row list alone cannot carry it.
        if (roster.TotalCount > 1)
        {
            _text.Color = InkDim;
            var counts = roster.LiveCount + " up";
            if (roster.ResolvedCount > 0)
                counts += "  " + roster.ResolvedCount + " down";
            canvas.DrawText(counts, Pad + Content, y, SKTextAlign.Right, _labelFont, _text);
        }

        y += 14f;

        foreach (var row in roster.Rows)
        {
            _text.Color = row.IsLive
                ? (row.IsCurrentTarget ? InkBright : Ink)
                : Dead;

            // The current target carries a marker rather than a different size, so rows stay on a
            // fixed pitch and the eye can track one name down the list as others resolve.
            if (row.IsCurrentTarget && row.IsLive)
            {
                _fill.Color = Hostile;
                canvas.DrawRect(Pad, y - 7f, 3f, 9f, _fill);
            }

            canvas.DrawText(row.Name, Pad + 8f, y, SKTextAlign.Left, _bodyFont, _text);

            if (!row.IsLive)
            {
                _text.Color = Dead;
                canvas.DrawText(OutcomeWord(row.Outcome), Pad + Content, y, SKTextAlign.Right, _smallFont, _text);
            }

            y += 17f;
        }

        if (roster.HasHidden)
        {
            _text.Color = InkDim;
            var more = "+" + roster.HiddenCount + " more";
            if (roster.HiddenLiveCount > 0)
                more += " (" + roster.HiddenLiveCount + " up)";
            canvas.DrawText(more, Pad + 8f, y, SKTextAlign.Left, _smallFont, _text);
            y += 15f;
        }

        return y + BlockGap;
    }

    private static string OutcomeWord(FightOutcome outcome) => outcome switch
    {
        FightOutcome.Killed => "killed",
        FightOutcome.KilledByNpc => "KILLED YOU",
        FightOutcome.NpcFled => "fled",
        FightOutcome.YouFled => "you fled",
        FightOutcome.Withdrawn => "withdrew",
        _ => string.Empty,
    };

    /// <summary>
    /// What each side is fighting with. Immediately after the opposition, because "who and with
    /// what" is one question. The NPC's own weapon matters as much as the player's and is easy to
    /// miss in the scroll, especially when the creature arrived after the fight started.
    /// </summary>
    private void DrawWeapon(SKCanvas canvas, float y, CombatLiveView live)
    {
        if (!live.HasEncounter)
            return;

        DrawLabel(canvas, "WEAPON", Pad, y);
        y += 16f;

        if (live.IsUnarmed)
        {
            // The loudest non-alarm element on the panel. Fighting unarmed while carrying something
            // wieldable is the most recoverable way there is to lose a fight.
            _fill.Color = Tint(Caution, 0.28f);
            canvas.DrawRoundRect(Pad, y - 12f, Content, 20f, 3f, 3f, _fill);
            _text.Color = Caution;
            canvas.DrawText("UNARMED", Pad + 6f, y + 2f, SKTextAlign.Left, _bodyFont, _text);
        }
        else if (!string.IsNullOrEmpty(live.WeaponText))
        {
            _text.Color = Ink;
            canvas.DrawText(live.WeaponText, Pad, y, SKTextAlign.Left, _bodyFont, _text);
        }

        if (live.CurrentTargetNpcWeapon is { Length: > 0 } npcWeapon)
        {
            y += 17f;
            _text.Color = Hostile;
            canvas.DrawText("they use " + npcWeapon, Pad, y, SKTextAlign.Left, _smallFont, _text);
        }
    }

    private void DrawLabel(SKCanvas canvas, string text, float x, float y)
    {
        _text.Color = InkDim;
        canvas.DrawText(text, x, y, SKTextAlign.Left, _labelFont, _text);
    }
}
