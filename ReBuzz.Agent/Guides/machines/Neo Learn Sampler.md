## How it sounds

A teaching sampler: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. One recording (Dye Essence's five built-in samples, or your own WAV) is played across the keyboard from a start offset past its attack, with a tone tilt around 1 kHz, a crossfaded loop, a vintage grit stage (12 bits, a lower sample rate, tape wow) and a small room. Lo-fi keys, loop pads, drones, bells and plucks from a recording.

## Use it for

Lo-fi keys, loop pads, drones, and bells and plucks made from a recording.

## Key controls

- `Source` chooses the recording: Choir, Strings, Bell, Pluck, Texture or User (your own WAV; with none loaded it plays Choir).
- `Start Offset` moves where playback starts, up to 80 % into the sample: past the attack, the note swells in.
- `Tone` tilts the spectrum around 1 kHz: to the left a gentle low-pass, to the right a high shelf.
- `Loop` is 0 % for a single play; above it a region after the start loops while the note is held, crossfaded at the seam (40 ms), and the region grows with the value.
- `Vintage Grit` engages 12-bit quantisation, a lower sample rate and a slow tape wow, together.
- `Room Acoustic` blends in early reflections and a short tail.
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## Starting points

- Loop pad: `Source` Choir, `Loop` 60 %, `Room Acoustic` 40 %, slow `Attack`.
- Lo-fi keys: `Source` Pluck, `Tone` -30 %, `Vintage Grit` 70 %.
- Bronze strike: `Source` Bell, `Loop` 0 %, `Sustain` 0 %, long `Decay`.
- Your own sound: import a WAV with `machine_data_import` (kind sample), then play it with `Source` User.

## Pitfalls

- The user sample is kept with the song as 24-bit audio, at most 20 s (longer files are cut; the result says so).
- Importing a sample sets `Source` to User; choosing User without one loaded plays Choir.
- A single play ends when the sample ends, however long the note is held: use `Loop` for pads and drones.
- Grit and Room run after the voices, on the sum; they are the same for every note.
- Unison 0 to anything above it jumps from 1 voice to 5.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.

## Pairs well with

Neo Verb for space, Neo EQ to shape a recording, Neo Comp on loops, Neo Delay on plucks.

## Checklist

- [ ] Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- [ ] Change one control at a time with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings`.
- [ ] `plot` a spectrum to see `Tone` tilt the highs, and `compare` `Vintage Grit` at 0 % and 70 %.
- [ ] Writes via `param_set` show on the Explain strip with the Agent badge.
