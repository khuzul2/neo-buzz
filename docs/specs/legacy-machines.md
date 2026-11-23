# Legacy machines: what to rebuild as Neo and what to map on load — Proposal

| | |
|---|---|
| Status | **Decided** (2026-11-23): the user's answers are in section 10. Analysis only: no product code changed yet |
| Decision (binding) | "One store, one engine". "Make sure all new machines and our VST host take real notes." "For old machines, make them read-only": their clips are view-only until the machine is converted to a Neo one; they keep playing. "Add to our backlog a new phase: analyze all of them and decide a. which ones is worth to rebuild as Neo native machines and which ones can be converted on load and mapped to existing Neo machines. With this, we're moving away from ReBuzz and actually crafting more a spiritual successor than a fully backwards compatible one. The goal with old songs is to load them, accepting conversions (e.g. from Jeskola Verb to our Neo Reverb)." (the user, 2026-11-23) |
| Scope | Every machine that is not a Neo machine (97 in the profile, plus the machines the repo's songs name that the package does not ship), the Neo family and its gaps, one recommendation per machine, the mapping tables of the most used, the conversion on open, the waves |
| Code baseline | fork `9cd1dd36` (origin/main, 2026-11-23); `clips.md` as on branch `design/clips-cu0` |
| Related | machines.md (catalogue, versioned state, manifests), clips.md (one store, section 7 old songs, section 12), mcp-server.md (section 12.5, 12.10, `machine_replace`), machine-safety.md (section 4), cross-platform.md (R1, R2, section 4.3), native-format.md (NF2 loss report), plugin-host.md |

---

## 1. Summary

**Verdict: most of this phase is already built, and it is a mapping job, not a rebuild job.** `machine_replace` (U5) moves a classic machine onto a Neo successor through a mapping file in real units: its connections, parameters, patterns (notes and parameter columns, ramps kept), arrangement and Session View, as one undo step, with a before and after render against a stated tolerance. **30 such pairs exist** (`Machines/Manifests/Successors/`). What is missing is (a) running it **on open, without a render**, for a whole song, (b) pairs for the machines that have none, (c) a loss report that names every change, (d) retargeting of controllers, (e) the read-only rule, and (f) three real gaps in the Neo family.

- **97 non-Neo machines** ship in the profile. Recommendation per machine (section 5):

| Recommendation | Machines | Meaning |
|---|---|---|
| **Map, pair exists** | 30 | Runs on open as is (wave 1) |
| **Map, new pair onto an existing Neo machine** | 24 | A mapping file and a calibration each; no new DSP |
| **Map into the wires, Song Notes or a platform machine** | 9 | Mixers dissolve into connection gains and pans; Info and QuickNote into Song Notes; Polac I/O onto ReBuzz's I/O |
| **Bake into the clips** | 6 | Chord, arpeggio, envelope and peer-control machines write their result into the target's clips |
| **Rebuild as a new Neo machine** | 1 now (Neo Lo-Fi), 0 to 2 later (section 4) | Clean room, from literature |
| **Keep as legacy** (plays, clips view-only, hidden) | 16 | Closed or niche, no Neo role |
| **Drop** (silent placeholder, warning) | 5 | Superseded by core features, or a developer example |
| **Platform, stays** | 6 | ReBuzz Audio In/Out, Midi Output, Groove, DAWControl, Modern Sequence Editor |

- **The Neo family covers every role the classic songs rely on, except four**: a tracker's wave-per-note sampling (**G1**), bit and sample-rate reduction (**G2**), the 303's accent and diode filter (**G3**, a Neo Poly extension, not a new machine), and a drawbar organ (**G4**, only on census evidence). Pitch and FFT effects (Joachims Neptune) stay legacy (**G5**).
- **The repo's own songs are a poor census.** 20 old songs name 103 machine instances: **28 (27 %)** have a pair today, 17 are in the package without one, and **58 (56 %) are machines the package does not ship** (Geonik, Zephod, Elak, Automaton...: Buzztrax demo songs for other people's machines). Both templates (`.nbl`) name only Neo machines and the pattern editor. Wave 1 alone covers 62 % of the in-package instances, wave 3 all of them. The real corpus is the user's own songs: wave 1 includes a census tool.
- **Licences set the rules.** Pedal's sources have no licence (16 of 19) or GPL-3.0 (3); FSM is LGPL-2.1, Oomek GPL-3.0+; Jeskola's six have none; the rest are closed binaries. A rebuild is clean room (documentation and measurement only); a pair file contains no code from any of them.
- **Effort (rough): 26 to 32 pw** in four waves (section 9). Wave 1 (6 to 8 pw) delivers the conversion on open with the 30 pairs.

### 1.1 Words

| Word | Means |
|---|---|
| **Pair** | One mapping file `Machines/Manifests/Successors/<from> to <to>.successor.json`: rules in real units, what is not mapped and why, the expected difference in words, the tolerance and how it was measured |
| **Legacy machine** | Any machine that is not a Neo or platform machine. It plays; its clips are view-only |
| **Convert** | Replace a legacy machine by its Neo successor through its pair |
| **Bake** | Write what a control machine would have played into the clips of the machine it drove, then remove it |
| **Wave** | A group of conversions shipped together (section 9) |

---

## 2. What exists, and what the decisions add

| Exists | Where |
|---|---|
| `machine_replace` and the machine menu's "Replace with <successor>...": connections with gains and pans, parameters in units (`SuccessorConverter`, clamped with a note), patterns (engine columns and the pattern editor's tracker columns; notes in the successor's pitch convention, slides as glides), arrangement, Session View tracks and clip settings (`SessionLayout.Adopt`), one undo step; a dry run; before and after renders against the pair's tolerance | `ReBuzz/Agent/MachineReplace.cs` (687 lines), `ReBuzz/Core/Session/SessionLayout.Replace.cs` |
| 30 pairs with 17 rule kinds (`unit`, `db`, `range`, `sum`, `choice`, `grid`, `dry_wet`, `sweep`, `delay_time`, `filter`, `levels_to_mix`, `clip`, `key_zones`, `interval`, `bypass`, `graphic`, `from_db`) | `Machines/Manifests/Successors/` |
| A manifest per machine in the package (curated or drafted), a catalogue of category, tags and one line | `Machines/Manifests/`, `machine-catalog.json`, `tools/machine_catalog.py` |
| A profile per machine: health, level tests, how it takes notes, parameter counts | `machine-profile.json`, `docs/machine-health.md` |
| Songs naming a renamed machine load as its successor (`Session Clip Player` as Neo Audio) | `ReBuzz/MachineManagement/MachineAliases.cs` |
| A missing machine loads as a placeholder that keeps its data and saves back unchanged | `BMXFile.LoadMachines`, mac-engine.md M2 |
| The 17 open natives build for Windows, macOS and Linux (R1) and are compared bit for bit or by tolerance with upstream's | `tools/native-compare.py`, `tools/native-cross.py` |
| The loss report of Export for Buzz (`ExportReport`: category, item, what happens upstream) | native-format.md section 11.2 |
| Song Notes, importing Info machines | machine-safety.md section 3.5 |

| The decisions add | Consequence |
|---|---|
| "Spiritual successor, accept conversions" | A pair's tolerance is the bar, not "sounds the same". A conversion that is within tolerance and says so is good enough |
| "Old machines read-only" | The clip editors, the tracker and the agent's write tools refuse a legacy machine's clips with a reason and the one command that fixes it: Convert |
| "One store, one engine" (clips.md) | The converter writes engine lanes (`Play=engine`), never pattern-editor-played ones, and reads the one store |
| "Real notes" | A converted generator gets its notes as notes-lane events (time, length, velocity, channel), not as track parameters |

---

## 3. Inventory

### 3.1 What the package ships (97 non-Neo machines)

Source: `Machines/Manifests/machine-profile.json` and `machine-catalog.json` (148 machines in all, 51 of them Neo family: Neo ... and Dye Essence), `ExternalMachines/machines.json`, `RunTime/Gear`. **Platforms** come from cross-platform.md (section 2.4, R1): the profile's own `platform` field only says where it was profiled. **Params** are the profiler's count (global + track); a closed machine's draft may miss track parameters (Jeskola Tracker lists 1 and no track parameters: its Note, Wave and Volume columns are not read). **In the repo**: instances in the repo's songs (section 3.2) and whether a factory preset file for it is in the repo. **Rec**: MAP (pair exists), MAP+ (new pair), BAKE, BUILD, KEEP (legacy), DROP, STAY (platform machine). **Wave**: section 9.

Kind: gen (generator), fx (effect), control (no audio, drives others).

#### A. Classic native machines, closed (30)

| Machine | Kind | What it does | Source, licence | Platforms | Notes in | Params (global+track) | In the repo | Rec | Target and why | Wave |
|---|---|---|---|---|---|---|---|---|---|---|
| BTDSys PeerChord | control | Turn single notes into chords and arpeggios played on other machines. | closed binary | Win only | Note tracks (16) in, notes out | 0+21 | – | BAKE | Chord and arpeggio tracks expanded into the target machine's notes lane | 3 |
| Bigyo Wood One | gen | Wooden, mallet-like plucks and tuned percussion from a resonator… | closed binary | Win only | Note tracks (16) | 12 | presets | KEEP | Niche resonator synth; no Neo role; Windows only | 4 |
| FSM Philthy | fx | Resonant filter with its own LFO for sweeps and rhythmic filter… | closed binary | Win only | – | 0+10 | presets | MAP+ | Neo Filter with its LFO | 3 |
| Intoxicat ACloud15 | gen | Turn a wave into clouds of tiny grains for textures, drones and… | closed binary | Win only | trigger, no pitch | 25 | – | KEEP | Grain clouds from a wave; Neo Learn Granular is a teaching synth with another model | 4 |
| Jeskola Bass | gen | Simple four-voice analog-style bass with a resonant filter and… | closed binary | Win only | Note tracks (4) | 2+3 | 1 in 1 song | MAP | Neo Poly (pair exists) | 1 |
| Jeskola Bass 2 | gen | Monophonic bass with a resonant filter, envelope modulation and… | closed binary | Win only | Note tracks (8) | 0+5 | – | MAP | Neo Poly (pair exists) | 1 |
| Jeskola Bass 3 | gen | Squelchy acid and rolling bass lines with accent and slide. | closed binary | Win only | Note tracks (8) | 0+5 | 2 in 2 songs, presets | MAP | Neo Poly (pair exists) | 1 |
| Jeskola Chorus | fx | Basic chorus for thickening a mono or stereo source. | closed binary | Win only | – | 6 | – | MAP | Neo Ensemble (pair exists) | 1 |
| Jeskola EQ-3 | fx | Broad three-band tone shaping with two crossover points. | closed binary | Win only | – | 6 | 4 in 3 songs, presets | MAP | Neo EQ (pair exists) | 1 |
| Jeskola Filter | fx | Resonant low-, high- or band-pass for sweeps and tone shaping. | closed binary | Win only | – | 4 | 1 in 1 song, presets | MAP | Neo Filter (pair exists) | 1 |
| Jeskola Filter 2 | fx | Compact resonant filter with a selectable type. | closed binary | Win only | – | 3 | 2 in 1 song | MAP | Neo Filter (pair exists) | 1 |
| Jeskola Flanger | fx | Classic swept flanger with feedback. | closed binary | Win only | – | 6 | 1 in 1 song | MAP | Neo Ensemble (pair exists) | 1 |
| Jeskola Organ | gen | Drawbar-style organ tones from a set of footage-labelled harmonics. | closed binary | Win only | Note tracks (8) | 10 | – | KEEP | Drawbar organ; no Neo equivalent. A small Neo Organ is listed as a gap (section 4), built only if the census shows use | 4 |
| Jeskola Raverb | fx | Simple reverb with a time and high-frequency damping. | closed binary | Win only | – | 4 | – | MAP+ | Neo Verb; four parameters, a pair file of its own beside Jeskola Reverb's | 2 |
| Jeskola Reverb | fx | Mono reverb with early reflections and long tails up to about six… | closed binary | Win only | – | 6 | 1 in 1 song, presets | MAP | Neo Verb (pair exists) | 1 |
| Jeskola Stereo Reverb | fx | Stereo reverb with pre-delay and a high cutoff for a wider space. | closed binary | Win only | – | 6 | 1 in 1 song | MAP+ | Neo Verb: the same six controls as Jeskola Reverb, so the same pair file with Width left open (7.6) | 2 |
| Jeskola Tracker | gen | Play waves from the song's wavetable as in the classic tracker… | closed binary | Win only | Note tracks (16): Note + Wave | 1 | 2 in 1 song | MAP+ | Neo Sampler, Tracker mode (gap G1, section 4) | 2 |
| Jeskola Wave Shaper | fx | Waveshaping distortion with a shape control per track. | closed binary | Win only | – | 0+2 | 1 in 1 song | MAP+ | Neo Drive; its four shapes are unverified, so reported as approximate | 3 |
| Joachims Chopper 2 | fx | Triggered band-pass sweeps for stuttering, chopped filter moves. | closed binary | Win only | – | 9 | – | KEEP | Triggered band-pass sweeps; rhythmic effect with no Neo role. Hidden | 4 |
| Joachims Enceladus Tube EQ v1 | fx | Band boosts with tube-style drive for warm, coloured tone shaping. | closed binary | Win only | – | 15 | – | MAP+ | Neo Strip (EQ plus drive stages in one machine) | 3 |
| Joachims Hyperion | fx | Graphic EQ with a band for every third of an octave, for fine… | closed binary | Win only | – | 34 | – | MAP | Neo EQ graphic mode (pair exists) | 1 |
| Joachims Jupiter 3 | fx | Three-band compressor with stereo width and a beefing control, for… | closed binary | Win only | – | 25 | presets | MAP+ | Neo Multiband (three bands); the stereo width and beef controls go to the report | 3 |
| Joachims Mars filter 2 | fx | Thirteen distortion characters, from soft and round to tube-like… | closed binary | Win only | – | 5 | presets | MAP+ | Neo Drive; 13 characters fold onto its 5 modes, nearest by measured transfer curve | 3 |
| Joachims Neptune 1.2 | fx | Spectral effects through an FFT: pitch shifting, bin scrambling… | closed binary | Win only | – | 7 | presets | KEEP | FFT effects (pitch shift, bin scramble); flagged unhealthy; no Neo role (G5). Hidden | 4 |
| Joachims Saturn 1.4 | fx | Lo-fi character in one: noise, bit and sample-rate reduction,… | closed binary | Win only | – | 16 | presets | BUILD | Neo Lo-Fi (gap G2): bit and sample-rate reduction, noise, detune. Until then KEEP | 3 |
| KrimZon AY-3-8910 | gen | Raw three-voice retro sound-chip tones, with arpeggio-style… | closed binary | Win only | Note tracks (3) | 2+9 | – | KEEP | Three-voice chip sound; no Neo role | 4 |
| Matilde Tracker2 | gen | Tracker-style sample playback with tuning and quantised offsets. | closed binary (source in Buzztrax, no licence) | Win only | Note tracks (16): Note + instrument | 4+2 | – | MAP+ | Neo Sampler, Tracker mode (gap G1) | 2 |
| Rout 909 | gen | Complete synthesised drum machine with kick, snare, toms, rim,… | closed binary | Win only | Note track (1) + trigger columns | 29 | 6 in 6 songs | MAP | Neo Drums (pair exists) | 1 |
| kibibu Green Milk | gen | Unison saw stacks and chord-shaped leads and pads with an… | closed binary | Win only | Note tracks (16) | 38 | – | MAP+ | Neo Poly (unison saws, chord-shaped leads); same recipe as Infector | 3 |
| mixIO | fx | Per-track gain, width, mute and solo for balancing several inputs. | closed binary | Win only | – | 0+9 | – | MAP | Dissolves into the wires: per-input gain and mute become connection amp and pan; width goes to the report | 2 |

#### B. Open native machines rebuilt from source (R1: FSM and Oomek) (11)

| Machine | Kind | What it does | Source, licence | Platforms | Notes in | Params (global+track) | In the repo | Rec | Target and why | Wave |
|---|---|---|---|---|---|---|---|---|---|---|
| FSM Chorus2 | fx | Stereo chorus with one to six taps for widening guitars, pads and… | LGPL-2.1, source in repo | Win, Mac, Linux (R1) | – | 0+5 | – | MAP | Neo Ensemble (pair exists) | 1 |
| FSM Infector | gen | Thick detuned-saw leads, stacks and pads from a polyphonic synth… | LGPL-2.1, source in repo | Win, Mac, Linux (R1) | Note tracks (12) | 38+2 | 5 in 1 song, presets | MAP+ | Neo Poly (unison saw stack, two filters, two LFOs through the mod matrix); table in section 7.2 | 2 |
| FSM KickXP | gen | Tunable synthesised kick drums, from short thumps to long sub sweeps. | LGPL-2.1, source in repo | Win, Mac, Linux (R1) | Note tracks (8), trigger columns | 0+12 | 1 in 1 song, presets | MAP | Neo Drums (pair exists) | 1 |
| FSM PanzerDelay | fx | Stereo multi-tap delay with per-tap pans and optional ping-pong… | LGPL-2.1, source in repo | Win, Mac, Linux (R1) | – | 7+8 | 1 in 1 song, presets | MAP | Neo Delay (pair exists) | 1 |
| FSM PhatMan | fx | Chorus and flanger with up to eight delay lines for wide, thick or… | LGPL-2.1, source in repo | Win, Mac, Linux (R1) | – | 2+6 | 2 in 1 song, presets | MAP+ | Neo Ensemble: tracks become voices (up to 4); table in 7.5 | 2 |
| FSM WahMan | fx | Auto-wah: a resonant low-pass swept by an LFO, for funky, talking… | LGPL-2.1, source in repo | Win, Mac, Linux (R1) | – | 0+6 | – | MAP | Neo Filter (pair exists) | 1 |
| FSM WahManPro | fx | Auto-wah with a choice of filter types and LFO shapes for more… | LGPL-2.1, source in repo | Win, Mac, Linux (R1) | – | 0+8 | – | MAP+ | Neo Filter, filter type and LFO shape by table | 3 |
| FSM WahPro2 | fx | Auto-wah with eight filters, including vowel-like ones, and many… | LGPL-2.1, source in repo | Win, Mac, Linux (R1) | – | 0+8 | presets | MAP+ | Neo Filter; the vowel-like filters of the eight have no counterpart (reported) | 3 |
| Oomek Aggressor 303 | gen | Acid bass lines: a resonant low-pass with envelope, accent and slide. | GPL-3.0+, source in repo | Win, Mac, Linux (R1) | Note track (1): Note + Slide + Accent | 8 | 3 in 2 songs, presets | MAP+ | Neo Poly mono legato bass; Neo Acid if the gap is built (G3); table in 7.1 | 2 |
| Oomek Exciter | fx | Add presence and sparkle by blending high-passed, shaped harmonics… | GPL-3.0+, source in repo | Win, Mac, Linux (R1) | – | 4 | 3 in 3 songs, presets | MAP | Neo Exciter (pair exists) | 1 |
| Oomek Masterizer | fx | Three-band limiter for the master, with input gain and per-band… | GPL-3.0+, source in repo | Win, Mac, Linux (R1) | – | 10 | 1 in 1 song, presets | MAP+ | Neo Multiband plus Neo Limiter ceiling (7.7) | 2 |

#### C. Jeskola rebuilds (sources without a licence) (6)

| Machine | Kind | What it does | Source, licence | Platforms | Notes in | Params (global+track) | In the repo | Rec | Target and why | Wave |
|---|---|---|---|---|---|---|---|---|---|---|
| Jeskola Cross Delay | fx | Stereo echoes whose feedback crosses between the left and right lines. | source in repo, no licence (private-use exception) | Win (upstream binary); built, not shipped | – | 1+5 | 2 in 1 song, presets | MAP | Neo Delay (pair exists) | 1 |
| Jeskola Delay | fx | Simple mono delay with up to eight taps, one per track. | source in repo, no licence (private-use exception) | Win x64; Mac/Linux build, not for release | – | 1+4 | 1 in 1 song, presets | MAP | Neo Delay (pair exists) | 1 |
| Jeskola Distortion | fx | Hard, buzzy threshold-and-clamp distortion for aggressive tones. | source in repo, no licence (private-use exception) | Win x64; Mac/Linux build, not for release | – | 5 | 1 in 1 song, presets | MAP | Neo Drive (pair exists) | 1 |
| Jeskola NiNjA dElaY | fx | Multi-tap delay with a filter in the feedback loop, with tap… | source in repo, no licence (private-use exception) | Win x64; Mac/Linux build, not for release | – | 1+7 | 1 in 1 song | MAP | Neo Delay (pair exists) | 1 |
| Jeskola Noise Generator | gen | Noise bursts with an envelope and a colour from dark to white, for… | source in repo, no licence (private-use exception) | Win x64; Mac/Linux build, not for release | trigger columns (8) | 0+5 | 2 in 2 songs | MAP+ | Neo Poly with the noise source only: triggers become notes whose length is Sustain; table in 7.4 | 2 |
| Jeskola Trilok | gen | Synthesised kick drum fired by a trigger, with tone, decay and volume. | source in repo, no licence (private-use exception) | Win x64; Mac/Linux build, not for release | trigger | 4 | – | MAP+ | Neo Drums kick voice (tune, decay, level); small table in 7.8 | 2 |

#### D. Pedal (ExternalMachines, managed) (19)

| Machine | Kind | What it does | Source, licence | Platforms | Notes in | Params (global+track) | In the repo | Rec | Target and why | Wave |
|---|---|---|---|---|---|---|---|---|---|---|
| Pedal Chord | control | Turn each note in a track into a chord or arpeggio on a target… | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | Note track in, notes out | 0+12 | – | BAKE | Chords and arpeggios expanded into the target's notes | 3 |
| Pedal Chorus | fx | Four-voice stereo chorus that widens without much pitch wobble. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 6 | presets | MAP | Neo Ensemble (pair exists) | 1 |
| Pedal Comp | fx | Feed-forward compressor with soft knee, lookahead and parallel mix… | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 10 | presets | MAP | Neo Comp (pair exists) | 1 |
| Pedal Drum Matrix | fx | A six-slot rack of drum effects (bit crush, drive, filters, comb,… | GPL-3.0, source in repo | Win, Mac, Linux (no WPF GUI off Windows) | – | 49 | presets | KEEP | Six-slot rack of drum effects (GPL-3.0); Neo Lo-Fi and Neo Drive cover parts. Hidden | 4 |
| Pedal EQ | fx | Four-band parametric EQ with shelves, bells and per-band solo. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 16 | presets | MAP | Neo EQ (pair exists) | 1 |
| Pedal FM | gen | Four-operator FM keys, bells and basses with eight algorithms. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | MIDI + Note tracks | 42+1 | presets | MAP+ | Neo FM6 (four operators on 1 to 4; eight algorithms to DX7 algorithms) | 3 |
| Pedal Filter | fx | Stereo multimode filter with several slopes, drive and a comb mode. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 12 | presets | MAP | Neo Filter (pair exists) | 1 |
| Pedal Gain | fx | Stereo gain in percent with a smooth mute. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 3 | – | MAP | Neo Utility (pair exists) | 1 |
| Pedal Gain Multi | fx | A small mixer of up to 24 inputs, each with fader, pan and mono. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 3+5 | – | MAP | Dissolves into the wires (up to 24 inputs: fader, pan, mono) | 2 |
| Pedal Gate | fx | Noise gate with hysteresis, hold and peak or RMS detection. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 9 | – | MAP | Neo Gate (pair exists) | 1 |
| Pedal Juno106 | gen | Warm six-voice analog-style pads, stabs and basses with a resonant… | GPL-3.0, source in repo | Win, Mac, Linux (no WPF GUI off Windows) | MIDI + Note tracks | 25 | presets | MAP+ | Neo Poly (+ Neo Ensemble for the chorus); recipe in 7.9 | 3 |
| Pedal LFO | control | Move another machine's parameter with a low-frequency wave. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 8 | – | MAP | Neo LFO (pair exists) | 1 |
| Pedal M1 | gen | Two-oscillator sample synth for sampled keys, pads and layered… | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | MIDI + Note tracks | 53+1 | – | MAP | Neo Sampler (pair exists) | 1 |
| Pedal MComp | fx | Four-band compressor for gluing and balancing a bus or master. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 35 | presets | MAP+ | Neo Multiband (four bands) | 2 |
| Pedal Peer Control | control | Drive parameters of other machines from track values through… | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | value tracks (64) | 1+2 | – | BAKE | Its track values become parameter lanes of the machines it drove | 3 |
| Pedal Plate | fx | Smooth plate reverb for vocals and snares, with pre-delay and a… | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 9 | presets | MAP | Neo Verb (pair exists) | 1 |
| Pedal SH101 | gen | Monophonic basses and leads with sub oscillator, glide and a… | GPL-3.0, source in repo | Win, Mac, Linux (no WPF GUI off Windows) | MIDI + Note tracks | 25 | presets | MAP+ | Neo Poly (mono, sub, glide); recipe in 7.9 | 3 |
| Pedal Shaper | fx | Waveshaping distortion with several curves and a tilt EQ before… | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | – | 8 | – | MAP+ | Neo Drive (curves to modes, tilt EQ to Tone) | 3 |
| Pedal Tracker | gen | A sixteen-track tracker that plays waves from the song's wavetable. | source in repo, no licence | Win, Mac, Linux (no WPF GUI off Windows) | MIDI + Note tracks (16) | 20+6 | – | MAP+ | Neo Sampler, Tracker mode (G1) | 2 |

#### E. ReBuzz managed machines (source in the repo) (9)

| Machine | Kind | What it does | Source, licence | Platforms | Notes in | Params (global+track) | In the repo | Rec | Target and why | Wave |
|---|---|---|---|---|---|---|---|---|---|---|
| AudioBlock | gen | Lay recorded audio clips on a timeline inside the song, such as… | MIT (ReBuzz), source in repo | Win; DSP portable | – | 1 | – | KEEP | Audio-clip editor with noise removal; Neo Audio plays and warps clips but cannot read its blob. Revisit on census | 4 |
| Connection Mixer Console | gen | Balance the levels and pans of many machine connections from one… | MIT (ReBuzz), source in repo | Win; DSP portable | – | 32 | – | MAP | Dissolves into the wires: its levels and pans become connection amp and pan (Session mixer) | 2 |
| EnvelopeBlock | control | Draw parameter envelopes over patterns and play them into the… | MIT (ReBuzz), source in repo | Win; DSP portable | – | 1 | – | BAKE | Its drawn envelopes become parameter lanes of the target machine's clips (the clip store takes envelopes natively) | 3 |
| ManagedMultiIOEffect | fx | A developer example of an effect with several inputs and outputs;… | MIT (ReBuzz), source in repo | Win; DSP portable | – | 3 | – | DROP | A developer example | 4 |
| QuickNote | control | A notes window for the song; it does nothing to the sound. | MIT (ReBuzz), source in repo | Win; DSP portable | – | 1 | – | MAP | Song Notes | 1 |
| ReBuzz Audio In | gen | Bring the audio device's input into the song. | MIT (ReBuzz), source in repo | Win; DSP portable | – | 1 | – | STAY | Platform machine; its name is kept in songs | – |
| ReBuzz Audio Out | fx | Send what is connected to it to other outputs of the audio device. | MIT (ReBuzz), source in repo | Win; DSP portable | – | 1 | – | STAY | Platform machine; its name is kept in songs | – |
| ReBuzz Groove | control | Swing for the whole song: up to eight steps that stretch or… | MIT (ReBuzz), source in repo | Win; DSP portable | – | 9 | – | STAY | Platform machine (song swing) | – |
| ReBuzz Midi Output | control | Send notes and controller changes from its tracks to a MIDI output… | MIT (ReBuzz), source in repo | Win; DSP portable | Note tracks (32) to MIDI out | 0+4 | – | STAY | Platform machine; the MIDI bridge to external gear | – |

#### F. Prebuilt managed machines (upstream binaries) (14)

| Machine | Kind | What it does | Source, licence | Platforms | Notes in | Params (global+track) | In the repo | Rec | Target and why | Wave |
|---|---|---|---|---|---|---|---|---|---|---|
| DAWControl | control | A control-only helper with a single bypass switch; it makes no… | prebuilt, source not in repo | Win (WPF) | – | 1 | – | STAY | Platform helper the app references (bypass switch); not offered in the browser | – |
| EasyRec | fx | Record the audio passing through it into a wave slot, to resample… | prebuilt, source not in repo | Win (WPF) | – | 4 | – | KEEP | Records into a wave slot; Neo Audio recording covers new work, old data stays | 4 |
| GuitarChordBox | control | Strum guitar-style chords from single notes, with per-string… | prebuilt, source not in repo | Win (WPF) | Note track in, notes out | 12 | – | BAKE | Strums expanded into notes of the target's clips | 4 |
| Info | control | Holds free text about the song in its own window; it does nothing… | prebuilt, source not in repo | Win (WPF) | – | 1 | – | MAP | Song Notes (machine-safety.md 3.5): its text is imported; the machine goes | 1 |
| Jeskola Gain | fx | Plain gain trim between machines. | prebuilt, source not in repo | Win (WPF) | – | 2+1 | – | MAP | Neo Utility (pair exists) | 1 |
| Jeskola Live | control | Older performance helper steered by a Bar control; the Session… | prebuilt, source not in repo | Win (WPF) | – | 1 | – | DROP | Superseded by the Session View; loads as a silent placeholder with a warning, its data kept | 4 |
| Jeskola Loop Recorder | gen | Older helper for capturing loops of the audio passing through it. | prebuilt, source not in repo | Win (WPF) | – | 1 | – | DROP | Loop capture lives in Session recording and Neo Audio; placeholder with a warning | 4 |
| Jeskola Note Matrix | control | Edit notes on a grid in its own window. | prebuilt, source not in repo | Win (WPF) | – | 1 | – | DROP | A note grid in its own window; the clip note lane is the replacement. Placeholder, data kept | 4 |
| Jeskola Relativion | control | Shift and spread notes across a key range relative to a root note. | prebuilt, source not in repo | Win (WPF) | – | 6 | – | BAKE | Shifted and spread notes written into the target's clips | 4 |
| JumpLoop | control | Jump or loop the song position from a pattern, for live… | prebuilt, source not in repo | Win (WPF) | – | 5 | – | KEEP | Moves the song position from a pattern; no Neo equivalent (follow actions are per clip). Hidden | 4 |
| Modern Sequence Editor | control | Edit the song's sequence in its own window. | prebuilt, source not in repo | Win (WPF) | – | 1 | – | STAY | The app's own editor (hidden machine in every song) | – |
| SpectrumBlock | gen | A spectrum and level display with its own window. | prebuilt, source not in repo | Win (WPF) | – | 12 | – | DROP | A display only; Neo Analyzer and Neo Scope replace it | 4 |
| TrackScript | control | A scripting helper that drives other machines from code in its own… | prebuilt, source not in repo | Win (WPF) | – | 1 | – | KEEP | Runs user scripts; cannot be converted. Hidden | 4 |
| Yitten Mongoose | control | An older control machine steered by a Bar control; it makes no… | prebuilt, source not in repo | Win (WPF) | – | 1 | – | KEEP | Control machine steered by a Bar control; behaviour is in its saved data. Hidden | 4 |

#### G. Polac I/O and VST hosts (closed) (8)

| Machine | Kind | What it does | Source, licence | Platforms | Notes in | Params (global+track) | In the repo | Rec | Target and why | Wave |
|---|---|---|---|---|---|---|---|---|---|---|
| Polac Audio In | gen | Bring an audio input into the song as a source (older routing… | closed binary | Win only | – | 1 | – | MAP | ReBuzz Audio In (same job; device input) | 4 |
| Polac Audio Out | fx | Send audio to an output of the audio device (older routing machine). | closed binary | Win only | – | 1 | – | MAP | ReBuzz Audio Out | 4 |
| Polac MIDI Bus II | control | Route MIDI between machines (older routing machine). | closed binary | Win only | – | 1 | – | KEEP | MIDI routing between machines; no Neo role yet. Hidden | 4 |
| Polac MIDI In | control | Receive MIDI from a device into the song (older routing machine). | closed binary | Win only | – | 1 | – | KEEP | MIDI device input; the app's MIDI map covers new work. Hidden | 4 |
| Polac Out | fx | Output stage with volume and panning to the audio device (older… | closed binary | Win only | – | 4 | – | MAP | Master (volume and pan become the connection's) | 4 |
| Polac Out II | fx | Output stage with a volume control to the audio device (older… | closed binary | Win only | – | 2 | – | MAP | Master (volume becomes the connection's) | 4 |
| Polac VST 1.1 | fx | Host a 32-bit effect plugin in the graph (older host; Windows only). | closed binary | Win only | – | 6+3 | – | KEEP | As Polac VSTi 1.1 | 4 |
| Polac VSTi 1.1 | gen | Host a 32-bit instrument plugin in the graph (older host; Windows… | closed binary | Win only | Note tracks (128) | 5+3 | – | KEEP | Hosts a 32-bit plug-in; Neo Plug takes VST3 and CLAP only. Convert by plug-in name where a VST3 twin exists (census) | 4 |


### 3.2 What the repo's songs, templates and presets name

**Songs.** 20 old songs (`.bmx` 19, `.bmw` 1; Buzz-era demos in `ExternalMachines/source` and `Other`), parsed by a throwaway script over the `PARA` and `MACH` sections (not committed; the same facts as clips.md section 3). 18 parse; `FSM_Kick_Demo` and `Makk_M4` have no `PARA`, so their machines were read from their strings (FSM KickXP; Makk M4).

| Song | Machines | With a pair today | In the package, no pair | Not in the package |
|---|---|---|---|---|
| CyanPhase_DTMF-1_Demo.bmx | 5 | 1 | 2 | 2 |
| Elak_Dist2.bmx | 2 | 0 | 0 | 2 |
| Elak_SVF.bmx | 2 | 1 | 0 | 1 |
| elenzil_ampmod.bmx | 11 | 0 | 0 | 11 |
| FSM_Infector_Demo.bmx | 11 | 3 | 7 | 1 |
| FSM_Kick_Demo.bmx | 1 (from strings: FSM KickXP) | 1 | 0 | 0 |
| FrequencyUnknown_O-Delay_delaytest.bmx | 3 | 0 | 0 | 3 |
| FrequencyUnknown_O-Delay_sparkle.bmx | 3 | 1 | 0 | 2 |
| Jeskola_NiNjA_dElaY.bmx | 2 | 1 | 0 | 1 |
| MadBrain_Dynamite6_Demo.bmx | 12 | 4 | 0 | 8 |
| Makk_M4.bmw | 1 (from strings: Makk M4) | 0 | 0 | 1 |
| Oomek_Aggressor_ctrack.bmx | 1 | 0 | 1 | 0 |
| Oomek_Aggressor_demo.bmx | 12 | 4 | 2 | 6 |
| 909.bmx | 2 | 2 | 0 | 0 |
| 909_excite.bmx | 3 | 2 | 0 | 1 |
| test.bmx | 4 | 2 | 0 | 2 |
| Masterizer_test.bmx | 22 | 5 | 5 | 12 |
| 11Stereo.bmx | 3 | 0 | 0 | 3 |
| Csi.bmx | 3 | 1 | 0 | 2 |
| sinesynthdemo.bmx | 1 | 0 | 0 | 1 |


| Total | Instances | Share |
|---|---|---|
| Machine instances (editors and Master left out) | 103 (104 with Makk M4) | |
| With a pair today (wave 1) | 28 | 27 % |
| In the package, no pair | 17 | 17 % |
| Not in the package | 58 | 56 % |

After wave 2 another 16 instances are covered (Infector 5, Aggressor 303 3, Noise Generator 2, PhatMan 2, Jeskola Tracker 2, Stereo Reverb 1, Masterizer 1) and wave 3 adds Wave Shaper: **every in-package instance is covered after wave 3** (45 of 45). The 58 others are 36 library names the package does not ship (list below): they already open as placeholders, keep their data and save back unchanged, and this phase leaves it so unless the census shows one worth a pair.

**Templates.** `Bass House Template.nbl` and `Tech House Template.nbl` name Master, Neo EQ, Neo Comp, Neo Clipper, Neo Limiter, Neo Multiband, Neo Drums, Dye Essence and nine Modern Pattern Editor machines (the editors are platform machines). **No legacy machine.** (Template songs in `.nbl` are read by the same loader, so they need no conversion.)

**Presets.** The repo holds 80 `.prs.xml` files and 20 upstream `.prs` files. 47 `.prs.xml` belong to Neo machines and Dye Essence. The other 33 (some duplicated across folders) are factory presets of legacy machines, written in units and compiled through `param_set`'s parser: FSM Infector, KickXP, PanzerDelay; Jeskola Bass 3, Cross Delay, Delay, Distortion, EQ-3, Filter, Reverb; Joachims Jupiter 3, Mars filter 2, Neptune 1.2, Saturn 1.4; Oomek Aggressor 303, Exciter, Masterizer; Pedal Chorus, Comp, Drum Matrix, EQ, FM, Filter, Juno106, MComp, Plate, SH101 (and Pedal Spectral, a Buzz 1503 port outside the profile). Section 8.5 says what happens to them. No preset or template names a machine outside these lists.

**Machines the songs name that the package does not ship** (library name as saved; role guessed from the name only, none has a manifest, so no unit mapping exists):

| Library name | Instances | Role by name | Nearest Neo | Note |
|---|---|---|---|---|
| elenzil_amplitude_modulator | 6 | amplitude modulation | Neo LFO on Neo Utility | Same song; no Neo role as one machine |
| MadBrain's Dynamite 6 | 6 | drum synth | Neo Drums | Source in Buzztrax, not shipped |
| Geonik's Expression | 3 | unknown | – | |
| asedev Gain01, Gain01 (Stereo), Gain03 | 7 | gain | Neo Utility | |
| Jeskola Freeverb | 2 | reverb (Freeverb algorithm) | Neo Verb | Source in Buzztrax (`Jeskola/Freeverb`), not among the 17 |
| Frequency UnKnown O-Delay, 11-Delay v1.1 | 3 | delay | Neo Delay | |
| Jeskola Reverb 2 | 2 | reverb | Neo Verb | |
| Geonik's Saturator, Elak_Dist_2, Dave's Smootherdrive | 4 | saturation | Neo Drive | |
| Automaton Compressor mkII, Geonik's Compressor | 3 | compressor | Neo Comp | |
| Geonik's Dolby Surround | 2 | stereo width | Neo Imager | |
| Geonik's Gate | 1 | gate | Neo Gate | |
| Elak SVF, Geonik's 2p Filter | 2 | filter | Neo Filter | |
| Automaton EQ-10 | 1 | graphic EQ | Neo EQ graphic mode | |
| HD Limiter Quad 4.1.2 | 1 | limiter | Neo Limiter | |
| Arguelles Degrader | 1 | bit reduction | Neo Lo-Fi (G2) | |
| Rout 808 | 1 | drum machine | Neo Drums | |
| Zephod Bass4, Zephod VoidBass, Arguelles TB3003, TB4004, Guru4, Geonik's Omega-1, M4 (Makk), Sine, Jeskola Sine Synth | 9 | synths and bass machines | Neo Poly | |
| CyanPhase DTMF-1, CyanPhase UnNative Effects, 11-Stereo, 11-CSI | 4 | tone dialler and others | – | |

---

## 4. The Neo family today, and its gaps

51 machines (profile, 2026-11-14): 18 synths (Neo Poly, Wave, FM6, Patch, 13 Neo Learn teaching synths, Dye Essence), Neo Drums, Neo Sampler, Neo Audio, 7 dynamics (Comp, Multiband, Limiter, Gate, De-esser, Transient, Strip), Neo EQ, Neo Filter, Neo Spectral, 4 distortion (Drive, Clipper, Exciter, Tape), Neo Verb, Neo Delay, Neo Ensemble, Neo Tune, 6 utility (Utility, Imager, Low End, Analyzer, Scope, Dither), 6 control (LFO, Envelope, Step, Macro, Voice to MIDI, Audio to MIDI). Neo Plug and Neo Plug FX host plug-ins.

| Role classic songs rely on | Neo machine | Gap |
|---|---|---|
| 303-style acid bass | Neo Poly: mono, legato, glide, ladder 24 or 12 dB, velocity to filter | **G3** (small): no accent circuit (an accented note also opens the filter and swells the envelope), no 3-pole diode filter, no slide per note beyond legato glide time |
| Tracker sampler (a wave per note, effect columns) | Neo Sampler: 16 zones by key and velocity, Kit mode | **G1**: zones are a fixed map from keys to waves; a tracker picks a wave per note. machines.md "No pair for the trackers" |
| Drum synths and kicks | Neo Drums (12 voices, GM keys, hit columns), Neo Sampler kit | none (Rout 909, KickXP pairs exist; Trilok is a kick) |
| Distortion types | Neo Drive (tape, tube, transistor, diode, fold), Clipper, Tape, Exciter | Bit and sample-rate reduction, noise, detune (Joachims Saturn, Pedal Drum Matrix, Arguelles Degrader): **G2**. Mars filter 2's 13 characters fold onto 5 modes |
| Filters, wahs | Neo Filter (ladder, SVF, drive, envelope follower, LFO), Neo Poly's | none for the closed wahs but WahPro2's vowel filters |
| Delays | Neo Delay (stereo, ping-pong, cross, tempo sync, filters, saturating loop) | none |
| Reverbs | Neo Verb (hall, room, chamber, plate, freeze, shimmer) | none (algorithms differ: tail density and early pattern) |
| Chorus, flanger, phaser | Neo Ensemble (1 to 4 voices, 2 to 12 stages) | PhatMan's up to 8 lines map to at most 4 voices |
| Compressors, limiters, multiband | Neo Comp, Multiband, Limiter, Strip | none |
| EQs | Neo EQ (8-band parametric, graphic 31-band), Strip | none |
| Gates, transient shaping | Neo Gate, Neo Transient | none |
| Gain, mixers, splitters | Neo Utility; the Session mixer and a connection's amp and pan | **no multi-input mixer machine, by design**: Pedal Gain Multi, mixIO and Connection Mixer Console dissolve into connection gains and pans. Splitters are not needed: any machine's output connects to many |
| LFO, envelopes, step, macro | Neo LFO, Envelope, Step, Macro; clip parameter lanes | Peer control and relative control (note to parameter, note shifting) are baked into clips |
| Chords, arpeggios, note remapping | none (Voice to MIDI and Audio to MIDI make notes from audio) | Not a legacy need: baking keeps old songs. A live **note effects** machine (chord, arp, scale) is a feature for later (G6), not part of this phase |
| Organs | Neo Learn Additive (teaching), Neo Poly | **G4**: no drawbar organ. Jeskola Organ stays legacy until the census says otherwise |
| Pitch shifting, FFT effects | Neo Tune (correction), Neo Spectral (resonance suppression) | **G5**: Joachims Neptune (pitch shift, bin scramble) has no Neo role; it is flagged unhealthy; stays legacy |
| Chip tunes | Neo Poly pulse and noise | KrimZon AY-3-8910 stays legacy |
| Audio clips | Neo Audio | AudioBlock's own audio-editor blob is not read (stays legacy) |
| Plug-in hosting | Neo Plug, Neo Plug FX (VST3, CLAP) | Polac VST/VSTi host 32-bit VST2: no equivalent; conversion by plug-in name where a VST3 twin exists (census) |
| Audio and MIDI I/O | ReBuzz Audio In/Out, Midi Output (platform machines, kept) | none |

**Gaps to build (clean room, literature and documentation only; no code from any legacy machine):**

| Gap | What | Size | Why it is worth it |
|---|---|---|---|
| **G1 Tracker mode in Neo Sampler** | A mode where **each note carries a sample number** that selects the wave (root C-4 by the tracker convention), per-note volume as velocity, sample offset as start, tuning and finetune as tune. One Neo Sampler holds all of a song's waves; there is no 16-wave limit (decision D5 A: the channel stays free for routing). Row effects a note list cannot carry (volume and pitch slides, retrigger, sample offset) become lanes where they map (volume slide to a parameter ramp on level, offset to start) and are otherwise **named in the loss report**. | 2 pw machine + 2 pw converter + 1 pw tests = **4 to 5 pw** | Trackers are the sample players of almost every Buzz song. Today none of them can be converted (machines.md) |
| **G2 Neo Lo-Fi** | Bit-depth and sample-rate reduction (with and without anti-alias), noise and crackle, wow and flutter light, detune; zero latency | **1.5 to 2 pw** | Saturn, Drum Matrix, Degrader; also a sound-design staple the family lacks. DSP is elementary and fully measurable |
| **G3 Acid in Neo Poly** | F1 type "Diode 18" (a 3-pole ladder), an accent stage (velocity above a threshold adds to the filter envelope, shortens its decay, swells the amp), and a per-note slide flag in the notes lane (a legato overlap already is one) | **1 to 1.5 pw** as a Neo Poly extension; a separate Neo Acid machine would cost about 3 pw and add nothing the extension does not | Aggressor 303, Jeskola Bass 3 and Neo's own acid presets |
| **G4 Neo Organ** | Drawbar organ, key click, rotary | 2 to 3 pw | Planned in LM3 (decision D2 B) |
| **G5 pitch and FFT effects** | Pitch shifter, spectral effects | – | Not planned; Neptune stays legacy |

---

## 5. Recommendation per machine

The inventory table (3.1) gives each machine's recommendation and target. The rules behind it:

1. **Map** when a Neo machine does the same job and the difference can be stated in words and measured: effects and synths with a one-to-one role. A pair is a mapping file plus a calibration; **0.3 to 0.5 pw** each, 1 pw for a synth.
2. **Dissolve into the wires** for mixers: the Session mixer and a connection's amp and pan are the replacement. No machine is built.
3. **Bake** for machines that only compute notes or parameter values for other machines. Their output is written into the target's clips as notes and lanes (the unified store takes envelopes and notes natively), and the machine goes. What is lost: the live behaviour (change the chord machine's setting and the song follows). Said in the loss report.
4. **Rebuild** only where no Neo machine can take the role and the machine matters to real songs (G1, G2). Clean room: documentation and listening measurements only, no code from a GPL or closed machine, whatever its licence says about reading. Every rebuild comes with a pair file for the classic machine it succeeds.
5. **Keep as legacy** where the machine is closed or niche, has no Neo role, and converting it would invent behaviour. It plays on Windows, its clips are view-only, it is hidden from the browser, and its data is kept on every platform (a placeholder elsewhere).
6. **Drop** only for a machine whose function is now a core feature (Jeskola Live, Loop Recorder, Note Matrix, SpectrumBlock) or a developer example. It loads as a silent placeholder with a warning; its data is kept in the song.

**Keep as legacy, 16:** AudioBlock, Bigyo Wood One, EasyRec, Intoxicat ACloud15, Jeskola Organ, JumpLoop, KrimZon AY-3-8910, Polac MIDI Bus II, Polac VSTi 1.1, TrackScript, Yitten Mongoose (generators and controls), Joachims Chopper 2, Joachims Neptune 1.2, Pedal Drum Matrix, Polac MIDI In, Polac VST 1.1 (effects). Decision D4 B: they are dropped (a silent placeholder and a warning on open).

**Licence notes for a public release.** Pedal's 16 unlicensed machines and the Jeskola six are the same blocker the ExternalMachines README names. This phase makes it smaller: after conversion a Pedal or Jeskola machine need not ship at all for songs to work (decision D3). The GPL machines (Pedal Juno106, SH101, Drum Matrix; Oomek's three) are not a source for any rebuild.

---

## 6. The pairs that exist, and how a mapping is measured offline

### 6.1 The 30 pairs

Tolerance is the pair's own, read from its file: the loudness of the machine's output and the third-octave bands over the stated range, as `AudioReport.Tolerance` judges them in `machine_replace`. "Unmapped" counts parameters the pair names as not carried, each with its reason.

| From | To | Loudness (dB) | Third octaves (dB) | Over | Unmapped | Repo use | Evidence |
|---|---|---|---|---|---|---|---|
| FSM Chorus2 | Neo Ensemble | ±1.5 | ±4 | 63 Hz – 12 kHz | 0 | – | measured on Windows |
| FSM KickXP | Neo Drums | ±3 | – | 30 Hz – 12 kHz | 6 | 1 | measured on Windows |
| FSM PanzerDelay | Neo Delay | ±1 | ±2 | 63 Hz – 12 kHz | 6 | 1 | measured on Windows |
| FSM WahMan | Neo Filter | ±1 | ±2.5 | 50 Hz – 12 kHz | 2 | – | measured on Windows |
| Jeskola Bass | Neo Poly | ±3 | – | 50 Hz – 12 kHz | 0 | 1 | measured on Windows |
| Jeskola Bass 2 | Neo Poly | ±3 | – | 50 Hz – 12 kHz | 0 | – | measured on Windows |
| Jeskola Bass 3 | Neo Poly | ±3 | – | 50 Hz – 12 kHz | 0 | 2 | measured on Windows |
| Jeskola Chorus | Neo Ensemble | ±1.5 | ±3 | 63 Hz – 12 kHz | 0 | – | measured on Windows |
| Jeskola Cross Delay | Neo Delay | ±0.5 | ±1 | 63 Hz – 12 kHz | 0 | 2 | measured on Windows |
| Jeskola Delay | Neo Delay | ±0.5 | ±1 | 63 Hz – 12 kHz | 0 | 1 | measured on Windows |
| Jeskola Distortion | Neo Drive | ±2 | ±6 | 80 Hz – 12 kHz | 2 | 1 | measured on Windows |
| Jeskola EQ-3 | Neo EQ | ±1.5 | ±5 | 50 Hz – 12 kHz | 1 | 4 | measured on Windows |
| Jeskola Filter | Neo Filter | ±1 | ±2.5 | 50 Hz – 12 kHz | 1 | 1 | measured on Windows |
| Jeskola Filter 2 | Neo Filter | ±1 | ±2.5 | 50 Hz – 12 kHz | 0 | 2 | measured on Windows |
| Jeskola Flanger | Neo Ensemble | ±1.5 | ±3 | 63 Hz – 12 kHz | 0 | 1 | measured on Windows |
| Jeskola Gain | Neo Utility | ±0.2 | ±0.3 | 20 Hz – 20 kHz | 1 | – | measured on Windows |
| Jeskola NiNjA dElaY | Neo Delay | ±1 | ±2 | 63 Hz – 12 kHz | 1 | 1 | measured on Windows |
| Jeskola Reverb | Neo Verb | ±1.5 | ±6 | 63 Hz – 10 kHz | 0 | 1 | measured on Windows |
| Joachims Hyperion | Neo EQ | ±1 | ±2 | 31.5 Hz – 16 kHz | 0 | – | measured on Windows |
| Oomek Exciter | Neo Exciter | ±3 | ±6 | 80 Hz – 12 kHz | 1 | 3 | measured on Windows |
| Pedal Chorus | Neo Ensemble | ±1 | ±2.5 | 63 Hz – 12 kHz | 0 | – | see file |
| Pedal Comp | Neo Comp | ±1 | ±2 | 40 Hz – 16 kHz | 1 | – | see file |
| Pedal EQ | Neo EQ | ±0.2 | ±0.5 | 20 Hz – 20 kHz | 4 | – | see file |
| Pedal Filter | Neo Filter | ±1 | ±3 | 50 Hz – 12 kHz | 0 | – | measured on Windows |
| Pedal Gain | Neo Utility | ±0.1 | ±0.2 | 20 Hz – 20 kHz | 1 | – | see file |
| Pedal Gate | Neo Gate | ±0.1 | ±0.5 | 40 Hz – 16 kHz | 0 | – | see file |
| Pedal LFO | Neo LFO | – | – | – | 6 | – | – |
| Pedal M1 | Neo Sampler | ±1 | ±2 | 100 Hz – 12 kHz | 15 | – | see file |
| Pedal Plate | Neo Verb | ±1.5 | ±6 | 50 Hz – 12 kHz | 1 | – | see file |
| Rout 909 | Neo Drums | ±3 | – | 40 Hz – 16 kHz | 5 | 6 | measured on Windows |


Expected difference in plain words, from the pairs' own remarks (the others are in their files):

- **Jeskola Reverb to Neo Verb.** Level, length and colour are close; the tail's density and the early reflections' pattern are not (a 2000s reverb against a 16-line feedback delay network). Neo Verb is stereo where Jeskola's is mono: set Width to 0 % for a mono tail.
- **Jeskola EQ-3 to Neo EQ.** EQ-3's band corners move with their gains; Neo EQ's shelves do not. One fit serves boosts and cuts to within 5 dB at plus or minus 12 dB; settings more than 24 dB apart between bands differ more.
- **Jeskola Distortion to Neo Drive.** Distortion jumps from the threshold to the clamp level; Neo Drive rises to it smoothly and is oversampled, so it does not alias. Quiet material comes out louder, loud material the same.
- **Rout 909 to Neo Drums.** Neo Drums was voiced against the 909's samples; each voice is within 3 dB; it has no shuffle (hits land on their rows); the toms' decay knobs did nothing in the original and are not carried.
- **Jeskola Delay, Cross Delay, NiNjA dElaY and FSM PanzerDelay to Neo Delay.** Echo times, sides and levels to within 0.5 to 1 dB; only the first track of a multi-track delay moves.
- **Oomek Exciter to Neo Exciter.** Neo Exciter adds only the harmonics through an oversampled shaper where Oomek adds the whole shaped signal: the harmonic mix differs, the added band's level is matched. Derived from source, not yet measured.

### 6.2 Measuring a mapping offline (no Windows machine on the CI run)

`machine_replace` renders the old machine and its successor in one process, so it needs the legacy DLL: Windows. For a conversion on open, and for CI on Linux and macOS, the legacy side must be a reference taken once.

1. **Reference kit (Windows package job, once per pair).** A fixed set of stimuli through the legacy machine at the pair's defaults and at three settings of each mapped control (minimum, middle, maximum), at three input levels:
   - effects: a logarithmic sine sweep 20 Hz to 20 kHz (10 s; gives the magnitude response and, from the harmonics, the distortion curve), an impulse (delay taps, reverb tail: EDT and T60 per octave), pink noise at minus 30, minus 12 and minus 3 dBFS (level and third-octave transfer), a 1 kHz sine stepped in level (compressor and gate curves), and for modulation effects a sweep measured as delay against time;
   - generators: notes C1 to C7 in steps of a fifth at three velocities (pitch to 5 cents, attack and decay times to 5 %, third-octave spectrum), and for filters a cutoff sweep with the minus 3 dB point read at each setting.
   `tools/native-compare.py --save-renders` already writes reference renders for the 17 natives (the `native-references` artifact); the kit extends it to every machine with a pair, and writes **numbers (curves, times, levels), not audio**, into the pair's `measured` field. No third-party audio is committed.
2. **Pair test (any OS).** A test applies the pair to a Neo machine at the same settings, runs the same stimuli, and compares with the stored numbers against the pair's tolerance. This is what `DrumMachinesMoveOntoNeoDrums` and the pair tests do today on Windows, made independent of the legacy DLL.
3. **Song test (any OS).** The repo's songs and a generated corpus (one song per feature) open, convert, and render. The render is compared with a Windows render of the same song stored as numbers (loudness, third-octave bands, per-bar RMS), not as audio. The tolerance is the sum of the pairs' in the song, stated in the test.
4. **Where a legacy machine cannot be measured at all** (Joachims Neptune, any machine the profile marks unhealthy): no pair is written; it stays legacy.

---

## 7. Mapping tables for the machines without a pair that songs use most

Units are those of the machines' manifests (curated from their own texts and measurements). Where a manifest has no unit (raw 0 to 240), the row says **measure** and names the measurement; no number is guessed. "To" names Neo parameters as they are in `machines.md`.

### 7.1 Oomek Aggressor 303 to Neo Poly (3 instances, 2 songs; source GPL-3.0+, not a source for the mapping: read for its documentation of slide and accent only)

Mono legato bass, as the Jeskola Bass 3 pair does it (voice mode Legato, glide mode Legato, unison 1, O1 shape from Osc Type, O2 and O3 off, ladder 24, amp sustain 100 %).

| Aggressor (raw) | Unit | Neo Poly | How |
|---|---|---|---|
| Osc Type 0 or 1 | saw, square | O1 shape | Saw, Square |
| Cutoff 0 to 240 | unknown | F1 cutoff (Hz) | **Measure**: a cutoff sweep, minus 3 dB point against raw, fitted as a log curve; expected range 80 Hz to 16 kHz as the Bass 3 pair assumes |
| Res 0 to 128 | unknown | F1 reso (%) | **Measure** the Q at 0, 32, 64, 96, 128 |
| Env.Mod 0 to 128 | % | F1 env (%) | Same percent; the octaves it spans are measured |
| Decay 0 to 128 | unknown (a per-sample coefficient) | Flt decay (s) | **Measure** the time to fall 20 dB at four settings |
| Accent Level 0 to 128 | % | F1 velocity (%), Vel to amp | Accented notes are written at velocity 127, plain ones at 90; F1 velocity and Vel to amp set so the accent's cutoff and level lift match, **measured** |
| Finetune 0 to 200 | ct (100 is centre) | O1 fine (ct) | Value minus 100 |
| Volume 0 to 200 | % | Volume (dB) | 20 log10(v / 100); above 100 % is hot (+4.3 dBFS at 200 %) and is clamped to Neo Poly's range with a note |
| Slide (per note) | switch | glide | A slid note overlaps the next (legato); Glide set to a third of a tick, from its source: **1000 x 60 / (BPM x TPB) / 3 ms** at the song's tempo (tempo changes are not followed: reported) |
| Accent (per note) | switch | velocity | 127 |
| Note | note | notes lane | Pitch as written (`pitch_offset` 0), length to the next note or Off |

Not mapped: none of the 8 parameters is dropped. Expected sound: the same note line, cutoff and envelope shape close; Neo Poly's ladder is 24 dB per octave and cleaner where the 303's is a 3-pole diode filter that squelches and saturates. **G3** closes most of that. Tolerance proposed: loudness 3 dB, as Bass 3.

### 7.2 FSM Infector to Neo Poly (5 instances, 1 song; source LGPL-2.1)

Polyphony 12 (Infector's tracks), notes lane. SuperSaw maps to unison.

| Infector | Unit | Neo Poly | How |
|---|---|---|---|
| OSC1 Wave, OSC2 Wave (0 to 22) | table of 23 | O1 shape, O2 shape | Saw, square, pulse, triangle and sine map; SuperSaw: saw with Unison 7 and Uni detune from the table; the 13 others by nearest spectrum, **reported by name** |
| PW Offset (0 to 240) | % | O1 PW, O2 PW | 240 is 100 % |
| PWM Rate (0 to 239), PWM Depth (0 to 239) | none, none | LFO1 rate (Hz), a mod matrix slot to PW | **Measure** the rate; depth as % |
| Transpose (0 to 72) | st | O2 semi | Value minus 36, **checked** against the manifest's centre |
| Detune (0 to 240) | ct | O2 fine | Centre 120; scale **measured** |
| OSC Mix (0 to 240) | % | O1 level, O2 level | Equal-power crossfade, as the Bass 3 pair does for its mix |
| SubOsc Wave, SubOsc Vol | table, % | O3 shape, O3 level, octave -1 | |
| Glide (0 to 240) | % | Glide (s) | **Measure** |
| Flt Type (0 to 17) | table of 18 | F1 type, F2 type | Low, band, high, notch at 12 or 24 dB map; the 6-pole types map to F1 + F2 in series; the rest **reported** |
| Cutoff, Reso, EnvMod | %, %, % | F1 cutoff (Hz), F1 reso, F1 env | Cutoff **measured** (a sweep); reso, env percent |
| Attack, Decay, Sustain, Release (filter) | s, s, %, s | Flt attack, decay, sustain, release | Times **measured** (Infector's are curves) |
| Mod Shp, Inertia | %, none | – | Not carried (envelope curve shape; filter smoothing): reported |
| KTrack | ct | F1 key track (%) | |
| LFO1 and LFO2: Rate (0 to 254), Shape (0 to 16), To Cutoff, To Env, To Res | Hz, table, %, %, % | LFO1 and LFO2 rate (Hz), shape, mod matrix slots to F1 cutoff, F1 env, F1 reso | Eight of the matrix's slots |
| Amp Attack, Decay, Sustain, Release | s, s, %, s | Amp attack, decay, sustain, release | Times **measured** |
| Mode Flags | flags | – | Not carried |
| Command 1 and 2 (track, per row) | commands | – | **Lost** (clips.md section 3: 5 machines use them); named in the report with the number of cells |

Expected sound: the same stack of detuned saws with the same filter and envelopes; the supersaw's exact detune law and the filter's resonance character differ. This is the weakest of the new pairs; proposed tolerance loudness 3 dB, and the report says "sound differs" for it. The presets (FSM Infector.prs.xml) convert through the same table (8.5).

### 7.3 Jeskola Tracker, Matilde Tracker2, Pedal Tracker to Neo Sampler (Tracker mode, G1)

Jeskola Tracker has the profile's one parameter (Subdiv) plus Note, Wave and Volume per track; Matilde Tracker2 has Ampl.Decay, Offset, Quantize, Tuning plus note, instrument and effect columns; Pedal Tracker has 20 and plays MIDI too.

| Tracker | Neo Sampler | How |
|---|---|---|
| Note on a track, Wave n | notes-lane event on **channel n**; zone n holds wave slot n | The converter fills zone n with slot n (wave 0 off) for each wave used; the song's waves are kept as they are |
| Note pitch | key | Root C-4 at the wave's own speed; a tracker's note C-4 plays the wave unpitched: **checked per machine** (pitch_offset) |
| Volume column | velocity | 0 to 0x80 mapped to 1 to 127, Vel to amp 100 % |
| Off | note end | The note's length |
| Matilde Ampl.Decay | amp decay | Measure |
| Matilde Tuning (st) | zone tune | Also fine |
| Offset (sample offset effect) | zone start (%) | A start per note is not carried by a zone: lost beyond the first value; reported |
| Volume slide, pitch slide, retrigger, finetune effects | – | Volume and pitch slides become ramps on Level and Tune; retrigger becomes repeated notes; others **named in the loss report** with their cell counts |
| Subdiv, Quantize | – | A pattern-time convention, dropped with the track timing kept as the notes' own time |

Not verified: that Neo Sampler's tracks reproduce each tracker's pitch convention and sample-start effect to within 5 cents and one sample: this is the measurement of the G1 wave. The note that "neither plays through the tools" (machines.md) must be solved first (Jeskola Tracker's draft manifest has no track parameters).

### 7.4 Jeskola Noise Generator to Neo Poly (2 instances; source without a licence)

A trigger column per track; no pitch.

| Noise Generator | Neo Poly | How |
|---|---|---|
| Trigger (a pattern value) | note on, any key | Length = Sustain |
| Attack (1 to 65535 ms) | Amp attack | ms to s |
| Sustain (1 to 65535 ms, a hold time) | note length | The note is held that long, amp sustain 100 % |
| Release | Amp release | ms to s |
| Color (0 dark to 4096 white) | Noise colour, F1 cutoff | Measured on a noise sweep |
| Volume (0 to 254; 128 = 100 %) | Volume, velocity | dB |

O1, O2 and O3 off, Noise 100 %. Unison and drift off. Expected: the same bursts; the colour curve measured, not guessed.

### 7.5 FSM PhatMan to Neo Ensemble (2 instances)

Each track is a modulated delay line (up to 8): the converter maps lines to Neo Ensemble voices (up to 4), their phase from Phasing and the settings of the first track; more than 4 tracks, or tracks with different settings, are named in the report.

| PhatMan | Unit | Neo Ensemble | How |
|---|---|---|---|
| Minimum delay | s | Delay | Geometric centre with Modulation, as the Chorus2 pair |
| Modulation | s | Depth | |
| LFO rate | Hz | Rate | |
| Feedback ((v-64)/64) | % | Feedback | Capped at plus or minus 99 %; PhatMan's total feedback limit is lost |
| Phasing | degrees | Spread | |
| Wet out, Dry out | dB | Mix, Output | `dry_wet` rule |
| Reset Phase | degrees | – | Not carried |
| LFO Shape attribute | choice | Shape | Sine, triangle |

### 7.6 Jeskola Stereo Reverb and Jeskola Raverb to Neo Verb

Stereo Reverb has the **same six controls as Jeskola Reverb** (Dry Out, Rev Out, ER Out, Rev Time, Pre-Delay, High Cutoff; profile: correlation 0.98): the existing pair file applies, Width left open instead of 0 % (it is stereo). Raverb has four controls (time and high-frequency damping): its own file, `unit` rules to Decay and High damping, **measure** the tail on the impulse. Tolerance as the Reverb pair (loudness 1.5 dB, bands 6 dB).

### 7.7 Oomek Masterizer to Neo Multiband plus Neo Limiter (1 instance; GPL-3.0+)

A three-band limiter with input gain and per-band thresholds: Neo Multiband with three bands, ratio at its limit, attack and release at their minimum with lookahead, per-band threshold from the manifest's dB; the input gain on Neo Multiband's input. The ceiling goes on a Neo Limiter inserted after it, so this pair makes **two machines from one** (the first conversion of that kind: a rule `insert_after`). Expected: close in level; the original's crossover and its limiting law differ, measured on a sweep and on pink noise.

### 7.8 Jeskola Trilok to Neo Drums

Trigger columns to kick hits (BD key); tone to Kick tune, decay to Kick decay, volume to Kick level; the remaining voices off. Four parameters. Measure: pitch over time and the time to fall 20, 40 and 60 dB as `DrumsCalibration` does for the 909.

### 7.9 Pedal Juno106 and Pedal SH101 to Neo Poly

Recipes, not tables yet (25 parameters each): Juno's single DCO with sub, noise and the chorus becomes O1, O3 (sub), Noise and a Neo Ensemble chorus **inserted after** it (the `insert_after` rule); SH101: mono, sub, glide, LP resonant filter, as Bass 3's recipe. Their factory banks (48 and 22 presets) convert through the same rules (8.5). GPL-3.0: the mapping is by measurement and manifest, no code.

---

## 8. The conversion on open

### 8.1 When

On every open path (Studio, headless, agent `song_open`, drop), after all machines are loaded and after the clip migration of clips.md section 7.1 (`ClipMigration`), so the converter reads the one store. Before CU3 lands it reads what `MachineReplace` reads today (engine columns and the pattern editor's tracker columns); the converter takes a `ClipSource` seam so the same code serves both.

1. Plan: for each machine whose gear has a pair (and a Neo successor installed), build the plan. For a machine the platform cannot run (a placeholder on macOS and Linux), **the plan is made from the song's own `PARA` table and the gear's manifest**, no DLL needed: names, raw ranges and defaults are in the song, units are in the manifest. (To verify in the first spike: `MakePlan` takes the manifest by DLL name and the machine's parameter groups; whether a placeholder's groups are complete enough is not tested here.)
2. Preview: a dialog (decision D1) lists the conversions with their loss report. Default Convert; Keep legacy per machine or for all; the choice is remembered in the song.
3. Apply: every conversion in one undo group (`BeginActionGroup`), without renders (`Compare = false`). A render comparison is a command on Windows (`Verify conversion`, the existing before and after) and the numbers from section 6.2 tell CI.
4. The song is marked converted in memory. **The file on disk is not touched**: an `.bmx`, `.bmw` or `.bmxml` has `LegacyOrigin`, and Save asks for a name (native-format.md section 7).

### 8.2 What a conversion does to each part of the song

| Part | What happens | Exists today |
|---|---|---|
| **Machine** | The successor takes the name, position, mute and bypass of the old machine; a free name if the old stays | yes |
| **Wires** | The same connections in and out with their amp and pan; the pair's `input_gain_db` and `output_gain_db` go on them (a mono native summed left and right takes plus 6 dB on its inputs). Machines that dissolve (mixers) write their levels onto the connections of their inputs and delete themselves | yes (not the dissolve) |
| **Clips: parameters** | Each parameter column of each clip of the old machine becomes a lane of the successor's clip with the same name and length, values through the pair's rules, ramps kept, clamped with a note where a value cannot be reached | yes |
| **Clips: notes** | Note tracks become **notes-lane events** (time, length, velocity, channel; voice = the old track), in the successor's pitch convention (`pitch_offset`), slides as glides, accents as velocity, the volume column as velocity; Off as the note's length. Trigger columns become notes of a fixed length where the pair says | yes (track notes), the lane form needs CU3 |
| **Clip metadata** | Launch settings, loop region, scene follow actions, placement: `SessionLayout.Adopt` moves tracks, slots, colours, returns, cue and crossfader side | yes |
| **Arrangement and automation** | The successor's sequences are made beside the old ones with the same events; automation lanes of the arrangement retarget to the successor's parameters through the same rules | yes (sequences), retargeting of lanes to verify |
| **Controllers pointing at the machine** (Neo LFO, Envelope, Step, Macro, Pedal LFO, Peer Control, EnvelopeBlock, MIDI bindings) | **New.** Each binding names a parameter of the old machine; after conversion it names the pair's target parameter and its range is re-expressed through the rule (the LFO's depth in units of the new parameter). A binding with no target is listed | **no** |
| **Presets** | See 8.5 | no |
| **Waves** | Untouched (the song's wavetable stays; Tracker mode's zones reference slots) | n/a |
| **The old machine** | Not loaded any more. Its saved state, its patterns and its parameter values go into a hidden `legacy` section of the song (a blob per converted machine, with the gear name and the pair used) so nothing is deleted and Export for Buzz can write the original machine back | **new** |

### 8.3 Read-only until converted

Every machine that is not Neo, not platform, and not converted is a legacy machine: it plays, and:

- the clip editors (note roll, tracker, parameter lanes, envelope lane), the tracker's and the Session View's editing commands show the clips view-only, with one line: "Jeskola Reverb is a legacy machine. Convert to Neo Verb to edit";
- the agent's write tools (`notes_write`, `automation_write`, `pattern_*`, `param_set` on its clips) refuse with the same reason and name the successor and the call that fixes it (`machine_replace`);
- parameters stay editable (turning a knob is not editing clips);
- legacy machines are not in the browser, the machine menu's Add, or the agent's `gear_list` (unless asked), and the machine-profile's `health` still flags the unhealthy ones;
- a machine marked **STAY** (platform) is editable and offered.

The mark is one manifest field, `family`: `neo`, `platform` or `legacy` (every manifest under `Machines/Manifests/` gets it from `tools/machine_catalog.py --write`). Unknown gear (a library the package does not ship) is `legacy`.

### 8.4 The loss report

One list, in the style of `ExportReport` (native-format.md section 11.2): `Category | Item | What happens`, distinct and sorted, shown grouped in the dialog, returned by the agent as `{converted[], kept[], dropped[], losses[], lossless}`, and written into the song's `legacy` section with the song's name and date.

| Category | Item | What the report says |
|---|---|---|
| Machine converted | Jeskola Reverb "Hall" to Neo Verb | Pair, tolerance (loudness 1.5 dB, bands 6 dB over 63 Hz to 10 kHz), "measured on Windows" or "derived from source" |
| Sound difference | per machine | The pair's remark in plain words ("the tail's density and the early reflections' pattern are not the same") |
| Parameter not carried | machine: parameter | Name and the reason from `unmapped` ("Shuffle: Neo Drums has no swing") |
| Parameter clamped | machine: parameter: from to | "Volume 200 % is above Neo Poly's range: set to +12 dB" |
| Parameter approximated | machine: parameter | "Flt Type 14 (6-pole notch) is F1 and F2 in series" |
| Clip columns lost | machine / clip: K cells | "Command 1 and Command 2: 48 cells" (tracker effect columns); retriggers, slides, sample offsets |
| Notes | machine / clip: K notes | "slides become glides; accents become velocity 127" |
| Tempo | machine | "Glide set from the tempo at conversion (125 BPM); tempo changes are not followed" |
| Baked | machine to target | "Pedal Chord expanded into 312 notes on Bass: change them there" |
| Controller retargeted | controller: parameter | "Neo LFO 'LFO 1' now drives Neo Verb Decay (range re-expressed)" |
| Controller not retargeted | controller: parameter | "no counterpart" |
| Preset | machine: preset | "converted" or "not converted" |
| Kept as legacy | machine | Reason: "no Neo role"; "Windows only"; "you chose Keep" |
| Dropped | machine | "superseded by Song Notes / the Session View; its data is kept in the song" |
| Missing machine | name | "not installed and has no successor: kept silent with its data" |
| Not verified | machine | "measured on Windows only" and the check command |

### 8.5 Presets

Three kinds of preset files:

- **The legacy machine's factory presets** (the repo's 20 families, section 3.2). At build, `tools/preset-convert.py` runs each through its pair (the pair's rules are data; `param_set`'s parser reads units) and writes a Neo preset in the successor's bank named `<legacy preset> (from <gear>)`. A song's converted machine gets the preset's name in its state.
- **A song's current values**: converted with the machine (they are the parameters).
- **A user's own `.prs` files** for a legacy machine: converted on request through a menu command and the agent (`preset_convert`), never silently.

### 8.6 Order, undo, platforms

- **Order of operations on open:** load machines, load engine columns, `ClipMigration`, conversions (effects before generators, so a generator's output connection already points at the converted effect), controller retargeting last.
- **Undo:** one group per song; each conversion is its own sub-step so the user can undo the one machine.
- **macOS and Linux:** closed machines are placeholders (silent). The dialog's Keep legacy option is greyed there ("not available on this platform: its data is kept"); the default converts everything that has a pair. Machines with no pair stay silent placeholders with their data, as today.
- **Windows:** both choices work; Keep legacy plays the machine as before.

### 8.7 Tests

- Pair tests and the song test of section 6.2, per wave.
- The loss report's categories each have a golden case in a generated corpus (one song per feature: a tracker with effect columns, a drum pattern with shuffle, a slid 303 line, a controller on a converted machine, a mixer with three inputs).
- A round trip: a song opened, converted, saved as `.nbl`, opened again: the converted machines play, the `legacy` section is intact; exported for Buzz, the original machines come back with their data.
- A song with no legacy machines (the templates): unchanged, no dialog.
- Zero allocation and the audio gates of `AudioHygieneTests` and `EngineHardeningTests` on a converted song.

---

## 9. Waves

Most-used first, by the instances in the repo (a stand-in until the census says otherwise: section 3.2), the machines the real songs use (reverbs, delays, EQs, trackers, bass synths) and the cost. Estimates are rough, in person-weeks (pw).

| Wave | What | Covers (repo instances) | pw |
|---|---|---|---|
| **LM1 Convert on open** | The converter as a document-level pass over today's `MachineReplace`: plan from `PARA` + manifest, no renders, one report; the loss report and its dialog; the read-only rule and the agent's refusals; the `legacy` section and Export for Buzz restore; controller retargeting; the `family` field and browser hiding; **dissolve** for mixers (Connection Mixer Console, mixIO, Pedal Gain Multi), Info and QuickNote to Song Notes; the 30 pairs run on open; the reference kit (6.2) for the existing pairs; the **census tool** (a command that scans a folder of songs and reports each machine's instances, songs and pair status; run on the user's own songs and any public `.bmx` archive) | 28 of 45 in-package (62 %) | 6 to 8 |
| **LM2 The top of the use list** | New pairs: FSM Infector, Oomek Aggressor 303 (with the Neo Poly extension **G3**), Jeskola Noise Generator, FSM PhatMan, Stereo Reverb and Raverb, Oomek Masterizer (with `insert_after`), Trilok, Pedal MComp; **G1 Tracker mode** and the three tracker pairs | +16 (44 of 45) | 9 to 10 |
| **LM3 The rest of the pairs, bakes, G2** | Philthy, WahManPro, WahPro2, Green Milk, Pedal Juno106, SH101, FM, Jupiter 3, Mars filter 2, Enceladus Tube EQ, Pedal Shaper, Wave Shaper; **bake** (Pedal Chord, PeerChord, EnvelopeBlock, Pedal Peer Control; Relativion and GuitarChordBox follow in LM4); **G2 Neo Lo-Fi** and the Saturn pair | +1 (45 of 45) | 9 to 10 |
| **LM4 Long tail, by census** | Whatever the census of the user's own songs shows: the Keep and Drop lists reviewed, Polac VST to Neo Plug by plug-in name, Polac I/O, pairs for library names the package does not ship (Freeverb, the Geonik and Automaton set), G4 if Jeskola Organ is used | by census | 2 to 4 |

Dependencies: LM1 does not wait for the clips waves (the converter reads through the seam) but its notes-lane output needs CU3; until then it writes tracker-column notes as `MachineReplace` does. LM2's G1 needs the notes lane's channel (clips.md section 5.1: it is in the message). Each wave carries the gates of clips.md section 10.1 and the pair tests of 6.2.

---

## 10. Decisions (the user, 2026-11-23)

- **D1 B: convert on open, report afterwards.** An old song opens with every machine that has a pair already converted; the loss report shows after it opens, with **one button to go back to the old machines** for this song (it reopens the file with Keep legacy for all). The file on disk is untouched; Save asks for a name.
- **D2 B: the gap machines, organ included.** G1 Tracker mode in Neo Sampler and the 303's accent and diode filter as a Neo Poly extension in LM2; G2 Neo Lo-Fi and **G4 Neo Organ** in LM3.
- **D3 B+: the Pedal family goes.** Each Pedal machine is removed from the package once LM3 has its conversion; a Pedal machine with no pair (Drum Matrix) is lost from songs that use it, as D4. **Plus two new real-time machines**, so chords and arpeggios stay live rather than only baked: **Neo Chord** (a note in, a chord out: shape, inversion, spread, voicing, strum) and **Neo Arp** (a note or chord in, an arpeggio out: mode, rate in steps, gate, octaves, swing, latch). Both take and send real notes and run on every platform. Pedal Chord, PeerChord and GuitarChordBox convert to Neo Chord or Neo Arp where the settings map; the rest bake as section 5 says. Clean room. About 2 to 3 pw for the pair, in LM3.
- **D4 B: drop the 16 machines with no Neo role now.** Each becomes a silent placeholder that keeps its connections, and the loss report names it.
- **D5 A: a sample number per note** for the converted tracker (G1 above): one Neo Sampler per tracker, all waves in it, no channel limit.

Effect on the waves: LM3 grows by Neo Organ (2 to 3 pw) and Neo Chord with Neo Arp (2 to 3 pw), to about 13 to 16 pw; LM4 loses its Keep list and is the census plus Polac VST to Neo Plug.

---

## 11. Risks and loose ends

| Risk | Plan |
|---|---|
| The repo's songs are not representative (56 % are for machines not shipped) | LM1 includes the census tool; the waves after LM1 are reordered by what it finds in the user's songs |
| A pair's sound difference is larger than the user will accept (reverbs, the tracker, Infector) | The tolerance and the plain-words remark are in the dialog; Keep legacy stays (Windows) until the user converts; the original is never overwritten |
| A placeholder's parameter table is not enough to plan a conversion | First task of LM1: run a dry-run conversion on a placeholder on Linux; if the groups are incomplete, read the song's `PARA` directly |
| Controller retargeting is not built and is the most likely source of silent breakage (a Neo LFO driving a machine that is replaced) | In LM1, with a golden case |
| Baking changes how a song is edited (the chord machine's setting becomes notes) | Said in the report; a bake keeps the original in the `legacy` section |
| Wave-per-note in Tracker mode costs a second Neo Sampler past 16 waves | The report names it; Matilde and Jeskola Tracker songs often use fewer; census |
| Licence: Pedal and Jeskola sources without licence, GPL machines | No pair or rebuild uses their code; the release decision is D3 |
| The 17 natives' R1 rebuilds are not bit-identical on arm64 | They are a fallback for Keep legacy on macOS only; conversion is the default there |
| Presets: a pair maps values, not the character of a preset | The converted preset keeps its name and the report says "converted" |
| The estimates are rough | Each wave's first week produces a measured size for its first pair |

---

## 12. Sources

Read for this document: `Machines/Manifests/machine-profile.json`, `machine-catalog.json`, `Successors/*.successor.json` (all 30), `Effects/`, `Generators/` manifests (Aggressor, Infector, PhatMan, Noise Generator, Jeskola Stereo Reverb, Jeskola Tracker, Matilde Tracker2), `tools/machine_catalog.py`, `ExternalMachines/README.md`, `machines.json` (licences and ship rules), `FORK.md`, `docs/specs/machines.md` (family, Neo Sampler, Neo Drums, Neo Poly), `machine-safety.md` section 4 and 3.5, `cross-platform.md` (2.4, 4.2, 4.3, R1), `native-format.md` (11.2), `plugin-host.md`, `clips.md` on `design/clips-cu0` (sections 3, 5, 7, 10, 12), `ReBuzz/Agent/MachineReplace.cs`, `ReBuzz/Core/Session/SessionLayout.Replace.cs`, `ReBuzz/MachineManagement/MachineAliases.cs`, `ReBuzz/FileOps/BMXFile.cs` (`LoadMachines`, `LoadPara`), the repo's 20 old songs and 2 templates (machine names read from `PARA` and `MACH`), the repo's preset files. The Oomek Aggressor source was read only for how its slide and accent switches work (documentation), not as a source for any implementation.
