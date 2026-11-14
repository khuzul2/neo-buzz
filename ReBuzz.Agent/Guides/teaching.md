# Teaching sound design

Explaining a sound to a person, with the sound playing. The Neo machines and Dye Essence carry their own teaching text for every control: a **How** sentence (how the control changes the sound) and a **Sound** sentence (what you will hear). Use it. It is written to be right, and it is the same text the person sees in the editor's Explain strip and the studio inspector's Selection tab, so what you say and what they read agree.

## What a control tells you

`machine_describe(machine="Teacher")` returns, for each parameter with Explain text, its `id` (a stable snake_case name `param_set` and `param_get` accept as well as the plain name), its `unit` (Hz, s, dB, %, a count), `min`, `max` and `default_number` in that unit, its `curve` (linear, logarithmic, bipolar, power or choice: how the range is spread, which tells you where the useful part is), and `how` and `sound`.

- Quote `how` and `sound` rather than paraphrasing them, then add what they leave out: an example from music the person knows, and a number.
- The curve matters for teaching: a logarithmic control (a frequency, a time) has most of its musical range in the lower part of the slider.
- A machine without Explain text still has its manual: `guide(machine=…)`.

## The Neo Learn machines: one idea each

Small synths, each a Dye Essence patch behind twelve controls (the first six are the sound's core idea), so a lesson never has more than a handful of things to turn. Each has presets that sit near -22 dB RMS, so the person can change a control and hear the difference without a level jump. Pick the machine for the idea:

| Idea | Machine | Start with |
|---|---|---|
| Brightness: a filter, its resonance and an envelope | Neo Learn Subtractive | Cutoff, Resonance, Env to Filter |
| Timbre from one oscillator bending another | Neo Learn FM | FM Depth, Mod Ratio, Mod Decay |
| A tone that moves through a table of waves | Neo Learn Wavetable | Frame Pos, Phase Warp, Motion Rate |
| A sound built from sine partials | Neo Learn Additive | Brightness Tilt, Odd / Even, Partials |
| Clouds from tiny grains of a recording | Neo Learn Granular | Scan Position, Grain Size, Density |
| Struck and plucked bodies | Neo Learn Physical | Material, Exciter Mode, Damping |
| A plucked string that rings by feeding itself | Neo Learn String | Pick Position, String Damping, Decay Time |
| Vowels: a voice shaped by formants | Neo Learn Formant | Vowel (A-E-I-O-U), Tract Size, Formant Q |
| Folding a wave back on itself | Neo Learn Wavefolder | Wavefold, Symmetry, Fold Stages |
| Bending the phase of a sine | Neo Learn Phase Distortion | Phase Bend, Resonance, Bend Decay |
| A cursor mixing four sources | Neo Learn Vector | Vector X (W-E), Vector Y (S-N), Orbit Rate |
| Playing one recording across the keyboard | Neo Learn Sampler | Start Offset, Tone, Loop |
| A recording rebuilt from its partials and noise | Neo Learn Spectral | Stretch, Time Smear, Freeze |

The manual (`guide(machine="Neo Learn Vector")`) has the rest of each machine's controls. Learn machines also report `rms_db`, `peak_db` and `active_voices` in `machine_readings`, so you can show a level moving while a note holds.

Neo Patch and `guide(topic="build-a-synth")` are the next step: the same ideas, one node at a time, as a patch the person builds.

## The lesson loop

One idea per lesson. Never more than one control moving at a time.

1. **Set the scene.** `machine_add(gear="Neo Learn Subtractive", name="Teacher")`, `preset_load(machine="Teacher", preset="Bass — Fat Saw")`. A held note to hear: write a two-bar pattern with one long note (`pattern_create`, `notes_write`, `arrangement_place`) and loop it (`song_set(looping=true)`), or in studio mode play a phrase on demand with `midi_play(machine="Teacher", notes=[…])`.
2. **Show them where to look.** In the studio, `select(machine="Teacher")` and `machine_open_editor(machine="Teacher", window="gui")` bring the machine up; its Explain strip shows the text of the control you change, badged as the agent's.
3. **Say what will happen.** Read the control's `how` and `sound` in your own flow: "Cutoff sets the corner frequency of the filter. Lowering it should make the sound darker and warmer."
4. **Change it, once, in steps.** `param_set(machine="Teacher", changes=[{"param": "Cutoff", "value": "400 Hz"}])`, let them hear, then another step. Large jumps teach less than a sweep in three or four steps.
5. **Ask what they heard** before telling them. Compare with the `sound` sentence; if they heard something else, believe their ears and look for the reason (a resonance, a note in a different octave).
6. **Show a number.** Headless or not, `render` two settings and `compare(a=…, b=…)`, or `plot(kind="spectrum", sources=[…])`, to see what they heard: a falling spectrum for a lowered cutoff. The measurement says how much, not whether it is good.
7. **Put it back** (`undo`, or `preset_load` again) and name the idea in one sentence before starting the next.

## Teaching on Dye Essence and the other Neo machines

Every Neo parameter and every Dye Essence parameter has the same How and Sound text, so the loop works on any of them: `machine_describe(machine="Pad", group="Filter 1")` narrows to a group (`machine_describe(machine="Pad")` lists the group names). For a machine's character before its controls, read its manual's "How it sounds" section, and for how an engine of Dye Essence differs from the others, `guide(topic="dye-sound-design")`.

## Pitfalls

- Do not invent a reason a control sounds as it does: if the text does not say, measure.
- Do not teach the safety limits as taste: a clipped render is a level problem (`guide(topic="mixing")`).
- Do not change several things and then attribute the change to one of them.
- A parameter the text calls "a step, not a ramp" (such as unison on the Learn machines) will jump: warn them.
- In headless mode the person hears nothing live: render, give them the file's path (the `render` result names it) and the numbers.

## Checklist

- [ ] You read the control's own text before explaining it: `machine_describe(machine=…)` shows `how` and `sound`.
- [ ] One control moved at a time, in steps, with the person listening: `param_set` then `param_get(machine=…, params=[…])` to say the exact value.
- [ ] What they heard is backed by a number when it matters: `compare(a=…, b=…)` of two renders.
- [ ] The song is left as you found it: `undo` or `preset_load`, and the audition placement removed (`arrangement_clear`).
