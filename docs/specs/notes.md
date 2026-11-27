# Real notes everywhere (CU6a) - audit and plan

| | |
|---|---|
| Status | Phase 1 (audit and plan) and Phase 2 (built, section 8), 2026-11-26. Issue #168, branch `engine/real-notes-cu6a`. |
| Principle | Every machine that plays notes takes note-on and note-off with velocity and any polyphony, wherever the notes come from: clips, the piano roll, Session, live MIDI, note wires and note inserts. Neo Plug and the exported plug-ins too. |
| Not here | The tracker's chord columns are CU6b. |
| Read first | `clips.md` S3, section 10.0d (CU7 as built), `note-wires.md`, `mcp-server.md` section 12.2 |

## 1. How a note gets to a machine

There are two ways in, and only one of them has a voice limit that comes from tracks.

| Source | Path | What the machine's intake gets |
|---|---|---|
| Clip, piano roll, Session clip, take playback | `WorkManager` plays the clip's MIDI column (the Notes lane) and calls `MachineManager.SendMidiInput`; `MidiColumnNotes` is the editor of the same column | MIDI note-on with velocity 1-127, note-off; at the start of the next block |
| Live MIDI, machine keyboard, mapped keys, agent `midi_play`, Session recording | `SendMidiInput` (`KeyboardPlayer`, `MidiMapping.Router`, `LivePlayer`) | the same |
| Note insert on a track (`NoteWires.RouteTrackInput`) | the event is queued for the first insert; each insert hands on; the last one delivers to the instrument | `INoteReceiver.ReceiveNotes` (Neo machines) or `SendMIDINote` (others); velocity kept, note-on velocity at least 1 |
| Free note wire | `NoteWireStore` to `ReceiveNotes` | the same; the synths' `NoteInput.Receive` turns it into the MIDI queue, so the sample offset inside the block is lost (start at the block's start) |
| Tracks route (a Note and a Velocity track parameter, one note per track) | `TrackerColumnNotes` reads and writes the Note lanes (`TrackNotes.Allocate`, `MaxTracks`); the engine plays the parameter columns | `NoteInput.SetNote/SetVelocity`, key = the track (0-15), velocity default 100 when none |

Which route a machine uses is decided in one place, `SongNotes.For` (and `KeyboardRoute` for the keyboard): a machine that has `MidiNote` takes MIDI. Every Neo synth derives from `NeoSynthBase`, which has `MidiNote`, so **every Neo instrument takes the MIDI route**. Only two things take the tracks route today: Neo Sampler in Tracker mode (`PlaysSamplePerNote`, a sample, offset and tune beside each note) and a caller that asks for `input: "tracks"` (the agent's override).

On the machine's side, all the Neo synths share `NoteInput` (MIDI queue of 1024 events, track notes) and `VoiceAllocator` (`Voices.cs`): a note is a key (the track, or 1000 + the MIDI note); one key, one note; Poly mode past `Polyphony` releases the oldest held key, and a new note takes a free voice, else the quietest released one (oldest on ties), else the oldest.

## 2. The table

Columns: **Arrives** (besides the common routes above), **Velocity** (what it does to the sound), **Voices and stealing**, **Note-off**, **Test** (yes: a test of this exists today; part: something related), **Gaps** (numbers refer to section 3).

### Instruments

| Machine | Arrives | Velocity reaches the sound | Voices, stealing | Note-off reaches each voice | Test | Gaps |
|---|---|---|---|---|---|---|
| Neo Poly | MIDI route; tracks route too | yes: `Vel -> amp` (default 70 %): gain 1 - a(1 - v); 30 against 127 is 6.6 dB at the default; also a mod source and the accent | 32 voices; Polyphony 1-16 (default 8) times Unison 1-7; past it the oldest held key is released, then a free, the quietest released or the oldest voice, with a fade (3 to 6 ms) | yes, per key | part (`PolyTests`: chord, stealing without clicks; baseline golden) | G6, G8 |
| Neo Wave | same | yes: `Vel -> amp` (default 70 %: 6.6 dB between 30 and 127) and a velocity-to-filter amount | 32 voices; Polyphony 1-16 (8) times Unison 1-8; same stealing | yes | no velocity or chord test | G6, G8 |
| Neo FM6 | same | yes, as a DX7: velocity sensitivity per operator, 0-7, **default 0 on the Init patch** (the factory presets set it); `Fm6Tests.VelocityAndKeyboardScalingSetTheLevel` | 32 voices; Polyphony 1-16 (8) times Unison 1-4; same stealing | yes | part (velocity: yes; chord and 16 notes: no) | G8 |
| Neo Mallet | same | yes: strike force is linear in velocity (30 against 127 is 12.5 dB); `Velocity to hardness` (60 %) also shortens the contact | 16 voices, fixed; past it the quietest released voice, else the oldest held one, faded in 3 ms | yes (Held to Released; sustain pedal honoured) | part (`MalletTests`: 20 notes over 16 voices, clicks at steals) | G6 |
| Neo Organ | same | **no, by design**: a tonewheel organ's keys are not velocity sensitive (`OrganTests.KeysAreNotVelocitySensitive...`, neo-organ.md) | no voice limit: 91 wheels run all the time; a key is a contact (16 track slots, 128 MIDI keys); a track note replaces the track's previous one | yes (a key opening) | yes (`OrganTests`, many) | G3, G6 |
| Neo Sampler | MIDI route; **tracks route in Tracker mode** (sample, offset, tune per note; `MaxTracks` 32 since CU6b) | yes: gain velocity^(2 x `Vel -> amp`) (default 70 %: 30 against 127 is 17 dB); zones also split by velocity range with crossfades | 32 voices, Polyphony 1-32 (32); past it the quietest released voice, else the oldest, faded 3 ms; choke groups | yes (a one-shot zone, and Kit mode, plays on) | yes (`SamplerTests`: velocity, steal and choke; `SamplerTrackerTests`) | G6, G11 |
| Neo Drums | MIDI route (GM drum keys from C2); per-track hit columns with a velocity; Accent column | yes: `Vel -> level` (70 %) and `Vel -> tone` (50 %) | 12 voices, one per drum; a new hit on a drum restarts that voice; closed hat chokes open hat; a chord of four keys plays four drums | no, by design (hits are one-shots) | yes (`DrumsTests.VelocityAccentAndVelToneDoWhatTheySay`) | none |
| Neo Patch | MIDI route; MPE (channel) | **the `Keys` node outputs it; a patch must wire it**: no factory template or preset does (0 uses) | 16 voices, Voices 1-16 (8); same allocator, with a fade | yes (gate off) | part (`NeoPatchTests`: chord = sum of its notes, steal) | G2 |
| Dye Essence | MIDI route; MPE; its own sequencer and arp intercept notes | yes: `Vel -> amp` (default 70 %: 6.6 dB), velocity as a mod source on 16 routes | 32 voices (`NoteBank.Notes`); Polyphony 1-32 (8); same stealing | yes | part (`DyeEssenceTests`: 16 notes, steals) | G8 |
| Neo Learn x12 (Subtractive, FM, Wavetable, Granular, Physical, String, Formant, Wavefolder, Phase Distortion, Vector, Spectral, Sampler, Additive) | MIDI route, through `LearnHost` onto a Dye Essence patch | yes: Dye's `Vel -> amp` stays at its default 70 % (no Learn map changes it); Learn FM adds `Velocity to FM` | Polyphony 16 (set by each map, not a control) | yes | part (`LearnL1Tests`, `LearnL1bTests`: chords and notes; no velocity assertion) | G11 |
| Neo Plug | MIDI route; tracks route (16 tracks: one note per track, velocity default 100) | yes: velocity/127 as the CLAP/VST3 note velocity (CLAP event, MIDI 1-127, no release velocity) | the plug-in's own; events from the MIDI queue, the wires and the tracks share one 1024-event block buffer | yes (note-off by key and channel; `AllNotesOff` on stop) | **no managed test** of the note path; the native probes play a note on the test plug-in | G5, G9 |
| Neo Plug FX | none: `AcceptsNotes` is false (an effect) | not applicable | not applicable | not applicable | no (XS) | G9 |

### Note machines (notes in, notes out)

| Machine | Arrives | Velocity | Voices, stealing | Note-off | Test | Gaps |
|---|---|---|---|---|---|---|
| Neo Arp | `ReceiveNotes` (wire, insert); `MidiNote` for a machine not on a wire | taken and used: Velocity As played, Fixed or Accent, humanize; sent on each step | held notes in, 4096 events out with a reserve for the offs; every note it sends ends | yes (a note it plays ends with its step; the source's off ends the held note) | yes (`NeoArpTests`, `NeoArpChainTests`) | none |
| Neo Chord | the same | taken and used: kept as played (Off passes through), or shaped (tilt, strum); clamped 1-127 | 8 voices per source note, 256 source notes; a voice limit setting; every note sent is ended when its source ends (pitches counted) | yes | yes (`NeoChordTests.VelocityShaping` and others) | none |
| Neo Voice to MIDI, Neo Audio to MIDI | sources: audio in, notes out to a target, an external device or `Machine: <name>` | velocity is sent with each note-on (as the machines' own spec says) | as the mode says | yes | yes (`VoiceToMidiTests`, `AudioToMidiTests`) | none |

### Note-driven effects and controls

| Machine | Arrives | Velocity | Voices | Note-off | Test | Gaps |
|---|---|---|---|---|---|---|
| Neo Chop | `ReceiveNotes` (wire), `MidiNote`, the track's note column; `MaxTracks` 1 | **dropped** (`NoteSink.NoteOn` ignores it): a note only starts a cut | one cut at a time | yes (`QueueNote(off)`) | yes (`NeoChopTests`) | G4 |
| Neo Envelope (Note mode) | **Note-parameter events of the listened machine only** (`NeoModulator.WatchNotes`) | not used; notes are counted, not read | one envelope | yes (a note-off with no note-on in the block) | part (the engine: `ModulatorTests`; the arrival: no) | **G1**, G4 |
| Neo LFO (retrigger on note) | the same | not used | one LFO | not used | the same | **G1** |
| Neo Tune (MIDI notes as the target) | `MidiNote` (a pitch target) | not applicable: velocity says nothing about a target pitch | not a voice machine | yes | yes (`TuneTests`) | none |
| Neo Step, Neo Macro | no note input (clocked / manual) | not applicable | not applicable | not applicable | not applicable | none |

There is no Pulse or Random note machine in the repository.

### Exported plug-ins (PluginExport: CLAP; VST3 and AU through the wrapper)

Instruments exported: Dye Essence, Neo Poly, Neo FM6, Neo Drums and the twelve Neo Learn synths. Not exported (so out of this task unless you say otherwise): Neo Wave, Neo Organ, Neo Mallet, Neo Sampler, Neo Patch, Neo Plug.

| Plug-in side | Arrives | Velocity | Voices | Note-off | Test | Gaps |
|---|---|---|---|---|---|---|
| All exported instruments (`Runtime.Apply`) | CLAP note-on / note-off / choke and MIDI events (the wrapper turns VST3 and AU notes into these), sample accurate; one note port | `MidiVel`: velocity 0-1 to 1-127 into `NoteOn(1000 + key, key, v/127)`, so the engine's own response | the engine's (as above) | key only: `note_id`, port and channel are ignored; choke = release | part: `selftest` plays two notes (0.8, 0.5) and checks the output **bit-identical to the engine used directly**, 0 bytes allocated; the host plays two notes. No test that velocity changes the level, or of a 16-note chord, per plug-in | G10 |

## 3. Gaps

| # | Gap | Where | Effect on a user |
|---|---|---|---|
| G1 | Neo Envelope (Note mode) and Neo LFO (retrigger on note) hear only Note-parameter events (`WatchNotes`). Notes that come as MIDI (every clip, roll, Session, live MIDI and wire note on a Neo synth) never reach them. The Neo patch spec says so: "sees the notes patterns and midi_play send, not a MIDI keyboard's" | `NeoBuzz.Machine/NeoModulator.cs`, `MachineManager` | A filter envelope driven by the notes of a Neo Poly clip does nothing. The most serious gap |
| G2 | Neo Patch: velocity arrives at the `Keys` node, and no factory patch uses it | `NeoPatch/Engine` (templates, presets) | Every Patch preset plays all velocities alike |
| G3 | Neo Organ ignores velocity (by design, asserted by a test) | `OrganEngine` | none; a decision: keep as a declared exception |
| G4 | Neo Chop, Neo Envelope and Neo LFO take notes but not velocity | the three machines | no velocity depth on a note-driven effect |
| G5 | The synth MIDI queue (`NoteInput`, 1024) and Neo Plug's (1024) drop an event when full, including a note-off, with no count | `NoteInput.Push`, `PlugCore.QueueMidi` | a stuck note in a burst of more than 1024 events in a block (very large imports, a runaway wire) |
| G6 | Two sources playing the same pitch at once share one key (1000 + note): the second note-on releases the first, and either note-off ends the note | `VoiceAllocator`, `NoteInput` | in Session, two clips with the same pitch overlapping on one machine. This is how MIDI itself behaves; no change planned (decision below) |
| G7 | Notes by wire start at the block start in the Neo synths (the offset is lost) | `NoteInput.Receive` | timing, not notes: out of scope, noted |
| G8 | Default Polyphony is 8 (Poly, Wave, FM6, Dye, Patch); 16 notes at once on a new instance release the oldest 8. Polyphony tops out at 16 for Poly, Wave and FM6 although the engines have 32 voices | `Params.def` of the three | a 16-note chord needs the knob set; Unison halves what 16 can give |
| G9 | Neo Plug: no managed test that notes with velocity reach the hosted plug-in; Neo Plug FX is not tested as note-less | `NeoPlug` | none today; a regression would go unseen |
| G10 | Exported plug-ins: no test that velocity changes the sound, that a chord sounds as many voices, or that each note-off ends only its note | `PluginExport/selftest` | as G9 |
| G11 | One test per machine for velocity (level rises in order, 6 dB or more from 30 to 127), the chord, 16 notes and the note-off exists only for some machines (FM6, Drums, Sampler, Organ, Mallet in part) | `NeoBuzz.MachineTests` | no guarantee for a new machine |

## 4. Plan for each gap

Sizes are person-days (pd), build and test, without the CI wait.

| Gap | Plan | Size | Songs and sound |
|---|---|---|---|
| G1 | A **note tap** on `MachineCore`: `MachineManager.SendMidiInput`, `SendMIDINote` and the note-wire delivery tell the tap (note, velocity, on or off) with an interlocked counter and the last velocity, no allocation. `NeoModulator` reads the tap beside the Note-parameter events (both feed the same counters, so a tracks-route note counts once). Test: a clip note, a roll note, a live MIDI note and a wire note on a Neo Poly start a Neo Envelope and a Neo LFO | 1.5 | Existing songs: modulators that watched a machine whose notes were MIDI start working, which is the intent; ones that followed Note parameters sound as before (bit-exact check on the golden songs) |
| G2 | A voice-sum `Vel -> level` control for Neo Patch, applied after the voice, with the same gain law as the other synths (70 % new). Appended parameter; the state version goes up and `MigrateState` sets 0 % for a song saved before, so it sounds the same | 1 | Old songs unchanged; new instances respond to velocity. Needs an editor knob (generic parameters view until NP2: no UI change) |
| G3 | Decision (below). If kept: the exception is stated in the test table and in the spec | 0.2 | none |
| G4 | Chop and LFO: stated as not velocity-driven (notes are triggers), in the table and the guides. Envelope: appended `Velocity` depth (0 % = as today), driven by the tap's velocity | 0.5 | Old songs unchanged (0 %) |
| G5 | `NoteInput` ring 1024 to 4096 (fixed array), a note-off is never dropped (an overflow drops the oldest note-on of the same key instead), a drop counter; the same for `PlugCore` | 0.5 | none |
| G6 | No change. Documented as MIDI's own behaviour. | 0.1 | none |
| G8 | Raise the Polyphony range of Poly, Wave and FM6 to 1-32 (appended as the same parameter, a wider range: saved raw values stay valid); defaults stay 8 | 0.3 | Old songs unchanged; only the range grows |
| G9 | Test Neo Plug's note path against the test CLAP synth (`neoplug.test.synth`, built by the plug-in host workflow) when the native library can be loaded on the CI runner: a chord of velocities 30, 60, 90 and 127 comes out in order, and each note-off ends its own voice. If the runner cannot load it, test `PlugCore`'s event building through an injectable native seam. Neo Plug FX: assert `AcceptsNotes` is false | 1.5 | none |
| G10 | `PluginExport/selftest`: for each instrument descriptor, play the chord (30, 60, 90, 127) one note at a time and as a chord through the CLAP entry, and compare levels and voice count with the engine; 16 notes; each off ends its own. Data-driven over `plugins.json` | 1 | none |
| G11 | The contract test below | 2 | none |

Total about 8.5 pd.

## 5. The tests to add (Phase 2)

A table-driven `NoteContractTests` in `NeoBuzz.MachineTests`: a row per instrument gives how to make its engine (or `LearnHost`) and render; a new machine is one line.

| Test | What it asserts |
|---|---|
| Velocity | a 4-note chord at 30, 60, 90 and 127 gives four voices; each voice's level (one note at a time, same pitch and settings) rises with velocity in that order, and 127 is at least 6 dB over 30; machines with the amount at 0 by design are marked in the row (Neo Organ; Neo FM6's Init patch is tested with a preset that sets sensitivity) |
| Note-off | each note-off releases only its own note: the other three go on |
| Polyphony | 16 notes at once give 16 voices, or the machine's stated maximum (Drums 12 drums, Mallet 16 voices) with stealing as stated and no crash |
| Allocation | 0 bytes allocated per block with notes, ons and offs, in the same run |
| Route | `SongNotes.For` gives the MIDI column for every instrument that has `MidiNote` (so `TrackNotes.Allocate` and `MaxTracks` are not in its path), and the tracks route only for Neo Sampler's Tracker mode and an explicit `input: "tracks"` |
| Note machines and effects | Neo Arp, Neo Chord: velocity passes or is shaped as stated; Neo Chop, Envelope, LFO: a MIDI note reaches them (G1), Envelope's velocity depth |
| Neo Plug | notes with velocity reach the hosted test plug-in; each off ends its note |
| Exported plug-ins | the PX harness plays the chord with velocities for every instrument in `plugins.json` |
| Sound | the null tests and the golden tests, unchanged: `tools/clip-null.py check --twice`, `NeoPolyUnchangedTests`, `FxGoldenTests`, and the song-saving gate (`ClipRetireTests`) |

## 6. Decision: `TrackNotes.Allocate` and `MaxTracks`

**Keep both.** Reasons:

1. They are not in the way of real notes. After CU7 and with every Neo instrument on the MIDI route, no clip note, roll note, Session note, live MIDI note, wire note or insert note passes through `Allocate` or is refused by `MaxTracks`. A chord of any size already reaches an instrument as MIDI, limited only by that instrument's voices (section 2).
2. They are the tracker's own model. A track is a lane and a lane holds one note at a time: that is what a Note column is, and Neo Sampler's Tracker mode needs it (a sample, offset and tune are track parameters beside each note, and the MIDI column has no room for them). `MaxTracks` is the machine interface's own limit for those lanes. CU6b (chord columns) changes how the tracker shows chords, and should be the task that touches this code.
3. Songs. A saved song holds Note columns per track and a track count. Replacing the allocation would change which track a note is written to when a song is edited, and a refused note would become silently dropped or moved: the saved events and the lanes of the songs in the repository (and of every user song with a Sampler in Tracker mode) would no longer match what the tracker shows. Keeping the code means nothing about the stored form changes, so every existing song loads and sounds the same, which the null and golden tests then confirm.

What changes instead is a guard: the Route test above fails if a Neo instrument other than the Sampler's Tracker mode ever takes the tracks route, so the limit cannot creep back into the note path. The one place a user can still hit `MaxTracks` is a chord of more than 16 notes in Neo Sampler's Tracker mode (its `MaxTracks` is 16). Raising that to 32 is a possible extra (0.2 pd, `MaxTracks` only, saved songs keep their track count); I would leave it to CU6b.

## 7. Decisions for review

1. G3: keep Neo Organ velocity-blind (declared exception), or add `Vel -> level` at 0 % (appended, sounds the same, adds an editor knob so a Mac gallery run)? Recommended: keep.
2. G4: Chop and LFO as triggers (no velocity), Envelope gets a Velocity depth. Recommended: yes.
3. G6: same pitch from two sources is MIDI's own behaviour; no refcount. Recommended: yes.
4. G8: raise Polyphony to 32 on Poly, Wave and FM6 (defaults stay 8). Recommended: yes.
5. Exported plug-ins: only the 16 instruments that are exported are covered; Wave, Organ, Mallet, Sampler, Patch and Neo Plug are not exported and stay out.
6. `TrackNotes.Allocate` and `MaxTracks`: keep (section 6).

## 8. Phase 2: what was built, and what was decided

The coordinator took the decisions of section 7 as follows. Gaps G1, G2, G4, G5, G9, G10 and G11 are built; G3, G6, G7 and G8 are documented.

| Gap | Result |
|---|---|
| G1 | `INoteTap` / `INoteTapSource` (`INoteWires.cs`): `MachineCore` tells its taps every note it is given (live MIDI and keyboard in `SendMidiInput`, clip and Session notes through the same path, `SendMIDINote`, and a note wire's delivery in `NoteNode`). `NeoModulatorBase` subscribes while it listens to "any note" and keeps the last velocity. Neo Envelope's Note mode and Neo LFO's note retrigger now start on a clip, roll, Session, live MIDI or wire note on a Neo synth. A listener set to one track still hears only that track. Tests: `NoteTapTests` (Neo Poly and Neo Envelope, real engine). |
| G2 | Neo Patch: appended `Vel -> level` (`vel_level`, 0 to 100 %, default 70 % for a new instance), the gain law of the other synths, 1 - a(1 - v), applied per voice and ramped. `PatchMigrations` gives a song saved before 0 %, so it sounds the same. Tests: `NoteGapsTests` (new default 70 %, old song 0 %, 6 dB between velocity 30 and 127 at 70 %, 0 % blind). |
| G3 | **Declared exception: Neo Organ is velocity-blind.** A tonewheel organ's keys are not velocity sensitive. It is marked `VelocityBlind` in the contract table, which asserts equal levels at every velocity instead of a rise. No knob. |
| G4 | Neo Chop and Neo LFO stay triggers: a note starts them, its velocity does nothing. Neo Envelope: appended `Velocity` (`velocity`, 0 to 100 %, default 0 %, so every old song and preset is unchanged); at 100 % the envelope's peak is velocity/127. The editor has a knob in the Output section (Mac gallery, light and dark). |
| G5 | `NoteInput`: the ring is 4096 events, a note-on is refused once only a reserve of 256 is left, so a note-off always finds room; a `Dropped` counter. `PlugCore`: the same idea in `PlugEventQueue` (1024 events, 128 reserve), with the note-ons also stopping short of the block's event limit. Tests: `NoteQueueTests`. |
| G6 | **Accepted limit.** Two sources playing the same pitch at once share one key; the second note-on takes over the voice and either note-off ends it, as on any MIDI instrument. No reference count. |
| G7 | Out of scope, as noted: a note that arrives by wire starts at its block's start in the Neo synths. |
| G8 | **Not done: Polyphony stays 1 to 16 on Poly, Wave and FM6.** A MIDI map stores a mapped knob's range as a fraction of the parameter's range (`MapModel`, `MapDrive.Min` and `Max`, read by `Router.Make` against the parameter's min and max). Widening 1-16 to 1-32 would move every saved map's value, and automation of that parameter written as a normalised value would shift with it. The defaults (8) are unchanged, and a 16-note chord on a new instance releases the oldest eight; raise Polyphony to 16 for it. The engines do have 32 voices: a new parameter with a wider range, or a map version, is the way, and is a decision for a later task. |
| G9 | Neo Plug: the native library is built only by the plug-in host workflow, so the Studio tests that need the native synth skip where it is not there. The checks are therefore: `tools/plugin-host-test.py` (fixtures) plays a four-note chord at velocity 30 and 127 on the native test synth (`neoplug.test.synth`, which plays each voice at its note-on velocity) and asserts 6 dB or more between them; `PlugEventQueue` (the queue of events for the plug-in) is unit-tested in `NeoBuzz.MachineTests` without the native library (a flood of note-ons never costs a note-off or a controller); and `NoteTapTests` asserts that Neo Plug takes notes and Neo Plug FX has no note input. |
| G10 | `PluginExport/selftest` plays, for every instrument in the factory, a four-note chord at velocity 30, 60, 90 and 127 (6 dB or more from 30 to 127), sixteen notes at once, and a note-off that must end only its own note (the level late in the tail, with the other note held, matches that note alone). Run by `plugin-export.yml` ("ALL OK"). Two sounds are marked: Neo FM6's Init patch has operator velocity sensitivity 0 (the presets set it), so the test gives Op1 sensitivity 7 first; Neo Learn Physical is not monotone in velocity (below), so it is asked only to be louder at 127 than at 30. |
| G11 | `NoteContractTests` (`NeoBuzz.MachineTests`): one row per instrument (Poly, Wave, FM6, Mallet, Organ, Sampler, Drums, Patch, Dye, the 13 Neo Learn synths): velocity ordering and 6 dB between 30 and 127, four notes at four velocities give four voices, each note-off ends only its own note, sixteen notes give sixteen voices or the stated maximum, notes past the maximum steal without a crash or click, and 0 bytes allocated per block with note-ons and note-offs. A new instrument is one row. |

Route guard (section 6): `NoteTapTests.EveryNeoInstrumentTakesItsClipNotesByTheMidiColumn` asserts `SongNotes.For` is the MIDI column for every Neo instrument, so `TrackNotes.Allocate` and `MaxTracks` stay out of the note path. `TrackNotes.Allocate` and `MaxTracks` are kept; `MaxTracks` of Neo Sampler went to 32 in CU6b (section 9).

### Neo Learn Physical: monotone in velocity (CU6b)

CU6a found its level moving by up to 17 dB between neighbouring velocities. The mallet's contact pulse (a half sine whose length is the velocity's contact time) has spectral nulls, and a null that falls on the lowest modes moves with the velocity and the pitch. Four changes in the Morph exciter of the Modal engine (Dye Essence's `ModalEngine`, used only by Neo Learn Physical; Dye's own exciters are untouched):

1. The mallet is levelled by what it rings in the first 50 ms, from the pulse's exact response at each mode (`MalletResponse`, the z transform of the sampled pulse with the mode's decay), not by the energy of the first sample, up to 13 dB of lift for a null (`MalletNullFloor` 0.05). The reference is the pulse's envelope (the lobes' peaks), so a null is levelled and a lobe is not.
2. The pulse's length is pulled toward a whole number of cycles of the lowest mode (halfway, from the second cycle on), so the lowest mode sits on a lobe and not in a null as the velocity moves.
3. When the mallet is in the blend, the pluck starts in the mallet's phase (as if its ring began at the pulse's centre), so the two do not cancel at a velocity that depends on the contact time.
4. A harder key is louder as well as brighter: the mallet's and the pick's level goes as (0.6 + 0.8 velocity) to the power 0.6 (`MorphDynamics`), on top of Dye's `Vel -> amp`. The bow's level follows its force already.

The mallet's overall level (`MalletLevel` 0.757) and the Output of the Marimba, Pluck, Glass, Kalimba, Harp and Plate presets were set again so every preset still sounds at the same loudest 50 ms (-22 dB). `LearnPhysicalVelocityTests.LevelRisesWithVelocity`: velocities 1 to 127 in steps of 6, each at least as loud as the one before, 30 to 127 at least 6 dB, on the default settings and the nine presets at notes 48, 60 and 72. The contract row no longer carries `NotMonotone`. The morph's ends test fits a gain for the mallet and the pick (their levels are the engine's own now); the shapes still match Dye's to -90 dB. Pins `LearnL1Tests`: Learn Physical init, marimba and nylon-pluck changed on purpose.

## 9. Chords in the tracker and MaxTracks 32 (CU6b, #168)

**Chords.** A clip's Tracker shows as many note+velocity column pairs as the user wants for its Notes lane (the MIDI column of a machine that takes real notes). A pair has no data of its own: it is a view of the one Notes lane (clips.md section 5.3), and a note sits on the lowest pair free at its time (`NoteLanes`), so a chord is several notes at one time and the pairs show them side by side. The count is view state kept in the Notes lane's metadata (`NoteColumns`, in `ClipMeta`), saved in the clip with the lane (TAP2, no format change), copied with the clip, and set by one undo step (`NoteColumnsAction`, on the song's history; the tracker's lanes lay out again on every do, undo and redo).

| What | How |
|---|---|
| Add / remove | `Alt++` and `Alt+-`, the grid's menu (Add Note Column, Remove Note Column) and a right click on a column header. A pair is never taken off while a note would not be shown, and the status bar says so; at most 32 pairs (a machine's tracks-route limit). |
| Old clips | With no count stored the clip shows what it always showed: the voices its notes need and one free. Nothing is written until a pair is added, so every old song loads and plays as before (`NoteColumns.Shown`). |
| The note path | A chord typed across two pairs is two notes of the MIDI column at one time: the real note path (`SongNotes.For` is `MidiColumnNotes`), each note on its own voice. |
| Keyboard | Right, Left and Tab move across the pairs like across any column sets. |
| Agent | `notes_read` and `clip_get` give each note its `column` (from 1) and the clip's `note_columns` (`shown`, `stored`, `needed`); `notes_write(columns=n)` sets the count (refused, with the reason, while it would hide a note or past 32; one undo step). |

Limit: no note is pinned to a pair. A note shows on the lowest pair free at its time, so typing in a higher pair while a lower one is free puts the note on the lower one. Tests: `NoteColumnsTests` (pure), `ChordColumnsTests` (the Tracker headless: add and remove by key and by command, undo and redo, saved in the .nbl and reopened, a chord of two notes, the cursor across the pairs, the agent), the existing `TrackerNotesViewTests` for the clips that never had a count, and the gallery shot `tracker-chords` (two chord column pairs, light and dark).

**MaxTracks 32.** Neo Sampler's Tracker mode (the only tracks-route instrument with a user-visible limit) takes up to 32 notes at once: `Tracks` in `Params.def` and the manifest go to 32 and `machine-profile.json` says `max_tracks` 32. A song saved with fewer tracks keeps its tracks and its notes (`ChordColumnsTests.ASongSavedWithSixteenTracksKeepsItsTracksAndItsNotes`; the parameters of tracks 17 to 32 are appended, so saved state, presets, MIDI maps and automation load the same). The refusal names the machine's limit: "has 32 tracks (it takes up to 32): C4 at 0 ticks needs one more at once, and 32 is the most it has." (`TrackerColumnNotes`), and the agent's "has at most 32 tracks, but these notes need 33 at once".
