## How it sounds

A quiet, monophonic synth for wooden, mallet-like plucks and tuned percussion: a noise and pulse burst strikes a resonator, with controls for the strike colour, harmonics, damping, a tunable body resonance and the envelope. It plays A4 at 440 Hz as written.

## Use it for

- Marimba-, wood-block- and pluck-like notes.
- Short, tuned percussion for mallet parts and rhythmic tics.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Noise to pulse mix | 50.0 / 50.0 % | an even mix of breathy and clicky strike |
| Noise time | 4.5 ms | a short burst is a sharp tick |
| Brightness | 981 Hz | how bright the strike is |
| Resonator frequency | 621.8 Hz | the body that colours the note |
| Resonator gain | -0.3 dB | how loud the body is against the dry strike |
| Decay | 2.47 s | shorter is a dry block; longer a ringing bar |
| Release | 0.087 s | the tail after the note ends |

## Starting points

- A marimba: the defaults, a short Decay near 0.3 s and Resonator gain +6 dB.
- A wood block: Decay 0.04 s, Resonator frequency 1.5 kHz.
- Play it: `notes_write(machine="Wood One", pattern="A", notes=[{"pitch": "A3", "start": 0, "length": 0.5}])`.

## Pitfalls

- Quiet at its defaults (about -28 LUFS for one note): raise Resonator gain or the master.
- Resonator gain at +40 dB can overload the output.
- Unverified: what Pulse colour and Inharmonicity do in the signal path, beyond their ranges.

## Pairs well with

- Jeskola Reverb or Neo Verb for a room; Neo EQ to shape the body.

## Successor

None: no Neo machine matches a noise-excited resonator; Neo Poly or Neo Wave are different instruments, so `machine_replace` does not apply.

## Checklist

- [ ] `machine_describe(machine="Wood One")`: its twelve parameters are listed with their units.
- [ ] `machine_readings(machine="Wood One")`: a note reads near -28 LUFS at the defaults.
- [ ] `analyze(source="Wood One", metrics=["loudness"])`: the output stays under full scale.
