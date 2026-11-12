# Genres and moods

Forms by genre, the mood table and a template for each genre: tempo, key, structure, sounds. The calls that write them are in the composition guide (`guide(topic="composition")`).

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

- [ ] The form matches the template: `arrangement_markers()` lists each section at the template's bars, and `arrangement_get` shows every part placed.
- [ ] The tempo sits in the genre's range: `studio_status` shows it, and `analyze(source=…, metrics=["tempo"])` reads it back.
- [ ] The mood's mode and register hold: `analyze(source=…, metrics=["key"])` finds the key you chose and `theory(op="fit", key=…, notes=[…])` reads the melodic parts in key.
