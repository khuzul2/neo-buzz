// Neo Voice to MIDI's trigger classifier on the Amateur Vocal Percussion set (docs/specs/voice-to-midi.md §4.3): VoiceFeatures (Fast 18 ms and
// Accurate 30 ms windows) with the VoiceClassifier, against the PT1 k-NN on TriggerFeatures, with the onset detector at the 110 ms minimum
// interval a voice wants. Per participant and modality: train on the first N detected hits of each class recording, test on the rest of
// those recordings (the "same" test), on the participant's improvisation, and trained on Personal and tested on Fixed; 4 classes (kick, snare,
// closed hat, open hat) and 3 (the hats merged). Also the reject: trained on kick, snare and closed hat, how many open hats are "none of these".

using System.Text;
using NeoBuzz.Dsp;

namespace NeoBuzz.PitchEval
{
    internal static class VoiceTriggers
    {
        sealed class Set
        {
            public string Name;
            public Func<Lab.H, float[]> Extract;
            public int Dim;
            public bool Knn;      // the PT1 classifier on TriggerFeatures
        }

        sealed class Tally
        {
            public long Annotated, Detected, Correct, Rejected;
            public readonly List<double> PerParticipant = new();
            public double OverDetected => Detected == 0 ? double.NaN : (double)Correct / Detected;
            public double EndToEnd => Annotated == 0 ? double.NaN : (double)Correct / Annotated;
        }

        static int Map(int c, int classes) => classes == 3 && c == 3 ? 2 : c;

        static Func<float[], int, TriggerResult> Classifier(Set set, List<(int Cls, float[] F)> train, int classes)
        {
            if (set.Knn)
            {
                var knn = new TriggerClassifier();
                foreach (var (c, f) in train) knn.Add(c, f);
                knn.Train();
                return (f, _) => knn.Classify(f);
            }
            var lda = new VoiceClassifier();
            foreach (var (c, f) in train) lda.Add(c, f);
            lda.Train();
            return (f, _) => lda.Classify(f);
        }

        static Tally Run(List<Lab.P> data, Dictionary<Lab.H, float[]> cache, Set set, string scenario, int classes, int n)
        {
            var t = new Tally();
            foreach (var p in data)
            {
                // Personal -> Fixed pairs a participant's two modalities.
                if (scenario == "cross" && !p.Name.EndsWith("/Personal")) continue;
                var train = new List<(int, float[])>();
                var test = new List<(int Cls, float[] F)>(); int testAnn = 0;
                var trainSource = p;
                for (int c = 0; c < 4; c++)
                {
                    int take = Math.Min(n, trainSource.Class[c].Count);
                    for (int k = 0; k < take; k++) train.Add((Map(c, classes), cache[trainSource.Class[c][k]]));
                }
                if (scenario == "same")
                    for (int c = 0; c < 4; c++)
                    {
                        int take = Math.Min(n, p.Class[c].Count);
                        for (int k = take; k < p.Class[c].Count; k++) test.Add((Map(c, classes), cache[p.Class[c][k]]));
                        testAnn += Math.Max(0, p.Ann[c] - take);
                    }
                else if (scenario == "improv")
                {
                    foreach (var h in p.Improv) test.Add((Map(h.Cls, classes), cache[h]));
                    testAnn = p.ImprovAnn;
                }
                else
                {
                    var fx = data.FirstOrDefault(q => q.Name == p.Name.Replace("/Personal", "/Fixed"));
                    if (fx == null) continue;
                    for (int c = 0; c < 4; c++) { foreach (var h in fx.Class[c]) test.Add((Map(c, classes), cache[h])); testAnn += fx.Ann[c]; }
                }
                if (test.Count == 0) continue;
                var cls = Classifier(set, train, classes);
                int right = 0, rej = 0;
                foreach (var (c, f) in test)
                {
                    var r = cls(f, c);
                    if (r.Class < 0) rej++; else if (r.Class == c) right++;
                }
                t.Annotated += testAnn; t.Detected += test.Count; t.Correct += right; t.Rejected += rej;
                t.PerParticipant.Add((double)right / test.Count);
            }
            return t;
        }

        public static void Run(string dir, StringBuilder md)
        {
            var data = Lab.Collect(dir, 0.11);
            int rate = data[0].Class[0][0].Rate;
            Console.WriteLine($"VoiceTriggers: {data.Count} participant-modalities at 110 ms minimum interval");
            var sets = new List<Set>();
            var tf18 = new TriggerFeatures(rate, 0.018);
            sets.Add(new Set { Name = "PT1: k-NN on 19 features, 18 ms", Knn = true, Dim = TriggerFeatures.Dimension, Extract = h => { var f = new float[TriggerFeatures.Dimension]; tf18.Extract(h.Ring, h.Ring.Length - 1, h.Onset, f); return f; } });
            var vf18 = new VoiceFeatures(rate, VoiceFeatures.FastSeconds);
            sets.Add(new Set { Name = "Voice to MIDI Fast: 44 features, 18 ms", Dim = VoiceFeatures.Dimension, Extract = h => { var f = new float[VoiceFeatures.Dimension]; vf18.Extract(h.Ring, h.Ring.Length - 1, h.Onset, f); return f; } });
            var vf30 = new VoiceFeatures(rate, VoiceFeatures.AccurateSeconds);
            sets.Add(new Set { Name = "Voice to MIDI Accurate: 44 features, 30 ms", Dim = VoiceFeatures.Dimension, Extract = h => { var f = new float[VoiceFeatures.Dimension]; vf30.Extract(h.Ring, h.Ring.Length - 1, h.Onset, f); return f; } });
            var caches = new List<Dictionary<Lab.H, float[]>>();
            foreach (var set in sets)
            {
                var all = data.SelectMany(p => p.Class.SelectMany(l => l).Concat(p.Improv)).ToList();
                var arr = new float[all.Count][];
                // The extractors keep scratch buffers: one at a time.
                for (int i = 0; i < all.Count; i++) arr[i] = set.Extract(all[i]);
                var cache = new Dictionary<Lab.H, float[]>();
                for (int i = 0; i < all.Count; i++) cache[all[i]] = arr[i];
                caches.Add(cache);
            }

            md.AppendLine("## Neo Voice to MIDI: trigger classifier on the Amateur Vocal Percussion set");
            md.AppendLine();
            md.AppendLine("The onset detector runs at a 110 ms minimum interval (a voice's second onset, the vowel, is not a hit). Hits are matched to annotations within 50 ms; per participant and modality (56 sets) the classifier is trained on the first N detected hits of each class recording and tested on the rest of those recordings (**same**), on the improvisation (**improv**) and, trained on Personal, on all of Fixed (**cross**). **Of detected** is correct over the hits found; **end to end** is correct over all annotated hits. Fast and Accurate are the feature windows (18 and 30 ms from 3 ms before the onset: the hit is classified about 15 and 27 ms after it starts).");
            md.AppendLine();
            md.AppendLine("| Features and classifier | Test | N | 4 classes of detected | 4 end to end | 3 classes of detected | 3 end to end | 4 rejected |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
            foreach (var scenario in new[] { "same", "improv", "cross" })
                foreach (int n in scenario == "same" ? new[] { 10, 15 } : new[] { 15 })
                    for (int s = 0; s < sets.Count; s++)
                    {
                        var a4 = Run(data, caches[s], sets[s], scenario, 4, n);
                        var a3 = Run(data, caches[s], sets[s], scenario, 3, n);
                        md.AppendLine($"| {sets[s].Name} | {scenario} | {n} | {Fmt.P(a4.OverDetected)} | {Fmt.P(a4.EndToEnd)} | {Fmt.P(a3.OverDetected)} | {Fmt.P(a3.EndToEnd)} | {Fmt.P(a4.Detected == 0 ? 0 : (double)a4.Rejected / a4.Detected)} |");
                    }
            md.AppendLine();

            md.AppendLine("Per participant (the 56 participant-modality sets), \"same\" test with 15 examples per class; accuracy of detected hits:");
            md.AppendLine();
            md.AppendLine("| Features and classifier | Classes | mean | median | worst | 10th percentile | sets at 85 % or more |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            for (int s = 0; s < sets.Count; s++)
                foreach (int classes in new[] { 4, 3 })
                {
                    var t = Run(data, caches[s], sets[s], "same", classes, 15);
                    var v = t.PerParticipant.OrderBy(x => x).ToList();
                    md.AppendLine($"| {sets[s].Name} | {classes} | {Fmt.P(v.Average())} | {Fmt.P(v[v.Count / 2])} | {Fmt.P(v[0])} | {Fmt.P(v[v.Count / 10])} | {v.Count(x => x >= 0.85)} of {v.Count} |");
                }
            md.AppendLine();

            // Reject: trained on kick, snare, closed hat; the open hats are foreign.
            md.AppendLine("Reject (\"none of these\"): trained on kick, snare and closed hat (15 each), tested on the rest of those (kept) and on the participant's open hats (foreign, should be rejected):");
            md.AppendLine();
            md.AppendLine("| Features and classifier | RejectScale | known sounds rejected | foreign sounds rejected |");
            md.AppendLine("|---|---:|---:|---:|");
            for (int s = 1; s < sets.Count; s++)
                foreach (float scale in new[] { 2f, 3f, 4f })
                {
                    long keptRej = 0, kept = 0, forRej = 0, forTot = 0;
                    foreach (var p in data)
                    {
                        var lda = new VoiceClassifier { RejectScale = scale };
                        for (int c = 0; c < 3; c++) for (int k = 0; k < Math.Min(15, p.Class[c].Count); k++) lda.Add(c, caches[s][p.Class[c][k]]);
                        if (!lda.Train()) continue;
                        for (int c = 0; c < 3; c++) for (int k = 15; k < p.Class[c].Count; k++) { kept++; if (lda.Classify(caches[s][p.Class[c][k]]).Class < 0) keptRej++; }
                        foreach (var h in p.Class[3]) { forTot++; if (lda.Classify(caches[s][h]).Class < 0) forRej++; }
                    }
                    md.AppendLine($"| {sets[s].Name} | {scale:F0} | {Fmt.P(keptRej / (double)Math.Max(1, kept))} | {Fmt.P(forRej / (double)Math.Max(1, forTot))} |");
                }
            md.AppendLine();
            md.AppendLine("Reading: an open hat sounds like a closed hat to the classifier more often than not (it is the same \"tss\" for longer), so rejection of it is a modest check; it is there for the click, the clap and the cough.");
            md.AppendLine();
        }
    }
}
