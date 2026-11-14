#nullable disable
// Neo Patch NP3 (docs/specs/neo-patch.md §4.7): the template patches, one for each Neo Learn method the nodes can build, plus the empty
// patch with sound in three steps. Each is built in code from the same few builders the factory presets use, with its Out level set so the
// chord C3 E3 G3 B3 sounds at -22 dB RMS at the machine's default Volume and the macros at their 50 % default (every mapping is centred on
// the patch's own value). They are the lessons of the "build-a-synth" guide, in the editor's Templates menu and behind patch_load_template.

using System;
using System.Collections.Generic;

namespace NeoBuzz.Patch
{
    internal static class PatchLessons
    {
        public static readonly string[] Slugs =
        {
            "start", "subtractive", "fm-2op", "fm-4op", "am-ring", "wavefolder", "sync-lead", "plucked-string", "vector-crossfade", "sample-loop",
        };

        public static readonly string[] Titles =
        {
            "Start: sound in 3 steps", "Subtractive", "FM, 2 operators", "FM, 4 operators", "AM and ring", "Wavefolder", "Sync lead", "Plucked string", "Vector crossfade", "Sample loop",
        };

        public static readonly string[] Summaries =
        {
            "A basic voice with its three wires left for you: Keys, Osc, Envelope, VCA and Out. Wire the pitch, the oscillator and the VCA to Out and it plays.",
            "The classic: two oscillators into a ladder low-pass that an envelope opens and closes, then a VCA. Macros: cutoff, resonance, filter envelope depth.",
            "A sine carrier frequency-modulated by a sine an octave up, whose strength an envelope shapes: a bell or a tine. Macros: FM index, modulator decay, release.",
            "Two FM pairs mixed: operator 2 modulates 1, operator 4 modulates 3, and one envelope shapes both modulators. Macros: the two indexes, decay.",
            "A carrier multiplied by a second oscillator two ways (ring and amplitude modulation) and crossfaded. Macros: ring to AM, modulator tune, cutoff.",
            "A sine folded back on itself by a Shaper whose drive an envelope raises on each note. Macros: drive, bias, cutoff.",
            "A saw restarted by a square's cycle (the Sync input) while an envelope sweeps its pitch: the tearing lead. Macros: slave tune, sweep, cutoff.",
            "A burst of noise into a tuned Delay (the string), a Filter (the body) and a Feedback node. Macros: damping, sustain, cutoff.",
            "Four sources crossfaded in pairs and then together by three Crossfade nodes, two of them drifting on LFOs. Macros: the three mixes.",
            "A looped built-in sample played at the pitch of the keys through a filter. Macros: sample start, cutoff, release.",
        };

        public static int Count => Slugs.Length;

        /// <summary>The Out level that puts each template at -22 dB (the chord, loudest 50 ms; NeoPatchTemplateTests measures and prints them).</summary>
        static readonly float[] Levels = { 0.2628f, 0.3202f, 0.1783f, 0.2245f, 0.1733f, 0.2787f, 0.2352f, 0.0851f, 0.2268f, 0.3568f };

        /// <summary>A template's index from its slug or its title (case does not matter), or -1.</summary>
        public static int Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            string n = name.Trim();
            for (int i = 0; i < Count; i++)
                if (string.Equals(Slugs[i], n, StringComparison.OrdinalIgnoreCase) || string.Equals(Titles[i], n, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public static PatchGraph Build(int i, bool calibrated = true)
        {
            var g = i switch
            {
                0 => Start(), 1 => Subtractive(), 2 => FmTwo(12f, 2f, 0.5f, 1.4f, 0.1f), 3 => FmFour(12f, 19.02f, 0f, 1.5f, 2.4f, 1.8f),
                4 => AmRing(false, 7f, 4500f), 5 => Wavefolder(4f, 6f, 5000f), 6 => SyncLead(5f, 14f, 4500f), 7 => Plucked(0.96f, 6000f, 5000f),
                8 => Vector(), _ => SampleLoop(),
            };
            g.Name = Titles[i];
            if (calibrated)
                foreach (var n in g.Nodes) if (n.Kind == NodeKind.Out) n.Set(NodeInfo.OutLevel, Levels[i]);
            Place(g);
            return g;
        }

        /// <summary>The three wires of the empty patch: the lesson's steps, in order (from node kind, port, to node kind, port).</summary>
        public static readonly (NodeKind From, int FromPort, NodeKind To, int ToPort)[] StartSteps =
        {
            (NodeKind.Keys, 0, NodeKind.Osc, NodeInfo.OscInPitch),
            (NodeKind.Osc, 0, NodeKind.Vca, NodeInfo.VcaInAudio),
            (NodeKind.Vca, 0, NodeKind.Out, 0),
        };

        // ------------------------------------------------------------------ the patches

        static PatchGraph Start()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = g.Add(NodeKind.Osc);
            var e = PatchPresets.Env(g, k, 0.005f, 0.3f, 0.7f, 0.3f);
            var v = g.Add(NodeKind.Vca);
            g.Connect(e, 0, v, NodeInfo.VcaInCv);
            var u = g.Add(NodeKind.Out);
            return g;
        }

        /// <summary>The Start patch with its three wires made: what a learner has after the three steps.</summary>
        public static PatchGraph StartFinished()
        {
            var g = Build(0);
            foreach (var (from, fp, to, tp) in StartSteps)
                g.Connect(g.Nodes.Find(n => n.Kind == from), fp, g.Nodes.Find(n => n.Kind == to), tp);
            return g;
        }

        static PatchGraph Subtractive()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var a = PatchPresets.Osc(g, k, 0, 0f, 1f);
            var b = PatchPresets.Osc(g, k, 1, -12f, 1f, 7f);
            var m = PatchPresets.Mixer(g, (a, 0.6f), (b, 0.5f));
            var ef = PatchPresets.Env(g, k, 0.005f, 0.35f, 0.3f, 0.3f);
            var f = PatchPresets.Filter(g, 4, 1400, 2f, 2.5f);
            g.Connect(m, 0, f, NodeInfo.FilterInAudio);
            g.Connect(ef, 0, f, NodeInfo.FilterInCutoff);
            PatchPresets.Out(g, PatchPresets.Amp(g, k, f, 0.005f, 0.3f, 0.8f, 0.3f));
            PatchPresets.Mid(g, 0, f, NodeInfo.FilterCutoff, 1300);
            PatchPresets.Mid(g, 1, f, NodeInfo.FilterReso, 1.5f);
            PatchPresets.Mid(g, 2, f, NodeInfo.FilterMod, 2.5f);
            return g;
        }

        /// <summary>Two operators: a modulator sine (tuned by modTune semitones, its strength shaped by an envelope that decays in modDecay) into a carrier's FM input.</summary>
        internal static PatchGraph FmTwo(float modTune, float index, float modDecay, float ampDecay, float ampSustain)
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var car = PatchPresets.Osc(g, k, 2, 0f, 1f); car.Set(NodeInfo.OscFm, index);
            var mod = PatchPresets.Osc(g, k, 2, modTune, 1f);
            var em = PatchPresets.Env(g, k, 0.001f, modDecay, 0f, 0.3f);
            g.Connect(PatchPresets.Vca(g, mod, em), 0, car, NodeInfo.OscInFm);
            var ea = PatchPresets.Env(g, k, 0.001f, ampDecay, ampSustain, 0.5f);
            PatchPresets.Out(g, PatchPresets.Vca(g, car, ea));
            PatchPresets.Mid(g, 0, car, NodeInfo.OscFm, 2f);
            PatchPresets.Mid(g, 1, em, NodeInfo.EnvD, 0.4f);
            PatchPresets.Mid(g, 2, ea, NodeInfo.EnvR, 0.4f);
            return g;
        }

        /// <summary>Four operators in two pairs: 2 modulates 1, 4 modulates 3, one envelope shapes both modulators, and 1 and 3 are mixed.</summary>
        internal static PatchGraph FmFour(float op2Tune, float op3Tune, float op4Tune, float indexA, float indexB, float decay)
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var op1 = PatchPresets.Osc(g, k, 2, 0f, 1f); op1.Set(NodeInfo.OscFm, indexA);
            var op2 = PatchPresets.Osc(g, k, 2, op2Tune, 1f);
            var op3 = PatchPresets.Osc(g, k, 2, op3Tune, 1f); op3.Set(NodeInfo.OscFm, indexB);
            var op4 = PatchPresets.Osc(g, k, 2, op4Tune, 1f);
            var em = PatchPresets.Env(g, k, 0.001f, 0.6f, 0f, 0.3f);
            g.Connect(PatchPresets.Vca(g, op2, em), 0, op1, NodeInfo.OscInFm);
            g.Connect(PatchPresets.Vca(g, op4, em), 0, op3, NodeInfo.OscInFm);
            var m = PatchPresets.Mixer(g, (op1, 0.7f), (op3, 0.4f));
            var ea = PatchPresets.Env(g, k, 0.001f, decay, 0f, 0.6f);
            PatchPresets.Out(g, PatchPresets.Vca(g, m, ea));
            PatchPresets.Mid(g, 0, op1, NodeInfo.OscFm, 1.5f);
            PatchPresets.Mid(g, 1, op3, NodeInfo.OscFm, 2f);
            PatchPresets.Mid(g, 2, ea, NodeInfo.EnvD, decay * 0.7f);
            return g;
        }

        /// <summary>A carrier times a second sine: ring (the product) and AM (the product with the modulator lifted to 0 … 1), crossfaded; ring starts the crossfade at the ring end.</summary>
        internal static PatchGraph AmRing(bool ringOnly, float modTune, float cutoff)
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var car = PatchPresets.Osc(g, k, 2, 0f, 1f);
            var mod = PatchPresets.Osc(g, k, 2, modTune, 1f);
            var ring = g.Add(NodeKind.Vca);
            g.Connect(car, 0, ring, NodeInfo.VcaInAudio); g.Connect(mod, 0, ring, NodeInfo.VcaInCv);
            var lift = g.Add(NodeKind.Math); lift.Set(NodeInfo.MathOp, 2); lift.Set(NodeInfo.MathScale, 0.5f); lift.Set(NodeInfo.MathOffset, 0.5f);
            g.Connect(mod, 0, lift, NodeInfo.MathInA);
            var am = g.Add(NodeKind.Vca);
            g.Connect(car, 0, am, NodeInfo.VcaInAudio); g.Connect(lift, 0, am, NodeInfo.VcaInCv);
            var x = g.Add(NodeKind.Crossfade); x.Set(NodeInfo.XfadeMix, ringOnly ? 0.25f : 0.5f);
            g.Connect(ring, 0, x, NodeInfo.XfadeInA); g.Connect(am, 0, x, NodeInfo.XfadeInB);
            var f = PatchPresets.Filter(g, 0, cutoff, 0.8f, 0f);
            g.Connect(x, 0, f, NodeInfo.FilterInAudio);
            PatchPresets.Out(g, PatchPresets.Amp(g, k, f, 0.002f, ringOnly ? 1.2f : 0.5f, ringOnly ? 0f : 0.8f, 0.4f));
            PatchPresets.Mid(g, 0, x, NodeInfo.XfadeMix, 0.25f);
            PatchPresets.Mid(g, 1, mod, NodeInfo.OscTune, 7f);
            PatchPresets.Mid(g, 2, f, NodeInfo.FilterCutoff, cutoff * 0.7f);
            return g;
        }

        /// <summary>A sine into a folding Shaper; an envelope raises the drive (by envAmount) on each note.</summary>
        internal static PatchGraph Wavefolder(float drive, float envAmount, float cutoff)
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = PatchPresets.Osc(g, k, 2, 0f, 1f);
            var ed = PatchPresets.Env(g, k, 0.002f, 0.5f, 0.2f, 0.3f);
            var s = g.Add(NodeKind.Shaper); s.Set(NodeInfo.ShaperMode, 1); s.Set(NodeInfo.ShaperDrive, drive); s.Set(NodeInfo.ShaperLevel, 0.8f);
            g.Connect(o, 0, s, NodeInfo.ShaperInAudio); g.Connect(ed, 0, s, NodeInfo.ShaperInDrive, envAmount); g.Connect(k, 0, s, NodeInfo.ShaperInPitch);
            var f = PatchPresets.Filter(g, 0, cutoff, 0.8f, 0f);
            g.Connect(s, 0, f, NodeInfo.FilterInAudio);
            PatchPresets.Out(g, PatchPresets.Amp(g, k, f, 0.003f, 0.4f, 0.8f, 0.25f));
            PatchPresets.Mid(g, 0, s, NodeInfo.ShaperDrive, drive * 0.7f);
            PatchPresets.Mid(g, 1, s, NodeInfo.ShaperBias, 0.4f);
            PatchPresets.Mid(g, 2, f, NodeInfo.FilterCutoff, cutoff * 0.7f);
            return g;
        }

        /// <summary>A square (the master) restarts a saw (the slave) on every cycle; an envelope raises the slave's pitch by sweep semitones and lets it fall.</summary>
        internal static PatchGraph SyncLead(float slaveTune, float sweep, float cutoff)
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var master = PatchPresets.Osc(g, k, 1, 0f, 0f);
            var slave = PatchPresets.Osc(g, k, 0, slaveTune, 1f);
            g.Connect(master, 0, slave, NodeInfo.OscInSync);
            var es = PatchPresets.Env(g, k, 0.001f, 0.6f, 0.3f, 0.3f);
            g.Connect(es, 0, slave, NodeInfo.OscInPitch, sweep);
            var f = PatchPresets.Filter(g, 0, cutoff, 0.8f, 0f);
            g.Connect(slave, 0, f, NodeInfo.FilterInAudio);
            PatchPresets.Out(g, PatchPresets.Amp(g, k, f, 0.002f, 0.3f, 0.85f, 0.2f));
            PatchPresets.Mid(g, 0, slave, NodeInfo.OscTune, slaveTune);
            PatchPresets.Mid(g, 1, es, NodeInfo.EnvD, 0.5f);
            PatchPresets.Mid(g, 2, f, NodeInfo.FilterCutoff, cutoff * 0.7f);
            return g;
        }

        /// <summary>
        /// A noise burst into a Delay whose time follows the pitch (its own feedback closes the loop sample-accurately: that is the string), a
        /// Filter (the body) and a Feedback node that returns a little of the body to the string. Time is the period of note 0; each semitone
        /// halves it by a twelfth of an octave (a wire of -1/12 from the pitch).
        /// </summary>
        internal static PatchGraph Plucked(float feedback, float damping, float cutoff)
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var n = g.Add(NodeKind.Noise); n.Set(NodeInfo.NoiseLevel, 1f);
            var eb = PatchPresets.Env(g, k, 0.0005f, 0.006f, 0f, 0.01f);
            var burst = PatchPresets.Vca(g, n, eb);
            var d = g.Add(NodeKind.Delay);
            d.Set(NodeInfo.DelayTime, 122.3f); d.Set(NodeInfo.DelayFeedback, feedback); d.Set(NodeInfo.DelayDamping, damping); d.Set(NodeInfo.DelayMix, 1f); d.Set(NodeInfo.DelayTimeMod, 1f);
            g.Connect(burst, 0, d, NodeInfo.DelayInAudio);
            g.Connect(k, 0, d, NodeInfo.DelayInTime, -1f / 12f);
            var f = PatchPresets.Filter(g, 0, cutoff, 0.7f, 0f);
            g.Connect(d, 0, f, NodeInfo.FilterInAudio);
            var fb = g.Add(NodeKind.Feedback); fb.Set(NodeInfo.FeedbackGain, 0.15f);
            g.Connect(f, 0, fb, 0);
            g.Connect(fb, 0, d, NodeInfo.DelayInAudio);
            PatchPresets.Out(g, PatchPresets.Amp(g, k, f, 0.001f, 2f, 0f, 0.4f));
            PatchPresets.Mid(g, 0, d, NodeInfo.DelayDamping, Math.Min(damping - 200f, 5000f));
            PatchPresets.Mid(g, 1, d, NodeInfo.DelayFeedback, 0.01f);
            PatchPresets.Mid(g, 2, f, NodeInfo.FilterCutoff, cutoff * 0.7f);
            return g;
        }

        static PatchGraph Vector()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var a = PatchPresets.Osc(g, k, 0, 0f, 1f);
            var b = PatchPresets.Osc(g, k, 1, 0f, 1f, 5f);
            var c = g.Add(NodeKind.WavetableOsc); c.Set(NodeInfo.WtTable, 2); c.Set(NodeInfo.WtPosition, 0.4f);
            g.Connect(k, 0, c, NodeInfo.OscInPitch);
            var d = PatchPresets.Osc(g, k, 3, 12f, 1f);
            Node X(Node p, Node q, float mix)
            {
                var x = g.Add(NodeKind.Crossfade); x.Set(NodeInfo.XfadeMix, mix);
                g.Connect(p, 0, x, NodeInfo.XfadeInA); g.Connect(q, 0, x, NodeInfo.XfadeInB);
                return x;
            }
            var x1 = X(a, b, 0.5f); var x2 = X(c, d, 0.5f);
            g.Connect(PatchPresets.Lfo(g, 0.17f, 0.4f), 0, x1, NodeInfo.XfadeInMix);
            g.Connect(PatchPresets.Lfo(g, 0.11f, 0.4f), 0, x2, NodeInfo.XfadeInMix);
            var x3 = X(x1, x2, 0.5f);
            var f = PatchPresets.Filter(g, 0, 4000, 0.8f, 0f);
            g.Connect(x3, 0, f, NodeInfo.FilterInAudio);
            PatchPresets.Out(g, PatchPresets.Amp(g, k, f, 0.3f, 0.6f, 0.9f, 0.8f));
            PatchPresets.Mid(g, 0, x1, NodeInfo.XfadeMix, 0.5f);
            PatchPresets.Mid(g, 1, x2, NodeInfo.XfadeMix, 0.5f);
            PatchPresets.Mid(g, 2, x3, NodeInfo.XfadeMix, 0.5f);
            return g;
        }

        static PatchGraph SampleLoop()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var s = g.Add(NodeKind.Sample); s.Set(NodeInfo.SampleSource, 0); s.Set(NodeInfo.SampleLoop, 1); s.Set(NodeInfo.SampleStart, 0.5f);
            g.Connect(k, 0, s, NodeInfo.SampleInPitch); g.Connect(k, 1, s, NodeInfo.SampleInGate);
            var f = PatchPresets.Filter(g, 0, 3500, 0.8f, 0f);
            g.Connect(s, 0, f, NodeInfo.FilterInAudio);
            var amp = PatchPresets.Amp(g, k, f, 0.3f, 0.5f, 0.9f, 0.8f);
            PatchPresets.Out(g, amp);
            PatchPresets.Mid(g, 0, s, NodeInfo.SampleStart, 0.5f);
            PatchPresets.Mid(g, 1, f, NodeInfo.FilterCutoff, 3000);
            PatchPresets.Mid(g, 2, g.Nodes.Find(n => n.Kind == NodeKind.Envelope), NodeInfo.EnvR, 0.7f);
            return g;
        }

        // ------------------------------------------------------------------ places

        /// <summary>Lays the nodes out in columns by how far each is from the notes (a wire into a Feedback node is a way back, not a step), one row per node in a column.</summary>
        public static void Place(PatchGraph g)
        {
            var depth = new Dictionary<int, int>();
            foreach (var n in g.Nodes) depth[n.Id] = 0;
            for (int pass = 0; pass < g.Nodes.Count + 1; pass++)
            {
                bool moved = false;
                foreach (var w in g.Wires)
                {
                    var to = g.Find(w.ToNode);
                    if (to == null || to.Kind == NodeKind.Feedback) continue;
                    if (depth[w.ToNode] < depth[w.FromNode] + 1) { depth[w.ToNode] = depth[w.FromNode] + 1; moved = true; }
                }
                if (!moved) break;
            }
            var rows = new Dictionary<int, int>();
            foreach (var n in g.Nodes)
            {
                int d = depth[n.Id];
                rows.TryGetValue(d, out int r);
                rows[d] = r + 1;
                n.X = d * 208f;
                n.Y = r * 136f;
            }
        }
    }
}
