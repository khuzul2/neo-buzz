## How it sounds

A clean digital delay that can turn into a tape echo: two lines, their time in ms or a note value of the song's tempo, with a low cut, a high cut and soft saturation inside the loop, so every repeat is thinner, darker or grittier than the one before. An echo comes exactly `Time` after the sound (or 60 / BPM x the note value); each repeat is `Feedback` times the one before, and the line cannot run away even at 99 %.

| Mode | Where the echoes go | Use it for |
|---|---|---|
| Stereo | each side echoes on its own side | echo on a vocal, lead or snare; `Offset` makes the right side later, for width |
| Ping-pong | the input, summed to mono, enters the left line; echoes bounce left, right, left | throws on a send, wide rhythmic echoes |
| Cross | each side's echo feeds the other side's line | stereo material that should bounce only where it already sits to one side |

Time changes glide (small ones bend the pitch like tape) or crossfade (large ones). Zero latency; at `Mix` 0 % the input passes bit for bit. Every control has a How and a Sound sentence: `machine_describe` returns them with the id, unit and range, and `param_set` takes the name or the id.

## Use it for

- Echoes and space on a send (`Mix` 100 %) or an insert (15-35 %), slapback and doubling, dub and tape echoes, ping-pong throws, ducked vocal echoes that bloom in the gaps.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Sync | 1/8 | a note value of the song's tempo; Free uses `Time` |
| Feel | Dotted | the classic dotted eighth; Straight is square, Triplet swings |
| Feedback | 35% | three or four audible repeats; 75 % for long dub tails |
| Low cut | 250 Hz | each repeat thinner, clear of the voice's body |
| High cut | 5 kHz | each repeat darker, so the echoes sit behind the dry sound |
| Duck | 12 dB | echoes step aside while the input plays |
| Mix | 100% | on a send; 15-35 % as an insert |

## Starting points

- "Mix — Vocal Echo (Ducked)", "Mix — Quarter Note Echo", "Mix — Dotted Eighth Throw", "Mix — Slapback", "Mix — Doubler (Wide)".
- On a send: "Send — Ping-Pong Eighths", "Send — Long Ambient Wash". Dub: "Dub — Tape Echo (Saturated)". Textures: "Texture — Comb Resonator" (5 ms at 85 %), "Texture — Lo-Fi Telephone Echo".
- Slapback by hand: Sync Free, `Time` 110 ms, `Feedback` 8 %, `High cut` 5 kHz, `Mix` 30 %.

## Pitfalls

- `Time` is ignored while `Sync` is not Free; `Feel` only applies with `Sync`.
- Ping-pong sums the input to mono: a sound panned hard left enters at half level. Use Cross to keep the image.
- `Low cut` at its bottom (20 Hz) is off, and `High cut` at its top (20 kHz) is open.
- `Mix` is a straight crossfade: an insert at 30 % puts the dry sound 3 dB down. For full dry level use a send at 100 %.
- Near +-99 % feedback the tail holds at about the knee and its peaks round off; a long `Time` keeps ringing after the song stops, so renders need a `tail_seconds` that covers it.

## Pairs well with

- Neo Verb after it (a short plate on the echoes) or on a parallel send; Neo EQ to shape the return; Neo Comp on the dry source, so the ducked echoes stay clear. For wobble, Neo Tape before it, or Neo Ensemble for a modulated repeat.

## Successor

Neo Delay replaces Jeskola Delay, Jeskola Cross Delay, Jeskola NiNjA dElaY and FSM PanzerDelay: `machine_replace(machine="Delay")` carries the settings over in their units.

## Checklist

- [ ] `machine_describe(machine="Echo")` shows the How and Sound of each control you changed; `param_set(machine="Echo", changes=[{"param": "Feedback", "value": "35%"}])` takes it.
- [ ] `render(probes=["Echo"])`, then `analyze` its spectrum and stereo: the repeats are darker and thinner than the dry sound and the image is as intended.
- [ ] `machine_readings(machine="Echo")` gives `rms_db` and `peak_db`: the wet level sits behind the dry source, with no clipping at high feedback.
- [ ] With ducking, `analyze` the echoes while the source plays: they are `Duck` dB lower than in the gaps.
