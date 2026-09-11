namespace MudSharp.Models;

/// <summary>
/// One <c>You have completed a Task.</c> line: MUD2 announcing that one of the eight tasks standing
/// between the persona and the top rank has just been discharged.
///
/// <para><b>Why this is parsed at all.</b> Not for the task count - for the score line that follows
/// it. A task pays out through the same <c>(Persona saved on ...)</c> channel as everything else, so
/// a task discharged BY A KILL prints two rises in one frame and the client, pairing the first rise
/// it sees with the kill still waiting for an award, recorded the task's payout as the creature's
/// value. This event is what lets the pairing skip the one that is not a kill award.</para>
///
/// <para><b>The two wordings observed</b>, and the ordering, which is the whole point:
/// <list type="bullet">
/// <item><c>You have completed a Task. This makes a total of 1.</c> - a task discharged for the first
/// time; the running count is the number of the eight now done. Recording
/// <c>session-rec.mud2.co.uk.20260902-232101.jsonl</c>, a dragon: the line is followed by
/// <c>(Persona saved on +100 = 5,144).</c> and then <c>(Persona saved on +976 = 6,120).</c></item>
/// <item><c>You have completed a Task which you have done before.</c> - no count, and it still pays.
/// <c>RESEARCH/mud2-multi-combat.jsonl</c>: <c>+100 = 16,739</c> then <c>+976 = 17,715</c>.</item>
/// </list>
/// The owner's own report (2026-09-09, a water-snake) is the third instance and has the same shape:
/// task line, then <c>+100</c>, then <c>+84</c>. In all three the task payout is printed FIRST and
/// the combat award second, which is the ordering everything downstream relies on.</para>
///
/// <para><b>The line carries no C1 code.</b> Verified against the raw bytes of the 2026-09-02
/// recording: the preceding line is framed (<c>A8 A6 9D FF FF</c> … <c>FF FF</c>) and the task line
/// is bare ASCII between two <c>0D 00 0D 0A</c> breaks, exactly like the <c>(Persona saved on ...)</c>
/// lines under it. Prose is therefore the only handle there is - see GameLineAnalyzerTests for the
/// verbatim-bytes test that pins that down rather than leaving it as a claim.</para>
///
/// <para>The <b>100</b> is deliberately NOT written down anywhere as a constant. Two observations of
/// one figure is not a rule, and nothing needs it: the pairing skips the payout because the game
/// announced a task, not because the number looked like a task's.</para>
/// </summary>
/// <param name="TotalCompleted">The running count from "This makes a total of N", or null for the
/// repeat wording, which states none. Null means "the game did not say", never zero.</param>
/// <param name="RawText">The line as printed, kept so a later reading can re-parse it.</param>
public sealed record TaskCompletion(int? TotalCompleted, string RawText);
