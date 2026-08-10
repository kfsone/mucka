using MudSharp.Combat;
using Mucka.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Mucka.Rendering;

/// <summary>
/// The Combat Rail's render surface. Built to tools/combat/COMBAT-RAIL-SPEC.md - read that
/// before changing anything here; most of what looks arbitrary below is a settled decision.
///
/// <para><b>Bottom-focused.</b> The player's gaze rests at the bottom-center of the window, on
/// the input box and the newest game text. So live content is laid out from the BOTTOM edge
/// upward and empty space collects at the top. The top of a full-height right-edge panel is
/// about the longest saccade available in the window; nothing that matters mid-fight belongs
/// there.</para>
///
/// <para><b>Nothing moves.</b> Slot geometry is computed once per size change and held. An
/// indicator changes state in place - lit, unlit, colour - the way a warning lamp does. Live
/// elements never resize or reflow, because a critical readout that shifts under the eye has
/// to be re-found on every glance.</para>
///
/// <para><b>Invariant #1 - this class never animates.</b> It repaints only when
/// <see cref="Live"/> becomes different state. There is no timer here and there must never be
/// one: on WinUI <c>SKXamlCanvas</c> paints ON the UI thread, so a repeating repaint competes
/// directly with typing. Continuous motion (tick sweep, pulse, glow) belongs to the
/// Composition layer behind this canvas - see <c>PulseLayer</c>.</para>
///
/// <para><b>Invariant #0 - this surface cannot take focus.</b> No gesture recognizers, mounted
/// <c>InputTransparent</c>. There is nothing to click, by construction.</para>
/// </summary>
public sealed class CombatRailView : SKCanvasView
{
    // ---- Geometry (logical units; the canvas is scaled to these at paint time) -------------
    private const float RailWidth = 336f;
    private const float Pad = 10f;
    private const float Content = RailWidth - (Pad * 2);

    private const float SlotHeight = 46f;
    private const float SlotGap = 5f;
    private const float BottomRowHeight = 96f;
    private const float TickRowHeight = 30f;
    private const float SealSize = 92f;

    /// <summary>Upper bound on opponent slots. The measured maximum simultaneously-engaged
    /// opponents across the whole capture corpus is 4 - kills clear slots as fast as new
    /// creatures join, so even a 16-rat brawl never exceeded 4 at once. This cap exists for
    /// the pathological case, not the normal one.</summary>
    private const int MaxSlots = 8;

    // ---- Health pips: atlas's exact geometry. Do not scale these. -------------------------
    // Thin dashes, fixed width, never stretched. Seven rungs because the game's wound
    // vocabulary is ordinal with roughly seven steps - discrete segments are truthful about
    // that in a way a continuous bar is not.
    private const int PipCount = 7;
    private const float PipWidth = 16f;
    private const float PipHeight = 6f;
    private const float PipGap = 3f;

    // ---- Palette: Campbell by index, so the rail and the terminal never drift -------------
    private static readonly SKColor Ink = TerminalTheme.Palette[7];
    private static readonly SKColor InkBright = TerminalTheme.Palette[15];
    private static readonly SKColor InkDim = TerminalTheme.Palette[8];
    private static readonly SKColor Hostile = TerminalTheme.Palette[9];
    private static readonly SKColor Caution = TerminalTheme.Palette[11];
    private static readonly SKColor PipLive = TerminalTheme.Palette[6];   // #3A96DD
    private static readonly SKColor PipStale = new(0x3d, 0x55, 0x59);
    private static readonly SKColor PipEmptyFill = new(0x17, 0x1d, 0x20);
    private static readonly SKColor PipEmptyEdge = new(0x26, 0x2e, 0x32);
    private static readonly SKColor PipUnknownEdge = new(0x3a, 0x42, 0x47);
    private static readonly SKColor Magic = new(0x8F, 0x84, 0xEE);        // purple, shaded blue
    private static readonly SKColor Rule = Dim(TerminalTheme.Palette[8], 0.40f);

    private static SKColor Dim(SKColor c, float k)
        => new((byte)(c.Red * k), (byte)(c.Green * k), (byte)(c.Blue * k), c.Alpha);

    /// <summary>
    /// Clio's colorcode() ladder, ported so the stamina seal agrees with the status strip at
    /// the top of the window - two readouts of one number must never disagree about its
    /// colour. Deliberately NOT the player's own flee doctrine: the 40/20 thresholds they act
    /// on drive the ALARM, never the readout's colour. A readout reports; an alarm interprets.
    /// </summary>
    private static SKColor RatioColor(int value, int max)
    {
        if (value <= 0 || max <= 0)
            return TerminalTheme.Palette[10];
        var ratio = value * 100 / max;
        if (ratio >= 100) return TerminalTheme.Palette[10];
        if (ratio >= 76) return TerminalTheme.Palette[2];
        if (ratio >= 36) return TerminalTheme.Palette[11];
        if (ratio >= 16) return TerminalTheme.Palette[3];
        if (ratio >= 6) return TerminalTheme.Palette[1];
        return TerminalTheme.Palette[9];
    }

    // ---- Paints, built once. The paint handler allocates nothing. -------------------------
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly SKFont _nameFont = new(SKTypeface.Default, 11.5f);
    private readonly SKFont _phraseFont = new(SKTypeface.FromFamilyName("Cascadia Mono") ?? SKTypeface.Default, 11f);
    private readonly SKFont _sealNumFont = new(SKTypeface.Default, 25f);
    private readonly SKFont _tinyFont = new(SKTypeface.Default, 7.5f);
    private readonly SKFont _weaponFont = new(SKTypeface.Default, 13f);
    private readonly SKFont _smallFont = new(SKTypeface.Default, 10f);

    private CombatLiveView _live = CombatLiveView.Idle;

    public CombatRailView()
    {
        InputTransparent = true;
    }

    /// <summary>The frame state to draw. Repaints only when the reference actually changes, so
    /// the per-event and 1 Hz refresh paths do not invalidate for identical content.</summary>
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
        // Mandatory: this codebase has a live crash precedent (RO_E_CLOSED) from a surface that
        // stayed subscribed after its host was destroyed, so the next combat line drew into
        // already-torn-down objects.
        if (Handler is null && SidePanel is { } vm)
            vm.PropertyChanged -= OnSidePanelPropertyChanged;
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var scale = e.Info.Width / RailWidth;
        canvas.Save();
        canvas.Scale(scale);

        var height = e.Info.Height / scale;
        var live = _live;

        // Everything is placed relative to the BOTTOM edge and worked upward.
        var tickTop = height - Pad - TickRowHeight;
        var bottomRowTop = tickTop - BottomRowHeight;
        var slotsBottom = bottomRowTop - 6f;

        DrawTickRow(canvas, tickTop, live);
        DrawBottomRow(canvas, bottomRowTop, live);
        DrawOpponents(canvas, slotsBottom, live);

        canvas.Restore();
    }

    /// <summary>
    /// Opponent slots, filled from the bottom upward. The slot count comes from the height
    /// actually available rather than a fixed number, so a taller window simply shows more of
    /// the fight; it is recomputed only on resize, never on a combat event, so nothing shifts
    /// mid-fight.
    /// </summary>
    private void DrawOpponents(SKCanvas canvas, float bottom, CombatLiveView live)
    {
        var available = bottom - Pad;
        var capacity = Math.Clamp((int)Math.Floor(available / (SlotHeight + SlotGap)), 1, MaxSlots);
        if (!live.HasEncounter)
            return;

        var rows = live.Roster.Rows;
        var overflow = rows.Count > capacity;
        // One slot is surrendered to the overflow row when there are more opponents than fit.
        var shown = overflow ? capacity - 1 : Math.Min(rows.Count, capacity);

        var y = bottom - SlotHeight;
        for (var i = 0; i < shown; i++)
        {
            DrawOpponentSlot(canvas, y, rows[i]);
            y -= SlotHeight + SlotGap;
        }

        if (overflow)
            DrawOverflowRow(canvas, y, rows, shown, live.Roster);
    }

    /// <summary>One opponent: the name, and a seven-rung health ladder with the game's own
    /// wording laid over it. The phrase is verbatim from the MUD and set in the terminal's own
    /// monospace, because echoing what the player just read in the scroll is what anchors the
    /// panel to it.</summary>
    private void DrawOpponentSlot(SKCanvas canvas, float y, RosterRow row)
    {
        _fill.Color = row.IsCurrentTarget && row.IsLive
            ? new SKColor(0x61, 0xd6, 0xd6, 0x14)
            : new SKColor(0xff, 0xff, 0xff, 0x08);
        canvas.DrawRoundRect(Pad, y, Content, SlotHeight, 5f, 5f, _fill);

        // The current target is marked inside its own slot - never by being bigger. A slot that
        // changes size moves everything around it.
        _fill.Color = row.IsCurrentTarget && row.IsLive ? TerminalTheme.Palette[14] : Rule;
        canvas.DrawRect(Pad, y, 3f, SlotHeight, _fill);

        _text.Color = row.IsLive ? (row.IsCurrentTarget ? InkBright : Ink) : InkDim;
        canvas.DrawText(row.Name, Pad + 11f, y + 17f, SKTextAlign.Left, _nameFont, _text);

        if (!row.IsLive)
        {
            _text.Color = InkDim;
            canvas.DrawText(OutcomeWord(row.Outcome), Pad + Content - 8f, y + 17f,
                SKTextAlign.Right, _smallFont, _text);
            return;
        }

        DrawHealthLadder(canvas, Pad + 11f, y + 30f, row);
    }

    /// <summary>
    /// The health ladder. Pips are fixed 16x6 dashes with 3px gaps and never stretch - the
    /// separation between them is the point, and scaling them to contain the overlaid text
    /// turns dashes into boxes.
    ///
    /// <para>Fill means health REMAINING, depleting as the creature is hurt, so
    /// "close to death" shows one lit pip. That matches the stamina seal rather than inverting
    /// between two gauges on the same panel.</para>
    /// </summary>
    private void DrawHealthLadder(SKCanvas canvas, float x, float centerY, RosterRow row)
    {
        // Until the health-descriptor parser lands, no rung is known: every pip renders in the
        // dashed "unknown" state. That is deliberate - an unknown reading must never look like
        // a full or an empty one.
        var rung = HealthRungOf(row);

        var top = centerY - (PipHeight / 2f);
        for (var i = 0; i < PipCount; i++)
        {
            var px = x + (i * (PipWidth + PipGap));
            if (rung is null)
            {
                _stroke.Color = PipUnknownEdge;
                _stroke.PathEffect = SKPathEffect.CreateDash([2f, 2f], 0f);
                canvas.DrawRoundRect(px, top, PipWidth, PipHeight, 2f, 2f, _stroke);
                _stroke.PathEffect = null;
                continue;
            }

            var lit = i < rung.Value;
            _fill.Color = lit ? PipLive : PipEmptyFill;
            canvas.DrawRoundRect(px, top, PipWidth, PipHeight, 2f, 2f, _fill);
            if (!lit)
            {
                _stroke.Color = PipEmptyEdge;
                canvas.DrawRoundRect(px, top, PipWidth, PipHeight, 2f, 2f, _stroke);
            }
        }

        var phrase = HealthPhraseOf(row);
        if (string.IsNullOrEmpty(phrase))
            return;

        // Overlaid, centred on the ladder. The pips are shorter than the text and read behind
        // and around it.
        var ladderWidth = (PipCount * PipWidth) + ((PipCount - 1) * PipGap);
        _text.Color = InkBright;
        canvas.DrawText(phrase, x + (ladderWidth / 2f), centerY + 4f, SKTextAlign.Center,
            _phraseFont, _text);
    }

    /// <summary>Opponents beyond the visible slots: names only, worst first. Placed at the TOP
    /// of the stack, farthest from the gaze, because it is the least actionable thing on the
    /// panel - you cannot do anything about the sixth rat.</summary>
    private void DrawOverflowRow(SKCanvas canvas, float y, IReadOnlyList<RosterRow> rows, int shown, RosterPlan plan)
    {
        _stroke.Color = Rule;
        _stroke.PathEffect = SKPathEffect.CreateDash([3f, 3f], 0f);
        canvas.DrawRoundRect(Pad, y, Content, SlotHeight, 5f, 5f, _stroke);
        _stroke.PathEffect = null;

        var hidden = rows.Count - shown;
        _text.Color = Hostile;
        canvas.DrawText("+" + hidden.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Pad + 10f, y + 18f, SKTextAlign.Left, _nameFont, _text);

        // Names only, ordered by how much each has actually hurt the player - the ordering the
        // owner asked for, because "who is doing the damage" is the only question this row can
        // usefully answer.
        var names = string.Join(", ", rows.Skip(shown).Select(r => r.Name));
        _text.Color = InkDim;
        canvas.DrawText(Ellipsize(names, Content - 52f, _smallFont), Pad + 42f, y + 18f,
            SKTextAlign.Left, _smallFont, _text);

        if (plan.HiddenLiveCount > 0)
        {
            _text.Color = InkDim;
            canvas.DrawText(plan.HiddenLiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " alive",
                Pad + 10f, y + 34f, SKTextAlign.Left, _tinyFont, _text);
        }
    }

    /// <summary>Stamina seal, weapon, magic seal - one row, three fixed columns.</summary>
    private void DrawBottomRow(SKCanvas canvas, float y, CombatLiveView live)
    {
        DrawSeal(canvas, Pad, y, "STA", live.StaminaCurrent, live.StaminaMax,
            live.StaminaCurrent is int s && live.StaminaMax is int m && m > 0
                ? RatioColor(s, m)
                : InkDim,
            inert: false);

        var magMax = live.MagicMax ?? 0;
        var magInert = magMax <= 0;
        var magColor = magInert
            ? InkDim
            : live.MagicCurrent is int mc && mc < 20 ? Hostile : Magic;
        DrawSeal(canvas, Pad + Content - SealSize, y, "MAG", live.MagicCurrent, live.MagicMax,
            magColor, magInert);

        DrawWeapon(canvas, Pad + SealSize + 8f, y, Content - (SealSize * 2f) - 16f, live);
    }

    /// <summary>A seal: ring, value, and its own dim name INSIDE the ring. Never a label
    /// underneath, and never a separate status dot beside it - the ring carries its own
    /// state.</summary>
    private void DrawSeal(SKCanvas canvas, float x, float y, string label, int? value, int? max,
        SKColor color, bool inert)
    {
        var cx = x + (SealSize / 2f);
        var cy = y + (SealSize / 2f);
        var radius = (SealSize / 2f) - 7f;

        _stroke.StrokeWidth = 7f;
        _stroke.Color = Dim(InkDim, 0.30f);
        canvas.DrawCircle(cx, cy, radius, _stroke);

        if (!inert && value is int v && max is int mx && mx > 0)
        {
            var sweep = Math.Clamp(v / (float)mx, 0f, 1f) * 360f;
            _stroke.Color = color;
            using var path = new SKPath();
            path.AddArc(new SKRect(cx - radius, cy - radius, cx + radius, cy + radius), -90f, sweep);
            canvas.DrawPath(path, _stroke);
        }
        _stroke.StrokeWidth = 1f;

        _text.Color = inert ? Dim(InkDim, 0.7f) : color;
        var text = inert ? "-" : value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "--";
        canvas.DrawText(text, cx, cy + 4f, SKTextAlign.Center, _sealNumFont, _text);

        _text.Color = Dim(InkDim, 0.85f);
        canvas.DrawText(label, cx, cy - 16f, SKTextAlign.Center, _tinyFont, _text);
    }

    /// <summary>
    /// The weapon in hand and the alternate. An icon carries the armed/unarmed state - the word
    /// "armed" is redundant next to a weapon's name.
    ///
    /// <para>`wield` is per-engagement, not sticky: every fight starts empty-handed until the
    /// player says otherwise, so an unarmed opening is normal and must not raise an alarm. Only
    /// once damage has landed does this go amber, and it never goes straight to red.</para>
    /// </summary>
    private void DrawWeapon(SKCanvas canvas, float x, float y, float width, CombatLiveView live)
    {
        var midY = y + (SealSize / 2f);

        if (live.IsUnarmed)
        {
            var hurt = live.StaminaCurrent is int sta && live.StaminaMax is int max && max > 0 && sta < max;
            var tone = hurt ? Caution : InkDim;
            DrawOpenHand(canvas, x + 2f, midY - 16f, tone);
            _text.Color = tone;
            canvas.DrawText("UNARMED", x + 20f, midY - 8f, SKTextAlign.Left, _weaponFont, _text);
        }
        else if (!string.IsNullOrEmpty(live.WeaponText))
        {
            DrawSword(canvas, x + 2f, midY - 16f, Ink);
            _text.Color = Ink;
            canvas.DrawText(Ellipsize(live.WeaponText, width - 22f, _weaponFont), x + 20f, midY - 8f,
                SKTextAlign.Left, _weaponFont, _text);
        }

        if (live.CurrentTargetNpcWeapon is { Length: > 0 } npcWeapon)
        {
            _text.Color = Hostile;
            canvas.DrawText(Ellipsize("they: " + npcWeapon, width, _smallFont), x, midY + 10f,
                SKTextAlign.Left, _smallFont, _text);
        }

        // Alternate weapon: hotkey chip left, name right-aligned. Ctrl+W, consistent with the
        // client's other combat bindings; the handler must mark the event handled so it never
        // reaches a default close-window action.
        _stroke.Color = Rule;
        canvas.DrawRoundRect(x, midY + 18f, 34f, 13f, 3f, 3f, _stroke);
        _text.Color = InkDim;
        canvas.DrawText("Ctrl+W", x + 17f, midY + 27.5f, SKTextAlign.Center, _tinyFont, _text);
    }

    /// <summary>
    /// The tick meter, with the opponent count drawn over it. The tick is pale and grey - it is
    /// a timer, not a judgement, so it carries no colour coding and no label. It turns red at
    /// 30 stamina and glows at 20, and those are the only exceptions.
    /// </summary>
    private void DrawTickRow(SKCanvas canvas, float y, CombatLiveView live)
    {
        var trackY = y + (TickRowHeight / 2f) - 2.5f;
        var sta = live.StaminaCurrent;

        _fill.Color = new SKColor(0xff, 0xff, 0xff, 0x10);
        canvas.DrawRoundRect(Pad, trackY, Content, 5f, 3f, 3f, _fill);

        _fill.Color = sta is int s && s <= 30
            ? Hostile.WithAlpha(0xC0)
            : PipLive.WithAlpha(0x52);
        canvas.DrawRoundRect(Pad, trackY, Content * 0.4f, 5f, 3f, 3f, _fill);

        if (!live.HasEncounter)
            return;

        DrawEncounterGauge(canvas, y, live.Roster.LiveCount);
    }

    /// <summary>
    /// How many things are fighting you, drawn over the tick and sharing its pixels. Up to five
    /// crossed-swords marks, then a plain "+N" for the remainder - so nine opponents reads as
    /// five swords and "+4". Never zero-padded.
    /// </summary>
    private void DrawEncounterGauge(SKCanvas canvas, float y, int count)
    {
        if (count <= 0)
            return;

        const int maxMarks = 5;
        var marks = Math.Min(count, maxMarks);
        var remainder = count - marks;

        const float markW = 11f;
        const float markGap = 3f;
        var totalW = (marks * markW) + ((marks - 1) * markGap) + (remainder > 0 ? 22f : 0f);
        var x = Pad + ((Content - totalW) / 2f);
        var cy = y + (TickRowHeight / 2f);

        for (var i = 0; i < marks; i++)
        {
            DrawCrossedSwords(canvas, x, cy - 5.5f, Hostile);
            x += markW + markGap;
        }

        if (remainder > 0)
        {
            _text.Color = Hostile;
            canvas.DrawText("+" + remainder.ToString(System.Globalization.CultureInfo.InvariantCulture),
                x + 2f, cy + 4f, SKTextAlign.Left, _smallFont, _text);
        }
    }

    // ---- Drawn icons. ASCII source only, so every glyph is a path, never a font character. --

    private void DrawCrossedSwords(SKCanvas canvas, float x, float y, SKColor color)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = 1.6f;
        _stroke.StrokeCap = SKStrokeCap.Round;
        // Two blades, plus crossguards and pommels so this reads as weapons rather than a
        // letter x.
        canvas.DrawLine(x + 1f, y + 10f, x + 9f, y + 1f, _stroke);
        canvas.DrawLine(x + 9f, y + 10f, x + 1f, y + 1f, _stroke);
        _stroke.StrokeWidth = 1.2f;
        canvas.DrawLine(x + 0.5f, y + 7f, x + 3.5f, y + 9.5f, _stroke);
        canvas.DrawLine(x + 9.5f, y + 7f, x + 6.5f, y + 9.5f, _stroke);
        _stroke.StrokeWidth = 1f;
    }

    private void DrawSword(SKCanvas canvas, float x, float y, SKColor color)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = 1.8f;
        _stroke.StrokeCap = SKStrokeCap.Round;
        canvas.DrawLine(x + 2f, y + 14f, x + 13f, y + 2f, _stroke);
        canvas.DrawLine(x + 8f, y + 2f, x + 14f, y + 8f, _stroke);
        _stroke.StrokeWidth = 1f;
    }

    /// <summary>An open hand - four fingers and a thumb. Deliberately literal: the previous
    /// icon was a curve and a dot and read as nothing at all.</summary>
    private void DrawOpenHand(SKCanvas canvas, float x, float y, SKColor color)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = 1.5f;
        _stroke.StrokeCap = SKStrokeCap.Round;
        for (var i = 0; i < 4; i++)
        {
            var fx = x + 3f + (i * 3f);
            canvas.DrawLine(fx, y + 3f + (i == 0 || i == 3 ? 2f : 0f), fx, y + 9f, _stroke);
        }
        canvas.DrawLine(x + 1f, y + 8f, x + 3f, y + 6f, _stroke);
        _fill.Color = color;
        canvas.DrawRoundRect(x + 2f, y + 8f, 11f, 6f, 3f, 3f, _fill);
        _stroke.StrokeWidth = 1f;
    }

    // ---- Helpers ---------------------------------------------------------------------------

    /// <summary>The creature's health rung, 1..7, or null when nothing has been observed yet.
    /// Wired to null until the health-descriptor parser lands; an unknown reading renders as
    /// dashed pips, which must never look like a full or an empty ladder.</summary>
    private static int? HealthRungOf(RosterRow row) => null;

    /// <summary>The game's own wording for this creature's condition, verbatim. Empty until the
    /// descriptor parser lands.</summary>
    private static string HealthPhraseOf(RosterRow row) => string.Empty;

    private static string OutcomeWord(FightOutcome outcome) => outcome switch
    {
        FightOutcome.Killed => "killed",
        FightOutcome.KilledByNpc => "KILLED YOU",
        FightOutcome.NpcFled => "fled",
        FightOutcome.YouFled => "you fled",
        FightOutcome.Withdrawn => "withdrew",
        _ => string.Empty,
    };

    private static string Ellipsize(string value, float maxWidth, SKFont font)
    {
        if (string.IsNullOrEmpty(value) || font.MeasureText(value) <= maxWidth)
            return value;
        for (var len = value.Length - 1; len > 1; len--)
        {
            var candidate = string.Concat(value.AsSpan(0, len), "...");
            if (font.MeasureText(candidate) <= maxWidth)
                return candidate;
        }
        return value;
    }
}
