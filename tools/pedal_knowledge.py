#!/usr/bin/env python3
# Fork (docs/specs/agent-knowledge.md, K2 batch a): what an agent is told about the Pedal machines' parameters, and the
# curation of the nine Pedal manifests that were generated from their source (tools/source_manifests.py).
#
#  - EXPLAIN: a How sentence (how the control changes the sound) and a Sound sentence (what you will hear) for every
#    parameter of the 19 Pedal machines, global and track. They go into the manifest as "how" and "sound", which
#    machine_describe serves as it does for the Neo machines. Keys are the parameter's name as the machine declares it;
#    "t:<name>" names a track parameter whose name a global one shares (Pedal M1's Volume).
#  - CURATE: for the nine generated manifests, what a curated one carries beyond the generated mappings: a type, plain
#    names, roles, quirks and evidence. source_manifests.py applies it, so the mappings stay what the source generates.
#
#   python3 tools/pedal_knowledge.py            check: the ten hand-curated Pedal manifests carry their Explain text
#   python3 tools/pedal_knowledge.py --write    write it into them (the nine others get it from source_manifests.py)

import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PEDAL = "ExternalMachines/source/thepedal/"
HAND_CURATED = ["Pedal Chorus", "Pedal Comp", "Pedal Drum Matrix", "Pedal EQ", "Pedal Filter", "Pedal Plate",
                "Pedal FM", "Pedal Juno106", "Pedal M1", "Pedal SH101"]


def band(prefix, name):
    return {
        f"{prefix} Threshold": (f"Sets the level, from 0 down to -60 dB below full scale, above which the {name} band is turned down.", f"A lower threshold squeezes more of the {name} range; a higher one leaves it alone."),
        f"{prefix} Ratio": (f"Sets how strongly the {name} band is turned down above its threshold, from 1:1 to 40:1, with the top step acting as a limiter.", "Low ratios level gently; high ratios flatten the band and the top step stops it dead."),
        f"{prefix} Attack": (f"Sets how fast the {name} band's gain reduction arrives, from 0.1 to 200 ms.", "Slow attacks let the front of each hit through; fast ones tame it."),
        f"{prefix} Release": (f"Sets how fast the {name} band's gain comes back, from 1 ms to 2 s.", "Short releases pump in time with the music; long ones glide."),
        f"{prefix} Makeup": (f"Raises the {name} band after compression by 0 to 24 dB.", f"Brings the squeezed {name} range back up so the band does not sound smaller."),
        f"{prefix} Bypass": (f"Takes the {name} band out of the compression but leaves it in the crossover split.", f"The {name} range plays untouched while the others stay compressed."),
    }


def _ops():
    d = {}
    for n in "1234":
        d[f"Op{n} Ratio"] = (f"Sets operator {n}'s pitch as a multiple of the note, from 0.125 to 16 in steps of 0.125.", "Whole ratios stay harmonic; odd ones turn the sound bell-like or metallic.")
        d[f"Op{n} Detune"] = (f"Detunes operator {n} by up to 50 cents either way.", "Small amounts add slow beating and warmth; the middle is exactly in tune.")
        d[f"Op{n} Level"] = (f"Sets operator {n}'s output from 0 to 100 percent: modulation depth for a modulator, loudness for a carrier.", "More level on a modulator brightens and thickens the tone; on a carrier it is simply louder.")
        d[f"Op{n} Attack"] = (f"Sets how long operator {n}'s envelope takes to rise, from 0 to 4 s on a square-law curve.", "Slow attacks on a modulator make the tone open up; on a carrier they fade the note in.")
        d[f"Op{n} Decay"] = (f"Sets how long operator {n}'s envelope falls to its sustain level, from 0 to 8 s on a square-law curve.", "A short decay on a modulator gives a bright pluck that mellows; long decays keep the colour.")
        d[f"Op{n} Sustain"] = (f"Sets the level operator {n}'s envelope holds while the key is down, from 0 to 100 percent.", "Low sustain lets the note thin out to a plain tone; high sustain keeps it bright and full.")
        d[f"Op{n} Release"] = (f"Sets how long operator {n}'s envelope takes to fall after the key is let go, from 0 to 8 s on a square-law curve.", "Short releases stop the note at once; long ones leave a ringing tail.")
        d[f"Op{n} VelSens"] = (f"Sets how much note velocity scales operator {n}'s level, from 0 to 100 percent.", "At 0 every note sounds alike; higher values make harder notes louder or brighter, by the operator's role.")
    return d


def _m1():
    d = {}
    for n in "12":
        d[f"OSC{n} Wave"] = (f"Chooses which wavetable slot oscillator {n} plays in its first key zone (1 to 200).", "The sample you picked sounds; an empty slot stays silent.")
        d[f"OSC{n} Octave"] = (f"Shifts oscillator {n} by whole octaves, from -2 to +2.", "Moves the sample up or down in register, changing its length and brightness with it.")
        d[f"OSC{n} Transpose"] = (f"Shifts oscillator {n} by semitones, from -12 to +12.", "Retunes the sample against the other oscillator, for intervals and chords.")
        d[f"OSC{n} Detune"] = (f"Detunes oscillator {n} by up to 50 cents either way.", "Small differences between the two oscillators add slow chorus-like beating.")
        d[f"OSC{n} Level"] = (f"Sets oscillator {n}'s level in the mix, from 0 to 100 percent.", "Balances the two samples against each other.")
        for z, s in (("2", "60"), ("3", "72"), ("4", "84")):
            d[f"OSC{n} Wave {z}"] = (f"Chooses the wavetable slot oscillator {n} plays from zone {z} upward (1 to 200).", "Higher notes switch to this sample, so a multisample keeps its natural tone across the keyboard.")
            d[f"OSC{n} Split {z}"] = (f"Sets the MIDI note, 0 to 127, where zone {z} of oscillator {n} takes over from the zone below.", "Moves the point on the keyboard where the sample changes.")
    for n in "12":
        d[f"LFO{n} Wave"] = (f"Chooses the shape of LFO {n}: triangle, sawtooth, square, sample and hold, or smooth random.", "Triangle and sine-like shapes sway; square jumps; random shapes wander.")
        d[f"LFO{n} Speed"] = (f"Sets LFO {n}'s rate, from 0.05 to 30 Hz on an exponential scale.", "Slow rates drift over seconds; fast ones turn into vibrato or tremolo.")
        d[f"LFO{n} Delay"] = (f"Waits 1 ms to 8 s after a note starts before LFO {n} begins.", "The note starts steady and the movement arrives later.")
        d[f"LFO{n} Fade"] = (f"Fades LFO {n} in over 1 ms to 8 s once it has started.", "The movement grows in gently instead of appearing at full strength.")
        d[f"LFO{n} to Pitch"] = (f"Sends LFO {n} to the pitch, up to one semitone either way.", "Gives vibrato: a little is natural, a lot sounds seasick.")
        d[f"LFO{n} to VDF"] = (f"Sends LFO {n} to the filter cutoff, up to 4 octaves either way.", "Sweeps the brightness in time with the LFO, like a wah.")
        d[f"LFO{n} to VDA"] = (f"Sends LFO {n} to the amplifier, from 0 to 100 percent.", "Gives tremolo: the level swells and dips with the LFO.")
    return d


def _drum():
    d = {}
    for n in "123456":
        d[f"Slot{n}Type"] = (f"Chooses the effect in slot {n} out of 21 (bitcrush, drive, filters, ring modulation, comb, stutter, delay, reverb and more); the slots run 1 to 6 in series.", "None passes the sound unchanged; every other choice gives it its own kind of damage or space.")
        d[f"Slot{n}Amount"] = (f"Sets how much of slot {n}'s effect is applied, from 0 to 100 percent.", "At 0 the slot is clean; raising it brings its character in.")
        d[f"Slot{n}Char"] = (f"Sets slot {n}'s main control, whose meaning follows the effect loaded: the cutoff of a filter, the time of a delay, the degree of a resonator.", "What changes depends on the effect; the value's text names it.")
        d[f"Slot{n}Mode"] = (f"Sets slot {n}'s two-way option, such as the Q of a filter or hard against soft clipping in a drive.", "Switches between two flavours of the same effect.")
        d[f"Slot{n}EnvDepth"] = (f"Lets the input's loudness move slot {n}'s main control, from -100 to +98 percent with 0 in the middle.", "Hits push the effect harder; a negative depth pulls it the other way as they get louder.")
        d[f"Slot{n}LfoDepth"] = (f"Lets the rack's LFO move slot {n}'s main control, from -100 to +98 percent with 0 in the middle.", "The effect sweeps in time with the song.")
    return d


def _tracker():
    d = {f"Mute Track {n}": (f"Mutes track {n} with a fade of Mute Inertia, while its voice keeps running.", "The track fades out and comes back in phase with the song.") for n in range(1, 17)}
    return d


EXPLAIN = {
    "Pedal Chorus": {
        "Rate": ("Sets the speed of the voices' sweep, from 0.1 to 5 Hz.", "Slow rates shimmer; fast ones begin to wobble."),
        "Depth": ("Sets how far each voice's delay swings, from 0 to 8 ms either way.", "More depth thickens the sound and then detunes it audibly."),
        "Delay": ("Sets each voice's starting delay, from 5 to 25 ms.", "Short delays sound tight and doubled; long ones sound looser and more distant."),
        "Spread": ("Pans the four voices from the centre (0 percent) to wide apart (100 percent).", "At 0 the output is mono; at 100 it opens into a wide, soft stereo image."),
        "Tone": ("Darkens only the wet signal with a low-pass from 22 kHz (0 percent) down to 2 kHz (100 percent).", "The copies sit back behind the dry sound as the tone falls."),
        "Mix": ("Blends dry and chorused sound from 0 to 100 percent.", "Low mixes add width; high mixes sound lush and detuned."),
    },
    "Pedal Comp": {
        "Threshold": ("Sets the level, from 0 down to -60 dB, above which the signal is turned down.", "A lower threshold compresses more of the signal; a higher one only catches the peaks."),
        "Ratio": ("Sets how strongly the signal is turned down above the threshold, from 1:1 to 30:1.", "Low ratios level gently; high ratios flatten peaks almost like a limiter."),
        "Knee": ("Sets the width of the bend into compression, from 0 (hard) to 12 dB.", "A wide knee eases in unnoticed; a hard one bites at the threshold."),
        "Attack": ("Sets how fast the gain is turned down after a peak, from 1 to 200 ms.", "Slow attacks let the front of a hit through; fast ones soften it."),
        "Release": ("Sets how fast the gain returns after the signal drops, from 10 ms to 2 s.", "Short releases pump with the beat; long ones glide and glue."),
        "Makeup Gain": ("Raises the level after compression by 0 to 24 dB; it is ignored while Auto Makeup is on.", "Restores the loudness the compression took away."),
        "Auto Makeup": ("Sets the makeup gain from the threshold and ratio, up to +24 dB.", "The level stays close to where it was before compression."),
        "Wet/Dry": ("Blends the compressed signal with the original, from 0 to 100 percent.", "Below 100 percent keeps the natural attack under the squeezed body: parallel compression."),
        "Lookahead": ("Delays the audio by 0 to 20 ms so the compressor can react before a peak arrives.", "Fast peaks are caught cleanly; the output is that much later."),
        "Detection": ("Chooses whether the compressor listens to peaks or to the average level (RMS).", "Peak reacts to every transient; RMS follows the loudness and sounds smoother."),
    },
    "Pedal EQ": {
        "Bypass": ("Skips all the EQ processing.", "Lets you compare the EQ against the untouched signal."),
        "LS Solo": ("Plays only the low shelf's filter on its own.", "Lets you hear exactly which part of the sound that band touches."),
        "LM Solo": ("Plays only the low-mid bell's filter on its own.", "Lets you hear exactly which part of the sound that band touches."),
        "HM Solo": ("Plays only the high-mid bell's filter on its own.", "Lets you hear exactly which part of the sound that band touches."),
        "HS Solo": ("Plays only the high shelf's filter on its own.", "Lets you hear exactly which part of the sound that band touches."),
        "LS Freq": ("Sets the low shelf's corner in steps from 20 to 500 Hz.", "Moves where the bass lift or cut starts."),
        "LS Gain": ("Raises or lowers everything below the low shelf's corner, by up to 24 dB in 0.5 dB steps.", "Boosts add weight; cuts thin the bottom end."),
        "LM Freq": ("Sets the low-mid bell's centre in steps from 100 Hz to 5 kHz.", "Moves the boost or cut between the body and the upper mids."),
        "LM Gain": ("Raises or lowers the low-mid bell by up to 24 dB in 0.5 dB steps.", "Boosts add body or boxiness; cuts clear mud."),
        "LM Q": ("Sets the low-mid bell's width in steps from 0.3 (broad) to 10 (narrow).", "Broad bells change the tone; narrow ones pick out single frequencies."),
        "HM Freq": ("Sets the high-mid bell's centre in steps from 500 Hz to 16 kHz.", "Moves the boost or cut between the presence and the air."),
        "HM Gain": ("Raises or lowers the high-mid bell by up to 24 dB in 0.5 dB steps.", "Boosts add presence or harshness; cuts soften."),
        "HM Q": ("Sets the high-mid bell's width in steps from 0.3 (broad) to 10 (narrow).", "Broad bells change the tone; narrow ones pick out single frequencies."),
        "HS Freq": ("Sets the high shelf's corner in steps from 1 to 20 kHz.", "Moves where the treble lift or cut starts."),
        "HS Gain": ("Raises or lowers everything above the high shelf's corner, by up to 24 dB in 0.5 dB steps.", "Boosts add air and brightness; cuts darken."),
        "Output": ("Trims the level after the EQ by up to 24 dB either way.", "Matches the loudness to the untouched signal so the comparison is fair."),
    },
    "Pedal Filter": {
        "Mode": ("Chooses low pass, high pass, band pass, band reject or comb.", "Each removes or keeps a different part of the spectrum; comb adds a ringing, pitched resonance."),
        "Slope": ("Chooses 12 or 24 dB per octave; in comb mode it chooses FIR (flanging) or IIR (resonator).", "The steeper slope cuts more sharply; in comb mode the choice changes the colour of the ringing."),
        "Cutoff": ("Sets the cutoff, or the comb's base pitch, from 20 Hz to 18 kHz.", "Moves the part of the spectrum that is cut or kept; sweeping it makes a classic filter sweep."),
        "Resonance": ("Boosts the region at the cutoff from 0 to 100 percent; in comb mode it is the feedback.", "Low values are smooth; high values whistle and nearly self-oscillate."),
        "Drive": ("Pushes the signal 1 to 10 times harder into a soft saturator before the filter.", "Adds warm to gritty harmonics that the filter then shapes."),
        "LFO Wave": ("Chooses the LFO shape: sine, triangle, square, saw up, saw down or sample and hold.", "Smooth shapes sweep; square and sample and hold jump."),
        "LFO Rate": ("Sets the free-running LFO speed from 0 to 20 Hz; 0 stops it.", "Slow rates breathe; faster ones wobble."),
        "Tempo Sync": ("Locks the LFO to the song's tempo, at the division set by LFO Div.", "The sweep stays in time with the beat."),
        "LFO Div": ("Chooses the length of one LFO cycle in bars, notes, dotted notes or triplets when Tempo Sync is on.", "Longer divisions give slow builds; short ones give rhythmic chopping."),
        "LFO Depth": ("Sets how far the LFO swings the cutoff, up to 4 octaves either way.", "A little adds movement; a lot makes a wah or auto-filter."),
        "Phase Offset": ("Offsets the right channel's LFO from the left by 0 to 180 degrees.", "At 0 both sides move together; at 180 they sweep in opposite directions and the sound moves in the stereo field."),
        "Mix": ("Blends the filtered signal with the dry one, from 0 to 100 percent.", "Below 100 percent keeps some of the original body under the sweep."),
    },
    "Pedal Plate": {
        "Mix": ("Blends dry and reverb from 0 percent (dry only) to 100 percent (reverb only).", "Low mixes add a little space; on a send, 100 percent gives only the reverb."),
        "PreDelayMs": ("Delays the start of the reverb by 0 to 500 ms.", "A gap keeps the dry sound clear before the space arrives."),
        "Decay": ("Sets the tail length from 0 percent (short) to 100 percent (long).", "Short decays sound like a small plate; long ones ring on and on."),
        "Damping": ("Removes high frequencies from the tail, from 0 percent (bright) to 100 percent (dark).", "The tail goes from shiny to dull and distant."),
        "Size": ("Scales the internal delay lengths from 10 to 200 percent.", "Small sizes sound tight and metallic; large sizes sound big and slow to build."),
        "ModRate": ("Sets the speed of the tank's modulation, from 0 to 5 Hz.", "Slow modulation keeps the tail from ringing on one pitch."),
        "ModDepth": ("Sets how far the tank's modulation swings, from 0 to 100 percent.", "More depth smooths the tail and adds a gentle chorus-like shimmer."),
        "LowCut": ("High-passes the signal going into the reverb, from 20 to 800 Hz.", "Keeps bass out of the tail so it stays clear."),
        "Feedback": ("Sends the reverb's output back into its tank, from 0 to 95 percent; it is capped inside for stability.", "Stretches the tail and makes it build; high values sound like a big, ringing hall."),
    },
    "Pedal Drum Matrix": {
        **_drum(),
        "Limiter": ("Limits the rack's output; it is on by default.", "Keeps loud, resonant or fed-back settings from overloading what follows."),
        "AutoGainOn": ("Levels the output automatically after the slots.", "Changing slots or amounts does not change the loudness much."),
        "Feedback": ("Sends the rack's output back to its input, from 0 to 100 percent.", "Builds echoes and howls; higher settings run away until the limiter catches them."),
        "FbTime": ("Sets the delay in the feedback path, from 1 to 500 ms on an exponential scale.", "Short times give metallic ringing; long ones give distinct echoes."),
        "FbTone": ("Low-passes the feedback path, from 200 Hz to 12 kHz.", "Lower settings make the repeats darker and softer."),
        "EnvRelease": ("Sets how slowly the input's loudness follower lets go, from 20 ms to 2 s.", "Short releases follow each hit; long ones follow the overall loudness."),
        "EnvToFb": ("Lets the input's loudness raise the feedback, from 0 to 100 percent.", "Loud hits ring and howl more, quiet parts stay clean."),
        "LfoRate": ("Sets the rack LFO's cycle in ticks, from 1 to 64, locked to the song's tempo.", "Short cycles flutter; long ones sweep slowly in time."),
        "LfoWave": ("Chooses the rack LFO's shape: sine, triangle, saw, square, steps or random.", "Smooth shapes sweep; steps and random jump."),
        "Key": ("Sets the root note that Resonator slots tune to.", "Resonator slots ring at pitches of this key."),
        "Scale": ("Chooses the scale that Resonator slots pick their degree from.", "The tuned ringing follows major, minor, pentatonic or another scale."),
        "Morph": ("Blends the live settings toward the stored target, from 0 to 100 percent.", "Moves the whole rack between two scenes in one control."),
        "Store": ("Switch from off to Store to capture the current settings as the morph target.", "Saves the scene that Morph will move toward."),
    },
    "Pedal Gate": {
        "Threshold": ("Sets the level, from -60 to 0 dB in 0.5 dB steps, at which the gate opens.", "Higher thresholds let only the loud parts through; lower ones keep quieter material."),
        "Hysteresis": ("Makes the gate close this far below the opening level, from 0 to 20 dB.", "More hysteresis stops the gate from chattering on signals that hover near the threshold."),
        "Attack ms": ("Sets how long the gate takes to open fully, from 0 to 500 ms.", "Short attacks keep the start of a hit sharp; long ones fade the sound in."),
        "Hold ms": ("Keeps the gate open for 0 to 2000 ms after the signal falls below the closing level.", "Longer holds stop the gate from cutting the ends of notes between close hits."),
        "Release ms": ("Sets how long the gate takes to close, from 1 to 2000 ms.", "Short releases chop tails; long ones fade them away."),
        "Range dB": ("Sets how far the closed gate turns the signal down: 0 is nearly silent (-90 dB), 90 is no reduction.", "Low settings shut the sound completely; high settings only dip the level between hits."),
        "Detection": ("Chooses whether the gate listens to peaks or to the average level (RMS).", "Peak reacts to every transient; RMS is smoother on sustained sounds."),
        "Lookahead ms": ("Delays the audio by 0 to 25 ms so the gate can open before a transient arrives.", "The attack of a hit is never clipped; the output is that much later."),
        "Bypass": ("Passes the audio through unprocessed.", "Lets you hear the sound without the gate."),
    },
    "Pedal Gain": {
        "Gain": ("Sets the output level from 0 to 200 percent of the input: 100 is unity and 200 is +6 dB.", "Below 100 turns the sound down; above it makes it louder and may overload what follows."),
        "Mute": ("Silences the output, fading over the Inertia time.", "The sound fades out or back in instead of cutting with a click."),
        "Inertia": ("Sets how long the mute fade takes, from 0 to 500 ms.", "At 0 the mute is instant; longer times fade smoothly."),
    },
    "Pedal Gain Multi": {
        "Gain": ("Sets the master fader in steps of 0.5 dB, from -80.5 dB (the lowest step is silence) to +10 dB.", "Moves the level of the whole mix."),
        "Master Mute": ("Mutes the master output, fading over the Inertia time.", "The whole mix fades out or back in without a click."),
        "Inertia": ("Sets the fade time of every mute and solo, from 0 to 500 ms.", "At 0 the switches are instant; longer times fade smoothly."),
        "Solo": ("Solos this input in the master mix only; direct outs are not affected.", "Only soloed inputs are heard in the master mix."),
        "Mute": ("Mutes this input in the master mix and its direct out, fading over Inertia.", "The input fades out of the mix."),
        "Volume": ("Sets this input's fader in steps of 0.5 dB, from -80.5 dB (the lowest step is silence) to +10 dB.", "Balances this input against the others."),
        "Pan": ("Pans this input from full left (0) through the centre (64) to full right (128).", "Moves the input across the stereo field."),
        "Mono": ("Sums this input's left and right to mono before the fader and pan.", "A stereo source collapses to the centre of its pan position."),
    },
    "Pedal Shaper": {
        "Drive": ("Sets how hard the signal drives the shaper, from -24 to +24 dB in 0.5 dB steps.", "More drive adds more distortion; at 0 dB a quiet signal is barely changed."),
        "Shape": ("Chooses the curve: soft, hard clip, tube, wavefolder or crush.", "Soft warms, hard buzzes, tube adds even harmonics, fold rings metallically and crush sounds lo-fi."),
        "Tone": ("Tilts the spectrum before the shaper around 1 kHz, from -100 percent (darker) to +98 percent (brighter).", "A darker tilt makes the distortion rounder; a brighter one makes it bite."),
        "Bias": ("Adds a DC offset before the shaper, from -100 to +98 percent of full scale.", "Makes the clipping lopsided, which adds even harmonics and a thicker, tube-like colour."),
        "Mix": ("Blends dry and shaped signal from 0 to 100 percent.", "Low mixes keep the original and add grit; full wet is all distortion."),
        "Output": ("Sets the level after the shaper, from -24 to +24 dB.", "Pulls the louder, driven signal back to the level it started at."),
        "Hysteresis": ("Smears the release after the shaper with a time constant up to 10 ms; 0 is off.", "Tails round off and bass gets fatter, like magnetic tape."),
        "Dust": ("Mixes noise that follows the signal's level into the shaper, from 0 to 100 percent; 0 is off.", "Adds a layer of hiss that comes and goes with the music and is distorted with it."),
    },
    "Pedal MComp": {
        "Listen": ("Plays one band alone, or all of them with All.", "Lets you hear what one band of the compressor is doing."),
        "Xover L-LM": ("Sets the crossover between the low and low-mid bands, from 40 to 600 Hz.", "Moves the top of the bass band."),
        "Xover LM-HM": ("Sets the crossover between the low-mid and high-mid bands, from 200 Hz to 3 kHz.", "Moves the dividing line between the body and the presence of the sound."),
        "Xover HM-H": ("Sets the crossover between the high-mid and high bands, from 800 Hz to 12 kHz.", "Moves the bottom of the top band."),
        "Knee": ("Sets the width of the bend into compression for every band, from 0 (hard) to 24 dB.", "A wide knee eases in unnoticed; a hard one bites at the threshold."),
        "Detection": ("Chooses whether all bands listen to peaks or to the average level (RMS).", "Peak reacts to transients; RMS follows loudness and sounds smoother."),
        **band("L", "low"), **band("LM", "low-mid"), **band("HM", "high-mid"), **band("H", "high"),
        "Output Gain": ("Sets the level after the four bands are summed, from -24 to +24 dB.", "Trims the overall loudness after the compression."),
        "Dry-Wet": ("Blends the dry input with the compressed output, from 0 to 100 percent.", "Below 100 percent keeps natural dynamics under the squeezed body: parallel compression."),
        "Lookahead": ("Delays the audio by up to 10 ms so the compressors can act before peaks arrive; Off adds no delay.", "Fast peaks are caught cleanly; the output arrives later by the same time."),
        "Phase Linear": ("Adds all-pass compensation so the four bands add up in phase at the crossovers.", "The crossover regions sound smoother; the output is delayed by a few milliseconds."),
        "Spectrum View": ("Shows a spectrum analyser in the machine's window; it has no effect on the sound.", "Nothing changes in the audio."),
    },
    "Pedal Tracker": {
        "Master": ("Sets the level of the whole machine from 0 to 200 percent; 100 is unity.", "Turns every track up or down together."),
        "Mute": ("Mutes the whole machine, fading over Mute Inertia while the voices keep running.", "The sound fades out and comes back in time with the song."),
        "Mute Inertia": ("Sets how long a mute fade takes, from 0 to 500 ms.", "At 0 the mute is as quick as one block; longer times fade smoothly."),
        **_tracker(),
        "Interpolation": ("Chooses how samples are read when they are played at another pitch: none, linear, cubic, B-spline or 16 or 32 point sinc.", "Higher quality gives cleaner highs and fewer aliasing artefacts at the cost of CPU."),
        "Wave": ("Chooses which wavetable slot, 1 to 200, the track plays from its next note on.", "The sample you chose plays instead of the previous one."),
        "Volume": ("Sets the track's volume on a square-law scale where 128 is unity and 254 is about +11.9 dB.", "Higher values play the track louder, and zero silences it."),
        "Cmd": ("Chooses the first effect command, such as a pitch slide, portamento, vibrato, sample offset or volume slide.", "The track slides, wobbles or jumps according to the command."),
        "Arg": ("Gives the first effect command its argument, 0 to 254.", "Sets how fast, how deep or how far the command works."),
        "Cmd2": ("Chooses the second effect command, which runs alongside the first.", "Lets a track combine two effects, such as vibrato and a volume slide."),
        "Arg2": ("Gives the second effect command its argument, 0 to 254.", "Sets how fast, how deep or how far the second command works."),
    },
    "Pedal Chord": {
        "Velocity": ("Sets the velocity sent to the target, from 1 to 127.", "Louder or brighter notes, depending on how the target uses velocity."),
        "Chord": ("Chooses the chord, one of 51 voicings from triads and sevenths to shells, quartal stacks and clusters.", "Changes the mood from bright and open to dark, tense or ambiguous."),
        "Mode": ("Chooses whether the chord plays at once or as an arpeggio up, down, up and down, down and up, or random.", "A chord blocks together; an arpeggio plays its notes one after another."),
        "Speed": ("Sets the pattern ticks between arpeggio steps, from 1 to 1024.", "Smaller values give a fast arpeggio; larger ones a slow one."),
        "Length": ("Sets how long each note holds in ticks, up to 16384; 0 holds it until the next note.", "Short lengths play staccato; 0 lets notes ring until replaced."),
        "Octaves": ("Sets the octave range of the arpeggio, from 1 to 4.", "More octaves stretch the arpeggio across a wider register."),
        "Step": ("Sets how many chord tones the arpeggio advances per step, from 1 to 8.", "Skipping tones makes the arpeggio leap rather than walk."),
        "Oct Walk": ("Cycles the octave after each pass of the chord: off, up or ping-pong.", "The arpeggio climbs or rocks through the octaves instead of repeating."),
        "Swing": ("Lengthens every other step, from 0 (straight) to 100 percent (a 2:1 triplet feel).", "Straight steps become a shuffle."),
        "Swing On": ("Chooses whether the first or the second step of each pair gets the long wait.", "Shifts the shuffle from a lilt to a push."),
        "Humanize": ("Moves each step's timing at random by up to half this percentage of a step.", "The arpeggio loosens from machine-exact to played by hand."),
        "Hum. Vel": ("Moves each step's velocity at random by up to half this percentage.", "Notes vary in strength like a live player's."),
    },
    "Pedal LFO": {
        "Shape": ("Chooses the wave: sine, triangle, square, saw up, saw down or sample and hold.", "Smooth shapes move the target gently; square and sample and hold jump."),
        "Period": ("Sets one cycle in ticks, from 2 to 16384, when Sync is Free; 16 ticks is one bar at 4 ticks per beat.", "Short periods wobble; long ones sweep over many bars."),
        "Depth": ("Sets the swing as a percentage of the target's range (peak to peak).", "Small depths add a touch of movement; large ones sweep the whole range."),
        "Center": ("Sets the middle of the swing as a percentage of the target's range.", "Moves the average setting around which the target swings."),
        "Phase": ("Offsets the start of the wave from 0 to about 357 degrees.", "Staggers the movement against another LFO."),
        "Sync": ("Locks one cycle to a note length from a whole note to a triplet eighth, or runs free on Period.", "Locked cycles stay in time with the song."),
        "Bypass": ("Stops the LFO writing to its target and leaves it at its last value.", "The target stops moving."),
        "Start": ("Runs the LFO when on and restarts it from phase 0 on each switch from off to on; off holds the target at Center.", "The movement starts on cue."),
    },
    "Pedal Peer Control": {
        "Inertia": ("Sets the glide time to a new value, from 0 (off) to 128 ticks in tenths of a tick.", "Targets slide smoothly instead of jumping."),
        "Slaved": ("Makes this track mirror the previous track's value.", "Two tracks move together from one control."),
        "Value": ("Sets the value sent through the track's mapping curve to its target, from 0 to 100 percent in 65535 steps.", "The target parameter moves across its range."),
    },
    "Pedal Juno106": {
        "LFO Rate": ("Sets the triangle LFO's speed from 0.1 to 30 Hz.", "Slow rates drift; fast ones give vibrato or wobble."),
        "LFO Delay": ("Fades the LFO in over 0 to 5 s after each note starts.", "Notes start steady and the vibrato arrives afterward."),
        "DCO LFO": ("Sends the LFO to pitch, up to one semitone.", "Gives vibrato from subtle to wide."),
        "DCO PWM": ("Sets how far the PWM source moves the pulse width, up to 45 points either way.", "Gives the moving, thickening sound of pulse width modulation."),
        "DCO PWM Src": ("Chooses what moves the pulse width: the manual knob, the LFO or the envelope.", "A manual width is static; the LFO gives chorus-like movement; the envelope makes it change with each note."),
        "DCO PW": ("Sets the pulse width from 50 percent (square) to 95 percent (thin) when the source is Manual.", "A square sounds hollow; thin pulses sound reedy and nasal."),
        "DCO Range": ("Chooses 16', 8' or 4' for the oscillator's register.", "Sounds an octave down, as written, or an octave up."),
        "DCO Pulse": ("Switches the pulse wave on or off.", "Adds a hollow, woody tone to the mix."),
        "DCO Saw": ("Switches the sawtooth wave on or off; it is on by default.", "Adds a bright, buzzy tone to the mix."),
        "DCO Sub": ("Sets the level of a square wave one octave down, from 0 to 100 percent.", "Adds weight and bass."),
        "DCO Noise": ("Sets the level of white noise into the filter, from 0 to 100 percent.", "Adds breath, hiss and a rough edge."),
        "HPF": ("Chooses a one-pole high-pass after the voices: off, or about 100, 200 or 400 Hz.", "Higher settings thin out the bass."),
        "VCF Freq": ("Sets the low-pass cutoff before modulation, from 30 Hz to 16 kHz.", "Low cutoffs sound dark and muffled; high ones open up and brighten."),
        "VCF Reso": ("Adds feedback at the cutoff, from 0 to 100 percent; the top self-oscillates.", "Low values are smooth; higher values whistle and squelch."),
        "VCF Env Amt": ("Sets how far the envelope opens the cutoff, up to 6 octaves.", "Notes start bright and settle dark: the classic pluck or brass swell."),
        "VCF Env Pol": ("Chooses whether the envelope opens (+) or closes (-) the filter.", "A negative envelope makes notes start dark and open later in reverse."),
        "VCF LFO": ("Sends the LFO to the cutoff, up to 4 octaves either way.", "Gives a wah-like sweep."),
        "VCF Key Trk": ("Sets how much the cutoff follows the note, up to 100 percent (an octave per octave around C4).", "At 0 high notes are duller; at 100 the tone stays the same across the keyboard."),
        "VCA Mode": ("Chooses whether the envelope shapes the level (Env) or the note simply holds at full level (Gate).", "Env gives plucks and swells; Gate gives an organ-like on and off."),
        "VCA Level": ("Sets the output level after the voices, from 0 to 100 percent.", "Turns the whole synth up or down."),
        "Env A": ("Sets the envelope's attack from 1 ms to 4 s.", "Short attacks are percussive; long ones swell in slowly."),
        "Env D": ("Sets the time the envelope takes to fall to the sustain level, from 5 ms to 8 s.", "Short decays pluck; long ones keep the note bright."),
        "Env S": ("Sets the level held while the key is down, from 0 to 100 percent.", "Low sustain makes plucks; high sustain holds pads."),
        "Env R": ("Sets how long the envelope takes to fall after the key is let go, from 5 ms to 8 s.", "Short releases cut off; long ones leave a tail."),
        "Chorus": ("Chooses the chorus: off, I (slow and lush), II (denser) or III (fast warble).", "Widens the sound into stereo and adds shimmer."),
    },
    "Pedal SH101": {
        "Range": ("Chooses 16', 8' or 4' for the oscillator's register.", "Sounds an octave down, as written, or an octave up."),
        "VCO Mod": ("Sends the LFO to pitch, up to 12 semitones.", "Gives vibrato, or at the top wild pitch sweeps."),
        "PWM Source": ("Chooses what moves the pulse width: manual, the LFO or the envelope.", "A manual width is static; the others make it move."),
        "PWM": ("Sets the pulse width, from a square to a narrow 10 percent pulse, or the depth of the moving source.", "A square sounds hollow; narrow pulses sound thin and reedy."),
        "Pulse Lvl": ("Sets the pulse wave's level in the mixer, from 0 to 100 percent.", "Adds a hollow, reedy tone."),
        "Saw Lvl": ("Sets the sawtooth's level in the mixer, from 0 to 100 percent.", "Adds a bright, buzzy tone."),
        "Sub Lvl": ("Sets the sub-oscillator's level, from 0 to 100 percent.", "Adds deep weight; at the top it can overload the output."),
        "Noise Lvl": ("Sets the noise level in the mixer, from 0 to 100 percent.", "Adds breath, hiss and percussion."),
        "Sub Type": ("Chooses the sub-oscillator's wave: a square one octave down, a square two octaves down, or a narrow pulse two octaves down.", "Changes the depth and thickness of the bass."),
        "Cutoff": ("Sets the ladder low-pass cutoff from 20 Hz to 20 kHz.", "Low cutoffs sound dark and muffled; high ones open up."),
        "Resonance": ("Adds feedback at the cutoff, from 0 to 100 percent; the top self-oscillates.", "Low values are smooth; higher values squelch and whistle."),
        "Env Amt": ("Sends the envelope to the cutoff from -5 to +5 octaves; negative values close the filter.", "Positive values give the classic acid zap; negative ones give a reversed sweep."),
        "VCF Mod": ("Sends the LFO to the cutoff, up to 4 octaves at 100 percent.", "Gives a wah-like sweep."),
        "Kbd Follow": ("Makes the cutoff follow the note: off, half or full.", "Without it, high notes sound duller than low ones."),
        "VCA Mode": ("Chooses whether the note holds at full level (Gate) or the envelope shapes it (Env).", "Gate sounds like an organ; Env gives plucks and swells."),
        "Attack": ("Sets the envelope's attack from 2 ms to 8 s.", "Short attacks are percussive; long ones swell in."),
        "Decay": ("Sets how long the envelope takes to fall to the sustain level, from 2 ms to 15 s.", "Short decays pluck; long ones keep the note open."),
        "Sustain": ("Sets the level held while the key is down, from 0 to 100 percent.", "Low sustain gives plucks; high sustain holds the note."),
        "Release": ("Sets how long the envelope takes to fall after the key is let go, from 2 ms to 15 s.", "Short releases cut off; long ones leave a tail."),
        "LFO Rate": ("Sets the LFO speed from 0.1 to 30 Hz.", "Slow rates drift; fast ones wobble."),
        "LFO Wave": ("Chooses the LFO shape: triangle, square, sample and hold, or noise.", "Smooth shapes sway; square and random jump."),
        "LFO Delay": ("Waits 0 to 2 s after a note starts before the LFO begins.", "The note starts steady and the movement arrives later."),
        "Glide": ("Sets the portamento time between overlapping notes, from off to 2 s.", "Notes slide into each other, bass-line style."),
        "Tune": ("Fine-tunes the whole synth by up to 50 cents either way.", "Matches it to another instrument or detunes it for warmth."),
        "Volume": ("Sets the output level from 0 to 100 percent.", "Turns the synth up or down; keep it down when the sub is high."),
    },
    "Pedal FM": {
        **_ops(),
        "Algorithm": ("Chooses how the four operators connect, from a single chain to side-by-side pairs and fully additive.", "Changes which operators modulate which, so the same settings sound completely different."),
        "Output": ("Sets the master level on a square-law curve.", "Turns the whole synth up or down."),
        "Voices": ("Limits how many notes sound at once, from 1 to 16.", "More voices allow bigger chords; extra notes steal the oldest."),
        "Op1 Feedback": ("Feeds operator 1 back into itself, from 0 to 127.", "A pure sine at 0 becomes brighter and then sawtooth-like, then noisy."),
        "Compound Mod": ("Scales the modulators that feed other modulators, from 0 to 100 percent.", "Keeps long chains from getting harsh; higher values sound more aggressive."),
        "Glide": ("Sets the portamento time between overlapping notes, up to 1 s.", "Notes slide into each other."),
        "LFO Rate": ("Sets the LFO speed from 0.05 to 30 Hz.", "Slow rates drift; fast ones wobble."),
        "LFO Shape": ("Chooses the LFO wave: sine, triangle, square, saw or sample and hold.", "Smooth shapes sway; square and sample and hold jump."),
        "LFO Pitch": ("Sends the LFO to the pitch, up to one semitone.", "Gives vibrato."),
        "LFO Mod": ("Sends the LFO to the voice level, up to 50 percent.", "Gives tremolo and moving timbre."),
        "t:Velocity": ("Sets the velocity for this track's notes, from 0 to 127; it sticks for the later notes.", "Harder notes are louder or brighter, by each operator's velocity sensitivity."),
    },
    "Pedal M1": {
        **_m1(),
        "OSC Mode": ("Chooses whether oscillator 1, oscillator 2 or both sound.", "Using both layers two samples; using one plays a single sample."),
        "Portamento": ("Sets the glide time between notes from off to 8 s.", "Notes slide into each other."),
        "Cutoff": ("Sets the two-pole low-pass cutoff from 20 Hz to 18 kHz.", "Low cutoffs sound dark and muffled; high ones leave the sample untouched."),
        "Resonance": ("Adds a gentle boost at the cutoff, from 0 to 100 percent.", "Gives a little colour to sweeps; it never self-oscillates."),
        "Key Track": ("Makes the cutoff follow the note around C4, from -100 to +98 percent.", "With tracking the tone stays alike across the keyboard; without, high notes are duller."),
        "Filter EG Int": ("Sends the filter envelope to the cutoff, up to 6 octaves.", "Notes open from dark to bright."),
        "Filter Vel": ("Makes velocity move the cutoff by up to one octave around velocity 64.", "Harder notes sound brighter."),
        "Filter Attack": ("Sets the filter envelope's attack from 1 ms to 8 s.", "Short attacks open at once; long ones sweep slowly."),
        "Filter Decay": ("Sets how long the filter envelope takes to fall to its sustain level, from 1 ms to 8 s.", "Short decays pluck the brightness; long ones hold it."),
        "Filter Sustain": ("Sets the level the filter envelope holds, from 0 to 100 percent.", "Low values let the note settle dark; high values keep it bright."),
        "Filter Release": ("Sets how long the filter envelope takes to fall after the key is let go, from 1 ms to 8 s.", "Short releases close the filter at once; long ones fade the brightness."),
        "Amp Attack": ("Sets the amplifier envelope's attack from 1 ms to 8 s.", "Short attacks are immediate; long ones swell in like strings."),
        "Amp Decay": ("Sets how long the amplifier envelope takes to fall to its sustain level, from 1 ms to 8 s.", "Short decays pluck; long ones keep the note ringing."),
        "Amp Sustain": ("Sets the level the amplifier envelope holds, from 0 to 100 percent.", "Low values make plucks; full level holds the note."),
        "Amp Release": ("Sets how long the amplifier envelope takes to fall after the key is let go, from 1 ms to 8 s.", "Short releases cut off; long ones leave a tail."),
        "Amp Vel": ("Sets how much velocity scales the level, from 0 to 100 percent.", "At 0 every note is equally loud; higher values make soft notes quieter."),
        "Volume": ("Sets the master output from 0 to 100 percent; the machine runs about 6 dB below other synths.", "Raise it to match the rest of the mix."),
        "t:Volume": ("Sets the velocity of this track's notes, from 0 to 127 (127 is full).", "Quieter and duller notes at lower values, through Amp Vel and Filter Vel."),
    },
}


# ---- the nine generated manifests' curation -----------------------------------------------------------------------

def _src(gear_dir, file, what):
    return f"{PEDAL}{gear_dir}/{file} — {what}"


CURATE = {
    "Pedal Gate": {
        "type": "dynamics",
        "params": {
            "Threshold": ("Open threshold", "dyn.threshold"), "Hysteresis": ("Close hysteresis", ""), "Attack ms": ("Attack", "dyn.attack"),
            "Hold ms": ("Hold", ""), "Release ms": ("Release", "dyn.release"), "Range dB": ("Closed-gate floor", ""),
            "Detection": ("Detection", "dyn.detection"), "Lookahead ms": ("Lookahead", "dyn.lookahead"), "Bypass": ("Bypass", "bypass"),
        },
        "quirks": [
            "The code, not its README, is right about Threshold: 0.5 dB steps from -60 to 0 dBFS (the README says 1 dB steps and -80).",
            "The gate closes Hysteresis dB under Threshold, holds for Hold, then closes over Release.",
            "Lookahead delays the output by the lookahead time (up to 25 ms); at 0 the gate reacts to a transient only as it arrives.",
            "It is a hard gate: a closed gate sits at Range (0 is -90 dB, almost shut; 90 is no reduction).",
        ],
        "evidence": [
            _src("pedal-gate", "PedalGate.cs", "parameters and the gate's state machine (threshold, hysteresis, hold, release, range, lookahead)"),
            _src("pedal-gate", "README.md", "features and state machine"),
            "profile (tools/machine-profiler.py, linux 2026-11-13) — dynamics, healthy, no tail",
        ],
    },
    "Pedal Gain": {
        "type": "utility",
        "params": {"Gain": ("Gain", "level"), "Mute": ("Mute", ""), "Inertia": ("Mute fade time", "")},
        "quirks": [
            "Gain is a percentage of the input: 100 is unity, 200 is +6 dB, 50 is -6 dB.",
            "Mute and un-mute fade linearly over Inertia (0 is an instant cut).",
            "Above 100 percent a loud input can overload what follows: there is no limiter.",
        ],
        "evidence": [
            _src("pedal-gain", "PedalGainMachine.cs", "parameters and the gain and mute ramps"),
            _src("pedal-gain", "README.md", "parameters and how Inertia works"),
            "profile (tools/machine-profiler.py, linux 2026-11-13) — utility, 0 dB at the defaults",
        ],
    },
    "Pedal Gain Multi": {
        "type": "utility",
        "params": {
            "Gain": ("Master fader", "level"), "Master Mute": ("Master mute", ""), "Inertia": ("Mute and solo fade time", ""),
            "Solo": ("Track solo", "solo"), "Mute": ("Track mute", ""), "Volume": ("Track fader", "level"), "Pan": ("Track pan", "pan"), "Mono": ("Track mono", ""),
        },
        "quirks": [
            "One stereo input per track (up to 24, 6 on a new machine), mixed to the master output; each track also has its own direct output.",
            "Solo affects the master mix only; Mute affects the master mix and the direct out; the master Gain and Master Mute never touch a direct out.",
            "Faders are 0.5 dB per step: 0 is silence, 1 is -80 dB, 161 is 0 dB, 181 is +10 dB.",
            "Pan is a balance control on stereo inputs and a constant-power pan on Mono ones.",
            "Not compatible with 1.x songs: the parameter layout changed in 2.0 and old settings do not carry over.",
        ],
        "evidence": [
            _src("pedal-gain-multi", "PedalGainMultiMachine.cs", "parameters, the level table and the channel strip"),
            _src("pedal-gain-multi", "README.md", "parameters, mixer topology and upgrade note"),
            "profile (tools/machine-profiler.py, linux 2026-11-13) — utility; hot only with a fader at its maximum",
        ],
    },
    "Pedal Shaper": {
        "type": "distortion",
        "params": {
            "Drive": ("Drive", "drive"), "Shape": ("Shape", "drive.type"), "Tone": ("Tilt tone", "drive.tone"), "Bias": ("DC bias", ""),
            "Mix": ("Mix", "mix.wet"), "Output": ("Output gain", "level"), "Hysteresis": ("Release smear", ""), "Dust": ("Dust", ""),
        },
        "quirks": [
            "The profiler typed it a utility (it measures flat at the defaults, where Drive is 0 dB); it is a waveshaping distortion.",
            "Its own value texts are bare numbers: the manifest gives Drive and Output in dB from the code ((value - 48) x 0.5 dB).",
            "No oversampling: high notes driven hard alias, most with Hard, Fold and Crush.",
            "Output at its maximum (+24 dB) is hot (+14.6 dBFS peak in the profile).",
        ],
        "evidence": [
            _src("pedal-shaper", "PedalShaper.cs", "parameters, the five curves, the tilt EQ, bias, hysteresis and dust"),
            _src("pedal-shaper", "README.md", "shapes and texture controls"),
            "profile (tools/machine-profiler.py, linux 2026-11-13) — flat at the defaults, hot with Drive or Output at the maximum",
        ],
    },
    "Pedal MComp": {
        "type": "dynamics",
        "params": {
            "Listen": ("Band listen", "solo"), "Xover L-LM": ("Low crossover", "dyn.crossover"), "Xover LM-HM": ("Mid crossover", "dyn.crossover"),
            "Xover HM-H": ("High crossover", "dyn.crossover"), "Knee": ("Knee width", "dyn.knee"), "Detection": ("Detection", "dyn.detection"),
            **{f"{b} {w}": (f"{n} {lw}", role) for b, n in (("L", "Low"), ("LM", "Low-mid"), ("HM", "High-mid"), ("H", "High"))
               for w, lw, role in (("Threshold", "threshold", "dyn.threshold"), ("Ratio", "ratio", "dyn.ratio"), ("Attack", "attack", "dyn.attack"),
                                   ("Release", "release", "dyn.release"), ("Makeup", "makeup", "dyn.makeup"), ("Bypass", "bypass", "bypass"))},
            "Output Gain": ("Output gain", "level"), "Dry-Wet": ("Mix", "mix.wet"), "Lookahead": ("Lookahead", "dyn.lookahead"),
            "Phase Linear": ("Phase-linear crossovers", ""), "Spectrum View": ("Spectrum view", ""),
        },
        "quirks": [
            "Four bands split by 24 dB per octave crossovers; each band has its own threshold, ratio, attack, release, makeup and bypass, while the knee and detection are shared.",
            "Ratio runs 1:1 to 40:1 on a log scale and its top step is a limiter (infinite).",
            "Lookahead (off to 10 ms) and Phase Linear (a few ms of delay) add latency; Lookahead's text names the delay.",
            "Output Gain at its maximum (+24 dB), or a band's Makeup at its maximum, is hot (up to +17.5 dBFS peak in the profile).",
            "Spectrum View only draws the analyser in the machine's window; it does not change the sound.",
        ],
        "evidence": [
            _src("pedal-mcomp", "PedalMComp.cs", "parameters, the crossover tree and the band compressors (ratio, attack and release maps)"),
            _src("pedal-mcomp", "README.md", "signal flow and parameter table"),
            "profile (tools/machine-profiler.py, linux 2026-11-13) — dynamics; hot with makeup or output gain at the maximum",
        ],
    },
    "Pedal Tracker": {
        "type": "sampler",
        "params": {
            "Master": ("Master level", "level"), "Mute": ("Master mute", ""), "Mute Inertia": ("Mute fade time", ""),
            **{f"Mute Track {n}": (f"Mute track {n}", "") for n in range(1, 17)},
            "Interpolation": ("Interpolation", ""),
            "Wave": ("Wave slot", "osc.wave"), "Volume": ("Track volume", "velocity"), "Cmd": ("Effect command", ""), "Arg": ("Effect argument", ""),
            "Cmd2": ("Second effect command", ""), "Arg2": ("Second effect argument", ""),
        },
        "quirks": [
            "It plays the song's wavetable: it is silent until a slot holds a wave (wave_import), and each track picks its slot with Wave.",
            "A track's Volume is a square-law scale (amplitude = (value / 128) squared): 128 is unity, 254 about +11.9 dB.",
            "Each track has two effect columns (Cmd and Arg, Cmd2 and Arg2) that run side by side: pitch slides, tone portamento, vibrato, sample offset, volume slides and a few fine commands.",
            "Master is a percentage of unity: 100 percent is value 32768, up to 200 percent.",
            "The muting parameters fade over Mute Inertia while the voices keep running, so a muted track stays in step with the song.",
        ],
        "evidence": [
            _src("pedal-tracker", "PedalTracker.cs", "parameters, the track columns, the volume curve and the mute fades"),
            _src("pedal-tracker", "README.md", "pattern columns, global parameters and effect commands"),
            "profile (tools/machine-profiler.py, linux 2026-11-13) — sampler with a test wave",
        ],
    },
    "Pedal Chord": {
        "type": "control",
        "params": {
            "Velocity": ("Velocity", "velocity"), "Chord": ("Chord type", ""), "Mode": ("Chord or arpeggio mode", ""), "Speed": ("Arpeggio step length", ""),
            "Length": ("Note length", "note.length"), "Octaves": ("Octave range", ""), "Step": ("Arpeggio step", ""), "Oct Walk": ("Octave walk", ""),
            "Swing": ("Swing", "groove.shuffle"), "Swing On": ("Swing on", ""), "Humanize": ("Timing humanize", ""), "Hum. Vel": ("Velocity humanize", ""),
        },
        "quirks": [
            "It makes no sound of its own: it plays notes on a target generator, which must be set in the machine's own target dialog (no tool sets it).",
            "Chord mode uses consecutive tracks of the target, one per chord tone; arpeggio modes play on one target track.",
            "Length 0 holds each note until the next one; Speed is in pattern ticks, so it follows the song's tempo.",
            "Its Note column (the root note) is not in the parameter list; write it with notes_write like any note.",
        ],
        "evidence": [
            _src("pedal-chord", "PedalChord.cs", "parameters, the 51 voicings, the arpeggio modes and swing"),
            _src("pedal-chord", "README.md", "track parameters, speed and timing, step and octave walk"),
            "profile (tools/machine-profiler.py, linux 2026-11-13) — control machine, silent",
        ],
    },
    "Pedal LFO": {
        "type": "control",
        "params": {
            "Shape": ("LFO shape", "lfo.wave"), "Period": ("Cycle length", "lfo.rate"), "Depth": ("Depth", "lfo.depth"), "Center": ("Centre", ""),
            "Phase": ("Phase offset", "lfo.phase"), "Sync": ("Tempo sync", "lfo.sync"), "Bypass": ("Bypass", "bypass"), "Start": ("Start", ""),
        },
        "quirks": [
            "It makes no sound of its own: it writes values into one parameter of another machine, chosen in its own target dialog (no tool sets it).",
            "The output is the target's minimum plus Center and a swing of Depth, both as percentages of the target's range, rounded to the target's steps.",
            "Period is in pattern ticks (16 is a bar at 4 ticks per beat); with Sync set to a note length it follows the song's tempo instead.",
            "Start off holds the target at Center; switching it on restarts the wave from phase 0.",
        ],
        "evidence": [
            _src("pedal-lfo", "PedalLFO.cs", "parameters and the output mapping"),
            _src("pedal-lfo", "README.md", "parameters and output mapping"),
            "profile (tools/machine-profiler.py, linux 2026-11-13) — control machine, silent",
        ],
    },
    "Pedal Peer Control": {
        "type": "control",
        "params": {"Inertia": ("Glide time", ""), "Slaved": ("Slaved", ""), "Value": ("Value", "")},
        "quirks": [
            "It makes no sound of its own: each of up to 64 tracks sends its Value through a mapping curve to a parameter of another machine, or out as a MIDI controller; the targets and curves are set in its own settings window (no tool sets them).",
            "The profile warns that it makes no sound from a MIDI note: it has no Note column and is not an instrument.",
            "Value runs 0 to 65534 (percent of the target's range through the curve); Inertia is a glide in tenths of a tick (1280 is 128 ticks).",
            "A slaved track mirrors the one before it, so a group of tracks can follow one control.",
        ],
        "evidence": [
            _src("pedal-peer-control", "PeerCtrl.cs", "parameters, assignments, the mapping curve and inertia"),
            _src("pedal-peer-control", "README.md", "parameters and what the controller does"),
            "profile (tools/machine-profiler.py, linux 2026-11-13) — control machine, silent",
        ],
    },
}


def curate(m):
    """The generated manifest dict made curated (in place): type, quirks, evidence, plain names and roles."""
    c = CURATE.get(m["gear"])
    if c is None:
        return m
    out = {}
    for k, v in m.items():
        if k == "status":
            v = "curated"
        if k == "source":
            v = v + "; curated by tools/pedal_knowledge.py"
        if k == "type":
            v = c["type"]
        out[k] = v
        if k == "summary":
            out["quirks"] = list(c["quirks"])
            out["evidence"] = list(c["evidence"])
    if "type" not in out:
        out["type"] = c["type"]
    m.clear()
    m.update(out)
    seen = set()
    for group in ("parameters", "track_parameters"):
        for i, e in enumerate(m.get(group, [])):
            plain, role = c["params"][e["name"]]
            new = {}
            for k, v in e.items():
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
            key = ("t:" + e["name"]) if group == "track_parameters" and ("t:" + e["name"]) in texts else e["name"]
            if key not in texts:
                raise SystemExit(f"{m['gear']}: no Explain text for {group[:-1]} {e['name']!r}")
            how, sound = texts[key]
            used.add(key)
            for k in ("how", "sound"):
                e.pop(k, None)
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
    for gear in HAND_CURATED:
        path = path_of(gear)
        with open(path, encoding="utf-8-sig") as f:
            before = f.read()
        m = json.loads(before)
        text = dump(explain(m))
        if text != before:
            if write:
                with open(path, "w", encoding="utf-8", newline="\n") as f:
                    f.write(text)
            else:
                problems.append(f"{gear}: its Explain text is not what tools/pedal_knowledge.py says (python3 tools/pedal_knowledge.py --write)")
    return problems


if __name__ == "__main__":
    found = run("--write" in sys.argv[1:])
    for p in found:
        print(p)
    print(("wrote" if "--write" in sys.argv[1:] else "checked") + f" {len(HAND_CURATED)} manifests, {len(found)} problems")
    sys.exit(1 if found else 0)
