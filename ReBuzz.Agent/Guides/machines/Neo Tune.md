## How it sounds

A pitch corrector for one voice or one melodic line. It follows the pitch of the input, moves it to the nearest note of a key and scale (or to the notes held on its MIDI input), and re-synthesises the sound with pitch-synchronous overlap-add, so the singer's timbre stays the singer's. `Retune speed` 0 ms is the hard, stepped, robotic sound; 20 to 80 ms is natural correction; 400 ms lets the voice slide between notes. Breaths, s and t sounds, and silence pass through unchanged, and the sharp start of a consonant is kept as sung.

- `Key from` Set uses `Key` and `Scale`; Listen works the key out from the notes sung so far (the app has no song-wide key yet). `Scale` Custom uses the 12 notes of `Custom notes`, which the editor's keyboard sets by clicking notes (a click on any scale turns it into Custom).
- `Humanize` keeps the slow drift of a held note; `Keep vibrato` passes the singer's own vibrato through (0 % flattens it under a hard retune); `Add vibrato`, `Vibrato rate` and `Vibrato delay` put a steady vibrato on a note that is held flat. `Amount` moves the voice only part of the way to the note (in cents).
- `Transpose` moves the corrected voice by up to 24 semitones. `Formant preserve` On keeps the vowel colour where it was when the pitch moves; `Formant shift` moves it up (a smaller voice) or down (a larger one) by up to 12 semitones. The extremes turn robotic.
- `Target` MIDI sends the voice to the notes held on the machine's MIDI input (key and scale are ignored): with `MIDI octave` Nearest a held note counts in every octave, so the voice stays in its own range; Exact makes it sing the played pitch.
- `Range` picks the tracker and the latency: Low latency (voices above 100 Hz, about 10 ms: for playing live), Quality (about 28 ms, the best sound) or Full (down to bass, about 36 ms). The latency is reported to the host and compensated.
- `Input` takes the sum of the channels or the left one; the corrected voice is mono. `Mix` blends in the dry, delayed to match (at 0 % the output is the delayed input bit for bit).

## Use it for

- A lead vocal or a harmony to put in tune, hard-tuned for the effect or lightly for a repair; a solo instrument with one note at a time; a voice sent to a keyboard's notes; a robot or a small character voice with `Transpose` and `Formant shift`.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Scale | Major | the notes the voice may land on; fewer notes pull harder |
| Retune speed | 60 ms | quick but natural; 0 ms is the hard effect |
| Humanize | 40 % | keeps the drift of long notes |
| Keep vibrato | 100 % | leave the singer's vibrato; 0 % flattens it |
| Amount | 100 % | lower it to hide the correction |
| Formant preserve | On | the vowel stays put when the pitch moves |
| Range | Quality | Low latency only for playing live |

## Starting points

- "Lead — Natural" (60 ms, vibrato kept), "Lead — Subtle" (55 % amount, slow), "Lead — Hard Tune" (0 ms, vibrato flattened), "Live — Low Latency Natural" (the 10 ms tracker).
- "Special — MIDI-led" (the voice sings the held notes), "Special — Robot" (a fifth down, vowel up, hard), "Special — Thick" (half dry, a slow vibrato added).

## Pitfalls

- One voice at a time: a chord, a full mix or a noisy track has no single pitch to follow, so little is corrected. Put it on a solo track.
- Set the `Key` and `Scale` of the song, or the voice is pulled to wrong notes; a mistuned scale sounds worse than none. With `Key from` Listen the key is a guess from what has been sung.
- A very low `Retune speed` with a small scale sounds obviously corrected; that is the effect, not a fault. For a natural result raise it and keep `Humanize` and `Keep vibrato` up.
- The latency is 10, 28 or 36 ms by `Range`, reported to the host (see `latency_samples` in `machine_readings`); Low latency follows low voices less steadily and can jump an octave on bright vowels.
- The machine only receives MIDI notes the app routes to it (it is an effect): to conduct it from a keyboard, use `Target` MIDI and a MIDI source that reaches the machine.

## Pairs well with

- Neo De-esser before it for sibilance, Neo Comp and Neo EQ after it; Neo Analyzer to check the result.

## Checklist

- [ ] `machine_readings(machine="Tune")`: `voiced` is 1 while the voice sings, `correction_cents` is within 50 cents, `latency_samples` matches `Range`.
- [ ] `analyze(source="Vocal", metrics=["spectrum"])`: the vowel colour is unchanged with `Formant preserve` On.
- [ ] With `Mix` at 0 % the output is the input, delayed (`param_set(machine="Tune", changes=[{"param": "Mix", "value": "0%"}])`).
