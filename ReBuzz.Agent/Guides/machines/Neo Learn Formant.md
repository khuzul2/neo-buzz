## How it sounds

A teaching vowel synth (the box is called Learn Voice): one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A glottal pulse, a sub and breath noise go through four band-pass filters placed where a human mouth puts its formants, so the oscillator seems to speak. Choirs, whispers, talking basses, child voices, robot chants.

## Use it for

Vowel sounds: choirs, whispers, talking basses, child voices and robot chants.

## Key controls

- `Vowel (A-E-I-O-U)` slides the four formant filters across the vowels A, E, I, O, U (0 % is A, 100 % is U).
- `Tract Size` scales all four formants together, without changing the pitch: down is a giant, up is a child.
- `Formant Q` is how sharp the four peaks are: low is a warm choir, high is nasal and robotic.
- `Glottal / Bass` is the voice source: low is a soft round pulse, high a buzzing pulse with a sub and drive.
- `Breath Noise` mixes air into the filters; it is not pitch-tracked (it is the same at every note).
- `Talking Env` lets each note's envelope sweep the vowel, so each key press says a syllable ("wah", "yoi"); the middle is off, above sweeps up, below sweeps down.
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## Starting points

- Ah choir pad: `Vowel` 0 %, low `Formant Q`, slow `Attack`, `Unison / Spread` 30 %.
- Whisper: `Breath Noise` 90 %, `Glottal / Bass` 5 %.
- Talking bass: `Tract Size` -40 %, `Glottal / Bass` 70 %, `Talking Env` 60 %, play low notes.
- Child choir: `Tract Size` 80 %, `Vowel` 50 %.

## Pitfalls

- The formants stay put when you change the note; that is why a vowel stays the same vowel up the keyboard. Very high notes (above the formants) sound thin: that is how real voices behave.
- `Talking Env` needs a note: it sweeps with each note's envelope, so a long `Attack` slows the syllable.
- `Breath Noise` is not pitch-tracked.
- Unison 0 to anything above it jumps from 1 voice to 5.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.

## Pairs well with

Neo Verb for a choir hall, Neo Ensemble to widen pads, Neo Filter to sweep the whole voice, Neo Delay on talking leads.

## Checklist

- [ ] Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- [ ] Change one control at a time with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings` (rms_db, peak_db, active_voices).
- [ ] `plot` a spectrum of the render to see the four formant peaks move with `Vowel`, and `compare` two settings.
- [ ] Writes via `param_set` show on the Explain strip with the Agent badge.
