## How it sounds

Neo Audio to MIDI listens to an instrument (a guitar or a bass through Neo Audio, a synth, drums) and plays MIDI from it. It is an effect for instruments: the audio passes through at `Monitor` (0% is silent, 100% is the input as it came). It has no sound of its own: send its MIDI to a synth or a drum machine, and that makes the sound. It is the instrument twin of Neo Voice to MIDI and shares its MIDI outputs.

- Melody (`Mode` Melody): one note at a time. A note on at each attack (pluck, key press), at the pitch heard, `Octave` shifted. `Range` Guitar (from 80 Hz), Bass (from 38 Hz, a longer window: slower) or Full. `Response` Fast plays at the first two agreeing clean pitches (a median of 5 to 16 ms after a real pluck) and corrects a wrong first note within 80 ms, or by pitch bend; Stable waits for a clean pitch and at least 20 ms (a median of about 15 ms). A plucked string is handled as one: a re-pluck of the same note plays it again (`Retrigger`), a string dying away ends the note (`Release` dB under the note's own peak, with hysteresis), and a ringing string does not chatter. `Min note length` is 50 ms by default. `Level` is the quietest note; a note also has to be within 24 dB of the loudest recent one.
- Key: `Lock` Off plays every semitone, Set key uses `Root` and `Scale`, Song key follows the song's Key entry, Suggest finds the key from what you played. Off by default.
- Notes: `Min note length`, `Legato` (a new note on before the old one's off), `Stickiness` and `Settle` against a wobbling pitch, `Velocity` Dynamic (from the attack) or Fixed.
- Pitch bend: `Pitch bend` sets the range, Off or plus or minus 1 to 12 semitones (set the same range on the synth); with it on a slide or a bend is followed by the bend. `Bend mode` Anchored keeps the note still until the pitch leaves it by `Anchor` cents.
- Drums (`Mode` Drums): kick, snare and hat, built in, no setup. Each has its own note (36, 38, 42), velocity (0 from how hard the hit was), sensitivity and shortest interval (50, 30 and 15 ms), so hats run fast while a kick's tail is not a second kick. The hit's note is sent about 15 ms after the hit (a 14 ms window). Calibrate (the editor's panel, Drums mode): pick a drum, press Calibrate, hit it five times; do the three; the kit is then named from your own sounds, with the built-in rules behind it for what it does not recognise.
- Where the MIDI goes: a machine (the Commands menu or the editor's "MIDI to" buttons; the notes land in its next block), an external MIDI output device, and a MIDI From source "Machine: <name>" for armed tracks, so a synth track can record the instrument as a clip.

## Use it for

- Playing a bass line or a guitar lead into a synth; a drum kit or tapped table played as a drum machine; a keyboard part doubled by another sound.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Monitor | 100% | hear the instrument itself; 0% to hear only the synth |
| Mode | Melody | Drums for kick, snare and hat |
| Range | Guitar | Bass for the low strings (about 48 ms slower), Full for both |
| Response | Fast | Stable waits for a clean pitch: fewer wrong first notes, a few dozen ms later |
| Level | -40 dB | the quietest note; raise it for a noisy room or a hum, lower it for soft playing |
| Sensitivity | 50% | how soft an attack may be; lower for a noisy instrument |
| Pitch bend | Off | on: slides and bends are sent as bend (match the synth's range) |
| Retrigger | On | a new pluck of the sounding note plays it again |
| Record at attack | On | a track recording from `Machine: <name>` puts each note at the attack (the sound's start), not when the note was sent; live output is unchanged |

## Starting points

- preset="Guitar lead", preset="Bass", preset="Keys", preset="Drum kit", preset="Percussion".
- Send the notes to a synth: pick it in the editor's "MIDI to" button or the Commands menu, or set a synth track's MIDI From to "Machine: <this machine's name>" (`track_input(action="set", machine="Lead", midi_from="machine:Audio MIDI")`) and arm it.
- Drums mode: with `Monitor` 0% and the output going to a drum machine, a kit hit on a pad plays the drum machine. Calibrate if it mistakes a drum.

## Pitfalls

- It follows one note at a time: chords, or strings ringing together, give one wandering note. Play single notes, or one string per machine.
- Strings that ring on, or a noisy signal: a note per wobble. Raise `Level`, lower `Sensitivity`, or use `Lock`.
- Drums: a loud cymbal or a busy mix hides the quieter drum under it; a hit with two drums at once (a kick with a hat) plays both notes. Calibrate for your kit.
- The synth's pitch bend range must match `Pitch bend`, or the bends sound off.
- Accuracy, measured on real recordings it was not tuned on: a guitar through a pickup or DI gets about 71 to 74% of notes right (F-measure); a guitar through a microphone only about 50%, so use a pickup or a DI. The low E string is weakest, drop-D is below Guitar range (use Bass). Drums: kick 91%, hat 83%, snare 82%.
- Latency: Guitar Fast plays a note a median of 5 to 16 ms after the pluck (worst about 46 ms), Bass range about 30 ms (worst about 47 ms), Stable about 15 to 21 ms, a hit's note about 16 ms. `machine_readings` gives `note_latency_ms`, the longest to expect.
- Drum notes go to channel 10 (the drum channel) by default; a bass synth listening on channel 1 will not play them.

## Pairs well with

- Neo Audio (the instrument in), Neo Poly or Neo Sampler (the notes), Neo Drums (the hits), Neo Gate before it to remove noise.

## Checklist

- [ ] `machine_readings(machine="Audio MIDI")`: `voiced` flips while you play, `note` follows the pitch, `last_hit` is 1, 2 or 3 after a kick, snare or hat.
- [ ] The synth plays; `track_input(action="get", machine="Lead")` shows `machine:Audio MIDI` as its MIDI From, and its armed track records the notes.
