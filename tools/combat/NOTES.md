# Combat reducer notes
Capture: `G:\Source\mucka\RESEARCH\mud2-multi-combat.jsonl`  
Rows: 16862 JSONL lines  
Detected encounters: 58  
Detected fights: 77  
Encounter end-reason breakdown: `flee`=33, `withdraw`=1, `you-killed-them`=24  
Fight outcome breakdown: `killed`=42, `npc-fled`=30, `withdrawn`=1, `you-fled`=4

## Database row counts

- `captures`: 1
- `raw_events`: 15857
- `room_snapshots`: 1906
- `stats_snapshots`: 3044
- `inventory_snapshots`: 2314
- `status_effect_events`: 1
- `status_effect_windows`: 1
- `combat_sessions`: 58
- `combat_fights`: 77
- `combat_events`: 809
- `combat_session_commands`: 126
- `combat_session_stats`: 392
- `combat_session_inventory`: 224
- `combat_session_status_effects`: 36

## Observed literal 08-family sub-codes

| Code | Count | Example 1 | Example 2 |
| --- | ---: | --- | --- |
| `08.00` | 77 | You attack the zombie9, using the axe0 as a weapon. | You attack the raven, using the axe0 as a weapon. |
| `08.01` | 173 | You hit the zombie9 (20-29). | You hit the zombie9 (5-9). |
| `08.02` | 144 | You miss the zombie9. | You miss the zombie9. |
| `08.03` | 126 | The raven hits you (99/100). | The raven hits you (92/100). |
| `08.04` | 167 | The zombie9 misses you. | The zombie9 misses you. |
| `08.07` | 5 | You offer to withdraw from your fight with the banshee. | You offer to withdraw from your fight with the zombie7. |
| `08.08` | 42 | You have killed the zombie9. | You have killed the raven. |
| `08.10` | 1 | The zombie1 withdraws from your fight, and so do you. |  |
| `08.11` | 33 | The raven has fled by going northwest. | The raven has fled by going south. |
| `08.12` | 30 | You can fight it no longer. | You can fight it no longer. |

## Plain-text combat-only events not wrapped in literal 08.05/08.06 tags

| Event type | Count | Example 1 | Example 2 |
| --- | ---: | --- | --- |
| `weapon-change` | 7 | You are now using the axe0 to fight! | You are now using the axe0 to fight! |
| `weapon-broke` | 1 | The dagger0 breaks to bits. |  |
| `dropped-guard` | 3 | You drop your guard as you switch from using the falchion to the dagger0. | Your guard drops momentarily in your confusion. |

- There are no literal `08.05` or `08.06` tags in this capture, but the reducer now records their plain-text equivalents as real `combat_events`.

## Detected encounters

| # | start_ms | end_ms | dur_s | initiator | reason | primary target | participants | you hit/miss | they hit/miss | weapon | fight breakdown |
| ---: | ---: | ---: | ---: | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 1785608531251 | 1785608556127 | 24.876 | player | you-killed-them | zombie9 | zombie9 | 5/2 | 0/6 | axe0 | zombie9[player,axe0,5/2,0/6,killed] |
| 2 | 1785608585979 | 1785608608128 | 22.149 | player | flee | raven | raven | 2/4 | 3/3 | axe0 | raven[player,axe0,2/4,3/3,npc-fled] |
| 3 | 1785608615198 | 1785608616141 | 0.943 | player | flee | raven | raven | 0/1 | 1/0 | axe0 | raven[player,axe0,0/1,1/0,npc-fled] |
| 4 | 1785608619672 | 1785608620278 | 0.606 | player | flee | raven | raven | 1/0 | 0/1 | axe0 | raven[player,axe0,1/0,0/1,npc-fled] |
| 5 | 1785608624702 | 1785608626133 | 1.431 | player | you-killed-them | raven | raven | 1/0 | 0/0 | axe0 | raven[player,axe0,1/0,0/0,killed] |
| 6 | 1785608653116 | 1785608654126 | 1.01 | player | flee | dragonfly | dragonfly | 0/1 | 0/1 | axe0 | dragonfly[player,axe0,0/1,0/1,npc-fled] |
| 7 | 1785608658121 | 1785608658287 | 0.166 | player | flee | dragonfly | dragonfly | 0/1 | 1/0 | axe0 | dragonfly[player,axe0,0/1,1/0,npc-fled] |
| 8 | 1785608661367 | 1785608662132 | 0.765 | player | flee | dragonfly | dragonfly | 0/1 | 0/1 | axe0 | dragonfly[player,axe0,0/1,0/1,npc-fled] |
| 9 | 1785608665216 | 1785608666130 | 0.914 | player | flee | dragonfly | dragonfly | 0/1 | 0/1 | axe0 | dragonfly[player,axe0,0/1,0/1,npc-fled] |
| 10 | 1785608669070 | 1785608670130 | 1.06 | player | flee | dragonfly | dragonfly | 0/1 | 1/0 | axe0 | dragonfly[player,axe0,0/1,1/0,npc-fled] |
| 11 | 1785608673158 | 1785608674130 | 0.972 | player | flee | dragonfly | dragonfly | 1/0 | 0/1 | axe0 | dragonfly[player,axe0,1/0,0/1,npc-fled] |
| 12 | 1785608676912 | 1785608678128 | 1.216 | player | flee | dragonfly | dragonfly | 0/1 | 1/0 | axe0 | dragonfly[player,axe0,0/1,1/0,npc-fled] |
| 13 | 1785608680803 | 1785608682128 | 1.325 | player | flee | dragonfly | dragonfly | 0/1 | 0/1 | axe0 | dragonfly[player,axe0,0/1,0/1,npc-fled] |
| 14 | 1785608685562 | 1785608686192 | 0.63 | player | flee | dragonfly | dragonfly | 0/1 | 0/1 | axe0 | dragonfly[player,axe0,0/1,0/1,npc-fled] |
| 15 | 1785608689269 | 1785608690131 | 0.862 | player | flee | dragonfly | dragonfly | 0/1 | 1/0 | axe0 | dragonfly[player,axe0,0/1,1/0,npc-fled] |
| 16 | 1785608693472 | 1785608694133 | 0.661 | player | flee | dragonfly | dragonfly | 0/1 | 0/1 | axe0 | dragonfly[player,axe0,0/1,0/1,npc-fled] |
| 17 | 1785608697127 | 1785608698130 | 1.003 | player | flee | dragonfly | dragonfly | 0/1 | 0/1 | axe0 | dragonfly[player,axe0,0/1,0/1,npc-fled] |
| 18 | 1785608701042 | 1785608702133 | 1.091 | player | flee | dragonfly | dragonfly | 0/1 | 0/1 | axe0 | dragonfly[player,axe0,0/1,0/1,npc-fled] |
| 19 | 1785608705008 | 1785608706126 | 1.118 | player | flee | dragonfly | dragonfly | 0/1 | 0/1 | axe0 | dragonfly[player,axe0,0/1,0/1,npc-fled] |
| 20 | 1785608708789 | 1785608710130 | 1.341 | player | you-killed-them | dragonfly | dragonfly | 1/0 | 0/0 | axe0 | dragonfly[player,axe0,1/0,0/0,killed] |
| 21 | 1785608716127 | 1785608772151 | 56.024 | npc | you-killed-them | ram | ram | 9/5 | 5/9 | axe0 | ram[npc,axe0,9/5,5/9,killed] |
| 22 | 1785608784087 | 1785608812214 | 28.127 | player | flee | magpie | magpie | 1/6 | 4/3 | axe0 | magpie[player,axe0,1/6,4/3,npc-fled] |
| 23 | 1785609098133 | 1785609154133 | 56.0 | npc | you-killed-them | banshee | banshee | 8/6 | 6/8 | axe0 | banshee[npc,axe0,8/6,6/8,killed] |
| 24 | 1785609270132 | 1785609306150 | 36.018 | npc | you-killed-them | rat16 | rat16, rat18, rat20, rat19 | 9/8 | 7/10 | axe0 | rat16[npc,axe0,2/2,2/2,killed]; rat18[npc,axe0,2/2,2/2,killed]; rat20[npc,axe0,3/3,1/5,killed]; rat19[npc,axe0,2/1,2/1,killed] |
| 25 | 1785609319563 | 1785609322266 | 2.703 | player | you-killed-them | rat21 | rat21 | 2/0 | 1/0 | axe0 | rat21[player,axe0,2/0,1/0,killed] |
| 26 | 1785609553445 | 1785609564306 | 10.861 | player | you-killed-them | zombie7 | zombie7 | 5/0 | 1/3 | axe0 | zombie7[player,axe0,5/0,1/3,killed] |
| 27 | 1785609607642 | 1785609675460 | 67.818 | player | flee | thief | thief | 6/14 | 12/8 | axe0 | thief[player,axe0,6/14,12/8,you-fled] |
| 28 | 1785609878218 | 1785609904146 | 25.928 | player | flee | thief | thief | 5/4 | 7/2 | falchion | thief[player,falchion,5/4,7/2,npc-fled] |
| 29 | 1785609907922 | 1785609908285 | 0.363 | player | flee | thief | thief | 0/1 | 1/0 | falchion | thief[player,falchion,0/1,1/0,npc-fled] |
| 30 | 1785609912935 | 1785609914140 | 1.205 | player | you-killed-them | thief | thief | 1/0 | 0/0 | falchion | thief[player,falchion,1/0,0/0,killed] |
| 31 | 1785610066196 | 1785610102136 | 35.94 | npc | you-killed-them | rat17 | rat17 | 4/6 | 6/4 | falchion | rat17[npc,falchion,4/6,6/4,killed] |
| 32 | 1785610164767 | 1785610196137 | 31.37 | player | flee | zombie6 | zombie6 | 5/2 | 2/5 | dagger0 | zombie6[player,dagger0,5/2,2/5,npc-fled] |
| 33 | 1785610201718 | 1785610224135 | 22.417 | player | withdraw | zombie1 | zombie1, zombie6 | 7/2 | 0/8 | dagger0 | zombie1[player,dagger0,6/0,0/6,withdrawn]; zombie6[player,dagger0,1/2,0/2,killed] |
| 34 | 1785610234136 | 1785610236141 | 2.005 | npc | you-killed-them | zombie1 | zombie1 | 1/0 | 0/1 | dagger0 | zombie1[npc,dagger0,1/0,0/1,killed] |
| 35 | 1785610285432 | 1785610312139 | 26.707 | player | you-killed-them | zombie0 | zombie0 | 7/1 | 3/4 | falchion | zombie0[player,falchion,7/1,3/4,killed] |
| 36 | 1785610320451 | 1785610366138 | 45.687 | player | you-killed-them | zombie5 | zombie5 | 5/7 | 1/11 | falchion | zombie5[player,dagger0,5/7,1/11,killed] |
| 37 | 1785610374531 | 1785610376137 | 1.606 | player | you-killed-them | firefly0 | firefly0 | 1/0 | 0/0 | dagger0 | firefly0[player,dagger0,1/0,0/0,killed] |
| 38 | 1785610708075 | 1785610714140 | 6.065 | player | flee | viper | viper | 2/0 | 0/0 | dagger0 | viper[player,dagger0,2/0,0/0,npc-fled] |
| 39 | 1785610718435 | 1785610720151 | 1.716 | player | flee | viper | viper | 0/1 | 0/0 | dagger0 | viper[player,dagger0,0/1,0/0,npc-fled] |
| 40 | 1785610747437 | 1785610748140 | 0.703 | player | you-killed-them | viper | viper | 1/0 | 0/0 | dagger0 | viper[player,dagger0,1/0,0/0,killed] |
| 41 | 1785610854942 | 1785610876141 | 21.199 | player | you-killed-them | dwarf21 | dwarf21 | 4/1 | 1/3 | dagger0 | dwarf21[player,dagger0,4/1,1/3,killed] |
| 42 | 1785610910334 | 1785610912148 | 1.814 | player | you-killed-them | billy goat | billy goat | 1/0 | 0/0 | dagger0 | billy goat[player,dagger0,1/0,0/0,killed] |
| 43 | 1785611033557 | 1785611034159 | 0.602 | player | flee | canary | canary | 0/1 | 1/0 | dagger0 | canary[player,dagger0,0/1,1/0,npc-fled] |
| 44 | 1785611037536 | 1785611038140 | 0.604 | player | flee | canary | canary | 0/1 | 1/0 | dagger0 | canary[player,dagger0,0/1,1/0,npc-fled] |
| 45 | 1785611040775 | 1785611042145 | 1.37 | player | flee | canary | canary | 1/0 | 0/1 | dagger0 | canary[player,dagger0,1/0,0/1,npc-fled] |
| 46 | 1785611044782 | 1785611046143 | 1.361 | player | flee | canary | canary | 1/0 | 0/1 | dagger0 | canary[player,dagger0,1/0,0/1,npc-fled] |
| 47 | 1785611048774 | 1785611050139 | 1.365 | player | flee | canary | canary | 0/1 | 0/1 | dagger0 | canary[player,dagger0,0/1,0/1,npc-fled] |
| 48 | 1785611053974 | 1785611054178 | 0.204 | player | flee | canary | canary | 0/1 | 1/0 | falchion | canary[player,falchion,0/1,1/0,npc-fled] |
| 49 | 1785611057264 | 1785611058146 | 0.882 | player | flee | canary | canary | 0/1 | 1/0 | falchion | canary[player,falchion,0/1,1/0,npc-fled] |
| 50 | 1785611060915 | 1785611062146 | 1.231 | player | you-killed-them | canary | canary | 1/0 | 0/0 | falchion | canary[player,falchion,1/0,0/0,killed] |
| 51 | 1785611283512 | 1785611411223 | 127.711 | player | flee | large rat0 | large rat0, rat12, rat14, rat15, rat5, rat7, rat2, rat9, rat8, rat6, rat10, rat11, rat1, rat4, rat3, rat13 | 49/33 | 36/46 | dagger0 | large rat0[player,dagger0,7/6,2/10,killed]; rat12[npc,dagger0,2/3,2/3,killed]; rat14[npc,dagger0,2/0,1/1,killed]; rat15[npc,dagger0,3/3,1/5,killed]; rat5[npc,dagger0,3/0,1/2,killed]; rat7[npc,dagger0,5/2,4/3,killed]; rat2[npc,dagger0,2/1,2/1,killed]; rat9[npc,dagger0,3/2,2/3,killed]; rat8[npc,dagger0,3/0,1/2,killed]; rat6[npc,dagger0,2/2,3/1,killed]; rat10[npc,dagger0,3/3,3/3,killed]; rat11[npc,dagger0,3/1,0/4,killed]; rat1[npc,dagger0,3/3,4/2,killed]; rat4[npc,dagger0,4/4,4/4,killed]; rat3[npc,dagger0,1/2,3/0,you-fled]; rat13[npc,dagger0,3/1,3/2,you-fled] |
| 52 | 1785611588330 | 1785611608150 | 19.82 | player | you-killed-them | rat3 | rat3 | 3/2 | 1/3 | falchion | rat3[player,falchion,3/2,1/3,killed] |
| 53 | 1785611734866 | 1785611760176 | 25.31 | player | you-killed-them | rat13 | rat13 | 4/4 | 3/4 | falchion | rat13[player,falchion,4/4,3/4,killed] |
| 54 | 1785612698338 | 1785612716149 | 17.811 | player | you-killed-them | zombie2 | zombie2 | 6/3 | 1/4 | falchion | zombie2[player,falchion,6/3,1/4,killed] |
| 55 | 1785612782148 | 1785612851412 | 69.264 | npc | flee | wolf | wolf | 7/8 | 10/5 | falchion | wolf[npc,falchion,7/8,10/5,you-fled] |
| 56 | 1785613259887 | 1785613286157 | 26.27 | player | you-killed-them | parrot | parrot | 3/5 | 5/2 | broadsword | parrot[player,broadsword,3/5,5/2,killed] |
| 57 | 1785613988289 | 1785613990157 | 1.868 | player | you-killed-them | mouse | mouse | 2/0 | 0/1 | broadsword | mouse[player,broadsword,2/0,0/1,killed] |
| 58 | 1785614073398 | 1785614196159 | 122.761 | player | you-killed-them | starfish | starfish | 1/0 | 1/0 | broadsword | starfish[player,broadsword,1/0,1/0,killed] |

## Mid-log reset

- Raw event seq `16158`, timestamp `1785614674161`, code `06.04`: `Auto-reset initiated, you have 120 seconds to finish up. No further warnings will be issued!`
- No encounter was open at that timestamp, so the reducer records the reset boundary but does not have to force-close a live encounter in this capture.

## Edge cases and confidence

- High confidence: bare `08.00` has two real start shapes here. `You attack the X, using the Y as a weapon.` is player-initiated; `The X is ...` is NPC-initiated aggro/join. Encounter 21 (`ram`) and encounter 23 (`banshee`) are clean NPC-initiated examples.
- High confidence: encounter 33 has two fights, not one. `zombie6` is killed during the encounter, while `zombie1` ends with the only explicit withdraw acceptance line: `The zombie1 withdraws from your fight, and so do you.`
- High confidence: the three literal player-flee lines map to four fight-level `you-fled` outcomes. Encounters 27 and 55 each close one fight, but encounter 51 ends on one `You have fled by going out.` line while two open rat fights (`rat3` and `rat13`) both resolve as `you-fled`.
- High confidence: the zombie5/firefly0 span contains a voluntary weapon switch. The reducer now records both `You drop your guard as you switch from using the falchion to the dagger0.` and the following `You are now using the dagger0 to fight!` inside encounter 36.
- High confidence: the long rat-swarm encounter includes a real mid-fight weapon break. The reducer records `The dagger0 breaks to bits.` plus the follow-on confusion guard drop inside encounter 51.
- High confidence: the starfish encounter includes a same-weapon retarget guard drop. The reducer records `You're using the broadsword anyway...` plus `Your guard drops momentarily in your confusion.` as a `dropped-guard` event even though no literal `08.06` tag exists.
- Medium confidence caveat: `weapon_used` is known for every persisted fight (`unknown/null` count = 0), but that knowledge can come from the attack line itself rather than an earlier in-capture `use X` command. The opening `zombie9` fight starts mid-capture already attacking with `axe0`, so equip provenance is not always visible.
- No `pass/unresolved` fights remained in this capture after replaying the full log.

## Ancillary-state recovery in this capture

- FES (`12.08.01`) is abundant: 3044 snapshots. This gives stamina, strength, dexterity, magic, score, reset timer, and weather codes; the reducer uses the stamina values to approximate health lost.
- FEI (`12.08.03`) is abundant: 2314 snapshots. It captures room-side and carried item lists, and sometimes explicit `weapon in use:` hints, but not on every fight boundary.
- Room context is good: 1906 snapshots with room short text, room long text, exits, and ambient `20.xx` sound codes when present.
- Status effects are sparse: 1 observed effect event and 1 open window. The only captured effect is timestamp `1785608874764`: `You have suddenly and magically started glowing!`
- Lighting is only best-effort via room prose; there is no dedicated light-state code surfaced in this capture.

## Not observed as literal protocol tags in this capture

- `08.05` / `08.06` as wrapped tags. Their combat behavior still occurs, but only as plain prose in this log and is now reduced that way.
- `08.09` they-killed-you
- `08.13` persona-not-updated
- `06.03` wiz-reset
- `06.12.00` / `06.12.01` fighting disallowed / allowed again
- `05.00.10` / `05.01.10` seen-fleeing notices
