#nullable disable
// Neo Patch NP0: the patches the gate is measured on (docs/specs/neo-patch.md §5). NP1 turns these into template patches.

namespace NeoBuzz.Patch
{
    internal static class PatchTemplates
    {
        const int Pitch = 0, Gate = 1, Out = 0;

        /// <summary>
        /// Eight nodes: a saw FM'd by a square an octave up, the square and the saw into a low-pass whose cutoff follows an
        /// envelope, a VCA on the amp envelope. Dye Essence's counterpart: two Analog oscillators (the first with an FM warp from the
        /// second), one SVF low-pass, the two envelopes.
        /// </summary>
        public static PatchGraph Core8()
        {
            var g = new PatchGraph();
            var keys = g.Add(NodeKind.Keys);
            var a = g.Add(NodeKind.Osc); a.Set(NodeInfo.OscShape, 0); a.Set(NodeInfo.OscFm, 1.6f);
            var b = g.Add(NodeKind.Osc); b.Set(NodeInfo.OscShape, 1); b.Set(NodeInfo.OscTune, 12);
            var envA = g.Add(NodeKind.Envelope); envA.Set(NodeInfo.EnvA, 0.005f); envA.Set(NodeInfo.EnvD, 0.3f); envA.Set(NodeInfo.EnvS, 1f); envA.Set(NodeInfo.EnvR, 0.4f);
            var envF = g.Add(NodeKind.Envelope); envF.Set(NodeInfo.EnvA, 0.01f); envF.Set(NodeInfo.EnvD, 0.5f); envF.Set(NodeInfo.EnvS, 0.3f); envF.Set(NodeInfo.EnvR, 0.5f);
            var f = g.Add(NodeKind.Filter); f.Set(NodeInfo.FilterCutoff, 1200); f.Set(NodeInfo.FilterReso, 1.2f); f.Set(NodeInfo.FilterMod, 2f);
            var vca = g.Add(NodeKind.Vca);
            var o = g.Add(NodeKind.Out); o.Set(NodeInfo.OutLevel, 0.5f);
            g.Connect(keys, Pitch, a, NodeInfo.OscInPitch);
            g.Connect(keys, Pitch, b, NodeInfo.OscInPitch);
            g.Connect(b, Out, a, NodeInfo.OscInFm);
            g.Connect(a, Out, f, NodeInfo.FilterInAudio);
            g.Connect(b, Out, f, NodeInfo.FilterInAudio, 0.5f);
            g.Connect(keys, Gate, envA, NodeInfo.EnvInGate);
            g.Connect(keys, Gate, envF, NodeInfo.EnvInGate);
            g.Connect(envF, Out, f, NodeInfo.FilterInCutoff);
            g.Connect(f, Out, vca, NodeInfo.VcaInAudio);
            g.Connect(envA, Out, vca, NodeInfo.VcaInCv);
            g.Connect(vca, Out, o, 0);
            return g;
        }

        /// <summary>
        /// Twenty nodes: Keys; six oscillators (a saw FM'd by a sine through an FM-amount VCA, a square, a detuned saw, a sub sine, and
        /// a slow sine as an LFO); four envelopes; three filters in series with a feedback loop around the second; four VCAs; Out.
        /// Dye Essence's counterpart: three oscillators (FM from the second) plus the sub, two filters, the two envelopes, an LFO and a
        /// modulation envelope on routes. Dye has no feedback loop and no third filter: that is the difference the ratio pays for.
        /// </summary>
        public static PatchGraph Big20()
        {
            var g = new PatchGraph();
            var keys = g.Add(NodeKind.Keys);
            var oa = g.Add(NodeKind.Osc); oa.Set(NodeInfo.OscShape, 0); oa.Set(NodeInfo.OscFm, 1.6f);
            var ob = g.Add(NodeKind.Osc); ob.Set(NodeInfo.OscShape, 1); ob.Set(NodeInfo.OscTune, 12);
            var oc = g.Add(NodeKind.Osc); oc.Set(NodeInfo.OscShape, 2); oc.Set(NodeInfo.OscTune, 12);
            var od = g.Add(NodeKind.Osc); od.Set(NodeInfo.OscShape, 0); od.Set(NodeInfo.OscTune, 0.12f);
            var oe = g.Add(NodeKind.Osc); oe.Set(NodeInfo.OscShape, 2); oe.Set(NodeInfo.OscTune, -12);
            var lfo = g.Add(NodeKind.Osc); lfo.Set(NodeInfo.OscShape, 2); lfo.Set(NodeInfo.OscTune, -60);
            var eAmp = g.Add(NodeKind.Envelope); eAmp.Set(NodeInfo.EnvA, 0.005f); eAmp.Set(NodeInfo.EnvD, 0.3f); eAmp.Set(NodeInfo.EnvS, 1f); eAmp.Set(NodeInfo.EnvR, 0.4f);
            var eFlt = g.Add(NodeKind.Envelope); eFlt.Set(NodeInfo.EnvA, 0.01f); eFlt.Set(NodeInfo.EnvD, 0.5f); eFlt.Set(NodeInfo.EnvS, 0.3f); eFlt.Set(NodeInfo.EnvR, 0.5f);
            var eFm = g.Add(NodeKind.Envelope); eFm.Set(NodeInfo.EnvA, 0.002f); eFm.Set(NodeInfo.EnvD, 0.25f); eFm.Set(NodeInfo.EnvS, 0.2f);
            var eFb = g.Add(NodeKind.Envelope); eFb.Set(NodeInfo.EnvA, 0.05f); eFb.Set(NodeInfo.EnvD, 0.6f); eFb.Set(NodeInfo.EnvS, 0.4f);
            var vFm = g.Add(NodeKind.Vca);
            var vSub = g.Add(NodeKind.Vca);
            var vFb = g.Add(NodeKind.Vca); vFb.Set(NodeInfo.VcaGain, 0.5f);
            var vAmp = g.Add(NodeKind.Vca);
            var f1 = g.Add(NodeKind.Filter); f1.Set(NodeInfo.FilterCutoff, 1200); f1.Set(NodeInfo.FilterReso, 1.2f); f1.Set(NodeInfo.FilterMod, 2f);
            var f2 = g.Add(NodeKind.Filter); f2.Set(NodeInfo.FilterCutoff, 3000); f2.Set(NodeInfo.FilterReso, 0.9f); f2.Set(NodeInfo.FilterMod, 1f);
            var f3 = g.Add(NodeKind.Filter); f3.Set(NodeInfo.FilterMode, 2); f3.Set(NodeInfo.FilterCutoff, 60);
            var fb = g.Add(NodeKind.Feedback); fb.Set(NodeInfo.FeedbackGain, 0.6f);
            var o = g.Add(NodeKind.Out); o.Set(NodeInfo.OutLevel, 0.4f);

            foreach (var osc in new[] { oa, ob, oc, od, oe }) g.Connect(keys, Pitch, osc, NodeInfo.OscInPitch);
            foreach (var env in new[] { eAmp, eFlt, eFm, eFb }) g.Connect(keys, Gate, env, NodeInfo.EnvInGate);
            g.Connect(oc, Out, vFm, NodeInfo.VcaInAudio);
            g.Connect(eFm, Out, vFm, NodeInfo.VcaInCv);
            g.Connect(vFm, Out, oa, NodeInfo.OscInFm);
            g.Connect(oa, Out, f1, NodeInfo.FilterInAudio);
            g.Connect(ob, Out, f1, NodeInfo.FilterInAudio, 0.5f);
            g.Connect(od, Out, f1, NodeInfo.FilterInAudio, 0.5f);
            g.Connect(eFlt, Out, f1, NodeInfo.FilterInCutoff);
            g.Connect(lfo, Out, f1, NodeInfo.FilterInCutoff, 0.15f);
            g.Connect(f1, Out, f2, NodeInfo.FilterInAudio);
            g.Connect(eFlt, Out, f2, NodeInfo.FilterInCutoff, 0.5f);
            g.Connect(f2, Out, fb, 0);
            g.Connect(fb, Out, vFb, NodeInfo.VcaInAudio);
            g.Connect(eFb, Out, vFb, NodeInfo.VcaInCv);
            g.Connect(vFb, Out, f2, NodeInfo.FilterInAudio, 0.3f);
            g.Connect(f2, Out, f3, NodeInfo.FilterInAudio);
            g.Connect(oe, Out, vSub, NodeInfo.VcaInAudio);
            g.Connect(eAmp, Out, vSub, NodeInfo.VcaInCv);
            g.Connect(vSub, Out, f3, NodeInfo.FilterInAudio, 0.5f);
            g.Connect(f3, Out, vAmp, NodeInfo.VcaInAudio);
            g.Connect(eAmp, Out, vAmp, NodeInfo.VcaInCv);
            g.Connect(vAmp, Out, o, 0);
            return g;
        }
    }
}
