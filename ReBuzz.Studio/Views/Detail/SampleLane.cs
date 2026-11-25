// Fork (TM1, docs/specs/machines.md Neo Sampler Tracker mode): the Sample lane under the piano roll. The same chips as on the notes, in a strip, also for
// notes too short to wear one: a click opens the sample picker for that note, Alt+wheel steps its sample. Notes that start together stack.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Detail
{
    using Control = Avalonia.Controls.Control;

    internal sealed class SampleLane : Control
    {
        public const double LaneHeight = 92;
        const double ChipHeight = 22;
        const double Stack = 27;

        readonly PianoRoll roll;
        double offsetX;

        public SampleLane(PianoRoll roll)
        {
            this.roll = roll;
            Height = LaneHeight;
            ClipToBounds = true;
            Focusable = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Sample lane");
            ToolTip.SetTip(this, "Sample: click a chip to choose the note's sample, Alt+wheel steps it to the next or previous one.");
        }

        public double OffsetX { get => offsetX; set { offsetX = value; InvalidateVisual(); } }

        double ChipWidth => Math.Clamp(roll.TickWidth - 3, 22, 30);

        /// <summary>Each note's chip: stacked under the note before it that starts at the same time.</summary>
        List<(int Index, Rect Rect)> Chips()
        {
            var list = new List<(int, Rect)>();
            var seen = new Dictionary<int, int>();
            var notes = roll.Notes;
            double w = ChipWidth;
            foreach (int i in Enumerable.Range(0, notes.Count).OrderBy(i => notes[i].Time).ThenBy(i => notes[i].Note))
            {
                seen.TryGetValue(notes[i].Time, out int k);
                seen[notes[i].Time] = k + 1;
                list.Add((i, new Rect(roll.X(notes[i].Time) - offsetX + 3, 6 + k * Stack, w, ChipHeight)));
            }
            return list;
        }

        /// <summary>Where a note's chip is, for tests that click it.</summary>
        internal Point ChipPoint(int index)
        {
            var c = Chips().First(x => x.Index == index).Rect;
            return new Point(c.X + c.Width / 2, c.Y + c.Height / 2);
        }

        static readonly IPen Guide = new Pen(StudioTokens.SurfaceBorderBrush, 1);

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            dc.FillRectangle(StudioTokens.SurfaceViewBrush, new Rect(0, 0, w, h));
            dc.DrawLine(Guide, new Point(0, 0.5), new Point(w, 0.5));
            var notes = roll.Notes;
            var samples = roll.Samples;
            if (samples == null) return;
            var selected = roll.Selection;
            foreach (var (i, r) in Chips())
            {
                if (r.Right < roll.KeyColumn || r.X > w) continue;
                var n = notes[i];
                bool sel = selected.Contains(i);
                dc.DrawRectangle(n.Sample > 0 ? SampleColours.BrushOf(n.Sample) : StudioTokens.SurfacePanelBrush, n.Sample > 0 ? null : Guide, r, 4, 4);
                if (sel) dc.DrawRectangle(null, new Pen(Brushes.White, 2), r.Inflate(1), 4, 4);
                var t = SampleColours.Text(SampleColours.Label(n.Sample), 11, n.Sample > 0 ? SampleColours.Ink : StudioTokens.TextSecondaryBrush);
                dc.DrawText(t, new Point(r.X + (r.Width - t.Width) / 2, r.Y + (r.Height - t.Height) / 2));
            }
            dc.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(0, 1, roll.KeyColumn, h - 1));
            dc.DrawText(new FormattedText("Sample", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush), new Point(3, 4));
        }

        int HitTest(Point p)
        {
            foreach (var (i, r) in Chips().AsEnumerable().Reverse())
                if (p.X >= roll.KeyColumn && r.Contains(p)) return i;
            return -1;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(this);
            int hit = HitTest(p);
            if (hit < 0) return;
            var chip = Chips().First(x => x.Index == hit).Rect;
            roll.RequestPickerFor(hit, this, chip);
            e.Handled = true;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
            int hit = HitTest(e.GetPosition(this));
            double d = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            if (hit < 0 || d == 0) return;
            roll.StepSample(hit, d > 0 ? 1 : -1);
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            int hit = HitTest(e.GetPosition(this));
            Cursor = hit >= 0 ? new Cursor(StandardCursorType.Hand) : null;
        }
    }
}
