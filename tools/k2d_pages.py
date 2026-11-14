#!/usr/bin/env python3
# Fork (agent-knowledge.md K2 batch d): the machine pages (ReBuzz.Agent/Guides/machines/<Gear>.md, template §4) of the 28 machines of
# batch d: ReBuzz's own machines, the Rout 909 and the single-machine families. The text is in our words; where a machine was not
# measured the page says so (Unverified). tools/k2c_knowledge.py puts the matching Explain text and curation into the manifests.
#
#   python3 tools/k2d_pages.py            check: every page is what this file says
#   python3 tools/k2d_pages.py --write    write them

import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIR = os.path.join(ROOT, "ReBuzz.Agent", "Guides", "machines")
SELF = "Use it as the song already does: `machine_describe(machine=\"{0}\")` lists what is on it."


def page(sound, use, controls, starting, pitfalls, pairs, successor, checklist):
    out = ["## How it sounds", "", sound, "", "## Use it for", ""]
    out += [f"- {u}" for u in use]
    out += ["", "## Key controls", ""]
    if controls:
        out += ["| Parameter | Value | Why |", "|---|---|---|"] + [f"| {n} | {v} | {w} |" for n, v, w in controls]
    else:
        out += ["None: it has no parameters."]
    out += ["", "## Starting points", ""] + [f"- {s}" for s in starting]
    out += ["", "## Pitfalls", ""] + [f"- {p}" for p in pitfalls]
    out += ["", "## Pairs well with", ""] + [f"- {p}" for p in pairs]
    if successor:
        out += ["", "## Successor", "", successor]
    out += ["", "## Checklist", ""] + [f"- [ ] {c}" for c in checklist]
    return "\n".join(out) + "\n"


NONE = "None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it."
PAGES = {}

# ---- ReBuzz's own machines ------------------------------------------------------------------------------------------------
PAGES["ReBuzz Audio In"] = page(
    "A source that brings one stereo pair of the audio device's input into the song, with one Bypass switch. It adds nothing to the sound; you hear whatever the device is fed. The pair and mono or stereo are chosen in its menu, not by a parameter.",
    ["Live input (a microphone, a synth, a record player) as a source to run through effects.",
     "A signal to record: connect it to a Session Clip Player and record a take."],
    [("Bypass", "no", "no passes the input; yes makes the machine silent")],
    ["Add it, connect it to an effect or to the master, then check the meter: `machine_add` then `meters_read(machines=[\"Audio In\"])`.",
     "To record the input, connect it to a Session Clip Player with `connect(from=\"Audio In\", to=\"Clips\")` and use `session_record(action=\"start\", machine=\"Clips\", bars=4)`."],
    ["It is silent until the audio device has a signal on the chosen pair; unverified here (the profile had no input), so which device channel arrives was not checked.",
     "Live input through speakers can feed back: lower the monitoring first.",
     "A note does nothing: it is a source, not an instrument."],
    ["Any effect you want to run the input through; Session Clip Player to capture it."],
    NONE,
    ["`machine_describe(machine=\"Audio In\")`: its one parameter is `Bypass`.",
     "`param_get(machine=\"Audio In\")`: `Bypass` reads no.",
     "`meters_read(machines=[\"Audio In\"])`: it shows a level when the device input has a signal."])

PAGES["ReBuzz Audio Out"] = page(
    "An effect that sends what is connected to it to one of the audio device's extra outputs, or, at channel 0, passes it through unchanged (the profile: 0 dB, no latency). The channel is chosen in the machine's menu; its one parameter does nothing.",
    ["Sending one part of the song to a separate hardware output (a headphone cue, a second pair of speakers).",
     "Leaving it at channel 0 is a transparent pass-through."],
    [("Unused switch", "Off", "does nothing; the channel is set in the menu, not here")],
    ["Connect a machine to it and choose its channel from its menu in the machine view; param_set cannot choose the channel.",
     "To check it passes audio: `analyze(source=\"Audio Out\", metrics=[\"loudness\"])`."],
    ["At any channel above 0 the machine's own output is silent: the audio goes to the device output instead, so do not connect it onward and expect sound.",
     "Unverified: whether the device really has that many outputs; the profile only measured channel 0."],
    ["The master, with ReBuzz Audio Out hanging off a bus you want to monitor separately."],
    NONE,
    ["`machine_describe(machine=\"Audio Out\")`: its one parameter is `Unused switch`.",
     "`analyze(source=\"Audio Out\", metrics=[\"loudness\"])`: at channel 0 the level equals its input.",
     "`param_get(machine=\"Audio Out\")`: the switch reads Off."])

PAGES["ReBuzz Groove"] = page(
    "A control machine for swing: up to eight steps, each stretching or squeezing one tick, repeated over the song. It makes no sound itself; it changes when notes fall. Only the ratios between steps matter, because the pattern is scaled to keep the average length at 1.",
    ["Swing and shuffle for the whole song: a long first tick and a short second tick make a swung eighth.",
     "A loose, played-by-hand feel on a quantised pattern."],
    [("Steps in groove", "2", "how many steps repeat; 0 is off"),
     ("Step 1 length", "1.5x", "the first tick lasts one and a half times"),
     ("Step 2 length", "1x", "the second tick is the reference"),
     ("Step 3 length", "1x", "unused at 2 steps"),
     ("Step 4 length", "1x", "unused at 2 steps"),
     ("Step 5 length", "1x", "unused at 2 steps")],
    ["Swing: Steps in groove 2, Step 1 length 1.5x, Step 2 length 1x.",
     "Straight again: Steps in groove 0.",
     "Set it with `param_set(machine=\"Groove\", changes=[{\"param\": \"Steps in groove\", \"value\": 2}])`."],
    ["Every step at its default (1x) means no swing at all; set a step first.",
     "Steps above Steps in groove are ignored.",
     "It shifts every machine's timing; render before and after to hear it: `render(probes=[\"Master\"])`."],
    ["Any drum or bass pattern written on a straight grid."],
    NONE,
    ["`machine_describe(machine=\"Groove\")`: it lists `Steps in groove` and eight step lengths.",
     "`param_get(machine=\"Groove\")`: `Steps in groove` reads 0 until you set it.",
     "`render(probes=[\"Master\"])`: with steps set, the notes' timing differs from a render with 0."])

PAGES["ReBuzz Midi Output"] = page(
    "A control machine that sends MIDI to an output device: each track sends notes and, from its controller columns, controller changes and the pitch wheel. It makes no sound itself; the device it drives does.",
    ["Playing external synthesisers or a drum machine from a pattern.",
     "Sending controller changes (modulation, volume) and pitch bend to a device on a given channel."],
    [("MIDI output device", "0", "the device's number in the app's output list"),
     ("MIDI channel", "0", "0 is channel 1, 15 is channel 16"),
     ("Controller number", "1", "0 to 127 a controller, 224 the pitch wheel"),
     ("Controller value", "64", "0 to 127; the wheel uses 0 to 16,383 with 8,192 as centre")],
    ["Write notes into a pattern of the machine with `notes_write(machine=\"Midi Output\", pattern=\"A\", notes=[{\"pitch\": \"C3\", \"start\": 0, \"length\": 1}])`.",
     "Modulation sweep: Controller number 1 with Controller value written along the pattern."],
    ["The profile heard silence by design (a health warn); whether a device receives the notes was not tested here: unverified.",
     "Controller number values other than 0 to 127 and 224 send nothing.",
     "Channel is 0-based: raw 0 is MIDI channel 1."],
    ["An external instrument on the chosen channel; `midi_bindings_list` for the opposite direction, controllers into the song."],
    NONE,
    ["`machine_describe(machine=\"Midi Output\")`: it lists four track parameters, `MIDI output device` first.",
     "`param_get(machine=\"Midi Output\", track=0)`: the device and channel read 0.",
     "`notes_read(machine=\"Midi Output\", pattern=\"A\")`: the notes written are back."])

PAGES["Session Clip Player"] = page(
    "An audio track for the Session View: it plays the audio clips its track launches, warped to the song's tempo, and records takes from its input or from the master. It is silent until a clip is launched. Two parameters: what you hear of the input, and what a take records.",
    ["Audio loops launched as clips next to pattern clips in the Session View.",
     "Recording a take from a connected machine or, by resampling, from the master."],
    [("Monitor", "auto", "hear the input only while the track is armed; in always, off never"),
     ("Take source", "in", "in records what is connected to it; master records the song's output")],
    ["Make the track in the Session View with + Track, then drop an audio file on a slot; `session_get` lists the grid.",
     "Launch a clip: `session_launch(scene=0, machine=\"Clips\")`.",
     "Record a take: `session_record(action=\"start\", machine=\"Clips\", bars=4)`."],
    ["Silent with no clip launched (the profile's health warn); that is not a fault.",
     "Take source master records everything the song plays, including this track: mute it first to avoid doubling.",
     "Outside the app's own Session engine it only passes its input through."],
    ["ReBuzz Audio In or any instrument connected to it for takes."],
    NONE,
    ["`machine_describe(machine=\"Clips\")`: its parameters are `Monitor` and `Take source`.",
     "`session_get`: the track's clips are listed.",
     "`meters_read(machines=[\"Clips\"])`: a launched clip shows a level."])

PAGES["AudioBlock"] = page(
    "A control machine that plays audio clips laid out on a timeline in its own window, following the song's position. It makes no sound of its own when the timeline is empty, and its one parameter is Mute.",
    ["Songs that already use it for audio on a timeline."],
    [("Mute", "No", "yes silences every clip")],
    [SELF.format("AudioBlock"),
     "For new work use the Session View's audio tracks or the arrangement."],
    ["The clips live in its own window and are saved with the song; the Studio does not show that window, so they cannot be edited here: unverified how an AudioBlock song plays in the Studio.",
     "Silent at its defaults in the profile (no clips placed): that is expected."],
    ["Any machine you want to line up with its clips."],
    "None: the Session View's Session Clip Player does the same job but is not a drop-in for it, so `machine_replace` does not apply.",
    ["`machine_describe(machine=\"AudioBlock\")`: its one parameter is `Mute`.",
     "`param_get(machine=\"AudioBlock\")`: `Mute` reads No.",
     "`meters_read(machines=[\"AudioBlock\"])`: with clips placed it shows a level."])

PAGES["EnvelopeBlock"] = page(
    "A control machine that draws envelopes for parameters over patterns, in its own window, and plays them into the machines they target. It makes no sound. Its one parameter decides whether a new envelope point starts from the target's current value or its default.",
    ["Songs that already use it for drawn parameter envelopes.",
     "Reading how a song's automation was built."],
    [("New point start", "Current", "Current keeps what you hear when a point is added; Default resets to the parameter's default")],
    [SELF.format("EnvelopeBlock"),
     "For new automation, `automation_write` writes a movement into a pattern of the machine whose parameter it moves."],
    ["The envelopes are in its window and the song's saved data; param_set cannot draw them.",
     "Unverified: how an existing EnvelopeBlock song plays in the Studio."],
    ["The machines whose parameters it moves."],
    "None: `automation_write` and `modulate` do the same work, but they do not read an EnvelopeBlock's envelopes, so `machine_replace` does not apply.",
    ["`machine_describe(machine=\"EnvelopeBlock\")`: its one parameter is `New point start`.",
     "`param_get(machine=\"EnvelopeBlock\")`: it reads Current.",
     "`automation_read(machine=\"Bass\")`: shows the movements already written into a pattern."])

PAGES["QuickNote"] = page(
    "A control machine that holds a text note in the song, edited in its own window. It makes no sound and sends nothing; its one parameter does nothing.",
    ["A reminder pinned beside a machine in the machine view."],
    [("Unused switch", "Off", "does nothing")],
    ["For notes about the song as a whole, write them with `song_notes_write(text=\"...\")` instead; it is the app's own place and the agent can read it back with `song_notes_read`.",
     SELF.format("QuickNote")],
    ["The text is the machine's own state; the agent cannot read or write it through a tool.",
     "It has no audio: the profile heard silence."],
    ["Nothing; it is a note."],
    NONE,
    ["`machine_describe(machine=\"QuickNote\")`: its one parameter is `Unused switch`.",
     "`song_notes_read`: the song's own notes are there instead.",
     "`param_get(machine=\"QuickNote\")`: the switch reads Off."])

PAGES["Connection Mixer Console"] = page(
    "A control machine that is a mixing console for up to 16 machine connections: each strip's Volume and Pan set one connection's amp and pan, as the machine view's connection sliders do. It makes no sound itself. A strip does nothing until a connection is assigned to it in the machine's window.",
    ["Hands-on mixing of many connections from one place, with MIDI controllers bound in its window.",
     "Automating a connection's level or pan from patterns."],
    [("Strip 1 volume", "100%", "unity gain; 0 to 400%"),
     ("Strip 1 pan", "0%", "centre; -100% left, +100% right"),
     ("Strip 2 volume", "100%", "the second connection's level"),
     ("Strip 2 pan", "0%", "the second connection's pan"),
     ("Strip 3 volume", "100%", "the third connection's level")],
    ["Assign a connection to each strip in the window, then move Volume and Pan; unassigned strips do nothing.",
     "From the tools, `connection_set` sets a connection's amp and pan directly and needs no assignment."],
    ["Volume above 100% boosts the connection and can overload the machine it feeds.",
     "The assignment is saved with the song but is not a parameter, so param_set cannot make it.",
     "Unverified: how the console behaves in the Studio, which does not show its window."],
    ["The machines whose connections you assigned; the master for the overall level."],
    NONE,
    ["`machine_describe(machine=\"ConnMixer\")`: it lists sixteen Volume and sixteen Pan parameters.",
     "`param_get(machine=\"ConnMixer\")`: every Volume reads 100% and every Pan 0%.",
     "`analyze_mix`: the mix changes when a strip's Volume moves."])

PAGES["ManagedMultiIOEffect"] = page(
    "The host's example of a multi-input, multi-output effect: a clean input, a noise input that gets noise added, and a delay input with feedback. The first output is the sum of all three, the others carry the noise and the delay alone. With only a clean input connected it passes the sound unchanged.",
    ["Learning how a multi-input machine works, or testing a host's multi-channel routing.",
     "A delay and a noise generator on separate inputs, with a mix out."],
    [("Delay time", "0.3 s", "100 ms to 2 s for the delay input"),
     ("Delay feedback", "50%", "0 to 100; at 100 the repeats never fade"),
     ("Noise amount", "50%", "0 to 100; scaled by the noise input's own level")],
    ["Connect a source to the first input for a clean pass-through; the other two inputs only matter when you connect them with `connect(from=\"Src\", to=\"Multi IO\", input=1)`.",
     "Keep Delay feedback under 80% on a long delay."],
    ["It is an example, not a studio effect: its delay buffer resizes each time Delay time is set, so changing it during play can click.",
     "A silent noise input stays silent: the noise is scaled by that input's own level.",
     "At 100% feedback the level keeps building."],
    ["A synth on the clean input and a noise source on the second."],
    NONE,
    ["`machine_describe(machine=\"Multi IO\")`: it lists `Delay time`, `Delay feedback` and `Noise amount`.",
     "`analyze(source=\"Multi IO\", metrics=[\"loudness\"])`: with only the clean input the level equals the input.",
     "`param_get(machine=\"Multi IO\")`: the defaults are 0.3 s, 50% and 50%."])

# ---- Rout 909 ------------------------------------------------------------------------------------------------------------
PAGES["Rout 909"] = page(
    "An analog-style drum machine with eleven voices: bass drum, snare, three toms, rim shot, clap, closed and open hi-hat, crash and ride. Each voice has a level; most have tune and decay controls, and the snare has tone and snap. It plays from trigger columns in its tracks, not from notes, so it is silent until a trigger fires.",
    ["Four-on-the-floor kicks, snares, claps and hi-hat patterns for house, techno and breaks.",
     "Songs that already carry its trigger patterns."],
    [("Bass drum level", "100%", "0 to 198%; raw 128"),
     ("Snare level", "100%", "the snare against the kick"),
     ("Closed hi-hat level", "80%", "keep hats under the snare"),
     ("Bass drum tune", "32", "0 to 63; lower is deeper"),
     ("Bass drum decay", "32", "0 to 63; longer is boomier"),
     ("Shuffle", "0 ticks", "0 to 0.5 tick of swing; 0 is straight")],
    ["A kick and clap groove: Bass drum level 100%, Snare level 100%, Bass drum decay 32.",
     "Swing it: Shuffle 0.25 ticks with Shuffle step 2.",
     "Its triggers are pattern columns: `notes_write` does not play it, so build the pattern in a song that carries them."],
    ["Silent at its defaults until a trigger fires (the profile found no note column): a note does nothing.",
     "Levels reach about 200%: keep every voice under 120% to leave headroom.",
     "Tune and decay are bare 0 to 63 scales with no unit in the machine's texts."],
    ["Neo Drums for a modern kit; a compressor on the bus for glue."],
    "Neo Drums replaces Rout 909: `machine_replace(machine=\"Rout\")` plays its eleven triggers on Neo Drums' voices, carries the levels over in decibels, and sets the tunes, decays, attack, tone and snap to the same settings in Neo Drums' units.",
    ["`machine_describe(machine=\"Rout\")`: its parameters are the eleven levels, the tunes, decays and the shuffle.",
     "`param_get(machine=\"Rout\")`: every level reads 100%.",
     "`machine_replace(machine=\"Rout\", dry_run=true)`: shows what Neo Drums would get."])

# ---- synths and samplers --------------------------------------------------------------------------------------------------
PAGES["Bigyo Wood One"] = page(
    "A quiet, monophonic synth for wooden, mallet-like plucks and tuned percussion: a noise and pulse burst strikes a resonator, with controls for the strike colour, harmonics, damping, a tunable body resonance and the envelope. It plays A4 at 440 Hz as written.",
    ["Marimba-, wood-block- and pluck-like notes.",
     "Short, tuned percussion for mallet parts and rhythmic tics."],
    [("Noise to pulse mix", "50.0 / 50.0 %", "an even mix of breathy and clicky strike"),
     ("Noise time", "4.5 ms", "a short burst is a sharp tick"),
     ("Brightness", "981 Hz", "how bright the strike is"),
     ("Resonator frequency", "621.8 Hz", "the body that colours the note"),
     ("Resonator gain", "-0.3 dB", "how loud the body is against the dry strike"),
     ("Decay", "2.47 s", "shorter is a dry block; longer a ringing bar"),
     ("Release", "0.087 s", "the tail after the note ends")],
    ["A marimba: the defaults, a short Decay near 0.3 s and Resonator gain +6 dB.",
     "A wood block: Decay 0.04 s, Resonator frequency 1.5 kHz.",
     "Play it: `notes_write(machine=\"Wood One\", pattern=\"A\", notes=[{\"pitch\": \"A3\", \"start\": 0, \"length\": 0.5}])`."],
    ["Quiet at its defaults (about -28 LUFS for one note): raise Resonator gain or the master.",
     "Resonator gain at +40 dB can overload the output.",
     "Unverified: what Pulse colour and Inharmonicity do in the signal path, beyond their ranges."],
    ["Jeskola Reverb or Neo Verb for a room; Neo EQ to shape the body."],
    "None: no Neo machine matches a noise-excited resonator; Neo Poly or Neo Wave are different instruments, so `machine_replace` does not apply.",
    ["`machine_describe(machine=\"Wood One\")`: its twelve parameters are listed with their units.",
     "`machine_readings(machine=\"Wood One\")`: a note reads near -28 LUFS at the defaults.",
     "`analyze(source=\"Wood One\", metrics=[\"loudness\"])`: the output stays under full scale."])

PAGES["kibibu Green Milk"] = page(
    "A thick, loud subtractive synth: up to 16 unison oscillators with their own pitch LFOs and chord shapes, a dual filter with 39 modes, pre and post distortion, two per-track LFOs and exponential envelopes. It is monophonic and sounds an octave below the written note.",
    ["Fat unison leads and pads, detuned stacks and chord-shaped basses.",
     "Filter sweeps and wobbles with the filter envelope and the LFOs."],
    [("Oscillator count", "12", "1 to 16; more voices cost CPU"),
     ("Chord shape", "Phat", "the pitch layout of the oscillators"),
     ("Cutoff", "25%", "filter cutoff"),
     ("Resonance", "4%", "keep low to stay safe"),
     ("Filter envelope amount", "26%", "how far the filter envelope opens the cutoff"),
     ("Filter mode", "LP", "low pass; 39 modes in all"),
     ("Amp sustain", "50%", "the held level")],
    ["A supersaw lead: Oscillator count 12, Chord shape Straight, Unison depth about 20%, Cutoff 60%.",
     "A pad: Amp attack and Amp release long, Chord shape Rich.",
     "Write notes an octave up: `notes_write(machine=\"Green Milk\", pattern=\"A\", notes=[{\"pitch\": \"C4\", \"start\": 0, \"length\": 1}])`."],
    ["Loud at its defaults: one note measures about -9 LUFS with a peak near -2 dBFS; lower the master first.",
     "It plays 12 semitones low (profile): write notes an octave higher.",
     "Many oscillators with distortion cost CPU; lower Oscillator count if the song stutters.",
     "Unverified: the time parameters above raw 128, where the machine gives no text, and the LFO speeds and delays, which are plain raw numbers here."],
    ["Neo Verb and Neo Delay on its tail; a compressor to tame the peaks."],
    "None: Neo Poly is a different synth, so `machine_replace` does not apply to Green Milk.",
    ["`machine_describe(machine=\"Green Milk\")`: 38 parameters with units.",
     "`machine_readings(machine=\"Green Milk\")`: a note reads near -9 LUFS.",
     "`analyze(source=\"Green Milk\", metrics=[\"loudness\"])`: the output stays under full scale."])

PAGES["KrimZon AY-3-8910"] = page(
    "A three-voice chip synth in the style of a classic sound chip: each voice plays a square-wave tone, noise or both at one of 16 volumes, with a hardware-style envelope, sub-note arpeggios, glide and vibrato. It is bright, thin and loud for its size, and plays 13 semitones above the written note.",
    ["Chip-tune leads, arpeggiated chords and noise percussion.",
     "Retro game music over three tracks."],
    [("Final amp", "1x", "the overall level, 0 to 2"),
     ("Tune", "1x", "0.5 to 2; 1 is in tune"),
     ("Voice content", "Tone", "Tone, Noise or both"),
     ("Volume", "15", "the chip's 0 to 15 steps"),
     ("Sub-notes", "3", "arpeggio length from one written note"),
     ("Envelope shape", "0: \\____", "a single falling envelope")],
    ["A chip lead: Voice content Tone, Volume 15, Vibrato a little.",
     "A hi-hat: Voice content Noise, Envelope shape 0: \\____ and a short Envelope time.",
     "Write a chord over all three tracks with `notes_write(machine=\"AY\", pattern=\"A\", notes=[{\"pitch\": \"C3\", \"start\": 0, \"length\": 1}])`."],
    ["Written notes sound 13 semitones high: write an octave and a semitone lower, or lower Tune.",
     "None in Voice content is silent.",
     "Unverified: how Sub-note length and Envelope time map to ticks or seconds."],
    ["Jeskola Distortion for crunch, a short delay for width."],
    "None: no Neo machine imitates a sound chip, so `machine_replace` does not apply.",
    ["`machine_describe(machine=\"AY\")`: `Final amp` and `Tune`, then the track parameters.",
     "`machine_readings(machine=\"AY\")`: a note reads near -21.5 LUFS.",
     "`param_get(machine=\"AY\", track=0)`: Volume reads 15."])

PAGES["Intoxicat ACloud15"] = page(
    "A granular generator: it cuts small grains from two wavetable slots and scatters their position, length, rate, pan and envelope into a cloud of sound. It is silent until a wave is in its slot, and the profile could not hear it, so what it sounds like was not measured.",
    ["Textures and drones from a recorded sound.",
     "Smearing a sample into ambience."],
    [("Skip direction", "Random", "Forwards or Backwards scan through the wave instead"),
     ("Grain length", "0.023 s", "short grains fizz, long grains stay recognisable"),
     ("Grain rate", "1", "playback rate of the grains, a table from 0.25 to 4 times"),
     ("Envelope amount", "50%", "fade grains in and out to avoid clicks"),
     ("Maximum grains", "20", "1 to 100; more are thicker and cost CPU"),
     ("Random seed", "0", "the same seed repeats the same cloud")],
    ["Load a wave first: `wave_import(path=\"/path/pad.wav\", slot=1)`, then set Wave slot 1 to 1.",
     "A slow cloud: Grain length 0.1 s, Envelope amount 80%, Maximum grains 30."],
    ["Silent at its defaults (empty slots, health warn): load a wave first.",
     "Its parameter list has gaps in the machine's own numbering: some controls are not exposed.",
     "Unverified: how it is triggered, and what it sounds like with a wave loaded."],
    ["Neo Verb for a wash; Neo Delay for movement."],
    "None: Neo Sampler and the Neo Learn Granular teaching machine are different machines, so `machine_replace` does not apply.",
    ["`machine_describe(machine=\"ACloud\")`: 16 parameters, `Grain length` in seconds.",
     "`wave_list`: a wave is in slot 1 before you expect sound.",
     "`machine_readings(machine=\"ACloud\")`: reads silence until a wave is loaded."])

PAGES["Matilde Tracker2"] = page(
    "A tracker-style sample player in the manner of a classic module tracker: it plays waves from the wavetable per track, with a volume decay, a sample offset for slicing breaks, a whole-semitone tuning and two effect columns per note. With no wave loaded it is silent.",
    ["Sample-based parts, breaks and one-shots played from patterns.",
     "Percussion slicing with Sample offset."],
    [("Wave", "1", "the wavetable slot a track plays (1 to 200)"),
     ("Volume", "128", "0 to 254, on a squared curve"),
     ("Volume decay", "0", "0 is none; higher shortens sustained samples"),
     ("Sample offset", "0", "where in the sample playback starts"),
     ("Offset quantize", "perc", "free curve or 2 to 64 slices"),
     ("Tuning", "0 st", "whole semitones, about -5 to +5")],
    ["Load a wave: `wave_import(path=\"/path/break.wav\", slot=1)` and write notes with Wave 1.",
     "Plucks from a sustained wave: Volume decay 80.",
     "`notes_write(machine=\"Matilde\", pattern=\"A\", notes=[{\"pitch\": \"C4\", \"start\": 0, \"length\": 1}])`."],
    ["Volume is squared: raw 128 is a quarter of full level, so use higher values.",
     "Silent without a wave in the track's slot.",
     "Its effect columns (arpeggio, slides, retriggers) are not in the parameter list: unverified whether `notes_write` reaches them."],
    ["Neo Comp or Neo Transient on breaks; Neo Sampler for the same samples with modern controls."],
    "None: Neo Sampler plays waves too, but its controls and effect columns differ, so `machine_replace` does not apply to Matilde Tracker2.",
    ["`machine_describe(machine=\"Matilde\")`: four global and two track parameters.",
     "`wave_list`: the wave is loaded.",
     "`machine_readings(machine=\"Matilde\")`: a note reads a level when the wave is in place."])

# ---- utility and control ---------------------------------------------------------------------------------------------------
PAGES["EasyRec"] = page(
    "A utility that records what is connected to it into a wavetable slot, with a Stop and Record mode, an auto-stop switch and an overwrite switch. It passes its input through unchanged while it records.",
    ["Capturing a loop or a live take straight into a wave slot.",
     "Sampling the output of a machine for a sampler to play."],
    [("Recording mode", "Stop", "set Record to start a take"),
     ("Auto stop", "Enabled", "ends the take on its own"),
     ("Wavetable slot", "1", "1 to 200; where the take is saved"),
     ("Overwrite existing", "Yes", "No protects a slot that already has a wave")],
    ["Pick a free slot with `wave_list`, set Wavetable slot, then `param_set(machine=\"EasyRec\", changes=[{\"param\": \"Recording mode\", \"value\": \"Record\"}])`.",
     "For a bounce of the whole song, `wave_from_render` does it without a recording machine."],
    ["Overwrite existing Yes can erase a wave: check `wave_list` first.",
     "Unverified: the condition that ends a take with Auto stop, and whether it records in the Studio."],
    ["Any source you want to sample; a sampler in the next track."],
    "None: `wave_from_render` and the Session View's take recording cover the same need, but they are not a replacement for it, so `machine_replace` does not apply.",
    ["`machine_describe(machine=\"EasyRec\")`: four parameters, `Recording mode` first.",
     "`wave_list`: the slot holds a wave after a take.",
     "`analyze(source=\"EasyRec\", metrics=[\"loudness\"])`: its output equals its input."])

PAGES["mixIO"] = page(
    "A multi-input mixer: every track has a gain from silence to +12 dB, a stereo width, mute and solo, and gliding times for each. With one clean input at its defaults it is a pass-through (the profile: 0 dB, no latency); at +12 dB it overloads.",
    ["Balancing several inputs of a multi-channel setup.",
     "Muting and soloing inputs with a smooth fade."],
    [("Gain", "-0.18 dB", "about 0 dB at the default; +12 dB is the top"),
     ("Stereo width", "100%", "mono at 0, unchanged in the middle"),
     ("Gain glide", "1 ticks", "how long a gain change takes"),
     ("Mute", "OFF", "silences the input"),
     ("Solo", "OFF", "plays only the soloed inputs")],
    ["Keep Gain at its default and balance by ear; Mute an input with Mute ON.",
     "Smooth fades: Gain glide 4 ticks."],
    ["Gain at +12 dB peaks above full scale: keep it lower.",
     "Unverified: how its 32 tracks map onto several connected inputs, and what the Pan glide smooths (the profile found no pan parameter).",
     "Several tracks mean several inputs only in a multi-input setup; with one input only track 0 matters."],
    ["Neo Imager for width and Neo Utility for gain on a single stream."],
    "Neo Utility covers a single stream's gain and width: `machine_replace(machine=\"mixIO\")` does not apply to a multi-input mixer, so use it only on a one-input mixIO.",
    ["`machine_describe(machine=\"mixIO\")`: eight track parameters, `Gain` first.",
     "`param_get(machine=\"mixIO\", track=0)`: Gain reads about 0 dB.",
     "`analyze(source=\"mixIO\", metrics=[\"loudness\"])`: at the defaults the level equals the input."])

PAGES["SpectrumBlock"] = page(
    "A spectrum and level display: it analyses the audio (left, right or both) with a chosen window and shows low, mid and high band levels, volume, stereo balance and correlation in its own window. It makes no sound and changes nothing.",
    ["Watching the balance of low, mid and high on a bus.",
     "Checking stereo balance and phase correlation by eye."],
    [("Analysed channel", "Mix", "Left, Right or Mix"),
     ("FFT window", "Hanning", "None, Hanning, Hamming and others"),
     ("Low band ceiling", "300 Hz", "where the low meter stops"),
     ("High band floor", "2400 Hz", "where the high meter starts"),
     ("Volume smoothing", "0.01 s", "how calm the volume trace is, 1 to 100 ms")],
    ["Defaults: Mix, Low 300 Hz, High 2400 Hz.",
     "The same readings through the tools: `analyze_mix` and `meters_read(machines=[\"Master\"])`."],
    ["It is a window: the Studio does not show it, and the tools give the same numbers.",
     "Unverified: what Display scale scales, and what it listens to when nothing is connected."],
    ["Neo Analyzer and Neo Scope for the same job inside the Studio."],
    "Neo Analyzer and Neo Scope do the same job with the app's own views: `machine_replace(machine=\"SpectrumBlock\")` does not apply, because it is a display, not a processor.",
    ["`machine_describe(machine=\"SpectrumBlock\")`: twelve parameters, `Analysed channel` first.",
     "`meters_read(machines=[\"Master\"])`: the same levels without a window.",
     "`analyze_mix`: the tools read the spectral balance too."])

PAGES["BTDSys PeerChord"] = page(
    "A control machine that turns each note written to its track into a chord or an arpeggio on a target machine: 54 chord types, strum and delay modes, a note cut, arpeggio modes and an octave spread. It makes no sound itself; the target plays the notes.",
    ["One-note chords and arpeggios for a pad, a pluck or a bass.",
     "Strums and spreads that sound played by hand."],
    [("Chord type", "min", "one of 54 chords; none sends the note alone"),
     ("Strum mode", "Strum Dn", "Off, Simple, Random, Strum Dn or Strum Up"),
     ("Strum delay", "32/128 ticks", "the gap between chord notes"),
     ("Arpeggio mode", "Off", "Up, Down, Up/Down, Down/Up, Random or Brownian"),
     ("Arpeggio steps", "4", "2 to 16"),
     ("First target track", "0", "the first of up to six target tracks"),
     ("Velocity", "50%", "from the target's lowest to its highest")],
    ["A pad: Chord type min, Strum mode Off, a root note each bar.",
     "A strum: Strum mode Strum Dn, Strum delay 12/128 ticks.",
     "Write the root note: `notes_write(machine=\"PeerChord\", pattern=\"A\", notes=[{\"pitch\": \"C3\", \"start\": 0, \"length\": 1}])`."],
    ["The target machine is chosen in the machine's own window and no tool sets it: ask the person to pick it, or nothing plays.",
     "A chord needs up to six free consecutive tracks on the target.",
     "Octave controls hold two shifts each, -8 to +7; custom note offsets 0 to 47 with 48 meaning off."],
    ["A polysynth with enough tracks; Pedal Chord does the same with a source and its own page."],
    "None: Pedal Chord is the repo's own chord and arpeggio machine, but a different one, so `machine_replace` does not apply.",
    ["`machine_describe(machine=\"PeerChord\")`: 20 track parameters, `Chord type` first.",
     "`param_get(machine=\"PeerChord\", track=0)`: Chord type reads none until set.",
     "`notes_read(machine=\"PeerChord\", pattern=\"A\")`: the root notes written are back."])

PAGES["DAWControl"] = page(
    "A control machine with a Bypass switch and its own window that responds to MIDI notes and controller changes as a DAW controller. It makes no sound. What it controls was not measured here.",
    ["Songs that already use it."],
    [("Bypass", "no", "yes stops it acting")],
    [SELF.format("DAWControl"),
     "In this app, transport and MIDI bindings are covered by `transport`, `midi_learn` and `midi_bind`."],
    ["Unverified: its DLL names MIDI note and control-change handling and a DAW control type, and the app's preferences have a DAW controllers list; whether it acts on those assignments was not checked.",
     "Its window is Windows only; the Studio does not show it."],
    ["A MIDI controller bound in the app's preferences."],
    "None: `transport`, `midi_learn` and `midi_bind` cover the job, but they are not a drop-in, so `machine_replace` does not apply.",
    ["`machine_describe(machine=\"DAWControl\")`: its one parameter is `Bypass`.",
     "`midi_bindings_list`: lists what is bound in the app.",
     "`param_get(machine=\"DAWControl\")`: `Bypass` reads no."])

PAGES["GuitarChordBox"] = page(
    "A control machine that sends the notes of a guitar chord to target machines on a MIDI channel, strummed with a delay per string and a little randomness. It makes no sound itself; the chord is picked in its window and the targets play it.",
    ["Guitar-like strums from a synth or a sampler.",
     "Humanised chord stabs with random timing and volume."],
    [("Send notes", "Yes", "turns the sending on"),
     ("MIDI channel", "0", "0 is channel 1"),
     ("Send velocity", "60", "0 to 127"),
     ("Delay randomness", "40", "0 to 2000 ms of random delay"),
     ("Low E delay", "0 s", "the first string"),
     ("High E delay", "0.1 s", "up to 2 s; a later string makes a strum"),
     ("Transpose", "0", "-12 to +12 semitones")],
    ["A downstrum: Low E delay 0 s up to High E delay 0.1 s in equal steps.",
     "A loose feel: Delay randomness 40 and Volume randomness 20."],
    ["The chord and the targets are chosen in its window, not through parameters: nothing plays until a person picks them.",
     "Unverified: which machines receive the notes by default, and the chords on offer.",
     "String delays reach 2 s, longer than most strums: keep them short."],
    ["A guitar-like instrument to receive the notes."],
    NONE,
    ["`machine_describe(machine=\"GuitarChordBox\")`: twelve parameters, `Send notes` first.",
     "`param_get(machine=\"GuitarChordBox\")`: `Send notes` reads No until set.",
     "`midi_bindings_list`: lists what drives the app, as a way to start a strum."])

PAGES["Info"] = page(
    "A control machine that holds a rich text note in the song, edited in its own window. It makes no sound and sends nothing. Its one parameter does nothing measurable.",
    ["Notes on how a song is built, pinned in the machine view."],
    [("Unused value", "0", "does nothing")],
    ["For notes about the song, `song_notes_write(text=\"...\")` is the app's own place and `song_notes_read` reads them.",
     SELF.format("Info")],
    ["The text is the window's own state; no tool reads or writes it.",
     "Its window is Windows only."],
    ["Nothing; it is a note."],
    NONE,
    ["`machine_describe(machine=\"Info\")`: its one parameter is `Unused value`.",
     "`song_notes_read`: the app's own song notes.",
     "`param_get(machine=\"Info\")`: the value reads 0."])

PAGES["JumpLoop"] = page(
    "A control machine that makes the song loop or jump: when playback reaches it, it jumps to a target position, given in ticks, beats or patterns, a set number of times, optionally holding for a few ticks first. It makes no sound.",
    ["A section that repeats a few times in a live set.",
     "A hold-and-return that loops until stopped."],
    [("Target unit", "Beat", "Tick, Beat or Pattern"),
     ("Jump count", "4", "0 jumps without end"),
     ("Reset count", "Yes", "start the count again at zero"),
     ("Jump target", "0", "the position to return to, in the unit"),
     ("Hold ticks", "0", "0 to 20,000 ticks")],
    ["Repeat the first four beats four times: Target unit Beat, Jump target 0, Jump count 4.",
     "Loop until stopped: Jump count 0."],
    ["A count of 0 loops for as long as the song plays: stop it with `transport(action=\"stop\")`.",
     "The count restarts when play is pressed.",
     "Unverified: exactly where in the song the jump is triggered and how Reset count behaves at zero."],
    ["Any section of the song."],
    "None: `song_set(loop_start_beats=0, loop_end_beats=16, looping=true)` loops a range, but it is not a drop-in for a jump with a count, so `machine_replace` does not apply.",
    ["`machine_describe(machine=\"JumpLoop\")`: five parameters, `Target unit` first.",
     "`param_get(machine=\"JumpLoop\")`: Jump count reads 0 (without end).",
     "`transport(action=\"play\")`: the song returns to the target when it reaches the machine."])

PAGES["Modern Sequence Editor"] = page(
    "A control machine that belongs to the Modern Sequence Editor view, with one parameter that does nothing measurable. It makes no sound. What it does for the editor beyond carrying it was not found.",
    ["Nothing on its own: it appears in songs saved with that editor."],
    [("Unused switch", "no", "does nothing")],
    [SELF.format("Modern Sequence Editor"),
     "To work on the arrangement here, `arrangement_get` and `arrangement_place`."],
    ["Unverified: the editor's view is in ReBuzzGUI/ModernSequenceEditor; the Studio has its own arrangement view.",
     "It is a Windows machine; the Studio does not show its window."],
    ["Nothing."],
    NONE,
    ["`machine_describe(machine=\"Modern Sequence Editor\")`: its one parameter is `Unused switch`.",
     "`arrangement_get`: the song's arrangement is read through the tools.",
     "`param_get(machine=\"Modern Sequence Editor\")`: it reads no."])

PAGES["TrackScript"] = page(
    "A control machine that edits a text script for a track in its own window. It makes no sound itself, and its one parameter does nothing measurable. What the script language is was not found.",
    ["Songs that already use it."],
    [("Unused value", "0", "does nothing")],
    [SELF.format("TrackScript")],
    ["Unverified: its DLL names a script canvas and pattern text changes, but what scripts do was not measured.",
     "It references Windows presentation code: the Studio does not show its window."],
    ["The machine its script writes to, if the song has one."],
    NONE,
    ["`machine_describe(machine=\"TrackScript\")`: its one parameter is `Unused value`.",
     "`param_get(machine=\"TrackScript\")`: the value reads 0.",
     "`pattern_list(machine=\"TrackScript\")`: any patterns the song holds for it."])

PAGES["Yitten Mongoose"] = page(
    "A control machine with a window that works on several patterns together and a Bar length from 1 to 128. It makes no sound. Its own window cannot load in this host, so what it does was not measured.",
    ["Songs that already use it."],
    [("Bar length", "16", "1 to 128; what it measures here was not found")],
    [SELF.format("Mongoose")],
    ["Its window subscribes to sequence events that this host changed, so it cannot load here (a known break); the parameter is still usable.",
     "Unverified: what Bar length does, and what the multi-pattern view offers."],
    ["Nothing; Jeskola Live has the same Bar parameter and the same open question."],
    NONE,
    ["`machine_describe(machine=\"Mongoose\")`: its one parameter is `Bar length`.",
     "`param_get(machine=\"Mongoose\")`: Bar length reads 16.",
     "`pattern_list(machine=\"Mongoose\")`: any patterns the song holds for it."])


def run(write):
    problems = []
    for gear, text in sorted(PAGES.items()):
        path = os.path.join(DIR, gear + ".md")
        old = open(path, encoding="utf-8").read() if os.path.exists(path) else None
        if old != text:
            if write:
                with open(path, "w", encoding="utf-8", newline="\n") as f:
                    f.write(text)
            else:
                problems.append(f"{gear}: its page is not what tools/k2d_pages.py says (python3 tools/k2d_pages.py --write)")
    return problems


if __name__ == "__main__":
    found = run("--write" in sys.argv[1:])
    for p in found:
        print(p)
    print(("wrote" if "--write" in sys.argv[1:] else "checked") + f" {len(PAGES)} pages, {len(found)} problems")
    sys.exit(1 if found else 0)
