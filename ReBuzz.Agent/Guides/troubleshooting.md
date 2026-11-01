# Troubleshooting

Reproduce, measure, isolate, fix one thing, measure again. Each section: what you see, what to check (with the call), the usual causes and the fix.

## Silence

`render` or `analyze` reads "silent" (nothing above −90 dBFS), or a part is missing from the mix. Check in this order:

1. Placed in the range? `arrangement_get(from=…, to=…)`. A pattern only plays where it is placed, and the render's range must cover it.
2. Notes in the pattern? `pattern_list(machine=…)`, `notes_read(machine=…, pattern=…)`. Notes past the pattern's end are dropped (`notes_write` lists them under problems).
3. A path to Master? `machine_list`: follow the connections; a gain of −inf is silence. An effect with nothing feeding it is silent.
4. Muted or soloed? `machine_list` shows both; `machine_set(machine=…, mute=false)`. A render's own `solo` list silences the other generators for that render.
5. The sound itself: `param_get(machine=…)`. Look for `Volume` at -inf, oscillator levels at 0 % (Neo Poly O1–O3, Neo Wave A and B), amp sustain 0 % with a very short decay, a filter cutoff near 20 Hz, Neo FM6 carriers at level 0 or switched off (`Op1 on`).
6. Does the machine play notes at all? See "Machines that don't play notes".
7. Effects: Neo Verb with `Freeze` On takes no new input. Neo Comp with `Sidechain` External still needs its main input. `Mix` 0 % on an effect passes the dry sound (quiet effect, not silence).
8. Neo Wave builds its tables in the background; renders wait up to 5 s for them. If the first notes are missing after a table import, render again.

## Clipping

`render` reports clipped samples, or a sample peak above 0 dBFS.

- Find the part: `render(probes=[…every source…])`; the probe with the highest peak.
- Usual causes:
  - a Neo synth's `Volume` raised toward +6 dB;
  - stacking: 7 unison voices are about +8.5 dB, a 4-note chord about +6 dB;
  - resonance near 100 % (the ladder self-oscillates);
  - Neo Drive with `Auto gain` Off and a high `Drive` or `Output`; Neo Comp `Makeup` too high;
  - connection gains (up to +12 dB), several sends adding up in one reverb;
  - Neo FM6 with feedback 7 on loud operators.
- Fix at the source: `param_set` on its `Volume`, or its connection gain with `connection_set`. When the whole master is too hot, lower every connection into Master by the same dB in one call.
- Renders are 32-bit float, so the WAV keeps peaks above 0 dBFS, but every export and playback clips them: treat anything above 0 as clipped, and keep the master's true peak at −6 dBTP or lower before mastering.

## Phase and mono

`analyze(source=…, metrics=["stereo"])` reads a correlation below 0, a band with correlation under 0.5, mono loss over 3 dB, or "the low end is wide or out of phase".

- Causes: a polarity-flipped copy; the same sound on two routes with different delays (a parallel route through Neo Drive's 62 samples or Neo Comp's lookahead) making a comb filter; unison spread on a bass; widened lows.
- Comb filters show as regularly spaced dips in the third octaves (`analyze(source=…, metrics=["spectrum"])`); confirm by rendering with one route muted (`render(mute=[…])`).
- Fixes: parallel processing with the effect's own `Mix` (its dry path is delay-matched) instead of two routes; basses with `Unison` 1 or `Uni spread` 0%; kick and bass panned 0; Neo Utility's polarity flip and mono-below when it is installed.

## Latency

- Neo Drive adds 62 samples at every quality; Neo Comp adds its `Lookahead` (0–5 ms). Both tell ReBuzz, and renders stay aligned.
- Live playing in studio mode adds the audio buffer; renders don't.
- Two routes with different latency meeting again cause phase problems (above).

## CPU and slow renders

- Renders over about 2 s report progress. One render is at most 600 s; render long songs in parts with from and to.
- Heavy: voices (polyphony × unison; Neo Poly has a 32-voice pool), Neo FM6 (6 operators at 2× oversampling), Neo Wave (2× oversampling, two tables), Neo Drive at `Quality` 8×, long reverb tails.
- Lighter: fewer voices (`Polyphony`, `Unison`), Neo FM6 `Engine` Raw 1× (it also changes the sound), Neo Drive `Quality` 2×, or bounce a finished part to a sample with `wave_from_render(source=…)`.
- In studio mode the user's audio pauses while a render runs: keep renders short when they are listening.

## Missing machines

- `gear_list` marks unusable gear with its health ("missing", "crashed"). `machine_list` shows what actually loaded from a song.
- Substitutes (successors with the same role): Jeskola Reverb, Raverb and Pedal Plate → Neo Verb; Jeskola Distortion → Neo Drive; Pedal Comp → Neo Comp; Jeskola Bass, Oomek Aggressor 303 → Neo Poly ("Bass — Acid"); Pedal FM → Neo FM6. Search with `gear_list(search=…)`.
- Gear names must match `gear_list` exactly; machine names are the ones in the song.

## Machines that don't play notes

- `notes_write` puts notes in each pattern's engine MIDI column, and ReBuzz sends them to the machine as MIDI. Neo machines and most managed machines play them; some older native machines ignore MIDI.
- Check: `machine_describe(machine=…)` (its notes field), then one note rendered solo and `analyze(source=…, metrics=["pitch"])`.
- Fix: use a Neo synth or another machine. For a tracker-only machine the user can move the notes to the Pattern View's note lanes (Session View slot menu, "Move notes to the Pattern View").
- An octave off: the tools use C4 = 60, which the Pattern View shows as C-5. Some older machines play an octave away; measure the pitch and shift with `notes_transform(machine=…, pattern=…, transforms=[{"op": "transpose", "semitones": 12}])`.

## Hanging notes

A note that never stops, or a tail that runs on and on.

- `notes_read`: a note longer than intended, or overlapping notes of one pitch (`notes_write` reports overlaps).
- Long releases: amp release up to 20 s on Neo Poly and Neo Wave; low `Op1 rate 4` on Neo FM6 carriers; Neo Verb `Freeze` On; a long `Decay`.
- Voice mode Mono or Legato with overlapping notes glides instead of starting a new note: intended for slides, a bug otherwise.
- `tail_seconds` in `render` (default 2) only lets tails ring after the range; it doesn't hold notes.
- In studio mode, stopping releases all voices and stopping again silences everything (a panic).
- Fix: `notes_transform(machine=…, pattern=…, transforms=[{"op": "legato", "gap": 0.05}])` or `{"op": "staccato", "factor": 0.5}`; shorter releases; `Freeze` Off.

## A render that differs from the last one

The same song state over the same range renders the same audio. When two renders differ:

1. Did the song change? `studio_status` (what undo would revert), `snapshot_diff(a=…)` against a snapshot saved at the earlier render. In studio mode the user may have edited.
2. Same range and options? from, to, section (markers may have moved), solo, mute, probes, tail_seconds.
3. Randomness in the sound: Neo Poly `Drift`, the Random mod source, S&H and Smooth LFO shapes, noise, LFOs with retrig Off (one free-running LFO whose phase depends on when playback started), Neo FM6's S&H LFO. For exact A/B comparisons: `Drift` 0%, retrig On, no Random.
4. Tails from before: renders first wait (up to 5 s) for earlier audio to die away; a frozen reverb never does.
5. Tempo changes inside the range: the render follows them and says so.

Compare the two with `analyze` on both: the same state reads the same loudness to 0.1 LU and the same bands.

## Tool errors

- Errors name the next step ("Did you mean …", the valid range, the call that lists the names). Read them; don't repeat the same arguments.
- Unknown parameter: `machine_describe(machine=…)`. Wrong unit or choice: the error lists examples. Unknown machine: `machine_list`. Unknown gear: `gear_list(search=…)`.
- A call that fails changes nothing: its undo step is taken back.

## Checklist

- [ ] The problem is reproduced and measured before any fix: a `render` and the `analyze` numbers that show it.
- [ ] The cause is isolated: `render(solo=[…])`, `render(mute=[…])` or a probe (`render(probes=[…])`) shows where it starts.
- [ ] One fix at a time, measured on the same range: `analyze(source=…, metrics=[…])`.
- [ ] Nothing else got worse: `snapshot_diff(a=…)`, and the master's loudness, peaks and stereo unchanged except as intended.
- [ ] The whole song plays: `render()` of everything is not silent and has no clipped samples.
- [ ] Saved: `song_save(path=…)`.
