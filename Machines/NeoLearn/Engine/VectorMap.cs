#nullable disable
// Neo Learn Vector's macro map (docs/specs/neo-learn.md §9, L2b as built): four sources at the corners of a diamond, W = slot A, E = slot B, N = slot C and S = the sub
// (with a little noise), each palette a set of four. A cursor (x, y) mixes them; the orbit generator, which lives in this map, moves the cursor around the point the two
// Vector controls give. The mix is a level the host moves between ticks (SlotExt.LevelExt, every 32 samples, smoothed by the engine over 4 ms), so the machine needs no
// LFO and the cursor is one for every note: the picture shows the same cursor the voices follow.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class VectorMap : MacroMap
    {
        // The core controls' positions.
        public const int VectorX = 0, VectorY = 1, Palette = 2, OrbitRate = 3, OrbitDepth = 4, OrbitPath = 5;

        /// <summary>The cursor moves in pieces of this many samples (the engine smooths each step over 4 ms).</summary>
        public const int Piece = 32;
        /// <summary>The share of the south corner's weight the noise gets in the palettes that have noise.</summary>
        public const double NoiseShare = 0.15;

        static readonly int[] Slots = { DyeP.AType, DyeP.BType, DyeP.CType };

        readonly ParamDef[] controls;
        float rate = 48000;
        double phase;
        Rng rng = new Rng(0x7EC70Eu);
        double driftX, driftY, targetX, targetY;
        double lastPhase;
        // what the picture reads: the cursor and the four weights (W, E, N, S), written by the audio thread
        volatile float cursorX, cursorY, wW = 0.25f, wE = 0.25f, wN = 0.25f, wS = 0.25f;

        public VectorMap(ParamDef[] controls)
        {
            this.controls = controls;
        }

        public override ParamDef[] Controls => controls;
        public override int TickSamples => Piece;
        public override void SetSampleRate(float sr) => rate = sr;

        public float CursorX => cursorX;
        public float CursorY => cursorY;
        /// <summary>The four corner weights now (west, east, north, south): they sum to 1.</summary>
        public void Weights(out float w, out float e, out float n, out float s) { w = wW; e = wE; n = wN; s = wS; }

        /// <summary>The weights of the cursor (x, y): the cursor is held in the diamond |x| + |y| ≤ 1; each corner has what is left of its axis, and the middle's remainder is shared equally.</summary>
        public static void CornerWeights(double x, double y, out double w, out double e, out double n, out double s)
        {
            double a = Math.Abs(x) + Math.Abs(y);
            if (a > 1) { x /= a; y /= a; a = 1; }
            double rest = (1 - a) * 0.25;
            e = Math.Max(0, x) + rest;
            w = Math.Max(0, -x) + rest;
            n = Math.Max(0, y) + rest;
            s = Math.Max(0, -y) + rest;
        }

        /// <summary>The orbit's offset at <paramref name="phase"/> (cycles) for a path: circle, figure-8, diamond step (the four corners in turn), as a unit offset (random drift is state, see <see cref="Tick"/>).</summary>
        public static void Orbit(int path, double phase, out double dx, out double dy)
        {
            double f = phase - Math.Floor(phase);
            switch (path)
            {
                case 0:
                    dx = PortableMath.Cos(2 * Math.PI * f);
                    dy = PortableMath.Sin(2 * Math.PI * f);
                    break;
                case 1:
                    dx = PortableMath.Sin(2 * Math.PI * f);
                    dy = PortableMath.Sin(4 * Math.PI * f);
                    break;
                default:
                {
                    int k = (int)(f * 4) & 3;                     // east, north, west, south
                    dx = k == 0 ? 1 : k == 2 ? -1 : 0;
                    dy = k == 1 ? 1 : k == 3 ? -1 : 0;
                    break;
                }
            }
        }

        public override void Base(IDyeSink dye)
        {
            dye.Set(DyeP.VoiceMode, 0);
            dye.Set(DyeP.Polyphony, 16);
            dye.Set(DyeP.GlideMode, 0);
            dye.Set(DyeP.Quality, 0);
            dye.Set(DyeP.Volume, 0);
            for (int s = 0; s < 3; s++)
            {
                int b = DyeP.AType + s * (DyeP.BType - DyeP.AType);
                dye.Set(b + (DyeP.AOct - DyeP.AType), 0);
                dye.Set(b + (DyeP.ASemi - DyeP.AType), 0);
                dye.Set(b + (DyeP.AFine - DyeP.AType), 0);
                dye.Set(b + (DyeP.APan - DyeP.AType), 0);
                dye.Set(b + (DyeP.ALevel - DyeP.AType), 1);
                dye.Set(b + (DyeP.AWarp1 - DyeP.AType), 0);
                dye.Set(b + (DyeP.AWarp2 - DyeP.AType), 0);
                dye.Set(b + (DyeP.AToF2 - DyeP.AType), 0);
                dye.Set(b + (DyeP.ARandom - DyeP.AType), 1);       // random start phases: the corners add in power
            }
            dye.Set(DyeP.ADrift, 0.1);
            dye.Set(DyeP.BDrift, 0.1);
            dye.Set(DyeP.CDrift, 0.1);
            dye.Set(DyeP.SubOct, -1);
            dye.Set(DyeP.NoiseColor, 0.4);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
        }

        // One corner's source: Dye slot type (2 Analog, 1 Wavetable, 3 Sample), then its shape / table / sample.
        void Corner(IDyeSink dye, int slot, int type, int a)
        {
            int b = DyeP.AType + slot * (DyeP.BType - DyeP.AType);
            dye.Set(b, type);
            if (type == 2) dye.Set(new[] { DyeP.AShape, DyeP.BShape, DyeP.CShape }[slot], a);
            else if (type == 1) dye.Set(b + (DyeP.ATable - DyeP.AType), a);
            else dye.Set(new[] { DyeP.ASmpSrc, DyeP.BSmpSrc, DyeP.CSmpSrc }[slot], a);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            switch ((int)Math.Clamp(Math.Round(c[Palette]), 0, 3))
            {
                case 0:   // Poly Classic: a saw, a square, a choir, a triangle sub
                    Corner(dye, 0, 2, 2); Corner(dye, 1, 2, 3); Corner(dye, 2, 3, 0);
                    dye.Set(DyeP.SubShape, 1); dye.Ext(ExtSlot.Noise, SlotExt.LevelExt, 0);
                    break;
                case 1:   // Cinema Horizon: strings, a harmonic table, texture, a sine sub with a breath of noise
                    Corner(dye, 0, 3, 1); Corner(dye, 1, 1, 2); Corner(dye, 2, 3, 4);
                    dye.Set(DyeP.SubShape, 0);
                    break;
                case 2:   // Cyber Pulse: a pulse, a digital table, a sync sweep, a square sub
                    Corner(dye, 0, 2, 4); Corner(dye, 1, 1, 8); Corner(dye, 2, 1, 4);
                    dye.Set(DyeP.SubShape, 3);
                    break;
                default:  // Organic Hybrid: an organ table, a bell, a formant table, a triangle sub with noise
                    Corner(dye, 0, 1, 6); Corner(dye, 1, 3, 2); Corner(dye, 2, 1, 3);
                    dye.Set(DyeP.SubShape, 1);
                    break;
            }
            Globals(c, dye, 5, Slots);
        }

        /// <summary>The palette's noise: Poly Classic has none, the others a little (the south corner's air).</summary>
        static bool HasNoise(int palette) => palette != 0 && palette != 2;

        public override void Tick(double[] c, int samples, IDyeSink dye)
        {
            double hz = c[OrbitRate], radius = Math.Clamp(c[OrbitDepth], 0, 1);
            int path = (int)Math.Clamp(Math.Round(c[OrbitPath]), 0, 3);
            phase += hz * samples / rate;
            phase -= Math.Floor(phase);
            double dx, dy;
            if (path == 3)
            {
                // Random drift: a new target each time the phase wraps, approached by a one-pole (about a third of a cycle)
                if (phase < lastPhase) { targetX = rng.NextBipolar(); targetY = rng.NextBipolar(); }
                double k = 1 - PortableMath.Exp(-3.0 * hz * samples / rate);
                driftX += (targetX - driftX) * k;
                driftY += (targetY - driftY) * k;
                dx = driftX;
                dy = driftY;
            }
            else Orbit(path, phase, out dx, out dy);
            lastPhase = phase;
            double x = Math.Clamp(c[VectorX], -1, 1) + radius * dx, y = Math.Clamp(c[VectorY], -1, 1) + radius * dy;
            CornerWeights(x, y, out double w, out double e, out double n, out double s);
            double a = Math.Abs(x) + Math.Abs(y);
            if (a > 1) { x /= a; y /= a; }
            cursorX = (float)x; cursorY = (float)y;
            wW = (float)w; wE = (float)e; wN = (float)n; wS = (float)s;
            dye.Ext(0, SlotExt.LevelExt, w);
            dye.Ext(1, SlotExt.LevelExt, e);
            dye.Ext(2, SlotExt.LevelExt, n);
            dye.Ext(ExtSlot.Sub, SlotExt.LevelExt, s);
            dye.Ext(ExtSlot.Noise, SlotExt.LevelExt, HasNoise((int)Math.Clamp(Math.Round(c[Palette]), 0, 3)) ? s * NoiseShare : 0);
        }
    }
}
