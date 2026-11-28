## How it sounds

A channel vocoder with no latency. The main input is the modulator (a voice, a drum loop, anything): its spectrum is measured in 8 to 40 log-spaced bands, and each band's level is imposed on the same band of a carrier. The carrier is `Synth`, the machine's own 8-voice synth, played by notes and chords (a note wire, live MIDI or its own note tracks, so it is wired like an instrument: `note_chain` or `note_wire_connect`); `Noise`, which gives a whisper and needs no notes; or `Sidechain`, any machine wired into the second input (`connect(from="Pad", to="Vocoder", input="Carrier")`) that is then made to speak. With `Carrier` on Sidechain and nothing wired, the machine is silent and its editor says "Wire a carrier into the sidechain".

- Bands: `Bands` (8 to 40) between `Low` (50 to 500 Hz) and `High` (2 to 16 kHz), each a 4th-order band-pass on both sides, `Width` (50 to 200 % of the spacing). Few bands are a classic robot, many keep the words.
- Followers: `Attack` (0.5 to 100 ms) and `Release` (5 to 1000 ms) are 10 to 90 % times. Short is crisp, long runs syllables into a wash. Bands below about 300 Hz cannot follow faster than their own period.
- Voice: `Formant` moves the carrier's bands up or down 12 semitones against the modulator's without changing the carrier's pitch; `Sibilance` passes the modulator's highs above about 5 kHz so consonants stay clear; `Freeze` holds the band levels (switched on with nothing heard yet, it holds the first 150 ms of the next sound); `Spread` sends odd bands left and even bands right.
- Synth: `Wave` (saw to pulse), `Noise blend`, `Detune`, `Glide` and a `Synth` attack, decay, sustain and release envelope. A chord is levelled to about the loudness of one note.

## Use it for

- A robot or talking-synth voice (Synth carrier, saw, fast followers), a choir from a spoken line (long envelope, `Spread`, chord), a whisper (Noise), a drum loop that plays a chord, a pad or synth that speaks (Sidechain), and held vowel drones (Freeze).

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Carrier | Synth | Synth needs notes, Noise needs none, Sidechain needs a wire into input 2 |
| Bands | 16 | 8 is a robot, 40 keeps the consonants |
| Low | 100 Hz | the lowest band; lower keeps the body of the voice |
| High | 8 kHz | the highest band; higher keeps the air |
| Attack | 3 ms | short keeps the consonants |
| Release | 60 ms | longer runs the words together |
| Formant | 0 st | -7 is a giant, +8 a small creature |
| Mix | 100 % | lower lays the carrier under the dry voice |

`Amount` (default 100 %) scales `Formant`, `Sibilance` and `Spread` together: 0 % is the bypassed modulator bit for bit, 200 % doubles the three up to their limits. `Output` is the level of the vocoded sound in dB.

## Starting points

- preset="Robot — Classic 16 Band", preset="Choir — Wide Chord Pad", preset="Whisper — Noise Carrier", preset="Bright — 40 Band Detail", preset="Deep — Formant Down", preset="Small — Formant Up", preset="Drone — Frozen Vowel", preset="Sidechain — Synth Carrier".

## Pitfalls

- A Synth carrier with no notes is silent: connect notes first. Sidechain with no wire is silent, by design.
- The modulator is the main input only; the sidechain is the carrier, not a key. Put the voice on input 1 and the instrument on input 2.
- Changing `Bands` or `Carrier` while it plays fades out over 4 ms and back in; `Low`, `High`, `Width` and `Formant` glide over 20 ms.
- A very quiet or very dull modulator gives a very quiet result: raise it before the vocoder, or add `Sibilance`.
- Freeze needs a sound to hold; a held vowel lasts as long as the notes do.

## Pairs well with

- Neo Chord before it (a held chord from one key), Neo Poly or Neo Wave as a sidechain carrier, Neo Comp on the modulator, Neo Delay or Neo Verb after it.

## Checklist

- [ ] `machine_describe(machine="Vocoder")`, then `param_set(machine="Vocoder", changes=[{"param": "Carrier", "value": "Noise"}])` to hear it without notes.
- [ ] `machine_readings(machine="Vocoder")` gives `bands`, `voices` and `loudest_band_db`: a Synth carrier with `voices` at 0 means no notes arrive.
- [ ] `analyze(source="Vocoder", metrics=["stereo"])` at `Spread` 100 %: the correlation drops.
