#nullable disable
// Neo Envelope's engine (docs/specs/neo-patch.md §3): an ADSR started by notes (Note mode), or a follower of a machine's output level
// (Follower mode), one step per update, output 0 … 1. Pure: the machine gives it notes, the level and the elapsed time.

using System;
using System.Runtime.CompilerServices;
using NeoBuzz.Dsp;

namespace NeoBuzz.EnvelopeMachine
{
    internal sealed class EnvelopeEngine
    {
        readonly ParamValues v;
        readonly Adsr adsr = new Adsr();
        bool gate;
        bool manualWas;
        double follow;
        int rate = 44100;

        public EnvelopeEngine(ParamValues values)
        {
            v = values;
        }

        public bool Following => v.Choice(P.Mode) == 1;

        /// <summary>The stage of the ADSR (for the editor).</summary>
        public EnvStage Stage => adsr.Stage;

        public void SetSampleRate(int sampleRate)
        {
            rate = sampleRate;
            adsr.SetSampleRate(sampleRate);
        }

        /// <summary>A note began (Note mode): the envelope attacks from where it is.</summary>
        public void NoteOn()
        {
            gate = true;
            adsr.Set((float)v.Value(P.Attack), (float)v.Value(P.Decay), v.F(P.Sustain), (float)v.Value(P.Release));
            adsr.Gate(true);
        }

        /// <summary>The note ended: the envelope releases.</summary>
        public void NoteOff()
        {
            gate = false;
            adsr.Set((float)v.Value(P.Attack), (float)v.Value(P.Decay), v.F(P.Sustain), (float)v.Value(P.Release));
            adsr.Gate(false);
        }

        /// <summary>Back to rest (the transport stopped, the mode changed).</summary>
        public void Reset()
        {
            gate = false;
            adsr.Reset();
            follow = 0;
        }

        /// <summary>
        /// Advances <paramref name="samples"/> samples (<paramref name="dt"/> seconds). <paramref name="level"/> is the listened
        /// output's peak over the last block (full scale 1), used in Follower mode.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public float Step(int samples, double dt, float level)
        {
            double y;
            if (Following)
            {
                double db = level > 1e-6f ? 8.685889638065035 * PortableMath.Log(level) : -120.0;
                double thr = v.Value(P.Threshold);
                double x = thr >= 0 ? (db >= 0 ? 1 : 0) : (db - thr) / (0 - thr);
                x = x < 0 ? 0 : x > 1 ? 1 : x;
                double tau = x > follow ? v.Value(P.Attack) : v.Value(P.Release);
                follow += (x - follow) * (tau <= 0.0002 ? 1.0 : 1.0 - PortableMath.Exp(-dt / tau));
                y = follow;
            }
            else
            {
                // The Gate switch is a note too: an edge starts or releases the envelope.
                bool manual = v.On(P.Gate);
                if (manual && !manualWas) NoteOn();
                else if (!manual && manualWas) NoteOff();
                manualWas = manual;
                adsr.Set((float)v.Value(P.Attack), (float)v.Value(P.Decay), v.F(P.Sustain), (float)v.Value(P.Release));
                for (int i = 0; i < samples; i++) adsr.Next();
                y = adsr.Level;
            }
            return (float)Curved(y, v.Value(P.Curve));
        }

        /// <summary>The curve: −1 … +1 bends a 0 … 1 value (0 straight).</summary>
        public static double Curved(double y, double curve)
        {
            if (curve == 0 || y <= 0 || y >= 1) return y;
            return ModBank.Bend(y, (int)Math.Round(curve * 100));
        }

        public double Glide => v.Value(P.Glide);
    }
}
