## How it sounds

An acid bass synth: a saw or square oscillator into a resonant low-pass with an envelope, accent and slide. Monophonic, with the squelchy character of old acid lines.

## Use it for

- Acid lines and squelchy basses.
- Rubbery, plucky mono leads.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Oscillator wave | Saw | saw or square |
| Filter cutoff | 60 | 0 to 240, a bare scale |
| Filter resonance | 100 | 0 to 128 |
| Filter envelope amount | 70% | 0 to 100 percent |
| Envelope decay | 60 | 0 to 128 |
| Volume | 60% | 0 to 200 percent |

## Starting points

- Presets: "Acid — Classic Squelch", "Acid — Square Rubber", "Bass — Deep Round", "Acid — Screamer"; load with `preset_load(machine="Aggressor", preset="Acid — Classic Squelch")`.

## Pitfalls

- Slide and Accent are per-note pattern switches: `param_set` can't set them and `notes_write` doesn't write them yet.
- Volume above 100 percent gets hot: +4.3 dBFS at 200 percent in the profile.
- Monophonic, one track.
- The profiler heard no steady pitch on its default patch; notes play as written.

## Pairs well with

- Jeskola Distortion for more bite; Jeskola Delay for echoes.

## Checklist

- [ ] `analyze(source="Aggressor", metrics=["loudness"])`: a note is near -18 LUFS.
- [ ] `param_get(machine="Aggressor")`: `Filter resonance` and `Envelope decay` are what you set.
- [ ] `render(probes=["Aggressor"])`: the note decays to silence.
