#nullable disable
// Fork (docs/specs/pitch.md section 9.3): the Basic Pitch network (Spotify, Apache-2.0; 230 KB, nmp.onnx from the upstream repository's
// saved_models/icassp_2022) run by ONNX Runtime on the CPU, the way the stem models are. The graph takes the waveform (22.05 kHz mono,
// 43844 samples) and computes its own constant-Q transform, so there is no spectrogram here; it answers with the note, onset and contour
// activations of two seconds. The model file is embedded in ReBuzz.Engine.dll (3rdParty/basic-pitch, with the licence and NOTICE that ship
// in Licenses). Runs off the UI and audio threads; cancel stops between windows and ends a run in progress.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ReBuzz.Core.AudioLibrary.AudioToMidi
{
    internal sealed class BasicPitchModel : IDisposable
    {
        public const string ResourceName = "ReBuzz.BasicPitch.nmp.onnx";
        const string InputName = "serving_default_input_2:0";
        static readonly string[] OutputNames = { "StatefulPartitionedCall:1", "StatefulPartitionedCall:2", "StatefulPartitionedCall:0" };   // note, onset, contour

        readonly InferenceSession session;
        readonly RunOptions runOptions = new();

        /// <summary>The model bundled with the program, or the file at <paramref name="path"/> (tests, a newer upstream model).</summary>
        public BasicPitchModel(string path = null)
        {
            var options = OnnxStemPassOptions();
            session = path != null ? new InferenceSession(path, options) : new InferenceSession(Embedded(), options);
        }

        static SessionOptions OnnxStemPassOptions() => new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };

        public static byte[] Embedded()
        {
            foreach (var assembly in new[] { typeof(BasicPitchModel).Assembly, Assembly.GetExecutingAssembly() }.Distinct())
            {
                using var s = assembly.GetManifestResourceStream(ResourceName);
                if (s == null) continue;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            throw new FileNotFoundException("The Basic Pitch model isn't in this build (" + ResourceName + ").");
        }

        /// <summary>The activations for audio already at 22.05 kHz, mono.</summary>
        public BasicPitchNotes.Output Run(float[] audio22k, Action<double> progress, CancellationToken cancel)
        {
            int count = BasicPitchNotes.WindowCount(audio22k.Length);
            var windows = new List<(float[], float[], float[])>(count);
            int index = 0;
            foreach (var w in BasicPitchNotes.Windows(audio22k))
            {
                cancel.ThrowIfCancellationRequested();
                using var registration = cancel.Register(() => runOptions.Terminate = true);
                var input = new DenseTensor<float>(w, new[] { 1, BasicPitchNotes.WindowSamples, 1 });
                try
                {
                    using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(InputName, input) }, OutputNames, runOptions);
                    var arrays = results.Select(r => r.AsTensor<float>().ToArray()).ToArray();
                    windows.Add((arrays[0], arrays[1], arrays[2]));
                }
                catch (OnnxRuntimeException) when (cancel.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancel);
                }
                progress?.Invoke(++index / (double)Math.Max(1, count));
            }
            return BasicPitchNotes.Join(windows, audio22k.Length);
        }

        public void Dispose()
        {
            runOptions.Dispose();
            session.Dispose();
        }
    }
}
