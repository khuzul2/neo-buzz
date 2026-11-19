## How it sounds

Neo Voice to MIDI listens to a voice (a microphone through Neo Audio, or any audio) and plays MIDI from it. The audio passes through at `Monitor` (0% is silent, 100% is the input as it came). It has no sound of its own: send its MIDI to a synth or a drum machine, and the synth makes the sound.

- Pitch: the sung note becomes a note on `Pitch channel`. `Range` Low latency (about 8 ms of analysis, from 100 Hz) or Quality (about 18 ms, from 70 Hz). `Lock` Off plays every semitone, Set key uses `Root` and `Scale`, Song key follows the song's Key entry, Suggest finds the key from what you sang. `Muted notes` (click the key/scale keyboard) are never played: the voice goes to the nearest note that is on. `Response` Fast (the default) plays the first note at once, about 14 ms (Low latency) or 21 ms (Quality) after the voice starts, and corrects a wrong first note within 80 ms; Stable waits `Settle` ms first and is safer for beatboxing. `Stickiness` and `Settle` stop a wobbling voice changing notes; `Min note length` and `Legato` shape the notes; `Octave` shifts them.
- Pitch bend: `Pitch bend` sets the range (set the same range on the synth). `Bend mode` Anchored keeps the note still until the voice leaves it by `Anchor` cents, then bends.
- Triggers: up to eight sounds you train (a kick, a snare, a click, a "tss"). Press Train on a pad and say the sound 10 to 20 times; it stops at 20. Each sound has its own `Sound n note`, velocity and sensitivity. The sound that is a trigger plays a drum note; a sung vowel plays the pitch; with `Overlap` Both the voice also plays its pitch. `Window` Accurate (30 ms) is a few points more right than Fast (18 ms).
- Chords: `Chords` plays a diatonic chord of the key on `Chord channel` under every sung note (`Chord follows` Root, or Melody for the chord that fits the sung note), triads or sevenths, with an inversion (Auto keeps them close) and a spread. The editor shows the chord and the chords that fit what you sang.
- Expression: `Level` and the vowels aa, oo and ee each play a controller (CC number, min and max thresholds, value range, channel).
- Where the MIDI goes: a machine (the Commands menu or the editor's "MIDI to" buttons; the notes land in its next block), an external MIDI output device, and a MIDI From source "Machine: <name>" for armed tracks, so a synth track can record the voice into a clip.

## Use it for

- Singing a bass line, a lead or a chord pad into a synth; beatboxing a drum kit; vowel-controlled filters.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Monitor | 0% | hear only the synth; raise it for a guide |
| Range | Quality | Low latency answers sooner and tracks less well |
| Lock | Set key | notes land on the key; Suggest learns it |
| Stickiness | 25 cents | a hysteresis: how far past the middle before the note changes |
| Response | Fast | Fast plays the first note at the first clean pitch; Stable waits Settle |
| Settle | 30 ms | how long a new note must be held before it replaces the one sounding |
| Level | -50 dB | the quietest voice that plays a note; raise it in a noisy room |
| Window | Accurate | the trigger classifier's slice of each hit |
| Min gap | 110 ms | the shortest time between two hits; a voice needs 100 to 120 ms |

## Starting points

- preset="Sing a Lead", preset="Sing a Bass", preset="Expressive Bend", preset="Beatbox Kit", preset="Beatbox and Bass", preset="Chords from the Voice", preset="Jazz Chords (Sevenths)", preset="Vowel Filter", preset="Full Voice Band".
- Send the notes to a synth: pick it in the editor's "MIDI to" button or the Commands menu, or set a synth track's MIDI From to "Machine: <this machine's name>" (`track_input(action="set", machine="Lead", midi_from="machine:Voice MIDI")`) and arm it.
- Train the sounds in the editor: select a pad, press Train, say the sound 10 to 20 times, press Done. The training is saved with the song and with presets you save.

## Pitfalls

- Nothing is trained: the Triggers part plays nothing; the pitch part still works.
- A noisy room: the pitch part follows noise as a note. Raise `Level`, or use `Lock` to keep it in a key.
- The synth's pitch bend range must match `Pitch bend`, or the bends sound off.
- Trigger notes go to channel 10 (the drum channel) by default; a bass synth listening on channel 1 will not play them.
- Latency: with Response Fast a note starts about 14 ms (Low latency) or 21 ms (Quality) after the voice does, 40 to 50 ms with Stable; the readings give `note_latency_ms` (the longest to expect).

## Pairs well with

- Neo Audio (the voice in), Neo Poly or Neo Sampler (the notes), Neo Drums (the triggers), Neo Gate before it to remove noise.

## Checklist

- [ ] `machine_readings(machine="Voice MIDI")`: `voiced` flips while you sing, `note` follows the key, `trained_sounds` counts the trained sounds.
- [ ] The synth plays; `track_input(action="get", machine="Lead")` shows `machine:Voice MIDI` as its MIDI From, and its armed track records the notes.
