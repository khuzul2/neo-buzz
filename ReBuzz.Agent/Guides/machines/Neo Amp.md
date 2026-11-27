## How it sounds

A guitar or bass amp for a DI signal: noise gate, preamp, tone stack, power stage, cabinet and a tuner in one machine. Input is mono (the `Input` choice takes Left, Right or both summed) and the amp, cabinet and both outputs carry the same signal. Eight voicings (`Voicing`) set how many gain stages (1-4) the sound goes through and how they are shaped; the Bass-Mid-Treble knobs are a passive tone stack worked out from its resistors and capacitors, so the knobs interact like the real thing and the stack always costs some level (made up inside). Oversampling (`Quality` 2x, 4x, 8x) keeps the distortion of high notes free of aliasing. Changing `Voicing`, `Quality` or `Cabinet` while the song plays crossfades over 30 ms: no click.

| Voicing | Character | Use it for |
|---|---|---|
| Clean, Bright clean | open, glassy; the bright one sparkles at low Gain | chords, arpeggios, jazz |
| Crunch | breaks up when played hard | rhythm, blues |
| Classic rock | full, rough, medium gain | power chords, riffs |
| Lead | smooth, long sustain, singing | solos |
| High gain | tight, heavy | palm-muted rhythm |
| Bass clean, Bass drive | round and deep; growl with grit | bass guitar |

## Use it for

- A DI guitar or bass into a played-through-an-amp sound; `Cabinet` Off for the raw amp into a cabinet or impulse response of your own; the tuner to tune a track.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Voicing | Classic rock | pick by gain; the knobs then fine-tune |
| Gain | 20 dB | how hard the later stages are driven: 0-40 dB |
| Bass, Mid, Treble | 50 % | the tone stack; a low Mid scoops, a high one pushes forward |
| Sag | 30 % | power-supply droop: a slow compression that makes chords bloom |
| Presence, Resonance | 40 %, 30 % | highs and lows of the power stage |
| Master | 60 % | how hard the power stage is driven; its level is made up |
| Cabinet | Closed 4x12 | six made by code; Wave uses the song's wave in slot `Wave` as the cabinet |
| Mic position | 30 % | 0 is the centre of the cone (bright), 100 % the edge (dark) |
| Gate | -60 dB | opens at the level, closes 6 dB lower; the lowest setting is off |
| Trim | 0 dB | the level into the amp; the editor's meter shows it |
| Tuner | Off | On mutes the output and reads the note and cents (the editor shows them) |

`Amount` (default 100 %) scales the drive: 0 % is the bypass (bit for bit the input, delayed by the latency), 200 % doubles the Gain in dB and `Master`. `Mix` blends the dry input in, delayed to match. `Output` sets the level in dB.

## Starting points

- "Clean — Glassy", "Clean — Warm Jazz", "Crunch — Edge of Breakup", "Crunch — Classic Rock", "Lead — Singing Sustain", "High Gain — Tight Rhythm", "Bass — Round Clean", "Bass — Growl".

## Pitfalls

- A DI that is too hot overdrives the first stage: lower `Trim`, not `Gain`, when a clean tone breaks up too soon.
- With `Cabinet` on Wave the song saves the wave, not the machine: the slot must hold a short impulse response (up to 250 ms is used). An empty slot plays the closed 4x12 and the editor says so.
- High `Gain` raises hiss: use `Gate` (about -60 dB) and a `Gate release` of 60-150 ms for tight rhythm.
- `Quality` 2x is the cheapest and aliases most on high notes at the highest gain; 4x is the default; 8x is for solo notes at full gain.
- The tuner mutes the sound: switch it off again before you play the part.
- The latency (what the host compensates) is the oversampler's, 62 samples, at every quality.

## Pairs well with

- Neo EQ after it for a final tilt; Neo Verb or Neo Space after it for a room; Neo Delay before the reverb; Neo Comp before it for a steadier DI.

## Checklist

- [ ] `param_set(machine="Amp", changes=[{"param": "Voicing", "value": "Crunch"}, {"param": "Gain", "value": 18}])`, then `render(probes=["Amp"])`: the render waits for the cabinet.
- [ ] `machine_readings(machine="Amp")` gives `rms_db` and `peak_db`: keep the peak under 0 dBFS with `Output`.
- [ ] With `Cabinet` Wave: `machine_describe(machine="Amp")` shows the `Wave` slot; it must hold a wave, or the closed 4x12 plays instead.
