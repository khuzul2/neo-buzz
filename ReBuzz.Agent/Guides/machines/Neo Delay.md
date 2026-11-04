## How it sounds

A clean digital delay that can turn into a tape echo: two lines, their time in ms or a note value of the song's tempo, with a low cut, a high cut and tape saturation inside the loop, so each repeat is thinner, darker or grittier than the one before.

| Mode | Where the echoes go | Use it for |
|---|---|---|
| Stereo | each side echoes on its own side (a centred sound stays centred) | the classic echo on a vocal, a lead, a snare; with `Offset` the right side later, for width |
| Ping-pong | the input, summed to mono, enters the left line; the echoes bounce left, right, left | throws on a send, wide rhythmic echoes |
| Cross | each side's echo feeds the other side's line: a sound on the left bounces, a centred one stays centred (unless `Offset` makes the lines differ) | stereo material that should bounce only where it already is to one side |

Measured: an echo comes exactly `Time` (rounded to a sample) after the sound, or 60 ÷ BPM × the note value (1/8 dotted at 128 BPM: 351.6 ms); each repeat is `Feedback` times the one before. `Feedback` reaches ±99 % and the line can't run away: what enters it saturates softly (never more than 6 dB over the knee), so at 99 % the echoes build up, level off and die away once the input stops.

## Signal flow

Input → the line (Stereo, Ping-pong or Cross routing) → read at the delay → `High cut` (12 dB/oct, two real poles; open at its top) → `Low cut` (12 dB/oct Butterworth; off at its bottom) → the echo. The echo goes back through `Feedback` and the tape saturator (`Drive` lowers its knee) into the line, and out through `Width` and the ducker (`Duck`, `Duck threshold`, `Duck release`: the input's envelope turns the echoes down) to `Mix` against the dry signal, then `Output`. Zero latency; at `Mix` 0 % the input passes bit for bit.

Time changes: a small change (under 50 ms, such as a tempo change) glides, bending the pitch at most 20 % (about 4 semitones down, 3 up), like tape; a larger one (another note value) crossfades over 40 ms, without a bend. `Mod depth` and `Mod rate` add tape wow (the right line a quarter cycle on).

## How to use it

- On a send (the usual way in a mix): `machine_add(gear="Neo Delay", name="Echo")`, connect the source to it with the send level as the connection's gain, `Mix` 100 %, then a preset (`preset_load(machine="Echo", preset="Send — Ping-Pong Eighths")`).
- As an insert: `machine_add(gear="Neo Delay", name="Echo", insert_after="Vocal")`, `Mix` 15–35 %.
- Times: `Sync` and `Feel` follow the song's tempo (1/8 dotted is the classic); `Sync` Free uses `Time`. `Offset` makes the right line longer or shorter (+50 %: a dotted relation; a few % for width).
- Filter the repeats so they sit behind the dry sound: `Low cut` 150–400 Hz, `High cut` 3–7 kHz. Each repeat passes them again, so they darken and thin as they fade.
- Ducking keeps echoes out of the way of the phrase and lets them bloom in the gaps: `Duck` 6–18 dB with `Duck threshold` a few dB under the source's peaks (−24 dB for a vocal peaking at −12 dBFS), `Duck release` 200–500 ms.
- Check: render with a probe on the delay (`render(probes=["Echo"])`) and `analyze` its spectrum and stereo; at 100 % wet on a send, its level sets the effect's depth.

Vocal echo on a send (quarter notes, ducked):

| Parameter | Value | Why |
|---|---|---|
| Mode | Stereo | |
| Sync | 1/4 | |
| Feel | Straight | |
| Feedback | 30.0% | three or four audible repeats |
| Low cut | 250 Hz | clear of the voice's body |
| High cut | 5.00 kHz | darker than the dry voice, so it sits behind it |
| Duck | 12.0 dB | out of the way while she sings |
| Duck threshold | −24.0 dB | |
| Mix | 100% | a send |

Dub echo (insert or send):

| Parameter | Value | Why |
|---|---|---|
| Mode | Ping-pong | |
| Sync | 1/8 | |
| Feel | Dotted | |
| Feedback | 75.0% | long, building tails |
| Low cut | 350 Hz | each repeat thinner |
| High cut | 2.50 kHz | each repeat darker |
| Drive | 12.0 dB | the repeats saturate like tape |
| Mod depth | 0.80 ms | a little wow |
| Mix | 35% | |

Slapback (rockabilly, 80s vocals):

| Parameter | Value | Why |
|---|---|---|
| Sync | Free | |
| Time | 110 ms | |
| Feedback | 8.0% | one repeat |
| High cut | 5.00 kHz | |
| Mix | 30% | |

## Gotchas

- `Time` is ignored while `Sync` is not Free; `Feel` only applies with `Sync`.
- Ping-pong sums the input to mono first: a sound panned hard left enters at half level. Use Cross to keep the input's image.
- With `Feedback` near ±99 % the line saturates: the tail holds at about the knee's level and its peaks round off. Lower `Drive`'s knee (raise `Drive`) for that on purpose, or lower `Feedback`.
- `Low cut` at its bottom (20 Hz) is off, not 20 Hz; `High cut` at its top (20 kHz) is open.
- `Mix` is a straight crossfade: as an insert at 30 % the dry sound is 3 dB down. For an insert at full dry level, put Neo Delay on a send at 100 % instead.
- A long `Time` at a high `Feedback` keeps ringing long after the song stops; renders need a `tail_seconds` that covers it.

## Preset map

| Preset | What |
|---|---|
| "Mix — Dotted Eighth Throw" | 1/8 dotted, filtered, 25 % |
| "Mix — Quarter Note Echo" | 1/4, gentle |
| "Mix — Slapback" | 110 ms, one repeat |
| "Mix — Vocal Echo (Ducked)" | 1/4, ducked 12 dB under the voice |
| "Mix — Doubler (Wide)" | 18 ms left, 29 ms right, no feedback, wide |
| "Mix — Mono Echo (Narrow)" | 1/8, mono echoes (Width 0 %) |
| "Send — Ping-Pong Eighths" | 1/8 ping-pong, 100 % wet |
| "Send — Long Ambient Wash" | cross, a dotted quarter and a half note, wow, 100 % wet |
| "Ping-Pong — Wide Dotted Quarter" | 1/4 dotted ping-pong |
| "Cross — Triplet Bounce" | 1/8 triplet, the right line half as long again |
| "Dub — Tape Echo (Saturated)" | 1/8 dotted, 72 %, dark and driven, wow |
| "Dub — Runaway Feedback (Ducked)" | 95 % ping-pong, ducked, saturated |
| "Tape — Analog Wobble (Dark)" | 1/4, dark, deep wow |
| "Rhythm — Sixteenth Stutter" | 1/16, 60 % |
| "Texture — Comb Resonator" | 5 ms at 85 %: a metallic comb at 200 Hz |
| "Texture — Lo-Fi Telephone Echo" | band-limited 800 Hz – 2.5 kHz, driven |
