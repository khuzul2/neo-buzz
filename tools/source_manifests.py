#!/usr/bin/env python3
# Fork (docs/specs/mcp-server.md §12.13, U6): manifests from code for the Pedal effects and ReBuzz's own managed
# machines, which had profiled drafts (tier 3: "a Params.def, or an equivalent attribute table, generates the
# manifest, so it can never drift from the code"). The attribute table is the machine's own [ParameterDecl]s:
# names, ranges, defaults, descriptions, value texts and which are track or played parameters are read from
# them as ReBuzz reads them (ReBuzz/ManagedMachine/MachineParameter.cs). What a raw value means is read from the
# code that uses it: each entry below names its kind and gives the code's expression (`expr`, the raw value as
# v, the source's constants by name, their values read from the source) with the source text it rests on
# (`code`: each must be in the file, whitespace aside). The generator evaluates the expression at every raw
# value and fits the kind exactly (an affine line, a geometric curve, an integer offset) or writes a table, and
# fails when the declarations, the code or the fit disagree with this file. The profiled measurements (type,
# health, profile, notes) stay from the committed manifest, which tools/machine-profiler.py --merge keeps
# up to date as it does a curated one's.
#
# U6 for the Pedal synths (§12.15) added Pedal Tracker, Chord, LFO and Peer Control, which had stayed drafts.
#
#   python3 tools/source_manifests.py            check: every committed manifest is what its source generates
#   python3 tools/source_manifests.py --write    write them (Machines/Manifests/<Effects|Generators>/<Gear>.machine.json)

import json
import math
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PEDAL = "ExternalMachines/source/thepedal/"
FORMAT = "rebuzz-machine-manifest/1"


def P(name, kind, expr=None, code=(), unit=None, flag=None, choices=None, note=None, own_texts=None, group=None, file_extra=None):
    """One parameter: its name as the machine declares it, the manifest kind, the code's expression (raw value v) and the
    source text it rests on, the unit, a flag (min_off: the lowest value is −inf dB; max_inf: the highest is ∞:1), a note
    for the description, own_texts to override whether the machine's texts say the value, and a second file the code is in."""
    return {k: v for k, v in dict(name=name, kind=kind, expr=expr, code=list(code) if isinstance(code, (list, tuple)) else [code],
                                  unit=unit, flag=flag, choices=choices, note=note, own_texts=own_texts, group=group,
                                  file_extra=file_extra).items()
            if v is not None and v != []}


def mcomp_band(b):
    """One band of Pedal MComp (PedalMComp.cs: Work's per-band precompute and the band compressor's use)."""
    m = {"L": "L", "LM": "LM", "HM": "HM", "H": "H"}[b]
    return [
        P(f"{b} Threshold", "db", "-v", f"-(float){m}_Threshold", unit="dB", group=f"{b} band"),
        P(f"{b} Ratio", "ratio", "v >= 127 ? RATIO_LIMIT_VALUE : pow(RATIO_MAX, v / RATIO_MAX_INDEX)",
          ["if (v >= 127) return RATIO_LIMIT_VALUE;", "float t = v / RATIO_MAX_INDEX;", "return MathF.Pow(RATIO_MAX, t);", f"MapRatio({m}_Ratio)"],
          unit=":1", flag="max_inf", group=f"{b} band"),
        P(f"{b} Attack", "time", "ATTACK_MIN_MS * pow(ATTACK_MAX_MS / ATTACK_MIN_MS, v / 127) / 1000",
          ["return loMs * MathF.Pow(hiMs / loMs, t);", f"MapMsLog({m}_Attack,  ATTACK_MIN_MS,  ATTACK_MAX_MS)"], unit="s", group=f"{b} band"),
        P(f"{b} Release", "time", "RELEASE_MIN_MS * pow(RELEASE_MAX_MS / RELEASE_MIN_MS, v / 127) / 1000",
          ["return loMs * MathF.Pow(hiMs / loMs, t);", f"MapMsLog({m}_Release, RELEASE_MIN_MS, RELEASE_MAX_MS)"], unit="s", group=f"{b} band"),
        P(f"{b} Makeup", "db", "v", f"FastMath.DbToLin({m}_Makeup)", unit="dB", group=f"{b} band"),
        P(f"{b} Bypass", "switch", group=f"{b} band"),
    ]


SPECS = [
    # ---- the Pedal effects with drafts -------------------------------------------------------------------------
    {
        "gear": "Pedal Shaper", "kind": "effect", "file": PEDAL + "pedal-shaper/PedalShaper.cs",
        "summary": "Stereo waveshaping distortion: a tilt EQ before a choice of curves (soft tanh, hard clip, asymmetric tube, sine "
                   "wavefolder, 4-bit crush), drive and DC bias into it, tape-like release smear and signal-following dust after "
                   "it, then output gain and a dry/wet mix. No oversampling, so high notes driven hard alias.",
        "params": [
            P("Drive", "db", "(v - GAIN_OFFSET) * GAIN_DB_PER_STEP", "(Drive  - GAIN_OFFSET) * GAIN_DB_PER_STEP", unit="dB"),
            P("Shape", "enum"),
            P("Tone", "linear", "(v - TONE_OFFSET) / (float)TONE_OFFSET * 100", ["(Tone - TONE_OFFSET) / (float)TONE_OFFSET", "_toneLowGain  = 1f - toneN * 0.75f;"],
              unit="%", note="Tilt around 1 kHz: −100 % weights the lows ×1.75 and the highs ×0.25, +98 % the other way, 0 % flat"),
            P("Bias", "linear", "(v - BIAS_OFFSET) * BIAS_PER_STEP * 100", "(Bias - BIAS_OFFSET) * BIAS_PER_STEP", unit="%",
              note="A DC offset into the shaper, in % of full scale (the DC blocker removes it after)"),
            P("Mix", "percent", "v / 127", "float mixN = Mix / 127f;", unit="%"),
            P("Output", "db", "(v - GAIN_OFFSET) * GAIN_DB_PER_STEP", "(Output - GAIN_OFFSET) * GAIN_DB_PER_STEP", unit="dB"),
            P("Hysteresis", "linear", "v / 127 * 10 / 1000", ["float hystN     = Hysteresis / 127f;", "float downSamps = (hystN * 10f) * sr * 0.001f;"], unit="s",
              note="The release smear's time constant (the rise's is a tenth of it); 0 is off"),
            P("Dust", "percent", "v / 127", "_dustGain = (Dust / 127f) * 0.8f;", unit="%"),
        ],
    },
    {
        "gear": "Pedal Gate", "kind": "effect", "file": PEDAL + "pedal-gate/PedalGate.cs",
        "summary": "Noise gate with an open threshold and a lower close threshold (hysteresis), attack, hold and release, peak or RMS "
                   "detection, a closed-gate floor (Range), lookahead and a sidechain input: it shuts quiet passages and lets "
                   "the loud ones through.",
        "params": [
            P("Threshold", "db", "v * 0.5 - 60", "ThresholdParam * 0.5f - 60f", unit="dB"),
            P("Hysteresis", "db", "v", "DbToLin(-HysteresisParam)", unit="dB", note="The gate closes this far under the threshold"),
            P("Attack ms", "linear", "v / 1000", "(AttackMs  * 0.001f * sr_f)", unit="s"),
            P("Hold ms", "linear", "v / 1000", "HoldMs * 0.001f * sr_f", unit="s"),
            P("Release ms", "linear", "v / 1000", "(ReleaseMs * 0.001f * sr_f)", unit="s"),
            P("Range dB", "db", "v - 90", "DbToLin(RangeParam - 90)", unit="dB", note="The closed gate's gain: −90 dB nearly shut, 0 dB no gating"),
            P("Detection", "enum"),
            P("Lookahead ms", "linear", "v / 1000", "(int)(LookaheadMs * 0.001f * sr_f)", unit="s"),
            P("Bypass", "switch"),
        ],
    },
    {
        "gear": "Pedal Gain", "kind": "effect", "file": PEDAL + "pedal-gain/PedalGainMachine.cs",
        "summary": "Stereo gain in percent (100 % is unity, 200 % +6 dB) with a mute that fades over Inertia.",
        "params": [
            P("Gain", "linear", "v * 0.01 * 100", "Gain * 0.01f", unit="%", note="Output level in % of the input"),
            P("Mute", "switch"),
            P("Inertia", "linear", "v / 1000", "1f / (Inertia * sr / 1000f)", unit="s", note="Mute's fade: gain moves its full range in this time"),
        ],
    },
    {
        "gear": "Pedal Gain Multi", "kind": "effect", "file": PEDAL + "pedal-gain-multi/PedalGainMultiMachine.cs",
        "summary": "A mixer of up to 24 inputs, one track each, with a fader, pan (balance on stereo, constant power on mono), mono, "
                   "mute and solo per track, a master fader and mute, and fades over Inertia; each track also has a direct out.",
        "params": [
            P("Gain", "db", "(v - 161) * 0.5", ["if (code <= 0) return \"-inf dB\";", "float db = (code - 161) * 0.5f;", "float tgtMasterGain = LevelTable[gain];"],
              unit="dB", flag="min_off"),
            P("Master Mute", "switch"),
            P("Inertia", "linear", "v / 1000", "float fadeSec  = inertia * 0.001f;", unit="s"),
        ],
        "track_params": [
            P("Solo", "switch"),
            P("Mute", "switch"),
            P("Volume", "db", "(v - 161) * 0.5", ["if (code <= 0) return \"-inf dB\";", "float db = (code - 161) * 0.5f;"], unit="dB", flag="min_off"),
            P("Pan", "bipolar", "(v - 64) / 64", ["if (pan <= 64) { bl = 1f;                      br = pan  * (1f / 64f); }",
                                                  "else           { bl = (128 - pan) * (1f / 64f); br = 1f;                }"], unit="%"),
            P("Mono", "switch"),
        ],
    },
    {
        "gear": "Pedal MComp", "kind": "effect", "file": PEDAL + "pedal-mcomp/PedalMComp.cs",
        "summary": "Four-band compressor: Linkwitz–Riley 24 dB/oct crossovers split the input into low, low-mid, high-mid and "
                   "high bands, each with its own threshold, ratio, attack, release, makeup and bypass, a shared knee and peak or "
                   "RMS detection; then output gain, a dry/wet mix, lookahead and optional phase-linear summing.",
        "params": [
            P("Listen", "enum"),
            P("Xover L-LM", "freq", "XO1_MIN_HZ * pow(XO1_MAX_HZ / XO1_MIN_HZ, v / 127)",
              ["return loHz * MathF.Pow(hiHz / loHz, t);", "MapHzLog(XoverLLM,  XO1_MIN_HZ, XO1_MAX_HZ)"], unit="Hz", group="Crossovers"),
            P("Xover LM-HM", "freq", "XO2_MIN_HZ * pow(XO2_MAX_HZ / XO2_MIN_HZ, v / 127)",
              ["return loHz * MathF.Pow(hiHz / loHz, t);", "MapHzLog(XoverLMHM, XO2_MIN_HZ, XO2_MAX_HZ)"], unit="Hz", group="Crossovers"),
            P("Xover HM-H", "freq", "XO3_MIN_HZ * pow(XO3_MAX_HZ / XO3_MIN_HZ, v / 127)",
              ["return loHz * MathF.Pow(hiHz / loHz, t);", "MapHzLog(XoverHMH,  XO3_MIN_HZ, XO3_MAX_HZ)"], unit="Hz", group="Crossovers"),
            P("Knee", "db", "v", "float kneeDb  = Knee;", unit="dB"),
            P("Detection", "enum"),
        ] + mcomp_band("L") + mcomp_band("LM") + mcomp_band("HM") + mcomp_band("H") + [
            P("Output Gain", "db", "v - 24", "FastMath.DbToLin(OutputGain - 24)", unit="dB", group="Output"),
            P("Dry-Wet", "linear", "v * 0.01 * 100", "float wet        = DryWet * 0.01f;", unit="%", group="Output"),
            P("Lookahead", "table", "v > 0 ? 0.1 * pow(100, (v - 1) / 126) / 1000 : 0",
              ["if (Lookahead > 0)", "float ms = 0.1f * MathF.Pow(100f, (Lookahead - 1) / 126f);"], unit="s", group="Output",
              note="Off at 0"),
            P("Phase Linear", "switch", group="Output"),
            P("Spectrum View", "switch", group="Output", note="Shows the output's spectrum in its window; no effect on the sound"),
        ],
    },
    # ---- the Pedal machines that were drafts after U6 (mcp-server.md §12.15) ------------------------------------
    {
        "gear": "Pedal Tracker", "kind": "generator", "file": PEDAL + "pedal-tracker/PedalTracker.cs", "section": "§12.15",
        "summary": "A tracker after BTDSys Matilde: up to 16 tracks, each a voice playing a wave from the song's wavetable with "
                   "its own volume and two effect-command columns (pitch slides, tone portamento, vibrato, sample offset, volume "
                   "slides, retrigger, note cut and delay); a master level, master and per-track mutes that fade over Mute "
                   "Inertia, a choice of interpolation, and an output per track beside the master mix. MIDI notes play the wave "
                   "track 1 last chose (wave 1 until it chooses one), on free voices from the last.",
        "params": [
            P("Master", "linear", "v / 32768 * 100", "float  master = MasterParam / 32768f;", unit="%",
              note="The master mix's level in % of unity (0x8000), up to 200 %; the per-track outputs leave it out"),
            P("Mute", "switch"),
            P("Mute Inertia", "linear", "v / 1000", "(inertiaMs / 1000f) * _engineRate", unit="s", own_texts=False,
              note="0 is a one-block fade. Its own text (the engine's Milliseconds descriptor) reads the raw value over 500, "
                   "0.0 … 1.0 ms; the code takes the raw value as ms"),
        ] + [P(f"Mute Track {i}", "switch") for i in range(1, 17)] + [
            P("Interpolation", "enum"),
        ],
        "track_params": [
            P("Wave", "int", "v", "v.PendingWave = (byte)value;", note="The wave slot the track plays from its next note on; setting it sets the track's volume to unity"),
            P("Volume", "table", "v > 0 ? 40 * Math.Log10(v / 128) : -inf", ["float davol    = v.PendingVolume / 128f;", "v.Volume       = davol * davol;"],
              unit="dB", note="The amplitude is (v / 128)²: 0x80 is unity, 0xFE +11.9 dB, 0 silent"),
            P("Cmd", "int", "v", "v.PendingCmd[slot] = (byte)value;", note="01/02 slide, 03 tone portamento, 04 vibrato, 09 sample offset, 0A volume slide, 0C set volume, 0E extended"),
            P("Arg", "int", "v", "v.PendingArg[slot] = (byte)value;"),
            P("Cmd2", "int", "v", "v.PendingCmd[slot] = (byte)value;"),
            P("Arg2", "int", "v", "v.PendingArg[slot] = (byte)value;"),
        ],
    },
    {
        "gear": "Pedal Chord", "kind": "generator", "file": PEDAL + "pedal-chord/PedalChord.cs", "section": "§12.15",
        "summary": "A control machine that turns each note in its track into a chord or an arpeggio on a target machine: 51 voicings, "
                   "chord or five arpeggio modes, a step and octave range with octave walks, swing, and humanised timing and "
                   "velocity. It makes no sound of its own.",
        "params": [],
        "track_params": [
            P("Velocity", "int", "v", "_vs.Velocity = Math.Max(1, Math.Min(127, value));", note="The velocity it sends"),
            P("Chord", "enum"),
            P("Mode", "enum"),
            P("Speed", "int", "v", "_vs.Speed = Math.Max(1, value);", unit="ticks", note="Ticks between arpeggio steps"),
            P("Length", "int", "v", ["_vs.Length = Math.Max(0, value);", "_vs.SlotOff[0]   = _vs.Length > 0 ? _vs.Length : 0;"], unit="ticks",
              note="0 holds each note until the next"),
            P("Octaves", "int", "v", "_vs.OctaveSpread = Math.Max(1, Math.Min(4, value));"),
            P("Step", "int", "v", "_vs.Step = Math.Max(1, Math.Min(8, value));"),
            P("Oct Walk", "enum"),
            P("Swing", "percent", "v / 100", "double ratio  = 1.0 + _vs.Swing / 100.0;", unit="%",
              note="The long step of each pair is (1 + Swing) of the short: 100 % is 2:1"),
            P("Swing On", "enum", choices=["1st beat", "2nd beat"]),
            P("Humanize", "percent", "v / 100", "double drift = _vs.Speed * _vs.Humanize / 200.0;", unit="%",
              note="Each step's timing moves by up to ±half this share of a step, at random"),
            P("Hum. Vel", "percent", "v / 100", "? (int)Math.Round(_vs.Velocity * _vs.HumanizeVel / 200.0) : 0;", unit="%",
              note="Each step's velocity moves by up to ±half this share of it, at random"),
        ],
    },
    {
        "gear": "Pedal LFO", "kind": "generator", "file": PEDAL + "pedal-lfo/PedalLFO.cs", "section": "§12.15",
        "summary": "A control machine that moves another machine's parameter with a low-frequency wave (sine, triangle, square, "
                   "saws, sample and hold) around a centre by a depth, free-running over a period in ticks or locked to the "
                   "song's beat. It makes no sound of its own.",
        "params": [
            P("Shape", "enum"),
            P("Period", "int", "v", "int    period = Period;", unit="ticks", note="One cycle; Sync other than Free overrides it"),
            P("Depth", "percent", "v / 100", "double swing    = (Depth / 100.0) * range * 0.5;", unit="%"),
            P("Center", "percent", "v / 100", "double centerNorm = Center / 100.0;", unit="%"),
            P("Phase", "linear", "v / 128 * 360", "double phaseOffset = Phase / 128.0;", unit="°",
              note="127 is 357.2° (the code divides by 128)"),
            P("Sync", "enum"),
            P("Bypass", "switch"),
            P("Start", "switch"),
        ],
    },
    {
        "gear": "Pedal Peer Control", "kind": "generator", "file": PEDAL + "pedal-peer-control/PeerCtrl.cs", "section": "§12.15",
        "summary": "A control machine with up to 64 tracks, each sending its Value through a mapping curve to a parameter of "
                   "another machine (or out as MIDI controllers), gliding over Inertia; a track can mirror the one before it. "
                   "It makes no sound of its own.",
        "params": [
            P("Inertia", "linear", "v / 10", "InertiaLength = value / 10.0f;", unit="ticks", note="The glide to a new value; 0 is off"),
        ],
        "track_params": [
            P("Slaved", "switch", choices=["No", "Yes"], note="On a track after the first: it follows the track before"),
            P("Value", "percent", "v / 65534", "ValueChange(_tracks[track], track, value / 65534.0f);", unit="%"),
        ],
    },
    # ---- ReBuzz's own managed machines ---------------------------------------------------------------------------
    {
        "gear": "AudioBlock", "kind": "generator", "file": "Machines/AudioBlock/AudioBlock.cs",
        "summary": "Plays audio clips placed on a timeline in its own window, following the song's position (WDE AudioBlock).",
        "params": [P("Mute", "switch")],
    },
    {
        "gear": "Connection Mixer Console", "kind": "generator", "file": "Machines/ConnectionMixer/ConnectionMixer.cs",
        "summary": "A mixing console for up to 16 machine connections: each strip's Vol and Pan set that connection's amp and pan, "
                   "as the machine view's sliders do (100 % is unity gain at 0x4000; pan centred at 0x4000).",
        "params": [p for i in range(1, 17) for p in (
            P(f"Vol{i}", "linear", "v / 16384 * 100", [f"UpdateConnectionVolume({i - 1}, vol{i});", "conn.Amp = vol;", "interpolatorAmp.Value / 0x4000"],
              unit="%", group=f"Strip {i}", file_extra="ReBuzz/Core/MachineConnectionCore.cs"),
            P(f"Pan{i}", "bipolar", "(v - 16384) / 16384", [f"UpdateConnectionPan({i - 1}, pan{i});", "conn.Pan = pan;", "double panScaled = pan / ((double)0x4000);"],
              unit="%", group=f"Strip {i}", file_extra="ReBuzz/Core/MachineConnectionCore.cs"))],
    },
    {
        "gear": "EnvelopeBlock", "kind": "generator", "file": "Machines/EnvelopeBlock/EnvelopeBlock.cs",
        "summary": "Draws parameter envelopes over patterns in its own window and plays them into the machines they target.",
        "params": [P("NewParamValue", "enum")],
    },
    {
        "gear": "QuickNote", "kind": "generator", "file": "Machines/QuickNote/QuickNote.cs",
        "summary": "A control machine with a notes window for the song; its one parameter does nothing.",
        "params": [P("Dummy", "switch", note="Does nothing")],
    },
    {
        "gear": "ReBuzz Audio In", "kind": "generator", "file": "Machines/ReBuzzAudioIn/ReBuzzAudioIn.cs",
        "summary": "Brings the audio driver's input into the song.",
        "params": [P("Bypass", "switch")],
    },
    {
        "gear": "ReBuzz Audio Out", "kind": "effect", "file": "Machines/ReBuzzAudioOut/ReBuzzAudioOut.cs",
        "summary": "Sends what is connected to it to the audio driver's other outputs.",
        "params": [P("Dummy", "switch", note="Does nothing")],
    },
    {
        "gear": "ReBuzz Groove", "kind": "generator", "file": "Machines/ReBuzzGroove/ReBuzzGroove.cs",
        "summary": "Swing for the whole song: up to 8 steps, each tick's length multiplied by its step's factor in turn.",
        "params": [P("NumSteps", "int", "v", "float[] grooveData = new float[NumSteps];", note="0 is off")] + [
            P(f"Step{i}", "linear", "1.0 + v * 2.0 / 127", f"grooveData[i] = 1.0f + grooveDataParams[i] * 2.0f / 127f;", unit="×",
              note="This step's tick lasts this many ticks") for i in range(1, 9)],
    },
    {
        "gear": "ReBuzz Midi Output", "kind": "generator", "file": "Machines/MidiOutput/MidiOutput.cs",
        "summary": "Sends notes and controller changes from its tracks to a MIDI output device.",
        "params": [],
        "track_params": [
            P("Device", "int", "v", "ne.device = pg.Parameters[0].GetValue(t);", note="The MIDI output device, by its number in NeoBuzz Live's list"),
            P("MidiCC", "int", "v", ["public void MidiCC(int value, int track)", "ce.control = pg.Parameters[3].GetValue(t);"],
              note="Meant as the controller number, but Work reads the controller from the track's fourth parameter (Delay), "
                   "so this one does nothing (MidiOutput.cs)"),
            P("MidiCCValue", "int", "v", "GetCCEvent(track).value = value;", note="The value sent with a controller change"),
            P("Channel", "int", "v", ["ne.channel = pg.Parameters[5].GetValue(t);", "MIDI.Encode(MIDI.NoteOn, midiNote, ne.velocity)"],
              note="Not used: Work reads the track's sixth parameter (MidiCC) as the channel, and encodes every message without one, "
                   "so they go out on channel 1 (MidiOutput.cs)"),
        ],
    },
    {
        "gear": "Session Clip Player", "kind": "effect", "file": "Machines/SessionClipPlayer/SessionClipPlayer.cs",
        "summary": "A Session View audio track: plays its audio clips, and records takes from its input or the master.",
        "params": [P("Monitor", "enum"), P("Input", "enum")],
    },
    {
        "gear": "ManagedMultiIOEffect", "kind": "effect", "file": "Machines/ManagedMultiIOEffect/ManagedMultiIOEffect.cs",
        "summary": "ReBuzz's example of a managed multi-input, multi-output effect: a clean input, a noise input that gets noise "
                   "added, a delay input with feedback; a mix out and each part on its own output.",
        "params": [
            P("Delay Time", "linear", "v / 1000", "(int)Math.Ceiling(value / 1000.0 * sampleRate)", unit="s",
              note="Sizes the delay buffer when set (its getter reads 0, a quirk of the example)"),
            P("Delay Feedback", "percent", "v / 100", "dBuffer[delayIndex] * DelayFeedback / 100.0f", unit="%"),
            P("Noise Volume", "percent", "v / 100", "NoiseVolume * level / 100.0f", unit="%"),
        ],
    },
]


# ---- reading C# ------------------------------------------------------------------------------------------------------

def strip_comments(src):
    """The source with comments blanked (strings kept), so offsets stay."""
    out, i, n = [], 0, len(src)
    while i < n:
        c = src[i]
        if src.startswith("//", i):
            j = src.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i)); i = j
        elif src.startswith("/*", i):
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(re.sub(r"[^\n]", " ", src[i:j])); i = j
        elif c == '"' or src.startswith('@"', i):
            j = i + (2 if c == "@" else 1)
            while j < n and src[j] != '"':
                j += 2 if src[j] == "\\" and c != "@" else 1
            out.append(src[i:j + 1]); i = j + 1
        elif c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == "\\" else 1
            out.append(src[i:j + 1]); i = j + 1
        else:
            out.append(c); i += 1
    return "".join(out)


def split_args(text):
    """Top-level comma-separated parts of an argument list."""
    parts, depth, cur, i = [], 0, [], 0
    while i < len(text):
        c = text[i]
        if c == '"':
            j = i + 1
            while j < len(text) and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            cur.append(text[i:j + 1]); i = j + 1; continue
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        if c == "," and depth == 0:
            parts.append("".join(cur).strip()); cur = []
        else:
            cur.append(c)
        i += 1
    if "".join(cur).strip():
        parts.append("".join(cur).strip())
    return parts


def literal(text, names=None):
    """A ParameterDecl argument's value: a literal, or (U6 for the synths, §12.15) a constant of the machine's or an enum
    member, cast or not ("PERIOD_MIN", "(int)Interp.Cubic"), from <names>."""
    t = text.strip()
    cast = re.match(r"^\(\s*int\s*\)\s*([\w.]+)$", t)
    if names is not None and (cast or re.match(r"^[A-Za-z_][\w.]*$", t)):
        key = cast.group(1) if cast else t
        for k in (key, key.split(".")[-1]):
            if k in names:
                return int(names[k]) if cast or float(names[k]).is_integer() else names[k]
    if t.startswith('"'):
        return json.loads(t)
    if t in ("true", "false"):
        return t == "true"
    m = re.match(r"^new\s*(?:[Ss]tring)?\s*\[\s*\]\s*\{(.*)\}$", t, re.S)
    if m:
        return [literal(x, names) for x in split_args(m.group(1))]
    m = re.match(r"^(?:Descriptors|Transformations)\.(\w+)$", t)
    if m:
        return m.group(1)
    if re.match(r"^0[xX][0-9a-fA-F]+$", t):
        return int(t, 16)
    if re.match(r"^-?\d+$", t):
        return int(t)
    raise ValueError(f"can't read {t!r} in a ParameterDecl")


def source_names(path):
    """The machine's constants (its file) and enum members (by "Enum.Member", from every file of its folder)."""
    src = strip_comments(open(os.path.join(ROOT, path), encoding="utf-8-sig").read())
    names = constants(src)
    folder = os.path.dirname(os.path.join(ROOT, path))
    for f in sorted(os.listdir(folder)):
        if not f.endswith(".cs"):
            continue
        text = strip_comments(open(os.path.join(folder, f), encoding="utf-8-sig").read())
        for m in re.finditer(r"\benum\s+(\w+)\s*(?::\s*\w+\s*)?\{([^}]*)\}", text):
            value = -1
            for part in split_args(m.group(2)):
                k, _, v = part.partition("=")
                value = int(v.strip(), 0) if v.strip() else value + 1
                names[f"{m.group(1)}.{k.strip()}"] = value
    return names


def declarations(path):
    """The [ParameterDecl]s of a machine source, in order: (member name, type, is_method, {argument: value})."""
    src = strip_comments(open(os.path.join(ROOT, path), encoding="utf-8-sig").read())
    names = source_names(path)
    out = []
    for m in re.finditer(r"\[\s*ParameterDecl\s*\(", src):
        i, depth = m.end(), 1
        while depth:
            c = src[i]
            if c == '"':
                i += 1
                while src[i] != '"':
                    i += 2 if src[i] == "\\" else 1
            elif c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
            i += 1
        args = {}
        for part in split_args(src[m.end():i - 1]):
            k, _, v = part.partition("=")
            args[k.strip()] = literal(v, names)
        d = re.compile(r"\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:override\s+|virtual\s+)?([\w.<>\[\]]+)\s+(\w+)\s*(\(|\{|=>)").match(src, i)
        if not d:
            raise ValueError(f"{path}: a ParameterDecl at offset {m.start()} isn't on a public property or method")
        kind, name, opener = d.group(1), d.group(2), d.group(3)
        is_method = opener == "("
        if is_method:
            first = re.match(r"\s*([\w.]+)\s+\w+", src[d.end():])
            kind = first.group(1)
        out.append((name, kind, is_method, args))
    return out


def machine_params(path):
    """The parameters as ReBuzz builds them (MachineParameter.Create): global (properties) and track (methods)."""
    glob, track = [], []
    for member, typ, is_method, a in declarations(path):
        name = a.get("Name") or member
        stateless = bool(a.get("IsStateless"))
        vd = a.get("ValueDescriptions")
        if a.get("IsWaveNumber"):
            lo, hi, de = 1, 200, 1   # MachineParameter.Create: a wave number is 1–200, default 1, whatever it declares
        elif typ == "bool":
            lo, hi, de = 0, 1, 1 if a.get("DefValue") is True else 0
        elif typ == "Note":
            continue
        else:
            lo, hi = a.get("MinValue", 0), a.get("MaxValue", 0)
            de = a.get("DefValue")
            if vd is not None and (de is None or hi < len(vd) - 1):
                lo, hi = 0, len(vd) - 1
                de = 0 if de is None else de
            de = lo if de is None else de
        p = {"member": member, "type": typ, "name": name, "min": lo, "max": hi, "default": de, "stateless": stateless,
             "description": a.get("Description") or name, "texts": vd, "descriptor": a.get("ValueDescriptor")}
        (track if is_method else glob).append(p)
    # Track parameters that hold no value are played, not set (SongModel.Params): manifests leave them out.
    return glob, [p for p in track if not p["stateless"]]


def constants(src):
    out = {}
    for m in re.finditer(r"\bconst\s+(?:int|float|double)\s+([^;]+);", src):
        for part in split_args(m.group(1)):
            k, _, v = part.partition("=")
            try:
                out[k.strip()] = evaluate(v, out)
            except Exception:   # a constant that isn't plain arithmetic: no mapping here uses it
                pass
    return out


def to_python(expr):
    e = expr
    e = re.sub(r"(?<![\w.])(\d+\.?\d*(?:[eE][+-]?\d+)?)[fFdDmM]\b", r"\1", e)
    e = re.sub(r"\((?:float|double|int)\)", "", e)
    e = re.sub(r"\bMathF?\.(Pow|Exp|Log10|Log|Sqrt|Abs|PI)\b", lambda m: "math." + {"Pow": "pow", "Exp": "exp", "Log10": "log10", "Log": "log",
                                                                                   "Sqrt": "sqrt", "Abs": "fabs", "PI": "pi"}[m.group(1)], e)
    e = re.sub(r"\bpow\(", "math.pow(", e) if "math.pow" not in e else e
    m = re.match(r"^(.*?)\?(.*):(.*)$", e)
    if m:
        e = f"(({m.group(2)}) if ({m.group(1)}) else ({m.group(3)}))"
    return e


def evaluate(expr, names, v=None):
    env = dict(names)
    env.setdefault("inf", math.inf)
    if v is not None:
        env["v"] = v
    return eval(to_python(expr), {"math": math, "__builtins__": {}}, env)


def normalise(text):
    return re.sub(r"\s+", "", text)


# ---- the manifest ----------------------------------------------------------------------------------------------------

class Problem(Exception):
    pass


def close(a, b, tol=1e-5):
    if math.isinf(a) or math.isinf(b):
        return a == b
    return abs(a - b) <= tol * max(1.0, abs(a), abs(b))


def mapping(spec, p, consts):
    """The manifest fields for one parameter's mapping, checked against every raw value."""
    kind, lo, hi = spec["kind"], p["min"], p["max"]
    out = {"kind": kind}
    if spec.get("unit"):
        out["unit"] = spec["unit"]
    if kind == "enum":
        names = spec.get("choices") or p["texts"]
        if not names or len(names) != hi - lo + 1:
            raise Problem(f"{p['name']}: an enum needs {hi - lo + 1} value texts or choices")
        out["choices"] = list(names)
        return out
    if kind == "switch":
        if hi - lo != 1:
            raise Problem(f"{p['name']}: a switch over {hi - lo + 1} values")
        out["choices"] = list(spec.get("choices") or p["texts"] or ["Off", "On"])
        return out
    f = {v: evaluate(spec["expr"], consts, v) for v in range(lo, hi + 1)}
    flag = spec.get("flag")
    fit = [v for v in range(lo, hi + 1) if not (flag == "min_off" and v == lo) and not (flag == "max_inf" and v == hi)]
    t = lambda v: 0.0 if hi == lo else (v - lo) / (hi - lo)
    if kind in ("linear", "db", "percent", "bipolar"):
        v0, v1 = fit[0], fit[-1]
        slope = (f[v1] - f[v0]) / (t(v1) - t(v0))
        a = f[v0] - slope * t(v0)
        b = a + slope
        for v in fit:
            if not close(a + t(v) * (b - a), f[v]):
                raise Problem(f"{p['name']}: not {kind}: the code gives {f[v]} at {v}, the line {a + t(v) * (b - a)}")
        if kind == "percent":
            if not (close(a, 0) and close(b, 1)):
                raise Problem(f"{p['name']}: a percent runs 0…1 over the range, the code {a}…{b}")
        elif kind == "bipolar":
            if not (close(a, -1) and close(b, 1)):
                raise Problem(f"{p['name']}: a bipolar runs −1…+1 over the range, the code {a}…{b}")
        else:
            out["a"], out["b"] = round(a, 9), round(b, 9)
        if flag == "min_off":
            out["flag"] = True
    elif kind in ("exp", "freq", "time", "ratio"):
        v0, v1 = fit[0], fit[-1]
        k = math.log(f[v1] / f[v0]) / (t(v1) - t(v0))
        a = f[v0] / math.exp(k * t(v0))
        b = a * math.exp(k)
        for v in fit:
            if not close(a * (b / a) ** t(v), f[v]):
                raise Problem(f"{p['name']}: not geometric: the code gives {f[v]} at {v}")
        out["a"], out["b"] = round(a, 9), round(b, 9)
        if flag == "max_inf":
            out["flag"] = True
    elif kind == "int":
        off = f[lo] - lo
        if any(not close(f[v], v + off) for v in f):
            raise Problem(f"{p['name']}: not an integer count")
        out["a"] = off
    elif kind == "table":
        out["points"] = [[v, ("-inf" if f[v] == -math.inf else "inf" if f[v] == math.inf else round(f[v], 9))] for v in range(lo, hi + 1)]
    else:
        raise Problem(f"{p['name']}: unknown kind {kind}")
    return out


def own_texts(spec, p, src):
    """Whether the machine's own texts say the value (else the server shows the mapping's)."""
    if spec.get("own_texts") is not None:
        return spec["own_texts"]
    if p["texts"] is not None or p["descriptor"] not in (None, "None"):
        return True
    if re.search(r'case\s+"' + re.escape(p["name"]) + r'"\s*:', src) and "DescribeValue" in src:
        return True
    return spec["kind"] == "int" and spec.get("expr", "v").strip() == "v"


def generate(spec, existing):
    path = spec["file"]
    raw = open(os.path.join(ROOT, path), encoding="utf-8-sig").read()
    src = strip_comments(raw)
    consts = constants(src)
    glob, track = machine_params(path)
    m = {"format": FORMAT, "gear": spec["gear"], "kind": spec["kind"], "status": "generated",
         "source": f"generated by tools/source_manifests.py from {path} (mcp-server.md {spec.get('section', '§12.13, U6')})"}
    for k in ("type",):
        if existing and k in existing:
            m[k] = existing[k]
    m["summary"] = spec["summary"]
    decl = re.search(r"\[\s*MachineDecl\s*\((.*?)\)\s*\]", src, re.S)
    max_tracks = 0
    if decl:
        for part in split_args(decl.group(1)):
            k, _, v = part.partition("=")
            if k.strip() == "MaxTracks":
                try:
                    max_tracks = literal(v)
                except ValueError:   # a constant (Pedal Gain Multi: PedalGainMultiMachine.MaxTracks)
                    max_tracks = int(consts[v.strip().split(".")[-1]])
    m["tracks"] = max(1, max_tracks) if track else 0
    for k in ("notes", "health", "profile"):
        if existing and k in existing:
            m[k] = existing[k]
    for group, params, specs in (("parameters", glob, spec.get("params", [])), ("track_parameters", track, spec.get("track_params", []))):
        if [p["name"] for p in params] != [s["name"] for s in specs]:
            raise Problem(f"{spec['gear']} {group}: the source declares {[p['name'] for p in params]}, this file {[s['name'] for s in specs]}")
        entries = []
        for i, (p, s) in enumerate(zip(params, specs)):
            files = [path] + ([s["file_extra"]] if s.get("file_extra") else [])
            text = normalise(" ".join(strip_comments(open(os.path.join(ROOT, f), encoding="utf-8-sig").read()) for f in files))
            for c in s.get("code", []):
                if normalise(c) not in text:
                    raise Problem(f"{spec['gear']} {p['name']}: the code no longer says {c!r}")
            e = {"index": i, "id": p["member"], "name": p["name"]}
            if s.get("group"):
                e["group"] = s["group"]
            e.update({"min": p["min"], "max": p["max"], "default": p["default"]})
            e.update(mapping(s, p, consts))
            if not own_texts(s, p, src):
                e["own_texts"] = False
            desc = p["description"]
            if s.get("note") and s["note"] not in desc:
                desc = desc.rstrip(".") + ". " + s["note"] if desc != p["name"] else s["note"]
            e["description"] = desc
            old = next((q for q in (existing or {}).get(group, []) if q.get("name") == p["name"]), None)
            if old and old.get("safe_range"):
                e["safe_range"] = old["safe_range"]
            entries.append(e)
        m[group] = entries
    return m


def target(spec):
    sub = "Effects" if spec["kind"] == "effect" else "Generators"
    return os.path.join(ROOT, "Machines", "Manifests", sub, spec["gear"] + ".machine.json")


def dump(m):
    return json.dumps(m, indent=1, ensure_ascii=False) + "\n"


def run(write):
    problems = []
    for spec in SPECS:
        path = target(spec)
        existing = None
        if os.path.exists(path):
            with open(path, encoding="utf-8-sig") as f:
                existing = json.load(f)
        try:
            m = generate(spec, existing)
        except (Problem, OSError, ValueError) as e:
            problems.append(f"{spec['gear']}: {e}")
            continue
        text = dump(m)
        if write:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text)
        elif existing is None or dump(existing) != text:
            problems.append(f"{spec['gear']}: {os.path.relpath(path, ROOT)} isn't what its source generates (python3 tools/source_manifests.py --write)")
    return problems


def gears():
    return [s["gear"] for s in SPECS]


if __name__ == "__main__":
    write = "--write" in sys.argv[1:]
    found = run(write)
    for p in found:
        print(p)
    print(("wrote" if write else "checked") + f" {len(SPECS)} manifests from source, {len(found)} problems")
    sys.exit(1 if found else 0)
