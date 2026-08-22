# Sound Effects

Files prefixed **`clio.`** are taken from the **Clio MUD2 Client** by **Ian Peattie**. See
[LICENSE](LICENSE) for the licence terms (MIT-style). The prefix exists to identify their origin, so
anything WITHOUT it is not Clio's and is not covered by that licence - see "Mucka's own sounds" below.

## Mucka's own sounds

| File | Origin | Used for |
|------|--------|----------|
| `Perc_Stick_hi.wav` | percussion sample | Combat metronome, pre-tick click |
| `Perc_Stick_lo.wav` | percussion sample | Combat metronome, after-tick click |
| `mucka.flee_failed.wav` | **generated** - `tools/sounds/make_flee_failed.py` | Your flee failed |

`mucka.flee_failed.wav` is synthesized rather than sourced, and the script is its provenance: re-run
it and you get the identical file, with the parameters in it serving as the sound's actual definition
rather than a description of an opaque binary. A buzzer lifted from a TV show would have neither
Clio's licence nor any other.

The brief was a **single note**, buzzer-like - "NRRRK" / "UNNNK" / "BZZT" - and explicitly not an
electrical zap. So it is one oscillator at one pitch in one burst: 150 Hz, 280 ms, odd harmonics
stopped at the 13th. That ceiling is what keeps it a buzz; a raw square runs to Nyquist and the
broadband hiss up there is exactly the zap character to avoid. An earlier version had a pitch bend, a
detuned second voice and a two-burst gate, and was rejected as too complex - a buzzer is not a
composition.

It fires on `You have fled by trying to go <dir>.`, the worst-value outcome MUD2 offers: the points
are charged, the persona can lose an experience level, the weapon drops out of your hands, every fight
you were in ends, and you are still standing in front of whatever you were running from. It is
catalogued under **Client alerts**, so it has a volume slider and an off switch like every other sound,
and unlike the metronome it is NOT gated on the combat rail being visible - hiding a panel is not a
request to stop being warned.

## Standard Sound Effects

Files whose names start with `clio.13` relate to actual sounds heard in the
game, typically announced as _"In the distance you hear..."_

| File | Description |
|------|-------------|
| clio.1301.wav | Ox's "MOO" |
| clio.1302.wav | Swamp exploding on a lit brand |
| clio.1303.wav | Lion's "ROARRRR" |
| clio.1304.wav | Rumble of thunder |
| clio.1305.wav | Crack of thunder |
| clio.1306.wav | Piercing scream |
| clio.1307.wav | Incredibly loud >*B*O*O*M*< |
| clio.1308.wav | Bell tolling when a magic-user dies |
| clio.1309.wav | Bangers' "BANG" |
| clio.1310.wav | Wolf's "AAAOOOOOOHHHH" |
| clio.1311.wav | Dragon's "RHOAAAUUUAURRRRGGGGGGHGHHHGHHHH" |
| clio.1312.wav | Noise made by hitting the bell |
| clio.1313.wav | The >CRACK< of the cannon going off |
| clio.1314.wav | The flute |
| clio.1315.wav | Clear tones of horn |
| clio.1316.wav | Badly-tuned horn |
| clio.1317.wav | The conch |
| clio.1318.wav | Thunderous roar of a FOD |
| clio.1319.wav | Whistling feedback of a failed FOD |
| clio.1320.wav | Incredibly irritating sound of a tin drum |
| clio.1321.wav | Terrifying sound of rock splitting |
| clio.1322.wav | Shrill note of a whistle being blown |
| clio.1323.wav | Wailing sound of a warning siren |
| clio.1324.wav | Champagne bottle exploding |
| clio.1325.wav | Dragon's "HAWUMPH" |
| clio.1326.wav | Mine flooding |
| clio.1327.wav | Mine flushing |

## Extended Sound Effects

| File | Description |
|------|-------------|
| clio.06.wav | Information (reset due, FYI, etc.) |
| clio.070000.wav | Isolated hit |
| clio.070001.wav | Eros hits |
| clio.070002.wav | Hitting an object |
| clio.070100.wav | General bites |
| clio.070101.wav | Rat bites |
| clio.070200.wav | General stings |
| clio.070201.wav | Bee stings |
| clio.070202.wav | Jellyfish stings |
| clio.070203.wav | Electric eel hits |
| clio.0703.wav | Kicks |
| clio.0704.wav | Throws |
| clio.0705.wav | Captures by grizzly, octopus, etc. |
| clio.0706.wav | Ghost hits |
| clio.0801.wav | You hit in a fight |
| clio.0803.wav | Foe hits you in a fight |
| clio.1100.wav | Start of disabling spell |
| clio.1101.wav | End of disabling spell |
| clio.1102.wav | Start of enhance spell |
| clio.1103.wav | End of enhance spell |
| clio.1104.wav | Change spell |
| clio.1105.wav | Detect spell |
| clio.1107.wav | Force spell |
| clio.1108.wav | Ignite spell |
| clio.1110.wav | Repair spell |
| clio.1111.wav | Sleep spell |
| clio.1112.wav | Snoop spell |
| clio.1113.wav | Unsnoop spell |
| clio.1115.wav | Track spell |
| clio.1116.wav | Untrack spell |
| clio.1117.wav | Unsite spell |
| clio.1118.wav | Chance spell |
| clio.1119.wav | Diagnose spell |
| clio.1120.wav | Start of super disabling spell |
| clio.1121.wav | End of super disabling spell |
| clio.140302.wav | Rain on the trees |
| clio.1800.wav | Touchstone success |
| clio.1801.wav | Touchstone failure |
| clio.1802.wav | Pull lever and fall in chute |
| clio.1803.wav | Bump in chute |
| clio.1804.wav | Emerge from chute |
| clio.1806.wav | Land safely at bottom of cliff |
