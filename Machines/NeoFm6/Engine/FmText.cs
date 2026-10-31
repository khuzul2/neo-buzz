#nullable disable
// Neo FM6 (docs/specs/machines.md §5): how values read. The parameters keep the DX7's numbers;
// these add what the numbers mean (ratios, notes, Hz, seconds, semitones).

using System;
using System.Globalization;
using NeoBuzz.Dsp;

namespace NeoBuzz.Fm6
{
    internal static class FmText
    {
        static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// <summary>A MIDI note's name, C4 = 60.</summary>
        public static string Note(int midi) => NoteNames[((midi % 12) + 12) % 12] + (midi / 12 - 1).ToString(C);

        /// <summary>Operator frequency as the editor shows it: "×1.41" or "440 Hz".</summary>
        public static string Frequency(bool isFixed, int coarse, int fine, int detune)
        {
            if (isFixed)
            {
                double hz = Dx7.FixedHz(coarse, fine) * Math.Pow(2, Dx7.FixedDetuneOctaves(detune));
                return hz >= 1000 ? (hz / 1000).ToString(hz >= 10000 ? "F1" : "F2", C) + " kHz" : hz.ToString(hz >= 100 ? "F0" : hz >= 10 ? "F1" : "F2", C) + " Hz";
            }
            double r = Dx7.Ratio(coarse, fine);
            return "×" + r.ToString(r >= 10 ? "F1" : "F2", C);
        }

        static int installed;

        /// <summary>Gives the parameter table its texts (once).</summary>
        public static void Install()
        {
            if (System.Threading.Interlocked.Exchange(ref installed, 1) != 0) return;
            var t = Params.Table;
            t[P.LfoSpeed].Describer = raw => $"{raw} · {Dx7.LfoHz(raw).ToString(Dx7.LfoHz(raw) < 1 ? "F2" : "F1", C)} Hz";
            t[P.LfoDelay].Describer = raw =>
            {
                if (raw == 0) return "0 · none";
                var (wait, fade) = Dx7.LfoDelay(raw);
                return $"{raw} · {(wait + fade).ToString("F2", C)} s";
            };
            for (int i = 0; i < 4; i++)
                t[P.PegL1 + i].Describer = raw =>
                {
                    double st = Dx7.PitchLevelOctaves(raw) * 12;
                    string s = Math.Abs(st) < 0.05 ? "0" : (st > 0 ? "+" : "−") + Math.Abs(st).ToString("F1", C);
                    return $"{raw} · {s} st";
                };
            for (int op = 0; op < 6; op++)
            {
                t[FmEngine.OpParam(op, P.O1Coarse - P.O1On)].Describer = raw => raw == 0 ? "0 · ×0.5" : $"{raw} · ×{raw}";
                t[FmEngine.OpParam(op, P.O1Fine - P.O1On)].Describer = raw => raw == 0 ? "0" : $"{raw} · +{raw}%";
                t[FmEngine.OpParam(op, P.O1Break - P.O1On)].Describer = raw => $"{raw} · {Note(raw + 21)}";
            }
        }
    }
}
