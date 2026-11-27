#nullable disable
// Neo Pulse's editor (docs/specs/neo-pulse.md): global Clock & Probability bar on top, eight-row interactive
// Lanes overview (live Euclidean step strip with hits, accents and playing step beside inline Steps, Pulses,
// Rotation, Chance, Note and Divider controls), and a synced Lane 1..8 detail panel below. One source for WPF
// and Avalonia (the Neo editor kit), light and dark theme safe.

using System;
using System.Globalization;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Pulse
{
    internal sealed class PulseLaneGrid : IGraphInput
    {
        public const double Width = 800;
        public const double HeaderHeight = 18;
        public const double RowHeight = 32;
        public const double Height = HeaderHeight + PulseEngine.LaneCount * RowHeight + 4;

        public static readonly (double X, double W) ColLane = (4, 24);
        public static readonly (double X, double W) ColMute = (32, 24);
        public static readonly (double X, double W) ColStrip = (62, 444);
        public static readonly (double X, double W) ColSteps = (512, 42);
        public static readonly (double X, double W) ColPulses = (558, 44);
        public static readonly (double X, double W) ColRot = (606, 36);
        public static readonly (double X, double W) ColChance = (646, 52);
        public static readonly (double X, double W) ColNote = (702, 56);
        public static readonly (double X, double W) ColDiv = (762, 34);

        enum Zone { None, Lane, Mute, Strip, Steps, Pulses, Rot, Chance, Note, Div }

        readonly EditorContext ctx;
        readonly Action<int> onSelectLane;
        int selectedLane;
        int hoverLane = -1;
        Zone hoverZone = Zone.None;

        int dragLane = -1;
        Zone dragZone = Zone.None;
        double dragStartY;
        int dragStartVal;

        public PulseLaneGrid(EditorContext ctx, Action<int> onSelectLane)
        {
            this.ctx = ctx;
            this.onSelectLane = onSelectLane;
        }

        public int SelectedLane
        {
            get => selectedLane;
            set => selectedLane = Math.Clamp(value, 0, PulseEngine.LaneCount - 1);
        }

        public bool Capturing => dragLane >= 0;

        static bool In(double x, (double X, double W) c) => x >= c.X && x < c.X + c.W;

        static (int Lane, Zone Zone) HitTest(double x, double y)
        {
            if (y < HeaderHeight || y >= HeaderHeight + PulseEngine.LaneCount * RowHeight) return (-1, Zone.None);
            int lane = (int)((y - HeaderHeight) / RowHeight);
            if (lane < 0 || lane >= PulseEngine.LaneCount) return (-1, Zone.None);
            if (In(x, ColLane)) return (lane, Zone.Lane);
            if (In(x, ColMute)) return (lane, Zone.Mute);
            if (In(x, ColStrip)) return (lane, Zone.Strip);
            if (In(x, ColSteps)) return (lane, Zone.Steps);
            if (In(x, ColPulses)) return (lane, Zone.Pulses);
            if (In(x, ColRot)) return (lane, Zone.Rot);
            if (In(x, ColChance)) return (lane, Zone.Chance);
            if (In(x, ColNote)) return (lane, Zone.Note);
            if (In(x, ColDiv)) return (lane, Zone.Div);
            return (lane, Zone.Lane);
        }

        int FieldFor(Zone z) => z switch
        {
            Zone.Steps => PulseEngine.FSteps,
            Zone.Pulses => PulseEngine.FPulses,
            Zone.Rot => PulseEngine.FRot,
            Zone.Chance => PulseEngine.FChance,
            Zone.Note => PulseEngine.FNote,
            Zone.Div => PulseEngine.FDiv,
            _ => -1,
        };

        void Select(int lane)
        {
            selectedLane = lane;
            onSelectLane?.Invoke(lane);
        }

        public bool Down(double x, double y, int clicks, bool fine)
        {
            var (lane, zone) = HitTest(x, y);
            if (lane < 0) return false;
            Select(lane);

            if (zone == Zone.Mute)
            {
                int p = PulseEngine.LaneParam(lane, PulseEngine.FMute);
                ctx.Edits?.Begin();
                ctx.Set(p, ctx.Raw(p) == 0 ? 1 : 0);
                ctx.Edits?.End();
                return true;
            }
            if (zone == Zone.Div)
            {
                int p = PulseEngine.LaneParam(lane, PulseEngine.FDiv);
                ctx.Edits?.Begin();
                ctx.Set(p, (ctx.Raw(p) + 1) % 4);
                ctx.Edits?.End();
                return true;
            }
            int field = FieldFor(zone);
            if (field >= 0)
            {
                int p = PulseEngine.LaneParam(lane, field);
                if (clicks >= 2)
                {
                    ctx.Edits?.Begin();
                    ctx.Set(p, Params.Table[p].Default);
                    ctx.Edits?.End();
                    return true;
                }
                dragLane = lane;
                dragZone = zone;
                dragStartY = y;
                dragStartVal = ctx.Raw(p);
                ctx.Edits?.Begin();
                return true;
            }
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (dragLane >= 0)
            {
                int field = FieldFor(dragZone);
                if (field < 0) return false;
                int p = PulseEngine.LaneParam(dragLane, field);
                double scale = dragZone == Zone.Chance || dragZone == Zone.Note ? (fine ? 0.25 : 0.6) : (fine ? 0.15 : 0.35);
                int delta = (int)Math.Round((dragStartY - y) * scale);
                int max = dragZone == Zone.Pulses ? Math.Clamp(ctx.Raw(PulseEngine.LaneParam(dragLane, PulseEngine.FSteps)), 1, 32)
                    : dragZone == Zone.Rot ? Math.Max(0, Math.Clamp(ctx.Raw(PulseEngine.LaneParam(dragLane, PulseEngine.FSteps)), 1, 32) - 1)
                    : Params.Table[p].Max;
                int val = Math.Clamp(dragStartVal + delta, Params.Table[p].Min, max);
                ctx.Set(p, val);
                return true;
            }

            var (lane, zone) = HitTest(x, y);
            bool changed = lane != hoverLane || zone != hoverZone;
            hoverLane = lane;
            hoverZone = zone;
            return changed;
        }

        public bool Up()
        {
            if (dragLane < 0) return false;
            dragLane = -1;
            dragZone = Zone.None;
            ctx.Edits?.End();
            return true;
        }

        public bool Wheel(double x, double y, int delta, bool fine)
        {
            var (lane, zone) = HitTest(x, y);
            if (lane < 0) return false;
            int field = FieldFor(zone);
            if (field < 0) return false;
            int p = PulseEngine.LaneParam(lane, field);
            int step = (delta > 0 ? 1 : -1) * (zone == Zone.Chance && !fine ? 5 : 1);
            int max = zone == Zone.Pulses ? Math.Clamp(ctx.Raw(PulseEngine.LaneParam(lane, PulseEngine.FSteps)), 1, 32)
                : zone == Zone.Rot ? Math.Max(0, Math.Clamp(ctx.Raw(PulseEngine.LaneParam(lane, PulseEngine.FSteps)), 1, 32) - 1)
                : Params.Table[p].Max;
            ctx.Edits?.Begin();
            ctx.Set(p, Math.Clamp(ctx.Raw(p) + step, Params.Table[p].Min, max));
            ctx.Edits?.End();
            return true;
        }

        public bool Leave()
        {
            bool had = hoverLane >= 0;
            hoverLane = -1;
            hoverZone = Zone.None;
            return had;
        }

        public void Draw(INeoCanvas g, NeoRect r, NeoColor accent, NeoPulseMachine machine)
        {
            var dim = Palette.DimText;
            g.Text("#", r.X + ColLane.X + ColLane.W / 2, r.Y + 3, 10, dim, false, NeoAlign.Center);
            g.Text("M", r.X + ColMute.X + ColMute.W / 2, r.Y + 3, 10, dim, false, NeoAlign.Center);
            g.Text("Euclidean step ring (hits · accents · live step)", r.X + ColStrip.X + 2, r.Y + 3, 10, dim);
            g.Text("Steps", r.X + ColSteps.X + ColSteps.W / 2, r.Y + 3, 10, dim, false, NeoAlign.Center);
            g.Text("Pulses", r.X + ColPulses.X + ColPulses.W / 2, r.Y + 3, 10, dim, false, NeoAlign.Center);
            g.Text("Rot", r.X + ColRot.X + ColRot.W / 2, r.Y + 3, 10, dim, false, NeoAlign.Center);
            g.Text("Chance", r.X + ColChance.X + ColChance.W / 2, r.Y + 3, 10, dim, false, NeoAlign.Center);
            g.Text("Note", r.X + ColNote.X + ColNote.W / 2, r.Y + 3, 10, dim, false, NeoAlign.Center);
            g.Text("Div", r.X + ColDiv.X + ColDiv.W / 2, r.Y + 3, 10, dim, false, NeoAlign.Center);

            for (int l = 0; l < PulseEngine.LaneCount; l++)
            {
                double y = r.Y + HeaderHeight + l * RowHeight;
                bool sel = l == selectedLane;
                bool hot = l == hoverLane;
                int b = PulseEngine.LaneBase + l * PulseEngine.LaneStride;

                int steps = Math.Clamp(ctx.Raw(b + PulseEngine.FSteps), 1, 32);
                int pulses = Math.Clamp(ctx.Raw(b + PulseEngine.FPulses), 0, steps);
                int rot = Math.Clamp(ctx.Raw(b + PulseEngine.FRot), 0, steps - 1);
                int chance = Math.Clamp(ctx.Raw(b + PulseEngine.FChance), 0, 100);
                int accPulses = Math.Clamp(ctx.Raw(b + PulseEngine.FAccPulses), 0, steps);
                int accRot = Math.Clamp(ctx.Raw(b + PulseEngine.FAccRot), 0, steps - 1);
                int note = Math.Clamp(ctx.Raw(b + PulseEngine.FNote), 0, 127);
                int divIdx = Math.Clamp(ctx.Raw(b + PulseEngine.FDiv), 0, 3);
                bool muted = ctx.Raw(b + PulseEngine.FMute) != 0;

                // Row background card.
                var rowRect = new NeoRect(r.X + 2, y + 2, Width - 4, RowHeight - 4);
                var rowFill = sel ? accent.WithAlpha(0x1E) : (hot ? Palette.PanelEdge.WithAlpha(0x50) : Palette.Panel);
                var rowStroke = sel ? new NeoStroke(accent.WithAlpha(0x90), 1) : new NeoStroke(Palette.PanelEdge, 1);
                g.Rect(rowRect, rowFill, rowStroke, 4);

                // Lane number badge.
                var badgeRect = new NeoRect(r.X + ColLane.X + 2, y + 6, ColLane.W - 4, RowHeight - 12);
                g.Rect(badgeRect, sel ? accent : Palette.Track, NeoStroke.None, 3);
                g.Text((l + 1).ToString(CultureInfo.InvariantCulture), badgeRect.X + badgeRect.Width / 2, y + 9, 10.5,
                    sel ? Palette.Background : Palette.Text, true, NeoAlign.Center);

                // Mute toggle chip.
                var muteRect = new NeoRect(r.X + ColMute.X + 1, y + 6, ColMute.W - 2, RowHeight - 12);
                g.Rect(muteRect, muted ? Palette.Over.WithAlpha(0x38) : Palette.Track,
                    new NeoStroke(muted ? Palette.Over : Palette.PanelEdge, 1), 3);
                g.Text("M", muteRect.X + muteRect.Width / 2, y + 9, 10, muted ? Palette.Over : dim, muted, NeoAlign.Center);

                // Euclidean step strip (1..32 cells scaled evenly across ColStrip).
                uint mask = PulseEuclidean.Rhythm(pulses, steps, rot);
                uint accMask = PulseEuclidean.Rhythm(accPulses, steps, accRot);
                int now = machine?.Engine.CurrentStep(l) ?? -1;

                double sx = r.X + ColStrip.X;
                double sw = ColStrip.W;
                double cellStep = sw / steps;
                double gap = steps > 20 ? 1.5 : 2.5;
                double cellW = Math.Max(4.0, cellStep - gap);
                double cellH = RowHeight - 12;
                double cellY = y + 6;

                for (int s = 0; s < steps; s++)
                {
                    bool hit = ((mask >> s) & 1u) != 0;
                    bool acc = ((accMask >> s) & 1u) != 0;
                    bool lit = !muted && s == now;
                    double cx = sx + s * cellStep + gap * 0.5;
                    var cr = new NeoRect(cx, cellY, cellW, cellH);

                    NeoColor fill;
                    if (hit)
                    {
                        if (muted) fill = accent.WithAlpha(0x35);
                        else if (lit) fill = accent.Tint(0.45);
                        else if (acc) fill = accent.Tint(0.20);
                        else fill = accent.WithAlpha(0xB8);
                    }
                    else
                    {
                        fill = lit ? accent.WithAlpha(0x30) : Palette.Background;
                    }

                    var stroke = lit
                        ? new NeoStroke(accent.Tint(0.55), 1.5)
                        : new NeoStroke(s % 4 == 0 ? Palette.Grid : Palette.PanelEdge, 1);
                    g.Rect(cr, fill, stroke, 2.5);

                    if (hit && acc && !muted)
                    {
                        double mw = Math.Max(2.5, Math.Min(cellW - 4, 6));
                        g.Rect(new NeoRect(cx + (cellW - mw) * 0.5, cellY + 2.5, mw, 2.5), Palette.Background, NeoStroke.None, 1);
                    }
                    else if (!hit && acc && !muted)
                    {
                        double mw = Math.Max(2.0, Math.Min(cellW - 4, 4));
                        g.Rect(new NeoRect(cx + (cellW - mw) * 0.5, cellY + cellH - 4.5, mw, 2.0), accent.WithAlpha(0x70), NeoStroke.None, 1);
                    }
                }

                // Inline numeric cells.
                NeoColor valCol = muted ? dim : Palette.Text;
                void DrawCell((double X, double W) col, Zone z, string text, bool active)
                {
                    bool zHot = hot && hoverZone == z;
                    var rect = new NeoRect(r.X + col.X, y + 6, col.W, RowHeight - 12);
                    g.Rect(rect, zHot ? Palette.PanelEdge : Palette.Background, new NeoStroke(zHot ? accent : Palette.PanelEdge, 1), 3);
                    g.Text(text, rect.X + rect.Width / 2, y + 9, 10, active ? valCol : dim, false, NeoAlign.Center);
                }

                bool hasHits = pulses > 0 && !muted;
                DrawCell(ColSteps, Zone.Steps, steps.ToString(CultureInfo.InvariantCulture), hasHits);
                DrawCell(ColPulses, Zone.Pulses, pulses.ToString(CultureInfo.InvariantCulture), hasHits);
                DrawCell(ColRot, Zone.Rot, rot.ToString(CultureInfo.InvariantCulture), hasHits);
                DrawCell(ColChance, Zone.Chance, chance.ToString(CultureInfo.InvariantCulture) + "%", hasHits);
                DrawCell(ColNote, Zone.Note, PulseText.FormatNote(note) + " " + note.ToString(CultureInfo.InvariantCulture), hasHits);
                DrawCell(ColDiv, Zone.Div, (divIdx + 1).ToString(CultureInfo.InvariantCulture) + "x", hasHits);
            }
        }
    }

    internal sealed class NeoPulseEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x4F, 0xC3, 0xE0));
        const double D = 38;
        const double SmallD = 34;

        protected override string Title => "Neo Pulse";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            PulseText.Install(Params.Table);
            var machine = ctx.Machine?.ManagedMachine as NeoPulseMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            // Global Clock & Probability row.
            var clockRow = Layout.Row(
                new ParamDropdown(ctx, P.Rate, "Rate", 72),
                Layout.Gap(8),
                new ParamDropdown(ctx, P.Feel, "Feel", 86),
                Layout.Gap(12),
                new ParamKnob(ctx, P.Swing, "Swing", D),
                new ParamKnob(ctx, P.Fill, "Fill", D),
                new ParamKnob(ctx, P.HumTime, "Hum time", D),
                new ParamKnob(ctx, P.HumVel, "Hum vel", D),
                new ParamKnob(ctx, P.Seed, "Seed", D));

            // Lane 1..8 detail pages below, kept in sync with clicking any row in the Lanes overview.
            var laneBodies = new UIElement[PulseEngine.LaneCount];
            double pageW = PulseLaneGrid.Width;
            double pageH = 148;
            for (int l = 0; l < PulseEngine.LaneCount; l++)
            {
                int b = PulseEngine.LaneBase + l * PulseEngine.LaneStride;
                var rowA = Layout.Row(
                    new ParamKnob(ctx, b + PulseEngine.FSteps, "Steps", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FPulses, "Pulses", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FRot, "Rotation", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FChance, "Chance", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FAccPulses, "Acc pulses", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FAccRot, "Acc rot", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FNote, "Note", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FVel, "Velocity", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FAccVel, "Acc vel", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FGate, "Gate", SmallD));
                var rowB = Layout.Row(
                    new ParamKnob(ctx, b + PulseEngine.FRatchet, "Ratchet", SmallD),
                    new ParamKnob(ctx, b + PulseEngine.FRatchetCh, "Ratchet %", SmallD),
                    Layout.Gap(10),
                    new ParamDropdown(ctx, b + PulseEngine.FDiv, "Divider", 66),
                    Layout.Gap(10),
                    new ParamKnob(ctx, b + PulseEngine.FCh, "Channel", SmallD),
                    Layout.Gap(10),
                    new ParamChoice(ctx, b + PulseEngine.FMute, "Mute", 44));
                laneBodies[l] = Layout.Column(rowA, Layout.Gap(1, 4), rowB);
            }

#if AVALONIA
            laneBodies[0].Measure(Avalonia.Size.Infinity);
            pageW = Math.Max(PulseLaneGrid.Width, Math.Ceiling(laneBodies[0].DesiredSize.Width));
            pageH = Math.Ceiling(laneBodies[0].DesiredSize.Height);
#else
            laneBodies[0].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            pageW = Math.Max(PulseLaneGrid.Width, Math.Ceiling(laneBodies[0].DesiredSize.Width));
            pageH = Math.Ceiling(laneBodies[0].DesiredSize.Height);
#endif
            var pages = new Pages(AccentColor, pageW, pageH, null);
            for (int l = 0; l < PulseEngine.LaneCount; l++)
                pages.Add("Lane " + (l + 1).ToString(CultureInfo.InvariantCulture), laneBodies[l]);

            PulseLaneGrid gridModel = null;
            InteractiveGraph lanesView = null;
            gridModel = new PulseLaneGrid(ctx, lane =>
            {
                pages.Selected = lane;
                lanesView?.InvalidateVisual();
            });
            lanesView = new InteractiveGraph(PulseLaneGrid.Width, PulseLaneGrid.Height, (g, r) =>
            {
                if (gridModel.SelectedLane != pages.Selected) gridModel.SelectedLane = pages.Selected;
                gridModel.Draw(g, r, AccentColor, machine);
            }, gridModel, animate: true);
            ctx.Changed += lanesView.InvalidateVisual;

            return Layout.Column(
                S("Clock & Probability", clockRow),
                S("Lanes", lanesView),
                S("Lane Detail", pages));
        }
    }
}
