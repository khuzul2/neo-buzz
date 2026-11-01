#nullable disable
// Neo analysis library (docs/specs/mcp-server.md §7): masking between tracks. Each track becomes
// band levels over time (critical bands, ~85 ms frames). Two tracks fight in a band when, in the
// same frames, both are active there (above an absolute level and near their own loudest band)
// and within a few dB of each other. Severity weighs how much of the time they overlap (of the
// time the less active of the two plays there) by how close their levels are.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Analysis
{
    /// <summary>One track's band levels over time.</summary>
    internal sealed class BandFrames
    {
        public string Name;
        /// <summary>Band edges, Hz (bands = edges − 1).</summary>
        public double[] Edges;
        public double FrameSeconds;
        /// <summary>Level per frame and band, dB RMS (−∞ silent).</summary>
        public double[][] Db;
        public int Frames => Db.Length;
        public int BandCount => Edges.Length - 1;

        /// <summary>Band levels of a track (channels' mean power), in Hann frames of about 85 ms every half frame.</summary>
        public static BandFrames Analyze(string name, float[] left, float[] right, int rate, double[] edges = null)
        {
            edges ??= Bands.BarkEdges;
            int size = Fft.Nearest(0.085 * rate), hop = size / 2;
            int frames = Stft.Frames(left.Length, size, hop);
            var db = new double[frames][];
            double binHz = rate / (double)size;
            Stft.Run(left, right, size, hop, (f, p) =>
            {
                var row = new double[edges.Length - 1];
                for (int b = 0; b < row.Length; b++) row[b] = Spectrum.Db(Spectrum.Integrate(p, binHz, edges[b], edges[b + 1]));
                db[f] = row;
            });
            return new BandFrames { Name = name, Edges = edges, FrameSeconds = hop / (double)rate, Db = db };
        }
    }

    internal sealed class MaskingOptions
    {
        /// <summary>A band is active when its level is at least this (dB RMS) …</summary>
        public double ActiveDb = -50;
        /// <summary>… and no more than this below the track's loudest band in the same frame.</summary>
        public double BelowOwnPeakDb = 24;
        /// <summary>Levels within this many dB of each other fight; closeness falls off as 1 − (Δ/this)².</summary>
        public double WithinDb = 6;
        /// <summary>Bands (and pairs) below this severity are left out.</summary>
        public double MinSeverity = 0.05;
        /// <summary>The less active track must play in the band at least this long.</summary>
        public double MinSeconds = 0.1;
    }

    internal sealed class MaskingBand
    {
        public int Band;
        public double LoHz, HiHz;
        public double CentreHz => Math.Sqrt(LoHz * HiHz);
        /// <summary>0 … 1: overlap weighted by closeness.</summary>
        public double Severity;
        /// <summary>Frames where both fight, over the frames the less active track plays in the band, 0 … 1.</summary>
        public double Overlap;
        /// <summary>How long they fight, seconds.</summary>
        public double Seconds;
        /// <summary>Mean levels of each track (dB RMS) while they fight.</summary>
        public double LevelA, LevelB;
    }

    internal sealed class MaskingPair
    {
        public string A, B;
        /// <summary>The worst band's severity.</summary>
        public double Severity;
        /// <summary>Bands where they fight, worst first.</summary>
        public List<MaskingBand> Bands = new();
    }

    internal static class Masking
    {
        /// <summary>Every pair of tracks that fights somewhere, worst first.</summary>
        public static List<MaskingPair> Table(IReadOnlyList<BandFrames> tracks, MaskingOptions options = null)
        {
            options ??= new MaskingOptions();
            var actives = tracks.Select(t => Active(t, options)).ToList();
            var pairs = new List<MaskingPair>();
            for (int i = 0; i < tracks.Count; i++)
                for (int j = i + 1; j < tracks.Count; j++)
                {
                    var pair = Pair(tracks[i], tracks[j], actives[i], actives[j], options);
                    if (pair.Bands.Count > 0) pairs.Add(pair);
                }
            return pairs.OrderByDescending(p => p.Severity).ToList();
        }

        public static MaskingPair Pair(BandFrames a, BandFrames b, MaskingOptions options = null)
        {
            options ??= new MaskingOptions();
            return Pair(a, b, Active(a, options), Active(b, options), options);
        }

        static MaskingPair Pair(BandFrames a, BandFrames b, bool[][] activeA, bool[][] activeB, MaskingOptions o)
        {
            if (a.BandCount != b.BandCount || Math.Abs(a.FrameSeconds - b.FrameSeconds) > 1e-9)
                throw new ArgumentException("Both tracks need the same bands and frames (same sample rate).");
            var pair = new MaskingPair { A = a.Name, B = b.Name };
            int frames = Math.Min(a.Frames, b.Frames);
            for (int k = 0; k < a.BandCount; k++)
            {
                int countA = 0, countB = 0, both = 0;
                double close = 0, sumA = 0, sumB = 0;
                for (int f = 0; f < frames; f++)
                {
                    bool x = activeA[f][k], y = activeB[f][k];
                    if (x) countA++;
                    if (y) countB++;
                    if (!x || !y) continue;
                    double d = Math.Abs(a.Db[f][k] - b.Db[f][k]);
                    if (d > o.WithinDb) continue;
                    both++;
                    close += 1 - (d / o.WithinDb) * (d / o.WithinDb);
                    sumA += a.Db[f][k];
                    sumB += b.Db[f][k];
                }
                int fewer = Math.Min(countA, countB);
                if (both == 0 || fewer * a.FrameSeconds < o.MinSeconds) continue;
                double severity = close / fewer;
                if (severity < o.MinSeverity) continue;
                pair.Bands.Add(new MaskingBand
                {
                    Band = k,
                    LoHz = a.Edges[k],
                    HiHz = a.Edges[k + 1],
                    Severity = severity,
                    Overlap = both / (double)fewer,
                    Seconds = both * a.FrameSeconds,
                    LevelA = sumA / both,
                    LevelB = sumB / both,
                });
            }
            pair.Bands.Sort((x, y) => y.Severity.CompareTo(x.Severity));
            pair.Severity = pair.Bands.Count > 0 ? pair.Bands[0].Severity : 0;
            return pair;
        }

        static bool[][] Active(BandFrames t, MaskingOptions o)
        {
            var active = new bool[t.Frames][];
            for (int f = 0; f < t.Frames; f++)
            {
                var row = t.Db[f];
                double max = row.Max();
                active[f] = new bool[row.Length];
                for (int k = 0; k < row.Length; k++)
                    active[f][k] = row[k] >= o.ActiveDb && row[k] >= max - o.BelowOwnPeakDb;
            }
            return active;
        }
    }
}
