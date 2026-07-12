using System.Text;
using MudSharp.Models;

namespace MudSharp.Tests.Fixtures;

/// <summary>
/// Status-effect detection from the C11 (spell start/end) protocol family, using the
/// exact wire sequences observed in two session captures (20260711-190235 = starts;
/// 20260711-235427 = wear-offs).
///
/// Byte encoding: a C-code is 0x9B + code, so C11 = 0xA6 and its sub-code byte is 0x9B + sub:
///   glow    → C11 C00 (A6 9B)   unglow → C11 C01 (A6 9C)
///   start   → C11 C02 (A6 9D) + comparative phrase ("...become stronger!")
///   wear-off→ C11 C03 (A6 9E) + noun phrase ("[Some of] your magical strength has worn off.")
/// All six stat spells share 11 02 — only the bracketed phrase disambiguates stat + direction.
/// </summary>
public class StatusEffectTests
{
    private static ParserHarness InGameMode()
    {
        var h = new ParserHarness();
        h.Feed(0x9D, 0x9C, 0xFF, 0xFF); // C02 C01 → enter game mode
        h.ClearCounters();
        return h;
    }

    private static byte[] Bracket(byte sub, string phrase)
        => [0xA6, sub, 0xFF, 0xFF, .. Encoding.Latin1.GetBytes(phrase), 0xFF, 0xFF];

    private static byte[] Start(string phrase) => Bracket(0x9D, phrase);   // 11 02
    private static byte[] End(string phrase)   => Bracket(0x9E, phrase);   // 11 03

    // ── Starts (11 02) ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("You have suddenly and magically become stronger!",    StatusEffectKind.Strength,  EffectSign.Buff)]
    [InlineData("You have suddenly and magically become weaker!",      StatusEffectKind.Strength,  EffectSign.Debuff)]
    [InlineData("You have suddenly and magically become more adroit!", StatusEffectKind.Dexterity, EffectSign.Buff)]
    [InlineData("You have suddenly and magically become less adroit!", StatusEffectKind.Dexterity, EffectSign.Debuff)]
    [InlineData("You have suddenly and magically become fitter!",      StatusEffectKind.Stamina,   EffectSign.Buff)]
    [InlineData("You have suddenly and magically become less fit!",    StatusEffectKind.Stamina,   EffectSign.Debuff)]
    public void Start_EmitsStartedWithStatAndSign(string phrase, StatusEffectKind kind, EffectSign sign)
    {
        var h = InGameMode();
        h.Feed(Start(phrase));
        var e = Assert.Single(h.StatusEffects);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(sign, e.Sign);
        Assert.Equal(EffectTransition.Started, e.Transition);
    }

    [Fact]
    public void Start_CapturesDetectedLineAsMessage()
    {
        // The exact fed line is carried on the change as the tooltip source. Only the affliction
        // path asserted .Message before; the enhancing-start path is the primary tooltip source.
        const string line = "You have suddenly and magically become stronger!";
        var h = InGameMode();
        h.Feed(Start(line));
        var e = Assert.Single(h.StatusEffects);
        Assert.Equal(StatusEffectKind.Strength, e.Kind);
        Assert.Equal(EffectSign.Buff, e.Sign);
        Assert.Equal(EffectTransition.Started, e.Transition);
        Assert.Equal(line, e.Message);
    }

    [Fact]
    public void Start_PhraseSplitAcrossTwoFeeds_StillResolves()
    {
        // The bracketed phrase can straddle a network packet boundary. The decoder accumulates
        // across Feed() calls, so a mid-word split must still resolve to the same effect.
        var h = InGameMode();
        h.Feed([0xA6, 0x9D, 0xFF, 0xFF, .. Encoding.Latin1.GetBytes("You have suddenly and magically become str")]);
        Assert.Empty(h.StatusEffects);   // bracket not yet closed — nothing emitted
        h.Feed([.. Encoding.Latin1.GetBytes("onger!"), 0xFF, 0xFF]);
        var e = Assert.Single(h.StatusEffects);
        Assert.Equal(StatusEffectKind.Strength, e.Kind);
        Assert.Equal(EffectSign.Buff, e.Sign);
        Assert.Equal(EffectTransition.Started, e.Transition);
    }

    // ── Wear-offs (11 03) — confirmed nouns from the 235427 capture ─────────────

    [Theory]
    [InlineData("Your magical weakness has worn off.",   StatusEffectKind.Strength,  EffectSign.Debuff)]
    [InlineData("Your magical clumsiness has worn off.", StatusEffectKind.Dexterity, EffectSign.Debuff)]
    [InlineData("Your magical fitness has worn off.",    StatusEffectKind.Stamina,   EffectSign.Buff)]
    [InlineData("Your magical unfitness has worn off.",  StatusEffectKind.Stamina,   EffectSign.Debuff)]
    [InlineData("Your magical dexterity has worn off.",  StatusEffectKind.Dexterity, EffectSign.Buff)]
    [InlineData("Your magical strength has worn off.",   StatusEffectKind.Strength,  EffectSign.Buff)]
    public void FullWearOff_EmitsFullyWoreOffWithStatAndSign(string phrase, StatusEffectKind kind, EffectSign sign)
    {
        var h = InGameMode();
        h.Feed(End(phrase));
        var e = Assert.Single(h.StatusEffects);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(sign, e.Sign);
        Assert.Equal(EffectTransition.FullyWoreOff, e.Transition);
    }

    [Fact]
    public void Unfitness_NotMisreadAsFitness()
    {
        // "unfitness" contains "fitness" — ordering must resolve it to the Debuff slot.
        var h = InGameMode();
        h.Feed(End("Your magical unfitness has worn off."));
        var e = Assert.Single(h.StatusEffects);
        Assert.Equal(StatusEffectKind.Stamina, e.Kind);
        Assert.Equal(EffectSign.Debuff, e.Sign);
    }

    [Fact]
    public void PartialWearOff_EmitsPartiallyWoreOff()
    {
        var h = InGameMode();
        h.Feed(End("Some of your magical clumsiness has worn off."));
        var e = Assert.Single(h.StatusEffects);
        Assert.Equal(StatusEffectKind.Dexterity, e.Kind);
        Assert.Equal(EffectSign.Debuff, e.Sign);
        Assert.Equal(EffectTransition.PartiallyWoreOff, e.Transition);
    }

    [Fact]
    public void OneDrain_ProducesPartialThenFullUnfitnessWearOff()
    {
        // Real 235427 quirk: a single drain bled off as two 11 03 brackets in one packet —
        // "Some of ... unfitness" then "... unfitness has worn off". Both must decode.
        var h = InGameMode();
        h.Feed([
            .. End("Some of your magical unfitness has worn off."),
            .. End("Your magical unfitness has worn off."),
        ]);
        Assert.Collection(h.StatusEffects,
            e => { Assert.Equal(StatusEffectKind.Stamina, e.Kind); Assert.Equal(EffectSign.Debuff, e.Sign); Assert.Equal(EffectTransition.PartiallyWoreOff, e.Transition); },
            e => { Assert.Equal(StatusEffectKind.Stamina, e.Kind); Assert.Equal(EffectSign.Debuff, e.Sign); Assert.Equal(EffectTransition.FullyWoreOff, e.Transition); });
    }

    [Fact]
    public void StackedWeaken_StaysOneDebuffAcrossPartialsUntilFullClear()
    {
        // Real 002448 capture: 3× weaken → one STR-debuff that bled off as two partials
        // then a full clear. The parser reports every transition faithfully; a binary
        // tracker keeps the slot on from the first Started until FullyWoreOff.
        var h = InGameMode();
        for (int i = 0; i < 3; i++)
            h.Feed(Start("You have suddenly and magically become weaker!"));
        h.Feed(End("Some of your magical weakness has worn off."));
        h.Feed(End("Some of your magical weakness has worn off."));
        h.Feed(End("Your magical weakness has worn off."));

        Assert.All(h.StatusEffects, e =>
        {
            Assert.Equal(StatusEffectKind.Strength, e.Kind);
            Assert.Equal(EffectSign.Debuff, e.Sign);
        });
        Assert.Equal(
            [EffectTransition.Started, EffectTransition.Started, EffectTransition.Started,
             EffectTransition.PartiallyWoreOff, EffectTransition.PartiallyWoreOff, EffectTransition.FullyWoreOff],
            h.StatusEffects.ConvertAll(e => e.Transition));
    }

    // ── Glow (11 00 / 11 01) ────────────────────────────────────────────────────

    // 11 00 disabling-start + glow phrase. Byte C00 = 0x9B.
    private static byte[] DisableStart(string phrase) => Bracket(0x9B, phrase);
    // 11 01 disabling-end + phrase. Byte C01 = 0x9C.
    private static byte[] DisableEnd(string phrase)   => Bracket(0x9C, phrase);

    [Fact]
    public void Glow_EmitsGlowStarted()
    {
        var h = InGameMode();
        h.Feed(DisableStart("You have suddenly and magically started glowing!"));
        var e = Assert.Single(h.StatusEffects);
        Assert.Equal(StatusEffectKind.Glow, e.Kind);
        Assert.Equal(EffectTransition.Started, e.Transition);
    }

    [Fact]
    public void Unglow_EmitsGlowFullyWoreOff()
    {
        var h = InGameMode();
        h.Feed(DisableEnd("You have suddenly and magically regained your original state of not glowing!"));
        var e = Assert.Single(h.StatusEffects);
        Assert.Equal(StatusEffectKind.Glow, e.Kind);
        Assert.Equal(EffectTransition.FullyWoreOff, e.Transition);
    }

    [Theory]
    // Regression (bug h): ailments share the disabling code family with glow. Their start/end
    // must NOT emit a GLOW event — "regained your hearing" was clearing the glow icon. (Afflictions
    // do emit their own tooltip-only change on start; they must never be a Glow change.)
    [InlineData("You have suddenly and magically gone deaf!")]
    [InlineData("You have suddenly and magically regained your hearing!")]
    [InlineData("You have suddenly and magically gone blind!")]
    [InlineData("You have suddenly and magically regained your sight!")]
    public void Ailment_DisablingCode_DoesNotEmitGlow(string phrase)
    {
        var h = InGameMode();
        h.Feed(DisableStart(phrase));
        h.Feed(DisableEnd(phrase));
        Assert.DoesNotContain(h.StatusEffects, e => e.Kind == StatusEffectKind.Glow);
    }

    [Fact]
    public void AfflictionStart_EmitsTooltipChangeWithDetectedLine()
    {
        var h = InGameMode();
        h.Feed(DisableStart("You have suddenly and magically gone deaf!"));
        var e = Assert.Single(h.StatusEffects);
        Assert.Equal(StatusEffectKind.Deaf, e.Kind);
        Assert.Equal("You have suddenly and magically gone deaf!", e.Message);
    }

    [Fact]
    public void GlowSurvives_WhenAilmentEndsBeside_It()
    {
        // Exact bug h sequence: glow on, deaf on, then deafness wears off. Glow must persist —
        // no glow-off event, regardless of the deaf affliction change that also fires.
        var h = InGameMode();
        h.Feed(DisableStart("You have suddenly and magically started glowing!"));
        h.Feed(DisableStart("You have suddenly and magically gone deaf!"));
        h.Feed(DisableEnd("You have suddenly and magically regained your hearing!"));
        var glow = Assert.Single(h.StatusEffects, e => e.Kind == StatusEffectKind.Glow);
        Assert.Equal(EffectTransition.Started, glow.Transition);
    }

    [Fact]
    public void Start_PhraseStillDisplayed()
    {
        var h = InGameMode();
        h.Feed(Start("You have suddenly and magically become stronger!"));
        h.Feed("\r\n"); // line ending flushes the accumulated span (as in the real capture)
        Assert.Contains(h.Lines, l => l.PlainText.Contains("become stronger!"));
    }
}
