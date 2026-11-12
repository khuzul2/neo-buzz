#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/neo-learn.md §5.3): Explain. The strip at the bottom of an
// editor says what the control you touch does: a title line (name · value with unit · source badge), the
// control's How, and its Sound. ExplainState decides what it shows; ExplainPainter draws it on an INeoCanvas.
//
//  - Hover, a drag, keyboard focus, a drag on the picture (both controls' names) and an agent's write all
//    show at once.
//  - Automation (patterns, peer machines, MIDI controllers) shows only when nobody has touched a control for
//    2 s, and at most 10 times a second.
//  - The badge is who wrote the control's value last: Manual (the editor), Agent (the agent's param_set) or
//    Automation (anything else).

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    /// <summary>What happens to a control under the pointer, for Explain.</summary>
    internal enum KnobPhase { Enter, Leave, Touch, Release }

    /// <summary>Who wrote a value.</summary>
    internal enum ExplainSource { Manual, Agent, Automation }

    /// <summary>What the strip shows: the title's parts, and the two lines.</summary>
    internal sealed record ExplainLine(int Index, int Index2, string Name, string Value, ExplainSource Source, string How, string Sound)
    {
        public string Badge => Source.ToString();

        /// <summary>"Cutoff · 1.20 kHz".</summary>
        public string Title => Name + " · " + Value;
    }

    internal sealed class ExplainState
    {
        /// <summary>Automation waits this long after the last touch.</summary>
        public const long AutomationHoldMs = 2000;
        /// <summary>Automation updates the strip at most this often.</summary>
        public const long AutomationIntervalMs = 100;
        /// <summary>A drag nobody has touched for this long is taken as over (its end was missed).</summary>
        const long DragStaleMs = 3000;

        readonly ParamDef[] table;
        readonly Func<int, int> raw;
        readonly Func<long> clock;
        readonly ExplainSource[] last;

        int focus = -1, focus2 = -1;
        int hover = -1;
        int dragging = -1;
        long touchedAt = long.MinValue / 2;
        long automationAt = long.MinValue / 2;
        ExplainSource shown = ExplainSource.Manual;

        /// <param name="raw">A parameter's raw value now.</param>
        /// <param name="clock">Milliseconds (tests set it).</param>
        public ExplainState(ParamDef[] table, Func<int, int> raw, Func<long> clock = null)
        {
            this.table = table;
            this.raw = raw;
            this.clock = clock ?? (() => Environment.TickCount64);
            last = new ExplainSource[table.Length];
        }

        /// <summary>Raised when what the strip shows changes.</summary>
        public event Action Changed;

        /// <summary>The table entry the strip is about now (-1: none), and for a picture drag the second.</summary>
        public int Focus => focus;
        public int Focus2 => focus2;

        /// <summary>Whether any control has Explain text.</summary>
        public bool Any
        {
            get
            {
                foreach (var d in table) if (d.HasExplain) return true;
                return false;
            }
        }

        /// <summary>The source of a control's last value.</summary>
        public ExplainSource SourceOf(int index) => index >= 0 && index < last.Length ? last[index] : ExplainSource.Manual;

        bool Valid(int index) => index >= 0 && index < table.Length;

        void Show(int a, int b, ExplainSource source)
        {
            if (a == focus && b == focus2 && source == shown) return;
            focus = a;
            focus2 = b;
            shown = source;
            Changed?.Invoke();
        }

        void Touch(int index, int index2 = -1)
        {
            if (!Valid(index)) return;
            touchedAt = clock();
            Show(index, Valid(index2) ? index2 : -1, last[index]);
        }

        /// <summary>The pointer is over a control.</summary>
        public void Hover(int index)
        {
            hover = index;
            if (dragging < 0) Touch(index);
        }

        /// <summary>The pointer left the control: the strip keeps showing it (the last thing you touched).</summary>
        public void Unhover(int index)
        {
            if (hover == index) hover = -1;
        }

        /// <summary>A control's drag (or a wheel turn, or a double click) starts.</summary>
        public void BeginDrag(int index)
        {
            dragging = index;
            Touch(index);
        }

        public void EndDrag(int index)
        {
            if (dragging == index) dragging = -1;
        }

        /// <summary>A control takes keyboard focus.</summary>
        public void KeyboardFocus(int index) => Touch(index);

        /// <summary>A drag on the picture moves two controls: the strip names both.</summary>
        public void PictureDrag(int x, int y)
        {
            if (!Valid(x) || !Valid(y)) return;
            dragging = x;
            touchedAt = clock();
            last[x] = last[y] = ExplainSource.Manual;
            Show(x, y, ExplainSource.Manual);
        }

        public void PictureDragEnd() => dragging = -1;

        /// <summary>
        /// A control's value was written. A hand edit (the editor's own) is Manual and shows at once; an agent's write
        /// shows at once with the Agent badge (unless you are dragging a control); automation waits for 2 s without a touch
        /// and shows at most 10 times a second.
        /// </summary>
        public void Written(int index, ExplainSource source)
        {
            if (!Valid(index)) return;
            last[index] = source;
            long now = clock();
            switch (source)
            {
                case ExplainSource.Manual:
                    touchedAt = now;
                    Show(index, -1, source);
                    break;
                case ExplainSource.Agent:
                    if (dragging >= 0 && dragging != index && now - touchedAt < DragStaleMs) return;
                    Show(index, -1, source);
                    break;
                default:
                    if (now - touchedAt < AutomationHoldMs || now - automationAt < AutomationIntervalMs) return;
                    automationAt = now;
                    Show(index, -1, source);
                    break;
            }
        }

        /// <summary>What the strip shows now (the value is read when asked, so a drag shows as it moves); null: nothing yet.</summary>
        public ExplainLine Current
        {
            get
            {
                if (!Valid(focus)) return null;
                var d = table[focus];
                if (!Valid(focus2)) return new ExplainLine(focus, -1, d.Name, d.Describe(raw(focus)), shown, d.How, d.Sound);
                var e = table[focus2];
                return new ExplainLine(focus, focus2, d.Name + " + " + e.Name, d.Describe(raw(focus)) + " · " + e.Describe(raw(focus2)),
                    ExplainSource.Manual, d.Name + ": " + d.How, e.Name + ": " + e.How);
            }
        }
    }

    /// <summary>Draws the strip: three lines in a framed panel, and the source badge at the end of the title.</summary>
    internal static class ExplainPainter
    {
        public const double Height = 62;

        static readonly NeoColor ManualBadge = NeoColor.FromRgb(0x5A, 0xA9, 0xE6);
        static readonly NeoColor AgentBadge = NeoColor.FromRgb(0xE0, 0x9F, 0x3E);
        static readonly NeoColor AutomationBadge = NeoColor.FromRgb(0x6C, 0xC4, 0x8A);

        public static NeoColor BadgeColor(ExplainSource s) => s switch
        {
            ExplainSource.Agent => AgentBadge,
            ExplainSource.Automation => AutomationBadge,
            _ => ManualBadge,
        };

        /// <summary>The strip's content over <paramref name="rr"/> (a GraphView draws the frame).</summary>
        public static void Draw(INeoCanvas cv, NeoRect rr, ExplainLine line, NeoColor accent)
        {
            double x = rr.X + 10, w = rr.Width - 20;
            if (line == null)
            {
                cv.Text("Touch a control to see what it does.", x, rr.Y + 8, 12, Palette.DimText, maxWidth: w);
                return;
            }
            string badge = line.Badge;
            double bw = cv.MeasureText(badge, 10, true) + 14;
            string tail = " · " + line.Value;
            double tailW = cv.MeasureText(tail, 13, false);
            double nameW = Math.Min(cv.MeasureText(line.Name, 13, true), Math.Max(40, w - bw - 8 - tailW));
            cv.Text(line.Name, x, rr.Y + 6, 13, Palette.Text, true, maxWidth: nameW + 1);
            cv.Text(tail, x + nameW, rr.Y + 6, 13, accent, false, maxWidth: Math.Max(0, w - bw - 8 - nameW));
            var badgeRect = new NeoRect(rr.Right - 10 - bw, rr.Y + 7, bw, 16);
            var col = BadgeColor(line.Source);
            cv.Rect(badgeRect, col.WithAlpha(0x38), new NeoStroke(col, 1), 8);
            cv.Text(badge, badgeRect.X + bw / 2, badgeRect.Y + 2, 10, col, true, NeoAlign.Center);
            cv.Text("How  " + line.How, x, rr.Y + 26, 11, Palette.Text, maxWidth: w);
            cv.Text("Sound  " + line.Sound, x, rr.Y + 43, 11, Palette.DimText, maxWidth: w);
        }
    }
}
