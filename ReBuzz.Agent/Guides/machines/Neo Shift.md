## How it sounds

Three kinds of shift in one effect for a track, chosen by `Mode`:

- `Pitch` moves every partial by the same ratio, so harmonics stay in tune: `Pitch` (-24 to +24 semitones) plus `Fine` (+-100 cents). `Quality` Fast is a two-tap delay line (latency half of `Window`, 10-100 ms: short windows are tight and a little rough on low notes, long ones smoother); Smooth is a phase vocoder (2047 samples of latency, clean on chords and pads). `Stereo detune` (0-50 cents) puts the left down and the right up for a wide double.
- `Frequency` moves every partial by the same number of hertz (`Shift` +-2000 Hz, `Shift fine` +-5 Hz), so harmonics bend into bells and slow phasing; the image on the other side is more than 60 dB down. `Feedback` (+-95 %) repeats the shift: positive rises, negative falls, like an endless barber-pole. `Spread` shifts the right side the other way.
- `Ring` multiplies the sound by a carrier (`Carrier` 1 Hz-5 kHz, `Shape` Sine, Triangle or Square, band-limited). `Depth` 100 % is a ring modulator (sum and difference tones, the original gone); 0 % is amplitude modulation (a tremolo).

`Follow notes` (default off) lets notes steer it: in Pitch mode the note minus `Root` (C4) is the shift, in Ring mode the note's frequency is the carrier. `Low cut`/`High cut` filter the wet path, `Output` sets its level. `Mix` blends the dry (delayed by the latency, which depends on `Quality`, not on `Mode`); `Amount` (0-200 %, 100 % is the settings as they stand) scales the shifts, feedback, spread and depth, and 0 % or Mix 0 passes the input bit for bit.

## Use it for

- Octave and fifth doubles, thickening a mono part, barber-pole risers, bell and robot tones, tremolo, and ring modulation tuned to the keys.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Pitch | Frequency and Ring change character, not just pitch |
| Pitch | -12 | an octave down; +7 is a fifth up |
| Quality | Smooth | for chords and pads; Fast for single lines and short latency |
| Window | 30 ms | Fast only: longer for low notes, shorter for tight attacks |
| Shift | 45 Hz | with Feedback 80 % a rising barber-pole |
| Carrier | 523 Hz | a bell; below 20 Hz it is a tremolo |
| Depth | 100 % | 0 % keeps the sound and only modulates its level |
| Mix | 60 % | keep some dry under a harmony |

## Starting points

- "Pitch — Octave Down", "Pitch — Fifth Up", "Pitch — Wide Double".
- "Frequency — Rising Barber Pole", "Frequency — Gentle Wide Shift", "Ring — Metal Bell", "Ring — Slow Throb", "Ring — Note Ring".

## Pitfalls

- A fresh instance is an octave down at Mix 100 %: set `Pitch` first.
- The latency follows `Quality` (Fast: half the `Window`; Smooth: 2047 samples), the same in every mode; read `latency_samples` in the readings. Changing `Quality` or `Mode` while playing crossfades over 30 ms.
- Frequency shifting is not pitch shifting: a chord shifted by Hz is no longer in tune with itself. Use Pitch for harmonies.
- `Follow notes` needs a note source (a note wire, live MIDI or the track column); without a note the Pitch or Carrier knobs apply.
- Ring mode silences the original at `Depth` 100 %: with no sound in there is none out.

## Pairs well with

- Neo Delay or Neo Verb after a barber-pole; Neo Filter after a ring modulator; a note source into the note wire to play it.

## Checklist

- [ ] `machine_describe(machine="Shift")`: set `Mode` first, then that mode's controls.
- [ ] Read the latency (`machine_readings`: latency_samples) and `render` a bypassed copy against the processed one.
- [ ] `analyze(source="Shift", metrics=["spectrum"])`: Pitch moves the peaks by the ratio, Frequency by the hertz, Ring shows lines at f +- carrier.
- [ ] `compare` with `Amount` at 50 % and 100 % to find how far to push it.
