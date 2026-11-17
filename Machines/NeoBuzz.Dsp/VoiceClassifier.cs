#nullable disable
// Neo Voice to MIDI (docs/specs/voice-to-midi.md §4): the trigger classifier on VoiceFeatures. A regularised linear discriminant:
// features standardised by the training set, one mean per class, one pooled within-class covariance shrunk towards its own
// average variance (Lambda), and the class of the nearest mean in the whitened space (equal priors). A hit further than
// RejectScale times the class's typical distance is "none of these". Trained from about 15 examples per sound (a few seconds of
// the user's own sounds), off the audio thread: Train builds an immutable model and publishes it; Classify reads the published
// model and allocates nothing. The examples save and load as plain text (Serialize, Deserialize: "NBVOICE 1 <dim> <count>").
// On the Amateur Vocal Percussion set (4 classes, 15 examples, 110 ms minimum interval) it is 4 to 5 points better than the
// PT1 k-NN on VoiceFeatures and 8 to 9 better than the k-NN on TriggerFeatures (docs/specs/voice-to-midi.md §4.3).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NeoBuzz.Dsp
{
    internal sealed class VoiceClassifier
    {
        public const int Dim = VoiceFeatures.Dimension;
        public readonly int MaxClasses;
        public const int MaxPerClass = 64;

        /// <summary>Shrinkage of the covariance towards its mean variance, 0 ... 1.</summary>
        public float Lambda = 0.5f;
        /// <summary>A hit further than this many typical distances (of its class) from the class mean is "none of these"; 0 turns it off.</summary>
        public float RejectScale = 3.0f;

        sealed class Model
        {
            public readonly float[] Mean = new float[Dim], Inv = new float[Dim];
            public float[] Means, Chol, Ref;
            public bool[] Has;
        }

        readonly List<float[]>[] examples;
        volatile Model model;
        readonly float[] w = new float[Dim], d2;
        readonly float[] z = new float[Dim];

        public VoiceClassifier(int maxClasses = 8)
        {
            MaxClasses = maxClasses;
            examples = new List<float[]>[maxClasses];
            for (int c = 0; c < maxClasses; c++) examples[c] = new List<float[]>();
            d2 = new float[maxClasses];
        }

        public bool Trained => model != null;
        public int ExamplesOf(int cls) { lock (examples) return examples[cls].Count; }
        public int Count { get { lock (examples) { int n = 0; foreach (var l in examples) n += l.Count; return n; } } }
        public int ClassesTrained { get { var m = model; if (m == null) return 0; int n = 0; foreach (bool h in m.Has) if (h) n++; return n; } }

        public void Clear() { lock (examples) foreach (var l in examples) l.Clear(); model = null; }
        public void Clear(int cls) { lock (examples) examples[cls].Clear(); }

        /// <summary>Adds one example (copied). The oldest of a class goes when it holds <see cref="MaxPerClass"/>.</summary>
        public bool Add(int cls, float[] features)
        {
            if (cls < 0 || cls >= MaxClasses || features == null || features.Length < Dim) return false;
            var copy = new float[Dim];
            Array.Copy(features, copy, Dim);
            lock (examples)
            {
                var l = examples[cls];
                if (l.Count >= MaxPerClass) l.RemoveAt(0);
                l.Add(copy);
            }
            return true;
        }

        /// <summary>Builds and publishes the model from the examples. False with none.</summary>
        public bool Train()
        {
            float[][][] snapshot;
            lock (examples)
            {
                snapshot = new float[MaxClasses][][];
                for (int c = 0; c < MaxClasses; c++) snapshot[c] = examples[c].ToArray();
            }
            int total = 0, classes = 0;
            for (int c = 0; c < MaxClasses; c++) { total += snapshot[c].Length; if (snapshot[c].Length > 0) classes++; }
            if (total < 2 || classes < 1) { model = null; return false; }
            var m = new Model { Means = new float[MaxClasses * Dim], Ref = new float[MaxClasses], Has = new bool[MaxClasses], Chol = new float[Dim * Dim] };
            var mean = new double[Dim]; var sd = new double[Dim];
            foreach (var cls in snapshot) foreach (var x in cls) for (int i = 0; i < Dim; i++) mean[i] += x[i];
            for (int i = 0; i < Dim; i++) mean[i] /= total;
            foreach (var cls in snapshot) foreach (var x in cls) for (int i = 0; i < Dim; i++) { double e = x[i] - mean[i]; sd[i] += e * e; }
            for (int i = 0; i < Dim; i++) { sd[i] = Math.Max(Math.Sqrt(sd[i] / total), 1e-6); m.Mean[i] = (float)mean[i]; m.Inv[i] = (float)(1.0 / sd[i]); }
            var zs = new float[MaxClasses][][];
            var cm = new double[MaxClasses][];
            for (int c = 0; c < MaxClasses; c++)
            {
                zs[c] = new float[snapshot[c].Length][];
                cm[c] = new double[Dim];
                for (int k = 0; k < snapshot[c].Length; k++)
                {
                    var zz = new float[Dim];
                    for (int i = 0; i < Dim; i++) zz[i] = (float)((snapshot[c][k][i] - mean[i]) / sd[i]);
                    zs[c][k] = zz;
                    for (int i = 0; i < Dim; i++) cm[c][i] += zz[i];
                }
                if (zs[c].Length > 0) { m.Has[c] = true; for (int i = 0; i < Dim; i++) { cm[c][i] /= zs[c].Length; m.Means[c * Dim + i] = (float)cm[c][i]; } }
            }
            var S = new double[Dim, Dim];
            int dof = 0;
            for (int c = 0; c < MaxClasses; c++)
                foreach (var zz in zs[c])
                {
                    dof++;
                    for (int a = 0; a < Dim; a++) { double ea = zz[a] - cm[c][a]; for (int b = 0; b <= a; b++) S[a, b] += ea * (zz[b] - cm[c][b]); }
                }
            dof = Math.Max(1, dof);
            double trace = 0;
            for (int a = 0; a < Dim; a++) trace += S[a, a] / dof;
            double lam = Math.Clamp(Lambda, 0.01, 1.0);
            for (int a = 0; a < Dim; a++)
                for (int b = 0; b <= a; b++)
                {
                    S[a, b] = (1 - lam) * S[a, b] / dof + (a == b ? lam * trace / Dim : 0);
                    if (a == b) S[a, b] += 1e-6;
                    S[b, a] = S[a, b];
                }
            for (int i = 0; i < Dim; i++)
                for (int j = 0; j <= i; j++)
                {
                    double s = S[i, j];
                    for (int k = 0; k < j; k++) s -= m.Chol[i * Dim + k] * (double)m.Chol[j * Dim + k];
                    m.Chol[i * Dim + j] = i == j ? (float)Math.Sqrt(Math.Max(s, 1e-9)) : (float)(s / m.Chol[j * Dim + j]);
                }
            // Each class's typical distance: the root of the mean squared whitened distance of its examples to its mean, grown for the few examples
            // (a mean of n examples is nearer its own examples than a new one: sqrt(n / (n - 1))) and floored at the classes' median.
            var tw = new float[Dim]; var ty = new float[Dim];
            var refs = new List<float>();
            for (int c = 0; c < MaxClasses; c++)
            {
                if (zs[c].Length == 0) continue;
                double sum = 0;
                foreach (var zz in zs[c])
                {
                    for (int i = 0; i < Dim; i++) tw[i] = zz[i] - m.Means[c * Dim + i];
                    sum += WhitenedSquare(m, tw, ty);
                }
                int n = zs[c].Length;
                double r = Math.Sqrt(sum / n) * Math.Sqrt(n / (double)Math.Max(1, n - 1));
                m.Ref[c] = (float)Math.Max(r, 1e-3);
                if (n > 1) refs.Add(m.Ref[c]);
            }
            if (refs.Count > 0)
            {
                refs.Sort();
                float floor = 0.8f * refs[refs.Count / 2];
                for (int c = 0; c < MaxClasses; c++) if (m.Has[c]) m.Ref[c] = Math.Max(m.Ref[c], floor);
            }
            else for (int c = 0; c < MaxClasses; c++) if (m.Has[c]) m.Ref[c] = 1;
            model = m;
            return true;
        }

        // w holds the difference; the squared length of L^-1 w.
        static float WhitenedSquare(Model m, float[] w, float[] y)
        {
            double s = 0;
            for (int i = 0; i < Dim; i++)
            {
                double t = w[i];
                int row = i * Dim;
                for (int k = 0; k < i; k++) t -= m.Chol[row + k] * y[k];
                y[i] = (float)(t / m.Chol[row + i]);
                s += y[i] * (double)y[i];
            }
            return (float)s;
        }

        /// <summary>Classifies one feature vector. Class −1: untrained or "none of these". Allocation-free; one caller at a time.</summary>
        public TriggerResult Classify(float[] features)
        {
            var r = new TriggerResult { Class = -1 };
            var m = model;
            if (m == null) return r;
            int best = -1;
            float bestD = float.MaxValue;
            for (int c = 0; c < MaxClasses; c++)
            {
                if (!m.Has[c]) { d2[c] = float.MaxValue; continue; }
                for (int i = 0; i < Dim; i++) w[i] = (features[i] - m.Mean[i]) * m.Inv[i] - m.Means[c * Dim + i];
                float d = WhitenedSquare(m, w, z);
                d2[c] = d;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best < 0) return r;
            // Posterior of the winner under equal priors and the whitened squared distances (temperature 2 Dim / 8: the shrunk covariance is optimistic).
            double zsum = 0;
            for (int c = 0; c < MaxClasses; c++) if (m.Has[c]) zsum += PortableMath.Exp(-(d2[c] - bestD) * 0.5 / 4.0);
            float posterior = (float)(1.0 / zsum);
            float rel = (float)Math.Sqrt(bestD) / m.Ref[best];
            r.Distance = rel;
            r.Confidence = Math.Clamp(posterior * (1f - 0.5f * Math.Min(1f, rel / Math.Max(RejectScale, 1f))), 0f, 1f);
            r.Class = RejectScale > 0 && rel > RejectScale ? -1 : best;
            return r;
        }

        /// <summary>The examples as plain text: a header line, then one line per example: class, then the features.</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            lock (examples)
            {
                int n = 0; foreach (var l in examples) n += l.Count;
                sb.Append("NBVOICE 1 ").Append(Dim).Append(' ').Append(n).Append('\n');
                for (int c = 0; c < MaxClasses; c++)
                    foreach (var x in examples[c])
                    {
                        sb.Append(c);
                        for (int d = 0; d < Dim; d++) sb.Append(' ').Append(x[d].ToString("R", CultureInfo.InvariantCulture));
                        sb.Append('\n');
                    }
            }
            return sb.ToString();
        }

        /// <summary>Replaces the examples with those in <paramref name="text"/> and trains. False for text this version can't read (the classifier is then empty).</summary>
        public bool Deserialize(string text)
        {
            Clear();
            if (string.IsNullOrWhiteSpace(text)) return false;
            try
            {
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var head = lines[0].Trim().Split(' ');
                if (head.Length != 4 || head[0] != "NBVOICE" || head[1] != "1" || head[2] != Dim.ToString(CultureInfo.InvariantCulture)) return false;
                var f = new float[Dim];
                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Trim().Split(' ');
                    if (parts.Length != Dim + 1 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cls)) { Clear(); return false; }
                    for (int d = 0; d < Dim; d++)
                        if (!float.TryParse(parts[d + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out f[d])) { Clear(); return false; }
                    if (!Add(cls, f)) { Clear(); return false; }
                }
                return Train();
            }
            catch (Exception) { Clear(); return false; }
        }
    }
}
