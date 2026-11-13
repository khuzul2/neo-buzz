#!/usr/bin/env python3
# Fork (docs/specs/agent-knowledge.md, K2 batch b): what an agent is told about the Jeskola machines' parameters, and the
# curation of the manifests that were profiled drafts or generated from source.
#
#  - EXPLAIN: a How sentence (how the control changes the sound) and a Sound sentence (what you will hear) for every
#    parameter of the 25 Jeskola machines, global and track. They go into the manifest as "how" and "sound", which
#    machine_describe serves as it does for the Neo machines. Keys are the parameter's name as the machine declares it.
#  - CURATE: for the 15 profiled drafts and the 3 generated from source (NiNjA dElaY, Noise Generator, Trilok): a type, a
#    summary, plain names, roles, quirks and evidence, so they are curated like the seven that already were. The mappings stay
#    what the profiler measured from the machine's own texts (drafts) or what tools/native_manifests.py generates (source).
#
# Most of these machines are closed-source native DLLs: their meaning comes from their own parameter descriptions, value texts
# and measurements (tools/machine-profiler.py on Windows), written here in our own words. Where no measurement or source
# shows what a control does, the sentence says what the name and range give and the page marks it unverified.
#
#   python3 tools/jeskola_knowledge.py            check: the manifests carry this curation and Explain text
#   python3 tools/jeskola_knowledge.py --write    write it into the ones tools/native_manifests.py doesn't generate

import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BT = "ExternalMachines/source/buzztrax-buzzmachines/Jeskola/"
PROFILE = "profile (tools/machine-profiler.py, windows 2026-11-13)"
OWN = "its parameter descriptions and value texts (machine_describe, as profiled)"
MONO = ("Mono out, and about 6 dB louder than its input at unity settings: NeoBuzz Live feeds a mono native effect left + right "
        "summed (ReBuzz/NativeMachine/NativeMessage.cs).")
CLOSED = "Closed source: names come from its own descriptions and measurements, not from code."
PCT = {"a": 0.0, "b": 100.0}          # its texts round to a tenth of a percent: the formula is exact
BIP = {"a": -100.0, "b": 100.0}
FIXES = {"Jeskola Bass 3": [("(TB-303 flavoured)", "(acid flavoured)"), ("or a 303 saw)", "or an acid saw)"), ("the TB-303 sawtooth", "the acid sawtooth")]}


def mod(kind):
    return {
        "freq": ("Sets the speed of the sweep that moves the delayed copy, from 0 to 40.96 Hz in steps of 0.01 Hz.", "Low values give a slow, seasick swell; high values turn the movement into a fast warble or vibrato."),
        "depth": ("Sets how far the sweep moves the delay time, from 0 to 100 percent.", "At 0 the copy stands still and only thickens the sound; higher depth adds pitch wobble and a wider swirl."),
        "feedback": ("Feeds part of the delayed copy back in, from -100 to +100 percent (64 is none); negative values invert each pass.", "Positive feedback adds resonant ringing and a metallic edge; negative gives a hollow, different colour. Near the ends it rings hard."),
        "dry out": ("Sets the level of the untouched signal, from -100 to +100 percent; a negative value flips its polarity.", "Lower it to hear more of the effect on its own; the middle step removes the dry sound completely."),
        "wet out": ("Sets the level of the delayed copy, from -100 to +100 percent; a negative value flips its polarity.", "More wet means a stronger effect; flipping it against the dry sound changes the comb pattern from bright to hollow."),
    }


EXPLAIN = {
    "Jeskola Chorus": {
        **{k: v for k, v in mod(0).items()},
        "min. delay": ("Sets the shortest delay the sweep reaches, from 0 to 50 ms.", "Short values sit close to a flanger; 15 to 30 ms gives the classic wide chorus with a clearly separate second voice."),
    },
    "Jeskola Flanger": {
        **{k: v for k, v in mod(1).items()},
        "min. delay": ("Sets the shortest delay the sweep reaches, from 0 to 5 ms in steps of 0.1 ms.", "Very short delays give a tight jet-like comb; larger ones loosen it toward a chorus."),
    },
    "Jeskola Cross Delay": {
        "Dry thru": ("Switches the unprocessed input on or off in the output beside the echoes.", "On keeps the original sound with the echoes behind it; off leaves only the echoes."),
        "L. Length": ("Sets the left delay line's length, 1 to 32768 in the Length unit.", "Different left and right lengths make the echoes bounce between the speakers in an uneven pattern."),
        "R. Length": ("Sets the right delay line's length, 1 to 32768 in the Length unit.", "Equal lengths give a plain echo down the middle; unequal ones give the ping-pong."),
        "Length unit": ("Chooses what both lengths count: ticks, milliseconds, samples or 1/256 of a tick.", "Ticks keep the echoes in time with the tempo; milliseconds and samples give fixed times."),
        "Feedback": ("Sets how much of each echo is fed to the other line, from -100 to +100 percent (64 is none).", "Higher values give more repeats that cross from side to side; negative values flip each repeat. Near the ends the echoes hardly die away."),
        "Wet out": ("Sets this track's echo level, from 0 to 100 percent.", "Raises or lowers how loud the echoes are against the dry signal."),
    },
    "Jeskola Delay": {
        "Dry thru": ("Switches the unprocessed input on or off in the output beside the echoes.", "On keeps the original sound with the echoes behind it; off leaves only the echoes."),
        "Length": ("Sets this track's delay time, 1 to 32768 in the Length unit.", "Longer values space the echoes further apart; several tracks with different lengths make a multi-tap rhythm."),
        "Length unit": ("Chooses what the length counts: ticks, milliseconds, samples or 1/256 of a tick.", "Ticks keep the echoes in time with the tempo; milliseconds and samples give fixed times."),
        "Feedback": ("Sets how much of the output is fed back in, from -100 to +100 percent (64 is none); the safe range stops short of the ends.", "More feedback means more repeats; negative values flip each repeat; at the very ends the echoes never die away."),
        "Wet out": ("Sets this track's echo level, from 0 to 100 percent.", "Raises or lowers how loud the echoes are against the dry signal."),
    },
    "Jeskola Distortion": {
        "+threshold": ("Sets the positive level above which the signal is cut, in percent of full scale.", "Lower values cut more of every wave and make the fuzz harsher and denser."),
        "+clamp": ("Sets the level a cut positive peak is replaced with, in percent of full scale; the default is above full scale.", "Higher clamps are louder and more square; keep it near 100 percent to stay inside full scale."),
        "-threshold": ("Sets the negative level below which the signal is cut, in percent of full scale; it acts only when symmetric mode is off.", "A different value from the positive threshold makes the clipping lopsided and adds even harmonics."),
        "-clamp": ("Sets the level a cut negative peak is replaced with, in percent of full scale; it acts only when symmetric mode is off.", "Raising it makes the negative half louder; unequal clamps add a rough, uneven edge."),
        "Amount": ("Blends between the input (0 percent) and the clamped signal (100 percent) wherever a threshold is crossed.", "At 100 percent the peaks are fully replaced and the sound is square and harsh; lower values keep some of the original."),
    },
    "Jeskola EQ-3": {
        "Low Gain": ("Raises or lowers the band below the low crossover by up to 24 dB; the middle step is 0 dB.", "Boost for weight and boom, cut to thin the bass."),
        "Mid Gain": ("Raises or lowers the band between the two crossovers by up to 24 dB; the middle step is 0 dB.", "Boost to push the body or presence forward, cut to scoop it out."),
        "High Gain": ("Raises or lowers the band above the high crossover by up to 24 dB; the middle step is 0 dB.", "Boost for air and sizzle, cut to soften harsh top."),
        "Low Cutoff": ("Sets where the low band ends and the mid band starts, 10 Hz to 16 kHz.", "Lower it to confine the low gain to the deepest bass; raise it to include more body."),
        "High Cutoff": ("Sets where the mid band ends and the high band starts, 10 Hz to 16 kHz.", "Lower it to bring more of the upper mids into the high band; raise it to keep the high gain for the top only."),
        "Dry Thru": ("Switches between 0 and 1; the machine's own text does not say what it does and no measurement isolated it.", "Left at its default of 1; the effect of changing it is unverified."),
    },
    "Jeskola Filter": {
        "Type": ("Chooses low-pass, high-pass or band-pass.", "Low-pass darkens, high-pass thins, band-pass leaves a narrow band."),
        "Cutoff": ("Sets the filter's corner on a 0 to 128 scale; 128 is fully open for the low-pass.", "Lower values close a low-pass and darken the sound; the sweeps are what classic Buzz songs use it for."),
        "Resonance": ("Sets the emphasis at the corner on a 0 to 128 scale.", "Higher values add a whistling peak at the cutoff; near the top it rings loudly, so keep it below 118."),
        "Bandwidth": ("Sets a width on a 0 to 128 scale; no measurement showed a change with the others at their defaults.", "Its audible effect is unverified here; leave it at 0 unless a song uses it."),
    },
    "Jeskola Filter 2": {
        "Type": ("Chooses low-pass, high-pass or band-pass.", "Low-pass darkens, high-pass thins, band-pass leaves a narrow band."),
        "Cutoff": ("Sets the filter's corner on a 0 to 128 scale; 128 is fully open for the low-pass.", "Lower values close a low-pass and darken the sound; sweep it for classic filter movement."),
        "Resonance": ("Sets the emphasis at the corner on a 0 to 128 scale.", "Higher values add a whistling peak at the cutoff; near the top it rings very loudly, so keep it below 118."),
    },
    "Jeskola Gain": {
        "Gain": ("Sets the level from silence through 0 dB at 80 up to +12 dB at 127.", "Lower is quieter; it only changes loudness and does not colour the sound."),
        "Bypass": ("Switches the machine's processing off (yes) or on (no).", "When bypassed the signal passes unchanged."),
        "ATrackParam": ("A placeholder track parameter, 0 to 127; no measurement found any change from it.", "No audible effect."),
    },
    "Jeskola NiNjA dElaY": {
        "Dry thru": ("Switches the unprocessed input on or off in the output beside the echoes.", "On keeps the original sound with the echoes behind it; off leaves only the echoes."),
        "Length": ("Sets this track's delay time, 1 to 32768 in the Length unit.", "Longer values space the echoes further apart; several tracks make a multi-tap rhythm."),
        "Length unit": ("Chooses what the length counts: ticks, milliseconds, samples or 1/256 of a tick.", "Ticks keep the echoes in time with the tempo; milliseconds and samples give fixed times."),
        "Feedback": ("Sets how much of the echo is fed back through the filter, from -100 to +100 percent (64 is none).", "More feedback means more repeats, each filtered again; near the ends the echoes hardly die away."),
        "f. type": ("Chooses the loop filter: low-pass, high-pass, band-pass or band-reject.", "Low-pass makes each repeat darker, high-pass thinner; band-pass keeps a narrow stripe of the sound."),
        "f. cutoff": ("Sets the loop filter's frequency, 10 Hz to 20 kHz.", "A low cutoff on a low-pass makes repeats fade into dark mud; on a high-pass it strips the bass from them."),
        "f. bandwidth": ("Sets the width of the band-pass or band-reject filter, 1 to 10000 Hz.", "Narrow bands make each repeat more tonal; wide ones leave more of the sound. It does nothing for low-pass and high-pass."),
        "Wet out": ("Sets this track's echo level, from 0 to 100 percent.", "Raises or lowers how loud the echoes are against the dry signal."),
    },
    "Jeskola Raverb": {
        "Rev Time": ("Sets the length of the reverb tail on a 0 to 127 scale.", "Higher values give a longer tail; measured, it grows from about 0.4 s to about 4 s."),
        "HF Damp": ("Sets how strongly the highs of the tail are damped, 0 to 127; the measurement did not isolate its effect.", "Its audible effect is unverified here; by its name, more damping gives a darker, softer tail."),
        "Rev Out": ("Sets the reverb's output level, from -60 dB to 0 dB.", "Higher is a wetter, more distant sound."),
        "Dry Out": ("Sets the unprocessed signal's level, from -60 dB to 0 dB.", "Lower it for a more distant sound; the default passes the dry signal at full level."),
    },
    "Jeskola Reverb": {
        "Dry Out": ("Sets the unprocessed signal's level, from silence up to 0 dB.", "Lower it to push the sound back into the reverb; at silence only the reverb remains."),
        "Rev Out": ("Sets the reverb tail's level, from silence up to 0 dB.", "Raises or lowers how much of the tail is heard behind the dry sound."),
        "ER Out": ("Sets the early reflections' level, from silence up to 0 dB; it is off by default.", "Adds the first few close echoes that give a sense of a room around the sound."),
        "Rev Time": ("Sets the tail length from 0.1 to 6.4 s.", "Short values sound like a small room, long ones like a hall that keeps ringing."),
        "Pre-Delay": ("Sets the gap before the reverb starts, from 0 to 100 ms.", "A longer gap keeps the dry sound clear before the reverb blooms behind it."),
        "High Cutoff": ("Sets a low-pass on the reverb, from 100 Hz to 16.4 kHz.", "Lower values make the tail darker and softer; higher values keep it bright."),
    },
    "Jeskola Stereo Reverb": {
        "Dry Out": ("Sets the unprocessed signal's level, from silence up to 0 dB.", "Lower it to push the sound back into the reverb; at silence only the reverb remains."),
        "Rev Out": ("Sets the reverb tail's level, from silence up to 0 dB.", "Raises or lowers how much of the tail is heard behind the dry sound."),
        "ER Out": ("Sets the early reflections' level, from silence up to 0 dB; it is off by default.", "Adds the first few close echoes ahead of the tail; measured, they arrive about 20 ms in."),
        "Rev Time": ("Sets the tail length from 0.1 to 6.4 s.", "Short values sound like a small room, long ones like a hall that keeps ringing."),
        "Pre-Delay": ("Sets the gap before the reverb starts, from 0 to 100 ms; no measurement isolated its effect.", "A longer gap keeps the dry sound clear before the reverb blooms behind it."),
        "High Cutoff": ("Sets a low-pass on the reverb, from 100 Hz to 16.4 kHz.", "Lower values make the tail darker and softer; higher values keep it bright."),
    },
    "Jeskola Wave Shaper": {
        "gain": ("Sets the gain into the shaper, from -24 dB to +24 dB (the middle step is 0 dB).", "More gain pushes the signal harder into the shaping curve and makes it louder and more distorted."),
        "shape": ("Chooses one of four shapes, 1 to 4; no measurement or source shows what each one does.", "The four shapes sound different from one another; which is which is unverified here."),
    },
    "Jeskola Bass": {
        "Cutoff": ("Sets the filter's corner on a 0 to 128 scale; 128 is fully open.", "Lower values close the filter and make the bass duller and quieter."),
        "Resonance": ("Sets the emphasis at the cutoff on a 0 to 128 scale.", "Higher values add a squelchy peak; near the top it gets very loud, so check the level."),
        "Waveform": ("Chooses the oscillator: 1 sawtooth, 2 pulse, 3 triangle.", "Saw is bright and buzzy, pulse hollow, triangle soft and round."),
        "Volume": ("Sets a note's loudness on a 0 to 254 scale; 128 is 100 percent. It is a pattern column.", "Higher is louder; the measurement found no level change from it with the others at their defaults."),
        "Slide lenght": ("Sets how many ticks a slide to the next note takes, 1 to 255 (the machine's own spelling of length).", "Longer slides glide the pitch more slowly between notes."),
    },
    "Jeskola Bass 2": {
        "Cutoff": ("Sets the filter's corner, from 0 to 100 percent; fully open is the default.", "Lower values close the filter and make the note duller; measured, it also changes the level by about 50 dB."),
        "Resonance": ("Sets the emphasis at the cutoff, from 0 to 100 percent.", "Higher values add a peak around 8 kHz that stands out clearly from the rest of the sound."),
        "Envmod": ("Sets how far the envelope opens the filter on each note, from 0 to 100 percent.", "More makes each note start brighter and settle; measured, it also shortens the note's fall."),
        "Decay": ("Sets how fast the filter envelope falls, from 0 to 100 percent.", "Longer decays keep the note brighter for longer."),
        "Waveform": ("Chooses the oscillator: sine, sawtooth, pulse, triangle or noise.", "Sine is pure, saw buzzy, pulse hollow, triangle soft, noise unpitched."),
    },
    "Jeskola Bass 3": {
        "Cutoff": ("Sets the filter's corner, from 0 to 100 percent; fully open is the default.", "Lower values close the filter and make the note duller."),
        "Resonance": ("Sets the emphasis at the cutoff, from 0 to 100 percent.", "Higher values add a squelchy peak that gives the acid sound."),
        "Envmod": ("Sets how far the envelope opens the filter on each note, from 0 to 100 percent.", "More makes each note start brighter and settle."),
        "Decay": ("Sets how fast the filter envelope falls, from 0 to 100 percent.", "Longer decays keep the note brighter for longer."),
        "Waveform": ("Chooses the oscillator: sine, sawtooth, pulse, triangle, noise or the machine's special acid sawtooth.", "Sine is pure, saw buzzy, pulse hollow, triangle soft, noise unpitched; the last is the classic acid saw."),
    },
    "Jeskola Organ": {
        "Attack": ("Sets how long a note takes to rise, from about 1 ms to 9 s on an exponential scale.", "Short values start the note at once; long values fade it in."),
        "Release": ("Sets how long a note takes to fade after the key is let go, from about 1 ms to 9 s on an exponential scale.", "Short values stop the note quickly; long values leave a trailing tone."),
        '16"': ("Sets the level of the 16-foot drawbar, the octave below the note, from 0 to 100.", "Adds deep weight under the note."),
        '8"': ("Sets the level of the 8-foot drawbar, the note's own pitch, from 0 to 100.", "Adds the main body of the tone."),
        '5 1/3"': ("Sets the level of the 5 1/3-foot drawbar, a fifth above the note, from 0 to 100.", "Adds a hollow, fifth-heavy colour."),
        '4"': ("Sets the level of the 4-foot drawbar, the octave above the note, from 0 to 100.", "Adds brightness and presence."),
        '2 2/3"': ("Sets the level of the 2 2/3-foot drawbar, an octave and a fifth above the note, from 0 to 100.", "Adds a reedy, nasal edge."),
        '2"': ("Sets the level of the 2-foot drawbar, two octaves above the note, from 0 to 100.", "Adds shimmer and bite at the top."),
    },
    "Jeskola Live": {
        "Bar": ("A number from 1 to 128, 16 by default; the machine's own text gives no description and nothing was measured.", "Unverified: the machine makes no sound itself."),
    },
    "Jeskola Loop Recorder": {
        "Dummy": ("A placeholder parameter from 0 to 127 that the machine's own name calls a dummy.", "No audible effect: the machine makes no sound."),
    },
    "Jeskola Note Matrix": {
        "Dummy": ("A placeholder parameter from 0 to 127 that the machine's own name calls a dummy.", "No audible effect: the machine makes no sound."),
    },
    "Jeskola Relativion": {
        "Root Note": ("Chooses the note, from C-0 up, that the machine treats as the centre of the notes it reads.", "Sets which played note counts as the unshifted one; the machine makes no sound itself."),
        "Lowest Note": ("Chooses the lowest note the machine reacts to.", "Notes below it are ignored; the machine makes no sound itself."),
        "Highest Note": ("Chooses the highest note the machine reacts to.", "Notes above it are ignored; the machine makes no sound itself."),
        "Ctrl. Root": ("Chooses the note, from C-0 up, that stands for the middle of the controlled parameter's movement.", "Sets the neutral point the controlled value moves around; the machine makes no sound itself."),
        "Ctrl. Range": ("Sets how far, in steps from 0 to 48, the controlled value moves from its middle.", "A larger range makes the controlled parameter swing further per note; the machine makes no sound itself."),
        "Volume": ("Chooses a note value, C-0 up, for the machine's volume column; its default is the lowest.", "Unverified: the machine makes no sound itself."),
    },
    "Jeskola Tracker": {
        "Subdiv": ("Sets how many parts each tick is divided into for its rows, 1 to 15, 6 by default.", "Higher values give finer timing between rows; the machine plays nothing from a note at its defaults."),
    },
    "Jeskola Noise Generator": {
        "Attack": ("Sets how long the noise takes to rise to its level, 1 to 65535 ms.", "Short values start the noise with a click; long values fade it in."),
        "Sustain": ("Sets how long the noise holds at its level, 1 to 65535 ms.", "Short values make a burst; long values make a held hiss."),
        "Release": ("Sets how long the noise takes to fade after the sustain, 1 to 65535 ms.", "Short values cut off sharply; long values leave a trailing hiss."),
        "Color": ("Sets the noise's colour from 0 (dark) to 4096 (white).", "Low values sound like a dull rumble; high values sound like a bright hiss."),
        "Volume": ("Sets the noise's level on a 0 to 254 scale; 128 is 100 percent, and at 0 a trigger makes no sound.", "Higher is louder; at 0 the track stays silent."),
    },
    "Jeskola Trilok": {
        "BD Trigger": ("Fires the bass drum when written; it is an event, so it is played with automation_write rather than held.", "Each trigger plays one short, low kick."),
        "BD Tone": ("Sets the drum's filter, 0 to 127; higher values open it and take the resonance away.", "Higher values give a brighter, clickier thump; lower ones a deeper, rounder boom."),
        "BD Decay": ("Sets how long the drum takes to fade, from about 17 ms at 0 to about 1.3 s at 127.", "Short values give a tight tick; long ones give a booming tail."),
        "BD Volume": ("Sets a level on a 0 to 254 scale, but the machine's source never reads it and a sweep heard no change.", "No audible effect; set the level on the machine's output instead."),
    },
}


def _src(path, what):
    return f"{path} — {what}"


CURATE = {
    "Jeskola Chorus": {
        "type": "modulation",
        "summary": "Classic Buzz chorus: one delayed copy swept by an LFO, with rate, depth, feedback, minimum delay and dry and wet levels from -100 to +100 percent. For thickening a source; Neo Ensemble is the modern chorus.",
        "params": {"freq": ("LFO rate", "mod.rate"), "depth": ("LFO depth", "mod.depth", PCT), "feedback": ("Feedback", "mod.feedback", BIP),
                   "min. delay": ("Minimum delay", "mod.delay"), "dry out": ("Dry level", "mix.dry", BIP), "wet out": ("Wet level", "mix.wet", BIP)},
        "quirks": [
            MONO + " Profile: +8.8 dB at the defaults.",
            "Hot at its defaults: +1.8 dBFS peak on the profile's test signal; feedback at the ends adds a lot more (+16 to +19 dBFS).",
            "Dry out, wet out and feedback run from -100 to +100 percent: the middle step (64) is 0, and a negative level inverts the signal.",
            "min. delay counts milliseconds (0 to 50); Jeskola Flanger's counts tenths of a millisecond.",
            CLOSED,
        ],
        "evidence": [OWN, "ReBuzz/NativeMachine/NativeMessage.cs — a mono native effect gets left + right summed",
                     PROFILE + " — chorus traits, +8.8 dB at the defaults, hot at the defaults and at feedback's ends"],
    },
    "Jeskola Flanger": {
        "type": "modulation",
        "summary": "Classic Buzz flanger: a very short delayed copy swept by an LFO, with rate, depth, feedback, minimum delay in tenths of a millisecond and dry and wet levels. For jet sweeps; Neo Ensemble has a flanger mode.",
        "params": {"freq": ("LFO rate", "mod.rate"), "depth": ("LFO depth", "mod.depth", PCT), "feedback": ("Feedback", "mod.feedback", BIP),
                   "min. delay": ("Minimum delay", "mod.delay"), "dry out": ("Dry level", "mix.dry", BIP), "wet out": ("Wet level", "mix.wet", BIP)},
        "quirks": [
            MONO + " Profile: +8.8 dB at the defaults.",
            "Feedback at +100 percent diverges (the engine's guard caught it): the safe range is -100 to +98.4 percent.",
            "Wet out defaults to -100 percent (raw 0), an inverted copy; set it to the middle or above for the usual sound.",
            "min. delay counts tenths of a millisecond (0 to 5 ms); Jeskola Chorus's counts milliseconds.",
            CLOSED,
        ],
        "evidence": [OWN, "ReBuzz/NativeMachine/NativeMessage.cs — a mono native effect gets left + right summed",
                     PROFILE + " — modulation traits, +8.8 dB at the defaults, feedback diverges at +100 percent"],
    },
    "Jeskola Filter 2": {
        "type": "filter",
        "summary": "A second Buzz resonant filter: low-pass, high-pass or band-pass with cutoff and resonance on 0 to 128 scales and no bandwidth. For sweeps in old Buzz songs; Neo Filter has real units.",
        "params": {"Type": ("Filter type", "filter.type"), "Cutoff": ("Cutoff", "filter.cutoff"), "Resonance": ("Resonance", "filter.resonance")},
        "quirks": [
            MONO + " Profile: +6.0 dB at the defaults.",
            "Type is a bare number (0 low-pass, 1 high-pass, 2 band-pass): its texts say 0, 1 and 2, and its description gives the names.",
            "Resonance near its top self-oscillates very loudly (+52 dBFS peak on noise in the profile): keep it below 118.",
            "Cutoff and Resonance are bare 0 to 128 scales (its description says 0 to 80, in hex).",
            CLOSED,
        ],
        "evidence": [OWN, "ReBuzz/NativeMachine/NativeMessage.cs — a mono native effect gets left + right summed",
                     PROFILE + " — +6.0 dB at the defaults; hot with Resonance at its top"],
    },
    "Jeskola Gain": {
        "type": "utility",
        "summary": "A plain gain stage with a bypass: level from silence through 0 dB (at 80) to +12 dB. For trims and level automation; Neo Utility adds pan, width and polarity.",
        "params": {"Gain": ("Gain", "level", {"description": "Level from silence (0) through 0 dB (80) to +12 dB (127)."}),
                   "Bypass": ("Bypass", "bypass", {"description": "Passes the signal unchanged when on (yes)."}),
                   "ATrackParam": ("Placeholder track parameter", "", {"description": "A placeholder track parameter, 0 to 127, with no measured effect."})},
        "quirks": [
            "Gain is a table, not a formula: 0 is silence, 80 is 0 dB and 127 is +12 dB, about 0.3 dB per step near unity.",
            "It has one track parameter, ATrackParam, that no measurement found any effect from.",
            "Hot at its top: +12 dB pushes a normal signal above full scale (+2.8 dBFS peak in the profile).",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — +0.0 dB at the defaults, silent at Gain 0, +12 dB at 127"],
    },
    "Jeskola Raverb": {
        "type": "reverb",
        "summary": "A small Buzz reverb with a reverb time, high-frequency damping and separate reverb and dry levels in decibels. For quick ambience; Neo Verb is the better reverb.",
        "params": {"Rev Time": ("Reverb time", "reverb.decay"), "HF Damp": ("High damping", "reverb.damping"),
                   "Rev Out": ("Reverb level", "mix.wet"), "Dry Out": ("Dry level", "mix.dry")},
        "quirks": [
            "Rev Time is a bare 0 to 127 scale: measured, the tail grows from about 0.4 s to about 4 s (T60), 100 by default.",
            "Rev Out and Dry Out run from -60 to 0 dB; the defaults are about -22.6 dB reverb and 0 dB dry.",
            "HF Damp's effect was not isolated by the measurement; its meaning is from its name and description.",
            "A long tail: 3.5 s at the defaults, so renders need a tail.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — tail 3.5 s and T60 about 8.5 s at the defaults, +6.1 dB, Rev Time 0.4 s to 4 s"],
    },
    "Jeskola Stereo Reverb": {
        "type": "reverb",
        "summary": "A Buzz stereo reverb: dry, reverb and early-reflection levels, reverb time up to 6.4 s, pre-delay and a high cut. For quick ambience; Neo Verb is the better reverb.",
        "params": {"Dry Out": ("Dry level", "mix.dry", {"like": ("Jeskola Reverb", "Dry Out")}), "Rev Out": ("Reverb level", "mix.wet", {"like": ("Jeskola Reverb", "Rev Out")}),
                   "ER Out": ("Early reflections level", "reverb.early", {"like": ("Jeskola Reverb", "ER Out")}),
                   "Rev Time": ("Reverb time", "reverb.decay"), "Pre-Delay": ("Pre-delay", "reverb.predelay"), "High Cutoff": ("High cut", "reverb.damping")},
        "quirks": [
            "Same controls as Jeskola Reverb, but stereo out (the profile measured a correlation of 0.98); the mono one sums its input.",
            "Rev Out or ER Out at 0 dB peak above full scale on a hot input (+2.6 and +0.9 dBFS in the profile).",
            "Its descriptions give shorter ranges (0 to 40, 0 to 64) than its values reach: Rev Time to 6.4 s, Pre-Delay to 100 ms.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — tail 0.6 s at the defaults, +6.1 dB, correlation 0.98, reverb time 0.5 s to 3.7 s"],
    },
    "Jeskola Wave Shaper": {
        "type": "distortion",
        "summary": "A Buzz waveshaper effect with an input gain of plus or minus 24 dB and a choice of four shapes, per track. Distortion; Neo Drive is the modern one.",
        "params": {"gain": ("Input gain", "gain.input"), "shape": ("Shape", "fx.type")},
        "quirks": [
            "Both controls are track parameters (up to 8 tracks), none global.",
            "The profile found sound from silence (-52 dBFS peak: noise, hum or DC), and a long ring (3.5 s): check it on silence before using it.",
            "What each of the four shapes does was not measured or readable from source: unverified.",
            "Gain is plus or minus 24 dB with 0 dB at the middle step (4096); the profile read about +13 dB at the defaults.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — sound from silence (-51.9 dBFS), +13.3 dB at the defaults"],
    },
    "Jeskola Bass": {
        "type": "polysynth",
        "summary": "An early Buzz bass synth: four voices of saw, pulse or triangle into a resonant filter, with slides between notes. For simple basses in old Buzz songs; Neo Poly is the modern one.",
        "params": {"Cutoff": ("Filter cutoff", "filter.cutoff"), "Resonance": ("Filter resonance", "filter.resonance"),
                   "Waveform": ("Oscillator wave", "osc.wave"), "Volume": ("Volume", "level"), "Slide lenght": ("Slide length", "pitch.glide")},
        "quirks": [
            "Four voices (the profile's note count), notes as written (no pitch offset).",
            "Resonance near its top is very hot: +16 dBFS peak in the profile.",
            "Volume and Slide lenght are pattern columns: Volume 128 is 100 percent, 254 about 198 percent; the profile found no level change from them with the others at their defaults.",
            "Velocity does not change its level.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — four voices, -9 LUFS for a note, hot with Resonance at its top"],
    },
    "Jeskola Bass 2": {
        "type": "monosynth",
        "summary": "A Buzz bass synth, one voice per track: sine, saw, pulse, triangle or noise into a resonant filter with an envelope. For basses in old Buzz songs; Neo Poly is the modern one.",
        "params": {"Cutoff": ("Filter cutoff", "filter.cutoff", PCT), "Resonance": ("Filter resonance", "filter.resonance", PCT),
                   "Envmod": ("Filter envelope amount", "filter.env_amount", PCT), "Decay": ("Filter envelope decay", "env.decay", PCT),
                   "Waveform": ("Oscillator wave", "osc.wave")},
        "quirks": [
            "Each track is its own monophonic voice (8 at most); notes play as written.",
            "All five controls are track parameters, on 0 to 100 percent (Waveform is a choice).",
            "Cutoff swings the level by about 50 dB, so a closed filter is much quieter.",
            "Velocity does not change its level.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — -10 LUFS for a note, monophonic, filter and envelope controls measured"],
    },
    "Jeskola Live": {
        "type": "control",
        "summary": "A Buzz control machine with one parameter, Bar (1 to 128, 16 by default); it makes no sound. What it controls was not measured and is unverified here.",
        "params": {"Bar": ("Bar", "", {"description": "A number from 1 to 128 (16 by default); the machine gives no description."})},
        "quirks": [
            "It makes no sound; it is a control machine.",
            "Its one parameter has no description and nothing was measured beyond its range, so its role is unverified.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — a control machine, silent, one parameter"],
    },
    "Jeskola Loop Recorder": {
        "type": "control",
        "summary": "A Buzz control machine with a single placeholder parameter; it makes no sound. How it is used was not measured and is unverified here.",
        "params": {"Dummy": ("Placeholder", "", {"description": "A placeholder (the machine calls it Dummy), 0 to 127."})},
        "quirks": [
            "It makes no sound; its one parameter is a placeholder (its own name is Dummy).",
            "Its own work happens in its window, which was not exercised by the profile: unverified.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — a control machine, silent, one dummy parameter"],
    },
    "Jeskola Note Matrix": {
        "type": "control",
        "summary": "A Buzz control machine with a single placeholder parameter; it makes no sound. How it is used was not measured and is unverified here.",
        "params": {"Dummy": ("Placeholder", "", {"description": "A placeholder (the machine calls it Dummy), 0 to 127."})},
        "quirks": [
            "It makes no sound; its one parameter is a placeholder (its own name is Dummy).",
            "Its own work happens in its window, which was not exercised by the profile: unverified.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — a control machine, silent, one dummy parameter"],
    },
    "Jeskola Organ": {
        "type": "monosynth",
        "summary": "A drawbar-style Buzz organ: six drawbar levels from 16 foot to 2 foot with an attack and a release. For organ pads and stabs; Neo Poly is the modern synth.",
        "params": {"Attack": ("Attack", "env.attack"), "Release": ("Release", "env.release"), '16"': ("16 foot drawbar", "osc.level", PCT),
                   '8"': ("8 foot drawbar", "osc.level", PCT), '5 1/3"': ("5 1/3 foot drawbar", "osc.level", PCT), '4"': ("4 foot drawbar", "osc.level", PCT),
                   '2 2/3"': ("2 2/3 foot drawbar", "osc.level", PCT), '2"': ("2 foot drawbar", "osc.level", PCT)},
        "quirks": [
            "Monophonic: one note at a time; notes play as written.",
            "Hot at its defaults: 0 dBFS peak for a note; pull the drawbars down before stacking it.",
            "Vib.Int. and Vib.Spd. exist but read \"not impl. yet\" and have no mapping: they are not in the manifest.",
            "Velocity does not change its level. The drawbars sit in the machine's low-to-high order (16, 8, 5 1/3, 4, 2 2/3, 2 foot).",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — -7 LUFS for a note, 0 dBFS peak at the defaults, attack 15 ms to 875 ms"],
    },
    "Jeskola Relativion": {
        "type": "control",
        "summary": "A Buzz control machine that reads a range of notes against a root note and moves a controlled value by a range around its own root; it makes no sound. Its behaviour was not measured and is unverified here.",
        "params": {"Root Note": ("Root note", "", {"description": "A note, C-0 to G-10; default C-5."}),
                   "Lowest Note": ("Lowest note", "", {"description": "A note, C-0 to G-10; default C-5."}),
                   "Highest Note": ("Highest note", "", {"description": "A note, C-0 to G-10; default C-10."}),
                   "Ctrl. Root": ("Controller root", "", {"description": "A note, C-0 to G-10; default C-5."}),
                   "Ctrl. Range": ("Controller range", "", {"description": "0 to 48 steps; default 24."}),
                   "Volume": ("Volume note", "", {"description": "A note value, C-0 to G-10; default C-0."})},
        "quirks": [
            "It makes no sound; it is a control machine.",
            "Its roles are read from the parameter names and texts only: unverified by measurement.",
            "A seventh parameter exists in the machine that has no mapping and is not in the manifest.",
            "Notes are named C-0 up to G-10 (0 to 127); Root Note and Lowest Note default to C-5 (60), Highest Note to C-10 (120).",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — a control machine, silent, six parameters"],
    },
    "Jeskola Tracker": {
        "type": "generator",
        "summary": "A Buzz tracker machine with up to 16 tracks and a Subdiv parameter for row timing. The profile heard nothing from a note at its defaults, so what it plays is unverified here.",
        "params": {"Subdiv": ("Row subdivision", "")},
        "quirks": [
            "No sound from a note at its defaults, as MIDI or in its tracks, even with a wave in slot 1.",
            "Up to 16 tracks; Subdiv defaults to 6 ticks per row part.",
            "How to make it play was not found: unverified.",
            CLOSED,
        ],
        "evidence": [OWN, PROFILE + " — no sound from a note at its defaults, even with a wave in slot 1"],
    },
    "Jeskola NiNjA dElaY": {
        "type": "delay",
        "summary": "Buzz delay with a filter in its feedback loop: up to 8 taps, each with a length in ticks, ms, samples or 1/256 ticks, feedback from -100 to +100 percent, a low-pass, high-pass, band-pass or band-reject filter and a wet level. For dubby, darkening echoes; Neo Delay is the modern one.",
        "params": {"Dry thru": ("Dry through", ""), "Length": ("Delay length", "delay.time"), "Length unit": ("Delay time unit", "delay.unit"),
                   "Feedback": ("Feedback", "delay.feedback"), "f. type": ("Loop filter type", "filter.type"), "f. cutoff": ("Loop filter cutoff", "filter.cutoff"),
                   "f. bandwidth": ("Loop filter bandwidth", "filter.bandwidth"), "Wet out": ("Echo level", "mix.wet")},
        "quirks": [
            "Each track is its own tap with its own length, feedback, filter and level (8 at most).",
            "The filter sits in the feedback path, so each repeat is filtered again; the default filter is a high-pass at 550 Hz.",
            "Feedback at the ends (-100 and +100 percent) never decays: param_set keeps it within -98.4 to +98.4 percent.",
            "The x64 rebuild ships (machines.json); it is for private use, not for public release as it is.",
            "Mono: NeoBuzz Live feeds it left + right summed.",
        ],
        "evidence": [_src(BT + "Ninja/Ninja.cpp", "parameters, DescribeValue texts and the feedback filter"),
                     PROFILE + " — delay traits, feedback's safe range"],
    },
    "Jeskola Noise Generator": {
        "type": "triggered",
        "summary": "Noise with an attack, sustain and release envelope per track and a colour from dark to white; a track sounds when its Trigger is written (a played parameter: automation_write), up to 8 tracks. For hats, bursts and risers.",
        "params": {"Attack": ("Attack", "env.attack"), "Sustain": ("Sustain time", "env.sustain"), "Release": ("Release", "env.release"),
                   "Color": ("Noise colour", "osc.noise"), "Volume": ("Volume", "level")},
        "quirks": [
            "Each track is its own noise voice; a track sounds when its Trigger (a pattern column, not a state parameter) is written, and only while Volume is above 0.",
            "Attack, Sustain and Release are in milliseconds, 1 to 65535; Sustain is a hold time, not a level.",
            "Color is 0 (dark) to 4096 (white); Volume 128 is 100 percent.",
            "No note input: it does not follow pitch.",
        ],
        "evidence": [_src(BT + "Noise/Noise.cpp", "parameters, the trigger and the envelope stages"),
                     PROFILE + " — a trigger plays, attack 0 to 200 ms, colour changes the brightness"],
    },
    "Jeskola Trilok": {
        "type": "triggered",
        "summary": "Synthesised bass drum: BD Trigger fires it (written as an event: automation_write), with its tone, decay and volume. For simple kicks; Neo Drums is the modern one.",
        "params": {"BD Trigger": ("Kick trigger", ""), "BD Tone": ("Tone", "drum.tone"), "BD Decay": ("Decay", "drum.decay"), "BD Volume": ("Volume", "")},
        "quirks": [
            "BD Trigger is an event: write a 1 with automation_write to play the drum; it does not follow notes.",
            "BD Decay sets the release from about 17 ms (0) to 1.3 s (127), 150 ms by default.",
            "BD Volume is declared but the code never reads it, and a sweep found no change: it has no effect.",
            "A pitch sweep from about 250 Hz down to 1 Hz over the drum's length; BD Tone sets its filter.",
            "BD Tone at its top is hot: +2.5 dBFS peak in the profile.",
        ],
        "evidence": [_src(BT + "Trilok/Trilok.cpp", "parameters, the tone and decay formulas, and that BD Volume is never read"),
                     PROFILE + " — one trigger -12 LUFS, decay 20 to 470 ms, BD Tone brightness 85 to 177 Hz"],
    },
}

HAND_CURATED = ["Jeskola Cross Delay", "Jeskola Delay", "Jeskola Distortion", "Jeskola EQ-3", "Jeskola Filter", "Jeskola Reverb", "Jeskola Bass 3"]
FROM_SOURCE = ["Jeskola NiNjA dElaY", "Jeskola Noise Generator", "Jeskola Trilok"]


def curate(m):
    """The profiled draft or generated manifest dict made curated (in place): type, summary, quirks, evidence, plain names, roles."""
    for old, new in FIXES.get(m["gear"], []):
        m["summary"] = m["summary"].replace(old, new)
        for group in ("parameters", "track_parameters"):
            for e in m.get(group, []):
                e["description"] = e.get("description", "").replace(old, new)
    c = CURATE.get(m["gear"])
    if c is None:
        return m
    out = {}
    for k, v in m.items():
        if k == "status":
            v = "curated"
        if k == "source":
            if v.startswith("generated by"):
                v = v.split("; curated by")[0] + "; curated by tools/jeskola_knowledge.py"
            else:
                v = ("curated by hand (2026-11-13, agent-knowledge.md K2 batch b) with tools/jeskola_knowledge.py, from the profile by "
                     "tools/machine-profiler.py on windows, 2026-11-13 and the evidence listed")
        if k == "type":
            v = c["type"]
        if k == "summary":
            v = c["summary"]
        out[k] = v
        if k == "summary":
            out["quirks"] = list(c["quirks"])
            out["evidence"] = list(c["evidence"])
    m.clear()
    m.update(out)
    for group in ("parameters", "track_parameters"):
        for e in m.get(group, []):
            if e["name"] not in c["params"]:
                raise SystemExit(f"{m['gear']}: no curation for {group[:-1]} {e['name']!r}")
            plain, role, *over = c["params"][e["name"]]
            if over:
                o = dict(over[0])
                like = o.pop("like", None)
                if like:   # the same control of another Jeskola machine, whose exact mapping this one shares
                    with open(path_of(like[0]), encoding="utf-8-sig") as f:
                        src = next(q for q in json.load(f)["parameters"] if q["name"] == like[1])
                    for k in ("a", "b", "points", "unit", "kind", "choices"):
                        e.pop(k, None)
                    o = {**{k: src[k] for k in ("kind", "unit", "points") if k in src}, "description": src["description"], **o}
                e.update(o)
            new = {}
            for k, v in e.items():
                if k in ("confidence", "suggested_role", "role_evidence"):
                    continue
                new[k] = v
                if k == "name":
                    new["plain_name"] = plain
                    if role:
                        new["role"] = role
            e.clear()
            e.update(new)
    return m


def explain(m):
    """The manifest dict with how and sound on every parameter (in place)."""
    texts = EXPLAIN.get(m["gear"])
    if texts is None:
        return m
    used = set()
    for group in ("parameters", "track_parameters"):
        for e in m.get(group, []):
            key = e["name"]
            if key not in texts:
                raise SystemExit(f"{m['gear']}: no Explain text for {group[:-1]} {key!r}")
            how, sound = texts[key]
            used.add(key)
            for k in ("how", "sound"):
                e.pop(k, None)
            # how and sound go after the description, as the Pedal manifests have them
            e["how"], e["sound"] = how, sound
    extra = set(texts) - used
    if extra:
        raise SystemExit(f"{m['gear']}: Explain text for parameters it does not have: {sorted(extra)}")
    return m


def path_of(gear):
    for sub in ("Effects", "Generators"):
        p = os.path.join(ROOT, "Machines", "Manifests", sub, gear + ".machine.json")
        if os.path.exists(p):
            return p
    raise SystemExit(f"no manifest for {gear}")


def dump(m):
    return json.dumps(m, indent=1, ensure_ascii=False) + "\n"


def run(write):
    problems = []
    for gear in EXPLAIN:
        if gear in FROM_SOURCE:
            continue
        path = path_of(gear)
        with open(path, encoding="utf-8-sig") as f:
            before = f.read()
        m = json.loads(before)
        text = dump(explain(curate(m)))
        if text != before:
            if write:
                with open(path, "w", encoding="utf-8", newline="\n") as f:
                    f.write(text)
            else:
                problems.append(f"{gear}: its curation or Explain text is not what tools/jeskola_knowledge.py says (python3 tools/jeskola_knowledge.py --write)")
    return problems


if __name__ == "__main__":
    found = run("--write" in sys.argv[1:])
    for p in found:
        print(p)
    print(("wrote" if "--write" in sys.argv[1:] else "checked") + f" {len(EXPLAIN) - len(FROM_SOURCE)} manifests, {len(found)} problems")
    sys.exit(1 if found else 0)
