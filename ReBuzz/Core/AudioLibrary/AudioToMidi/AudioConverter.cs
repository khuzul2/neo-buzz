#nullable disable
// Fork (docs/specs/pitch.md section 9): the front door of "Convert to MIDI". Takes audio (any rate, any channel count already mixed to
// mono by the caller), runs the chosen converter and returns notes in seconds. Offline and cancellable; no session, no UI.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core.AudioLibrary.AudioToMidi
{
    internal static class AudioConverter
    {
        /// <summary>Audio longer than this is refused (the work grows with it, and a result of tens of thousands of notes is not useful).</summary>
        public const double MaxSeconds = 20 * 60;

        public static ConvertResult Run(float[] mono, int rate, ConvertOptions o, Action<double> progress = null, CancellationToken cancel = default, BasicPitchModel model = null)
        {
            o ??= new ConvertOptions();
            if (mono == null || mono.Length == 0 || rate <= 0) throw new ArgumentException("There is no audio to convert.");
            double seconds = mono.Length / (double)rate;
            if (seconds > MaxSeconds) throw new ArgumentException($"The audio is {seconds / 60:0.#} minutes; Convert to MIDI takes up to {MaxSeconds / 60:0} minutes (trim the clip first).");
            var clock = Stopwatch.StartNew();
            var r = new ConvertResult { Mode = o.Mode, AudioSeconds = seconds };
            switch (o.Mode)
            {
                case ConvertMode.Melody:
                    r.Notes = MelodyConverter.Convert(mono, rate, o, progress, cancel);
                    break;
                case ConvertMode.Drums:
                    r.Notes = DrumConverter.Convert(mono, rate, o, progress, cancel);
                    break;
                default:
                    r.Notes = Harmony(mono, rate, o, progress, cancel, model);
                    break;
            }
            clock.Stop();
            r.WorkSeconds = clock.Elapsed.TotalSeconds;
            r.Summary = Describe(r);
            return r;
        }

        static System.Collections.Generic.List<ConvertedNote> Harmony(float[] x, int rate, ConvertOptions o, Action<double> progress, CancellationToken cancel, BasicPitchModel model)
        {
            var audio = rate == BasicPitchNotes.SampleRate ? x : Resampler.Resample(x, rate, BasicPitchNotes.SampleRate);
            bool own = model == null;
            model ??= new BasicPitchModel();
            try
            {
                var output = model.Run(audio, p => progress?.Invoke(0.95 * p), cancel);
                var settings = new BasicPitchNotes.Settings
                {
                    OnsetThreshold = o.OnsetThreshold, FrameThreshold = o.FrameThreshold,
                    MinNoteFrames = BasicPitchNotes.MinFramesOf(o.HarmonyMinNoteMs),
                    MinHz = o.MinHz, MaxHz = o.MaxHz, PitchBends = o.PitchBends,
                };
                var notes = BasicPitchNotes.ToNotes(output, settings);
                progress?.Invoke(1);
                return notes;
            }
            finally { if (own) model.Dispose(); }
        }

        /// <summary>"23 notes, C3 to G4, in 0.4 s" for the status line and the agent.</summary>
        public static string Describe(ConvertResult r)
        {
            string what = r.Mode == ConvertMode.Drums ? "hits" : "notes";
            if (r.Notes.Count == 0) return $"No {what} found in {r.AudioSeconds:0.#} s of audio.";
            if (r.Mode == ConvertMode.Drums)
            {
                var groups = r.Notes.GroupBy(n => n.Label).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
                return $"{r.Notes.Count} {what} ({string.Join(", ", groups)}) in {r.AudioSeconds:0.#} s of audio.";
            }
            int lo = r.Notes.Min(n => n.Pitch), hi = r.Notes.Max(n => n.Pitch);
            return $"{r.Notes.Count} {what}, {MidiNames.Of(lo)} to {MidiNames.Of(hi)}, in {r.AudioSeconds:0.#} s of audio.";
        }
    }
}
