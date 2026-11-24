#nullable disable
// Fork addition (neo-buzz, docs/specs/neo-audio.md, AW4 as built): the pretrained beat and downbeat network (the small model of "Beat This!", Foscarin,
// Schlueter, Widmer, ISMIR 2024; MIT, 3rdParty/beat-this) run through ONNX Runtime, and the beat track made of what it finds.
//
// The weights are never in the repository: tools/autowarp/beat_this_model.py downloads the pinned upstream checkpoint, checks its SHA-256 and exports it
// (CI does this for the package, which embeds the file). The model is found, in this order, at: the path in NEO_BEAT_THIS_MODEL, a resource of this
// assembly (the package), Gear/Models/BeatThis beside the app, and the BeatThis folder of the audio data folder. A file whose SHA-256 is not the pinned one,
// or that ONNX Runtime cannot load, is no model: there is no error, the caller falls back to the AW2 network and then AW1's DSP tracker.
// Analysis is offline (never on the audio thread). The session is made once and kept; the windows of a long piece run in parallel, one thread each.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;

namespace ReBuzz.Core.Clips
{
    /// <summary>The network: one loaded ONNX file.</summary>
    internal sealed class BeatThisNet : IDisposable
    {
        readonly InferenceSession session;

        /// <summary>Where it was loaded from ("resource" for the embedded file) and the SHA-256 of the file.</summary>
        public string Origin { get; }
        public string Sha256 { get; }

        BeatThisNet(InferenceSession s, string origin, string sha, byte[] model) { session = s; Origin = origin; Sha256 = sha; bytes = model; }

        /// <summary>The model of these bytes, or null when they are not the pinned file or ONNX Runtime cannot load them. <paramref name="expectedSha256"/> null: not checked (tests of the check).</summary>
        public static BeatThisNet Load(byte[] bytes, string origin, string expectedSha256 = BeatThis.AnyPinned)
        {
            try
            {
                string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (expectedSha256 == BeatThis.AnyPinned ? !BeatThis.IsPinned(sha) : expectedSha256 != null && !string.Equals(sha, expectedSha256, StringComparison.OrdinalIgnoreCase)) return null;
                var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, IntraOpNumThreads = 1, InterOpNumThreads = 1 };
                return new BeatThisNet(new InferenceSession(bytes, o), origin, sha, bytes);
            }
            catch (Exception) { return null; }
        }

        public static BeatThisNet LoadFile(string path, string expectedSha256 = BeatThis.AnyPinned)
        {
            try { return Load(File.ReadAllBytes(path), path, expectedSha256); }
            catch (Exception) { return null; }
        }

        /// <summary>The beat and downbeat logits of a spectrogram (frames x 128), every frame, by the reference's windows. The windows run in parallel, one thread each (the network is 2 M
        /// numbers but its attention is memory-bound, so a window does not scale across threads as well as windows do across cores); when the last few windows would leave most cores idle
        /// they run one after the other on a second session that uses all of them.</summary>
        public void Predict(float[] spect, int frames, out float[] beat, out float[] down)
        {
            var starts = BeatThisFront.WindowStarts(frames);
            var beats = new float[starts.Length][];
            var downs = new float[starts.Length][];
            int cores = Math.Max(1, Environment.ProcessorCount);
            int tail = cores > 1 && starts.Length > cores ? starts.Length % cores : 0;
            if (tail > cores / 2) tail = 0;
            int bulk = starts.Length - tail;
            Parallel.For(0, bulk, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(bulk, cores) }, w => RunWindow(session, spect, frames, starts, w, beats, downs));
            if (tail > 0)
            {
                var wide = Wide();
                for (int w = bulk; w < starts.Length; w++) RunWindow(wide, spect, frames, starts, w, beats, downs);
            }
            beat = BeatThisFront.Aggregate(frames, starts, beats);
            down = BeatThisFront.Aggregate(frames, starts, downs);
        }

        InferenceSession wideSession;
        byte[] bytes;

        InferenceSession Wide()
        {
            lock (this)
            {
                if (wideSession == null)
                {
                    var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount), InterOpNumThreads = 1 };
                    wideSession = new InferenceSession(bytes, o);
                }
                return wideSession;
            }
        }

        static void RunWindow(InferenceSession s, float[] spect, int frames, int[] starts, int w, float[][] beats, float[][] downs)
        {
            var input = new float[BeatThisFront.Chunk * BeatThisFront.Mels];
            int len = BeatThisFront.FillWindow(spect, frames, starts[w], input);
            var b = new float[len];
            var d = new float[len];
            using (var tIn = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, len, BeatThisFront.Mels }))
            using (var tB = OrtValue.CreateTensorValueFromMemory(b, new long[] { 1, len }))
            using (var tD = OrtValue.CreateTensorValueFromMemory(d, new long[] { 1, len }))
            using (var opts = new RunOptions())
                s.Run(opts, new[] { "spect" }, new[] { tIn }, new[] { "beat", "downbeat" }, new[] { tB, tD });
            beats[w] = b; downs[w] = d;
        }

        public void Dispose() { session.Dispose(); wideSession?.Dispose(); }
    }

    /// <summary>The model the app holds, found once, and the switches the tests and tools use.</summary>
    internal static class BeatThis
    {
        /// <summary>The SHA-256 of the pinned export of the small checkpoint (tools/autowarp/beat_this_model.py, 3rdParty/beat-this/NOTICE).</summary>
        public const string PinnedSha256 = "b700cc6814038bfad9a3585357cd3d2deb606e7c60453af1a8bd8e8efba5a309";
        /// <summary>The checkpoint is pinned (tools/autowarp/beat_this_model.py); an ONNX export of it is not bit-identical across operating systems and library builds
        /// (the weights are, the graph's float constants fold differently), so each export that the parity jobs checked is pinned: this one (local), then the Linux CI one.</summary>
        static readonly string[] pinnedExports = { PinnedSha256, "827132d4fd4a2aecaabceaca6d60963ebdbc3086c6c03571757633588beb7619" };
        /// <summary>Pass as the expected checksum to accept any pinned export (the default).</summary>
        public const string AnyPinned = "*pinned*";
        public static bool IsPinned(string sha) => Array.Exists(pinnedExports, e => string.Equals(e, sha, StringComparison.OrdinalIgnoreCase));
        public const string FileName = "beat_this_small.onnx";
        public const string ResourceName = "ReBuzz.BeatThis.beat_this_small.onnx";

        /// <summary>Keys the analysis cache (the AW2 networks have small versions).</summary>
        public const int Version = 1_000_001;

        static BeatThisNet found;
        static bool searched;
        static readonly object gate = new object();
        [ThreadStatic] static BeatThisNet scoped;
        [ThreadStatic] static bool scopedSet;
        static volatile BeatThisNet global;
        static volatile bool globalSet;

        /// <summary>The network in use, or null (then the AW2 network, then AW1's DSP, answer). An explicit choice of the AW2 network (<see cref="BeatModel.Scope"/> or <see cref="BeatModel.Use"/>) leaves this one out unless it is chosen too.</summary>
        public static BeatThisNet Current
        {
            get
            {
                if (scopedSet) return scoped;
                if (globalSet) return global;
                if (BeatModel.Overridden) return null;
                return Found();
            }
        }

        /// <summary>The model found on this computer (or none), whatever the switches say.</summary>
        public static BeatThisNet Found()
        {
            lock (gate)
            {
                if (searched) return found;
                searched = true;
                found = Search();
                return found;
            }
        }

        /// <summary>Forgets what was found, so the next use searches again (the tests that change the search path).</summary>
        internal static void Forget() { lock (gate) { found = null; searched = false; } }

        /// <summary>The folders searched for the file, in order (the first has the path of NEO_BEAT_THIS_MODEL).</summary>
        public static string[] Candidates() => new[]
        {
            Environment.GetEnvironmentVariable("NEO_BEAT_THIS_MODEL") ?? "",
            Path.Combine(AppContext.BaseDirectory, "Gear", "Models", "BeatThis", FileName),
            Path.Combine(ReBuzz.Core.AudioLibrary.AudioCache.DataFolder, "BeatThis", FileName),
        };

        static BeatThisNet Search()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("NEO_BEAT_THIS_MODEL");
                if (!string.IsNullOrWhiteSpace(env)) { var n = BeatThisNet.LoadFile(env); if (n != null) return n; }
                var bytes = ResourceBytes();
                if (bytes != null) { var n = BeatThisNet.Load(bytes, "resource"); if (n != null) return n; }
                foreach (var path in Candidates().Skip(1))
                    if (File.Exists(path)) { var n = BeatThisNet.LoadFile(path); if (n != null) return n; }
            }
            catch (Exception) { }
            return null;
        }

        static byte[] ResourceBytes()
        {
            foreach (var asm in new[] { typeof(BeatThis).Assembly, Assembly.GetExecutingAssembly() }.Distinct())
            {
                using var s = asm.GetManifestResourceStream(ResourceName);
                if (s == null) continue;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            return null;
        }

        /// <summary>Uses this network (null: none, the AW2 path answers) for every thread until changed (the evaluation tools).</summary>
        public static void Use(BeatThisNet net) { global = net; globalSet = true; }

        /// <summary>Uses this network (null: none) on this thread until disposed (the tests).</summary>
        public static IDisposable Scope(BeatThisNet net)
        {
            var prevSet = scopedSet; var prev = scoped;
            scoped = net; scopedSet = true;
            return new Restore(() => { scoped = prev; scopedSet = prevSet; });
        }

        sealed class Restore : IDisposable
        {
            readonly Action a; public Restore(Action a) { this.a = a; }
            public void Dispose() => a();
        }
    }

    /// <summary>The beats and downbeats of a piece of audio from the pretrained network, as a <see cref="BeatTrack"/> for the markers.</summary>
    internal static class BeatThisTracker
    {
        /// <summary>AW4: how far (ms) a beat may be moved to the attack near it (<see cref="BeatModelTracker"/>'s AW3 setting); 0: not moved (the network's own beats, 20 ms frames).</summary>
        internal static double AttackReachMs = 0;

        /// <summary>AW4: the grid's bars follow the picked downbeats (see <see cref="AudioAnalysis"/>'s BarsByDownbeats); off: the beats as the network gave them.</summary>
        internal static bool BarsFromDownbeats = true;

        /// <summary>AW4b: beats to a fraction of a frame (a parabola through the logit's top), off: the picked frames.</summary>
        internal static bool SubFrame = true;

        /// <summary>AW4b: ms added to every beat (negative: earlier). The network's beats sit late against the attacks; this is the measured median (docs/specs/neo-audio.md, AW4b).</summary>
        internal static double ShiftMs = 0;

        /// <summary>The raw answer: logits for every frame, and the beats and downbeats (frames of 20 ms) the reference's post-processor takes from them.</summary>
        internal sealed class Result
        {
            public float[] Beat, Down;
            public double[] BeatFrames, DownFrames, RefinedFrames;
            public int Frames;
        }

        public static Result Run(AudioSource src, BeatThisNet net)
        {
            var mono = BeatFront.Mono22k(src);
            var spect = BeatThisFront.Compute(mono, out int frames);
            var r = new Result { Frames = frames };
            net.Predict(spect, frames, out r.Beat, out r.Down);
            BeatThisFront.PickPeaks(r.Beat, r.Down, out r.BeatFrames, out r.DownFrames);
            r.RefinedFrames = BeatThisFront.Refine(r.Beat, r.BeatFrames);
            return r;
        }

        public static BeatTrack Track(AudioSource src, BeatThisNet net) => TrackFrom(src, Run(src, net));

        /// <summary>The track of an answer already computed (the evaluation tool varies the settings after this point).</summary>
        internal static BeatTrack TrackFrom(AudioSource src, Result r)
        {
            int rate = src.SampleRate;
            int n = r.BeatFrames.Length;
            if (n < 4) return null;
            var beats = new double[n];
            var down = new double[n];
            var conf = new double[n];
            int di = 0;
            for (int i = 0; i < n; i++)
            {
                beats[i] = ((SubFrame && r.RefinedFrames != null ? r.RefinedFrames[i] : r.BeatFrames[i]) / BeatThisFront.Fps + ShiftMs / 1000.0) * rate;
                while (di < r.DownFrames.Length && r.DownFrames[di] < r.BeatFrames[i] - 1e-9) di++;
                bool isDown = di < r.DownFrames.Length && Math.Abs(r.DownFrames[di] - r.BeatFrames[i]) < 1e-9;
                int f = (int)Math.Round(r.BeatFrames[i]);
                float lb = -1000, ld = -1000;
                for (int u = Math.Max(0, f - 1); u <= Math.Min(r.Frames - 1, f + 1); u++) { lb = Math.Max(lb, r.Beat[u]); ld = Math.Max(ld, r.Down[u]); }
                down[i] = (isDown ? 1.0 : 0.0) + 0.01 / (1.0 + Math.Exp(-ld));         // the picked downbeats vote; the activation settles a tie
                conf[i] = 1.0 / (1.0 + Math.Exp(-lb));
            }
            var track = new BeatTrack { Rate = rate, Length = src.Length, Beats = beats, Down = down, FromModel = true, ModelVersion = BeatThis.Version, Source = "beatthis", BarsFromDownbeats = BarsFromDownbeats,
                                        Low = (double[])conf.Clone(), Full = (double[])conf.Clone(), Change = new double[n] };
            if (AttackReachMs > 0) BeatModelTracker.SnapToAttacks(src, track, AttackReachMs);
            var intervals = new System.Collections.Generic.List<double>(n);
            for (int i = 1; i < n; i++) intervals.Add(track.Beats[i] - track.Beats[i - 1]);
            intervals.Sort();
            double med = intervals[intervals.Count / 2];
            track.MedianBpm = med > 0 ? 60.0 * rate / med : 0;
            return track;
        }
    }
}
