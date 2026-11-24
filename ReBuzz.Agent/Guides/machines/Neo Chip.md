## How it sounds

A four-channel sound-chip instrument: three pulse channels with selectable duty cycles (12.5, 25, 50 and 75 %) plus continuous pulse-width modulation, and a linear-feedback shift-register noise channel with a 15-bit long mode for crisp percussion and a 7-bit short mode for pitched metallic buzz. Pulse edges are band-limited by default for clean high notes, with a `Raw` switch that keeps the stepped, aliased edges in on purpose, and a `Lo-res DAC` switch that quantises each channel to 4-bit resolution before linear mixing.

- Tracks 1–3 drive pulse channels 1–3 directly and track 4 drives the noise channel. From MIDI or the piano roll, `Voice mode` routes notes across the three pulse channels (`Poly` round-robin with oldest-note stealing, `Mono` last-note, or `Unison` detuned by `Detune`), while `Noise MIDI ch` (default `10`, or `All` for noise-only kits) routes MIDI notes to noise.
- The volume envelope runs in 16 discrete 4-bit steps (0–15), either as a stepped ADSR or in five hardware-style repeating and one-shot shapes (`Saw down`, `Saw up`, `Triangle`, `Decay shot`, `Attack hold`) with a period in milliseconds or tempo-synced.
- Per-track motion adds a 1–3 step semitone arpeggiator (free Hz or tempo-synced 1/16 to 1/64, straight or triplet), linear `Pitch sweep` in semitones per second, and delayed pitch vibrato.
- Every parameter has a How and a Sound sentence in `machine_describe`; `param_set` takes the parameter name or id.

## Use it for

- Crisp square and thin-pulse leads, punchy pulse basses, rapid chord arpeggios, detuned unison pads, retro noise hats and snares, bell overtones from fast hardware envelopes, and laser or coin sound effects.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| P1 duty | 50% | 50% is hollow and full; 25% and 12.5% sound progressively thinner and more nasal |
| Voice mode | Poly | Poly plays three-note chords; Mono tightens leads/basses; Unison stacks all three pulses |
| Raw | Off | Off band-limits pulse edges cleanly; On keeps raw aliased edges for extra sparkle |
| Env mode | ADSR | Stepped 16-level ADSR, or repeating/one-shot hardware shapes for bells and drums |
| Arp mode | Off | Up, Down, Up-Down or As written cycles `Arp step 1` to `Arp step 3` per track |
| Pitch sweep | 0.0 st/s | Negative sweeps make laser drops and snare punch; positive sweeps rise |
| Noise mode | Long | Long (15-bit) is white noise for snares; Short (7-bit) is pitched metallic buzz |
| Lo-res DAC | Off | Quantises each channel to 4-bit steps before linear mixing for authentic grain |

## Starting points

- Leads and bass: "Lead — Square hero", "Lead — Thin 12 %", "Bass — Pulse bass".
- Arpeggios and pads: "Arp — Major sparkle", "Arp — Minor run", "Pad — Unison detune".
- Percussion and FX: "Drums — Noise hat", "Drums — Noise snare", "FX — Laser sweep", "FX — Coin".
- Hardware envelope and grit: "Bell — Hardware envelope", "Raw — Lo-res grit".
- Quick rapid major chord arpeggio: `Arp mode` Up, `Arp steps` 3, `Arp step 1` 0 st, `Arp step 2` +4 st, `Arp step 3` +7 st, `Arp sync` 1/32.

## Pitfalls

- `Drums — Noise hat` and `Drums — Noise snare` set `Noise MIDI ch` to `All` so keyboard or piano-roll notes trigger noise immediately; set `Noise MIDI ch` back to `10` when switching manually to melodic pulse work on MIDI channel 1.
- Fast repeating hardware envelopes (`Env mode` Triangle with `Env period` under 30 ms) create audio-rate amplitude modulation sidebands—ideal for bells, less suited to smooth pads.
- `Raw` On and `Lo-res DAC` On intentionally introduce aliasing and 4-bit quantisation noise; turn both Off when you want clean band-limited pulse tones.
- In `Poly` mode there are three pulse channels, so a fourth simultaneous MIDI note steals the oldest held channel.

## Pairs well with

- Neo Delay and Neo Verb for stereo space around dry pulse leads, Neo Comp to glue noise drums and pulse basses, Neo EQ to tame high-frequency bite when `Raw` is On, and Neo Ensemble for extra chorus width.

## Checklist

- [ ] `preset_load(machine="Chip", preset="Lead — Square hero")`, then `notes_write` a melody and `render(probes=["Chip"])`.
- [ ] `analyze(source="Chip", metrics=["peaks", "loudness"])`: peaks stay cleanly below 0 dBFS on three-note chords plus noise.
- [ ] `param_set(machine="Chip", changes={"Arp mode": "Up", "Arp sync": "1/32"})` to verify tempo-synced arpeggio steps.
- [ ] `analyze(source="Chip", metrics=["spectrum"])`: compare `Raw` Off vs `Raw` On to confirm band-limited vs aliased high harmonics.
