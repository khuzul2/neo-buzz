# Composition

Forms, harmony, melody, groove and arrangement, with the calls that write them. Every number is a starting point: render the part, measure it with `analyze`, and adjust.

## Time and grid math

- 4/4 (check `beats_per_bar` in `studio_status`): bar n starts at beat (n − 1) × 4. Positions also take "bar.beat" text: "17.1" is beat 64.
- One beat lasts 60 ÷ BPM seconds; one bar 240 ÷ BPM. Bars for a length: bars = seconds × BPM ÷ 240.
- Note values in beats: whole 4, half 2, quarter 1, 8th 0.5, 16th 0.25, 32nd 0.125, dotted 8th 0.75, dotted quarter 1.5, 8th triplet 1/3 (0.3333), 16th triplet 1/6 (0.1667).
- 16-step grids below: step s starts at beat (s − 1) × 0.25 of its bar.
- Milliseconds to beats: beats = ms × BPM ÷ 60000. 10 ms is 0.02 beats at 120 BPM, 0.029 at 174.
- Ticks per beat (default 4) is only the Pattern View's grid. Notes can start between ticks, so swing and triplets need no change.

| BPM | 70 | 80 | 90 | 100 | 110 | 124 | 130 | 140 | 174 |
|---|---|---|---|---|---|---|---|---|---|
| Seconds per bar | 3.43 | 3.00 | 2.67 | 2.40 | 2.18 | 1.94 | 1.85 | 1.71 | 1.38 |
| Bars per minute | 17.5 | 20 | 22.5 | 25 | 27.5 | 31 | 32.5 | 35 | 43.5 |

## The tools, in order

1. Tempo: `song_set(tempo_bpm=124)`.
2. Sections: `arrangement_markers(set=[{"name": "Intro", "at": "1.1"}, {"name": "Drop", "at": "41.1"}])`. A section runs to the next marker; `render(section="Drop")` renders one.
3. Patterns: one per part and section role, length in beats. `pattern_create(machine="Bass", name="Drop", length_beats=16)`. Duplicate for variations: `pattern_duplicate(machine="Drums", pattern="Beat", new_name="Beat fill")`.
4. Pitches without arithmetic slips:
   - `theory(op="chord", chord="Am9", octave=3)`: a chord's pitches, spelled.
   - `theory(op="progression", key="A minor", progression="i VI III VII", octave=3, beats_per_chord=4)`: chords voice-led, plus notes ready for `notes_write`.
   - `theory(op="scale", key="D dorian")`, `theory(op="key", key="F minor")` (signature, diatonic triads and sevenths).
   - `theory(op="degree", key="A minor", pitches=["C4", "E4"], steps=2)`: moves notes along the scale.
5. Notes: `notes_write(machine="Chords", pattern="Drop", notes=[{"pitch": "A3", "start": 0, "length": 4, "velocity": 90}], mode="replace")`. Pitches are MIDI numbers or names (60 = C4). Chords are notes with the same start.
6. Edits: `notes_transform(machine="Lead", pattern="Hook", transforms=[{"op": "quantize", "grid": 0.25, "strength": 0.8, "swing": 0.48}])` (transpose, quantize, humanize, velocity, shift, stretch, reverse, invert, legato, staccato; on a selection by range or pitch; one undo step).
7. Timeline: `arrangement_place(machine="Bass", pattern="Drop", at="41.1", repeat_every_beats=16, times=8)`. A placed pattern plays until it ends or the lane's next pattern starts.
8. Movement: `automation_write(machine="Chords", pattern="Build", param="F1 cutoff", points=[{"at": 0, "value": "400 Hz"}, {"at": 32, "value": "8 kHz"}])`. Automation lives in a pattern and plays only where that pattern is placed.
9. Check: `notes_read`, `theory(op="fit", key="A minor", notes=[…])`, `render(section="Drop")`, `analyze(source="r3", metrics=["key", "tempo", "loudness"])`.

The Session View grid (`session_clip_set`, `session_scene_set`, `session_get`) is good for sketching loops as scenes. Renders play the arrangement, so the finished song lives there.

## Forms by genre

Sections are multiples of 4 bars, almost always 8, 16 or 32. Something changes every 8 bars; something big every 16 to 32.

| Genre | BPM | Length | Typical form (bars) |
|---|---|---|---|
| House | 120–128 | club 5–7 min, edit 3–4 | Intro 16–32 · Groove 16 · Build 8 · Drop 32 · Breakdown 16–32 · Build 8 · Drop 16–32 · Outro 16–32 |
| Techno | 125–135 (hard: 140–150) | 6–8 min, edit 4–5 | Intro 32 · Build 16 · Main 32 · Break 16 · Main 32 · Outro 32 |
| Drum and bass | 170–176 | 4–6 min | Intro 32 · Drop 32 · Breakdown 16–32 · Build 8 · Drop 32 · Outro 16–32 |
| Synthwave | 85–118 | 3.5–5 min | Intro 8 · Verse 16 · Chorus 16 · Verse 16 · Chorus 16 · Bridge or solo 16 · Chorus 16 · Outro 8 |
| Hip-hop | boom bap 85–95; trap 130–150 (half-time feel) | 2.5–3.5 min | Intro 4–8 · Verse 16 · Hook 8 · Verse 16 · Hook 8 · Bridge 8 · Hook 8 · Outro 4 |
| Ambient | 60–90, or free | 4–10 min | A 32 · B 32 · C 32 · A′ 16 · Fade 16 |
| Pop | 100–128 | 2.5–3.5 min | Intro 4 · Verse 16 · Pre 4 · Chorus 8 · Verse 8 · Pre 4 · Chorus 8 · Bridge 8 · Chorus 16 · Outro 4 |
| Lo-fi | 70–90 | 2–3 min | Intro 4 · A 16 · B 16 · A′ 16 · Outro 4 |

- Pop: the first chorus lands by 0:45–1:00 (bar 25 at 110 BPM is 52 s).
- Club tracks: 16–32 bars of drums alone at both ends so DJs can mix.
- A shorter brief: keep the order, halve sections (16 → 8), keep at least one full chorus or drop.

## Mood

| Mood | Mode | Progressions | Tempo | Register and sound |
|---|---|---|---|---|
| Melancholic | natural minor (aeolian) | i–VI–III–VII, i–iv–VI–V | lower end of the genre | mid and low, long releases, darker filters |
| Uplifting, euphoric | major, lydian | I–V–vi–IV, IV–V–vi | upper end | bright leads high, sus2 and add9 |
| Dark | phrygian, minor | i–bII, i–iv over a pedal | any | low register, detuned, filters closed |
| Dreamy | lydian, major 7ths | Imaj7–IVmaj7, I–II (lydian) | slow | chorus, long reverb, maj7 and 9th chords |
| Tense | harmonic minor | i–V7, vii°7, rising bass | any | ostinatos, rising filters |
| Nostalgic | major with borrowed chords | I–iv, I–bVI–bVII–I | mid | tape saturation, detune |
| Aggressive | minor, power chords (root and fifth) | i–bVI–bVII | fast | distortion, short envelopes |
| Chill, warm | dorian, minor 7ths | i7–IV7 (dorian), ii7–V7–Imaj7 | slow | soft attacks, swing |

## Harmony

**Functions.** Major: tonic I, vi, iii; predominant IV, ii; dominant V, vii°. Minor: tonic i, III; predominant iv, ii°, VI; dominant v, V (from harmonic minor), VII (subtonic). Cadences: authentic V–I (closed), plagal IV–I (soft), half → V (open), deceptive V–vi (surprise).

| Progression | C major / A minor | Feel | Where |
|---|---|---|---|
| I–V–vi–IV | C G Am F | open, anthemic | pop, EDM |
| vi–IV–I–V | Am F C G | bittersweet | pop, house, trance |
| I–vi–IV–V | C Am F G | nostalgic | synthpop |
| IV–V–iii–vi | F G Em Am | yearning | pop |
| ii7–V7–Imaj7 | Dm7 G7 Cmaj7 | resolved, jazzy | lo-fi, hip-hop, liquid DnB |
| Imaj7–vi7–ii7–V7 | Cmaj7 Am7 Dm7 G7 | smooth loop | lo-fi, deep house |
| i–VI–III–VII | Am F C G | epic minor | synthwave, trance, DnB |
| i–VII–VI–VII | Am G F G | driving | synthwave, rock |
| i–VI–VII | Am F G | rising | EDM, hip-hop |
| i–iv–v–i | Am Dm Em Am | modal, cool | techno, ambient |
| i–iv–V7–i | Am Dm E7 Am | dramatic cadence | DnB, cinematic |
| i–VII–VI–V | Am G F E | Andalusian, dark | dark pop |
| i7–IV7 (dorian) | Am7 D7 in A dorian | groove | house, funk |
| i–bII (phrygian) | Am Bb | menacing | techno |
| I–II (lydian) | C D | floating | ambient, film |

Get the pitches from `theory(op="progression", key="C major", progression="I V vi IV", octave=3)`: the result's notes go straight into `notes_write`.

**Registers.**

| Part | MIDI | Names | Rule |
|---|---|---|---|
| Sub bass | 24–40 | C1–E2 | one note at a time |
| Bass | 28–55 | E1–G3 | roots on chord changes |
| Chords, pads | 48–79 | C3–G5 | lowest chord note C3–A3 |
| Lead, melody | 60–84 | C4–C6 | above the chords' top note or between their phrases |
| Arps, plucks, bells | 64–96 | E4–C7 | thin and bright |

- Below C3 (48) don't stack thirds: they turn to mud. Below C2 (36) only roots, fifths and octaves.
- Tune the kick to the key: fundamental 45–65 Hz (F#1–C2, MIDI 30–36).

**Voice leading.** Keep common tones; move the other voices 1–2 semitones; the top voice moves by step. `theory` does this by default (`voice_leading` true). `voicing="open"` or `"drop2"` spreads close chords for pads and keys. Slash chords give a stepwise bass: C G/B Am (I V6 vi).

**Tension and release.**
- Dominant V7 → I. In minor use V or V7 from harmonic minor (E7 → Am).
- Secondary dominant: V7/V (D7 → G in C).
- Borrowed from the parallel minor in a major key: iv (Fm in C), bVI (Ab), bVII (Bb).
- Suspensions: sus4 resolving to the third on the next chord change; sus2 for openness without resolution.
- Pedal point: hold the tonic or dominant in the bass while the chords move; release by moving the bass.
- Extensions: 7ths and 9ths soften (house, lo-fi, liquid DnB); 11 over minor chords; 13 over dominants. Avoid a natural 11 over a major chord (it clashes with the third).
- Harmonic rhythm: one chord per bar (pop, house), per 2 bars (synthwave, EDM verses), per 4–8 bars or a drone (techno, ambient). Doubling it in the last bars of a build adds tension.
- Last-chorus lift (pop): up 1 or 2 semitones with `notes_transform(machine=…, pattern=…, transforms=[{"op": "transpose", "semitones": 2}])`.

## Melody

- Range: a 5th to an octave per phrase; a 10th to 12th over the song. The chorus sits 3–7 semitones above the verse.
- Motion: 60–80 % steps (1–2 semitones). After a leap over 4 semitones, step back the other way.
- Chord tones on strong beats (beats 1 and 3, or the first beat of each chord); passing and neighbour tones between them.
- Contour: an arch with its peak about two thirds through the phrase; one highest note per section.
- Phrases: 2 + 2 bars (call and response) inside 4-bar phrases. The first 4 bars end on degree 2 or 5 (open), the answer on 1 or 3 (closed).
- Rhythm: leads rest 25–50 % of the time. Repeat rhythms more than pitches. Anticipate a beat by a 16th for push.
- Hooks: a 1–2 bar motif of 3–6 notes, heard at least 3 times per section, changed on the 4th (AAAB or AABA).

**Developing a motif** with `notes_transform(machine="Lead", pattern="Hook", transforms=[…], range_start=4, range_end=8)`:

| Technique | Transform |
|---|---|
| Sequence up the scale | `{"op": "transpose", "scale_steps": 2, "key": "A minor"}` |
| Octave or interval shift | `{"op": "transpose", "semitones": 12}` |
| Inversion | `{"op": "invert", "axis": "A4"}` |
| Retrograde | `{"op": "reverse"}` |
| Augmentation, diminution | `{"op": "stretch", "factor": 2}`, `{"op": "stretch", "factor": 0.5}` |
| Displacement | `{"op": "shift", "beats": 0.5}` |
| Legato or staccato | `{"op": "legato", "gap": 0}`, `{"op": "staccato", "factor": 0.5}` |
| Crescendo | `{"op": "velocity", "ramp": [70, 115]}` |

Copy before you vary: `pattern_duplicate`, then transform the copy. Check the result: `theory(op="fit", key="A minor", notes=[…])` with the notes from `notes_read`; at least 90 % of the time in key unless the chromatic notes are deliberate.

## Rhythm and groove

**Pitches for drums.** Drum machines that follow General MIDI: kick 36 (C2), rim 37, snare 38, clap 39, closed hat 42, open hat 46, crash 49, ride 51. Synth drums built on Neo Poly (sound-design guide) play the pitch written: the kick at A1 (33) or in key, snare G3, hats any pitch. Give each drum voice its own machine and pattern when it needs its own sound or level.

**Grids** (16 steps per bar, `x` = hit, `.` = rest; step s is beat (s − 1) × 0.25):

| Genre | Voice | Steps 1–16 | Velocity |
|---|---|---|---|
| House 124 | Kick | `x...x...x...x...` | 115–127 |
| | Clap | `....x.......x...` | 100–110 |
| | Closed or open hat | `..x...x...x...x.` | 85–100 |
| | Shaker | `xxxxxxxxxxxxxxxx` | 40–70, offbeats +15 |
| Techno 130 | Kick | `x...x...x...x...` | 120–127 |
| | Offbeat hat | `..x...x...x...x.` | 90–105 |
| | 16th hats | `xxxxxxxxxxxxxxxx` | 50–80, accents every 3 steps for polyrhythm |
| | Perc | `...x..x....x..x.` | 70–90 |
| | Rolling bass | `.xxx.xxx.xxx.xxx` | 90–110 |
| DnB 174 | Kick | `x.........x.....` | 115–127 |
| | Snare | `....x.......x...` | 110–120 |
| | Ghost snare | `.......x.x....x.` | 30–50 |
| | Hats | `x.x.x.x.x.x.x.x.` | 60–90 |
| Synthwave 100 | Kick | `x.......x.......` (or four on the floor) | 115–127 |
| | Snare (big reverb) | `....x.......x...` | 110–120 |
| | Hats | `x.x.x.x.x.x.x.x.` | 70–90 |
| | Bass (octave 8ths or 16ths) | `x.x.x.x.x.x.x.x.` | 90–110 |
| Boom bap 90 | Kick | `x......x..x.....` | 110–127 |
| | Snare | `....x.......x...` | 105–120 |
| | Hats | `x.xxx.xxx.xxx.x.` | 60–95 |
| Trap 140 | Kick (808 follows it) | `x......x..x.....` | 110–127 |
| | Snare or clap (half-time) | `........x.......` | 110–120 |
| | Hats | `x.x.x.x.x.x.x.x.` plus rolls | 60–100 |
| Lo-fi 80 | Kick | `x.........x.....` | 90–110 |
| | Snare (late) | `....x.......x...` | 80–100 |
| | Hats | `x.x.x.x.x.x.x.x.` | 50–80 |
| Pop 110 | Kick | `x.......x.x.....` | 110–125 |
| | Snare or clap | `....x.......x...` | 105–120 |
| | Hats | `x.x.x.x.x.x.x.x.` | 70–95 |

Ambient: no drums, or a soft kick on step 1 and shakers.

- Trap hat rolls: on the last beat of every 2nd or 4th bar, 16ths (0.25), 16th triplets (1/6) or 32nds (0.125), velocity ramping up.
- Fills: the last bar of each 8-bar phrase. Snare 16ths on beat 4 with `{"op": "velocity", "ramp": [70, 120]}`, or drop the kick for the last beat.
- Write one bar or a 4-bar pattern with its fill, then repeat it: `arrangement_place(machine="Kick", pattern="Beat", at="1.1", repeat_every_beats=16, times=8)`.

**Swing.** `notes_transform` quantize with `swing` delays every second grid line: 0 is straight, 1 a full triplet. From the MPC's percentages: swing = (percent − 50) × 6 ÷ 100.

| MPC swing | 50 % | 54 % | 58 % | 62 % | 66 % |
|---|---|---|---|---|---|
| `swing` value | 0 | 0.24 | 0.48 | 0.72 | 0.96 |
| Late 16th at (beats into the 8th) | 0.25 | 0.27 | 0.29 | 0.31 | 0.33 |

**Humanize** with `{"op": "humanize", "timing": 0.01, "velocity": 8, "seed": 1}`; timing is in beats.

| Genre | Swing (16ths) | Timing | Velocity |
|---|---|---|---|
| House, garage | 0.24–0.48 | 0–0.005 | ±6, accents on offbeats |
| Techno | 0–0.24 | 0 | ±4, accent patterns |
| DnB | 0–0.24 | 0–0.005 | ±8, ghost notes |
| Synthwave, pop | 0 | 0–0.005 | ±5 |
| Boom bap | 0.48–0.72 | 0.01–0.02 | ±10 |
| Trap | 0 | 0 | ±8 on hats |
| Lo-fi | 0.72–0.96 | 0.02–0.03 | ±12; snare 0.02–0.03 late |
| Ambient | — | 0.02–0.05 | ±15 |

Accents: `{"op": "velocity", "accent": "x.x.", "grid": 0.25, "amount": 20}` lifts the steps marked x.

## Arrangement energy

Plan an energy curve (1–10) per section, then decide the layers. Neighbouring sections differ by at least two layers or one big move.

| Section (club) | Energy | Layers | Moves |
|---|---|---|---|
| Intro | 3 | kick, hats, percussion | music filtered (F1 cutoff 300–500 Hz) |
| Groove | 5 | + bass, clap | a new percussion part after 8 bars |
| Build | 6 → 8 | + chords opening, riser, snare roll | kick out for the last bar or beat |
| Drop | 9–10 | everything, the hook | full filter, widest stereo |
| Breakdown | 4 | pad, chords, hook motif; no kick, no bass | long reverb, the melody alone |
| Outro | 3 | drums, then less | remove a layer every 8 bars |

| Section (song) | Energy | Layers |
|---|---|---|
| Verse | 5 | drums light, bass, keys, lead or vocal line low |
| Pre-chorus | 6–7 | more percussion, rising bass or harmonic rhythm |
| Chorus | 8–9 | everything, melody 3–7 semitones higher, wider pads |
| Bridge | 4–6 | new chord, half the drums |

Moves, with tools:
- Filter build over 8 bars (a 32-beat pattern): `automation_write(machine="Chords", pattern="Build", param="F1 cutoff", points=[{"at": 0, "value": "400 Hz"}, {"at": 32, "value": "8 kHz"}])`.
- Snare roll: quarters, then 8ths, then 16ths over 8 bars, velocity ramp 70 → 120.
- Riser: a noise sweep (preset "FX — Noise Sweep" on Neo Poly) over the build.
- Reverb throw: freeze the send reverb for the last beat, `automation_write(machine="Hall", pattern="Build", param="Freeze", shape="step", points=[{"at": 28, "value": "On"}, {"at": 31.75, "value": "Off"}])`.
- Fade: `automation_write(machine="Pad", pattern="Outro", param="Volume", points=[{"at": 0, "value": "-6 dB"}, {"at": 64, "value": "-40 dB"}])`.
- A beat of silence before the drop: clear the last beat of each part's build pattern with `notes_write(machine=…, pattern="Build", notes=[], mode="replace_range", range_start=28, range_end=32)`.

Measure the contrast: render each section and compare integrated loudness. The drop or chorus should read 2–6 LU above the breakdown or verse; under 1 LU, it doesn't lift.

## Templates

Markers at the bars shown (`arrangement_markers(set=[{"name": "Intro", "at": "1.1"}, …])`). Beats = (bar − 1) × 4. Presets named here belong to the Neo machines (see the sound-design guide).

### House

- 124 BPM. Minor keys: A, F, G minor; also dorian. Chords as minor 7ths and 9ths, often one stab rhythm repeated.
- Form, 128 bars (4:08): Intro 1 · Groove 17 · Build 33 · Drop 41 · Breakdown 73 · Build 2 89 · Drop 2 97 · Outro 113.
- Drums: the house grid, swing 0.24–0.48. Bass: offbeat 8ths or a syncopated 1-bar riff on the root.
- Sounds: chords from "Keys — Organ" or the chord-stab recipe; bass "Bass — Classic Sub"; pad "Pad — Warm Strings".

### Techno

- 130 BPM. F or A minor, phrygian. One- or two-note riffs; harmony mostly implied by the bass and a drone.
- Form, 160 bars (4:55): Intro 1 · Build 33 · Main 49 · Break 81 · Main 2 97 · Outro 129.
- Drums: the techno grid, rolling bass between kicks. Change a filter, a percussion part or an accent every 8 bars.
- Sounds: "Bass — Acid" for acid lines; percussion from the Neo Poly drum recipes; a long "Hall — Cathedral" send for rumble.

### Drum and bass

- 174 BPM. Minor keys (F, E, G minor); liquid uses m9 and maj7 chords.
- Form, 160 bars (3:41): Intro 1 · Drop 33 · Breakdown 65 · Build 89 · Drop 2 97 · Outro 129.
- Drums: the two-step grid with ghost snares. Bass: long sub notes plus a mid-bass layer ("Bass — Reese").
- Sounds: pads "Pad — Glass"; leads short and high.

### Synthwave

- 100 BPM (85–118). A, E or F# minor. i–VI–III–VII or i–VI–VII, one chord per bar or two.
- Form, 112 bars (4:29 at 100): Intro 1 · Verse 9 · Chorus 25 · Verse 2 41 · Chorus 2 57 · Bridge 73 · Chorus 3 89 · Outro 105.
- Short form (48 bars, 2:05 at 92 BPM): Intro 4 · Verse 8 · Chorus 8 · Verse 8 · Chorus 8 · Bridge 8 · Outro 4.
- Drums straight, snare with a big plate or hall. Bass: root 8ths alternating octaves, or a 16th arpeggio.
- Sounds: "Pad — Evolving PWM", "Lead — Smooth Saw", "Keys — Tine Piano" (Neo FM6), "Hall — Concert".

### Hip-hop

- Boom bap 90 BPM (swing 0.48–0.72); trap 140 with the half-time snare. Minor keys, jazz chords (m7, m9, maj7), 2–4 bar loops.
- Form, 72 bars (3:12 at 90): Intro 1 · Verse 5 · Hook 21 · Verse 2 29 · Hook 2 45 · Bridge 53 · Hook 3 61 · Outro 69.
- Bass: sub notes following the kick; trap 808s glide (Neo Poly with "Glide mode" Legato, overlapping notes).
- Sounds: "Keys — Tine Piano", "Bass — Classic Sub", "Pluck — Marimba".

### Ambient

- 70 BPM or free time. Major, lydian or dorian; drones and sus2 chords; one chord per 4–8 bars.
- Form, 128 bars (7:19 at 70): A 1 · B 33 · C 65 · A′ 97 · Fade 113.
- One change at a time: a voice enters, a filter opens, a reverb freezes. Velocities soft (50–90), long notes overlapping.
- Sounds: "Pad — Vowel Choir" (Neo Wave), "Bell — Glass Bell" (Neo FM6), "Ambient — Shimmer" (Neo Verb).

### Pop

- 110 BPM (100–128). Major keys or relative minor; I–V–vi–IV and relatives; chords change every bar.
- Form, 80 bars (2:55): Intro 1 · Verse 5 · Pre 21 · Chorus 25 · Verse 2 33 · Pre 2 41 · Chorus 2 45 · Bridge 53 · Chorus 3 61 · Outro 77.
- The hook in the chorus, a variant of it in the intro. Final chorus can lift 1–2 semitones.
- Sounds: "Pluck — Bright", "Pad — Warm Strings", "Lead — Smooth Saw".

### Lo-fi

- 80 BPM (70–90). ii7–V7–Imaj7 and Imaj7–vi7–ii7–V7, m9 chords; heavy swing and humanize.
- Form, 56 bars (2:48): Intro 1 · A 5 · B 21 · A′ 37 · Outro 53.
- Sounds: "Keys — Tine Piano" through Neo Drive "Tape — Cassette", "Pluck — Marimba"; low-passed drums.

## Checklist

- [ ] Tempo and form match the brief: `studio_status` and `arrangement_markers()` list every section, each a multiple of 4 bars.
- [ ] Every part plays where it should: `arrangement_get` and `pattern_list` show the placements and note counts.
- [ ] Melodic parts are in key: `theory(op="fit", key=…, notes=[…])` reads at least 0.9 of the time in key for each.
- [ ] Registers are right: `notes_read` shows the bass below C3 and no stacked thirds below C3.
- [ ] The render agrees: `analyze(source=…, metrics=["key", "tempo"])` finds the key and tempo you wrote.
- [ ] Sections contrast: `render(section=…)` of the drop or chorus and the breakdown or verse, measured with `analyze(source=…, metrics=["loudness"])`, differ by 2–6 LU.
- [ ] No unintended silence: `analyze(source=…, metrics=["dynamics"])` shows no gap in `rms_over_time` except planned breaks.
- [ ] Automation plays: `automation_read(machine=…, pattern=…)` lists it and `arrangement_get` shows the pattern placed.
- [ ] Saved: `song_save(path=…)`.
