#!/usr/bin/env python3
# Fork (docs/specs/mac-engine.md decision 6, M3/M4): the null test. Neo machine renders on Linux and
# macOS (rebuzz-headless) must be bit-identical to the Windows package's (NeoBuzzLive.exe --agent): every
# sample the same. The Neo machines compute only with IEEE basic operations (NeoBuzz.Dsp's portable
# maths, docs/specs/machines.md §4), so nothing may differ.
#
# A fixed set of cases covers every Neo machine, with presets and settings that take their distinct
# code paths (filters, oscillators, FM feedback, saturation, reverb tails). Each case runs in a fresh
# process: it builds a short song through the MCP tools, as a client does, and renders it (32-bit
# float WAV, full scale 1.0). Renders start on a tick boundary, so they are bit-identical across
# processes on one platform (docs/specs/mcp-server.md §10).
#
#   python3 tools/null-test.py render  <exe> <out-dir>              one <case>.wav per case
#   python3 tools/null-test.py compare <exe> <ref-dir> <work-dir>   renders, then compares to ref-dir
#   python3 tools/null-test.py diff    <ref-dir> <dir>               compares two folders of renders
#   ... [--only case,case]
#
# <exe> is NeoBuzzLive.exe (Windows) or rebuzz-headless. compare fails a case on any differing sample, on a
# length or rate mismatch, or when it is missing from either side. A case that differs reports its
# first differing sample (frame, time, channel, both values, how many samples differ) and its largest
# difference in dBFS and where. Under GitHub Actions it writes the figures as a notice annotation
# (readable through the API) and to the step summary. The Windows package job renders the
# references; the Linux and macOS jobs compare.

import array
import json
import math
import os
import platform
import queue
import shutil
import struct
import subprocess
import sys
import threading
import time

TEMPO = 120   # 1 beat = 0.5 s

# Cases excluded on purpose, with the reason: {"case": "why"}. Empty: every case must match.
EXCLUDED = {}

# Pairs of cases that must render the same bits on every platform, as well as each matching Windows':
# a machine that passes its audio through (Neo Scope) leaves the chain it is in as it was.
SAME = [("scope-synced-after-delay", "delay-dotted-throw")]


# ---- the cases ------------------------------------------------------------------------------------

def n(pitch, start, length, velocity=100):
    return {"pitch": pitch, "start": start, "length": length, "velocity": velocity}


CHORDS = [n(p, 0, 1.75, 100) for p in ("C3", "E3", "G3", "B3")] + [n(p, 2, 2, 80) for p in ("A2", "E3", "A3", "C4")]
BASS = [n(p, i * 0.25, 0.2, 70 + (i * 7) % 57) for i, p in enumerate(
    ["A1", "A1", "A2", "A1", "C2", "A1", "G2", "A1", "A1", "E2", "A1", "A2", "D2", "A1", "C2", "E2"])]
LEAD = [n("E4", 0, 0.5), n("G4", 0.5, 0.5), n("A4", 1, 1), n("C5", 2, 0.25), n("B4", 2.25, 0.25), n("A4", 2.5, 1.5, 70)]
KEYS = [n("C4", 0, 1), n("E4", 0, 1), n("G4", 0, 1), n("D4", 1, 0.5, 60), n("F4", 1.5, 0.5, 90),
        n("A3", 2, 2, 110), n("C5", 2, 2, 110), n("E5", 3, 1, 40)]
BELLS = [n("C5", 0, 0.5), n("G5", 0.5, 0.5, 80), n("E5", 1, 1, 60), n("C6", 2, 0.25, 120), n("A4", 3, 1)]

# The source for the effects: Neo FM6 brass (feedback 7, bright, with a pitch envelope), a chord stab, a
# bass and a high note, then silence for the tails. FM6 rendered identically on Linux, macOS and
# Windows, so what an effect case measures is the effect's own difference.
SOURCE = ("Neo FM6", "Brass — Bright Section", [("Unison", 3), ("Uni spread", "100%")],
          [n(p, 0, 0.5, 120) for p in ("C3", "G3", "E4")] + [n("C2", 1, 1, 100), n("G5", 2, 0.25, 127), n("C4", 2.5, 0.75, 60)])
LONG = [n(p, 0, 15, 90) for p in ("A2", "E3", "C4", "G4")] + [n(p, 15, 13, 100) for p in ("F2", "C3", "A3", "E4")] + [n("C5", 2, 24, 50)]


# Neo EQ's graphic sliders, by their names' frequencies ("G 31.5 Hz" … "G 20 kHz").
GRAPHIC = ["20 Hz", "25 Hz", "31.5 Hz", "40 Hz", "50 Hz", "63 Hz", "80 Hz", "100 Hz", "125 Hz", "160 Hz", "200 Hz",
           "250 Hz", "315 Hz", "400 Hz", "500 Hz", "630 Hz", "800 Hz", "1 kHz", "1.25 kHz", "1.6 kHz", "2 kHz",
           "2.5 kHz", "3.15 kHz", "4 kHz", "5 kHz", "6.3 kHz", "8 kHz", "10 kHz", "12.5 kHz", "16 kHz", "20 kHz"]


# Neo Sampler's song waves: written here with IEEE basic operations only (no libm), so every platform
# imports the same samples (32-bit float WAV, 48 kHz): a triangle at 220 Hz (A3), one at 330 Hz
# (E4), and a decaying noise hit (xorshift).

def wave_triangle(hz, seconds=1.0, rate=48000, amp=0.5):
    period = rate / hz
    out = []
    for i in range(int(seconds * rate)):
        ph = (i % period) / period
        out.append(amp * (4 * ph - 1 if ph < 0.5 else 3 - 4 * ph))
    return out


def wave_hit(seconds=0.4, rate=48000, seed=0x1234567):
    out, s, n = [], seed, int(seconds * rate)
    for i in range(n):
        s ^= (s << 13) & 0xFFFFFFFF
        s ^= s >> 17
        s ^= (s << 5) & 0xFFFFFFFF
        u = (s & 0xFFFFFF) / 8388608.0 - 1.0
        d = 1.0 - i / n
        out.append(0.7 * u * d * d * d)
    return out


WAVES = {"tri-a3": lambda: wave_triangle(220.0), "tri-e4": lambda: wave_triangle(330.0), "hit": wave_hit}


def write_wave(path, samples, rate=48000):
    data = struct.pack("<%df" % len(samples), *samples)
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 3, 1, rate, rate * 4, 4, 32)
                + b"data" + struct.pack("<I", len(data)) + data)


def gen(name, gear, preset, params, notes, beats=4, tail=1.0, waves=(), auto=()):
    """A generator; waves: [(slot, WAVES key, root, layer)] imported into the song's wavetable first (Neo Sampler);
    auto: its parameters in its pattern, [(param, track or None, [(beat, value), ...])], in steps (a played parameter,
    a drum's hit column, one event per point)."""
    return {"name": name, "gen": (gear, preset, params, notes), "fx": [], "beats": beats, "tail": tail, "waves": list(waves),
            "gen_auto": list(auto)}


def fx(name, gear, preset, params=(), tail=1.0, extra=(), source=SOURCE, beats=4, auto=()):
    """An effect (or a chain: extra effects after it) on the shared source; auto: the effect's
    parameters automated in steps, [(param, [(beat, value), ...])]."""
    return {"name": name, "gen": source, "fx": [(gear, preset, list(params))] + list(extra), "beats": beats, "tail": tail,
            "auto": list(auto)}


CASES = [
    # Neo Poly: oscillators (blep saw, pulse, sync, FM, ring), ladder and SVF filters, unison, drift, noise.
    gen("poly-default", "Neo Poly", None, [], CHORDS),                     # drift on: per-voice random
    gen("poly-acid", "Neo Poly", "Bass — Acid", [], BASS),
    gen("poly-reese", "Neo Poly", "Bass — Reese", [], BASS[::2]),
    gen("poly-fm-growl", "Neo Poly", "Bass — FM Growl", [], BASS[::2]),
    gen("poly-sync-scream", "Neo Poly", "Lead — Sync Scream", [], LEAD),
    gen("poly-pwm-pad", "Neo Poly", "Pad — Evolving PWM", [], CHORDS),
    gen("poly-noise-sweep", "Neo Poly", "FX — Noise Sweep", [], [n("C4", 0, 3)]),
    gen("poly-self-osc", "Neo Poly", None,
        [("F1 reso", "100%"), ("F1 cutoff", "400 Hz"), ("F1 drive", "12 dB"), ("Ring 1×2", "40%"), ("O2 level", "60%"),
         ("O2 sync", "on"), ("O3 level", "50%"), ("Voice mode", "Legato"), ("Glide mode", "Always")], LEAD),
    gen("poly-svf-parallel", "Neo Poly", None,
        [("F1 type", "SVF BP"), ("F1 reso", "70%"), ("F2 type", "HP"), ("F2 reso", "60%"), ("Routing", "Parallel"),
         ("Noise", "30%"), ("Noise colour", "100%"), ("O1 shape", 3), ("O2 level", "50%"), ("FM 1 → 2", 1.5)], KEYS),
    # Neo Wave: tables built in the background, warps, PM, sync, its filter.
    gen("wave-init", "Neo Wave", "Init — Basic", [], CHORDS),
    gen("wave-vowel-choir", "Neo Wave", "Pad — Vowel Choir", [], CHORDS),
    gen("wave-window-sync", "Neo Wave", "Texture — Window Sync Sweep", [], [n("A2", 0, 3.5)]),
    gen("wave-pm-bloom", "Neo Wave", "Texture — PM Bloom", [], KEYS),
    gen("wave-grit-motion", "Neo Wave", "Texture — Grit Motion", [], [n("D3", 0, 3.5), n("A3", 1, 2.5)]),
    gen("wave-reso-pluck", "Neo Wave", "Bass — Reso Pluck", [], BASS),
    gen("wave-mirror-bell", "Neo Wave", "Pluck — Mirror Bell", [], BELLS),
    # Dye Essence: unison lanes, warps (FM from a silent slot, PM from the sub, window sync, mirror, squeeze at
    # High 4×), the sub and noise, the comb, two filters in parallel.
    gen("dye-init", "Dye Essence", "Init — Basic", [], CHORDS),
    gen("dye-wide-unison", "Dye Essence", "Lead — Wide Unison", [], KEYS),
    gen("dye-fm-growl", "Dye Essence", "Bass — FM Growl", [], BASS),
    gen("dye-sub-pm", "Dye Essence", "Lead — Sub PM", [], KEYS),
    gen("dye-two-filters", "Dye Essence", "Pad — Two Filters", [], CHORDS),
    gen("dye-comb-noise", "Dye Essence", "Texture — Comb Noise", [], KEYS),
    gen("dye-mirror-bell", "Dye Essence", "Pluck — Mirror Bell", [], BELLS),
    gen("dye-squeeze-high", "Dye Essence", "FX — Squeeze Sweep", [], [n("A2", 0, 3.5)]),
    # Neo Learn L0: the Subtractive pilot (one Dye Essence patch behind 12 controls), four presets and the init.
    gen("learn-sub-init", "Neo Learn Subtractive", None, [], CHORDS),
    gen("learn-sub-pluck-bass", "Neo Learn Subtractive", "Bass — Sub Pluck", [], BASS),
    gen("learn-sub-wide-pad", "Neo Learn Subtractive", "Pad — Unison Wash", [], CHORDS),
    gen("learn-sub-morph-lead", "Neo Learn Subtractive", "Lead — Pulse Morph", [], LEAD),
    # Neo Learn L1a: FM (exact ratio, feedback), Wavetable (frame scan, phase warp) and Additive (comb, shimmer).
    gen("learn-fm-tine-keys", "Neo Learn FM", "Keys — Tine Piano", [], KEYS),
    gen("learn-fm-growl-bass", "Neo Learn FM", "Bass — Growl", [], BASS),
    gen("learn-wt-formant-pad", "Neo Learn Wavetable", "Pad — Formant Drift", [], CHORDS),
    gen("learn-wt-sync-lead", "Neo Learn Wavetable", "Lead — Sync Sweep", [], LEAD),
    gen("learn-add-shimmer-pad", "Neo Learn Additive", "Pad — Choir Shimmer", [], CHORDS),
    gen("learn-add-glass-bell", "Neo Learn Additive", "Bell — Glass Chime", [], BELLS),
    # Neo Learn L1b: Granular (window morph, octave grains) and Physical (exciter morph, geometry blend).
    gen("learn-grain-frozen-choir", "Neo Learn Granular", "Pad — Frozen Choir", [], CHORDS),
    gen("learn-grain-octave-glow", "Neo Learn Granular", "Pad — Octave Glow", [], CHORDS),
    gen("learn-phys-rosewood", "Neo Learn Physical", "Marimba — Rosewood", [], KEYS),
    gen("learn-phys-cello-bow", "Neo Learn Physical", "Bow — Cello Strings", [], BASS),
    # Dye Essence P2: the Sample engine (looped, eight-voice unison, one-shot, reversed, two slots) and the
    # Granular engine (frozen, scanning, a spray of pitched grains with reversed ones); the grain clouds and
    # the sample recipes are built from the portable maths, so every platform renders the same.
    gen("dye-sample-choir", "Dye Essence", "Pad — Sample Choir", [], CHORDS),
    gen("dye-sample-strings", "Dye Essence", "Pad — Eight Strings", [], CHORDS),
    gen("dye-sample-bell", "Dye Essence", "Keys — Struck Bell", [], BELLS),
    gen("dye-sample-pluck", "Dye Essence", "Pluck — Double String", [], KEYS),
    gen("dye-sample-reverse", "Dye Essence", "FX — Reverse Pluck", [], KEYS),
    gen("dye-grains-frozen", "Dye Essence", "Texture — Frozen Cloud", [], CHORDS),
    gen("dye-grains-scan", "Dye Essence", "Pad — Scanning Strings", [], CHORDS),
    gen("dye-grains-rain", "Dye Essence", "FX — Bell Rain", [], BELLS),
    # Dye Essence P3a: the Harmonic engine (morphs, odd/even, stretch, damp, a drawbar set, a wide unison); the
    # phasor bank is rotations from polynomial sine and cosine, so every platform renders the same.
    gen("dye-harm-strings", "Dye Essence", "Pad — Additive Strings", [], CHORDS),
    gen("dye-harm-organ", "Dye Essence", "Keys — Drawbar Organ", [], KEYS),
    gen("dye-harm-reed", "Dye Essence", "Lead — Reed Morph", [], KEYS),
    gen("dye-harm-vowel", "Dye Essence", "Pad — Vowel Choir", [], CHORDS),
    gen("dye-harm-glass", "Dye Essence", "Bell — Stretched Glass", [], BELLS),
    gen("dye-harm-wire", "Dye Essence", "Pluck — Damped Wire", [], KEYS),
    gen("dye-harm-pipe", "Dye Essence", "Bass — Hollow Pipe", [], BASS),
    gen("dye-harm-random", "Dye Essence", "Texture — Random Partials", [], CHORDS),
    # Dye Essence P3b: the Modal engine (struck, plucked and continuous exciters; the six bodies; stiffness, loss, ring, size); the
    # resonators are rotations from polynomial sine and cosine and the portable exponential, so every platform renders the same.
    gen("dye-modal-marimba", "Dye Essence", "Keys — Glass Marimba", [], KEYS),
    gen("dye-modal-bronze", "Dye Essence", "Bell — Struck Bronze", [], BELLS),
    gen("dye-modal-nylon", "Dye Essence", "Pluck — Nylon String", [], KEYS),
    gen("dye-modal-bowed", "Dye Essence", "Pad — Bowed Glass", [], CHORDS),
    gen("dye-modal-pipe", "Dye Essence", "Lead — Breath Pipe", [], KEYS),
    gen("dye-modal-skin", "Dye Essence", "Bass — Skin Drum", [], BASS),
    gen("dye-modal-plate", "Dye Essence", "Keys — Steel Plate", [], KEYS),
    gen("dye-modal-bellpipe", "Dye Essence", "Pad — Bowed Bell and Pipe", [], CHORDS),
    # P4a (docs/specs/dye-essence.md §4.9): the arpeggiator and the sequencer follow the song's position; the notes land on exact samples.
    gen("dye-arp-pluck", "Dye Essence", "Arp — Glass Pluck Up", [], CHORDS),
    gen("dye-arp-saw", "Dye Essence", "Arp — Saw Up Down", [], CHORDS),
    gen("dye-arp-triplet", "Dye Essence", "Arp — Triplet Chord Stabs", [], CHORDS),
    gen("dye-arp-bells", "Dye Essence", "Arp — Random Bells", [], CHORDS),
    gen("dye-arp-organ", "Dye Essence", "Arp — Dotted Organ Down", [], CHORDS),
    gen("dye-arp-latch", "Dye Essence", "Arp — Latched Played Pad", [], CHORDS),
    gen("dye-seq-acid", "Dye Essence", "Seq — Acid Line", [], BASS),
    gen("dye-seq-bass", "Dye Essence", "Seq — Pulse Bass", [], BASS),
    gen("dye-seq-filter", "Dye Essence", "Seq — Filter Steps", [], CHORDS),
    gen("dye-seq-prob", "Dye Essence", "Seq — Probability Pluck", [], KEYS),
    # The effects rack (docs/specs/dye-essence.md §4.10): each hosted type, the layouts, the chains' order.
    gen("dye-fx-hall", "Dye Essence", "Rack — Bell In A Hall", [], KEYS, tail=3.0),
    gen("dye-fx-echo", "Dye Essence", "Rack — Dotted Echo Keys", [], KEYS, tail=3.0),
    gen("dye-fx-clip", "Dye Essence", "Rack — Saturated Bass", [], BASS),
    gen("dye-fx-filter", "Dye Essence", "Rack — Sweeping Choir", [], CHORDS),
    gen("dye-fx-eqcomp", "Dye Essence", "Rack — Tilted And Squeezed Pluck", [], KEYS),
    gen("dye-fx-duo", "Dye Essence", "Rack — Duo Plate Keys", [], KEYS, tail=3.0),
    gen("dye-fx-parallel", "Dye Essence", "Rack — Parallel Crush Bass", [], BASS, tail=2.0),
    gen("dye-fx-choir", "Dye Essence", "Rack — Delayed Choir", [], CHORDS, tail=3.0),
    # Neo FM6: algorithms, feedback, the Clean 2x and Raw 1x engines, envelopes, LFO.
    gen("fm6-tine-piano", "Neo FM6", "Keys — Tine Piano", [], KEYS),
    gen("fm6-glass-bell", "Neo FM6", "Bell — Glass Bell", [], BELLS, tail=2.0),
    gen("fm6-random-chimes", "Neo FM6", "Bell — Random Chimes", [], BELLS),
    gen("fm6-brass", "Neo FM6", "Brass — Bright Section", [], CHORDS),
    gen("fm6-saw-lead", "Neo FM6", "Lead — Saw Lead", [], LEAD),
    gen("fm6-metal-sweep", "Neo FM6", "Texture — Metal Sweep", [], [n("C3", 0, 3.5)]),
    gen("fm6-raw-feedback", "Neo FM6", "Init — Sine", [("Feedback", 7), ("Engine", "Raw 1×"), ("LFO pitch", 40)], LEAD),
    # Neo Comp: the four characters, lookahead, knee, auto release.
    fx("comp-glue", "Neo Comp", "Glue — Mix Bus", [("Threshold", "-30 dB")]),
    fx("comp-fet", "Neo Comp", "FET — All Buttons"),
    fx("comp-opto", "Neo Comp", "Opto — Smooth Vocal", [("Threshold", "-30 dB")]),
    fx("comp-clean-brickwall", "Neo Comp", "Clean — Brickwall", [("Lookahead", "3 ms"), ("Auto release", "on")]),
    # Neo Drive: the five models, oversampling 2x/4x/8x.
    fx("drive-tape", "Neo Drive", "Tape — Hot Reels"),
    fx("drive-tube", "Neo Drive", "Tube — Preamp Push"),
    fx("drive-transistor", "Neo Drive", "Transistor — Fuzz Wall", [("Quality", "8×")]),
    fx("drive-diode", "Neo Drive", "Diode — Edge", [("Quality", "2×")]),
    fx("drive-fold", "Neo Drive", "Fold — West Coast"),
    # Neo EQ: bells, shelves, cuts at each slope, notch.
    fx("eq-vocal-clarity", "Neo EQ", "Mixing — Vocal Clarity"),
    fx("eq-telephone", "Neo EQ", "Mixing — Telephone"),
    fx("eq-hum-notch", "Neo EQ", "Fix — Hum Notch 50 Hz", [("B1 on", "on"), ("B1 slope", "48 dB/oct"), ("B8 on", "on")]),
    # Neo EQ's graphic mode (machine-safety.md §4): the solver's result (the sliders' median as a gain, the
    # telephone's at −18 dB), the zigzag at ±18 dB (bands near 54 dB), and sliders and the mode automated
    # (the gains gliding in segments, the crossfade between the modes).
    fx("eq-graphic-loudness", "Neo EQ", "Graphic — Loudness (Smile)"),
    fx("eq-graphic-telephone", "Neo EQ", "Graphic — Telephone", [("Output", "+12 dB")]),
    fx("eq-graphic-zigzag", "Neo EQ", "Graphic — Flat",
       [(f"G {f}", "+18 dB" if i % 2 == 0 else "-18 dB") for i, f in enumerate(GRAPHIC)] + [("Output", "-18 dB")]),
    fx("eq-graphic-automated", "Neo EQ", "Graphic — Air",
       auto=[("G 250 Hz", [(0, "-12 dB"), (1, "+12 dB"), (1.5, "-18 dB"), (2, "+6 dB")]),
             ("G 2 kHz", [(0, "+9 dB"), (0.75, "-9 dB"), (2.25, "+18 dB")]),
             ("Mode", [(0, "Graphic"), (2.5, "Parametric"), (3.25, "Graphic")])]),
    # Neo EQ v2 (docs/specs/mastering-tools.md §5.3, M3): dynamic bands down and up (the detector on the band's shape,
    # its envelope from the 90° pair, the 16-sample grid and its spline), Mid and Side bands, the shapes, Delta.
    fx("eq-v2-deess", "Neo EQ", "Dynamic — De-ess", [("B6 threshold", "-50 dB"), ("B6 range", "12 dB")]),
    fx("eq-v2-mid-resonance", "Neo EQ", "Dynamic — Resonance Control (Mid)", [("B4 threshold", "-50 dB"), ("B4 Q", 1.5)],
       auto=[("B4 channel", [(2, "Side")])]),
    fx("eq-v2-up-click", "Neo EQ", "Dynamic — Click on Hits (Up)", [("B6 threshold", "-50 dB"), ("B3 dynamic", "Down"), ("B3 freq", "300 Hz")]),
    fx("eq-v2-shapes", "Neo EQ", "Character — Baxandall Tilt", [("B3 shape", "Proportional Q"), ("B3 gain", "+6 dB"), ("B4 shape", "All-pass"),
       ("B5 channel", "Left"), ("B5 gain", "+4 dB"), ("B6 channel", "Right"), ("B6 gain", "-5 dB")]),
    fx("eq-v2-vintage-ms-delta", "Neo EQ", "Character — Vintage Low Boost", [("B1 on", "on"), ("B1 channel", "Side"), ("B1 freq", "150 Hz"),
       ("B8 on", "on"), ("B8 channel", "Mid"), ("B8 freq", "9 kHz")], auto=[("Delta", [(3, "On")])]),
    # Neo EQ linear phase (docs/specs/mastering-tools.md §5.6, M5b): the FIR from the bands' magnitude, a Mid band and the curve layer
    # in it, a parameter moving (a redesign, crossfaded), Delta (latency-compensated).
    fx("eq-linear-tilt", "Neo EQ", "Linear — Master Tilt (+1.5 dB low, -1.5 dB air)"),
    fx("eq-linear-cut-ms", "Neo EQ", "Linear — Bus Low Cut (28 Hz, 48 dB/oct)", [("B8 on", "on"), ("B8 channel", "Mid"), ("B8 freq", "9 kHz"), ("Curve 12", "+3 dB"), ("Curve 13", "+3 dB")],
       auto=[("B2 gain", [(1, "+6 dB"), (2, "-3 dB")]), ("Delta", [(3, "On")])]),
    # Neo Limiter: pushed hard, auto and set release; Neo Analyzer after it (audio passes through).
    fx("limiter-loud", "Neo Limiter", "Mastering — Loud", [("Input gain", "+18 dB")], extra=[("Neo Analyzer", "Mix", [])]),
    fx("limiter-smash", "Neo Limiter", "Creative — Smash", [("Input gain", "+24 dB"), ("Auto release", "off")]),
    # Neo Limiter v2 (docs/specs/mastering-tools.md §5.2, M2): each style's stages; the soft clip switched on while playing
    # (its latency moving through the engine's crossfade); upward compression and the slow stage on a quiet passage.
    fx("limiter-v2-transparent", "Neo Limiter", "Style — Transparent", [("Input gain", "+12 dB")]),
    fx("limiter-v2-punchy", "Neo Limiter", "Style — Punchy", [("Input gain", "+9 dB")]),
    fx("limiter-v2-dense", "Neo Limiter", "Style — Dense (Soft Clip 9 dB, Upward)", [("Clip mix", "70%")]),
    fx("limiter-v2-loud-switched", "Neo Limiter", "Style — Loud (Soft Clip 3 dB)", [("Input gain", "+9 dB")],
       auto=[("Soft clip", [(0, "Off"), (2, "3 dB")])]),
    fx("limiter-v2-upward-quiet", "Neo Limiter", "Style — Classic", [("Input gain", "-12 dB"), ("Upward", "6 dB"), ("Upward threshold", "-10 dB"),
                                                                       ("Slow stage", "60%")]),
    # Neo Utility: gain, pan law, width, bass mono, polarity, swap, DC removal.
    fx("utility-bass-mono-wide", "Neo Utility", "Mastering — Bass Mono 120 Hz", [("Width", "180%"), ("Gain", "-3.5 dB")]),
    fx("utility-pan-dc-swap", "Neo Utility", "Mastering — Clean Up (Bass Mono 80 Hz, DC)",
       [("Pan law", "Mono pan"), ("Pan", "-30%"), ("Swap", "on"), ("Invert R", "on")]),
    # The mid/side matrix (docs/specs/mastering-tools.md, M1): encode, a Neo EQ on mid and side, decode; the encoder
    # switched off and on again by automation (its 5 ms crossfades).
    fx("utility-ms-eq-chain", "Neo Utility", "Mid/Side — Encode (Before a Stereo Machine)", [("Width", "120%")],
       extra=[("Neo EQ", "Mastering — Air Shelf", []), ("Neo Utility", "Mid/Side — Decode (After It)", [])],
       auto=[("Matrix", [(2, "Off"), (3, "Encode M/S")])]),
    # Neo Clipper (docs/specs/mastering-tools.md §5.1, M2): the knee at 4× before a limiter; hard at 1×; three bands in
    # mid/side with the drive automated (its ramps, the second stage, the ceiling guard); Delta switched on by automation.
    fx("clipper-knee-limiter", "Neo Clipper", "Mastering — Transparent (Before a Limiter)", [("Drive", "+9 dB")],
       extra=[("Neo Limiter", "Mastering — Streaming (−1 dBTP)", [])]),
    fx("clipper-hard-1x", "Neo Clipper", "Character — 1× Grit", [("Mix", "100%")]),
    fx("clipper-bands-ms", "Neo Clipper", "Mastering — Three Bands", [("Stereo", "M/S"), ("Oversampling", "8×")],
       auto=[("Drive", [(0, "+12 dB"), (1.5, "+3 dB"), (2.5, "+18 dB")])]),
    fx("clipper-delta", "Neo Clipper", "Drums — Hard Snap", [("Drive", "+12 dB")], auto=[("Delta", [(2, "On")])]),
    # Neo Transient (docs/specs/mastering-tools.md §5.4, M4): the onset/body gains with a lookahead (Attack automated: its
    # smoothed ramps); mid/side with a lookahead of 0; Impact mode (its four bands, the knobs and the mode automated:
    # the 20 ms crossfades); Delta switched on by automation.
    fx("transient-drums", "Neo Transient", "Drums — More Snap", [("Sustain", "-3 dB")],
       auto=[("Attack", [(0, "+9 dB"), (1.5, "-4 dB"), (2.5, "+12 dB")])]),
    fx("transient-ms-live", "Neo Transient", "Mid/Side — Punch the Mid", [("Lookahead", "0.0 ms")]),
    fx("transient-impact", "Neo Transient", "Mastering — Impact (Lows Up, Highs Calm)", [("Stereo", "M/S")],
       auto=[("Impact 2", [(0, "+15%"), (1.5, "-60%"), (2.5, "+80%")]), ("Mode", [(0.5, "Attack Sustain"), (1, "Impact")])]),
    fx("transient-delta", "Neo Transient", "Drums — Tighter (Less Room)", [], auto=[("Delta", [(2, "On")])]),
    # Neo Low End (§5.5, M4): minimum phase with the kick preset (Punch automated); linear phase (the FIR's 96 ms: a
    # longer tail); mono below a frequency with peak control; Balance toward the mids, the crossover moved.
    fx("lowend-kick", "Neo Low End", "Drums — Kick Punch", [],
       auto=[("Punch", [(0, "+4 dB"), (1.5, "-3 dB"), (2.5, "+8 dB")])]),
    fx("lowend-linear", "Neo Low End", "Mastering — Linear Phase Split", [("Sustain", "-2 dB")], tail=1.5),
    fx("lowend-mono-peak", "Neo Low End", "Mastering — Peak Control (−3 dBFS)", [("Mono", "On"), ("Mono freq", "90 Hz")],
       auto=[("Peak level", [(0, "-3 dB"), (2, "-9 dB")])]),
    fx("lowend-balance", "Neo Low End", "Mastering — Balance to the Mids (+6 dB)", [],
       auto=[("Crossover", [(0, "150 Hz"), (2, "80 Hz")])]),
    # Neo Imager (docs/specs/mastering-tools.md §5.7, M5): the widths and Stereoize (a width automated: its smoothed ramps),
    # Recover sides with every band narrowed, the crossovers moved; Delta switched on by automation.
    fx("imager-wide", "Neo Imager", "Mastering — Air and Width", [],
       auto=[("Width 4", [(0, "150%"), (1.5, "40%"), (2.5, "200%")])]),
    fx("imager-recover", "Neo Imager", "Mixing — Narrow a Wide Mix, Keep the Level", [],
       auto=[("Crossover 2", [(0, "2000 Hz"), (2, "500 Hz")])]),
    fx("imager-stereoize", "Neo Imager", "Mono-Safe Widening (Stereoize Only)", [], auto=[("Delta", [(2, "On")])]),
    # Neo Exciter (§5.8, M5): the five curves (Tape, Tube, Transistor, Diode, Warm), mid/side, auto gain, an Amount automated.
    fx("exciter-tape", "Neo Exciter", "Mastering — Air", [],
       auto=[("Amount 4", [(0, "30%"), (1.5, "80%"), (2.5, "10%")])]),
    fx("exciter-warm-ms", "Neo Exciter", "Mastering — Subtle Warmth", [("Stereo", "M/S"), ("Auto gain", "On")]),
    fx("exciter-diode-delta", "Neo Exciter", "Drums — Crack and Sizzle", [("Mode", "Diode")], auto=[("Delta", [(2, "On")])]),
    # Neo Dither (§5.9, M5): TPDF at 16 bits, psychoacoustic shaping, 24 bits with the shaping switched by automation.
    fx("dither-16", "Neo Dither", "16 bit — TPDF (Standard)", []),
    fx("dither-16-shaped", "Neo Dither", "16 bit — TPDF, Psychoacoustic Shaping", [], auto=[("Bits", [(0, "16"), (2, "20")])]),
    # Neo Tape, Neo De-esser, Neo Gate (docs/specs/mastering-tools.md §5.11 - §5.13, M6a).
    fx("tape-glue", "Neo Tape", "Mastering — Subtle Glue (15 ips)"),
    fx("tape-cassette-wow", "Neo Tape", "Lo-Fi — Worn Cassette Feel"),
    fx("tape-live-delta", "Neo Tape", "Drums — Tape Crunch", [("Quality", "Live")], auto=[("Delta", [(2, "On")])]),
    fx("deesser-vocal", "Neo De-esser", "Vocal — Gentle De-ess"),
    fx("deesser-linear", "Neo De-esser", "Vocal — Transparent (Linear Phase Split)"),
    fx("deesser-bell-abs", "Neo De-esser", "Mastering — Tame a Bright Master", [("Threshold", "-30 dB")]),
    fx("gate-snare", "Neo Gate", "Drums — Snare, Tight"),
    fx("gate-expander", "Neo Gate", "Drums — Toms, Gentle Expander", [("Detection", "RMS")]),
    fx("gate-pedal-same", "Neo Gate", "Pedal Gate — Same Settings"),
    # Neo Strip (docs/specs/mastering-tools.md §5.10, M5b): the five stages together, the gate, a stage switched on and off in a pattern, Delta.
    fx("strip-vocal", "Neo Strip", "Vocal — Up Front"),
    fx("strip-spoken-gate", "Neo Strip", "Vocal — Spoken Word (Gate and Tidy)", [("Gate threshold", "-30 dB")]),
    fx("strip-drums", "Neo Strip", "Drums — Punch Bus", auto=[("Drive on", [(1, "Off"), (2, "On")]), ("Delta", [(3, "On")])]),
    fx("strip-master", "Neo Strip", "Master — Gentle Glue and Ceiling"),
    # Neo Verb: the four algorithms, modulation, shimmer, a 20-second decay, and freeze switched on
    # mid-render by automation (the tail held with no loss).
    fx("verb-hall", "Neo Verb", "Hall — Concert", tail=2.5),
    fx("verb-room", "Neo Verb", "Room — Live Drums", tail=1.5),
    fx("verb-chamber", "Neo Verb", "Chamber — Echo Chamber", tail=2.0),
    fx("verb-plate", "Neo Verb", "Plate — Snare", tail=1.5),
    fx("verb-shimmer", "Neo Verb", "Ambient — Shimmer", tail=2.5),
    fx("verb-infinite", "Neo Verb", "Ambient — Infinite Pad", tail=2.5),
    fx("verb-freeze", "Neo Verb", "Hall — Cathedral", [("Mix", "60%")], tail=1.5, auto=[("Freeze", [(0, "off"), (1.5, "on")])]),
    # Neo Ensemble: four chorus voices, a triangle LFO, the flanger at ±99 % feedback (its saturator
    # working) and swept fast, the phaser's twelve stages at 99 %, tempo sync, and a mode switch
    # mid-render by automation (the fade, the line kept).
    fx("ensemble-strings", "Neo Ensemble", "Ensemble — Strings"),
    fx("ensemble-poly-synth", "Neo Ensemble", "Chorus — Poly Synth I"),
    fx("ensemble-flanger-full", "Neo Ensemble", "Flanger — Metallic", [("Feedback", "99%"), ("Depth", "100%"), ("Rate", "8 Hz")]),
    fx("ensemble-flanger-negative", "Neo Ensemble", "Flanger — Tape Style (Negative)", [("Feedback", "-99%"), ("Tone", "2 kHz")]),
    fx("ensemble-phaser-12", "Neo Ensemble", "Phaser — 8-Stage Deep", [("Stages", 12), ("Feedback", "99%"), ("Rate", "3 Hz")]),
    fx("ensemble-synced-switch", "Neo Ensemble", "Phaser — Slow Sweep (4 Bars)", [("Sync", "1/4")],
       auto=[("Mode", [(0, "Phaser"), (2, "Flanger")])]),
    # Neo Delay: synced stereo, ping-pong and cross lines with both loop filters, the saturator driven at
    # 95 % feedback, the wow and ducking, a comb at −99 %, and the time automated (a glide, then a
    # crossfade, then a note value).
    fx("delay-dotted-throw", "Neo Delay", "Mix — Dotted Eighth Throw", tail=1.5),
    fx("delay-pingpong-send", "Neo Delay", "Send — Ping-Pong Eighths", tail=1.5),
    fx("delay-dub-driven", "Neo Delay", "Dub — Tape Echo (Saturated)", [("Feedback", "95%"), ("Drive", "18 dB")], tail=2.0),
    fx("delay-cross-wow-duck", "Neo Delay", "Send — Long Ambient Wash", [("Mod depth", "5 ms"), ("Mod rate", "5 Hz"), ("Duck", "18 dB"), ("Duck threshold", "-30 dB")], tail=2.0),
    fx("delay-comb-negative", "Neo Delay", "Texture — Comb Resonator", [("Feedback", "-99%")]),
    fx("delay-time-moves", "Neo Delay", "Mix — Quarter Note Echo", [("Sync", "Free"), ("Time", "100 ms"), ("Feedback", "60%")], tail=1.5,
       auto=[("Time", [(0, "100 ms"), (1, "130 ms"), (2, "450 ms")]), ("Sync", [(3, "1/8")])]),
    # Neo Multiband: the linear-phase crossovers (the partitioned FFT convolution, its FIR design), each band's dynamics
    # (compression, upward expansion, knee, range), mid/side unlinked, three bands, minimum phase, and the crossovers
    # automated (redesigns crossfading), the band count and the phase switched.
    fx("multiband-gentle-glue", "Neo Multiband", "Mastering — Gentle Glue (4 Bands)"),
    fx("multiband-squash-ms", "Neo Multiband", "Creative — Heavy Squash (Parallel 50 %)", [("Stereo", "M/S"), ("Link", "0%")]),
    fx("multiband-expand-3", "Neo Multiband", "Mastering — Transient Restore (Upward Expansion)"),
    fx("multiband-minimum", "Neo Multiband", "Mastering — Zero-Latency Glue (Minimum Phase)", [("B4 ratio", "0.6:1"), ("B4 range", "6 dB")]),
    fx("multiband-moves", "Neo Multiband", "Bus — Drum Bus Punch (3 Bands)",
       auto=[("Crossover 1", [(0, "120 Hz"), (1, "300 Hz"), (2, "80 Hz")]), ("Bands", [(2, "4 bands")]), ("Phase", [(3, "Minimum")])]),
    # Neo Multiband M6b (docs/specs/mastering-tools.md §5.14): a lookahead (the bands and the dry through delay rings, the latency
    # reported), the Dynamic phase, the RMS detector with upward compression, an external band with nothing on input 2 (it hears
    # silence) with a lookahead in minimum phase, and the lookahead and the phase switched while playing (the fades).
    fx("multiband-lookahead", "Neo Multiband", "Mastering — Gentle Glue (4 Bands)", [("Lookahead", "3 ms")]),
    fx("multiband-dynamic-phase", "Neo Multiband", "Mastering — Dynamic-Phase Glue (Zero Latency)", [("B2 ratio", "4:1"), ("B2 threshold", "-34 dB")]),
    fx("multiband-upward-rms", "Neo Multiband", "Mastering — Quiet Detail Lift (Upward)", [("Detector", "RMS"), ("B1 ratio", "1.5:1")]),
    fx("multiband-key-missing", "Neo Multiband", "Bus — Kick Duck on the Lows (Sidechain)", [("B2 upward ratio", "2:1"), ("B2 threshold", "-40 dB")]),
    fx("multiband-lookahead-moves", "Neo Multiband", "Mastering — Gentle Glue (4 Bands)",
       auto=[("Lookahead", [(1, "4 ms"), (2, "0 ms")]), ("Phase", [(3, "Dynamic")])]),
    # Neo Sampler: the built-in kit (choke groups, pans, tunings), the song's waves imported (a zone loop with its
    # crossfade, layers by root and round robin, reversed and ping-pong), velocity layers crossfading, the 32-tap
    # reader pitched far up (octave levels) and down, the ladder and the SVF with their envelope.
    gen("sampler-kit-electro", "Neo Sampler", "Kit — Electro (GM Layout)", [],
        [n(p, i * 0.25, 0.2, 60 + (i * 13) % 67) for i, p in enumerate(["C2", "F#2", "D2", "F#2", "C2", "A#2", "D2", "F#2",
                                                                          "C2", "F#2", "D#2", "A#2", "C2", "C#3", "A2", "D#3"])]),
    gen("sampler-kit-808-short", "Neo Sampler", "Kit — 808 Style (Long Kick, Cowbell)", [("Amp decay", "300 ms"), ("Amp sustain", "0%")],
        [n(p, i * 0.5, 0.25, 110) for i, p in enumerate(["C2", "D2", "G#2", "F#2", "C2", "A#2", "C#3", "D#3"])]),
    gen("sampler-song-waves", "Neo Sampler", None,
        [("Z1 key high", "B3"), ("Z1 loop", "Forward"), ("Z1 loop start", "20%"), ("Z1 loop end", "63.3%"), ("Z1 crossfade", "30 ms"),
         ("Z2 wave", 2), ("Z2 key low", "C4"), ("Z2 loop", "Ping-pong"), ("Z2 loop start", "35%"), ("Z2 loop end", "80%"),
         ("Z3 wave", 3), ("Z3 key low", "C6"), ("Z3 reverse", "on"), ("Z3 trigger", "One-shot"), ("Z3 choke", 1),
         ("Filter", "LP 24"), ("Cutoff", "1.5 kHz"), ("Resonance", "40%"), ("Filter env", "40%"), ("Flt decay", "400 ms"),
         ("Amp release", "300 ms")],
        CHORDS + [n("C6", 3.5, 0.25, 90), n("C#6", 3.75, 0.25, 70)], tail=1.0,
        waves=[(1, "tri-a3", "A3", False), (2, "tri-e4", "E4", False), (2, "tri-a3", "A5", True), (3, "hit", "C4", False), (3, "hit", "C4", True)]),
    gen("sampler-epiano-velocity", "Neo Sampler", "Keys — Electric Piano (Two Velocity Layers)", [("Vel crossfade", 40), ("Bend range", 3)],
        [n(p["pitch"], p["start"], p["length"], v) for p, v in zip(KEYS, (30, 60, 90, 120, 85, 95, 100, 127))]),
    gen("sampler-strings-far", "Neo Sampler", "Pad — Strings (Looped, Slow Attack)", [("Filter", "BP"), ("Cutoff", "2 kHz"), ("Resonance", "50%")],
        [n("C1", 0, 3.5, 100), n("C7", 0, 3.5, 100), n("G8", 1, 2.5, 100), n("A2", 2, 1.5, 60)], tail=1.5),
    gen("sampler-saw-lead", "Neo Sampler", "Lead — Saw (Filter Envelope)", [], LEAD),
    # Neo Scope: the audio passes through untouched while it looks: synced to the song's beats after Neo Delay, and free
    # with the trigger and holdoff, its display, time base and freeze automated (sweeps restarting mid-render).
    fx("scope-synced-after-delay", "Neo Delay", "Mix — Dotted Eighth Throw", tail=1.5, extra=[("Neo Scope", "Synced — One Beat", [])]),
    fx("scope-free-triggered", "Neo Scope", "Free — Low End (Triggered, Holdoff)", [("Holdoff", "20 ms")],
       auto=[("Display", [(0, "Mid"), (1, "L & R"), (2, "Side")]), ("Sync", [(1.5, "1/8"), (2.5, "Free")]), ("Freeze", [(3, "on")])]),
    # Neo Drums: every voice from its General MIDI key at varied velocities (the closed hat choking the open one, the
    # crash and ride ringing out), the hit columns and Accent written as played parameters, rolls retriggering each
    # voice, and the voices' settings automated (a hit takes its tune, decay and tone as it starts; levels glide).
    gen("drums-909-groove", "Neo Drums", "909 — Classic", [],
        [n(p, i * 0.25, 0.2, 50 + (i * 23) % 78) for i, p in enumerate(["C2", "F#2", "D2", "F#2", "C2", "A#2", "D#2", "F#2",
                                                                          "C2", "C#2", "D2", "A#2", "G2", "B2", "D3", "G#3"])]
        + [n("C#3", 0, 0.5, 110), n("D#3", 2, 0.5, 90), n("F#2", 1.375, 0.1, 70)], tail=2.0),
    gen("drums-808-columns", "Neo Drums", "808 — Classic", [("Volume", "-6 dB"), ("Kick drive", "12 dB"), ("Kick pan", -30), ("Closed hat pan", 40)],
        [n("C2", 3, 0.25, 127)],
        auto=[("BD", 0, [(0, 127), (1, 100), (2, 127), (2.75, 60)]), ("SD", 0, [(1, 110), (3, 120)]),
              ("CH", 1, [(b * 0.5, 60 + (b * 29) % 67) for b in range(8)]), ("OH", 1, [(1.75, 90)]),
              ("CB", 2, [(0.75, 100), (2.25, 80)]), ("LT", 2, [(3.25, 120)]), ("MT", 2, [(3.5, 110)]), ("HT", 2, [(3.75, 100)]),
              ("CP", 3, [(1, 90), (3, 127)]), ("RS", 3, [(0.5, 100)]), ("Accent", 3, [(1, 1), (3, 1)])]),
    gen("drums-techno-rolls", "Neo Drums", "909 — Techno", [("Accent gain", "12 dB")],
        [n("C2", i * 0.125, 0.1, 40 + i * 5) for i in range(16)] + [n("D2", 2 + i * 0.0625, 0.05, 60 + i * 4) for i in range(16)]
        + [n("F#2", i * 0.25, 0.1, 100) for i in range(16)] + [n("A#2", 0.5, 0.25, 110), n("C#3", 3, 0.5, 127)],
        auto=[("Kick tune", None, [(0, "45 Hz"), (1, "60 Hz"), (1.5, "38 Hz")]), ("Kick decay", None, [(0, "0.3 s"), (1, "1.5 s")]),
              ("Snare level", None, [(2, "-12 dB"), (2.5, "0 dB"), (3, "-6 dB")]), ("Closed hat tone", None, [(0, "4 kHz"), (2, "14 kHz")])],
        tail=1.5),
    # Neo Filter: the ladder swept by an LFO locked to the song, the band-pass auto-wah on the envelope, the SVF at 24 dB/oct
    # driven (the saturator's anti-aliasing at four times the rate) with a sample-and-hold LFO and stereo spread, the ladder
    # and the SVF self-oscillating (the limiter), and the type, slope, cutoff and resonance automated (the fade, the glides).
    fx("filter-ladder-sweep", "Neo Filter", "Sweep — Ladder Low-Pass (1 Bar)", [("LFO sync", "1/4")]),
    fx("filter-autowah-bp", "Neo Filter", "Auto-Wah — Funky (Band-Pass)", [("Env sensitivity", "18 dB")]),
    fx("filter-svf24-driven", "Neo Filter", "Sweep — High-Pass Build (8 Bars)",
       [("Drive", "18 dB"), ("LFO sync", "1/8"), ("LFO shape", "S&H"), ("LFO spread", 90), ("Resonance", "4")]),
    fx("filter-self-oscillating", "Neo Filter", "Resonant — Ladder Low-Pass", [("Resonance", "100"), ("Cutoff", "880 Hz")],
       auto=[("Type", [(2, "SVF Peak")])], tail=0.5),
    fx("filter-moves", "Neo Filter", "Auto-Wah — Bass (Ladder 12)", [("LFO retrig", "Env"), ("LFO depth", "1.5 oct"), ("LFO sync", "1/16")],
       auto=[("Cutoff", [(0, "300 Hz"), (1, "2 kHz"), (2, "150 Hz")]), ("Type", [(1.5, "SVF HP"), (3, "SVF Notch")]), ("Slope", [(2.5, "24 dB/oct")]),
             ("Resonance", [(0.5, "12"), (3.5, "0.71")])]),
    # Long: what drifts with time (oscillator and LFO phases, modulated delays) shows here. 16 s each.
    gen("long-poly", "Neo Poly", "Pad — Evolving PWM", [], LONG, beats=32),
    gen("long-wave", "Neo Wave", "Pad — Organ Drift", [], LONG, beats=32),
    gen("long-dye", "Dye Essence", "Pad — Glass Choir", [], LONG, beats=32),
    gen("long-fm6", "Neo FM6", "Pad — FM Strings", [], LONG, beats=32),
    fx("long-verb", "Neo Verb", "Ambient — Long Dark Hall", tail=3.0, beats=32,
       source=("Neo FM6", "Keys — Tine Piano", [], [n(p, b, 0.5, 100) for b in range(0, 32, 2) for p in ("C4", "E4", "G4")])),
    fx("long-ensemble", "Neo Ensemble", "Flanger — Slow Jet", [("Rate", "0.3 Hz")], tail=1.0, beats=32,
       source=("Neo FM6", "Keys — Tine Piano", [], [n(p, b, 0.5, 100) for b in range(0, 32, 2) for p in ("C4", "E4", "G4")])),
    fx("long-delay", "Neo Delay", "Tape — Analog Wobble (Dark)", [("Feedback", "85%")], tail=2.0, beats=32,
       source=("Neo FM6", "Keys — Tine Piano", [], [n(p, b, 0.5, 100) for b in range(0, 32, 4) for p in ("C4", "E4", "G4")])),
    gen("long-sampler", "Neo Sampler", "Pad — Vox Aah (Looped)", [("Z2 wave", 221), ("Z2 tune", 12)], LONG, beats=32),
    fx("long-filter", "Neo Filter", "LFO — Stereo Sweep (1/2, 90°)", [("LFO retrig", "Free"), ("LFO sync", "Free"), ("LFO rate", "0.23 Hz"), ("LFO shape", "Smooth random")],
       tail=1.0, beats=32, source=("Neo FM6", "Keys — Tine Piano", [], [n(p, b, 0.5, 100) for b in range(0, 32, 2) for p in ("C4", "E4", "G4")])),
]


# ---- MCP client -----------------------------------------------------------------------------------

class Client:
    """One request at a time over stdio, as headless-smoke.py does; binary pipes so Windows reads the same."""

    def __init__(self, exe, work, label):
        os.makedirs(work, exist_ok=True)
        args = [exe, "--agent"]
        if os.path.basename(exe).lower() not in ("neobuzzlive.exe", "rebuzz.exe"):   # the Windows app takes no --settings
            args += ["--settings", os.path.abspath(os.path.join(work, "settings"))]   # a clean one per case
        self.stderr_path = os.path.join(work, label + ".stderr.txt")
        self.stderr = open(self.stderr_path, "wb")
        self.proc = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.stderr)
        self.lines = queue.Queue()
        threading.Thread(target=self._read, daemon=True).start()
        self.next_id = 0

    def _read(self):
        for line in self.proc.stdout:
            self.lines.put(line)
        self.lines.put(None)

    def stderr_tail(self):
        self.stderr.flush()
        try:
            with open(self.stderr_path, "rb") as f:
                return f.read()[-3000:].decode("utf-8", "replace")
        except OSError:
            return ""

    def request(self, method, params=None, timeout=180):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params or {}}
        self.proc.stdin.write((json.dumps(msg) + "\n").encode("utf-8"))
        self.proc.stdin.flush()
        deadline = time.time() + timeout
        while True:
            try:
                line = self.lines.get(timeout=max(0.1, deadline - time.time()))
            except queue.Empty:
                raise RuntimeError(f"{method}: no answer in {timeout} s; stderr:\n{self.stderr_tail()}")
            if line is None:
                raise RuntimeError(f"{method}: the server closed stdout; stderr:\n{self.stderr_tail()}")
            try:
                r = json.loads(line)
            except ValueError:
                continue
            if r.get("id") == self.next_id:
                return r

    def initialize(self):
        r = self.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "null-test", "version": "1"}})
        if "result" not in r:
            raise RuntimeError(f"initialize: {r}")
        self.proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.proc.stdin.flush()

    def call(self, tool, **args):
        r = self.request("tools/call", {"name": tool, "arguments": args})
        res = r.get("result") or {}
        text = (res.get("content") or [{}])[0].get("text", "") if res else json.dumps(r.get("error"))
        if "error" in r or res.get("isError", True):
            raise RuntimeError(f"{tool}({json.dumps(args, ensure_ascii=False)[:300]}): {text[:600]}")
        return res.get("structuredContent") or {}

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(60)
        except Exception:
            self.proc.kill()
        self.stderr.close()


# ---- rendering ------------------------------------------------------------------------------------

def set_params(c, machine, params):
    if params:
        c.call("param_set", machine=machine, changes=[{"param": p, "value": v} for p, v in params])


def render_case(exe, case, out_dir, work):
    c = Client(exe, os.path.join(work, case["name"]), case["name"])
    try:
        c.initialize()
        c.call("song_set", tempo_bpm=TEMPO, song_end_beats=case["beats"])
        for slot, key, root, layer in case.get("waves", []):
            path = os.path.abspath(os.path.join(work, case["name"], key + ".wav"))
            write_wave(path, WAVES[key]())
            c.call("wave_import", path=path, slot=slot, name=key, root_note=root, **({"layer": True} if layer else {}))
        gear, preset, params, notes = case["gen"]
        c.call("machine_add", gear=gear, name="Src")
        if preset:
            c.call("preset_load", machine="Src", preset=preset)
        set_params(c, "Src", params)
        tracks = [t for _, t, _ in case.get("gen_auto", []) if t is not None]
        if tracks:
            c.call("machine_set", machine="Src", tracks=max(tracks) + 1)
        c.call("pattern_create", machine="Src", name="A", length_beats=case["beats"])
        if notes:
            c.call("notes_write", machine="Src", pattern="A", notes=notes)
        for param, track, points in case.get("gen_auto", []):
            c.call("automation_write", machine="Src", pattern="A", param=param, shape="step",
                   points=[{"at": at, "value": v} for at, v in points], **({"track": track} if track is not None else {}))
        c.call("arrangement_place", machine="Src", pattern="A", at=[0])
        last = "Src"
        for i, (fgear, fpreset, fparams) in enumerate(case["fx"]):
            name = f"Fx{i + 1}"
            c.call("machine_add", gear=fgear, name=name, insert_after=last)
            if fpreset:
                c.call("preset_load", machine=name, preset=fpreset)
            set_params(c, name, fparams)
            if i == 0 and case.get("auto"):
                c.call("pattern_create", machine=name, name="A", length_beats=case["beats"])
                for param, points in case["auto"]:
                    c.call("automation_write", machine=name, pattern="A", param=param, shape="step",
                           points=[{"at": at, "value": v} for at, v in points])
                c.call("arrangement_place", machine=name, pattern="A", at=[0])
            last = name
        r = c.call("render", **{"from": 0, "to": case["beats"], "tail_seconds": case["tail"], "name": case["name"]})
        os.makedirs(out_dir, exist_ok=True)
        shutil.copyfile(r["file"], os.path.join(out_dir, case["name"] + ".wav"))
        return r
    finally:
        c.close()


def render_all(exe, out_dir, work, only):
    failed = []
    started = time.time()
    for case in CASES:
        if only and case["name"] not in only:
            continue
        t = time.time()
        try:
            r = render_case(exe, case, out_dir, work)
            loud = (r.get("measure") or {}).get("loudness", {}).get("integrated_lufs")
            print(f"rendered {case['name']:<26} {r.get('seconds')} s at {r.get('sample_rate')} Hz, {loud} LUFS ({time.time() - t:.1f} s)", flush=True)
            if (r.get("measure") or {}).get("silent"):
                failed.append(case["name"])
                print(f"FAIL {case['name']}: the render is silent", flush=True)
        except Exception as e:
            failed.append(case["name"])
            print(f"FAIL {case['name']}: {e}", flush=True)
    for a, b in SAME:
        pa, pb = os.path.join(out_dir, a + ".wav"), os.path.join(out_dir, b + ".wav")
        if a in failed or b in failed or not (os.path.exists(pa) and os.path.exists(pb)):
            continue
        with open(pa, "rb") as fa, open(pb, "rb") as fb:
            same = fa.read() == fb.read()
        print(f"{'same' if same else 'FAIL'} {a} and {b}: {'the same bits' if same else 'they differ'}", flush=True)
        if not same:
            failed.append(a)
    done = sum(1 for c in CASES if not only or c["name"] in only) - len(set(failed))
    print(f"{done} rendered in {time.time() - started:.0f} s on {platform_label()}", flush=True)
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::notice title=Null test renders ({platform_label()})::{done} cases rendered in {time.time() - started:.0f} s"
              + (f"; failed: {', '.join(failed)}" if failed else ""), flush=True)
    return failed


# ---- comparing ------------------------------------------------------------------------------------

def read_wav(path):
    """32-bit float WAV (what render writes): (rate, channels, samples interleaved)."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise ValueError(f"{path}: not a WAV file")
    pos, fmt, samples = 12, None, None
    while pos + 8 <= len(data):
        cid, size = data[pos:pos + 4], struct.unpack_from("<I", data, pos + 4)[0]
        body = data[pos + 8:pos + 8 + size]
        if cid == b"fmt ":
            fmt = struct.unpack_from("<HHIIHH", body)
        elif cid == b"data":
            samples = array.array("f")
            samples.frombytes(body[:len(body) - len(body) % 4])
            if sys.byteorder != "little":
                samples.byteswap()
        pos += 8 + size + (size & 1)
    if fmt is None or samples is None:
        raise ValueError(f"{path}: no fmt or data chunk")
    tag, channels, rate, _, _, bits = fmt
    if tag not in (3, 0xFFFE) or bits != 32:
        raise ValueError(f"{path}: format {tag}, {bits} bits (expected 32-bit float)")
    return rate, channels, samples


def db(x):
    return 20 * math.log10(x) if x > 0 else float("-inf")


def compare_one(ref, got):
    """(identical, max difference dBFS, text)."""
    r_rate, r_ch, a = read_wav(ref)
    g_rate, g_ch, b = read_wav(got)
    if (r_rate, r_ch) != (g_rate, g_ch):
        return False, None, f"format differs: reference {r_rate} Hz × {r_ch}, this {g_rate} Hz × {g_ch}"
    if len(a) != len(b):
        return False, None, f"length differs: reference {len(a) // r_ch} frames, this {len(b) // g_ch}"
    peak = max((abs(x) for x in a), default=0.0)
    # The bits, not the values: a NaN or a -0.0 counts.
    if a.tobytes() == b.tobytes():
        return True, float("-inf"), f"identical ({len(a) // r_ch} frames, peak {db(peak):.1f} dBFS)"
    ia, ib = array.array("I", a.tobytes()), array.array("I", b.tobytes())
    worst, at, first, count = 0.0, 0, None, 0
    for i in range(len(ia)):
        if ia[i] != ib[i]:
            count += 1
            if first is None:
                first = at = i
            d = abs(a[i] - b[i])
            if d > worst or math.isnan(d):
                worst, at = d, i

    def where(i):
        frame, ch = divmod(i, r_ch)
        return f"frame {frame} ({frame / r_rate:.3f} s, {'LR'[ch] if r_ch == 2 else ch})"

    level = float("inf") if math.isnan(worst) else db(worst)
    return False, level, (f"differs: first at {where(first)}, reference {a[first]:.9g}, this {b[first]:.9g}; "
                          f"{count} of {len(a)} samples differ, by up to {level:.1f} dBFS (at {where(at)}); "
                          f"peak {db(peak):.1f} dBFS")


def compare_dirs(ref_dir, got_dir, only):
    rows, failed = [], []
    names = [c["name"] for c in CASES if not only or c["name"] in only]
    extra = sorted(f[:-4] for f in os.listdir(ref_dir) if f.endswith(".wav") and f[:-4] not in names and not only)
    for name in names + extra:
        ref, got = os.path.join(ref_dir, name + ".wav"), os.path.join(got_dir, name + ".wav")
        if name in EXCLUDED:
            rows.append((name, "excluded", None, EXCLUDED[name]))
            continue
        if not os.path.exists(ref) or not os.path.exists(got):
            missing = "the reference" if not os.path.exists(ref) else "this platform's render"
            if name in extra:
                missing = "the case (not in this script)"
            rows.append((name, "FAIL", None, f"missing: {missing}"))
            failed.append(name)
            continue
        try:
            ok, level, text = compare_one(ref, got)
        except ValueError as e:
            ok, level, text = False, None, str(e)
        rows.append((name, "ok" if ok else "FAIL", level, text))
        if not ok:
            failed.append(name)
    return rows, failed


def report(rows, failed, title):
    lines = []
    for name, status, level, text in rows:
        lines.append(f"{status:<8} {name:<26} {text}")
    print("\n".join(lines), flush=True)
    worst = max((lv for _, _, lv, _ in rows if lv is not None), default=float("-inf"))
    summary = (f"{len(rows) - len(failed)} of {len(rows)} cases identical"
               + ("" if worst == float("-inf") else f"; largest difference {worst:.1f} dBFS"))
    print(summary, flush=True)
    if os.environ.get("GITHUB_ACTIONS"):
        # One notice with a line per case (readable through the API), and the step summary.
        short = [f"{name}: " + ("identical" if lv == float("-inf") else text)
                 + ("" if status == "ok" else f" [{status}]") for name, status, lv, text in rows]
        body = summary + "\n" + "\n".join(short)
        kind = "error" if failed else "notice"
        title = title.replace("%", "%25").replace(":", "%3A").replace(",", "%2C")
        print(f"::{kind} title={title}::" + body.replace("%", "%25").replace("\r", "").replace("\n", "%0A"), flush=True)
        step = os.environ.get("GITHUB_STEP_SUMMARY")
        if step:
            with open(step, "a", encoding="utf-8") as f:
                f.write(f"### {title}\n\n{summary}\n\n| Case | Result | Samples |\n|---|---|---|\n")
                for name, status, lv, text in rows:
                    f.write(f"| {name} | {status} | {text} |\n")
                f.write("\n")


def platform_label():
    system = {"Darwin": "macOS", "Windows": "Windows", "Linux": "Linux"}.get(platform.system(), platform.system())
    return f"{system} {platform.machine().lower()}"


def main(argv):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    only = None
    if "--only" in argv:
        i = argv.index("--only")
        only = set(argv[i + 1].split(","))
        argv = argv[:i] + argv[i + 2:]
    if len(argv) >= 3 and argv[0] == "render":
        exe, out_dir = argv[1], argv[2]
        failed = render_all(exe, out_dir, os.path.join(out_dir, "work"), only)
        return 1 if failed else 0
    if len(argv) >= 4 and argv[0] == "compare":
        exe, ref_dir, work = argv[1], argv[2], argv[3]
        got_dir = os.path.join(work, "renders")
        render_failed = render_all(exe, got_dir, os.path.join(work, "work"), only)
        rows, failed = compare_dirs(ref_dir, got_dir, only)
        report(rows, failed, f"Null test, {platform_label()} vs Windows")
        return 1 if failed or render_failed else 0
    if len(argv) >= 3 and argv[0] == "diff":
        rows, failed = compare_dirs(argv[1], argv[2], only)
        report(rows, failed, "Null test")
        return 1 if failed else 0
    print("usage: null-test.py render <exe> <out-dir> | compare <exe> <ref-dir> <work-dir> | diff <ref-dir> <dir> [--only a,b]")
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
