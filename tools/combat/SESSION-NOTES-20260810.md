# Session notes — 2026-08-10 captures

Source captures (both already ingested into the verify DB — `captures` table):

- **Session A**: `session-rec.mud2.co.uk.20260810-152631.jsonl` (5055 lines, ~1.1MB), capture id `0a605df5144af51362a6e576`. Runs 1786400791230 → 1786403659535 ms (47m48s wall clock, though the last ~6 minutes are idle after the character goes to sleep via `resite`).
- **Session B**: `session-rec.mud2.co.uk.20260810-161441.jsonl` (2940 lines), capture id `6dbee77203eb0c7618032400`. Runs 1786403681670 → 1786404947904 ms (21m06s), starting ~22s after Session A stopped — this is a continuous play session split across two captures.

All timestamps below are `+HH:MM:SS.mmm` relative to that capture's own start, plus the JSONL `seq<N>` line index (0-based) so any line can be found again directly (`sed -n 'N,Mp'` on the raw file, or `seq_index` in `raw_events`). Decoding method: reused `tools/mapping/decode_probe.py`'s `decode_rx` (telnet-unescape → C1-tag decode → strip tags) via a throwaway script (`decode_full.py`, written to the scratchpad), which also folds in `tx`/`an` records with the same timestamp base. Game text below is quoted verbatim from that decode.

## Session-level summary

Character: **Ollie the necromancer**, level 8, playing with a fixed party of **Moosebear the warrior** and **Drizzle the wobbly mage** (joined later in Session B by **Atomicbob the mage**). Session A opens at score **45,531**, ends at **44,824** (net −707, dominated by the −2,079 ram flee below). Session B opens at 44,824 and closes at **45,677** (net +853) with the sign-off line `"Gotta go cook, back in an hour.` and `capture stopped`.

Across the two captures the player fights: single zombies (falchion, later a croquet mallet under a strength/dexterity buff), a pack of 4+ rats, a "ram" (the session's single worst fight — see item h), a raven (chased across three rooms — item j), and a long, repetitive run of water-snakes (with an axe, then a falchion) that dominates the back half of both files. `//NOTE:` annotations are the owner's own deliberate commentary, typed as ordinary `tx` lines during play — every one is transcribed verbatim in section (a).

---

## (a) Every `//NOTE` annotation, verbatim, with context

All 30 notes below are exact `tx` text. Where useful I've quoted the immediately preceding/following game line(s) so the note is self-explanatory; fuller context for the thematically-grouped ones is in sections (b)–(j).

**Session A** (`...152631.jsonl`):

1. `+00:00:10.581 [seq8]` — `//NOTE: Using //NOTE: to make annotations` (the convention-setting first note).
2. `+00:00:35.565 [seq92]` — `//NOTE: Single z with falchion`. Preceded by `You miss the zombie1. / The zombie1 misses you.` (falchion vs. a lone zombie).
3. `+00:01:12.062 [seq140]` — `//NOTE: combat tick bar is not smooth - it seems to slow down towards the right.` (UI/feel observation about the client's own combat-tick indicator, not a game line).
4. `+00:01:40.665 [seq236]` — `//NOTE: second encounter with a z, staring with less sta on my part`. Preceded by `You hit the zombie0 (5-9). / The zombie0 looks superficially damaged. / The zombie0 hits you (69/105).`
5. `+00:08:15.623 [seq1596]` — `//NOTE: +str +dex boost, using mallet not falchion, single z`. Carried item right after: `croquet mallet` (replacing the falchion).
6. `+00:10:01.423 [seq1993]` — `//NOTE: z has a wafer may self-head. I still ahve str/dex boost` [sic]. See item (c) for the actual eat-and-heal line 23 seconds later.
7. `+00:16:31.359 [seq2816]` — `//NOTE: multi-combatant engagements with 'rats' (low dps creatures with low health)`.
8. `+00:18:36.017 [seq3179]` — `//NOTE: ram, notoriously risky fight, frequent fleer`.
9. `+00:18:46.012 [seq3192]` — `//NOTE: temporary self heal (sta boost)`. Two seconds later: `refresh` → `Your spell worked! You emit a bright magenta flash of light. You have suddenly and magically become fitter!` (stamina 63→68). See item (d).
10. `+00:19:03.773 [seq3223]` — `//NOTE: attempt to blind the ram (stat reduc, often backfires)`. The attempt (`blind ram`) actually **backfired immediately**: `Your spell failed! You have suddenly and magically gone blind!` (self-blind), requiring `unblind me` moments later.
11. `+00:19:42.436 [seq3307]` — `//NOTE: could 'clumsify' or 'weaken' the ram, but might backfire too`.
12. `+00:20:09.713 [seq3397]` — `//NOTE: HUGE point less for that flee!` — written ~21s after the −2,079-point flee in item (h).
13. `+00:20:41.238 [seq3441]` — `//NOTE: But I couldn't tell how dangerous staying was - and dying in combat = deletion`.
14. `+00:22:09.190 [seq3497]` — `//NOTE: useful to surface max observed hit for any given npc, that might have saved me 1000 points` — a direct feature request for the combat HUD.
15. `+00:22:35.417 [seq3596]` — `//NOTE: pre-emptive blind` (blinding the ram again before re-engaging, this time successfully: `Your spell worked! The ram has gone blind!` at seq3589).
16. `+00:23:29.302 [seq3660]` — `//NOTE: fleeing gave ram points, potentially levelled it, but only get a fraction back from vengance. I'm still -628 points from when I started this session.` [sic "vengance"]. This is written right after `You have killed the ram. (Persona saved on +313 = 44,650).` — i.e. killing the ram in revenge only recovered 313 of the 2,079 lost fleeing it.
17. `+00:24:20.368 [seq3780]` — `//NOTE: armed zombie`. Immediately preceded by `The zombie8 has started to use the axe1 to fight!` — an NPC arming itself mid-fight.
18. `+00:41:01.381 [seq4907]` — `//NOTE: attacking watersnakes with axe`.
19. `+00:41:36.765 [seq4991]` — `//NOTE: Used stethoscope to get a health read on snake`. (The actual mechanism observed nearby is the `diagnose <target>` **spell** — `Your spell worked! The giant snake has a stamina lying between 117 and 126.` — not an item-use verb; a literal `wear stethoscope` a few lines earlier failed with `Stethoscopes aren't in fashion at the moment...`. Flagging the discrepancy rather than guessing which the owner meant.)
20. `+00:41:44.901 [seq5013]` — `//NOTE: late str/dex boost`.
21. `+00:41:52.889 [seq5029]` — `//NOTE: expired dreamword (no heal)`. See item (d) — the dreamword `"zougnoa"` was spoken after it had expired, producing only `Ollie the necromancer says "zougnoa".` with no heal text at all.

**Session B** (`...161441.jsonl`):

22. `+00:00:50.887 [seq210]` — `//NOTE: falchion this time` (contrasted with the axe used at the end of Session A).
23. `+00:04:54.459 [seq666]` — `//NOTE: Managed to get leading sta claim (see diagnose)`. Refers to `diagnose snake` → `Your spell worked! The water-snake5 has a stamina lying between 90 and 99.` fired a moment earlier while actually fighting `water-snake4` (the diagnose spell targeted a different-numbered snake than the one currently being hit) — worth flagging as a targeting-ambiguity risk for anything that trusts "diagnose" output to label the *current* opponent.
24. `+00:05:08.267 [seq700]` — `//NOTE: incoming dream word heal`. Written the instant before `You have killed the water-snake3. (Persona saved on +86 = 44,996).`, just ahead of the dreamword speak.
25. `+00:05:16.988 [seq718]` — `//NOTE: expired again!` — second expired dreamword in a row (`"ayxaygiescoo"` → only `Ollie the necromancer says "ayxaygiescoo". Because you can't see anyone else here, it may be that you are speaking to yourself...`, no heal).
26. `+00:08:26.666 [seq1013]` — `//NOTE: successful dreamword. 86->105 sta`. Confirmed by the game text 7 seconds earlier: `Ollie the necromancer says "gneaptiacrey". ... A pleasant warmness courses through your veins... You feel better already. Your stamina is 105.`
27. `+00:09:59.557 [seq1169]` — `//NOTE: snakes are notoriously likely to flee`. Written in the middle of the water-snake5 failed-flee run described in item (i) below.
28. `+00:15:26.763 [seq1984]` — `//NOTE: multiple npcs`. See item (e) — 4 rats (rat16/17/19/20) engaged simultaneously.
29. `+00:16:47.479 [seq2196]` — `//NOTE: annyoing chase to finish raven` [sic "annyoing"] — written just after the raven kill in item (j).

That's 21 + 8 = **29 distinct texts across 30 annotation lines** (there is no 30th distinct note; the count of 30 in the task brief includes... actually recount: 21 in Session A + 8 in Session B = 29 total `//NOTE` lines, not 30 — stated here explicitly rather than padding the list to a round number).

---

## (b) Ordinary combat encounters — weapon and stat variations

| Encounter | Weapon | Stat state | Evidence |
|---|---|---|---|
| zombie1, solo (Session A, +00:00:35) | falchion | baseline (str 94/100, dex 99/100) | `You miss the zombie1. / The zombie1 misses you.` — see note 2 |
| zombie0, second encounter (+00:01:40) | falchion | started at lower stamina ("staring with less sta on my part" — note 4) | `You hit the zombie0 (5-9). / The zombie0 looks superficially damaged. / The zombie0 hits you (69/105).` |
| zombie7, solo (+00:08:15–00:10:20) | **croquet mallet** (not falchion) | **+str +dex boost** active: `You have suddenly and magically become stronger!` (+00:08:01) and `...become more adroit!` (+00:08:02); wears off later at `+00:10:20.420`: `Your magical dexterity has worn off.` | `k z with mallet` → `You attack the zombie7, using the croquet mallet as a weapon.` |
| rats (rat16/17/19/20), pack (+00:15:19 onward) | falchion | baseline | see item (e) |
| ram, solo (+00:18:20–00:23:00) | **axe0** | multiple casts mid-fight: `refresh` (temp heal, item d), `blind ram` (backfired then succeeded), stamina crashes to 19 forcing a flee (item h) | see items (d), (h) |
| zombie8, "armed zombie" (+00:24:07–00:24:20) | axe0 (player) vs NPC's **axe1** | baseline | `The zombie8 has started to use the axe1 to fight!` — note 17 |
| water-snakes, session A tail (+00:40:51 onward) | **axe0** | baseline, `diagnose`/stethoscope readings taken | note 18; item (c)'s wafer-eating zombie is earlier in the same session, unrelated NPC |
| water-snakes, session B (+00:00:46 onward, and +00:09:36–00:09:51 specifically) | **falchion** | baseline; several `str`/`blind <target>` casts interleaved (`blind snake`, `blind ws3`, `blind ws1`) | note 22; item (i) |
| raven, session B (+00:15:56–00:16:37) | unarmed → falchion mid-fight | baseline | item (j) |

Carrying/dropping was frequent but not obviously tied to a stat swing in this data: large item drops (e.g. `+00:04:17.837`: `Etude dropped. / Tazza dropped. / Tureen dropped. / Painting2 dropped. / Bonnet dropped.`, offloading a treasure haul) show no corresponding jump in the FES strength/dexterity fields in the surrounding ticks — effective strength stayed pinned at 50/100 through that whole sequence, only dexterity crept up (90→93) from natural regen. Worth stating plainly: **this capture does not contain clear before/after evidence that dropping carried weight changes combat stats** — CombatTracker's own comment about a "hidden gate on effective strength... depressed by carried weight" (`WeaponUnusable` doc comment) remains uncorroborated by this data, matching the code comment's own admission that the research corpus has zero direct observations of the wield-refusal line.

---

## (c) NPC eats a wafer and heals itself

Zombie7 (fought with the croquet mallet, +str/+dex still active per note 6):

- `+00:10:10.422 [seq2011]` — `You hit the zombie7 (15-19). / The zombie7 looks seriously damaged.`
- `+00:10:15.422 [seq2016]` — **`The zombie7 has eaten a wafer10.`** (no combat lines in this tick at all — a pure self-heal action)
- `+00:10:17.296 [seq2020]` — `You hit the zombie7 (10-14). / The zombie7 looks superficially damaged.`

The health descriptor genuinely improved — **"seriously damaged" → "superficially damaged"** — a jump of 3 rungs on the `NpcHealthRungs` ladder (3 → 6) in a single tick with no player action in between, directly attributable to the wafer. This is the cleanest confirmation in either capture that eating a wafer is a real, mechanically-effective NPC self-heal, not flavour text.

---

## (d) `refresh` (temporary heal) vs. dreamword (real heal) — and their FES signatures

**`refresh` (temporary):**

- `+00:18:48.406 [seq3198]` tx `refresh` → `+00:18:48.547 [seq3199]`: `Your spell worked! You emit a bright magenta flash of light. You have suddenly and magically become fitter!`
- Stamina before: 63/105 (seq3195). Immediately after: 68/105 (seq3202) — **+5**.
- It wears off explicitly with its own text: `+00:19:58.060 [seq3386]`: `Your magical fitness has worn off.` (after a `sleep in bed` command). Between the cast and the wear-off, max stamina never changed (`105` throughout) — only the temporary top-up decayed away as ordinary combat damage was taken in between; there's no separate "stamina drops back down" event because the fight kept draining it anyway.

**Dreamword (real heal), success case:**

- `+00:04:58.674 [seq1012]` `an| dreamword spoken: pyskabou` → tx `"pyskabou"` → `+00:04:58.816 [seq1014]`: `You wake up! Your stamina is 62. Ollie the necromancer says "pyskabou". Because you can't see anyone else here, it may be that you are speaking to yourself... A pleasant warmness courses through your veins... You feel better already. Your stamina is 74.`
- **62 → 74**, and the very next FES read (`74 105 50 100 93 100 105 105 45691 ...`) confirms 74 is the new baseline — it does **not** decay afterward the way `refresh`'s boost does; it's a straight stamina credit with no separate expiry message.
- A second clean success in Session B: `+00:08:19.620 [seq1002]`: `Ollie the necromancer says "gneaptiacrey". ... A pleasant warmness courses through your veins... You feel better already. Your stamina is 105.` (confirmed by note 26: "successful dreamword. 86->105 sta").

**Dreamword, expired-before-spoken case** (the contrast the task asked for): twice a dreamword was spoken after its window lapsed, and produced **no heal text and no stamina change at all** — just the bare speech-echo line:
- `+00:41:45.744 [seq5019]`: `"zougnoa" / Ollie the necromancer says "zougnoa".` — nothing else. FES stamina stayed at 81 before and after (seq5016 → seq5032, unchanged apart from ordinary combat).
- `+00:05:09.152 [seq708]` (Session B): `Ollie the necromancer says "ayxaygiescoo". Because you can't see anyone else here, it may be that you are speaking to yourself...` — again no heal clause, stamina held at 71.

**Summary distinction for the HUD:** a *successful* dreamword always carries the two extra sentences `A pleasant warmness courses through your veins...` / `You feel better already.` plus an explicit `Your stamina is NN.` restatement; a *lapsed* one only ever echoes the speech line. `refresh`'s temporary boost instead prints `You have suddenly and magically become fitter!` (no stamina sentence at all — you must read the next FES poll) and is later closed out by a distinct `Your magical fitness has worn off.` line that a real heal never gets.

---

## (e) Multi-NPC encounters

Largest observed: **4 rats simultaneously** (rat16, rat17, rat19, rat20) in Session B, cellar/tomb area starting ~`+00:15:19`. Example of several attacking in one tick:

```
+00:15:20.429 [seq1966] The raven has flown off.
The rat16 hits you (104/105).
You hit the rat16 (5-9).
The rat16 looks superficially injured.
The rat19 hits you (101/105).
You miss the rat19.
The rat20 misses you.
You miss the rat20.
The rat17 misses you.
You hit the rat17 (15-19).
The rat17 looks seriously injured.
```

That's 4 NPC actions and 4 player actions interleaved by name in a single poll response — i.e. up to **8 combat lines land in one tick** in a 4-way pack fight. A 5th rat (rat18) and 6th (rat21) join later in the same area (`+00:16:47`, `+00:17:00`), each announced only via the non-aggro `bares its razor-sharp incisors at you` join line (see the parser-gap section — this line is explicitly called out in `CombatTracker.cs`'s own comments as an unmatched pack-join phrase, and this capture is a real example of it happening 6+ times).

The earlier note-2816 "rats" reference (`multi-combatant engagements with 'rats' (low dps creatures with low health)`) is from Session A's cellar (rat19/rat20 there), a smaller instance of the same pattern.

---

## (f) Unarmed → armed transitions, and the `use` command mistake

Two distinct issues found, both weapon-related:

**1. `use <weapon>` silently substitutes a different weapon when the named one isn't held**, with no error:

```
+00:15:20.429 [seq1966] rx| ... ======== cowrie / femur / falchion   (carried: no axe)
+00:15:21.560 [seq1968] tx| use axe
+00:15:21.710 [seq1969] rx| use axe
You are now using the falchion to fight!
```

The player typed `use axe` while not actually carrying an axe (only cowrie/femur/falchion were held) — MUD2 did not refuse or report "you don't have an axe", it just silently armed the **falchion** instead. Contrast with `use axe` working exactly as intended earlier in Session A when an axe genuinely was held: `+00:16:35.622 [seq2832] tx| use axe` → `You are now using the axe0 to fight!`. The failure mode is entirely about inventory state, not command syntax — and it repeats through the whole raven chase below (`k bird wi axe`, `se,k bird wi axe`, `nw,k bird wi axe`, `n, k bird wi axe` — every one resolves to `..., using the falchion as a weapon.` because no axe was ever carried in that fight).

The safer, working alternative used later in the same session: **`wield best weap`** — it never names a specific item, so it can't silently mismatch:

```
+00:15:54.874 [seq2082] tx| wield best weap
+00:16:24.001 [seq2151] rx| wield best weap
You are now using the falchion to fight!
```

**2. Attacking before arming leaves you fighting unarmed even with a weapon in inventory.** The very first raven attack:

```
+00:16:23.663 [seq2148] tx| k bird
+00:16:23.941 [seq2150] tx| wield best weap
+00:16:24.001 [seq2151] rx| You attack the raven.          <- unarmed form, despite falchion in inventory
...
You are now using the falchion to fight!                    <- arms up only now, mid-fight
You miss the raven. / The raven misses you.
```

`k bird` (kill) fired the attack **before** the `wield best weap` command was actually processed, so the fight opened unarmed (`You attack the raven.` — no `, using the X as a weapon` clause) even though a falchion was already carried. The correct sequencing is to arm (`wield`/`use`) **before** issuing the attack, not react to being unarmed after the fact.

---

## (g) Other `//NOTE`-adjacent observations

- Combat-tick UI feel: `//NOTE: combat tick bar is not smooth - it seems to slow down towards the right.` (note 3) — a client-side rendering observation, not a game mechanic.
- Debuff risk on the ram: two notes flag that offensive stat-reduction spells against the ram are risky — `//NOTE: attempt to blind the ram (stat reduc, often backfires)` (note 10, and it did immediately backfire, self-blinding the caster) and `//NOTE: could 'clumsify' or 'weaken' the ram, but might backfire too` (note 11, never actually attempted in this capture).
- Direct feature ask for the HUD: `//NOTE: useful to surface max observed hit for any given npc, that might have saved me 1000 points` (note 14) — i.e. surfacing per-NPC max observed damage as a decision aid before committing to a fight.
- Diagnose/stethoscope ambiguity: note 19 and note 23 both reference "diagnose"/"stethoscope" as a stamina-range read tool; the actual in-game mechanism observed is the `diagnose <target>` **spell**, and it can target a different-numbered NPC than the one currently engaged (see item (a) note 23's detail) — a real risk if a future HUD auto-attributes diagnose output to "the current opponent."
- ~~Session framing: note 16's arithmetic doesn't reconcile against the raw score log, so treat the
  owner's inline point deltas as approximate mental math.~~ **RETRACTED - this was wrong, and the
  error was the reviewer's.** Note 16 says "since I started this session", and a *session* is the
  owner's play session, not this capture file: it spanned an earlier connection that predates the
  recording, and Mucka reports score combined across connections. The capture opening at 45,531 is
  simply where the log starts, not where the session started, so there is nothing to reconcile.
  −628 was correct. Do not treat the owner's in-session annotations as sloppy arithmetic; check
  what the words actually refer to first.

---

## (h) The 19-stamina flee that cost ~2,079 points

Session A, fighting the ram with axe0:

```
+00:19:45.421 [seq3314] The ram hits you (19/105).
                        You miss the ram.
+00:19:47.285 [seq3319] (FES: stamina 19/105, score 46,416)
+00:19:48.293 [seq3321] tx| flee w
+00:19:48.433 [seq3322] rx| flee w
                        (Persona saved on -2,079 = 44,337).
                        Axe0 dropped.
                        You have fled by going west.
                        Gorse.
                        You are in a tangled mass of prickly gorse. A croquet mallet has been left within reach.
```

Score immediately before the flee: **46,416**. Immediately after: **44,337** (a loss of exactly **2,079**, matching "~2000 from ~46000" almost exactly). Stamina at the moment of the flee: **19/105** (confirmed both by the preceding `The ram hits you (19/105)` line and the FES snapshot). Note the flee also **auto-dropped the current weapon** (`Axe0 dropped.`) as a side effect — not something `CombatTracker.cs` tracks anywhere.

The player later returns and kills the same ram for partial "revenge": `+00:23:00.418 [seq3638]`: `You have killed the ram. (Persona saved on +313 = 44,650). The ram has just passed on.` — recovering only 313 of the 2,079 lost, exactly as note 16 describes.

---

## (i) Water-snake fights — the flee attempts that "almost never succeed" and break the fight sequence

Session B, `water-snake5`, `+00:09:36` to `+00:09:51` (falchion). Correcting one detail up front: it is the **water-snake (NPC) that keeps trying and failing to flee**, not the player — note 27 says exactly this ("snakes are notoriously likely to flee"), and it's the water-snake's flee line, not a player `flee` command (there is only one player-typed `flee` command in either entire capture — the ram flee in item h). Seven failed flee attempts in 13 seconds, each in a **different, seemingly random direction**:

```
+00:09:36.846  The water-snake5 has fled by trying to go over.
+00:09:40.315  The water-snake5 has fled by trying to go up.
+00:09:41.837  The water-snake5 has fled by trying to go south.
+00:09:43.579  The water-snake5 has fled by trying to go in.
+00:09:47.561  The water-snake5 has fled by trying to go swampward.
+00:09:49.650  The water-snake5 has fled by trying to go northwest.
```
(An 8th, `+00:09:45.532`, repeats "up".) The snake **never actually leaves the room** — it's still there to re-attack every single time.

How this "breaks the fight sequence": every one of these failed-flee lines is immediately followed by `You can fight it no longer.` and a small score credit (`(Persona saved on +4 = 45,086).` etc.), **exactly as if the flee had succeeded** — the current fight is terminated in-game regardless of whether the snake actually went anywhere. That forces the player to manually re-issue the attack command from scratch every time:

```
+00:09:39.503 tx| kill ws with axe   -- wait, "with axe" though only falchion is held (see item f)
+00:09:40.315 rx| You attack the water-snake5, using the falchion as a weapon.
              You miss the water-snake5. / The water-snake5 misses you.
              The water-snake5 has fled by trying to go up.
              You can fight it no longer.
              (Persona saved on +4 = 45,090).
```
...repeated 7 times before the snake is finally killed on the 8th re-attack (`+00:09:51.551`: `You have killed the water-snake5. (Persona saved on +79 = 45,189).`). So what looks in-game like one continuous fight against one snake is actually **eight separate micro-encounters**, each opened by a fresh `You attack the water-snake5...` line and closed by a fake-successful flee. See the parser-gap section below — this is the single most actionable finding in this file.

---

## (j) The bird (raven) chase

Session B, `+00:15:56` to `+00:16:37`, three flee/chase cycles before the kill:

1. **First flee — southeast.** `+00:16:24.001 [seq2151]`: `You miss the raven. / The raven misses you. / The raven has fled by going southeast. / You can fight it no longer. / Fluttering wildly, the raven has flown off.`
   Chase command: `+00:16:28.204 [seq2158] tx| se,k bird wi axe` (move southeast into the room the raven fled to, then re-attack in the same input).
2. **Second flee — northwest.** `+00:16:28.349 [seq2159]`: `You attack the raven, using the falchion as a weapon. / You miss the raven. / The raven misses you. / The raven has fled by going northwest. / You can fight it no longer. / Fluttering wildly, the raven has flown off.`
   Chase command: `+00:16:31.367 [seq2167] tx| nw,k bird wi axe`.
3. **Third flee — north.** `+00:16:31.868 [seq2171]`: `You miss the raven. / The raven hits you (77/105). / The raven has fled by going north. / You can fight it no longer. / Fluttering wildly, the raven has flown off.`
   Chase command: `+00:16:36.904 [seq2180] tx| n, k bird wi axe`.
4. **Kill.** `+00:16:37.400 [seq2184]`: `You attack the raven, using the falchion as a weapon. / You hit the raven (1-4). / You have killed the raven. / (Persona saved on +34 = 45,407). / The raven has just passed on.`

Note the player's chase command is always the same shape: `<direction>,k bird wi axe` (or `n, k bird wi axe` with a space) — move into the room the fled-to direction implies, then re-issue the kill command in the same line, every time still asking for "axe" despite never actually holding one in this fight (falchion is what's actually used throughout, per item f). Note 29 ("annyoing chase to finish raven") is the owner's own summary of exactly this three-room chase.

---

## Lines the parser does not recognise

Checked every combat-adjacent line captured against the regexes in `G:\Source\mucka\combat\mudsharp\Combat\CombatTracker.cs` and `NpcHealthRungs.cs`. Ranked roughly by how actionable each gap is for the combat-HUD project.

1. **`The <npc> has fled by trying to go <direction>.`** — 7 occurrences (all `water-snake5`, Session B, `+00:09:36`–`+00:09:49`: directions `over`, `up` (×2), `south`, `in`, `swampward`, `northwest`). `NpcFled` only matches `"has fled by going \w+\."` — the word **"trying to"** makes this a completely different phrase that never matches. Consequence: `Begin()`/`End()` bookkeeping never removes this NPC from `_active` the way a real `NpcFled` would, yet the very next line every time is `You can fight it no longer.` (`FightEndOther`), which the tracker's own code comment documents as "informational only... never authoritative for combat state on its own" specifically *because* it's supposed to always trail an already-processed `NpcFled`. That assumption is false for this line — see item (i) above for the full mechanical picture (8 micro-fights against one snake). **This is the single highest-value fix**: either extend `NpcFled` to accept `"has fled by trying to go \w+"` too, or treat `FightEndOther` as authoritative when no matching `NpcFled`/kill/withdraw preceded it within the same tick.

    > **SUPERSEDED 2026-08-19 -- see `FIGHT-ENDS.md`.** The gap was real and is now fixed, but this
    > item's reading of it was wrong in one important way, and the wrong reading is what shipped.
    > The line was given its own event kind (`NpcFleeFailed`) that deliberately did **not** end the
    > fight, on the reasoning recorded in item (i): eight micro-fights against one snake looked like
    > fragmentation to be avoided. Per the owner, a failed flee **does** end the fight -- MUD2 breaks
    > the sequence and the player must attack again -- so those eight really are eight encounters,
    > each its own frame, command and weapon selection. Because nothing else in the frame can close a
    > fight, refusing to close here left any fight the player walked away from stuck "in combat" until
    > reset or logout. Read `FIGHT-ENDS.md` before touching fight-end detection; it lists all seven
    > ends with a verbatim capture each.

2. **`The <npc> looks covered in wounds, and is holding the following:`** — `+00:22:48.053 [seq3619]`, the ram. `NpcHealthRungs.Line` is anchored `^The (?<npc>.+?) looks (?<desc>[a-z][a-z ]*)\.$` — it requires the line to *end* right after the descriptor and full stop. This line continues past the rung phrase into an inventory-list intro, so the whole health update (a legitimate "covered in wounds" reading) is silently dropped rather than rung-4-scored.

3. **`The <npc> has eaten a wafer<N>.`** — `+00:10:15.422 [seq2016]`, zombie7. No pattern anywhere in `CombatTracker.cs` covers NPC self-healing via item consumption; combined with item (c) above (a confirmed, mechanically-real heal — "seriously damaged" → "superficially damaged" in one tick with zero player action), a HUD that only watches hit/miss/health-rung lines would see the NPC's health improve with no attributable cause.

4. **Pack-join lines that aren't the six verbs `NpcAggroStart` knows** (`looking at / glaring at / snarling at / moving towards / rushing at / advancing towards / approaching / staring at`) — confirmed present repeatedly in this data:
   - `An evil, black rat (rat<N>) bares its razor-sharp incisors at you.` — 9 occurrences across both files (rats numbered 16, 17, 18, 19, 20, 21 across the two separate rat-pack scenes in Session A and Session B).
   - `A large, black raven swoops about your head.` — 8 occurrences (room-description form, precedes every raven encounter).
   This is a documented, self-acknowledged gap already (see the comment on `YouHit` in `CombatTracker.cs`: *"e.g. only a 'bares its razor-sharp incisors at you' join message we don't classify as a start"*) — this capture supplies concrete corpus evidence (both exact phrases, with counts) if that gap is ever closed.

5. **`A horrifying zombie (zombie<N>) stands in your way!`** — 8 occurrences, always as a room-description clause (e.g. `+00:00:30.203`, `+00:01:26.090`, `+00:09:46.851`). Every zombie fight in Session A is preceded by this exact phrasing. Likely deliberately excluded by the existing "don't open a phantom encounter from a room description" design (the same caution `NpcHealthRungs`'s own doc comment raises) rather than a bug — flagged here for completeness, not necessarily actionable.

6. **Buff/debuff/heal prose — entirely outside `CombatTracker.cs`'s scope today, but load-bearing for a combat HUD that wants to show why damage output/health changed:**
   - Grant: `You have suddenly and magically become stronger!` / `...more adroit!` (dex) / `...fitter!` (refresh, item d).
   - Expiry: `Your magical strength has worn off.` / `Your magical dexterity has worn off.` / `Your magical fitness has worn off.`
   - Dreamword heal: `A pleasant warmness courses through your veins... You feel better already. Your stamina is NN.` (only on a *successful* dreamword — see item d).
   - Debuff outcomes: `The <npc> has gone blind!` (target) and the self-inflicted `You have suddenly and magically gone blind!` / `...regained your sight!` (backfire case, item g).
   - Spell resolution meta-lines with no target info at all: `Your spell worked!` / `Your spell failed!` / `Your spell doesn't find any.` / `You can't make yourself any stronger!` (spell already capped).

7. **`diagnose`/stethoscope stamina-range reads** — `The <npc> has a stamina lying between <lo> and <hi>.` (e.g. `The giant snake has a stamina lying between 117 and 126.`, `The water-snake5 has a stamina lying between 90 and 99.`, `The viper has a stamina lying between 18 and 27.`). Directly useful for a HUD's opponent-strength estimate (ties to note 14's ask for "max observed hit per NPC"), currently unmatched.

8. **Weapon auto-drop on flee**: `Axe0 dropped.` fires in the same tick as a successful flee (item h) with no accompanying `WeaponBroke`/`WeaponEquip`-style event — `CombatTracker`'s notion of "current weapon" has no mechanism to notice this and would keep reporting the dropped weapon as still equipped.

9. **Environmental (non-NPC) damage**: `Rain has swollen the river to a raging torrent! You fight your way across, but are constantly buffeted and pounded all the way, causing you major injury! (52/105).` (`+00:03:53.631`) — a stamina hit with the same `(cur/max)` suffix style as `NpcHitsYou` but from crossing a ford, not a monster; would currently be invisible to the tracker (no "The X hits you" prefix to match), so a HUD watching only combat regexes could see stamina drop with no attributable cause.

10. **Minor command-rejection lines**, low priority but real: `Stethoscopes aren't in fashion at the moment...` (failed `wear stethoscope`), `I don't know to what "stethoscope" you're referring.` (failed `diagnose X with stethoscope` syntax), `What for? You're not in a fight..!` (`wield best weap` issued while not in combat), `You can't make yourself any stronger!` (str spell already capped).

Not flagged: ordinary `You hit/miss the X`, `The X hits/misses you`, `You have killed/been killed by`, `You are now using the X to fight!`, `The X has started to use the Y to fight!`, and the ordinary `NpcHealthRungs` phrases (`fit`, `superficially injured/damaged`, `to have minor injuries`, `seriously injured/damaged`, `critically injured`, `close to death`, standalone `covered in wounds`) all matched cleanly and repeatedly across both captures — the regex set is solid for the core damage/kill/flee loop; the gaps above are specifically the water-snake failed-flee phrase, the compound health line, item-based healing, pack-join phrasing, and everything buff/debuff/diagnose-related.
