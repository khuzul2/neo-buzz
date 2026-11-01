#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): an FM algorithm as a
// diagram. Operators are boxes, modulators sit above what they modulate, carriers stand on the
// bottom row over the output bus, and the feedback loop runs on the right. The machine supplies
// the topology (Neo FM6: Engine/Algorithms.cs); this lays it out and draws it.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    internal sealed class OperatorDiagram
    {
        public const double BoxWidth = 24, BoxHeight = 18, ColumnStep = 32, RowStep = 27;
        /// <summary>Space under the carriers: the output bus.</summary>
        public const double BottomMargin = 22, BusOffset = 12;

        readonly (int Column, int Row)[] positions;
        readonly int[] modulators;

        /// <param name="number">Shown as the title ("Algorithm 5").</param>
        /// <param name="positions">Each operator's column and row (row 0 = carriers).</param>
        /// <param name="carriers">Bit <c>op</c> set for each carrier.</param>
        /// <param name="modulators">For each operator, bit <c>m</c> set when operator <c>m</c> modulates it.</param>
        public OperatorDiagram(int number, (int Column, int Row)[] positions, int carriers, int[] modulators, int feedbackFrom, int feedbackTo)
        {
            Number = number;
            this.positions = positions;
            Carriers = carriers;
            this.modulators = modulators;
            FeedbackFrom = feedbackFrom;
            FeedbackTo = feedbackTo;
            foreach (var (c, row) in positions)
            {
                Columns = Math.Max(Columns, c + 1);
                Rows = Math.Max(Rows, row + 1);
            }
        }

        public int Number { get; }
        public int Count => positions.Length;
        public int Carriers { get; }
        public int FeedbackFrom { get; }
        public int FeedbackTo { get; }
        public int Columns { get; } = 1;
        public int Rows { get; } = 1;

        public bool IsCarrier(int op) => (Carriers & (1 << op)) != 0;

        public bool Modulates(int from, int to) => (modulators[to] & (1 << from)) != 0;

        /// <summary>Operator <paramref name="op"/>'s box in a diagram drawn over <paramref name="r"/> (centred across).</summary>
        public NeoRect Box(NeoRect r, int op)
        {
            double x0 = r.Left + (r.Width - ((Columns - 1) * ColumnStep + BoxWidth)) / 2;
            double yBottom = r.Bottom - BottomMargin;
            return new NeoRect(x0 + positions[op].Column * ColumnStep, yBottom - BoxHeight - positions[op].Row * RowStep, BoxWidth, BoxHeight);
        }

        public static double BusY(NeoRect r) => r.Bottom - BusOffset;

        /// <summary>Box fill alpha for an operator's level now (a gain; 2 = full scale): carriers glow brighter.</summary>
        public byte Glow(int op, float level)
        {
            double glow = Math.Clamp((DspMath.GainToDb(Math.Max(1e-9f, level / 2f)) + 60) / 60, 0, 1);
            return (byte)(IsCarrier(op) ? 0x40 + 0x90 * glow : 0x10 + 0xA0 * glow);
        }

        /// <param name="feedback">The feedback amount (0 draws the loop faintly).</param>
        /// <param name="on">Whether an operator is switched on.</param>
        /// <param name="level">An operator's level now (see <see cref="Glow"/>).</param>
        public void Draw(INeoCanvas c, NeoRect r, NeoColor accent, int feedback, Func<int, bool> on, Func<int, float> level)
        {
            const double bw = BoxWidth, bh = BoxHeight;
            var line = new NeoStroke(accent.WithAlpha(0xA0), 1.2);

            c.Text($"Algorithm {Number}", r.Left + 6, r.Top + 4, 11, Palette.DimText, true);

            // Output bus under the carriers.
            double busY = BusY(r);
            double busLeft = double.MaxValue, busRight = double.MinValue;
            for (int op = 0; op < Count; op++)
            {
                if (!IsCarrier(op)) continue;
                var b = Box(r, op);
                double cx = b.X + bw / 2;
                c.Line(new NeoPoint(cx, b.Bottom), new NeoPoint(cx, busY), line);
                busLeft = Math.Min(busLeft, cx);
                busRight = Math.Max(busRight, cx);
            }
            c.Line(new NeoPoint(busLeft, busY), new NeoPoint(busRight, busY), line);

            // Modulation links.
            for (int to = 0; to < Count; to++)
                for (int from = 0; from < Count; from++)
                {
                    if (!Modulates(from, to)) continue;
                    var a = Box(r, from);
                    var b = Box(r, to);
                    c.Line(new NeoPoint(a.X + bw / 2, a.Bottom), new NeoPoint(b.X + bw / 2, b.Top), line);
                }

            // Feedback: a loop on the right of the operator (or from one operator back up to another).
            {
                var f = Box(r, FeedbackFrom);
                var t = Box(r, FeedbackTo);
                double xr = Math.Max(f.Right, t.Right) + 5;
                var loop = new NeoPath()
                    .MoveTo(f.Right, f.Y + bh / 2)
                    .LineTo(xr, f.Y + bh / 2)
                    .LineTo(xr, t.Y - 5)
                    .LineTo(t.X + bw / 2, t.Y - 5)
                    .LineTo(t.X + bw / 2, t.Y);
                c.Path(loop, NeoFill.None, new NeoStroke(accent.WithAlpha((byte)(feedback == 0 ? 0x40 : 0xC0)), feedback == 0 ? 1 : 1.6));
            }

            // Operators, lit by their level.
            for (int op = 0; op < Count; op++)
            {
                var b = Box(r, op);
                bool lit = on(op);
                c.Rect(b, accent.WithAlpha(Glow(op, level(op))), new NeoStroke(lit ? accent : Palette.PanelEdge, 1.2), 3);
                c.Text((op + 1).ToString(), b.X + bw / 2, b.Y + 1, 11.5, lit ? Palette.Text : Palette.DimText, true, NeoAlign.Center);
            }
        }
    }
}
