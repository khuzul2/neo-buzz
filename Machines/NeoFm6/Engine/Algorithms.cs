#nullable disable
// Neo FM6 (docs/specs/machines.md §5): the 32 algorithms of the DX7 — which operator modulates
// which, which are carriers (heard), and where the feedback loop is. In every algorithm a
// modulator has a higher number than what it modulates, so operators run 6 → 1 each sample.

using System;
using System.Collections.Generic;

namespace NeoBuzz.Fm6
{
    internal sealed class Algorithm
    {
        public Algorithm(int number, int[] modulators, int carriers, int feedbackFrom, int feedbackTo)
        {
            Number = number;
            Modulators = modulators;
            Carriers = carriers;
            FeedbackFrom = feedbackFrom;
            FeedbackTo = feedbackTo;
            for (int k = 0; k < 6; k++) if ((carriers & (1 << k)) != 0) CarrierCount++;
        }

        /// <summary>1 … 32.</summary>
        public int Number { get; }
        /// <summary>Per operator (index 0 = op 1): a bit mask of the operators modulating it.</summary>
        public int[] Modulators { get; }
        /// <summary>Bit mask of the carriers.</summary>
        public int Carriers { get; }
        public int CarrierCount { get; }
        /// <summary>The operator whose output is fed back (0-based) …</summary>
        public int FeedbackFrom { get; }
        /// <summary>… into this operator's input (the same one, except in algorithms 4 and 6).</summary>
        public int FeedbackTo { get; }

        public bool IsCarrier(int op) => (Carriers & (1 << op)) != 0;
    }

    internal static class Algorithms
    {
        // "a>b": operator a modulates operator b | carriers | feedback ("6", or "4>6" for a loop).
        static readonly string[] Defs =
        {
            "2>1 6>5 5>4 4>3 | 1 3 | 6",            // 1
            "2>1 6>5 5>4 4>3 | 1 3 | 2",            // 2
            "3>2 2>1 6>5 5>4 | 1 4 | 6",            // 3
            "3>2 2>1 6>5 5>4 | 1 4 | 4>6",          // 4
            "2>1 4>3 6>5 | 1 3 5 | 6",              // 5
            "2>1 4>3 6>5 | 1 3 5 | 5>6",            // 6
            "2>1 4>3 5>3 6>5 | 1 3 | 6",            // 7
            "2>1 4>3 5>3 6>5 | 1 3 | 4",            // 8
            "2>1 4>3 5>3 6>5 | 1 3 | 2",            // 9
            "3>2 2>1 5>4 6>4 | 1 4 | 3",            // 10
            "3>2 2>1 5>4 6>4 | 1 4 | 6",            // 11
            "2>1 4>3 5>3 6>3 | 1 3 | 2",            // 12
            "2>1 4>3 5>3 6>3 | 1 3 | 6",            // 13
            "2>1 4>3 5>4 6>4 | 1 3 | 6",            // 14
            "2>1 4>3 5>4 6>4 | 1 3 | 2",            // 15
            "2>1 3>1 4>3 5>1 6>5 | 1 | 6",          // 16
            "2>1 3>1 4>3 5>1 6>5 | 1 | 2",          // 17
            "2>1 3>1 4>1 5>4 6>5 | 1 | 3",          // 18
            "2>1 3>2 6>4 6>5 | 1 4 5 | 6",          // 19
            "3>1 3>2 5>4 6>4 | 1 2 4 | 3",          // 20
            "3>1 3>2 6>4 6>5 | 1 2 4 5 | 3",        // 21
            "2>1 6>3 6>4 6>5 | 1 3 4 5 | 6",        // 22
            "3>2 6>4 6>5 | 1 2 4 5 | 6",            // 23
            "6>3 6>4 6>5 | 1 2 3 4 5 | 6",          // 24
            "6>4 6>5 | 1 2 3 4 5 | 6",              // 25
            "3>2 5>4 6>4 | 1 2 4 | 6",              // 26
            "3>2 5>4 6>4 | 1 2 4 | 3",              // 27
            "2>1 4>3 5>4 | 1 3 6 | 5",              // 28
            "4>3 6>5 | 1 2 3 5 | 6",                // 29
            "4>3 5>4 | 1 2 3 6 | 5",                // 30
            "6>5 | 1 2 3 4 5 | 6",                  // 31
            "| 1 2 3 4 5 6 | 6",                    // 32
        };

        public static readonly Algorithm[] All = Build();

        /// <summary>Algorithm 1 … 32 (clamped).</summary>
        public static Algorithm Get(int number) => All[Math.Clamp(number, 1, 32) - 1];

        static Algorithm[] Build()
        {
            var all = new Algorithm[Defs.Length];
            for (int a = 0; a < Defs.Length; a++)
            {
                var parts = Defs[a].Split('|');
                var mods = new int[6];
                foreach (var link in parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int from = link[0] - '1', to = link[2] - '1';
                    if (from <= to) throw new InvalidOperationException($"algorithm {a + 1}: {link} runs upwards");
                    mods[to] |= 1 << from;
                }
                int carriers = 0;
                foreach (var c in parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)) carriers |= 1 << (c[0] - '1');
                var fb = parts[2].Trim();
                int fbFrom = fb[0] - '1', fbTo = fb.Length > 1 ? fb[2] - '1' : fbFrom;
                all[a] = new Algorithm(a + 1, mods, carriers, fbFrom, fbTo);
            }
            return all;
        }

        /// <summary>
        /// For drawing: each operator's column and row in a stack diagram (carriers on the bottom
        /// row, modulators above what they modulate).
        /// </summary>
        public static (int Column, int Row)[] Layout(Algorithm alg)
        {
            var pos = new (int Column, int Row)[6];
            var placed = new bool[6];
            int column = 0;
            for (int op = 0; op < 6; op++)
            {
                if (!alg.IsCarrier(op)) continue;
                column = Place(alg, op, column, 0, pos, placed);
            }
            return pos;
        }

        // Places op at (column, row) and its modulators above it, left to right; returns the next free column.
        static int Place(Algorithm alg, int op, int column, int row, (int Column, int Row)[] pos, bool[] placed)
        {
            placed[op] = true;
            pos[op] = (column, row);
            int next = column;
            bool any = false;
            for (int m = 0; m < 6; m++)
            {
                if ((alg.Modulators[op] & (1 << m)) == 0) continue;
                if (placed[m]) continue;   // shared modulator (6 → 4 and 5): drawn once
                next = Place(alg, m, next, row + 1, pos, placed);
                any = true;
            }
            return any ? next : column + 1;
        }

        /// <summary>A readable description: "6→5→4→3, 2→1; carriers 1 3; feedback on 6".</summary>
        public static string Describe(Algorithm alg)
        {
            var links = new List<string>();
            for (int to = 0; to < 6; to++)
                for (int from = 5; from > to; from--)
                    if ((alg.Modulators[to] & (1 << from)) != 0) links.Add($"{from + 1}→{to + 1}");
            var carriers = new List<string>();
            for (int op = 0; op < 6; op++) if (alg.IsCarrier(op)) carriers.Add((op + 1).ToString());
            string fb = alg.FeedbackFrom == alg.FeedbackTo ? $"feedback on {alg.FeedbackFrom + 1}" : $"feedback {alg.FeedbackFrom + 1}→{alg.FeedbackTo + 1}";
            return (links.Count > 0 ? string.Join(" ", links) + "; " : "") + "carriers " + string.Join(" ", carriers) + "; " + fb;
        }
    }
}
