## How it sounds

A sampler and drum machine that plays the song's waves: the wavetable slots `wave_import` fills, which NeoBuzz Live saves with the song, so a song carries its samples. It also has 26 built-in samples made by code (a drum kit, looped single waves and a few instruments), so it sounds without any file. Sixteen zones each map a wave to a range of keys and velocities, with its own root, tuning, level, pan, start and end, loop, trigger, reverse and choke group. In `Kit` mode zone n plays key `Kit base` + n − 1 (from C2, the General MIDI kick, by default) at the wave's own pitch, one-shot: a drum machine.

Measured: pitch within 0.001 cent from A0 to C8 at 44.1 and 48 kHz; a wave at its root and the song's rate plays as it is; pitched from an octave down to four up, a full-band saw's aliases stay under −90 dB (the 32-tap windowed sinc follows the pitch, and octave-down copies serve the high notes). Chokes fade in 5 ms, steals in 3 ms, a region's end in 2 ms, and a note starts over at least 0.5 ms, so none of them clicks. A wave's own loop has no seam at any pitch; a zone's own loop points crossfade (equal power) over `Zn crossfade`.

Built-in samples (a zone's wave 201 on):

| Wave | Sample |
|---|---|
| 201–215 | Kick, Kick 808, Snare, Clap, Rim, Closed hat, Open hat, Low tom, Mid tom, High tom, Crash, Ride, Cowbell, Shaker, Clave (root C4) |
| 216, 217, 218 | Sine (A4, looped), Saw and Square (C3, looped, every harmonic to 21.6 kHz) |
| 219, 220 | E-piano soft and hard (layers at C2, C4 and C6: a velocity pair) |
| 221 | Strings (stereo, layers at C3 and C5, looped) |
| 222, 223, 224 | Bass (C2), Bell (C5), Pluck (C4) |
| 225, 226 | Vox "aah" (C4, looped), Noise (looped) |

## Signal flow

A note → its zones (Instrument mode: every zone whose key and velocity ranges hold it; Kit mode: the zone on its key) → each zone's wave slot → a layer of the slot (the one whose root is the highest at or below the note; layers sharing a root alternate, round robin) → read at the pitch (the note against the root, `Zn tune`, `Zn fine`, pitch bend) → the region (`Zn start` to `Zn end`, backwards with `Zn reverse`) and the loop → the filter (`Filter`, `Cutoff`, `Resonance`; the filter envelope, key tracking and velocity move it) → the amp envelope, times `Zn level`, the velocity (`Vel → amp`) and `Zn pan` → `Volume`. Each zone a note plays takes a voice; 32 at most (`Polyphony`).

## How to use it

A drum kit from your own samples:

1. Import the hits into slots: `wave_import(path="C:/Samples/kick.wav", slot=1, name="Kick")`, the snare into slot 3, the clap into 4, the closed hat into 7, the open hat into 11 (General MIDI's keys: zone n is key 35 + n, so slot n on zone n lands on its GM note).
2. `machine_add(gear="Neo Sampler", name="Drums")`, then `preset_load(machine="Drums", preset="Kit — Song Waves 1–16 (Hats in Choke 1)")`: zones 1–16 play slots 1–16 on C2 … D#3, and the hats (zones 7, 9, 11) choke each other.
3. Write the beat: `notes_write(machine="Drums", pattern="Beat", notes=[{"pitch": "C2", "start": 0, "length": 0.25, "velocity": 110}, …])`: C2 kick, D2 snare, D#2 clap, F#2 closed hat, A#2 open hat.
4. Shape a hit: its zone's `Zn level`, `Zn pan`, `Zn tune`, `Zn start` (skip a slow attack), `Zn end` (shorten it: it fades out over 2 ms before the end).

No files at hand: `preset_load(machine="Drums", preset="Kit — Electro (GM Layout)")` plays the built-in kit on the same keys.

A sampled instrument:

1. Import each sample with its root: `wave_import(path="C:/Samples/piano C3.wav", slot=1, root_note="C3")`, another at C4 into slot 2. Without `root_note`, a wave plays unchanged at C3 (NeoBuzz Live's default root).
2. Zone 1 slot 1 across the low keys, zone 2 slot 2 above: `param_set(machine="Piano", changes=[{"param": "Z1 key high", "value": "B3"}, {"param": "Z2 wave", "value": 2}, {"param": "Z2 key low", "value": "C4"}])`.
3. Several samples of one note at different strengths are velocity layers: zones on the same keys with `Zn vel low` and `Zn vel high` (`Vel crossfade` blends them), or the preset "Instrument — Song Waves 1–4 (Velocity Layers)".
4. Sustain: a sample with a loop of its own loops as the wavetable says (`Zn loop` Wave); otherwise set `Zn loop` Forward with `Zn loop start` and `Zn loop end` (in % of the wave) and a `Zn crossfade` of 10–100 ms.
5. `wave_import(…, layer=true)` adds a file as another layer of a slot (with its own root): layers split a zone's keys by root, and layers of one root alternate (round robin: several snare hits in one slot).

A song with Pedal M1 moves over with `machine_replace(machine="Keys")`: its oscillators' waves and key zones become zones 1–4 and 5–8 (the same waves, at the same pitch), its envelopes, low-pass and levels carry over, and its notes become Neo Sampler's. Its LFOs and portamento have no counterpart, and its resonance comes out a little different (the reply says how much).

Check a kit or an instrument: render it (`render(probes=["Drums"])`) and `analyze` its loudness and spectrum; `wave_list()` shows each slot's root and loop.

Kick, snare and hats from the built-ins:

| Parameter | Value | Why |
|---|---|---|
| Mode | Kit | zone n on key Kit base + n − 1 |
| Kit base | C2 | General MIDI: C2 kick, D2 snare, F#2 closed hat, A#2 open hat |
| Z1 wave | 201 | Built-in Kick |
| Z3 wave | 203 | Built-in Snare |
| Z7 wave | 206 | Built-in Closed hat |
| Z7 choke | 1 | a closed hat cuts an open one |
| Z11 wave | 207 | Built-in Open hat |
| Z11 choke | 1 | |
| Volume | -7.0 dB | kick, snare and hat together stay under −3 dBFS |

A looped pad from the built-in strings:

| Parameter | Value | Why |
|---|---|---|
| Z1 wave | 221 | Built-in Strings: layers at C3 and C5, a seamless loop of their own |
| Amp attack | 350 ms | a slow swell |
| Amp release | 1.20 s | |
| Filter | LP 12 | |
| Cutoff | 7.00 kHz | softer highs |
| Volume | -8.0 dB | |

## Gotchas

- A zone plays nothing while its slot is empty (or its wave is 0, Off): import a wave or pick another; a slot a playing note uses can be replaced, and the note goes on with the old wave.
- Renders wait for the song's waves to be read in (and the built-ins to be made), so a render right after `wave_import` hears it.
- Without `root_note` a wave's root is C3 (NeoBuzz Live's default): played at C4 it sounds an octave up. Kit mode ignores roots.
- Kit mode ignores the zones' key and velocity ranges, roots, loops and triggers: one zone per key, one-shot. For velocity-layered drums use Instrument mode with one-key zones and `Zn trigger` One-shot.
- `Zn root` 128 (it reads "Wave") takes each layer's own root; another value overrides every layer's.
- A zone loop's crossfade reads the samples before `Zn loop start` (after `Zn loop end` reversed): a loop that starts at the wave's first sample has no crossfade.
- One-shot ignores note-offs and loops: the wave plays to `Zn end`. The amp envelope still shapes it (`Amp sustain` 0 and a short `Amp decay` shorten every hit).
- `Polyphony` counts voices, not notes: a note that two zones layer takes two.

## Preset map

| Preset | What |
|---|---|
| "Kit — Electro (GM Layout)" | the built-in kit on the General MIDI keys from C2, hats choked |
| "Kit — 808 Style (Long Kick, Cowbell)" | the long 808 kick, tuned down, cowbell and clave |
| "Kit — Tight and Dry (Short Decays)" | every hit cut short by the amp envelope |
| "Kit — Lo-Fi (Pitched Down, Low-Pass 3.5 kHz)" | the kit a few semitones down, through a low-pass |
| "Kit — Percussion (Cowbell, Shaker, Clave)" | hand percussion on the drum keys |
| "Kit — Song Waves 1–16 (Hats in Choke 1)" | the template for your own kit: zone n plays slot n |
| "Instrument — Song Waves 1–4 (Velocity Layers)" | slots 1–4 as four velocity layers, crossfaded |
| "Instrument — Song Wave 1, One-Shot Across the Keys" | slot 1 pitched across the keyboard (root C4), one-shot |
| "Keys — Electric Piano (Two Velocity Layers)" | the built-in tine piano, soft and hard layers |
| "Pad — Strings (Looped, Slow Attack)" | the built-in strings, a slow swell |
| "Pad — Vox Aah (Looped)" | the built-in vowel pad |
| "Bass — Sub Pluck" | the built-in bass through a ladder with an envelope |
| "Bell — Glass (Long Release)" | the built-in bell, one-shot |
| "Pluck — Nylon" | the built-in plucked string |
| "Lead — Saw (Filter Envelope)" | the looped saw through a resonant ladder |
| "FX — Reversed Strings Swell (One-Shot)" | the strings backwards |
| "Texture — Noise Wash (Band-Pass Sweep)" | looped noise through a slowly sweeping band-pass |
