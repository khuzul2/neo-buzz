#nullable disable
// Neo Patch NP3: fifteen more factory patches, built in code like the first eight (PatchPresets.cs). Every macro mapping is centred on the
// patch's own value (Mid), so the sound is the one described with the macros at their 50 % default. Machines/NeoPatch/Presets.txt carries each one
// as text with its Volume at -22 dB; NeoPatchTemplateTests regenerates that file (NEOPATCH_WRITE=1) and checks it otherwise.

using System;

namespace NeoBuzz.Patch
{
    internal static partial class PatchPresets
    {
        /// <summary>Maps a macro to a control, centred on the control's current value: the macro at 50 % leaves it where it is. The span is cut to stay inside the control's range on both sides.</summary>
        internal static void Mid(PatchGraph g, int macro, Node n, int control, float span)
        {
            var c = n.Info.Controls[control];
            float v = n.Controls[control];
            span = Math.Min(span, Math.Min(v - c.Min, c.Max - v));
            g.MapMacro(macro, n, control, v - span, v + span);
        }

        /// <summary>An amplitude envelope on the keys' gate and a VCA after the signal.</summary>
        internal static Node Amp(PatchGraph g, Node keys, Node sig, float a, float d, float s, float r)
        {
            var e = Env(g, keys, a, d, s, r);
            return Vca(g, sig, e);
        }

        internal static Node Mixer(PatchGraph g, params (Node From, float Gain)[] ins)
        {
            var m = g.Add(NodeKind.Mixer);
            for (int i = 0; i < ins.Length && i < 4; i++) { m.Set(NodeInfo.MixGain + i, ins[i].Gain); g.Connect(ins[i].From, 0, m, i); }
            return m;
        }

        internal static Node Lfo(PatchGraph g, float rate, float depth, int shape = 0, bool retrigger = false)
        {
            var l = g.Add(NodeKind.Lfo);
            l.Set(NodeInfo.LfoShape, shape); l.Set(NodeInfo.LfoRate, rate); l.Set(NodeInfo.LfoDepth, depth); l.Set(NodeInfo.LfoRetrig, retrigger ? 1 : 0);
            return l;
        }

        static PatchGraph More(int i)
        {
            switch (i)
            {
                case 8: return ReesePair();
                case 9: return PulseWalk();
                case 10: return WobbleLadder();
                case 11: return PatchLessons.SyncLead(5f, 12f, 4500f);
                case 12: return PatchLessons.Wavefolder(3.5f, 5f, 6000f);
                case 13: return PatchLessons.AmRing(true, 7.3f, 7000f);
                case 14: return SquareStack();
                case 15: return PatchLessons.FmTwo(0f, 1.3f, 0.7f, 1.2f, 0.1f);
                case 16: return PatchLessons.FmFour(0f, 7.02f + 12f, 12f, 1.1f, 2.1f, 1.6f);
                case 17: return SlowStrings();
                case 18: return CrossfadeGlass();
                case 19: return PatchLessons.Plucked(0.97f, 5200f, 4800f);
                case 20: return ShortPulse();
                case 21: return ChoirLoop();
                default: return MetalRain();
            }
        }

        static PatchGraph ReesePair()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var a = Osc(g, k, 0, -12f, 1f, 14f);
            var b = Osc(g, k, 0, -12f, 1f, -14f);
            var m = Mixer(g, (a, 0.6f), (b, 0.6f));
            var lfo = Lfo(g, 0.35f, 0.3f);
            var f = Filter(g, 4, 900, 1.4f, 1.2f);
            g.Connect(m, 0, f, NodeInfo.FilterInAudio);
            g.Connect(lfo, 0, f, NodeInfo.FilterInCutoff);
            Out(g, Amp(g, k, f, 0.004f, 0.3f, 0.85f, 0.2f));
            Mid(g, 0, f, NodeInfo.FilterCutoff, 700);
            Mid(g, 1, a, NodeInfo.OscFine, 14);
            Mid(g, 1, b, NodeInfo.OscFine, 14);
            return g;
        }

        static PatchGraph PulseWalk()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = Osc(g, k, 1, 0f, 1f);
            var lfo = Lfo(g, 0.7f, 0.3f);
            g.Connect(lfo, 0, o, NodeInfo.OscInPulse);
            var ef = Env(g, k, 0.002f, 0.2f, 0.3f, 0.15f);
            var f = Filter(g, 0, 1600, 1f, 2f);
            g.Connect(o, 0, f, NodeInfo.FilterInAudio);
            g.Connect(ef, 0, f, NodeInfo.FilterInCutoff);
            Out(g, Amp(g, k, f, 0.002f, 0.25f, 0.8f, 0.12f));
            Mid(g, 0, f, NodeInfo.FilterCutoff, 1400);
            Mid(g, 1, lfo, NodeInfo.LfoRate, 0.6f);
            return g;
        }

        static PatchGraph WobbleLadder()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = Osc(g, k, 0, -12f, 1f);
            var lfo = Lfo(g, 3f, 1f, 1, true);
            var f = Filter(g, 4, 500, 3f, 2.5f);
            g.Connect(o, 0, f, NodeInfo.FilterInAudio);
            g.Connect(lfo, 0, f, NodeInfo.FilterInCutoff);
            Out(g, Amp(g, k, f, 0.003f, 0.2f, 0.9f, 0.15f));
            Mid(g, 0, f, NodeInfo.FilterCutoff, 400);
            Mid(g, 1, lfo, NodeInfo.LfoRate, 2.5f);
            return g;
        }

        static PatchGraph SquareStack()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var a = Osc(g, k, 1, 0f, 1f, -10f);
            var b = Osc(g, k, 1, 0f, 1f, 0f);
            var c = Osc(g, k, 1, 0f, 1f, 10f);
            var m = Mixer(g, (a, 0.5f), (b, 0.5f), (c, 0.5f));
            var s = g.Add(NodeKind.Shaper); s.Set(NodeInfo.ShaperMode, 0); s.Set(NodeInfo.ShaperDrive, 2.5f); s.Set(NodeInfo.ShaperLevel, 0.8f);
            g.Connect(m, 0, s, NodeInfo.ShaperInAudio);
            var f = Filter(g, 0, 3800, 0.8f, 0f);
            g.Connect(s, 0, f, NodeInfo.FilterInAudio);
            Out(g, Amp(g, k, f, 0.003f, 0.3f, 0.75f, 0.2f));
            Mid(g, 0, s, NodeInfo.ShaperDrive, 1.5f);
            Mid(g, 1, f, NodeInfo.FilterCutoff, 3000);
            return g;
        }

        static PatchGraph SlowStrings()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var a = Osc(g, k, 0, 0f, 1f, -11f);
            var b = Osc(g, k, 0, 0f, 1f, 0f);
            var c = Osc(g, k, 0, 0f, 1f, 11f);
            var m = Mixer(g, (a, 0.45f), (b, 0.45f), (c, 0.45f));
            var lfo = Lfo(g, 0.18f, 0.25f);
            var f = Filter(g, 0, 2200, 0.7f, 1f);
            g.Connect(m, 0, f, NodeInfo.FilterInAudio);
            g.Connect(lfo, 0, f, NodeInfo.FilterInCutoff);
            var amp = Amp(g, k, f, 0.8f, 0.6f, 0.85f, 1.2f);
            Out(g, amp);
            Mid(g, 0, f, NodeInfo.FilterCutoff, 1800);
            Mid(g, 1, g.Nodes.Find(n => n.Kind == NodeKind.Envelope), NodeInfo.EnvA, 0.7f);
            return g;
        }

        static PatchGraph CrossfadeGlass()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            Node W(int table, float fine)
            {
                var w = g.Add(NodeKind.WavetableOsc);
                w.Set(NodeInfo.WtTable, table); w.Set(NodeInfo.WtFine, fine); w.Set(NodeInfo.WtPosition, 0.4f);
                g.Connect(k, 0, w, NodeInfo.OscInPitch);
                return w;
            }
            var a = W(2, 6f); var b = W(6, -6f);
            var x = g.Add(NodeKind.Crossfade); x.Set(NodeInfo.XfadeMix, 0.5f);
            g.Connect(a, 0, x, NodeInfo.XfadeInA); g.Connect(b, 0, x, NodeInfo.XfadeInB);
            var lfo = Lfo(g, 0.12f, 0.45f);
            g.Connect(lfo, 0, x, NodeInfo.XfadeInMix);
            var f = Filter(g, 0, 5000, 0.7f, 0f);
            g.Connect(x, 0, f, NodeInfo.FilterInAudio);
            Out(g, Amp(g, k, f, 0.5f, 0.8f, 0.9f, 1.5f));
            Mid(g, 0, x, NodeInfo.XfadeMix, 0.5f);
            Mid(g, 1, f, NodeInfo.FilterCutoff, 4000);
            return g;
        }

        static PatchGraph ShortPulse()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var o = Osc(g, k, 1, 0f, 1f); o.Set(NodeInfo.OscPulse, 0.3f);
            var ef = Env(g, k, 0.001f, 0.15f, 0f, 0.1f);
            var f = Filter(g, 0, 1800, 1.2f, 2.5f);
            g.Connect(o, 0, f, NodeInfo.FilterInAudio);
            g.Connect(ef, 0, f, NodeInfo.FilterInCutoff);
            Out(g, Amp(g, k, f, 0.001f, 0.18f, 0f, 0.12f));
            Mid(g, 0, f, NodeInfo.FilterCutoff, 1500);
            Mid(g, 1, o, NodeInfo.OscPulse, 0.25f);
            return g;
        }

        static PatchGraph ChoirLoop()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var s = g.Add(NodeKind.Sample); s.Set(NodeInfo.SampleSource, 0); s.Set(NodeInfo.SampleLoop, 1);
            g.Connect(k, 0, s, NodeInfo.SampleInPitch); g.Connect(k, 1, s, NodeInfo.SampleInGate);
            var r = g.Add(NodeKind.Random); r.Set(NodeInfo.RandRate, 1.2f); r.Set(NodeInfo.RandSmooth, 0.9f); r.Set(NodeInfo.RandDepth, 0.3f);
            var f = Filter(g, 0, 3000, 0.8f, 1.5f);
            g.Connect(s, 0, f, NodeInfo.FilterInAudio);
            g.Connect(r, 0, f, NodeInfo.FilterInCutoff);
            Out(g, Amp(g, k, f, 0.5f, 0.6f, 0.9f, 1.2f));
            Mid(g, 0, f, NodeInfo.FilterCutoff, 2500);
            Mid(g, 1, r, NodeInfo.RandDepth, 0.3f);
            return g;
        }

        static PatchGraph MetalRain()
        {
            var g = new PatchGraph();
            var k = g.Add(NodeKind.Keys);
            var n = g.Add(NodeKind.Noise); n.Set(NodeInfo.NoiseLevel, 0.6f);
            var o = Osc(g, k, 0, 12f, 0.3f);
            var m = Mixer(g, (n, 1f), (o, 1f));
            var r = g.Add(NodeKind.Random); r.Set(NodeInfo.RandRate, 9f); r.Set(NodeInfo.RandSmooth, 0f); r.Set(NodeInfo.RandDepth, 0.7f);
            var f = Filter(g, 1, 2500, 8f, 3f);
            g.Connect(m, 0, f, NodeInfo.FilterInAudio);
            g.Connect(r, 0, f, NodeInfo.FilterInCutoff);
            Out(g, Amp(g, k, f, 0.001f, 0.25f, 0.2f, 0.2f));
            Mid(g, 0, r, NodeInfo.RandRate, 8f);
            Mid(g, 1, f, NodeInfo.FilterCutoff, 2000);
            return g;
        }
    }
}
