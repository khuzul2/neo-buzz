#nullable disable

// Fork (docs/specs/audio-to-midi.md, Convert to MIDI drums CD1): drum hits from audio, offline. The shared onset detector (PT1) is run
// sensitively, so that nearly every hit is a candidate; each candidate's sound before and AFTER the onset (band energies in six windows to
// 300 ms on, three band envelopes, the pitch of the low body) goes through a small multi-label network (DrumModel) that says which drums are in it:
// kick, snare, closed hat, open hat, tom, cymbal (a hit can be several at once). Offline can look ahead: how long a hat rings, whether a tom's
// tone glides down, how long and broad a cymbal's decay is are all in the features. The network was fitted on the training split of the Groove
// MIDI Dataset (CC BY 4.0), an electronic kit; see DrumModel.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NeoBuzz.Dsp;

namespace ReBuzz.Core.AudioLibrary.AudioToMidi
{
    internal static class DrumConverter
    {
        public const string Kick = "Kick", Snare = "Snare", ClosedHat = "Closed hat", OpenHat = "Open hat", LowTom = "Low tom", MidTom = "Mid tom", HighTom = "High tom", Crash = "Crash";

        /// <summary>The General MIDI note of each class (also the keys of Neo Drums).</summary>
        public static int NoteOf(string label) => label switch
        {
            Kick => 36, Snare => 38, ClosedHat => 42, OpenHat => 46, LowTom => 43, MidTom => 47, HighTom => 50, Crash => 49, _ => 37,
        };

        /// <summary>Candidate detection: sensitive on purpose (the classifier throws out what is not a hit).</summary>
        public const double CandidateThreshold = 1.5, CandidateMinMs = 20;

        public static List<ConvertedNote> Convert(float[] x, int rate, ConvertOptions o, Action<double> progress, CancellationToken cancel)
        {
            if (rate > 48000)
            {
                x = Resampler.Resample(x, rate, 48000);
                rate = 48000;
            }
            var cands = Candidates(x, rate, CandidateThreshold, CandidateMinMs, cancel);
            progress?.Invoke(0.1);
            var fe = new DrumFeatures(rate);
            var probs = new List<float[]>(cands.Count);
            var feats = new List<float[]>(cands.Count);
            var p = new float[DrumModel.Classes];
            for (int i = 0; i < cands.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                double t = cands[i].Sample / (double)rate;
                double prev = i > 0 ? t - cands[i - 1].Sample / (double)rate : 9, next = i + 1 < cands.Count ? cands[i + 1].Sample / (double)rate - t : 9;
                var f = fe.Extract(x, cands[i].Sample, prev, next);
                DrumModel.Predict(f, p);
                probs.Add((float[])p.Clone());
                feats.Add(f);
                if (i % 64 == 0) progress?.Invoke(0.1 + 0.85 * i / Math.Max(1, cands.Count));
            }
            var notes = ToNotes(cands, probs, feats, rate, o);
            progress?.Invoke(1);
            return notes;
        }

        /// <summary>The notes of the candidates' class probabilities: every class over its threshold; one hit's ring that fired a second candidate of the same class within <see cref="DedupeMs"/> is dropped (the stronger stays).</summary>
        public static double DedupeMs = 35;

        public static List<ConvertedNote> ToNotes(List<OnsetEvent> cands, List<float[]> probs, List<float[]> feats, int rate, ConvertOptions o = null)
        {
            // The menu's sensitivity (ConvertOptions.DrumThreshold, 4 = as fitted; lower finds more hits, higher fewer) scales every class's threshold by its square root.
            o ??= new ConvertOptions();
            double scale = Math.Sqrt(Math.Clamp(o.DrumThreshold, 0.5, 40) / 4.0);
            double dedupe = Math.Min(DedupeMs, Math.Max(10, o.DrumMinIntervalMs)) / 1000.0;
            var notes = new List<ConvertedNote>();
            if (cands.Count == 0) return notes;
            var levels = feats.Select(f => (double)f[DrumFeatures.LevelIndex]).OrderBy(v => v).ToList();
            double reference = levels[Math.Min(levels.Count - 1, (int)(levels.Count * 0.95))];
            var lastOf = new int[DrumModel.Classes];
            for (int c = 0; c < lastOf.Length; c++) lastOf[c] = -1;
            var noteOf = new List<(int Cand, int Class, ConvertedNote Note, float P)>();
            for (int i = 0; i < cands.Count; i++)
            {
                double t = cands[i].Sample / (double)rate;
                int velocity = MelodyConverter.VelocityOf(feats[i][DrumFeatures.LevelIndex], reference);
                for (int c = 0; c < DrumModel.Classes; c++)
                {
                    if (probs[i][c] < Math.Clamp(DrumModelData.Threshold[c] * scale, 0.02, 0.97)) continue;
                    string label = c switch
                    {
                        0 => Kick, 1 => Snare, 2 => ClosedHat, 3 => OpenHat, 5 => Crash,
                        _ => TomLabel(feats[i]),
                    };
                    var n = new ConvertedNote { Start = t, End = t + 0.1, Pitch = NoteOf(label), Velocity = velocity, Label = label };
                    int prev = lastOf[c];
                    if (prev >= 0 && t - noteOf[prev].Note.Start < dedupe)
                    {
                        if (probs[i][c] > noteOf[prev].P) { noteOf[prev] = (i, c, n, probs[i][c]); }
                        continue;
                    }
                    lastOf[c] = noteOf.Count;
                    noteOf.Add((i, c, n, probs[i][c]));
                }
            }
            return noteOf.Select(q => q.Note).OrderBy(n => n.Start).ToList();
        }

        /// <summary>A tom's note by the pitch of its body (the strongest low peak in the first 60 ms).</summary>
        static string TomLabel(float[] f)
        {
            double hz = Math.Pow(2, f[DrumFeatures.PeakHzIndex]);
            return hz < 110 ? LowTom : hz < 170 ? MidTom : HighTom;
        }

        /// <summary>Every onset the shared detector finds at this threshold (candidates for the classifier, in samples).</summary>
        public static List<OnsetEvent> Candidates(float[] x, int rate, double threshold, double minIntervalMs, CancellationToken cancel = default)
        {
            var det = new OnsetDetector(rate) { Threshold = (float)threshold, MinIntervalSeconds = Math.Max(0.01, minIntervalMs / 1000.0) };
            var list = new List<OnsetEvent>();
            int block = 1 << 16;
            var buf = new OnsetEvent[det.MaxEvents(block) + 4];
            for (int pos = 0; pos < x.Length; pos += block)
            {
                cancel.ThrowIfCancellationRequested();
                int n = Math.Min(block, x.Length - pos);
                int got = det.Process(x, pos, n, buf, 0);
                for (int i = 0; i < got; i++) list.Add(buf[i]);
            }
            return list;
        }

    }
}
