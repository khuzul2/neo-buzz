#nullable disable
// Neo Patch NP2 (docs/specs/neo-patch.md §4.5): the words of the inner canvas. Every node kind has a one-line summary, every control has
// Explain How and Sound text (the K1 rule: neo-learn.md §5.3), and every port says what it carries and what a wire into it does. The side
// panel shows them; the Learn overlay names a wire's signal from them. A test checks that nothing is missing (NodeHelpIsComplete).
// Our own words: no product names.

using System;
using System.Collections.Generic;

namespace NeoBuzz.Patch
{
    /// <summary>What the editor says about one node kind.</summary>
    internal sealed class NodeDoc
    {
        public readonly string Summary;
        /// <summary>One per control (NodeInfo.Controls), in order.</summary>
        public readonly string[] How, Sound;
        /// <summary>One per input and per output port, in order.</summary>
        public readonly string[] Ins, Outs;

        public NodeDoc(string summary, string[] controls, string[] ins, string[] outs)
        {
            Summary = summary;
            How = new string[controls.Length];
            Sound = new string[controls.Length];
            for (int i = 0; i < controls.Length; i++)
            {
                int bar = controls[i].IndexOf('|');
                How[i] = controls[i].Substring(0, bar).Trim();
                Sound[i] = controls[i].Substring(bar + 1).Trim();
            }
            Ins = ins;
            Outs = outs;
        }
    }

    internal static class NodeHelp
    {
        static readonly string[] None = new string[0];

        static readonly NodeDoc[] docs = Build();

        public static NodeDoc For(NodeKind kind) => docs[(int)kind];

        /// <summary>The words for a port type, as the Learn overlay says them.</summary>
        public static string TypeWords(PortType t) => t switch
        {
            PortType.Audio => "Audio: a sound, a wave that swings between -1 and +1 many thousand times a second.",
            PortType.Control => "Control: a level that moves slowly (an envelope, an LFO, a knob), usually 0 to 1; it is read every 16 samples unless Audio-rate mod is on.",
            PortType.Gate => "Gate: 1 while a key is held, 0 when it is let go; it starts and stops envelopes and resets LFOs.",
            _ => "Pitch: a note number in semitones (69 is the A at 440 Hz, 60 is middle C); one unit is one semitone.",
        };

        static NodeDoc[] Build()
        {
            var d = new NodeDoc[NodeInfo.KindCount];
            d[(int)NodeKind.Keys] = new NodeDoc("The notes you play, one copy for each sounding voice: pitch, gate, velocity, pressure and slide.",
                None, None,
                new[]
                {
                    "The note being played, as a note number in semitones (69 = A at 440 Hz), with pitch bend and glide. Wire it to an oscillator's Pitch.",
                    "1 while the key is held, 0 after it is let go. Wire it to an envelope's Gate to shape each note.",
                    "How hard the key was struck, 0 to 1. Wire it to a VCA's CV or a filter's Cutoff to make loud notes brighter or louder.",
                    "Pressure on the key after it is down (aftertouch or MPE pressure), 0 to 1. Wire it to vibrato depth or brightness.",
                    "The MPE slide of the note (CC 74, or the mod wheel on the main channel), 0 to 1. Wire it to a filter or a crossfade.",
                });
            d[(int)NodeKind.Osc] = new NodeDoc("An oscillator: a band-limited saw, square, sine or triangle at the pitch it is given.",
                new[]
                {
                    "Picks the wave: saw, square (pulse), sine or triangle.|Saw is bright and buzzy, square hollow and woody, triangle soft and flute-like, sine pure with no overtones.",
                    "Shifts the pitch in semitones, up to 8 octaves either way.|+12 is an octave up; +7 a fifth. Two oscillators a fifth or an octave apart sound big and stacked.",
                    "Shifts the pitch in cents, a hundredth of a semitone.|A few cents between two oscillators makes them beat slowly and sound wide and alive.",
                    "How strongly the FM input bends this oscillator's pitch, in modulation index.|Low values add a bright edge or shimmer; high values turn the tone metallic, bell-like and clangorous.",
                    "Sets the square wave's width, from thin (5 %) to wide (95 %); the Pulse input moves it.|A centred pulse is hollow; narrow pulses sound thin and nasal. Moving it slowly gives a chorus-like sweep.",
                    "Scales the oscillator's output.|Lower it to blend oscillators in a mix or to keep a hot tone from overloading the next node.",
                    "Adds a slow random wander to the pitch.|A little drift makes a patch sound analog and imperfect; none is exact and digital.",
                    "How far the PM input bends the phase, in cycles of the wave.|Phase modulation sounds like FM that stays in tune; larger amounts give hard, glassy overtones.",
                },
                new[]
                {
                    "The note to play, in semitones (69 = 440 Hz). Unwired, the oscillator plays A at 440 Hz. A wire adds to the oscillator's own pitch.",
                    "Frequency modulation: another audio signal bends this oscillator's pitch, scaled by the FM control.",
                    "Phase modulation: another audio signal pushes the wave's phase, scaled by the PM control.",
                    "Hard sync: each rising crossing of this signal restarts the wave, which gives the tearing sync-lead sound.",
                    "Moves the square wave's width (pulse-width modulation).",
                },
                new[] { "The oscillator's sound." });
            d[(int)NodeKind.Envelope] = new NodeDoc("A delay, attack, hold, decay, sustain and release envelope, one for each voice, started by a gate.",
                new[]
                {
                    "How long the level takes to rise from 0 to 1 after the key goes down.|Short attacks are plucky and percussive; long ones fade in like a pad or a bowed string.",
                    "How long the level takes to fall from 1 to the sustain level.|Short decays give a pluck or a blip; long ones a slow bloom that settles.",
                    "The level held while the key stays down, 0 to 1.|0 makes every note die away even when held; 1 keeps full level for as long as the key is down, like an organ.",
                    "How long the level takes to fall to 0 after the key is let go.|Short releases stop the note at once; long ones let it ring out and blur into the next.",
                    "A wait between the gate and the start of the attack.|A delay lets one part of a sound come in late, such as a filter that opens after the note starts.",
                    "How long the peak is held before the decay begins.|A hold keeps a hit at full level for a moment, which makes notes sound firm and blocky.",
                    "Bends the segments: negative is snappy (fast then slow), positive is slow then fast.|Snappy curves sound punchy and natural; positive curves swell and feel languid.",
                },
                new[] { "The gate that starts and releases the envelope. Wire it from Keys, or from an LFO or Random for a rhythm." },
                new[] { "The envelope level, 0 to 1. Wire it to a VCA's CV for loudness, or to a filter's Cutoff or an oscillator's FM for movement." });
            d[(int)NodeKind.Filter] = new NodeDoc("A resonant filter: state-variable low-pass, band-pass, high-pass and notch, and a 12 or 24 dB ladder.",
                new[]
                {
                    "Picks the filter: low-pass 12, band-pass, high-pass, notch, ladder 24 or ladder 12.|Low-pass darkens; high-pass thins; band-pass sounds nasal; notch phases; the ladder is warmer and thicker.",
                    "Sets the frequency where the filter starts to act, from 20 Hz to 20 kHz.|Lowering a low-pass makes the sound duller; raising it opens it up and brightens it.",
                    "Emphasises the frequencies around the cutoff (Q, 0.5 to 20).|High values ring and squeal at the cutoff and sound sharp; low values are gentle and neutral.",
                    "How many octaves the Cutoff input moves the cutoff for each unit of signal.|Larger values make envelopes and LFOs sweep the filter more widely, so the sweep is easier to hear.",
                },
                new[]
                {
                    "The sound to filter.",
                    "Moves the cutoff: each unit of signal shifts it by the Cutoff mod amount, in octaves. Wire an envelope or an LFO here.",
                    "Moves the resonance up or down.",
                },
                new[] { "The filtered sound." });
            d[(int)NodeKind.Vca] = new NodeDoc("A voltage-controlled amplifier: it multiplies a sound by a level.",
                new[] { "The gain the sound is multiplied by, 0 to 2, before the CV input scales it.|With a CV wired it is the ceiling; unwired it is a plain volume knob. Above 1 it makes the sound louder." },
                new[]
                {
                    "The sound to amplify.",
                    "The level that multiplies the sound, usually an envelope. Unwired, the VCA passes the sound at its Gain.",
                },
                new[] { "The sound at the new level." });
            d[(int)NodeKind.Out] = new NodeDoc("The patch's stereo output: four inputs, each with its own pan, a level and a spread across the voices.",
                new[]
                {
                    "The master level of this output, 0 to 2.|Use it to set how loud the whole patch is before the machine's Volume and the safety limiter.",
                    "Where input 1 sits in the stereo picture, left (-1) to right (+1).|Centre is solid and mono; the sides give a wide, spread sound.",
                    "Where input 2 sits in the stereo picture.|Pan two inputs apart for a simple stereo layer.",
                    "Where input 3 sits in the stereo picture.|Pan it opposite input 2 for a symmetrical pair.",
                    "Where input 4 sits in the stereo picture.|The fourth input gives room for a sub or a noise layer at the centre.",
                    "Gives every voice its own place in the stereo picture, 0 to 1.|At 0 all voices share the pan; at 1 chords fan out across the speakers and sound wide.",
                },
                new[] { "Sound to send to the output, panned by Pan 1.", "Sound to send to the output, panned by Pan 2.", "Sound to send to the output, panned by Pan 3.", "Sound to send to the output, panned by Pan 4." },
                None);
            d[(int)NodeKind.Feedback] = new NodeDoc("The only way to loop: it hands its input back one block (64 samples) later, so a loop can run.",
                new[] { "Scales the signal fed back, 0 to 2.|Below 1 a loop fades and settles; at 1 it sustains; above 1 it grows until the safety limiter holds it, so use it carefully." },
                new[] { "The sound or control to send around the loop. The delay of one block lets a node use its own output." },
                new[] { "The input from one block ago, times Gain. Wire it into a node earlier in the loop." });
            d[(int)NodeKind.GlobalKeys] = new NodeDoc("The last note played, one copy for the whole patch: for mono patches and for global LFOs.",
                None, None,
                new[]
                {
                    "The last note held, as a note number in semitones, with the newest key taking priority.",
                    "1 while any key is held, 0 when all are let go.",
                    "The velocity of the newest held key, 0 to 1.",
                });
            d[(int)NodeKind.WavetableOsc] = new NodeDoc("A wavetable oscillator: it morphs through a table of waves, read band-limited.",
                new[]
                {
                    "Picks one of the built-in wave tables.|Each table has its own character and its own way of changing as Position moves.",
                    "Shifts the pitch in semitones, up to 8 octaves either way.|+12 is an octave up; two tables an octave apart sound thick.",
                    "Shifts the pitch in cents.|A few cents of detune between two oscillators makes them beat and sound wide.",
                    "How strongly the FM input bends the pitch, in modulation index.|Adds a metallic, bell-like edge at higher values.",
                    "Moves through the table, from its first wave (0) to its last (1); the Position input moves it too.|Sweeping it makes the tone change shape: from soft to buzzy or from one vowel to another.",
                    "Scales the oscillator's output.|Lower it to blend several sources or to keep the next node from overloading.",
                    "Adds a slow random wander to the pitch.|A little makes the tone analog and warm.",
                    "How far the PM input bends the phase, in cycles.|Gives glassy, harmonically rich tones that stay in tune.",
                },
                new[]
                {
                    "The note to play, in semitones (69 = 440 Hz). Unwired, it plays A at 440 Hz.",
                    "Frequency modulation: another audio signal bends this oscillator's pitch, scaled by FM.",
                    "Phase modulation: another audio signal pushes the phase, scaled by PM.",
                    "Hard sync: each rising crossing of this signal restarts the wave.",
                    "Moves the table position; it adds to the Position control.",
                },
                new[] { "The oscillator's sound." });
            d[(int)NodeKind.Noise] = new NodeDoc("A noise source: white (all frequencies equal) or pink (softer highs).",
                new[]
                {
                    "Picks white noise, bright and hissy, or pink noise, which falls off 3 dB each octave and sounds fuller.|White is a hiss for hats and breath; pink is more like wind or rain and sits well under a pad.",
                    "Scales the noise, 0 to 1.|A little noise adds air and grit to a tone; a lot makes a percussion hit or a wind.",
                },
                None, new[] { "The noise." });
            d[(int)NodeKind.Sample] = new NodeDoc("Plays a sample, one-shot or looped, at the pitch it is given; the built-in sounds or your own WAV.",
                new[]
                {
                    "Picks the sample: one of the built-in sounds, or \"user\" for the WAV loaded into the machine.|The source decides the whole character; try a short hit for plucks or a long one for a pad.",
                    "Shifts the pitch in semitones.|Transposes the sample; large shifts change its length and its colour, like speeding a tape up or down.",
                    "Scales the sample's output, 0 to 1.|Lower it to blend the sample under other sources.",
                    "One-shot plays the sample once; loop plays it again from the start for as long as the key is held.|Loops make sustained tones from a short sample; one-shots keep drum hits natural.",
                    "Where in the sample playing starts, 0 (the beginning) to 1 (the end).|Moving it past the attack makes the sound softer; sweeping it gives a scanning effect.",
                    "On: the sample follows the note's pitch. Off: it always plays at its original pitch.|On makes an instrument; off makes a drum that plays the same whatever key is pressed.",
                },
                new[] { "The note to follow, in semitones, when Follow is on.", "Starts the sample when the gate rises." },
                new[] { "The sample's sound." });
            d[(int)NodeKind.Lfo] = new NodeDoc("A low-frequency oscillator: a slow wave for vibrato, tremolo and sweeps.",
                new[]
                {
                    "Picks the wave: sine, triangle, saw up, saw down, square, smooth random or sample and hold.|Sine and triangle are smooth; saws ramp; square jumps; the random shapes wander or step.",
                    "Sets how fast the wave repeats, from 0.01 to 100 Hz.|Below 1 Hz it is a slow swell; around 5 Hz vibrato or tremolo; above 20 Hz it begins to buzz.",
                    "Scales the wave, 0 to 1.|Less depth gives a subtle movement; full depth swings the whole range of whatever it is wired to.",
                    "Shifts the whole wave up or down, -1 to +1.|An offset sets the middle of the movement, so a filter can sweep around a chosen point.",
                    "Where in the wave it starts, 0 to 1.|Different phases make several LFOs move out of step with each other.",
                    "On: every new note restarts the wave. Off: it runs freely.|Retriggering makes each note identical; free running gives a different movement each time.",
                    "How many octaves the Rate input moves the speed for each unit of signal.|Lets an envelope or another LFO speed this one up or slow it down.",
                },
                new[] { "Moves the speed of the LFO, in octaves per unit of signal.", "A gate that restarts the wave on its rising edge." },
                new[] { "The wave, from -1 to +1 around the offset. Wire it to a Cutoff, a Pitch or a VCA's CV." });
            d[(int)NodeKind.Slew] = new NodeDoc("A slew limiter: it makes a control signal glide instead of jumping.",
                new[]
                {
                    "How long a rising signal takes to catch up, 0 to 10 s.|Short rises keep the attack of a step; long ones give a slow swell.",
                    "How long a falling signal takes to catch up, 0 to 10 s.|Short falls drop at once; long ones make a gentle tail and smooth out steppy modulation.",
                },
                new[] { "The control signal to smooth." }, new[] { "The smoothed signal." });
            d[(int)NodeKind.SampleHold] = new NodeDoc("Sample and hold: it catches the input's value when triggered and holds it still until the next trigger.",
                new[]
                {
                    "Sample holds the value caught at each trigger; track follows the input while triggered and holds it after.|Sample gives steps; track gives a stuttering tracking effect.",
                    "How often it triggers by itself when nothing is wired to Trigger, from 0.1 to 100 Hz.|Slow rates give a slow staircase; fast ones a rough, ringing movement.",
                },
                new[] { "The signal to catch, often noise or an LFO.", "A gate whose rising edge takes a new sample; unwired, the Rate does it." },
                new[] { "The held value." });
            d[(int)NodeKind.Random] = new NodeDoc("A random control signal: a new random value at a set rate, with optional smoothing.",
                new[]
                {
                    "How often a new random value is chosen when no Trigger is wired, 0.1 to 100 Hz.|Slow rates drift; fast ones jitter.",
                    "Smooths the steps into curves, 0 to 1.|0 jumps between values; 1 glides softly between them.",
                    "Scales the random range, 0 to 1.|Less depth gives a small, subtle wander.",
                    "Shifts the whole range up or down, -1 to +1.|Sets the middle the randomness moves around.",
                },
                new[] { "A gate whose rising edge picks a new value." }, new[] { "The random control signal." });
            d[(int)NodeKind.Shaper] = new NodeDoc("A waveshaper: it bends a sound's wave to add overtones, by soft clipping, folding or hard clipping.",
                new[]
                {
                    "Picks soft clip (rounded), fold (the wave folds back on itself) or hard clip (flat tops).|Soft clip is warm; fold is complex and bright; hard clip is harsh and buzzy.",
                    "How hard the sound is driven into the shaper, 1 to 16 times.|More drive adds more overtones and a denser, brighter tone; at the top it is heavily distorted.",
                    "Pushes the wave off centre, -1 to +1, before it is shaped.|A bias makes the shaping lopsided, adding even harmonics and a richer, reedier colour.",
                    "How many times the sound passes through the shaper, 1 to 4.|More stages stack the effect: each fold folds again, giving denser, more metallic tones.",
                    "Scales the output, 0 to 2.|Shaping changes loudness; use the level to bring it back to where it was.",
                },
                new[] { "The sound to shape.", "Moves the drive; it adds to the Drive control.", "The note's pitch, in semitones, used to limit folding so that high notes do not alias." },
                new[] { "The shaped sound." });
            d[(int)NodeKind.Delay] = new NodeDoc("A delay line, up to 2 seconds: echoes, combs, flanging and plucked strings.",
                new[]
                {
                    "How long the echo takes to come back, 0.05 ms to 2 s.|Under about 20 ms it makes a comb filter with a pitch of its own; longer times are echoes.",
                    "How much of the echo is fed back into the line, -0.98 to +0.98.|High values give many repeats or a long ringing note; negative values change which overtones ring.",
                    "A low-pass in the loop, 200 Hz to 20 kHz.|Lower values make each repeat darker, as a string loses its highs while it rings.",
                    "Balance between the dry sound (0) and the delayed sound (1).|Echoes and flangers usually sit around the middle; at 1 only the delayed sound is heard.",
                    "How many octaves the Time input moves the delay for each unit of signal.|Lets an LFO sweep the time for flanging, chorus and tape-like pitch wobble.",
                },
                new[] { "The sound to delay.", "Moves the delay time, in octaves per unit of signal." },
                new[] { "The dry and delayed sound, mixed." });
            d[(int)NodeKind.Mixer] = new NodeDoc("A four-input mixer with a gain for each input.",
                new[]
                {
                    "Gain for input 1, 0 to 2.|Sets how much of input 1 is in the mix.",
                    "Gain for input 2, 0 to 2.|Sets how much of input 2 is in the mix.",
                    "Gain for input 3, 0 to 2.|Sets how much of input 3 is in the mix.",
                    "Gain for input 4, 0 to 2.|Sets how much of input 4 is in the mix.",
                },
                new[] { "Sound to mix, at Gain 1.", "Sound to mix, at Gain 2.", "Sound to mix, at Gain 3.", "Sound to mix, at Gain 4." },
                new[] { "The mixed sound: the inputs added at their gains." });
            d[(int)NodeKind.Math] = new NodeDoc("Math on control signals: add, multiply, scale and offset, min, max, absolute value or invert.",
                new[]
                {
                    "Picks the operation: add, multiply, scale and offset, min, max, abs or invert.|Add mixes two modulators; multiply makes one control the depth of another; min and max clamp.",
                    "Multiplies the result, -8 to +8.|A negative scale flips a modulator upside down; a large scale makes a small control reach far.",
                    "Adds a fixed amount after scaling, -8 to +8.|An offset moves the middle of the signal, for example to keep a cutoff modulation above zero.",
                },
                new[] { "The first signal.", "The second signal." },
                new[] { "The result." });
            d[(int)NodeKind.Crossfade] = new NodeDoc("A crossfade: it blends between two sounds.",
                new[]
                {
                    "How much of A (0) or B (1) is heard; the Mix input moves it.|Sweeping it morphs between two sounds, as a vector synth does between its corners.",
                    "Linear keeps the sum constant in level; equal power keeps the loudness steady through the middle.|Equal power is the natural choice for different sounds; linear suits two copies of the same sound.",
                },
                new[] { "The first sound.", "The second sound.", "Moves the blend; it adds to the Mix control." },
                new[] { "The blended sound." });
            d[(int)NodeKind.Macro] = new NodeDoc("Brings one of the machine's 16 Macro knobs into the patch as a control signal.",
                new[]
                {
                    "Which macro (1 to 16) this node reads.|The macro is the knob that patterns, automation and MIDI can move; this node lets any wire use it.",
                    "Scales the macro signal, -1 to +1.|A negative amount inverts the knob; a small amount makes it a gentle influence.",
                },
                None, new[] { "The macro's value, 0 to 1, times the amount." });
            d[(int)NodeKind.VoiceSum] = new NodeDoc("Adds up all the voices into one signal that runs once: everything after it is global.",
                None,
                new[] { "The per-voice sound or control to add up over all sounding voices." },
                new[] { "The sum of the voices, one signal for the whole patch. Use it before a shared delay or filter to save CPU." });
            for (int i = 0; i < d.Length; i++) if (d[i] == null) throw new InvalidOperationException("NodeHelp: no entry for " + (NodeKind)i);
            return d;
        }
    }
}
