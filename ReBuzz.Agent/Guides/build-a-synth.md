# Build a synth

Learn Neo Patch by building sounds node by node. Every step is one tool call and one undo step, so a wrong turn costs one `undo`. Ids in the calls come from `patch_describe`; each call that adds a node returns the new id. Every template loads at about -22 dB RMS and plays as soon as it is loaded.

## The loop

1. Add or change one node (`patch_add_node`, `patch_connect`, `patch_set`).
2. Write a long note with `notes_write` and `render` it with probes; check the level and the spectrum.
3. Read the patch back with `patch_describe`; the `unheard` list names nodes with no path to the Out node.
4. Keep what you like with `patch_save_preset`; step back with `undo`.

Real units everywhere: `patch_set` takes `"1.5 kHz"`, `"250 ms"`, `"saw"`, a number in the control's own unit, and refuses a value out of range by telling you the range.

## Sound in three steps

`patch_load_template(template="start")` loads Keys, Osc, Envelope, VCA and Out with the three wires missing, and it is silent on purpose. Then:

1. `patch_connect(from_node=<Keys>, to_node=<Osc>, to_port="Pitch")`: the key's pitch tunes the oscillator. Still silent: the oscillator goes nowhere.
2. `patch_connect(from_node=<Osc>, to_node=<VCA>, to_port="In")`: the oscillator reaches the amplifier. The VCA's CV input is already fed by the envelope, which is closed until a note.
3. `patch_connect(from_node=<VCA>, to_node=<Out>, to_port="In 1")`: the amplifier reaches Out's first input. Play a note: a saw with a pluck.

Now change one thing at a time: `patch_set(node=<Osc>, control="Shape", value="square")`, then the Envelope's `Decay` and `Sustain`.

## The templates, in the order to learn them

Load each with `patch_load_template(template=<slug>)`; Macros 1 to 3 sit at 50 percent, where the patch is as built, and the mapped control moves both ways from there.

- `subtractive`: two oscillators (the second a detuned octave down) into a Mixer, a ladder Filter that an Envelope opens and closes, then the VCA. Listen for: the filter envelope (Macro 3) turning a flat buzz into a note with a shape. Try `patch_set` on the Filter's `Cutoff` and `Reso`.
- `fm-2op`: a sine carrier whose FM input is fed by a second sine through a VCA that an envelope shapes. Listen for: the bell at the start of each note, then a pure tone. The FM amount on the carrier (Macro 1) is the brightness.
- `fm-4op`: two such pairs mixed, one envelope shaping both modulators. Listen for: the two pairs beating against each other; Macro 1 and 2 are the two indexes. Add a node: `patch_add_node(kind="LFO")` and wire it to a modulator's pitch for a slow shimmer.
- `am-ring`: the carrier times a second sine two ways, ring (the product) and AM (the product lifted so it never flips sign), crossfaded. Listen for: ring giving inharmonic, bell-like sidebands; AM keeping the pitch. Macro 1 slides between them; Macro 2 detunes the modulator.
- `wavefolder`: a sine into a Shaper in fold mode with an envelope raising the drive on each note. Listen for: the harmonics blooming and fading with the envelope; Macro 1 is the drive.
- `sync-lead`: a square restarts a saw on each of its cycles (the Osc's Sync input) while an envelope raises the saw's pitch. Listen for: the tearing sweep; Macro 1 tunes the slave oscillator.
- `plucked-string`: a short noise burst into a Delay whose time follows the key (the wire from Keys Pitch into the Delay's Time input has amount -1/12 so it is in tune), a Filter for the body, and a Feedback node that hands a little of the body back. Listen for: the string dying away; Macro 1 is the damping, Macro 2 the sustain. If you wire a loop yourself, `patch_connect` refuses it and names the cycle: send the wire through a Feedback node.
- `vector-crossfade`: four sources (saw, triangle, a wavetable, a sine an octave up) crossfaded in pairs and then together by three Crossfade nodes, two of them drifting on slow LFOs. Listen for: the sound turning over; Macros 1 to 3 are the three mixes.
- `sample-loop`: a looped built-in sample at the keys' pitch through a Filter. Listen for: the loop's character; Macro 1 moves where the loop starts.

## Make it yours

- Put a knob on it: `patch_macro_map(macro=4, node=<Filter>, control="Cutoff", low="300 Hz", high="6 kHz", label="Brightness")`. Centre low and high on the value you have so 50 percent leaves the sound alone.
- Take a node out with `patch_remove(node=<id>)`; its wires go with it.
- Keep it: `patch_save_preset(name="My sync lead", comment="a sync lead I built")`. Names are plain words, never a product's; a group name and a dash before the name files it in a group.

## Checklist

- [ ] Load `patch_load_template` with `start`, make the three wires with `patch_connect`, and `render` a held note.
- [ ] Walk each template: `patch_load_template`, `patch_describe` to read the nodes, then one `patch_set` and a new `render`.
- [ ] Add and remove one node with `patch_add_node` and `patch_remove`, and take each back with `undo`.
- [ ] Map a macro with `patch_macro_map`, then keep the patch with `patch_save_preset` and find it again with `preset_list`.
