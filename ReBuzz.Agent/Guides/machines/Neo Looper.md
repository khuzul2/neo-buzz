## How it sounds

A live looper that sits on a track: it records what comes in, plays it back in a loop and lets you stack more on top, in time with the song, with no latency. It is made to be played from a pad or a footswitch: seven switch parameters are triggers (a press is a change from 0 to 1; map each to a MIDI control). `Record` steps the cycle: empty, recording, playing, overdubbing, playing. `Stop`, `Play`, `Undo`, `Redo`, `Clear` and `Multiply` do the rest.

- `Sync` set to Sync: `Record`, `Stop`, `Play` and `Multiply` wait for the next bar (or the next beat with `Quantise` Beat) of the song, and the first take is rounded to whole bars at the song's tempo (a bar is the song's own bar). `Undo`, `Redo` and `Clear` act at once. The loop follows the song position, so a seek or a song loop moves it with the song; after a tempo change it keeps its recorded speed and restarts in time at the bar (it is not stretched). With the song stopped, Sync works like Free. `Sync` set to Free: a trigger acts in the block it arrives in (up to 5 ms late) and the first take is exactly as long as you held it.
- `Multiply` makes the loop 2, 3 or 4 times as long (`Multiply by`), repeating its content, so overdubs can vary over the longer cycle. It does nothing while an overdub is open or when the longer loop would not fit `Max loop`.
- An overdub keeps `Feedback` of what was there (100% keeps everything). `Undo` takes back the last pass, multiply or clear, up to `Undo levels` (1-8); `Redo` brings it back until the next new pass. The history is not saved with the song, the loop is.
- `Monitor` lets the input through beside the loop. `Reverse` plays the loop backwards. `Half speed` plays an octave down, exactly twice as long; an overdub made at half speed comes back an octave up at normal speed. `Crossfade` (1-50 ms) hides the loop point and every punch in and out.
- `Max loop` (15, 30, 60, 120 s) and `Undo levels` set the memory (30 s and 4 levels take 69 MB at 48 kHz; the pool never passes 320 MB, so a long loop gets fewer undo levels). They change only while the loop is empty.
- The loop's audio is saved with the song, bit for bit, about 0.3 MB for each second of loop. Presets set the behaviour and never the audio.
- `Amount` (0-200%) scales the loop's level against the input; `Amount` 0 or `Mix` 0 passes the input bit for bit.

## Use it for

- Building a loop live from a pad or footswitch: a drum pattern, a chord progression, then layers on top. In Sync it stays on the song's grid; in Free it is a sketch pad.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Sync | Sync | starts and stops land on the bar; Free takes the loop as long as you play it |
| Quantise | Bar | Beat drops in sooner, at the cost of a take that does not start on a bar line |
| Feedback | 100% | keep every layer; 70% lets older layers fade |
| Crossfade | 6 ms | longer is softer, shorter is tighter |
| Multiply by | x2 | the cycle a Multiply makes: x2, x3 or x4 |
| Max loop | 30 s | the longest loop, which sets the memory |
| Monitor | On | off hears only the loop |
| Mix | 100% | blend the dry input back in |

## Starting points

- "Bar — Bar Looper", "Beat — Beat Catcher", "Layers — Fading Layers", "Stack — Endless Stack".
- "Sketch — Free Sketch", "Octave — Octave Down Wash", "Tape — Backwards Tape", "Bed — Slow Decay Bed".

## Pitfalls

- Nothing is looped until you press `Record`: a trigger is a change of the parameter. With `machine_set` set it to 1 and back to 0 to press it again. A footswitch that stays down needs `Pedal` set to Press and release.
- In Sync a press waits for the bar: the take starts at the next bar line and `Record` again ends it at the next one. Press just before the line, as a player would.
- `Max loop` and `Undo levels` do not change while a loop is held: `Clear` first. A loop that is longer than `Max loop` at load makes room for itself.
- The song keeps the loop but not its undo history; a song opened again starts with the loop stopped (press `Play`).

## Pairs well with

- Neo Drums or Neo Poly into it for a loop of your own parts; Neo Delay, Neo Verb or Neo Filter after it to move the loop; Neo Chop before it for fills that you then keep.

## Checklist

- [ ] `machine_describe(machine="Looper")`: the trigger switches and `Sync`; press `Record` by setting it to 1 and back to 0.
- [ ] Play the song and `render` a few bars: the loop should land on the bar line.
- [ ] `analyze(source="Looper", metrics=["loudness"])`: overdubs raise the level; lower `Feedback` or `Loop level` to keep it in check.
- [ ] `compare` a bypassed render (`machine_set(machine="Looper", bypass=true)`) with the processed one.
