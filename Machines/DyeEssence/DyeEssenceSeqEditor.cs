#nullable disable
// Dye Essence's editor (docs/specs/dye-essence.md §8), the sequencer's steps (§4.9): one lane of the 32 steps at
// a time (note, velocity, gate, tie, probability) as bars, drawn by dragging across them. One source for WPF and
// Avalonia (the kit's graph; pointer input as plain calls). The steps live in the machine state, so a gesture is one
// undo step of its own (SeqEditAction).

using System;
using System.Linq;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;

namespace NeoBuzz.DyeEssence
{
    internal enum SeqLane { Note, Velocity, Gate, Tie, Probability }

    /// <summary>The steps being edited: reads and writes the machine's pattern, one undo step a gesture.</summary>
    internal sealed class SeqEdit
    {
        readonly EditorContext ctx;
        readonly DyeEssenceMachine machine;
        SeqPattern before;

        public SeqEdit(EditorContext ctx, DyeEssenceMachine machine)
        {
            this.ctx = ctx;
            this.machine = machine;
        }

        public SeqPattern Pattern => machine?.Pattern ?? SeqPattern.Default;

        /// <summary>The steps that play (the Seq length parameter).</summary>
        public int Length => (int)ctx.Value(P.SeqLength);

        public void Set(SeqPattern p) => machine?.SetPattern(p);

        public void BeginEdit() => before = machine?.Pattern;

        public void EndEdit()
        {
            if (machine == null || before == null) return;
            var after = machine.Pattern;
            if (after.SameAs(before))
            {
                before = null;
                return;
            }
            var m = ctx.Machine;
            try
            {
                m?.Graph?.Do(new SeqEditAction(m, before, after));
            }
            catch (Exception)
            {
                // A host without an undo history: the edit stands.
            }
            before = null;
        }
    }

    /// <summary>One undo step for the sequencer's steps.</summary>
    internal sealed class SeqEditAction : IAction
    {
        readonly IMachine machine;
        readonly IMachineGraph graph;
        readonly string name;
        readonly SeqPattern before, after;

        public SeqEditAction(IMachine machine, SeqPattern before, SeqPattern after)
        {
            this.machine = machine;
            graph = machine.Graph;
            name = machine.Name;
            this.before = before;
            this.after = after;
        }

        public void Do() => Apply(after);

        public void Undo() => Apply(before);

        void Apply(SeqPattern p)
        {
            var machines = graph?.Machines;
            var m = machines == null || machines.Contains(machine) ? machine : machines.FirstOrDefault(x => x.Name == name);
            (m?.ManagedMachine as DyeEssenceMachine)?.SetPattern(p);
        }
    }

    /// <summary>
    /// The pointer on one lane of the steps: press and drag across the bars to set them (up is more: the note's offset
    /// is ±24 semitones from the middle, velocity 0 (a rest) … 127, gate 1 … 100 %, probability 0 … 100 %); the tie lane
    /// toggles a step; the wheel moves a step by one; a double-click puts a step back to a plain one.
    /// </summary>
    internal sealed class SeqStepInput : IGraphInput
    {
        public const int Steps = SeqPattern.MaxSteps;
        public const double Margin = 8, LabelRoom = 14;

        readonly SeqEdit edit;
        readonly SeqLane lane;
        bool dragging;
        bool toggleTo;

        public SeqStepInput(SeqEdit edit, SeqLane lane)
        {
            this.edit = edit;
            this.lane = lane;
        }

        public NeoRect Plot { get; set; } = new NeoRect(0, 0, 1, 1);
        public int Hover { get; private set; } = -1;
        public bool Capturing => dragging;

        public double StepWidth => Plot.Width / Steps;
        public int StepAt(double x) => Math.Clamp((int)Math.Floor((x - Plot.X) / Math.Max(1e-6, StepWidth)), 0, Steps - 1);

        /// <summary>A step's value in this lane (its bar's height as a fraction: 0 … 1; the note's 0.5 is the middle).</summary>
        public static double Fraction(SeqPattern p, int i, SeqLane lane) => lane switch
        {
            SeqLane.Note => 0.5 + p.Offset[i] / 48.0,
            SeqLane.Velocity => p.Velocity[i] / 127.0,
            SeqLane.Gate => p.Gate[i] / 100.0,
            SeqLane.Tie => p.Tie[i] ? 1 : 0,
            _ => p.Prob[i] / 100.0,
        };

        public static string Text(SeqPattern p, int i, SeqLane lane) => lane switch
        {
            SeqLane.Note => p.Offset[i] > 0 ? "+" + p.Offset[i] + " st" : p.Offset[i] + " st",
            SeqLane.Velocity => p.Velocity[i] == 0 ? "rest" : p.Velocity[i].ToString(),
            SeqLane.Gate => p.Gate[i] + " %",
            SeqLane.Tie => p.Tie[i] ? "tied" : "not tied",
            _ => p.Prob[i] + " %",
        };

        SeqPattern With(SeqPattern p, int i, int value) => lane switch
        {
            SeqLane.Note => p.With(i, value, p.Velocity[i], p.Gate[i], p.Tie[i], p.Prob[i]),
            SeqLane.Velocity => p.With(i, p.Offset[i], value, p.Gate[i], p.Tie[i], p.Prob[i]),
            SeqLane.Gate => p.With(i, p.Offset[i], p.Velocity[i], value, p.Tie[i], p.Prob[i]),
            SeqLane.Tie => p.With(i, p.Offset[i], p.Velocity[i], p.Gate[i], value != 0, p.Prob[i]),
            _ => p.With(i, p.Offset[i], p.Velocity[i], p.Gate[i], p.Tie[i], value),
        };

        int ValueAt(double y)
        {
            double t = Math.Clamp(1 - (y - Plot.Y) / Math.Max(1, Plot.Height), 0, 1);
            return lane switch
            {
                SeqLane.Note => (int)Math.Round((t - 0.5) * 48),
                SeqLane.Velocity => (int)Math.Round(t * 127),
                SeqLane.Gate => Math.Max(1, (int)Math.Round(t * 100)),
                SeqLane.Tie => 0,
                _ => (int)Math.Round(t * 100),
            };
        }

        int Plain(SeqPattern p, int i) => lane switch
        {
            SeqLane.Note => 0,
            SeqLane.Velocity => SeqPattern.DefaultVelocity,
            SeqLane.Gate => SeqPattern.DefaultGate,
            SeqLane.Tie => 0,
            _ => 100,
        };

        public bool Down(double x, double y, int clicks, bool fine)
        {
            if (!Plot.Contains(x, y)) return false;
            int i = StepAt(x);
            var p = edit.Pattern;
            edit.BeginEdit();
            if (clicks >= 2)
            {
                edit.Set(With(p, i, Plain(p, i)));
                edit.EndEdit();
                return true;
            }
            if (lane == SeqLane.Tie)
            {
                toggleTo = !p.Tie[i];
                edit.Set(With(p, i, toggleTo ? 1 : 0));
            }
            else edit.Set(With(p, i, ValueAt(y)));
            dragging = true;
            Hover = i;
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            int was = Hover;
            Hover = Plot.Contains(x, y) || dragging ? StepAt(x) : -1;
            if (dragging)
            {
                var p = edit.Pattern;
                edit.Set(With(p, Hover, lane == SeqLane.Tie ? (toggleTo ? 1 : 0) : ValueAt(y)));
                return true;
            }
            return Hover != was;
        }

        public bool Up()
        {
            bool was = dragging;
            dragging = false;
            if (was) edit.EndEdit();
            return was;
        }

        public bool Wheel(double x, double y, int delta, bool fine)
        {
            if (!Plot.Contains(x, y) || lane == SeqLane.Tie) return false;
            int i = StepAt(x);
            var p = edit.Pattern;
            int now = lane switch { SeqLane.Note => p.Offset[i], SeqLane.Velocity => p.Velocity[i], SeqLane.Gate => p.Gate[i], _ => p.Prob[i] };
            int step = (lane == SeqLane.Note ? 1 : fine ? 1 : 5) * Math.Sign(delta);
            edit.BeginEdit();
            edit.Set(With(p, i, now + step));
            edit.EndEdit();
            return true;
        }

        public bool Leave()
        {
            bool changed = Hover >= 0;
            if (!dragging) Hover = -1;
            return changed;
        }
    }

    internal static class SeqStepPainter
    {
        public static void Draw(INeoCanvas c, NeoRect r, SeqEdit edit, SeqStepInput input, SeqLane lane, NeoColor accent)
        {
            double top = r.Y + SeqStepInput.Margin, bottom = r.Bottom - SeqStepInput.Margin - SeqStepInput.LabelRoom;
            var plot = new NeoRect(r.X + SeqStepInput.Margin, top, Math.Max(1, r.Width - 2 * SeqStepInput.Margin), Math.Max(1, bottom - top));
            input.Plot = plot;
            var p = edit.Pattern;
            int length = edit.Length;
            double w = input.StepWidth;
            var grid = new NeoStroke(Palette.Grid, 1);
            double zero = lane == SeqLane.Note ? plot.Y + plot.Height / 2 : plot.Bottom;
            for (int q = 1; q < 4; q++)
            {
                double gy = Math.Round(plot.Y + plot.Height * q / 4) + 0.5;
                c.Line(new NeoPoint(plot.Left, gy), new NeoPoint(plot.Right, gy), grid);
            }
            for (int i = 0; i < SeqStepInput.Steps; i++)
            {
                bool on = i < length;
                double x = plot.X + i * w;
                // A beat's worth of steps (4) is a group: a slightly lighter label.
                c.Text((i + 1).ToString(), x + w / 2, plot.Bottom + 3, 9.5, on ? (i % 4 == 0 ? Palette.Text : Palette.DimText) : Palette.Track, align: NeoAlign.Center);
                double f = SeqStepInput.Fraction(p, i, lane);
                var colour = on ? accent : Palette.Track;
                var bar = new NeoRect(x + 2, 0, Math.Max(1, w - 4), 0);
                if (lane == SeqLane.Note)
                {
                    double y = plot.Y + (1 - f) * plot.Height;
                    bar = bar with { Y = Math.Min(y, zero), Height = Math.Max(2, Math.Abs(y - zero)) };
                    if (p.Velocity[i] == 0) colour = colour.WithAlpha(0x40);
                }
                else if (lane == SeqLane.Tie)
                {
                    if (f > 0) bar = bar with { Y = plot.Bottom - 0.45 * plot.Height, Height = 0.45 * plot.Height };
                    else bar = bar with { Y = plot.Bottom - 2, Height = 2 };
                }
                else
                {
                    double y = plot.Bottom - f * plot.Height;
                    bar = bar with { Y = y, Height = Math.Max(2, plot.Bottom - y) };
                    if (lane != SeqLane.Velocity && p.Velocity[i] == 0) colour = colour.WithAlpha(0x40);
                }
                if (i == input.Hover) colour = colour.WithAlpha(0xFF);
                c.Rect(bar, colour, NeoStroke.None, 1.5);
                if (p.Tie[i] && lane != SeqLane.Tie && on)
                    c.Rect(new NeoRect(x + 2, plot.Bottom - 3, w - 2 + 2, 3), Palette.Text.WithAlpha(0x90), NeoStroke.None, 1);
            }
            c.Line(new NeoPoint(plot.Left, Math.Round(zero) + 0.5), new NeoPoint(plot.Right, Math.Round(zero) + 0.5), new NeoStroke(Palette.DimText.WithAlpha(0x80), 1));
            double endX = plot.X + length * w;
            if (length < SeqStepInput.Steps)
                c.Line(new NeoPoint(Math.Round(endX) + 0.5, plot.Top), new NeoPoint(Math.Round(endX) + 0.5, plot.Bottom), new NeoStroke(accent.WithAlpha(0xA0), 1));
            if (input.Hover >= 0)
                c.Text($"step {input.Hover + 1}: {SeqStepInput.Text(p, input.Hover, lane)}", plot.Right - 4, plot.Top + 1, 11, Palette.Text, align: NeoAlign.Right);
        }
    }
}
