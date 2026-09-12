namespace MudSharp.Models;

/// <summary>
/// One <c>(Persona saved on ...)</c> line: MUD2 announcing, explicitly, that the player's score just
/// changed and what it is now.
///
/// <para><b>Why this is an event and not a stat.</b> Score is not a per-swing quantity and is not
/// earned per swing. One blow can finish several creatures, the game names which one it scored you
/// for, and it sometimes says so across more than one line. A column on a swing row therefore cannot
/// hold the fact without inventing an attribution; a row of its own can hold it exactly as given, and
/// leave attribution to analysis that can see the whole frame.</para>
///
/// <para><b>The three forms on the wire</b>, counted across the 877 occurrences in the raw session
/// recordings:
/// <list type="bullet">
/// <item><c>(Persona saved on +38 = 19,214).</c> - a gain. 802 of them.</item>
/// <item><c>(Persona saved on -872 = 18,382).</c> - a loss, and the reason this matters: a flee cost
/// lands here and nowhere else. 15 of them.</item>
/// <item><c>(Persona saved on 45,691).</c> - no delta at all, just the total. 60 of them, and they
/// are the world reset and the shell-exit save, not a scoring event. <see cref="Delta"/> is null for
/// these, never zero: "the game did not say" and "the game said nothing changed" are different
/// facts and only one of them is true here.</item>
/// </list></para>
///
/// <para>On the wire the total is wrapped in a C1 frame (<c>F4 9C FF FF FE 9x FF FF</c> ... <c>FF FF FF
/// FF</c>, the inner code varying with the colour the server wants) while the signed delta is plain
/// text outside it. The decoder strips the frame, so by the time this is parsed the line is ordinary
/// ASCII - but see GameLineAnalyzerTests for the verbatim-bytes test that pins that down, because it
/// is the decoder's behaviour and not something this parse may assume.</para>
/// </summary>
/// <param name="Delta">The signed change, or null when the line carried none.</param>
/// <param name="Total">The player's score after the change. Always present - a line whose total does
/// not parse produces no event at all.</param>
/// <param name="RawText">The line as it was printed, kept so a later reading can re-parse it rather
/// than trust this one.</param>
public sealed record ScoreSave(int? Delta, int Total, string RawText);
