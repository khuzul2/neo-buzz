#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/neo-patch.md §3, phase NM): what the canvas modulators' editors show. The target
// table (8 rows: machine, parameter, depth, polarity, range, curve, a live position) with its pointer input, and the output scope.
// Nothing here knows a toolkit: the table asks its host (the editor) to open the pickers and to apply an edit, and draws on INeoCanvas.

using System;
using System.Collections.Generic;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Editor
{
    /// <summary>What the target table needs from its editor.</summary>
    internal interface IModTargetHost
    {
        /// <summary>The 8 slots' targets now (slots not in use are absent).</summary>
        IReadOnlyList<ModulationInfo> Rows { get; }
        /// <summary>Opens the machine picker for a slot (−1: a new target), anchored at the table point (x, y).</summary>
        void PickMachine(int slot, double x, double y);
        /// <summary>Opens the searchable parameter list of the slot's machine.</summary>
        void PickParam(int slot, double x, double y);
        /// <summary>Changes a target's depth, polarity, range or curve (the host applies it).</summary>
        void Edit(int slot, Action<ModulationInfo> change);
        void Remove(int slot);
        void BeginGesture();
        void EndGesture();
    }

    /// <summary>The target table: drawing, and the pointer.</summary>
    internal sealed class ModTable : IGraphInput
    {
        public const double Width = 600, HeaderHeight = 18, RowHeight = 22;
        public const int Slots = 8;
        public static double Height => HeaderHeight + Slots * RowHeight + 2;

        // Columns: x and width.
        static readonly (double X, double W) ColSlot = (4, 14), ColMachine = (22, 112), ColParam = (138, 128), ColDepth = (270, 84),
            ColPolarity = (358, 34), ColRange = (396, 76), ColCurve = (476, 46), ColLive = (526, 54), ColRemove = (582, 14);

        public enum Zone { None, Machine, Param, Depth, Polarity, RangeMin, RangeMax, Curve, Remove }

        readonly IModTargetHost host;
        readonly NeoColor accent;
        Zone dragZone = Zone.None;
        int dragSlot = -1;
        double dragStartX;
        int dragStartValue;
        int hoverSlot = -1;
        Zone hoverZone;

        public ModTable(IModTargetHost host, NeoColor accent)
        {
            this.host = host;
            this.accent = accent;
        }

        public bool Capturing => dragZone != Zone.None;

        static double RowY(int slot) => HeaderHeight + slot * RowHeight;

        ModulationInfo Row(int slot)
        {
            foreach (var r in host.Rows) if (r.Slot == slot) return r;
            return null;
        }

        /// <summary>The zone under a point, with the row it is in (−1 outside).</summary>
        public (int Slot, Zone Zone) HitTest(double x, double y)
        {
            if (y < HeaderHeight || y >= HeaderHeight + Slots * RowHeight) return (-1, Zone.None);
            int slot = (int)((y - HeaderHeight) / RowHeight);
            bool In((double X, double W) c) => x >= c.X && x < c.X + c.W;
            if (In(ColMachine)) return (slot, Zone.Machine);
            if (In(ColParam)) return (slot, Zone.Param);
            if (In(ColDepth)) return (slot, Zone.Depth);
            if (In(ColPolarity)) return (slot, Zone.Polarity);
            if (In(ColRange)) return (slot, x < ColRange.X + ColRange.W / 2 ? Zone.RangeMin : Zone.RangeMax);
            if (In(ColCurve)) return (slot, Zone.Curve);
            if (In(ColRemove)) return (slot, Zone.Remove);
            return (slot, Zone.None);
        }

        public bool Down(double x, double y, int clicks, bool fine)
        {
            var (slot, zone) = HitTest(x, y);
            if (slot < 0) return false;
            var row = Row(slot);
            switch (zone)
            {
                case Zone.Machine:
                    host.PickMachine(row?.Slot ?? -1, x, y);
                    return true;
                case Zone.Param:
                    if (row == null) host.PickMachine(-1, x, y);
                    else host.PickParam(row.Slot, x, y);
                    return true;
                case Zone.Remove:
                    if (row != null) host.Remove(row.Slot);
                    return true;
                case Zone.Polarity:
                    if (row != null) host.Edit(row.Slot, i => i.Bipolar = !i.Bipolar);
                    return true;
                case Zone.Depth:
                case Zone.Curve:
                case Zone.RangeMin:
                case Zone.RangeMax:
                    if (row == null) return false;
                    if (clicks >= 2)
                    {
                        // A double-click resets: depth 100 %, curve straight, range the whole parameter.
                        host.Edit(row.Slot, i =>
                        {
                            if (zone == Zone.Depth) i.Depth = 100;
                            else if (zone == Zone.Curve) i.Curve = 0;
                            else { i.RangeMin = ModSlotNone; i.RangeMax = ModSlotNone; }
                        });
                        return true;
                    }
                    dragZone = zone;
                    dragSlot = row.Slot;
                    dragStartX = x;
                    dragStartValue = zone == Zone.Depth ? row.Depth : zone == Zone.Curve ? row.Curve
                        : zone == Zone.RangeMin ? (row.RangeMin == ModSlotNone ? row.Min : row.RangeMin) : (row.RangeMax == ModSlotNone ? row.Max : row.RangeMax);
                    host.BeginGesture();
                    return true;
            }
            return false;
        }

        /// <summary>int.MinValue: no range (ModSlot.None).</summary>
        const int ModSlotNone = int.MinValue;

        public bool Move(double x, double y, bool fine)
        {
            if (dragZone == Zone.None)
            {
                var (s, z) = HitTest(x, y);
                bool changed = s != hoverSlot || z != hoverZone;
                hoverSlot = s;
                hoverZone = z;
                return changed;
            }
            var row = Row(dragSlot);
            if (row == null) return false;
            double dx = x - dragStartX;
            switch (dragZone)
            {
                case Zone.Depth:
                {
                    // The bar is 84 px for 200 points: 1 px is about 2 %; Shift for single points.
                    int v = dragStartValue + (int)Math.Round(dx * (fine ? 0.25 : 2.4));
                    host.Edit(dragSlot, i => i.Depth = Math.Clamp(v, -100, 100));
                    return true;
                }
                case Zone.Curve:
                {
                    int v = dragStartValue + (int)Math.Round(dx * (fine ? 0.5 : 4));
                    host.Edit(dragSlot, i => i.Curve = Math.Clamp(v, -100, 100));
                    return true;
                }
                default:
                {
                    // The range's two handles, as raw values across the parameter's own range.
                    double span = Math.Max(1, row.Max - row.Min);
                    int v = dragStartValue + (int)Math.Round(dx / (ColRange.W - 8) * span * (fine ? 0.1 : 1));
                    v = Math.Clamp(v, row.Min, row.Max);
                    bool lo = dragZone == Zone.RangeMin;
                    host.Edit(dragSlot, i =>
                    {
                        int a = i.RangeMin == ModSlotNone ? i.Min : i.RangeMin, b = i.RangeMax == ModSlotNone ? i.Max : i.RangeMax;
                        if (lo) a = Math.Min(v, b); else b = Math.Max(v, a);
                        i.RangeMin = a;
                        i.RangeMax = b;
                    });
                    return true;
                }
            }
        }

        public bool Up()
        {
            if (dragZone == Zone.None) return false;
            dragZone = Zone.None;
            dragSlot = -1;
            host.EndGesture();
            return true;
        }

        public bool Wheel(double x, double y, int delta, bool fine)
        {
            var (slot, zone) = HitTest(x, y);
            var row = slot >= 0 ? Row(slot) : null;
            if (row == null || (zone != Zone.Depth && zone != Zone.Curve)) return false;
            int step = (delta > 0 ? 1 : -1) * (fine ? 1 : 5);
            host.Edit(slot, i =>
            {
                if (zone == Zone.Depth) i.Depth = Math.Clamp(i.Depth + step, -100, 100);
                else i.Curve = Math.Clamp(i.Curve + step, -100, 100);
            });
            return true;
        }

        public bool Leave()
        {
            bool had = hoverSlot >= 0;
            hoverSlot = -1;
            hoverZone = Zone.None;
            return had;
        }

        /// <summary>The state's word and colour for a row.</summary>
        public static (string Text, NeoColor Colour) StateText(ModulationInfo r) => r.State switch
        {
            ModulationState.Active => ("", Palette.DimText),
            ModulationState.MachineMissing => ("machine missing", Palette.Over),
            ModulationState.ParamMissing => ("no such parameter", Palette.Over),
            _ => ("not modulated", Palette.Over),
        };

        public void Draw(INeoCanvas g, NeoRect r)
        {
            var dim = Palette.DimText;
            g.Text("#", ColSlot.X, r.Y + 3, 10, dim);
            g.Text("Machine", ColMachine.X, r.Y + 3, 10, dim);
            g.Text("Parameter", ColParam.X, r.Y + 3, 10, dim);
            g.Text("Depth", ColDepth.X, r.Y + 3, 10, dim);
            g.Text("Polarity", ColPolarity.X, r.Y + 3, 10, dim);
            g.Text("Range", ColRange.X, r.Y + 3, 10, dim);
            g.Text("Curve", ColCurve.X, r.Y + 3, 10, dim);
            g.Text("Live", ColLive.X, r.Y + 3, 10, dim);
            for (int s = 0; s < Slots; s++)
            {
                double y = r.Y + RowY(s);
                var row = Row(s);
                bool hot = s == hoverSlot;
                g.Rect(new NeoRect(r.X + 2, y + 1, Width - 4, RowHeight - 2), hot ? Palette.PanelEdge : Palette.Panel, NeoStroke.None, 3);
                g.Text((s + 1).ToString(), ColSlot.X, y + 5, 10, dim);
                if (row == null)
                {
                    g.Text("+ add a target: pick a machine", ColMachine.X, y + 5, 11, hot ? Palette.Text : dim);
                    continue;
                }
                var (state, stateColour) = StateText(row);
                bool ok = row.State == ModulationState.Active;
                g.Text(row.Machine, ColMachine.X, y + 4, 11.5, ok ? Palette.Text : Palette.Over, maxWidth: ColMachine.W - 4);
                g.Text(row.Param + (row.IsTrack ? " (t" + row.Track + ")" : ""), ColParam.X, y + 4, 11.5, ok ? Palette.Text : Palette.Over, maxWidth: ColParam.W - 4);
                if (!ok)
                {
                    g.Text(state, ColDepth.X, y + 5, 10, stateColour);
                    DrawRemove(g, y, hot && hoverZone == Zone.Remove);
                    continue;
                }
                DrawDepth(g, y, row, hot && hoverZone == Zone.Depth);
                // Polarity chip.
                var chip = new NeoRect(r.X + ColPolarity.X, y + 3, ColPolarity.W - 2, RowHeight - 6);
                g.Rect(chip, (row.Bipolar ? accent.WithAlpha(0x40) : Palette.Track), new NeoStroke(row.Bipolar ? accent : Palette.Grid, 1), 3);
                g.Text(row.Bipolar ? "bi" : "uni", chip.X + chip.Width / 2, y + 5, 10.5, Palette.Text, align: NeoAlign.Center);
                DrawRange(g, y, row);
                g.Text(row.Curve == 0 ? "0" : (row.Curve > 0 ? "+" : "") + row.Curve, ColCurve.X + ColCurve.W / 2, y + 5, 10.5, row.Curve == 0 ? dim : Palette.Text, align: NeoAlign.Center);
                DrawLive(g, y, row);
                DrawRemove(g, y, hot && hoverZone == Zone.Remove);
            }
        }

        void DrawRemove(INeoCanvas g, double y, bool hot) =>
            g.Text("×", ColRemove.X + 3, y + 3, 13, hot ? Palette.Over : Palette.DimText);

        void DrawDepth(INeoCanvas g, double y, ModulationInfo row, bool hot)
        {
            double x0 = ColDepth.X, w = ColDepth.W - 4, mid = x0 + w / 2;
            g.Rect(new NeoRect(x0, y + 8, w, 6), Palette.Track, NeoStroke.None, 3);
            double len = row.Depth / 100.0 * (w / 2);
            double a = Math.Min(mid, mid + len), b = Math.Max(mid, mid + len);
            if (b - a > 0.5) g.Rect(new NeoRect(a, y + 8, b - a, 6), hot ? accent.Tint(0.25) : accent, NeoStroke.None, 3);
            g.Line(mid, y + 5, mid, y + 17, new NeoStroke(Palette.Grid, 1, false));
            g.Text(row.Depth + "%", x0 + w, y + 4, 10, Palette.Text, align: NeoAlign.Right);
        }

        void DrawRange(INeoCanvas g, double y, ModulationInfo row)
        {
            double x0 = ColRange.X + 4, w = ColRange.W - 8;
            g.Rect(new NeoRect(x0, y + 10, w, 2), Palette.Track, NeoStroke.None, 1);
            double span = Math.Max(1, row.Max - row.Min);
            bool custom = row.RangeMin != int.MinValue && row.RangeMax != int.MinValue;
            double a = custom ? (row.RangeMin - row.Min) / span : 0, b = custom ? (row.RangeMax - row.Min) / span : 1;
            g.Rect(new NeoRect(x0 + a * w, y + 9, Math.Max(2, (b - a) * w), 4), custom ? accent : Palette.KnobTrack, NeoStroke.None, 2);
            // The safe range, where the manifest names one: a tick at each end.
            if (row.SafeMin > row.Min) g.Line(x0 + (row.SafeMin - row.Min) / span * w, y + 5, x0 + (row.SafeMin - row.Min) / span * w, y + 17, new NeoStroke(Palette.Over.WithAlpha(0x90), 1, false));
            if (row.SafeMax < row.Max) g.Line(x0 + (row.SafeMax - row.Min) / span * w, y + 5, x0 + (row.SafeMax - row.Min) / span * w, y + 17, new NeoStroke(Palette.Over.WithAlpha(0x90), 1, false));
        }

        void DrawLive(INeoCanvas g, double y, ModulationInfo row)
        {
            // The parameter's whole range as a track: the reach of the modulation as a band, the base as a tick, the value now as a dot.
            double x0 = ColLive.X, w = ColLive.W - 6;
            double span = Math.Max(1, row.Max - row.Min);
            double N(int v) => Math.Clamp((v - row.Min) / span, 0, 1) * w;
            g.Rect(new NeoRect(x0, y + 10, w, 2), Palette.Track, NeoStroke.None, 1);
            g.Rect(new NeoRect(x0 + N(row.Low), y + 8, Math.Max(2, N(row.High) - N(row.Low)), 6), accent.WithAlpha(0x70), NeoStroke.None, 2);
            g.Line(x0 + N(row.Base), y + 5, x0 + N(row.Base), y + 17, new NeoStroke(Palette.Text, 1, false));
            g.Ellipse(new NeoPoint(x0 + N(row.Current), y + 11), 3, 3, accent, NeoStroke.None);
        }
    }

    /// <summary>The modulator's output over the last moments, 0 … 1.</summary>
    internal static class ModScope
    {
        public static void Draw(INeoCanvas g, NeoRect r, float[] data, int count, float now, NeoColor accent)
        {
            g.Line(r.X + 4, r.Y + r.Height * 0.5, r.Right - 4, r.Y + r.Height * 0.5, new NeoStroke(Palette.Grid, 1, false));
            g.Text("1", r.X + 3, r.Y + 2, 9, Palette.DimText);
            g.Text("0", r.X + 3, r.Bottom - 12, 9, Palette.DimText);
            double top = r.Y + 8, bottom = r.Bottom - 8, left = r.X + 14, right = r.Right - 8;
            if (data != null && count > 1)
            {
                var path = new NeoPath();
                for (int i = 0; i < count; i++)
                {
                    double x = left + (right - left) * i / (count - 1);
                    double y = bottom - Math.Clamp(data[i], 0f, 1f) * (bottom - top);
                    if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
                }
                g.Path(path, NeoFill.None, new NeoStroke(accent, 1.6));
            }
            double ny = bottom - Math.Clamp(now, 0f, 1f) * (bottom - top);
            g.Ellipse(new NeoPoint(right, ny), 3.5, 3.5, accent, NeoStroke.None);
            g.Text(Math.Round(now * 100) + " %", r.Right - 6, r.Y + 2, 10, Palette.Text, align: NeoAlign.Right);
        }
    }
}
