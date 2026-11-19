// The first note of a sung tone, live (docs/specs/voice-to-midi.md §9): Neo Voice to MIDI's pitch engine, in each range and in Fast and Stable
// response, on VocalSet's straight long tones. There is no note truth in VocalSet, so the reference is the note the Quality tracker sings
// between 150 and 500 ms after the voice starts (the median of its voiced estimates, as the nearest semitone). Voice start is the first 2.7 ms
// window above -40 dBFS. A first note-on that is not the reference note is a wrong first note; the note sounding at 120 ms (after any
// correction) is compared the same way; the delay is from the voice start to the first note-on.

using NeoBuzz.Dsp;
using NeoBuzz.VoiceToMidi;

namespace NeoBuzz.PitchEval
{
    internal static class FirstNote
    {
        public static readonly string[] Names = { "Low latency, Fast", "Quality, Fast", "Low latency, Stable", "Quality, Stable" };

        public sealed class Result
        {
            public bool Valid;
            public double[] DelayMs = new double[4];
            public bool[] Got = new bool[4], FirstRight = new bool[4], At120Right = new bool[4];
            public int[] Retriggers = new int[4];
        }

        public static Result Run(float[] x, int rate)
        {
            var r = new Result();
            // Voice start.
            int win = Math.Max(1, rate / 370);
            int start = -1;
            for (int p = 0; p + win <= x.Length && start < 0; p += win / 2 + 1)
            {
                double s = 0; for (int i = 0; i < win; i++) s += x[p + i] * x[p + i];
                if (Math.Sqrt(s / win) >= 0.01) start = p;
            }
            if (start < 0 || start + (int)(0.6 * rate) > x.Length) return r;
            double t0 = start / (double)rate;
            // The reference note.
            var q = Track.Run(x, rate, PitchRange.Voice, PitchQuality.Quality);
            var midis = new List<double>();
            for (int i = 0; i < q.Count; i++)
                if (q.Voiced[i] && q.End[i] >= t0 + 0.15 && q.End[i] < t0 + 0.5 && q.F0[i] > 0) midis.Add(69 + 12 * Math.Log2(q.F0[i] / 440.0));
            if (midis.Count < 20) return r;
            midis.Sort();
            int reference = (int)Math.Round(midis[midis.Count / 2]);
            if (reference < 0 || reference > 127) return r;
            r.Valid = true;
            for (int c = 0; c < 4; c++)
            {
                var v = new ParamValues(Params.Table);
                v.Set(P.Monitor, 0); v.Set(P.Triggers, 0); v.Set(P.Lock, 0);
                v.Set(P.Range, c % 2); v.Set(P.Response, c / 2);
                var e = new VoiceEngine(v);
                e.SetSampleRate(rate);
                var buf = new float[VoiceEngine.MaxBlock];
                var ons = new List<(double T, int Note)>();
                int end = Math.Min(x.Length, start + (int)(0.6 * rate));
                for (int pos = Math.Max(0, start - rate / 10); pos < end; pos += 256)
                {
                    int n = Math.Min(256, end - pos);
                    Array.Copy(x, pos, buf, 0, n);
                    int count = e.Process(buf, n);
                    for (int i = 0; i < count; i++)
                    {
                        var m = e.Events[i];
                        if (m.Stream == MidiStream.Pitch && m.Kind == MidiKind.NoteOn) ons.Add(((pos + m.Offset) / (double)rate, m.Data1));
                    }
                }
                var list = ons.Where(o => o.T >= t0 - 0.01).ToList();
                if (list.Count == 0) continue;
                r.Got[c] = true;
                r.DelayMs[c] = (list[0].T - t0) * 1000;
                r.FirstRight[c] = list[0].Note == reference;
                var at120 = list.LastOrDefault(o => o.T < t0 + 0.12);
                r.At120Right[c] = at120.T > 0 && at120.Note == reference;
                r.Retriggers[c] = list.Count(o => o.T < t0 + 0.5) - 1;
            }
            return r;
        }

        public static void Report(List<Result> rs, System.Text.StringBuilder md)
        {
            rs = rs.Where(r => r.Valid).ToList();
            md.AppendLine("### First note, live (Neo Voice to MIDI engine, straight long tones)");
            md.AppendLine();
            md.AppendLine($"{rs.Count} long tones with a steady pitch between 150 and 500 ms after the voice starts. VocalSet has no note truth, so the reference is the note the Quality tracker sings there (median of its voiced estimates, nearest semitone); the voice start is the first 2.7 ms window above -40 dBFS. **Delay** is voice start to the first note-on. **First wrong** is a first note-on that is not the reference; **wrong at 120 ms** is the note sounding 120 ms in, after any correction; **extra notes** counts note-ons after the first in the first 500 ms.");
            md.AppendLine();
            md.AppendLine("| Setting | Delay median | Delay 90th | Delay worst | First wrong | Wrong at 120 ms | Files with extra notes |");
            md.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
            for (int c = 0; c < 4; c++)
            {
                var g = rs.Where(r => r.Got[c]).ToList();
                if (g.Count == 0) continue;
                var d = g.Select(r => r.DelayMs[c]).OrderBy(v => v).ToList();
                md.AppendLine($"| {Names[c]} | {Fmt.F(d[d.Count / 2])} ms | {Fmt.F(d[(int)(d.Count * 0.9)])} ms | {Fmt.F(d[^1])} ms | {Fmt.P(g.Count(r => !r.FirstRight[c]) / (double)g.Count)} | {Fmt.P(g.Count(r => !r.At120Right[c]) / (double)g.Count)} | {Fmt.P(g.Count(r => r.Retriggers[c] > 0) / (double)g.Count)} |");
            }
            md.AppendLine();
        }
    }
}
