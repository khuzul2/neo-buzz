## How it sounds

A teaching spectral synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. One recording (Dye Essence's five built-in samples, or your own WAV) is analysed once into up to 64 tracked partials and a band-by-band noise, and then rebuilt at any key: its partials spread apart or bunched, smeared in time, frozen at one moment, thinned to the loudest few, or balanced against the noise. Frozen pads, bell-like stretches, ghostly textures and slowed-down sounds. The picture is a spectrogram of the analysis.

## Use it for

Frozen and slowed pads, bell and glass stretches of any sound, whispering textures, and learning how analysis-resynthesis works.

## Key controls

- `Source` chooses the recording: Choir, Strings, Bell, Pluck, Texture or User (your own WAV; with none loaded it plays Choir). A WAV is analysed in the background, a second or less for a short one, and is silent until then.
- `Stretch` moves each partial away from its whole multiple of the pitch (+ apart: bells, glass; - bunched: dense, clustered). The pitch does not change.
- `Time Smear` averages the partials over up to 300 ms either side: attacks and movement drift into a liquid wash.
- `Freeze` slows the reading to a stop and moves its start to 40 % into the sample; at 100 % one moment is held while the key is down.
- `Partial Gate` keeps only the loudest partials: all 64 at 0 %, one at 100 %. The noise stays.
- `Tone / Noise` balances the halves of the analysis: 0 % the noise only, 50 % as recorded, 100 % the partials only.
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 3 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## Starting points

- Frozen pad: `Source` Choir, `Freeze` 100 %, `Time Smear` 40 %, slow `Attack`.
- Bell from a pluck: `Source` Pluck, `Stretch` +45 %, `Sustain` 0 %, long `Decay`.
- Ghost: `Source` Texture, `Tone / Noise` 15 %, `Time Smear` 50 %.
- Your own sound: import a WAV with `machine_data_import` (kind sample), then play it with `Source` User.

## Pitfalls

- The model has no phases and its frames are 43 ms long: a sharp attack becomes a short swell, and drums and plucks are better on Neo Learn Sampler.
- The user sample is kept with the song as 24-bit audio, at most 20 s, with its analysis (longer files are cut; the result says so).
- Importing a sample sets `Source` to User; choosing User without one loaded plays Choir.
- `Freeze` also moves where the reading starts, so a part-way value starts later in the sample.
- Unison 0 to anything above it jumps from 1 voice to 3.
- A machine of 12 controls: the other Dye Essence controls (key tracking, formant keep, harmonic snap, tilt) are fixed and cannot be set here.

## Pairs well with

Neo Verb for space, Neo Delay, Neo Comp on pads, Neo EQ to shape the cloud.

## Checklist

- [ ] Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- [ ] Change one control at a time with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings`.
- [ ] `plot` a spectrum to see `Stretch` move the partials off the harmonic series, and `compare` `Tone / Noise` at 0 % and 100 %.
- [ ] Writes via `param_set` show on the Explain strip with the Agent badge.
