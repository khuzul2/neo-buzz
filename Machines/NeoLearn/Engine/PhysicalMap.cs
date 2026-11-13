#nullable disable
// Neo Learn Physical's macro map (docs/specs/neo-learn.md §9, L1b as built): Dye Essence's Modal engine on slot A, 32 modes, plus the
// shared engine's additions: an exciter morph (mallet, pluck, bow, crossfaded; Dye's own choice is three exclusive exciters) and a
// geometry blend (Dye's choice is one body or another). Material walks four knots (wood, nylon, glass, metal) over Dye's stiffness,
// ring time and high-frequency loss; Damping then shortens the ring and adds loss; Geometry blends String, Tube, Bar and Plate.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class PhysicalMap : MacroMap
    {
        // The core controls' positions.
        public const int Material = 0, ExciterType = 1, ExciterForce = 2, ResonatorShape = 3, Damping = 4, BodySize = 5;

        /// <summary>Modes in the bank (Dye's AMModes).</summary>
        public const int Modes = 32;

        /// <summary>Where on the body it is excited (Dye's position): near an end, so the lower and upper modes all speak.</summary>
        public const double StrikePlace = 0.25;

        /// <summary>The material's knots at 0, 1/3, 2/3 and 1: wood, nylon, glass, metal. Stiffness (Dye's Material), ring time of the lowest mode (s) and high-frequency loss.</summary>
        public static readonly double[] KnotStiffness = { 0.30, 0.05, 0.75, 1.0 };
        public static readonly double[] KnotRing = { 0.35, 1.0, 2.5, 8.0 };
        public static readonly double[] KnotLoss = { 0.85, 0.65, 0.15, 0.05 };

        /// <summary>Damping 100 % shortens the ring by this much (a factor of 80, so 8 s of metal falls to 0.1 s); 0 % lengthens it 4 times.</summary>
        public const double RingAtNoDamping = 4, RingAtFullDamping = 0.05;

        /// <summary>The bodies Geometry walks through, in Dye's enumeration (String, Tube, Bar, Plate): three equal stretches blend one into the next.</summary>
        public static readonly int[] Bodies = { 0, 1, 2, 4 };

        static readonly int[] SlotA = { DyeP.AType };

        readonly ParamDef[] controls;

        public PhysicalMap(ParamDef[] controls)
        {
            this.controls = controls;
        }

        public override ParamDef[] Controls => controls;

        public override void Base(IDyeSink dye)
        {
            dye.Set(DyeP.VoiceMode, 0);
            dye.Set(DyeP.Polyphony, 16);
            dye.Set(DyeP.GlideMode, 0);
            dye.Set(DyeP.Quality, 0);
            dye.Set(DyeP.Volume, 0);
            dye.Set(DyeP.AType, 6);                                // Modal
            dye.Set(DyeP.BType, 0);
            dye.Set(DyeP.CType, 0);
            dye.Set(DyeP.AOct, 0);
            dye.Set(DyeP.ASemi, 0);
            dye.Set(DyeP.AFine, 0);
            dye.Set(DyeP.APan, 0);
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.APos, StrikePlace);
            dye.Set(DyeP.AMExc, 0);                                // (the morph replaces the exciter)
            dye.Set(DyeP.AMModes, Modes);
            dye.Set(DyeP.AToF2, 0);
            dye.Set(DyeP.SubLevel, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
        }

        /// <summary>Material's stiffness, ring (s, before Damping) and loss at <paramref name="m"/> (0 … 1): straight between the knots, the ring in the logarithm.</summary>
        public static void MaterialAt(double m, out double stiffness, out double ring, out double loss)
        {
            double s = Math.Clamp(m, 0, 1) * 3;
            int i = Math.Min((int)s, 2);
            double f = s - i;
            stiffness = KnotStiffness[i] + f * (KnotStiffness[i + 1] - KnotStiffness[i]);
            loss = KnotLoss[i] + f * (KnotLoss[i + 1] - KnotLoss[i]);
            ring = KnotRing[i] * Math.Pow(KnotRing[i + 1] / KnotRing[i], f);
        }

        /// <summary>The ring time (s) and loss with Damping <paramref name="d"/> (0 … 1) applied to a material's.</summary>
        public static void Damped(double ring, double loss, double d, out double ringS, out double lossD)
        {
            ringS = Math.Clamp(ring * RingAtNoDamping * Math.Pow(RingAtFullDamping / RingAtNoDamping, d), 0.05, 20);
            lossD = Math.Clamp(loss + 0.5 * d, 0, 1);
        }

        /// <summary>Geometry's stop (index into <see cref="Bodies"/>), its neighbour and the blend toward it, from the control (0 … 1).</summary>
        public static void BodyAt(double shape, out int from, out int to, out double blend)
        {
            double s = Math.Clamp(shape, 0, 1) * (Bodies.Length - 1);
            int i = Math.Min((int)s, Bodies.Length - 2);
            from = i;
            to = i + 1;
            blend = s - i;
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            MaterialAt(c[Material], out double stiffness, out double ring, out double loss);
            Damped(ring, loss, c[Damping], out double ringS, out double lossD);
            dye.Set(DyeP.AMMat, stiffness);
            dye.Set(DyeP.AMRing, ringS);
            dye.Set(DyeP.AMHfDamp, lossD);
            dye.Set(DyeP.AMForce, c[ExciterForce]);
            dye.Set(DyeP.AMSize, c[BodySize]);
            BodyAt(c[ResonatorShape], out int from, out int to, out double blend);
            dye.Set(DyeP.AMGeom, Bodies[from]);
            dye.Ext(0, SlotExt.GeomTo, Bodies[to]);
            dye.Ext(0, SlotExt.GeomBlend, blend);
            dye.Ext(0, SlotExt.ExciterMorph, c[ExciterType]);
            Globals(c, dye, 5, SlotA);
        }

        /// <summary>
        /// The modes' relative levels (0 … 1, the strongest 1) as the picture draws them for a body, a strike place, a material and a damping: mode j
        /// of the blended body at <paramref name="ratios"/>, weighted by where it is struck (a comb), by how fast it dies (the loss grows with the square of
        /// its frequency) and by the body size's radiation corner. <paramref name="note"/> sets the frequencies.
        /// </summary>
        public static void ModeLevels(double[] ratios, int count, double noteHz, double material, double damping, double size, double[] bars)
        {
            MaterialAt(material, out _, out double ring, out double loss);
            Damped(ring, loss, damping, out double ringS, out double lossD);
            double fc = 2000.0 * Math.Pow(2, -4.0 * (size - 0.5));
            double ringSeconds = ringS * Math.Pow(2, 1.5 * (size - 0.5));
            double h = 24.0 * lossD * (1.0 - 0.8 * material);
            double peak = 0;
            for (int j = 0; j < bars.Length; j++)
            {
                if (j >= count) { bars[j] = 0; continue; }
                double hz = noteHz * ratios[j];
                double wgt = Math.Abs(Math.Cos((j + 1) * (0.02 + 0.48 * StrikePlace) * Math.PI)) / Math.Sqrt(1 + (hz / fc) * (hz / fc));
                // The level some time into the ring (a tenth of a second): the upper modes have fallen faster.
                double sigma = 6.9078 / ringSeconds * (1.0 + h * (hz * 0.001) * (hz * 0.001));
                bars[j] = wgt / ratios[j] * Math.Exp(-sigma * 0.1);
                peak = Math.Max(peak, bars[j]);
            }
            if (peak > 0) for (int j = 0; j < bars.Length; j++) bars[j] /= peak;
        }

        /// <summary>The blended body's mode ratios (the engine's own blend) into <paramref name="ratios"/>.</summary>
        public static int BlendedRatios(double shape, double[] ratios)
        {
            BodyAt(shape, out int from, out int to, out double blend);
            var a = ModalBodies.Ratios[Bodies[from]];
            var b = ModalBodies.Ratios[Bodies[to]];
            int n = Math.Min(Math.Min(ModalBodies.Count[Bodies[from]], ModalBodies.Count[Bodies[to]]), ratios.Length);
            for (int j = 0; j < n; j++) ratios[j] = a[j] + blend * (b[j] - a[j]);
            return n;
        }

        /// <summary>
        /// The exciter's force on the body over <paramref name="t"/> (0 … 1 of the picture's width, about 8 ms), for the morph at <paramref name="morph"/>
        /// and force (0 … 1): the mallet's half-sine (wider when soft), the pick's spike and the bow's steady stream, crossfaded with the engine's shares.
        /// </summary>
        public static double Impulse(double morph, double force, double t)
        {
            morph = Math.Clamp(morph, 0, 1);
            double tt = morph < 0.5 ? 2 * morph : 2 * morph - 1;
            double a = Math.Cos(tt * Math.PI / 2), b = Math.Sin(tt * Math.PI / 2);
            double wM = morph < 0.5 ? a : 0, wP = morph < 0.5 ? b : a, wB = morph < 0.5 ? 0 : b;
            double width = 0.10 + 0.5 * (1 - force) * (1 - force);                         // the mallet's contact, a share of the picture
            double mallet = t < width ? Math.Sin(Math.PI * t / width) : 0;
            double spike = Math.Exp(-t * (6 + 40 * force)) * (t < 0.01 ? t / 0.01 : 1);    // the pluck: sharp, then it lets go
            double bow = 0.45 + 0.55 * Math.Sin(t * 90) * Math.Sin(t * 37 + 1);           // friction: a steady push with a grain
            return wM * mallet + wP * spike + wB * bow * (0.3 + 0.7 * force);
        }
    }
}
