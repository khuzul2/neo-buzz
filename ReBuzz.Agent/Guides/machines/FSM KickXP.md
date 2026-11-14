## How it sounds

A kick drum synth: a sine whose pitch sweeps from Start down to End, with buzz, click and punch layers and a level envelope. Each track is its own kick; a note sets the pitch the sweep falls to.

## Use it for

- 808 and 909 style kicks, from short thumps to long sub sweeps.
- Low toms.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Start pitch | 150 Hz | 34 Hz to 4.2 kHz |
| End pitch | 45 Hz | 33 to 528 Hz; the note sets the floor |
| Click amount | 10% | 0 to 100 percent |
| Punch amount | 30% | 0 to 100 percent |
| Pitch decay rate | 20 | 1 to 240; low is a slow sweep |
| Amp decay time | 0.6 s | 4 ms to 1 s |

## Starting points

- Presets: "Kick — Deep 808", "Kick — Punchy Techno", "Kick — Hard Clicky", "Tom — Low"; load with `preset_load(machine="Kick", preset="Kick — Deep 808")`.
- A hit: `notes_write` a low note (A1 or below for deep kicks) on the first track.

## Pitfalls

- Its pitch is the lowest the sweep falls to: notes above about A2 give a tuned, higher kick.
- Trigger/volume and NoteDelay are pattern columns, not set with `param_set`.
- Each track is its own kick with its own settings (8 tracks).
- A hit measures about -14 LUFS.

## Pairs well with

- Jeskola Distortion for grit.

## Successor

Neo Drums replaces FSM KickXP: `machine_replace(machine="Kick")` plays its notes and triggers as Neo Drums' kick on C2, fitting its sweep, decay, click and buzz; a few controls (End, the pitch curve, the layer fades) are fitted at their defaults.

## Checklist

- [ ] `analyze(source="Kick", metrics=["loudness"])`: a hit is near -14 LUFS.
- [ ] `param_get(machine="Kick")`: `Amp decay time` is what you set.
- [ ] `render(probes=["Kick"])`: the hit ends in silence.
