## How it sounds

A polyphonic analog-style synth: warm saws and pulses through a resonant ladder filter (24 or 12 dB, self-oscillating at the top) or a clean state-variable filter. Detuned unison and per-voice drift make it wide and alive. The oscillators are band-limited (a saw at 1.76 kHz has its alias floor at −101 dB), so bright leads and sync sweeps stay clean. Good for pads, basses, leads, plucks, brass, keys and synthesized drums.

## Signal flow

- Oscillators 1–3: `O1 shape` (and O2, O3) morphs 0 sine → 1 triangle → 2 saw → 3 pulse (`O1 PW`; 50 % is a square). Oscillator 2 can hard-sync to 1 (`O2 sync`) and take linear FM from it (`FM 1 → 2`). Oscillator 3 defaults to a sine one octave down. Plus ring modulation of 1 × 2 and noise (white → pink).
- Filter 1: Ladder 24, Ladder 12, SVF LP, BP, HP or Notch, with drive, envelope amount (±6 octaves), key tracking and velocity.
- Filter 2: SVF (Off, LP, BP, HP, Notch), after filter 1 (Serial) or beside it (Parallel).
- Amp envelope → output with unison spread and pan.
- Modulation: filter envelope (feeds `F1 env` and `F2 env`), mod envelope (matrix only), two LFOs (7 shapes, tempo sync, fade-in, per-voice or free), an 8-slot matrix.
- 16 notes; each note can have up to 7 unison voices; 32 voices in all.

## How to use it

- Pads: two detuned saws (`O2 level` 60–90 %, `O2 fine` +5 to +9 ct), `Unison` 2–4, amp attack 0.3–1.5 s, release 1–3 s, cutoff 1–3 kHz, a slow LFO on the cutoff.
- Basses: `Voice mode` Mono or Legato; `O3 level` for sub weight; Ladder 24 with filter envelope +30 to +60 %, short filter decay; `Unison` 1 so the lows stay mono.
- Leads: Legato with `Glide mode` Legato and 40–80 ms glide; vibrato from the matrix (below).
- Plucks: amp sustain 0 %, decay 150–600 ms; filter envelope +40 to +70 % with decay 100–300 ms.
- Filter choice: Ladder 24 is fat and loses some low end as resonance rises; Ladder 12 is brighter; SVF is clean and exact (HP for hats, BP for claps and noise sweeps, Notch for phasey pads).
- Velocity: `Vel → amp` for loudness, `F1 velocity` for brightness; the matrix's Velocity source for anything else.
- Matrix amounts at 100 %: pitch ±24 semitones, cutoff ±6 octaves, FM ±4, PW ±48 points, shape ±3, levels and resonance ±100 %, LFO rate ±4 octaves.

Vibrato that fades in:

| Parameter | Value | Why |
|---|---|---|
| LFO1 shape | Sine | |
| LFO1 rate | 5.5 Hz | |
| LFO1 fade | 500 ms | comes in after the attack |
| M1 source | LFO 1 | |
| M1 dest | Pitch | |
| M1 amount | +1% | ±24 cents |

## Gotchas

- Matrix slots 1–4 come with sources set (LFO 1, Mod wheel, Mod env, LFO 2) and dest Off: set a dest and an amount to use them.
- Notes × unison voices must fit 32: with `Unison` 7, only 4 notes ring at once.
- `Volume` starts at −6 dB; unison and chords add up (7 voices about +8.5 dB). Check peaks in chords.
- A ladder at resonance near 100 % self-oscillates: with `F1 key track` 100 % it plays a sine at the note.
- `Drift` (15 % by default) adds random pitch and cutoff movement per voice: set 0% for drums, sub basses and exact A/B renders.
- Transport stop releases all voices; changing `Voice mode` releases held notes.

## Preset map

| Preset | What | Tweak first |
|---|---|---|
| "Pad — Warm Strings" | two detuned saws, unison 3, slow bloom | `F1 cutoff`, Amp attack |
| "Pad — Glass" | triangle and sine an octave apart, bright SVF | `F1 cutoff`, release |
| "Pad — Dark Choir" | PWM pulse and saw, ladder 12 with a parallel band-pass | `F2 cutoff` (the formant) |
| "Pad — Evolving PWM" | two pulses with slow PW LFOs | LFO rates |
| "Bass — Classic Sub" | saw plus sine sub, ladder with envelope | `F1 cutoff`, `F1 env` |
| "Bass — Acid" | legato saw, ladder at 80 % resonance, drive | `F1 cutoff`, `F1 velocity` |
| "Bass — Reese" | two detuned saws, slow LFO on the cutoff | `O2 fine`, `Uni detune` |
| "Bass — FM Growl" | through-zero FM with a mod-envelope sweep | `FM 1 → 2`, `M3 amount` |
| "Lead — Sync Scream" | synced saw swept by the mod envelope | `M3 amount`, `O2 semi` |
| "Lead — Smooth Saw" | legato saws with delayed vibrato | `Glide`, `F1 cutoff` |
| "Lead — Square Chip" | plain square, fast envelope | `O1 PW` |
| "Pluck — Bright" | saw and pulse with a snappy filter | `Flt decay`, `Amp decay` |
| "Pluck — Marimba" | sine with a triangle overtone | `Amp decay` |
| "Keys — Poly Brass" | two saws, slow filter attack | `Flt attack`, `F1 env` |
| "Keys — Electric Piano" | sine FM with a decaying index | `FM 1 → 2`, `Mod decay` |
| "Keys — Organ" | three sines at 8′, 4′ and 2⅔′ | `O3 level` |
| "FX — Noise Sweep" | band-passed noise swept by a slow LFO | automate `F1 cutoff` instead |
| "FX — Laser Drop" | saw with a fast pitch drop | `Mod decay` |
