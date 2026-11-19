# Neo Tune

| | |
|---|---|
| Status | Built (NT1), reworked in NT2 (section 11): a control stage that works on the note centre, a resample-and-splice engine, the Natural control and the quality gates. The real-voice figures of NT1 are in section 9, NT2's in section 11.6. |
| Branch | `machines/neo-tune-nt1`, then `machines/neo-tune-nt2` |
| Code | `Machines/NeoTune/` (`NeoTune.cs`, `NeoTuneEditor.cs`, `Params.def`, `Presets.txt`, `Engine/TuneEngine.cs`, `Engine/TuneScales.cs`), the two flavours in `Machines/Portable/NeoTune` and `Machines/Studio/NeoTune`; tests `NeoBuzz.MachineTests/TuneTests.cs`; evaluation `tools/PitchEval/Tune.cs` |
| Builds on | the shared pitch engine (`docs/specs/pitch.md`: `PitchTracker`, `OnsetDetector`), `PortableMath`, the Neo machine framework (`docs/specs/machines.md`) |

## 1. What it is

Pitch correction for a single voice or a melodic line: an effect, mono in (the sum of the channels or the left one), stereo out (the corrected voice on both channels; with Mix below 100 % the dry sound keeps its stereo image). It follows the pitch of the input with the shared tracker, moves it to the nearest note of a key and scale (or to the notes a MIDI input holds) at a chosen speed, and re-synthesises the sound by TD-PSOLA, so the timbre stays the singer's. Unvoiced sounds and silence pass through as the delayed input. Clean room: the algorithms are the textbook ones (YIN pitch tracking, pitch-synchronous overlap-add, one-pole smoothing); the effect is called pitch correction everywhere.

## 2. Signal flow

1. **Tracker.** The input goes through `PitchTracker` (Voice range; Low latency or Quality by the Range control, Full for bass), one estimate per hop (1.35 or 2.7 ms), and an `OnsetDetector` for the Transients switch.
2. **Control stage** (per estimate). Target note: the nearest note of the scale's 12-bit mask (with 15 cents of hysteresis, so a note on the border does not flicker), or the nearest held MIDI note (all octaves, or the played octave only), or, with Key from Listen, of the scale on the root that a 30 s pitch-class histogram of the voice fits best. The correction is the distance to the target, shaped by: Retune speed (a one-pole glide, 0 ms = at once), Humanize (a share of the slow drift of the held note, taken from a slow average of the pitch, is kept), Keep vibrato (a share of the fast movement, the part above a faster average, is kept), Add vibrato (a sine of the set rate and depth that fades in after the delay) and Amount (the share of the correction applied, in the semitone domain). Transpose is added last. The result is a shift in semitones stored with the tracker's f0 in a contour at the middle of the audio the estimate heard.
3. **Analysis marks.** One mark per period of every voiced stretch, each placed where the waveform best matches the previous mark's (a normalised correlation over 0.6 of a period inside plus or minus 0.2 of a period of the predicted position, refined to a fraction of a sample by a parabola), so every grain starts at the same phase of the cycle.
4. **Synthesis.** Grain centres are laid at the corrected period, with fractional accuracy and read from the contour at their own time (so the tracker's jitter does not reach the output: a hard-tuned note has marks exactly one period of the note apart). Each grain is the input around the nearest analysis mark, a Hann window of two periods (at most the range's longest grain), read with a cubic (Catmull-Rom) interpolation and added into an accumulator together with the window's weight; the output is the sum divided by the weights, so the level does not depend on how the grains overlap.
5. **Unvoiced and transients.** Where the contour says unvoiced or silent the output is the dry signal delayed by the latency; a 2.5 ms crossfade joins it to the grains. At a hard attack (the onset detector) the dry signal is held for a few milliseconds (Transients on), which keeps the b, d, p of a voice and the pluck of a note as played. At Quality the lookahead lets the gate open before the attack; at Low latency it does so partly.

## 3. The formant method: the grain's own envelope

The brief allows the PSOLA grain envelope or a spectral-envelope correction. Neo Tune reads each grain at a speed phi (the window and its content scaled by 1/phi), with phi = 2 to the power (Formant shift / 12) when Formant preserve is on, and the pitch ratio times that when it is off (the spectral envelope then rides the pitch, as in a sped-up or slowed voice). Why: it needs no spectral analysis (no extra latency, no cost, nothing to tune), it is exact in the time domain (the envelope of a grain is the envelope of the voice), and the same mechanism does Formant shift. Its limit is the same as every PSOLA formant control: the grain must stay a few periods long, so the extremes (plus or minus 12 st with a large pitch move) turn robotic and short grains lose the low harmonics. A spectral-envelope correction would hold up better at the extremes and costs a transform and its latency; it can be added behind the same two controls without changing the manifest. Measured: after a +7 semitone move the envelope error (cepstral, 40 coefficients) is 1.64 dB with Formant preserve on and 11.62 dB off; a Formant shift of -7, -3, +4, +7 st moves the envelope by x0.65, x0.82, x1.25, x1.52 (wanted x0.67, x0.84, x1.26, x1.50).

## 4. Controls

*NT2 (section 11) adds five parameters, 25 in all: Natural, Correction zone, Transition, Note stickiness (group Natural) and Method (group Engine). The text below describes NT1's 20; sections 2 and 3 describe NT1's control stage and PSOLA, which section 11 reworks.*

20 parameters in 7 groups, `Params.def` (generated: `Params.g.cs`, the manifest and the agent's `machine_describe`): **Key** (Key from Set or Listen, Key, Scale: chromatic, major, minor, dorian, phrygian, lydian, mixolydian, locrian, harmonic minor, minor pentatonic, major pentatonic, blues, custom; Custom notes, the 12-note mask), **Pitch** (Retune speed 0 - 400 ms, Humanize, Amount, Transpose plus or minus 24), **Formant** (Formant preserve, Formant shift plus or minus 12), **Vibrato** (Keep vibrato, Add vibrato, Vibrato rate, Vibrato delay), **Target** (Scale or MIDI, MIDI octave Nearest or Exact), **Engine** (Range: Low latency, Quality, Full; Input: Sum or Left; Transients), **Output** (Mix). Readings for agents (`machine_readings`): `input_hz`, `target_note`, `correction_cents`, `voiced`, `latency_samples`.

**Follow song key.** The app has no song-wide key yet, so the brief's "Follow song key" is "Key from: Listen": the key is worked out from the notes sung so far (the scale type stays as set), and the machine settles on it after about a second of voice and moves only when another root beats the current one by 10 %. When the song gets a key, Set can follow it with no change to the manifest.

**MIDI target.** `NeoTuneMachine.MidiNote` is public, as in the Neo generators; the held notes are the targets. The app routes MIDI today only to generators and to machines with a note input in their manifest; an effect with a MIDI input is a limit of the host that this machine does not change (section 10).

## 5. Latency (PDC)

Reported to the app by `GetLatency()` (machine delay compensation) and in `latency_samples`: Low latency 10 ms (480 samples at 48 kHz), Quality 28 ms (1344), Full 36 ms (1728), the same in milliseconds at every sample rate. The lookahead is what the tracker's span and a grain of two periods need; at Low latency the grains are capped at 8 ms, so a voice below about 250 Hz gets grains shorter than two periods (fine for hard correction, a little thin for a deep voice): that is the Low latency trade. The Low latency figure is at the upper end of a 5 to 10 ms aim; a grain of two periods of a 100 Hz voice is 20 ms and cannot fit, and the tracker needs a window to find the period at all.

## 6. Editor

*NT2: see section 11.4 (the Natural knob first, the fine controls grouped, the centre line and the zone on the display).*

A fixed-size window on the Neo editor kit (one source for the Windows app and the studio): **Key** (Key from, Key, Scale, Target), **Pitch** (the live display and the scale keyboard), **Correction** (Retune speed, Humanize, Amount, Transpose), **Voice** (Formant preserve and shift, Keep vibrato, Add vibrato, rate, delay), **Engine** (Range, Input, Transients, Mix). The live display shows the last 8 seconds on a piano roll with the scale's notes lit: the voice (grey) and the corrected voice (amber), and the target note and the correction in cents while a voice is heard; the window follows the voice. The scale keyboard is one octave: a click on a note enables or mutes it in every octave and turns the scale into Custom (from the notes it had); the root of the scale is marked.

## 7. Presets

*NT2: the Lead presets are replaced (section 11.5).*

Seven factory presets: Lead (Hard Tune, Natural, Subtle), Live (Low Latency Natural), Special (MIDI-led, Robot, Thick). Robot is hard-tuned in minor pentatonic, a fifth down with the formants moved up and not preserved; Thick is half dry with a slow vibrato added (no doubler: the brief's slight detune would need a second grain stream and is left out).

## 8. Tests (`NeoBuzz.MachineTests/TuneTests.cs`, 37 on synthetic voices and the shared tracker as judge; `PresetTests`, `ModulationCostTests`, `StateVersionTests`, the guide tests, `ReBuzzTests` and the editor, hygiene and null tests register the machine)

| Criterion | Target | Measured |
|---|---|---|
| Hard tune accuracy | within 5 cents after the retune time, voices of 43 - 81 (MIDI), +/-40 cents off, three vowels, all ranges | steady error 0.6 cents at most (tracker p95 0.4 - 3.1, max 8.1 on a bass voice at Full) |
| Vibrato and scoops | a hard tune flattens them; natural keeps vibrato | input swings +/-44 cents, hard output p90 0.5, max 0.9; vibrato depth input 37.8, natural 33.9, hard 0.5, keep 50 % 17.8 cents |
| Retune speed | a one-pole glide | 99.9 % / 66.3 % / 30.6 % of a step 0.12 s after it at 0 / 100 / 300 ms |
| Humanize, Amount | the drift and the share | drift 26.1 cents in, 0.4 / 9.4 / 18.8 at 0 / 50 / 100 %; amount 0 / 50 / 100 % leaves 29.6 / 14.8 / 0.1 of a 30 cent error |
| Scales and key | the nearest note of 12 scales | 407 voices in 3 keys, all right; the custom mask; Listen finds D for a tune in D; MIDI: nearest octave, exact octave, chords pick the nearest chord tone |
| Transpose | whole semitones | within 0.11 cents of the target at -12, -5, +7, +12 |
| Added vibrato | the set depth and rate | +/-35.8 cents (set 40), 6.0 Hz (set 6) |
| Unvoiced and silence | pass through | bit for bit (noise, pink noise, silence); Mix 0 % is the delayed input bit for bit |
| Transients | a hard attack survives | a noise burst on a held tone: the test's following measure is 130.1 with Transients on at Quality, 10.3 at Low latency (partial), -2.7 off (the burst is lost) |
| Formant | the envelope within a set error | 1.64 dB (preserve on) against 11.62 dB (off) after +7 st; shifts within 0.02 of the octave ratio |
| Resynthesis | no pitch move leaves the waveform | 26.9 - 40.5 dB SNR against the input (a shift-free alignment), vowels aa, oo, ee at Low latency and Quality |
| Latency | per range | 480 / 1344 / 1728 samples at 48 kHz (10 / 28 / 36 ms), 1235 at 44.1 kHz, 2688 at 96 kHz for Quality; a click comes out exactly that late |
| Block size, sample rate | same bits | blocks of 32, 100 and 256 within 0.01 cents; 44.1 and 96 kHz within 0.03 cents |
| Allocation | 0 bytes | with every control moving (in the engine test and the hygiene test through the app) |
| Fuzz | no NaN, no crash | every parameter at random for many blocks |
| CPU | at least 20x real time | Linux x64 47 - 51x (Low latency, Quality, Full; floors 32 x64 and 22 arm64) |

## 9. Real voices: VocalSet

`tools/PitchEval/Tune.cs`, run by `.github/workflows/pitch-eval.yml` (`--tune`): the engine of the machine, as the machine runs it (256-sample blocks), over the VocalSet subset (long tones, scales, arpeggios straight, vibrato, breathy and vocal fry, excerpts; 2 files per singer and technique). The output is shifted back by the latency and its pitch measured with the shared tracker (Voice, Quality) against the nearest note of the scale (chromatic, or major on the key the machine listened for: Key from Listen), before and after; the share of voiced frames that stay voiced; the share of unvoiced frames that become voiced; and an artefact proxy: the spectral flux of the output against the input (mean change in dB of the 100 Hz - 8 kHz spectrum per 5.8 ms hop) and the rate of one-hop rises of the 4 - 10 kHz band over 12 dB, which a bad grain join produces. Six excerpts of at most 8 seconds are written before and after (artifact `neo-tune-excerpts`, with the CC BY 4.0 attribution to Wilkins, Seetharaman, Wahl and Pardo in their README); no audio is committed. The figures are below.

### 9.1 Measured (workflow run 2, 298 recordings of 20 singers, 929 457 voiced frames)

Pitch error against the nearest semitone (chromatic), all techniques, before and after:

| Setting | error median / mean (ct) | within 10 ct | within 25 ct | voiced kept | false voiced | flux in / out | flux p99 in / out | clicks per min in / out |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Input as sung | 24.8 / 24.9 | 20.0 % | 50.4 % | | | | | |
| Hard tune, Quality | 0.4 / 3.7 | 89.2 % | 94.6 % | 98.7 % | 4.2 % | 4.00 / 3.91 | 6.8 / 7.6 | 2.3 / 2.4 |
| Hard tune, Low latency | 0.3 / 4.1 | 87.8 % | 93.6 % | 97.7 % | 16.1 % | 4.00 / 4.01 | 6.8 / 8.1 | 2.3 / 3.9 |
| Natural (60 ms, vibrato kept), Quality | 18.0 / 20.6 | 31.1 % | 63.1 % | 97.6 % | 2.1 % | 4.00 / 4.07 | 6.8 / 7.0 | 2.3 / 2.4 |
| Subtle (55 %, 120 ms), Quality | 18.6 / 20.9 | 30.1 % | 62.3 % | 97.9 % | 2.6 % | 4.00 / 4.07 | 6.8 / 7.0 | 2.3 / 2.4 |
| Hard tune, Quality, major on the key listened for | 28.8 / 32.2 to 0.5 / 9.2 | 17.6 % to 85.6 % | 43.6 % to 89.5 % | 97.6 % | 4.8 % | 4.06 / 3.90 | 6.8 / 7.5 | 1.3 / 1.5 |

By technique (hard tune, Quality; within 10 ct before to after): long tones 18.4 % to 94.7 %, scales 20.8 % to 92.5 %, arpeggios straight 18.7 % to 91.8 %, breathy 20.3 % to 88.6 %, vibrato 20.7 % to 81.0 % (a wide vibrato is the hardest case for a correction that follows the pitch), vocal fry 19.8 % to 76.8 % (voiced kept 95.1 %). Reading: a hard tune puts about 9 frames in 10 within 10 cents of the note on real singing, from 2 in 10; a natural setting keeps the vibrato and the drift on purpose, so it moves the singer toward the note without flattening (the median error falls from 24.8 to 18.0 cents). The mean error of the hard tune (3.7 ct) against a median of 0.4 says that most frames are on the note and a minority are far off; the figures do not separate note transitions from errors of the tracker, which measures the output as well (not examined). On the major scale with the key listened for the mean after is 9.2 ct against 3.7 on the chromatic scale; the likely cause is a wrong root for a stretch, which pulls the voice to a neighbouring scale note (up to a semitone), but the run does not show it (the scale used for the measure is the root the listener ended on).

Artefacts: the flux of the output is within 2.3 % of the input's on average (3.91 against 4.00 dB; hard tune, Quality) and its 99th percentile is 12 % higher (7.6 against 6.8); the rate of sudden high-band rises is the input's (2.4 against 2.3 per minute). At Low latency the clicks are 3.9 per minute (arpeggios with vibrato 3.8 against 0.2, vocal fry 14.3 against 7.3): more of the frames the output tracker finds voiced are frames the Quality tracker calls unvoiced (the 16.1 % "false voiced", 59 % on the vibrato arpeggios): the Low latency tracker's voicing decisions differ from Quality's, so breaths and fade-outs are corrected there; the proxy cannot say whether the extra clicks come from the short grains or from those decisions. Six before/after excerpts were made for listening (artifact `neo-tune-excerpts` of the run). The levels of each pair agree within 0.5 dB.

## 10. Limits

- One voice at a time: a chord, a full mix or a very noisy track has no single pitch; little is corrected.
- Octave and voicing errors of the tracker are the machine's: the Low latency tracker can jump an octave on a bright vowel (pitch.md section 2), and a breathy or fry voice is called unvoiced and passes through uncorrected.
- Formant extremes turn robotic (section 3); very short grains at Low latency thin a deep voice (section 5).
- No song key yet (Key from: Listen replaces it); MIDI notes reach an effect only where the host routes them.
- No doubler, so no "Thick" detune beyond a slow vibrato.
- The vocal artefacts of any correction: a hard retune of a scoop or a fast slide can be heard as a step; that is the effect.

## 11. NT2: a natural control (hard effect to transparent)

**Why.** NT1's excerpts were judged "really low quality" against commercial real-time correctors. Measured on them (the lead's analysis of pitch-eval run 44140219236): the target flipped with vibrato on a hard tune; Natural and Subtle cut the singer's vibrato by 30 - 50 %; resynthesis added jitter (+25 - 37 %), sub-harmonics (+7 dB on a low male voice) and lost about 1 dB above 5 kHz; there were broadband bursts at note changes; and voiced frames dropped inside notes (0.93 to 0.89). NT1's evaluation measured only the pitch error and a click proxy, so it saw none of this. NT2 fixes each cause and measures each.

### 11.1 Control model (`Engine/TuneControl.cs`)

- **Centre.** The pitch is kept per tracker frame (absolute, and as cents from the current target note). The vibrato is detected by Schmitt zero crossings of the deviation from its slow mean; the **centre is the mean over one measured vibrato period** (two when it is fast), else over 70 - 140 ms. A mean over a whole period has no response at the vibrato's frequency, so the centre does not leak it (NT1's 120 ms one-pole leaked 23 % at 5.5 Hz). Measured leakage and the centre error are in 11.6.
- **Target on the centre.** The nearest note to the centre, with a hysteresis of `12 + 40 stick + 1.5 expression` cents past the midpoint (the expression term is the recent size of the movement around the centre, so a wide vibrato widens it) and a dwell of `25 + 100 stick` ms; a jump of 2 semitones or more waits until the centre is settled (speed under 300 ct/s), so the voice never picks the note it passes through. After a switch the recent frames are re-expressed against the new note.
- **Shift = goal - centre**, applied to the input: `shift = Amount * wj * (S + flatten) + vibrato LFO + Transpose`. `S` is the correction of the centre, smoothed by the retune time (times `1 + 3 Humanize` on a sustained note); it has a dead band of a few cents (a centre within it is left alone, which is what lets an in-tune voice through unchanged), is weighted by the **correction zone** (a centre far from the note is not pulled: a scoop or a slide passes; none in MIDI mode), by the **glide weight** (0 while the centre moves fast, by Transition) and by a **ramp** over the first 50 - 200 ms of a run (the centre is trusted only once settled). `flatten = -(1 - Keep vibrato)(pitch - centre)` removes the part of the pitch that is the singer's expression, to the degree Keep vibrato says; at Keep vibrato 100 % nothing is removed and the expression passes through exactly. `wj` releases the correction while the centre is far from the note (more than 110 - 170 ct, scale target), so the voice travels as sung and no large shift is made on the way; a correction under 0.4 ct is exactly 0.
- **Voicing continuity.** A gap in voicing under 70 ms inside a note holds the pitch and the shift; a real unvoiced gap or silence ramps the shift to 0 over 20 ms. The output never falls back to the dry voice inside a voiced note.
- **The Natural control** (`Engine/TuneNatural.cs`). One number, n from 0 to 1, writes six fine controls: Retune speed `100 n^2.2` ms (0, 13 ms at 40 %, 53 ms at 75 %, 100 ms), Correction zone `100 - 40 n` ct (none to 60), Keep vibrato `min(1, n / 0.6)`, Humanize `n`, Transition `n`, Note stickiness `0.15 + 0.85 n`. The machine does not read Natural: the editor's knob writes the six values in one undo step (`EditorContext.Set` gathers them into one gesture), a preset stores all values, and a test or an agent writes them (`TuneNatural.Apply`). The six stay editable; Natural stays where it was put.
- **State.** Five parameters were appended (Natural, Correction zone, Transition, Note stickiness, Method), so the first 20 are unchanged, no state version is needed and NT1 songs load by position with the new controls at their defaults (the defaults leave Retune, Humanize, Amount and Keep vibrato as the song had them).

### 11.2 Engines

- **Resample-and-splice** (`Engine/TuneSynth.cs`, `Engine/TuneSinc.cs`). The method of US 5,973,252 (priority 1997-10-27, filed 1998-10-15, granted 1999-10-26; listed as expired, anticipated 2018-10-14 on Google Patents, which says its status is an assumption and not a legal conclusion; checked 2026-11-19). The input is read from a delay line at the pitch ratio rho = 2^(shift/12) per output sample, through a 16-tap Kaiser-windowed sinc (beta 5, 256 phases, linearly blended): flat to 0.029 dB up to 0.40 of the sample rate over all phases, so the 1 dB loss above 5 kHz of NT1's cubic is gone. The reader is held in sync with the input by jumping whole periods: when it is more than 0.55 of the mark spacing J from the input position it jumps one J, forward or back, joined by a Hann crossfade of 0.25 J (16 to 3 ms) on the analysis marks, which are period-aligned, so the splice is seamless. Unvoiced: the reader resyncs to zero offset over 2 ms. With the shift exactly 0 for 30 ms the reader realigns with a crossfade of about 1.5 periods. There is no overlap-add of grains, so no comb filtering. It scales formants by rho, inaudible for the corrections under about a semitone that this engine is for.
- **PSOLA** (kept, fixed). Synthesis periods are now the **true analysis mark spacings** divided by the ratio (NT1 used the tracker's smoothed period, which removed the singer's cycle-to-cycle variation and raised jitter), read with the same sinc, and exactly bypassed when the shift is 0 (an in-tune voice is the input, bit for bit, at ratio 1: the null test). The transient dry hold stays here (Transients).
- **Choice (`Method`).** Auto uses splice when Formant preserve is off with no formant shift, Transpose is within 2 semitones and the target is the scale; otherwise PSOLA (formant moves, large transposition and MIDI-led). Splice and PSOLA can be forced. **Deviation from the brief**: the brief asked for a crossfade between engines; the choice here is made per setting, not per instant, because the two engines have different latencies of state (marks, reader) that would need a joint hand-over for no listening gain, and a setting change resets the engine. A change of engine resets it.
- **Latency** is the tracker's (unchanged from NT1), per range, for both engines, reported to the host and measured on a click: Low latency 480 samples (10.0 ms at 48 kHz), Quality 1344 (28.0 ms), Full 1728 (36.0 ms). Low latency works below 250 Hz (marks are found on the longest period of the range; tests on a 110 Hz voice).
- **Cost.** No allocation per block (the allocation test toggles Method, Range, Target, Scale and Natural while running). CPU is measured by `RunsFasterThanRealTime` (section 8): well over the floor at every range.

### 11.3 Objective gates (`NeoBuzz.MachineTests/TuneQualityTests.cs`, `tools/PitchEval/TuneQuality.cs`)

Synthetic voices (a glottal source with jitter, shimmer, vibrato, vowel formants) and the shared tracker as judge. The metrics code is shared by the tests and by the evaluation.

| Gate | Limit | Synthetic result |
|---|---|---|
| Transparency, Natural 100 and 75: aligned null | residual at least 30 dB below the signal | -200 dB (bit exact); jitter 2.86 to 2.86 %, shimmer 6.34 to 6.34 % |
| Expression kept, Natural 100 and 75 | 4 - 8 Hz extent at least 90 %, centre within 5 ct | 100 %; centre error 2.7 ct (a voice sung 30 ct sharp) |
| Effect, Natural 0 | vibrato at most 10 %, notes flat within 3 ct, no target flips | 4 % left; worst note mean 0.6 ct; notes [48 52 55 60 55 52] for the sung [48 52 55 60 55 52] |
| Artefacts, Natural 50 and 100 | sub-harmonics at most +1 dB, band above 5 kHz within 0.5 dB, bursts at note changes not above the input's, voiced at least 99 % | +0.1 / 0.0 dB; +0.04 / -0.02 dB; 0 (input 0); 99.8 / 100 % |
| Artefacts, Natural 0 | the same, but the band above 5 kHz within 1.0 dB | +0.5 dB; +0.77 dB; 0 (input 0); 99.7 % |
| Interpolator | flat to 0.40 of the rate | 0.029 dB |
| Latency | per range and engine | 10 / 28 / 36 ms, both engines |

The 1.0 dB allowance for the band above 5 kHz at Natural 0 is a stated relaxation: the hard effect resamples through the semitone jumps of a phrase, and the largest correction in the synthetic phrase is a 5-semitone drop. The measured figure is 0.77 dB.

### 11.4 Editor

The **Natural** knob is first and large (84 px). Moving it by hand rewrites Retune speed, Correction zone, Keep vibrato, Humanize, Transition and Note stickiness in the same undo step; a preset or automation moves only Natural and leaves the fine values as stored. The pitch display adds the **centre line** (a thin line: the note centre the correction works on) and the **correction zone** (a band around the target note, absent at Natural 0). Sections: Natural, Key, Pitch, Fine controls (the six), Voice (Formant, Amount, Transpose, vibrato add), Engine (Range, Method, Input, Transients, Mix).

### 11.5 Presets

Eight: Lead (Hard effect 0, Pop polish 40, Natural 75, Transparent 100), Live (Low Latency Natural: 75 with the 10 ms tracker), Special (MIDI-led, Robot, Thick). Each stores Natural and the six fine values. Lead - Subtle and Lead - Hard Tune are replaced (their settings were in effect Natural 100 and Natural 0).

### 11.6 Measured on VocalSet and before/after NT1

<<VOCALSET>>

### 11.7 Limits of NT2

- A snap lags the real note change by about 150 - 250 ms (the centre window and the settle rule): the price of choosing the target on the centre. A hard effect that must follow a very fast run of notes should have Note stickiness low.
- `flatten` (Keep vibrato under 100 %) shifts by the expression, which is measured by the tracker, so the output carries the tracker's own estimation noise (about 1.1 ct against 0.6 ct on a held note with PSOLA); the flatness gate is therefore stated on 150 - 200 ms means.
- Method Auto is chosen by setting, not by instant (11.2).
- A hard effect through a large jump (more than 3 semitones) resamples with a large ratio for the length of the travel: the band above 5 kHz can lose up to 1 dB there.
- The PSOLA transient dry hold can click on a hard attack inside a note (Transients on).
- NT1's evaluation setting "Natural" and "Subtle" were, through its clamping, Humanize 100 % and Amount 100 % (section 9); the comparison in 11.6 reproduces them as evaluated.
- An optional reference corrector (an open-source real-time pitch corrector run as a measurement and listening reference only) was not added: it was out of the time given to this task.
