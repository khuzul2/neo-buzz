#nullable disable
// Fork (docs/specs/pitch.md section 9.3): Basic Pitch's note creation, ported from the reference (spotify/basic-pitch, Apache-2.0,
// basic_pitch/note_creation.py and inference.py): the model's three outputs (note frames, onsets, contours; 88 piano keys, 86 frames a
// second) become notes. The steps and their order are the reference's, so the notes come out the same: the onsets are completed by the
// rises of the frame activation, an onset peak starts a note that runs until the activation has been under the frame threshold for 11
// frames, the energy it used (and its neighbouring keys) is struck out, and the "melodia trick" then takes the strongest activation
// that is left and grows it both ways into a note. Pitch bends come from the contours (1/3 semitone bins) around each note's key.
// Pure: no ONNX Runtime here (BasicPitchModel has it), so the post-processing is tested on its own.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.AudioLibrary.AudioToMidi
{
    internal static class BasicPitchNotes
    {
        public const int SampleRate = 22050;
        public const int FftHop = 256;
        public const int Keys = 88, ContourBins = 264, MidiOffset = 21;
        public const int WindowSamples = 43844;               // AUDIO_N_SAMPLES: two seconds less one hop
        public const int OutputFramesPerWindow = 172;
        public const int OverlapFrames = 30;
        public const double MagicAlignmentOffset = 0.0018;

        /// <summary>The model's output for a whole recording, frame by frame: [frame * 88 + key] and [frame * 264 + bin].</summary>
        internal sealed class Output
        {
            public int Frames;
            public float[] Note, Onset, Contour;
        }

        /// <summary>A note in model frames, as the reference finds it.</summary>
        internal readonly record struct FrameNote(int Start, int End, int Pitch, double Amplitude);

        public sealed class Settings
        {
            public double OnsetThreshold = 0.5, FrameThreshold = 0.3;
            /// <summary>Notes of this many frames or fewer are dropped (the reference: 127.7 ms is 11).</summary>
            public int MinNoteFrames = 11;
            public double MinHz, MaxHz;
            public bool InferOnsets = true, MelodiaTrick = true, PitchBends = true;
        }

        public static int MinFramesOf(double milliseconds) => (int)Math.Round(milliseconds / 1000.0 * (SampleRate / (double)FftHop));

        /// <summary>The time of a model frame, seconds (the reference's mapping, with the windows' real hop instead of its approximation).</summary>
        public static double FrameTime(int frame)
        {
            int perWindow = OutputFramesPerWindow - OverlapFrames;
            int w = frame / perWindow, r = frame % perWindow;
            int hop = WindowSamples - OverlapFrames * FftHop;
            return (w * (double)hop + r * (double)FftHop) / SampleRate + MagicAlignmentOffset;
        }

        // ------------------------------------------------------------------ notes

        public static List<FrameNote> FindNotes(Output o, Settings s)
        {
            int T = o.Frames;
            var frames = (float[])o.Note.Clone();
            var onsets = (float[])o.Onset.Clone();
            var notes = new List<FrameNote>();
            if (T < 2) return notes;

            // constrain_frequency: keys outside the range are zeroed.
            int minIdx = 0, maxIdx = Keys;
            if (s.MinHz > 0) minIdx = (int)Math.Round(HzToMidi(s.MinHz) - MidiOffset);
            if (s.MaxHz > 0) maxIdx = (int)Math.Round(HzToMidi(s.MaxHz) - MidiOffset);
            for (int t = 0; t < T; t++)
                for (int k = 0; k < Keys; k++)
                    if (k < minIdx || k >= maxIdx) { frames[t * Keys + k] = 0; onsets[t * Keys + k] = 0; }

            if (s.InferOnsets && !InferOnsets(onsets, frames, T)) return notes;

            // Onset peaks (scipy argrelmax: strictly higher than both neighbours in time) at or over the threshold, latest first.
            var starts = new List<(int T, int K)>();
            for (int t = 1; t < T - 1; t++)
                for (int k = 0; k < Keys; k++)
                {
                    float v = onsets[t * Keys + k];
                    if (v > onsets[(t - 1) * Keys + k] && v > onsets[(t + 1) * Keys + k] && v >= s.OnsetThreshold) starts.Add((t, k));
                }
            starts.Reverse();                                       // np.where is time-major, the reference walks it backwards

            float frameThresh = (float)s.FrameThreshold;
            var remaining = (float[])frames.Clone();
            const int energyTol = 11;
            foreach (var (start, k) in starts)
            {
                if (start >= T - 1) continue;
                int i = start + 1, below = 0;
                while (i < T - 1 && below < energyTol)
                {
                    if (remaining[i * Keys + k] < frameThresh) below++; else below = 0;
                    i++;
                }
                i -= below;
                if (i - start <= s.MinNoteFrames) continue;
                for (int t = start; t < i; t++)
                {
                    remaining[t * Keys + k] = 0;
                    if (k < Keys - 1) remaining[t * Keys + k + 1] = 0;
                    if (k > 0) remaining[t * Keys + k - 1] = 0;
                }
                notes.Add(new FrameNote(start, i, k + MidiOffset, Mean(frames, start, i, k)));
            }

            if (s.MelodiaTrick)
            {
                // The strongest activation left, again and again: the reference takes np.argmax of the whole matrix each time (the first of equals
                // in row-major order); the cells only ever go to zero, so the same order is the cells sorted once, skipping the zeroed ones.
                var order = new List<int>();
                for (int idx = 0; idx < remaining.Length; idx++)
                    if (remaining[idx] > frameThresh) order.Add(idx);
                var values = (float[])remaining.Clone();
                order.Sort((a, b) => values[a] != values[b] ? values[b].CompareTo(values[a]) : a.CompareTo(b));
                foreach (int idx in order)
                {
                    if (remaining[idx] != values[idx] || !(remaining[idx] > frameThresh)) continue;
                    int mid = idx / Keys, k = idx % Keys;
                    remaining[idx] = 0;
                    int i = mid + 1, below = 0;
                    while (i < T - 1 && below < energyTol)
                    {
                        if (remaining[i * Keys + k] < frameThresh) below++; else below = 0;
                        Strike(remaining, i, k);
                        i++;
                    }
                    int end = i - 1 - below;
                    i = mid - 1; below = 0;
                    while (i > 0 && below < energyTol)
                    {
                        if (remaining[i * Keys + k] < frameThresh) below++; else below = 0;
                        Strike(remaining, i, k);
                        i--;
                    }
                    int begin = i + 1 + below;
                    if (end - begin <= s.MinNoteFrames) continue;
                    notes.Add(new FrameNote(begin, end, k + MidiOffset, Mean(frames, begin, end, k)));
                }
            }
            return notes;
        }

        static void Strike(float[] remaining, int t, int k)
        {
            remaining[t * Keys + k] = 0;
            if (k < Keys - 1) remaining[t * Keys + k + 1] = 0;
            if (k > 0) remaining[t * Keys + k - 1] = 0;
        }

        static double Mean(float[] frames, int a, int b, int k)
        {
            double sum = 0;
            for (int t = a; t < b; t++) sum += frames[t * Keys + k];
            return b > a ? sum / (b - a) : 0;
        }

        /// <summary>
        /// get_infered_onsets: the onsets are raised to the rises of the frame activation (the smaller of its rise over one frame and over two,
        /// scaled so the biggest rise equals the biggest onset). False when the recording has no rise at all (silence: no notes).
        /// </summary>
        static bool InferOnsets(float[] onsets, float[] frames, int T)
        {
            var diff = new float[onsets.Length];
            float maxDiff = 0, maxOnset = float.MinValue;
            foreach (float v in onsets) if (v > maxOnset) maxOnset = v;
            for (int t = 0; t < T; t++)
                for (int k = 0; k < Keys; k++)
                {
                    float now = frames[t * Keys + k];
                    float d1 = now - (t >= 1 ? frames[(t - 1) * Keys + k] : 0f);
                    float d2 = now - (t >= 2 ? frames[(t - 2) * Keys + k] : 0f);
                    float d = Math.Min(d1, d2);
                    if (d < 0 || t < 2) d = 0;
                    diff[t * Keys + k] = d;
                    if (d > maxDiff) maxDiff = d;
                }
            if (!(maxDiff > 0)) return false;
            for (int i = 0; i < onsets.Length; i++)
            {
                float scaled = maxOnset * diff[i] / maxDiff;
                if (scaled > onsets[i]) onsets[i] = scaled;
            }
            return true;
        }

        static double HzToMidi(double hz) => 69 + 12 * Math.Log2(hz / 440.0);

        // ------------------------------------------------------------------ pitch bends

        /// <summary>
        /// The pitch bend of each note, in 1/3 semitones, one per frame (the reference's get_pitch_bends): the strongest contour bin within 25 bins
        /// (8 semitones) of the note's key under a Gaussian of 5 bins.
        /// </summary>
        public static int[] BendsOf(Output o, FrameNote n)
        {
            const int tol = 25;
            int window = tol * 2 + 1;
            int freqIdx = 3 * (n.Pitch - MidiOffset);              // 36 log2(f / 27.5) for the key's own frequency: 3 bins a semitone
            int fs = Math.Max(freqIdx - tol, 0), fe = Math.Min(ContourBins, freqIdx + tol + 1);
            int gStart = Math.Max(0, tol - freqIdx);
            int pbShift = tol - gStart;
            var bends = new int[n.End - n.Start];
            for (int t = n.Start; t < n.End; t++)
            {
                double best = double.NegativeInfinity;
                int arg = 0;
                for (int c = fs; c < fe; c++)
                {
                    double g = Math.Exp(-0.5 * Math.Pow((gStart + (c - fs) - (window - 1) / 2.0) / 5.0, 2));
                    double v = o.Contour[t * ContourBins + c] * g;
                    if (v > best) { best = v; arg = c - fs; }
                }
                bends[t - n.Start] = arg - pbShift;
            }
            return bends;
        }

        // ------------------------------------------------------------------ to ConvertedNote

        public static List<ConvertedNote> ToNotes(Output o, Settings s)
        {
            var frameNotes = FindNotes(o, s);
            var result = new List<ConvertedNote>(frameNotes.Count);
            foreach (var n in frameNotes)
            {
                var note = new ConvertedNote
                {
                    Start = FrameTime(n.Start),
                    End = FrameTime(Math.Min(n.End, o.Frames - 1)),
                    Pitch = n.Pitch,
                    Velocity = Math.Clamp((int)Math.Round(127 * n.Amplitude), 1, 127),
                };
                if (s.PitchBends)
                {
                    var bends = BendsOf(o, n);
                    var list = new List<(double, double)>();
                    for (int i = 0; i < bends.Length; i++)
                        list.Add((FrameTime(n.Start + i), bends[i] * 100.0 / 3));
                    note.Bends = list;
                }
                result.Add(note);
            }
            result.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
            return result;
        }

        // ------------------------------------------------------------------ windowing (inference.py)

        /// <summary>The windows the model is run on: the audio at 22.05 kHz with half the overlap of silence in front, in windows of 43844 samples every 36164.</summary>
        public static IEnumerable<float[]> Windows(float[] audio22k)
        {
            int overlap = OverlapFrames * FftHop;
            int hop = WindowSamples - overlap;
            int padded = audio22k.Length + overlap / 2;
            for (int i = 0; i < padded; i += hop)
            {
                var w = new float[WindowSamples];
                for (int j = 0; j < WindowSamples; j++)
                {
                    int src = i + j - overlap / 2;
                    if (src >= 0 && src < audio22k.Length) w[j] = audio22k[src];
                }
                yield return w;
            }
        }

        public static int WindowCount(int length) => (length + OverlapFrames * FftHop / 2 + (WindowSamples - OverlapFrames * FftHop) - 1) / (WindowSamples - OverlapFrames * FftHop);

        /// <summary>Joins the windows' outputs (each 172 frames) as unwrap_output does: 15 frames cut at both ends, the whole trimmed to the audio's length.</summary>
        public static Output Join(IReadOnlyList<(float[] Note, float[] Onset, float[] Contour)> windows, int audioLength)
        {
            int keep = OutputFramesPerWindow - OverlapFrames;
            int cut = OverlapFrames / 2;
            int hop = WindowSamples - OverlapFrames * FftHop;
            int frames = windows.Count * keep;
            double expected = audioLength / (double)hop;
            int total = Math.Min(frames, (int)(expected * keep));
            var o = new Output { Frames = total, Note = new float[total * Keys], Onset = new float[total * Keys], Contour = new float[total * ContourBins] };
            for (int w = 0, f = 0; w < windows.Count && f < total; w++)
                for (int r = 0; r < keep && f < total; r++, f++)
                {
                    int src = cut + r;
                    Array.Copy(windows[w].Note, src * Keys, o.Note, f * Keys, Keys);
                    Array.Copy(windows[w].Onset, src * Keys, o.Onset, f * Keys, Keys);
                    Array.Copy(windows[w].Contour, src * ContourBins, o.Contour, f * ContourBins, ContourBins);
                }
            return o;
        }
    }
}
