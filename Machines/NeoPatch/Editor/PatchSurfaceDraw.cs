#nullable disable
// Neo Patch NP2: drawing the inner canvas (docs/specs/neo-patch.md §4.5), on the kit's canvas. The look is the main canvas's: the design tokens' surfaces,
// a box with a band in the identity colour of its group (the machine boxes' band), the type scale (11 / 13 / 15), the 8 pt grid for the box's
// rows; wires in the colour of the signal they carry; a per-voice node drawn as a stack of two cards behind it.

using System;
using System.Collections.Generic;
using System.Linq;
using NeoBuzz.Editor;

namespace NeoBuzz.Patch
{
    internal sealed partial class PatchSurface
    {
        /// <summary>The refusal banner stays this long (ms).</summary>
        public const long RefusalMs = 8000;

        public void Draw(INeoCanvas cv, NeoRect r)
        {
            w = r.Width > 0 ? r.Width : Width;
            h = r.Height > 0 ? r.Height : Height;
            DrawCanvas(cv);
            PanelPass(cv);
            DrawMacros(cv);
            DrawToolbar(cv);
            DrawCallout(cv);
            Menu?.Draw(cv, w, h);
        }

        // ------------------------------------------------------------------ canvas

        void DrawCanvas(INeoCanvas cv)
        {
            var c = CanvasRect;
            cv.Rect(c, Tokens.SurfaceBase, NeoStroke.None);
            cv.PushClip(c);
            // The grid: a line every 32 world units, stronger every 128.
            if (Zoom >= 0.4)
            {
                double step = 32 * Zoom;
                double ox = c.X + ((PanX % step) + step) % step, oy = c.Y + ((PanY % step) + step) % step;
                var grid = new NeoStroke(Tokens.GridLine, 1);
                for (double x = ox; x < c.Right; x += step) cv.Line(x, c.Y, x, c.Bottom, grid);
                for (double y = oy; y < c.Bottom; y += step) cv.Line(c.X, y, c.Right, y, grid);
            }
            var g = model.Graph;
            var refusal = ActiveRefusal();
            var loopWires = refusal == null ? new HashSet<WireKey>() : new HashSet<WireKey>(refusal.Wires);
            var loopNodes = refusal == null ? new HashSet<int>() : new HashSet<int>(refusal.Nodes);

            foreach (var wire in g.Wires) DrawWire(cv, wire, loopWires.Contains(WireKey.Of(wire)));
            foreach (var n in g.Nodes) DrawNode(cv, n, loopNodes.Contains(n.Id));
            DrawPendingWire(cv, refusal);

            if (drag == Drag.Marquee && moved)
            {
                var box = new NeoRect(Math.Min(downX, pointerX), Math.Min(downY, pointerY), Math.Abs(pointerX - downX), Math.Abs(pointerY - downY));
                cv.Rect(box, Tokens.Accent.WithAlpha(0x20), new NeoStroke(Tokens.Accent, 1));
            }
            if (g.Nodes.Count == 0) DrawEmpty(cv);
            DrawBanner(cv, refusal);
            cv.PopClip();
            cv.Line(c.X, c.Bottom, c.Right, c.Bottom, new NeoStroke(Palette.PanelEdge, 1));
        }

        Refusal ActiveRefusal()
        {
            var rf = model.LastRefusal;
            if (rf == null) return null;
            return clock() - rf.At <= RefusalMs ? rf : null;
        }

        void DrawEmpty(INeoCanvas cv)
        {
            var c = CanvasRect;
            double cx = c.X + c.Width / 2, cy = c.Y + c.Height / 2;
            cv.Text("An empty patch", cx, cy - 74, Tokens.FontHeading, Palette.Text, true, NeoAlign.Center);
            string[] steps =
            {
                "1  Add Keys: it brings in the notes you play.",
                "2  Add an Osc and wire Keys' Pitch to it: that is the sound.",
                "3  Add an Out and wire the Osc into it: that is what you hear.",
            };
            for (int i = 0; i < steps.Length; i++) cv.Text(steps[i], cx, cy - 40 + i * 18, Tokens.FontBody - 1, Palette.DimText, align: NeoAlign.Center);
            cv.Text("Or right-click anywhere to add a node.", cx, cy + 14 + 30 + 12, Tokens.FontBody - 1, Palette.DimText, align: NeoAlign.Center);
            var s = StarterRect;
            panelItems.Add(new PItem { Kind = PK.Button, Rect = s, Id = "starter" });
            cv.Rect(s, accent.WithAlpha(0x40), new NeoStroke(accent, 1), 6);
            cv.Text("Add a basic voice", s.X + s.Width / 2, s.Y + 7, Tokens.FontBody, Palette.Text, true, NeoAlign.Center);
        }

        void DrawBanner(INeoCanvas cv, Refusal refusal)
        {
            string text = null;
            NeoColor col = Tokens.Error;
            if (drag == Drag.Wire && !string.IsNullOrEmpty(HoverVerdict)) text = HoverVerdict;
            else if (refusal != null) text = refusal.Message;
            else if (model.Notice != null && clock() - model.NoticeAt < 5000) { text = model.Notice; col = Tokens.Warning; }
            if (text == null) return;
            var c = CanvasRect;
            double wi = Math.Min(c.Width - 32, 620);
            var lines = Wrap(cv, text, 12, wi - 28).Take(3).ToList();
            double hh = 16 + lines.Count * 16;
            var box = new NeoRect(c.X + (c.Width - wi) / 2, c.Y + 12, wi, hh);
            cv.Rect(box, Tokens.SurfacePanel, new NeoStroke(col, 1.5), 8);
            cv.Rect(new NeoRect(box.X + 10, box.Y + 8, 3, hh - 16), col, NeoStroke.None, 1.5);
            for (int i = 0; i < lines.Count; i++) cv.Text(lines[i], box.X + 22, box.Y + 8 + i * 16, 12, Palette.Text);
        }

        void DrawNode(INeoCanvas cv, Node n, bool inLoop)
        {
            var r = NodeRect(n);
            var c = CanvasRect;
            if (r.Right < c.X - 8 || r.X > c.Right + 8 || r.Bottom < c.Y - 8 || r.Y > c.Bottom + 8) return;
            var info = n.Info;
            var col = GroupColour(info.Group);
            bool selected = model.Selection.Contains(n.Id), heard = model.IsHeard(n.Id), global = model.IsGlobal(n.Id);
            double z = Zoom;
            byte alpha = heard ? (byte)0xFF : (byte)0x70;
            // A node that runs for each voice is a stack of cards: two edges show behind it.
            if (!global)
            {
                for (int k = 2; k >= 1; k--)
                    cv.Rect(new NeoRect(r.X + 3.5 * k * z, r.Y + 3.5 * k * z, r.Width, r.Height), Tokens.SurfacePanel.WithAlpha(alpha), new NeoStroke(Tokens.SurfaceBorder.WithAlpha(alpha), 1), 6 * z);
            }
            var stroke = inLoop ? new NeoStroke(Tokens.Error, 2.5) : selected ? new NeoStroke(Tokens.Accent, 2) : new NeoStroke(Tokens.SurfaceBorder, 1);
            cv.Rect(r, Tokens.SurfacePanel.WithAlpha(heard ? (byte)0xFF : (byte)0xC0), stroke, 6 * z);
            // The identity band, as on the machine boxes.
            cv.Rect(new NeoRect(r.X + 1, r.Y + 1, r.Width - 2, Math.Max(2, 3 * z)), col.WithAlpha(alpha), NeoStroke.None, 2);
            double titleSize = Math.Max(8, Tokens.FontBody * z);
            if (z >= 0.45)
                cv.Text(info.Name, r.X + 10 * z, r.Y + 6 * z, titleSize, Palette.Text.WithAlpha(alpha), true, maxWidth: r.Width - 40 * z);
            else cv.Text(info.Name, r.X + 6 * z, r.Y + 5 * z, Math.Max(7, 12 * z * 1.4), Palette.Text.WithAlpha(alpha), true, maxWidth: r.Width - 8 * z);
            if (z >= 0.6) cv.Text("#" + n.Id, r.Right - 8 * z, r.Y + 8 * z, Math.Max(8, 10 * z), Palette.DimText.WithAlpha(alpha), align: NeoAlign.Right);

            double labelSize = Math.Max(8, Tokens.FontCaption * z);
            for (int p = 0; p < info.Ins.Length; p++)
            {
                var pos = PortPos(n, p, false);
                DrawPort(cv, n, p, false, pos);
                if (z >= 0.6) cv.Text(info.Ins[p].Name, pos.X + 10 * z, pos.Y - labelSize * 0.62, labelSize, Palette.DimText.WithAlpha(alpha), maxWidth: (NodeW / 2 - 14) * z);
            }
            for (int p = 0; p < info.Outs.Length; p++)
            {
                var pos = PortPos(n, p, true);
                DrawPort(cv, n, p, true, pos);
                if (z >= 0.6) cv.Text(info.Outs[p].Name, pos.X - 10 * z, pos.Y - labelSize * 0.62, labelSize, Palette.DimText.WithAlpha(alpha), align: NeoAlign.Right, maxWidth: (NodeW / 2 - 14) * z);
            }
            if (z >= 0.6)
            {
                string sum = Summary(n);
                if (sum.Length > 0) cv.Text(sum, r.X + 10 * z, r.Bottom - (FooterH - 5) * z, Math.Max(8, 10.5 * z), Palette.DimText.WithAlpha(alpha), maxWidth: r.Width - 18 * z);
            }
            if (!heard && z >= 0.6) cv.Text("not heard", r.Right - 8 * z, r.Bottom - (FooterH - 5) * z, Math.Max(8, 10 * z), Tokens.Warning, align: NeoAlign.Right);
        }

        /// <summary>The line at the foot of a node: its first controls in their units.</summary>
        public static string Summary(Node n)
        {
            var info = n.Info;
            if (info.Controls.Length == 0) return "";
            var parts = new List<string>();
            for (int i = 0; i < Math.Min(2, info.Controls.Length); i++) parts.Add(ControlScale.Format(info.Controls[i], n.Controls[i]));
            if (n.Kind == NodeKind.Envelope) parts[1] = "R " + ControlScale.Format(info.Controls[NodeInfo.EnvR], n.Controls[NodeInfo.EnvR]);
            if (n.Kind == NodeKind.Out) parts[1] = "pan " + ControlScale.Format(info.Controls[1], n.Controls[1]);
            if (n.Kind == NodeKind.Mixer) return "4 gains";
            return string.Join("  ·  ", parts);
        }

        void DrawPort(INeoCanvas cv, Node n, int port, bool output, NeoPoint pos)
        {
            var info = output ? n.Info.Outs[port] : n.Info.Ins[port];
            var col = PortColour(info.Type);
            double r = PortR * Math.Max(0.7, Zoom);
            bool wired = model.Graph.Wires.Any(wi => output ? wi.FromNode == n.Id && wi.FromPort == port : wi.ToNode == n.Id && wi.ToPort == port);
            bool hot = HoverPort is PortHit hp && hp.Node == n && hp.Port == port && hp.Output == output;
            bool target = drag == Drag.Wire && dragFrom.Output != output && HoverPort is PortHit h2 && h2.Node == n && h2.Port == port && h2.Output == output;
            if (target) col = string.IsNullOrEmpty(HoverVerdict) ? Tokens.Play : Tokens.Error;
            if (hot) r *= 1.35;
            cv.Ellipse(pos, r, r, wired || hot ? (NeoFill)col : (NeoFill)Tokens.SurfaceBase, new NeoStroke(col, 1.5));
        }

        void DrawWire(INeoCanvas cv, Wire wire, bool inLoop)
        {
            var pts = WireCurve(wire);
            if (pts.Length == 0) return;
            var from = model.Graph.Find(wire.FromNode);
            var type = from.Info.Outs[wire.FromPort].Type;
            var key = WireKey.Of(wire);
            bool selected = model.SelectedWire is WireKey sw && sw.Equals(key);
            bool hot = HoverWire is WireKey hw && hw.Equals(key);
            var col = inLoop ? Tokens.Error : PortColour(type);
            double width = (selected ? 3.2 : hot ? 2.8 : 1.8) * Math.Max(0.8, Math.Min(1.3, Zoom));
            byte alpha = selected || hot || inLoop ? (byte)0xFF : (byte)0xB0;
            cv.Path(NeoPath.Polyline(pts), NeoFill.None, new NeoStroke(col.WithAlpha(alpha), width));
            var mid = pts[pts.Length / 2];
            if (Math.Abs(wire.Amount - 1f) > 0.001f && Zoom >= 0.6)
            {
                string t = "×" + wire.Amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                double tw = cv.MeasureText(t, 10) + 8;
                cv.Rect(new NeoRect(mid.X - tw / 2, mid.Y - 8, tw, 16), Tokens.SurfaceBase, new NeoStroke(col, 1), 8);
                cv.Text(t, mid.X, mid.Y - 6, 10, Palette.Text, false, NeoAlign.Center);
            }
            else if (Learn && Zoom >= 0.5)
            {
                // The Learn overlay: each wire says what it carries.
                string t = type.ToString();
                double tw = cv.MeasureText(t, 10) + 10;
                cv.Rect(new NeoRect(mid.X - tw / 2, mid.Y - 8, tw, 16), Tokens.SurfaceBase, new NeoStroke(col, 1), 8);
                cv.Text(t, mid.X, mid.Y - 6, 10, col, true, NeoAlign.Center);
            }
        }

        void DrawPendingWire(INeoCanvas cv, Refusal refusal)
        {
            if (drag != Drag.Wire) return;
            var from = dragFrom;
            var a = PortPos(from.Node, from.Port, from.Output);
            var b = new NeoPoint(pointerX, pointerY);
            if (HoverPort is PortHit t && t.Output != from.Output) b = PortPos(t.Node, t.Port, t.Output);
            var pts = from.Output ? WireCurve(a, b) : WireCurve(b, a);
            var type = (from.Output ? from.Node.Info.Outs[from.Port] : from.Node.Info.Ins[from.Port]).Type;
            var col = !string.IsNullOrEmpty(HoverVerdict) ? Tokens.Error : PortColour(type);
            cv.Path(NeoPath.Polyline(pts), NeoFill.None, new NeoStroke(col, 2.5));
        }

        // ------------------------------------------------------------------ the Learn overlay

        void DrawCallout(INeoCanvas cv)
        {
            if (Menu != null || drag != Drag.None || pointerX < 0) return;
            var g = model.Graph;
            string head = null, body1 = null, body2 = null;
            NeoColor col = Palette.Text;
            if (HoverWire is WireKey wk && Learn)
            {
                var from = g.Find(wk.FromNode);
                var to = g.Find(wk.ToNode);
                if (from == null || to == null) return;
                var type = from.Info.Outs[wk.FromPort].Type;
                var wire = g.Wires.FirstOrDefault(q => WireKey.Of(q).Equals(wk));
                head = from.Info.Name + " " + from.Id + " · " + from.Info.Outs[wk.FromPort].Name + "  →  " + to.Info.Name + " " + to.Id + " · " + to.Info.Ins[wk.ToPort].Name;
                body1 = NodeHelp.TypeWords(type);
                body2 = "Carries: " + NodeHelp.For(from.Kind).Outs[wk.FromPort] + "  Into: " + NodeHelp.For(to.Kind).Ins[wk.ToPort]
                    + (model.IsGlobal(from.Id) ? "  Runs once." : "  Runs for each voice.") + (Math.Abs(wire.Amount - 1f) > 0.001f ? "  Amount ×" + wire.Amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "." : "");
                col = PortColour(type);
            }
            else if (HoverPort is PortHit ph && Learn)
            {
                var pi = ph.Output ? ph.Node.Info.Outs[ph.Port] : ph.Node.Info.Ins[ph.Port];
                head = ph.Node.Info.Name + " " + ph.Node.Id + " · " + pi.Name + (ph.Output ? " (output)" : " (input)");
                body1 = NodeHelp.TypeWords(pi.Type);
                body2 = (ph.Output ? NodeHelp.For(ph.Node.Kind).Outs[ph.Port] : NodeHelp.For(ph.Node.Kind).Ins[ph.Port]);
                col = PortColour(pi.Type);
            }
            else if (HoverNode != null && Learn)
            {
                head = HoverNode.Info.Name + " " + HoverNode.Id;
                body1 = NodeHelp.For(HoverNode.Kind).Summary;
                col = GroupColour(HoverNode.Info.Group);
            }
            if (head == null) return;
            double wi = 360;
            var lines = new List<(string, NeoColor, bool)> { (head, col, true) };
            foreach (string l in Wrap(cv, body1, 11, wi - 20)) lines.Add((l, Palette.Text, false));
            if (body2 != null) foreach (string l in Wrap(cv, body2, 11, wi - 20)) lines.Add((l, Palette.DimText, false));
            double hh = 14 + lines.Count * 15;
            double bx = Math.Min(pointerX + 16, w - wi - 8), by = pointerY + 18;
            if (by + hh > CanvasRect.Bottom) by = pointerY - hh - 12;
            cv.Rect(new NeoRect(bx, by, wi, hh), Tokens.SurfacePanel, new NeoStroke(col, 1.2), 8);
            for (int i = 0; i < lines.Count; i++) cv.Text(lines[i].Item1, bx + 10, by + 8 + i * 15, lines[i].Item3 ? 12 : 11, lines[i].Item2, lines[i].Item3, maxWidth: wi - 20);
        }

        // ------------------------------------------------------------------ toolbar and macro strip

        void DrawToolbar(INeoCanvas cv)
        {
            var t = ToolbarRect;
            cv.Rect(t, Palette.Panel, NeoStroke.None);
            cv.Line(t.X, t.Bottom, t.Right, t.Bottom, new NeoStroke(Palette.PanelEdge, 1));
            var nr = NameRect;
            string name = string.IsNullOrEmpty(model.Graph.Name) ? "Untitled patch" : model.Graph.Name;
            cv.Text(name, nr.X + 4, nr.Y + 3, Tokens.FontTitle, accent, true, maxWidth: nr.Width - 8);
            foreach (var b in Buttons())
            {
                bool on = b.Id == "learn" && Learn;
                cv.Rect(b.Rect, on ? accent.WithAlpha(0x50) : Tokens.SurfaceControl, new NeoStroke(on ? accent : Tokens.SurfaceBorder, 1), 4);
                string label = b.Id == "learn" ? "Learn" : b.Label;
                cv.Text(label, b.Rect.X + b.Rect.Width / 2, b.Rect.Y + 4, 12, Palette.Text, on, NeoAlign.Center);
                if (b.Id == "zoomout") cv.Text(Math.Round(Zoom * 100) + "%", b.Rect.Right + 22, b.Rect.Y + 5, 11, Palette.DimText, align: NeoAlign.Center);
            }
            // The port types.
            double x = 584;
            foreach (PortType pt in new[] { PortType.Audio, PortType.Control, PortType.Gate, PortType.Pitch })
            {
                cv.Ellipse(new NeoPoint(x, 18), 5, 5, PortColour(pt), NeoStroke.None);
                cv.Text(pt.ToString(), x + 10, 11, 11, Palette.DimText);
                x += 18 + cv.MeasureText(pt.ToString(), 11) + 10;
            }
            // The CPU readout and the node count, at the right.
            float cpu = model.Engine.CpuPercent;
            var cc = cpu >= 90 ? Tokens.MeterOver : cpu >= 60 ? Tokens.MeterHot : Tokens.MeterNormal;
            string cpuText = "CPU " + (cpu < 10 ? cpu.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : Math.Round(cpu).ToString(System.Globalization.CultureInfo.InvariantCulture)) + " %";
            cv.Text(cpuText, t.Right - 12, 10, 12, cc, true, NeoAlign.Right);
            double cw = cv.MeasureText(cpuText, 12, true);
            cv.Text(model.Graph.Nodes.Count + "/" + NodeInfo.Cap + " nodes", t.Right - 24 - cw, 11, 11, Palette.DimText, align: NeoAlign.Right);
        }

        void DrawMacros(INeoCanvas cv)
        {
            var m = MacroRect;
            cv.Rect(m, Palette.Panel, NeoStroke.None);
            cv.Line(m.X, m.Y, m.Right, m.Y, new NeoStroke(Palette.PanelEdge, 1));
            cv.Text("MACROS", 12, m.Y + 6, 10, accent, true);
            cv.Text("Patterns, automation and MIDI move these; each moves the node controls mapped to it. Click one to see its mappings.", 74, m.Y + 6, 10, Palette.DimText, maxWidth: w - 90);
            var g = model.Graph;
            for (int i = 0; i < 16; i++)
            {
                var c = MacroCell(i);
                bool sel = model.SelectedMacro == i;
                int maps = g.Macros.Count(q => q.Macro == i);
                cv.Rect(c, Tokens.SurfaceBase, new NeoStroke(sel ? Tokens.Accent : hoverMacro == i ? Tokens.SurfaceBorder : Palette.PanelEdge, sel ? 2 : 1), 6);
                cv.Text((i + 1).ToString(), c.X + 7, c.Y + 5, 10, maps > 0 ? accent : Palette.DimText, true);
                cv.Text(PatchEditModel.MacroName(g, i), c.X + 20, c.Y + 4, 11, maps > 0 ? Palette.Text : Palette.DimText, maps > 0, maxWidth: c.Width - 26);
                var bar = new NeoRect(c.X + 6, c.Y + 24, c.Width - 12, 6);
                cv.Rect(bar, Tokens.SurfaceBorder, NeoStroke.None, 3);
                float v = host.Macro(i);
                cv.Rect(new NeoRect(bar.X, bar.Y, Math.Max(2, bar.Width * v), bar.Height), maps > 0 ? accent : Tokens.ControlTrack, NeoStroke.None, 3);
                cv.Ellipse(new NeoPoint(bar.X + bar.Width * v, bar.Y + 3), 5, 5, Palette.Text, new NeoStroke(Palette.Background, 1.5));
                cv.Text(maps == 0 ? "no mapping" : maps == 1 ? "1 control" : maps + " controls", c.X + 7, c.Y + 36, 10, Palette.DimText, maxWidth: c.Width - 10);
            }
        }
    }
}
