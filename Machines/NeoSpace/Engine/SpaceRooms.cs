#nullable disable
// Neo Space (docs/specs/neo-space.md): the built-in rooms, made by our own code. Clean room: textbook acoustics (Allen and Berkley's
// image-source method for a shoebox room, Eyring's reverberation time, Schroeder's and Moorer's late tail of decaying noise); no
// recording, no impulse response, no table from any other product. Nothing here is committed as audio: every response is made when a
// room is asked for (on the builder's thread, never the audio thread), the same bits on every platform (PortableMath, a fixed random
// generator, sums in a fixed order).
//
//  1. Early part. The room is a box (Lx, Ly, Lz metres) with a source and two receivers (the ears, 0.2 m apart). Every image of the
//     source in the walls whose sound arrives before the mixing time is a tap, placed with a windowed sinc at its fractional delay, with
//     the amplitude d0 / d (the direct path's distance over its own) times the reflection coefficient of every wall it bounced off.
//     The coefficients come from the room's reverberation times by Eyring's formula, in three bands (low, mid, high), so a room whose
//     high band rings shorter loses its highs reflection by reflection. The direct sound itself is left out: the response is wet only.
//  2. Late part. White noise (a different sequence in each ear: the diffuse field is decorrelated) split in three bands, each decaying
//     exponentially at its own reverberation time, at the level the diffuse field has after the direct sound (the critical distance's
//     formula), fading in as the images fade out (equal power) around the mixing time.
//  3. A plate is no room: no images, a dense bright tail from the first millisecond.
// The response is normalised so that pink noise comes out at its own level (SpaceIr.PinkLevel), the two channels together.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Space
{
    internal static class SpaceRooms
    {
        /// <summary>The built-in rooms, in the order of the Room parameter (the parameter's last choice, Wave, is the song's wave).</summary>
        public static readonly string[] Names = { "Small room", "Ambience", "Drum room", "Chamber", "Plate", "Hall", "Large hall", "Cathedral", "Tunnel" };

        public const int Count = 9;

        /// <summary>The room the Wave choice falls back to when its wave is missing: the hall.</summary>
        public const int Fallback = 5;

        /// <summary>The longest response, in seconds.</summary>
        public const double MaxSeconds = 10.0;

        const double SpeedOfSound = 343.0;

        sealed class Def
        {
            public double Lx, Ly, Lz;                 // metres
            public double Sx, Sy, Sz;                 // the source, as fractions of the box
            public double Rx, Ry, Rz;                 // the receivers' centre
            public double RtLow, RtMid, RtHigh;       // reverberation time in the three bands, seconds
            public double Ceiling = 1.0;              // the ceiling's reflection, against the walls' (an absorbing ceiling: below 1)
            public double TailLevel = 1.0;            // the late field against the theory's (an ambience has less)
            public double LowTilt = 1.0, HighTilt = 1.0;   // the tail's balance against equal energy per octave
            public double BuildMs;                    // a plate's tail rises over this
            public double MixMs;                      // the images stop here (0: from the volume, sqrt(V) ms)
            public bool Plate;
        }

        static readonly Def[] Defs =
        {
            // Small room (a living room): close and a little boxy.
            new Def { Lx = 3.6, Ly = 2.9, Lz = 2.4, Sx = 0.30, Sy = 0.35, Sz = 0.45, Rx = 0.68, Ry = 0.62, Rz = 0.45, RtLow = 0.42, RtMid = 0.32, RtHigh = 0.20, Ceiling = 0.9, LowTilt = 1.1, HighTilt = 0.85 },
            // Ambience: a dry, treated space, mostly its first reflections and a faint, short tail.
            new Def { Lx = 6.0, Ly = 4.6, Lz = 3.0, Sx = 0.25, Sy = 0.40, Sz = 0.40, Rx = 0.70, Ry = 0.58, Rz = 0.42, RtLow = 0.55, RtMid = 0.40, RtHigh = 0.26, Ceiling = 0.8, TailLevel = 0.55, LowTilt = 0.9, HighTilt = 1.0 },
            // Drum room: a live, high-ceilinged room with hard walls: a big, fast, bright room.
            new Def { Lx = 8.5, Ly = 6.8, Lz = 5.0, Sx = 0.30, Sy = 0.45, Sz = 0.30, Rx = 0.72, Ry = 0.55, Rz = 0.35, RtLow = 1.00, RtMid = 0.85, RtHigh = 0.60, Ceiling = 1.0, HighTilt = 1.1 },
            // Chamber: a long, smooth room of plaster.
            new Def { Lx = 12.0, Ly = 7.0, Lz = 5.5, Sx = 0.22, Sy = 0.50, Sz = 0.40, Rx = 0.66, Ry = 0.50, Rz = 0.40, RtLow = 1.75, RtMid = 1.45, RtHigh = 1.05, Ceiling = 1.0 },
            // Plate: no room, a dense and bright tail from the start.
            new Def { Plate = true, RtLow = 1.7, RtMid = 2.1, RtHigh = 2.5, BuildMs = 2.5, LowTilt = 0.7, HighTilt = 1.5 },
            // Hall: a concert hall.
            new Def { Lx = 30.0, Ly = 22.0, Lz = 14.0, Sx = 0.15, Sy = 0.50, Sz = 0.18, Rx = 0.55, Ry = 0.46, Rz = 0.15, RtLow = 2.7, RtMid = 2.2, RtHigh = 1.5, Ceiling = 0.95 },
            // Large hall: a very big hall with a long, warm tail.
            new Def { Lx = 55.0, Ly = 38.0, Lz = 24.0, Sx = 0.12, Sy = 0.50, Sz = 0.12, Rx = 0.50, Ry = 0.47, Rz = 0.12, RtLow = 4.6, RtMid = 3.8, RtHigh = 2.4, Ceiling = 0.95, LowTilt = 1.1, HighTilt = 0.9 },
            // Cathedral: stone, enormous, seconds of low and mid ringing; the highs are taken by the air.
            new Def { Lx = 70.0, Ly = 30.0, Lz = 34.0, Sx = 0.10, Sy = 0.50, Sz = 0.06, Rx = 0.46, Ry = 0.48, Rz = 0.06, RtLow = 8.0, RtMid = 6.4, RtHigh = 3.2, Ceiling = 1.0, LowTilt = 1.15, HighTilt = 0.8 },
            // Tunnel: narrow and long: flutter echoes between the walls, a long and metallic ring.
            new Def { Lx = 4.5, Ly = 4.5, Lz = 60.0, Sx = 0.50, Sy = 0.50, Sz = 0.04, Rx = 0.50, Ry = 0.50, Rz = 0.30, RtLow = 5.0, RtMid = 4.2, RtHigh = 2.8, Ceiling = 1.0, MixMs = 60 },
        };

        // The bands: low below 450 Hz, mid to 4 kHz, high above. Octaves in each (20 Hz to 20 kHz), for equal energy per octave.
        const double LowCrossover = 450.0, HighCrossover = 4000.0;
        static readonly double[] BandShare = { 4.5 / 10.0, 3.2 / 10.0, 2.3 / 10.0 };

        sealed class Rng
        {
            uint s;
            public Rng(uint seed) { s = seed; }
            uint Next() { s = s * 1664525u + 1013904223u; return s; }
            public double Signed() => ((Next() >> 8) / 16777216.0) * 2.0 - 1.0;
        }

        /// <summary>A room's box, in metres, with the source and the centre of the two receivers (metres from the corner; a plate has none: zeros).</summary>
        public static (double Lx, double Ly, double Lz, double[] Source, double[] Receiver) Box(int room)
        {
            var d = Defs[Math.Clamp(room, 0, Count - 1)];
            if (d.Plate) return (0, 0, 0, new double[3], new double[3]);
            return (d.Lx, d.Ly, d.Lz, new[] { d.Sx * d.Lx, d.Sy * d.Ly, d.Sz * d.Lz }, new[] { d.Rx * d.Lx, d.Ry * d.Ly, d.Rz * d.Lz });
        }

        /// <summary>The distance between the two receivers (the ears), metres, along x.</summary>
        public const double EarSpacing = 0.2;

        /// <summary>The reverberation time of a room in its mid band, from its definition (the test measures the response against it).</summary>
        public static double Rt60(int room) => Defs[Math.Clamp(room, 0, Count - 1)].RtMid;

        /// <summary>The reverberation times (low, mid, high) of a room, seconds.</summary>
        public static (double Low, double Mid, double High) Rt60Bands(int room)
        {
            var d = Defs[Math.Clamp(room, 0, Count - 1)];
            return (d.RtLow, d.RtMid, d.RtHigh);
        }

        /// <summary>The length of a room's response at <paramref name="rate"/>, in samples (about 66 dB of decay in the slowest band, at most 10 s).</summary>
        public static int LengthOf(int room, double rate)
        {
            var d = Defs[Math.Clamp(room, 0, Count - 1)];
            return (int)(Math.Min(MaxSeconds, d.RtLow * 1.1 + 0.05) * rate);
        }

        /// <summary>A built-in room (<paramref name="room"/> 0 … 8) at <paramref name="rate"/>: left and right, at the level SpaceIr.PinkLevel makes 1.</summary>
        public static void Generate(int room, double rate, out float[] left, out float[] right)
        {
            room = Math.Clamp(room, 0, Count - 1);
            var d = Defs[room];
            int n = LengthOf(room, rate);
            left = new float[n];
            right = new float[n];
            double volume = d.Plate ? 800.0 : d.Lx * d.Ly * d.Lz;
            double surface = d.Plate ? 600.0 : 2 * (d.Lx * d.Ly + d.Lx * d.Lz + d.Ly * d.Lz);
            // The direct path (centre of the receivers) fixes t = 0 and the level of everything else.
            double d0 = d.Plate ? 3.0 : Distance(d.Sx * d.Lx, d.Sy * d.Ly, d.Sz * d.Lz, d.Rx * d.Lx, d.Ry * d.Ly, d.Rz * d.Lz);
            double mixSeconds = d.Plate ? 0 : (d.MixMs > 0 ? d.MixMs : Math.Clamp(Math.Sqrt(volume) * 1.0, 10.0, 110.0)) * 0.001;
            double fadeSeconds = d.Plate ? 0 : Math.Max(0.004, 0.25 * mixSeconds);
            // The diffuse field's strength relative to the direct sound (critical distance, Sabine and Eyring agree to this order).
            double dc = 0.057 * Math.Sqrt(volume / d.RtMid);
            double lateEnergy = (d0 / dc) * (d0 / dc) * d.TailLevel;
            double[] rt = { d.RtLow, d.RtMid, d.RtHigh };
            double[] tilt = { d.LowTilt, 1.0, d.HighTilt };
            for (int ch = 0; ch < 2; ch++)
            {
                var y = ch == 0 ? left : right;
                double earS = ch == 0 ? -0.1 : 0.1;
                if (!d.Plate) Early(d, rate, n, d0, mixSeconds + fadeSeconds, rt, volume, surface, earS, y, mixSeconds, fadeSeconds);
                Late(d, rate, n, room, ch, lateEnergy, rt, tilt, mixSeconds, fadeSeconds, y);
            }
            Finish(left, right, rate);
        }

        static double Distance(double x0, double y0, double z0, double x1, double y1, double z1) =>
            Math.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1) + (z0 - z1) * (z0 - z1));

        /// <summary>The images that arrive before <paramref name="until"/> seconds after the direct sound, in three bands, filtered into one signal, added to <paramref name="y"/>.</summary>
        static void Early(Def d, double rate, int n, double d0, double until, double[] rt, double volume, double surface, double earOffset, float[] y, double mixSeconds, double fadeSeconds)
        {
            // Eyring: beta = exp(-0.0805 V / (S T)) in pressure, per band.
            var beta = new double[3];
            for (int b = 0; b < 3; b++) beta[b] = PortableMath.Exp(-0.0805 * volume / (surface * rt[b]));
            double sx = d.Sx * d.Lx, sy = d.Sy * d.Ly, sz = d.Sz * d.Lz;
            double rx = d.Rx * d.Lx + earOffset, ry = d.Ry * d.Ly, rz = d.Rz * d.Lz;
            double reach = d0 + until * SpeedOfSound;
            int nx = (int)Math.Ceiling(reach / (2 * d.Lx)) + 1, ny = (int)Math.Ceiling(reach / (2 * d.Ly)) + 1, nz = (int)Math.Ceiling(reach / (2 * d.Lz)) + 1;
            var band = new float[3][];
            for (int b = 0; b < 3; b++) band[b] = new float[n];
            var pow = new double[3][];
            for (int b = 0; b < 3; b++)
            {
                pow[b] = new double[64];
                pow[b][0] = 1;
                for (int k = 1; k < 64; k++) pow[b][k] = pow[b][k - 1] * beta[b];
            }
            var ceilingPow = new double[64];
            ceilingPow[0] = 1;
            for (int k = 1; k < 64; k++) ceilingPow[k] = ceilingPow[k - 1] * d.Ceiling;
            for (int mz = 0; mz < 2; mz++)
                for (int my = 0; my < 2; my++)
                    for (int mx = 0; mx < 2; mx++)
                        for (int iz = -nz; iz <= nz; iz++)
                            for (int iy = -ny; iy <= ny; iy++)
                                for (int ix = -nx; ix <= nx; ix++)
                                {
                                    int kx0 = Math.Abs(ix - mx), kx1 = Math.Abs(ix), ky0 = Math.Abs(iy - my), ky1 = Math.Abs(iy), kz0 = Math.Abs(iz - mz), kz1 = Math.Abs(iz);
                                    int walls = kx0 + kx1 + ky0 + ky1 + kz0 + kz1;
                                    if (walls == 0 || walls >= 64) continue;
                                    double xi = (1 - 2 * mx) * sx + 2 * ix * d.Lx;
                                    double yi = (1 - 2 * my) * sy + 2 * iy * d.Ly;
                                    double zi = (1 - 2 * mz) * sz + 2 * iz * d.Lz;
                                    double dist = Distance(xi, yi, zi, rx, ry, rz);
                                    double t = (dist - d0) / SpeedOfSound;
                                    if (t <= 0 || t > until) continue;
                                    // The ceiling is a wall at the top of z: kz1 counts bounces off it.
                                    double ceiling = ceilingPow[kz1];
                                    double position = t * rate;
                                    int side = kx0 + kx1 + ky0 + ky1;
                                    int floorHits = kz0 + kz1;
                                    for (int b = 0; b < 3; b++)
                                    {
                                        double a = (d0 / dist) * pow[b][side] * pow[b][floorHits] * ceiling;
                                        Place(band[b], position, (float)a);
                                    }
                                }
            // Split into the three bands: low, mid and high each from its own tap train.
            var lp1 = new Biquad(); lp1.Set(BiquadType.LowPass, LowCrossover, rate, 0.7071);
            var lp2 = new Biquad(); lp2.Set(BiquadType.LowPass, HighCrossover, rate, 0.7071);
            var lp1m = new Biquad(); lp1m.Set(BiquadType.LowPass, LowCrossover, rate, 0.7071);
            var lp2h = new Biquad(); lp2h.Set(BiquadType.LowPass, HighCrossover, rate, 0.7071);
            // Past the last image nothing is left (the images fade out by `until`): the filters need only run a little beyond it.
            int run = Math.Min(n, (int)((until + 0.02) * rate));
            for (int i = 0; i < run; i++)
            {
                float low = lp1.Process(band[0][i]);
                float mid = lp2.Process(band[1][i]) - lp1m.Process(band[1][i]);
                float high = band[2][i] - lp2h.Process(band[2][i]);
                double t = i / rate;
                // The images fade out around the mixing time (equal power with the noise coming in).
                double g = ImageGain(t, mixSeconds, fadeSeconds);
                y[i] += (float)((low + mid + high) * g);
            }
        }

        static double ImageGain(double t, double mix, double fade)
        {
            if (fade <= 0) return 1.0;
            double x = (t - (mix - fade)) / (2 * fade);
            if (x <= 0) return 1.0;
            if (x >= 1) return 0.0;
            return PortableMath.Cos(0.5 * Math.PI * x);
        }

        static double NoiseGain(double t, double mix, double fade)
        {
            if (fade <= 0) return 1.0;
            double x = (t - (mix - fade)) / (2 * fade);
            if (x <= 0) return 0.0;
            if (x >= 1) return 1.0;
            return PortableMath.Sin(0.5 * Math.PI * x);
        }

        // A windowed sinc (Hann, 16 taps) at the fractional position: the tap lands between samples without a comb of its own.
        static void Place(float[] y, double position, float amplitude)
        {
            int i0 = (int)Math.Floor(position);
            double frac = position - i0;
            for (int k = -7; k <= 8; k++)
            {
                int at = i0 + k;
                if (at < 0 || at >= y.Length) continue;
                double x = k - frac;
                double sinc = Math.Abs(x) < 1e-9 ? 1.0 : PortableMath.Sin(Math.PI * x) / (Math.PI * x);
                double window = 0.5 + 0.5 * PortableMath.Cos(Math.PI * x / 8.0);
                y[at] += (float)(amplitude * sinc * window);
            }
        }

        static void Late(Def d, double rate, int n, int room, int ch, double lateEnergy, double[] rt, double[] tilt, double mixSeconds, double fadeSeconds, float[] y)
        {
            var rng = new Rng(0x5BD1E995u ^ (uint)(room * 7919 + ch * 104729 + 17));
            // Each crossover is three 12 dB/octave low-passes in a row (36 dB/octave), so a band's own decay is not heard in the next one.
            var lp1 = new Biquad[3];
            var lp2 = new Biquad[3];
            for (int s = 0; s < 3; s++)
            {
                lp1[s] = new Biquad(); lp1[s].Set(BiquadType.LowPass, LowCrossover * 1.25, rate, 0.7071);
                lp2[s] = new Biquad(); lp2[s].Set(BiquadType.LowPass, HighCrossover * 1.25, rate, 0.7071);
            }
            // The unit-variance band signals: a low-passed white noise has the share of the energy below its corner.
            // (The noise-equivalent bandwidth of three such stages with the corner at 1.25 times the crossover is 0.729 of that corner.)
            const double Neq = 0.729 * 1.25;
            double fLow = Neq * LowCrossover / (rate * 0.5), fMid = Neq * (HighCrossover - LowCrossover) / (rate * 0.5), fHigh = Math.Max(0.02, 1.0 - Neq * HighCrossover / (rate * 0.5));
            double[] norm = { 1.0 / Math.Sqrt(fLow), 1.0 / Math.Sqrt(fMid), 1.0 / Math.Sqrt(fHigh) };
            // Each band's energy at t = 0 from the diffuse field: lateEnergy over the band's decay (energy per second 13.8 / T).
            var p0 = new double[3];
            var k = new double[3];
            for (int b = 0; b < 3; b++)
            {
                p0[b] = lateEnergy * BandShare[b] * (13.8155 / rt[b]) * tilt[b] * tilt[b];
                k[b] = 6.90776 / (rt[b] * rate);   // amplitude decay per sample: 60 dB in the reverberation time
            }
            var amp = new double[3];
            double perSample = Math.Sqrt(1.0 / rate);
            for (int b = 0; b < 3; b++) amp[b] = Math.Sqrt(p0[b]) * perSample;
            double buildSamples = d.Plate ? Math.Max(1.0, d.BuildMs * 0.001 * rate) : 0;
            double[] env = { 1, 1, 1 };
            double[] step = new double[3];
            for (int b = 0; b < 3; b++) step[b] = PortableMath.Exp(-k[b]);
            for (int i = 0; i < n; i++)
            {
                float w = (float)rng.Signed() * 1.7320508f;   // unit variance
                float a = lp1[2].Process(lp1[1].Process(lp1[0].Process(w))), bb = lp2[2].Process(lp2[1].Process(lp2[0].Process(w)));
                double low = a * norm[0], mid = (bb - a) * norm[1], high = (w - bb) * norm[2];
                double t = i / rate;
                double g = d.Plate ? 1.0 : NoiseGain(t, mixSeconds, fadeSeconds);
                if (d.Plate)
                {
                    double x = Math.Min(1.0, i / buildSamples);
                    g = x * x * (3 - 2 * x);
                }
                if (g > 0)
                {
                    // Amplitude at t: sqrt(power per sample) times the envelope.
                    double yv = low * amp[0] * env[0] + mid * amp[1] * env[1] + high * amp[2] * env[2];
                    y[i] += (float)(yv * g);
                }
                for (int b = 0; b < 3; b++) env[b] *= step[b];
            }
        }

        /// <summary>The end fades out (a cosine over the last 30 ms), tiny values go to zero, and the response gets the pink level of 1.</summary>
        static void Finish(float[] l, float[] r, double rate)
        {
            int n = l.Length;
            int fade = Math.Min(n / 2, (int)(0.03 * rate));
            for (int i = 0; i < fade; i++)
            {
                float g = 0.5f + 0.5f * (float)PortableMath.Cos(Math.PI * (i + 1) / (fade + 1));
                l[n - fade + i] *= g;
                r[n - fade + i] *= g;
            }
            float norm = SpaceIr.Normaliser(l, r, rate);
            for (int i = 0; i < n; i++)
            {
                l[i] *= norm;
                r[i] *= norm;
                if (Math.Abs(l[i]) < 1e-20f) l[i] = 0;
                if (Math.Abs(r[i]) < 1e-20f) r[i] = 0;
            }
        }
    }
}
