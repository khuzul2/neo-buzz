#nullable disable
// Neo Patch NP1: the factory patches, built in code (the headless builder the tests use too; NP2's editor builds the same graphs by hand).
// Machines/NeoPatch/Presets.txt carries each one as text (state Patch = ...) with its Volume at -22 dB and the macros it maps.
// Rebuild that file with the dev tool in docs/specs/neo-patch-engine.md ("Presets").

using System;

namespace NeoBuzz.Patch
{
    internal static partial class PatchPresets
    {
        public static readonly string[] Names =
        {
            "Init — Saw", "Bass — Sub Square", "Lead — FM Bell", "Pad — Wavetable Drift",
            "Pluck — Ladder", "Keys — Detuned Pair", "Texture — Noise Sweep", "FX — Echo After the Sum",
            // NP3: the presets below the first eight are in PatchPresetsMore.cs.
            "Bass — Reese Pair", "Bass — Pulse Walk", "Bass — Wobble Ladder", "Lead — Sync Sweep", "Lead — Folded Sine", "Lead — Ring Glass",
            "Lead — Square Stack", "Keys — Electric Tine", "Keys — Four Op Chime", "Pad — Slow Strings", "Pad — Crossfade Glass",
            "Pluck — Karplus String", "Pluck — Short Pulse", "Texture — Choir Loop", "FX — Metal Rain",
        };

        /// <summary>The macros each patch maps, for the preset's comment (in Presets.txt order).</summary>
        public static readonly string[] Comments =
        {
            "A saw through a low-pass and a VCA, each with its own envelope. Macro 1: cutoff. Macro 2: release.",
            "A square and a saw an octave down through a 24 dB ladder low-pass; the filter envelope bites on each note. Macro 1: cutoff. Macro 2: resonance.",
            "Two sines, the second frequency-modulating the first through its own envelope: a bell that rings out. Macro 1: modulation depth. Macro 2: decay.",
            "Two wavetable oscillators on the Formants table, detuned, their position drifting on a slow LFO, into a low-pass. Macro 1: table position. Macro 2: cutoff.",
            "A saw into a ladder low-pass whose envelope snaps shut: a plucked bass-guitar or harp sound. Macro 1: cutoff. Macro 2: resonance.",
            "A triangle and a square a few cents apart, soft-clipped. Macro 1: drive. Macro 2: detune.",
            "Noise and a saw through a band-pass whose centre wanders on a random generator. Macro 1: wander rate. Macro 2: centre.",
            "Per voice: a saw and a VCA. After the Voice sum: one echo and one low-pass for all notes, with a global LFO on the cutoff. Macro 1: feedback. Macro 2: cutoff.",
            // NP3 (PatchPresetsMore.cs): every macro is centred on the patch's own value, so the sound described is the one at the macros' 50 % default.
            "Two saws an octave down, 14 cents apart either way, through a ladder low-pass that a slow LFO moves: the beating bass of drum and bass. Macro 1: cutoff. Macro 2: detune.",
            "A square whose pulse width an LFO walks, through a low-pass with a short envelope: a hollow, moving bass. Macro 1: cutoff. Macro 2: walk rate.",
            "A saw an octave down through a ladder low-pass whose cutoff a 3 Hz LFO sweeps, restarted on every note: a wobble bass. Macro 1: cutoff. Macro 2: wobble rate.",
            "A saw restarted by a square on every cycle while an envelope sweeps its pitch down: the tearing lead of hard sync. Macro 1: slave tune. Macro 2: sweep decay.",
            "A sine folded back on itself by a Shaper, its drive raised by an envelope on each note: bright at the attack, softer as it dies. Macro 1: drive. Macro 2: bias.",
            "Two sines multiplied (ring modulation) for the bell-like sum and difference tones, with a little of the carrier. Macro 1: modulator tune. Macro 2: cutoff.",
            "Three square waves a few cents apart, soft-clipped into a low-pass: a thick, buzzy lead. Macro 1: drive. Macro 2: cutoff.",
            "A sine frequency-modulated by a sine at the same pitch, the modulation decaying faster than the note: a tine with a soft attack. Macro 1: FM index. Macro 2: decay.",
            "Two FM pairs mixed: operator 2 modulates 1 and 4 modulates 3 at an inharmonic ratio, one envelope shaping both: a glassy chime. Macro 1: first index. Macro 2: second index.",
            "Three detuned saws through a low-pass that a very slow LFO moves, with a slow attack: a string section. Macro 1: cutoff. Macro 2: attack.",
            "Two wavetable oscillators crossfaded by a slow LFO through a low-pass: a pad that drifts between two timbres. Macro 1: crossfade. Macro 2: cutoff.",
            "A burst of noise into a tuned delay whose feedback rings it as a string, then a low-pass body and a little feedback node: a plucked string. Macro 1: damping. Macro 2: sustain.",
            "A narrow pulse into a low-pass with a short decay: a staccato, woody pluck. Macro 1: cutoff. Macro 2: pulse width.",
            "A looped choir sample at the pitch of the keys through a low-pass that a smooth random generator wanders. Macro 1: cutoff. Macro 2: wander depth.",
            "Noise and a saw an octave up through a resonant band-pass whose centre a stepped random generator jumps around: metallic drops. Macro 1: jump rate. Macro 2: centre.",
        };

        public static PatchGraph Build(int i)
        {
            var g = i switch
            {
                0 => InitSaw(), 1 => SubSquare(), 2 => FmBell(), 3 => WavetableDrift(),
                4 => Pluck(), 5 => DetunedPair(), 6 => NoiseSweep(), 7 => EchoAfterSum(),
                _ => More(i),
            };
            g.Name = Names[i];
            return g;
        }

        internal static Node Env(PatchGraph g, Node keys, float a, float d, float s, float r)
        {
            var e = g.Add(NodeKind.Envelope);
            e.Set(NodeInfo.EnvA, a); e.Set(NodeInfo.EnvD, d); e.Set(NodeInfo.EnvS, s); e.Set(NodeInfo.EnvR, r);
            g.Connect(keys, 1, e, NodeInfo.EnvInGate);
            return e;
        }

        internal static Node Osc(PatchGraph g, Node keys, int shape, float tune = 0f, float level = 1f, float fine = 0f)
        {
            var o = g.Add(NodeKind.Osc);
            o.Set(NodeInfo.OscShape, shape); o.Set(NodeInfo.OscTune, tune); o.Set(NodeInfo.OscLevel, level); o.Set(NodeInfo.OscFine, fine);
            g.Connect(keys, 0, o, NodeInfo.OscInPitch);
            return o;
        }

        internal static Node Filter(PatchGraph g, int mode, float cutoff, float reso, float mod)
        {
            var f = g.Add(NodeKind.Filter);
            f.Set(NodeInfo.FilterMode, mode); f.Set(NodeInfo.FilterCutoff, cutoff); f.Set(NodeInfo.FilterReso, reso); f.Set(NodeInfo.FilterMod, mod);
            return f;
        }

        internal static Node Vca(PatchGraph g, Node audio, Node cv)
        {
            var v = g.Add(NodeKind.Vca);
            g.Connect(audio, 0, v, NodeInfo.VcaInAudio);
            g.Connect(cv, 0, v, NodeInfo.VcaInCv);
            return v;
        }

        internal static Node Out(PatchGraph g, Node from, float level = 1f)
        {
            var o = g.Add(NodeKind.Out); o.Set(NodeInfo.OutLevel, level);
            g.Connect(from, 0, o, 0);
            return o;
        }

        static PatchGraph InitSaw()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = Osc(g, k, 0);
            var ef = Env(g, k, 0.005f, 0.4f, 0.5f, 0.4f);
            var f = Filter(g, 0, 3000, 0.9f, 1.5f);
            g.Connect(o, 0, f, NodeInfo.FilterInAudio);
            g.Connect(ef, 0, f, NodeInfo.FilterInCutoff);
            var ea = Env(g, k, 0.003f, 0.3f, 0.8f, 0.35f);
            Out(g, Vca(g, f, ea));
            g.MapMacro(0, f, NodeInfo.FilterCutoff, 200, 12000);
            g.MapMacro(1, ea, NodeInfo.EnvR, 0.05f, 2f);
            return g;
        }

        static PatchGraph SubSquare()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var sq = Osc(g, k, 1, -12f, 1f);
            var sw = Osc(g, k, 0, -24f, 0.8f, 6f);
            var m = g.Add(NodeKind.Mixer); m.Set(NodeInfo.MixGain, 0.6f); m.Set(NodeInfo.MixGain + 1, 0.8f);
            g.Connect(sq, 0, m, 0); g.Connect(sw, 0, m, 1);
            var ef = Env(g, k, 0.002f, 0.25f, 0.2f, 0.2f);
            var f = Filter(g, 4, 500, 2.5f, 2.5f);
            g.Connect(m, 0, f, NodeInfo.FilterInAudio);
            g.Connect(ef, 0, f, NodeInfo.FilterInCutoff);
            var ea = Env(g, k, 0.002f, 0.2f, 0.9f, 0.15f);
            Out(g, Vca(g, f, ea));
            g.MapMacro(0, f, NodeInfo.FilterCutoff, 120, 3000);
            g.MapMacro(1, f, NodeInfo.FilterReso, 0.7f, 8f);
            return g;
        }

        static PatchGraph FmBell()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var car = Osc(g, k, 2, 0f, 1f); car.Set(NodeInfo.OscFm, 2.2f);
            var mod = Osc(g, k, 2, 12f + 7.02f, 1f);
            var em = Env(g, k, 0.001f, 0.6f, 0.0f, 0.4f);
            var vm = Vca(g, mod, em);
            g.Connect(vm, 0, car, NodeInfo.OscInFm);
            var ea = Env(g, k, 0.001f, 1.6f, 0.0f, 0.8f);
            Out(g, Vca(g, car, ea));
            g.MapMacro(0, car, NodeInfo.OscFm, 0f, 5f);
            g.MapMacro(1, ea, NodeInfo.EnvD, 0.2f, 4f);
            return g;
        }

        static PatchGraph WavetableDrift()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var lfo = g.Add(NodeKind.Lfo); lfo.Set(NodeInfo.LfoRate, 0.23f); lfo.Set(NodeInfo.LfoDepth, 0.5f); lfo.Set(NodeInfo.LfoRetrig, 0);
            Node W(float tune, float fine)
            {
                var w = g.Add(NodeKind.WavetableOsc);
                w.Set(NodeInfo.WtTable, 3); w.Set(NodeInfo.WtTune, tune); w.Set(NodeInfo.WtFine, fine); w.Set(NodeInfo.WtPosition, 0.45f);
                g.Connect(k, 0, w, NodeInfo.OscInPitch);
                g.Connect(lfo, 0, w, 4);
                return w;
            }
            var a = W(0f, 7f); var b = W(0f, -9f);
            var m = g.Add(NodeKind.Mixer); m.Set(NodeInfo.MixGain, 0.6f); m.Set(NodeInfo.MixGain + 1, 0.6f);
            g.Connect(a, 0, m, 0); g.Connect(b, 0, m, 1);
            var f = Filter(g, 0, 2800, 0.8f, 0f);
            g.Connect(m, 0, f, NodeInfo.FilterInAudio);
            var ea = Env(g, k, 0.6f, 0.8f, 0.85f, 1.4f);
            Out(g, Vca(g, f, ea));
            g.MapMacro(0, a, NodeInfo.WtPosition, 0f, 1f);
            g.MapMacro(0, b, NodeInfo.WtPosition, 0f, 1f);
            g.MapMacro(1, f, NodeInfo.FilterCutoff, 300, 9000);
            return g;
        }

        static PatchGraph Pluck()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = Osc(g, k, 0, 0f, 1f);
            var ef = Env(g, k, 0.001f, 0.28f, 0f, 0.2f);
            var f = Filter(g, 4, 700, 1.8f, 3.2f);
            g.Connect(o, 0, f, NodeInfo.FilterInAudio);
            g.Connect(ef, 0, f, NodeInfo.FilterInCutoff);
            var ea = Env(g, k, 0.001f, 0.7f, 0f, 0.3f);
            Out(g, Vca(g, f, ea));
            g.MapMacro(0, f, NodeInfo.FilterCutoff, 150, 4000);
            g.MapMacro(1, f, NodeInfo.FilterReso, 0.7f, 8f);
            return g;
        }

        static PatchGraph DetunedPair()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var a = Osc(g, k, 3, 0f, 1f, 8f);
            var b = Osc(g, k, 1, 0f, 0.5f, -8f);
            var m = g.Add(NodeKind.Mixer); m.Set(NodeInfo.MixGain, 0.8f); m.Set(NodeInfo.MixGain + 1, 0.6f);
            g.Connect(a, 0, m, 0); g.Connect(b, 0, m, 1);
            var s = g.Add(NodeKind.Shaper); s.Set(NodeInfo.ShaperMode, 0); s.Set(NodeInfo.ShaperDrive, 3f); s.Set(NodeInfo.ShaperLevel, 0.8f);
            g.Connect(m, 0, s, NodeInfo.ShaperInAudio);
            var f = Filter(g, 0, 4500, 0.8f, 0f);
            g.Connect(s, 0, f, NodeInfo.FilterInAudio);
            var ea = Env(g, k, 0.004f, 0.5f, 0.7f, 0.3f);
            Out(g, Vca(g, f, ea));
            g.MapMacro(0, s, NodeInfo.ShaperDrive, 1f, 10f);
            g.MapMacro(1, a, NodeInfo.OscFine, 0f, 30f);
            g.MapMacro(1, b, NodeInfo.OscFine, 0f, -30f);
            return g;
        }

        static PatchGraph NoiseSweep()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var n = g.Add(NodeKind.Noise); n.Set(NodeInfo.NoiseColor, 1); n.Set(NodeInfo.NoiseLevel, 0.7f);
            var o = Osc(g, k, 0, -12f, 0.5f);
            var m = g.Add(NodeKind.Mixer);
            g.Connect(n, 0, m, 0); g.Connect(o, 0, m, 1);
            var r = g.Add(NodeKind.Random); r.Set(NodeInfo.RandRate, 3f); r.Set(NodeInfo.RandSmooth, 0.8f); r.Set(NodeInfo.RandDepth, 0.9f);
            var f = Filter(g, 1, 1200, 4f, 3f);
            g.Connect(m, 0, f, NodeInfo.FilterInAudio);
            g.Connect(r, 0, f, NodeInfo.FilterInCutoff);
            var ea = Env(g, k, 1.2f, 1f, 0.9f, 2f);
            Out(g, Vca(g, f, ea), 1.4f);
            g.MapMacro(0, r, NodeInfo.RandRate, 0.3f, 12f);
            g.MapMacro(1, f, NodeInfo.FilterCutoff, 300, 4000);
            return g;
        }

        static PatchGraph EchoAfterSum()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = Osc(g, k, 0, 0f, 1f);
            var ea = Env(g, k, 0.002f, 0.15f, 0.0f, 0.15f);
            var v = Vca(g, o, ea);
            var vs = g.Add(NodeKind.VoiceSum);
            g.Connect(v, 0, vs, 0);
            var d = g.Add(NodeKind.Delay); d.Set(NodeInfo.DelayTime, 375f); d.Set(NodeInfo.DelayFeedback, 0.55f); d.Set(NodeInfo.DelayDamping, 5000f); d.Set(NodeInfo.DelayMix, 0.6f);
            g.Connect(vs, 0, d, NodeInfo.DelayInAudio);
            var lfo = g.Add(NodeKind.Lfo); lfo.Set(NodeInfo.LfoRate, 0.3f); lfo.Set(NodeInfo.LfoDepth, 0.4f); lfo.Set(NodeInfo.LfoRetrig, 0); lfo.Global = true;
            var f = Filter(g, 0, 2400, 1.1f, 2f); f.Global = true;
            g.Connect(d, 0, f, NodeInfo.FilterInAudio);
            g.Connect(lfo, 0, f, NodeInfo.FilterInCutoff);
            Out(g, f);
            g.MapMacro(0, d, NodeInfo.DelayFeedback, 0f, 0.9f);
            g.MapMacro(1, f, NodeInfo.FilterCutoff, 400, 8000);
            return g;
        }
    }
}
