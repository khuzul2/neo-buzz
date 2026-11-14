#nullable disable
// Neo Patch NP2 (docs/specs/neo-patch.md §4.5): the inner canvas, toolkit-free. One surface drawn on the Neo kit's INeoCanvas and driven through the
// kit's pointer interface (IGraphInput, with the right button, keys and text added: IGraphInputPlus), so the Windows app (WPF) and the studio (Avalonia)
// show and do the same. It holds only view state (zoom, pan, hover, drags, the open menu); the patch and every edit are the PatchEditModel's.
//
//   +-------------------------------------------------------------+
//   | toolbar: name · Fit − + zoom · Learn · port legend · CPU    |
//   +--------------------------------------------+----------------+
//   | canvas: nodes, ports, wires, menu, banner  | side panel     |
//   |                                            | (selected node)|
//   +--------------------------------------------+----------------+
//   | macro strip: Macro 1 … 16 with label, value and mapping     |
//   +-------------------------------------------------------------+
//
// Partial files: PatchSurface.cs (state, geometry, pointer and keys), PatchSurfaceDraw.cs (the canvas), PatchPanel.cs (toolbar, side panel, macro strip).

using System;
using System.Collections.Generic;
using System.Linq;
using NeoBuzz.Editor;

namespace NeoBuzz.Patch
{
    /// <summary>What the surface needs from the machine besides the patch: the macros' values (parameters), the voice count and the macros' undo gesture.</summary>
    internal interface IPatchSurfaceHost
    {
        float Macro(int i);
        void SetMacro(int i, float value01);
        void BeginMacro();
        void EndMacro();
        int Voices { get; }
    }

    internal sealed partial class PatchSurface : IGraphInputPlus
    {
        public const double Width = 1180, Height = 660;
        public const double ToolbarH = 36, MacroH = 84, PanelW = 300;

        // The node box, in world units (one unit is a pixel at zoom 1).
        public const double NodeW = 152, HeaderH = 24, RowH = 20, FooterH = 22, PortR = 5;
        public const double MinZoom = 0.25, MaxZoom = 2.5;

        readonly PatchEditModel model;
        readonly IPatchSurfaceHost host;
        readonly NeoColor accent;
        readonly Func<long> clock;
        double w = Width, h = Height;

        public PatchSurface(PatchEditModel model, IPatchSurfaceHost host, NeoColor accent, Func<long> clock = null)
        {
            this.model = model;
            this.host = host;
            this.accent = accent;
            this.clock = clock ?? (() => Environment.TickCount64);
            model.Changed += () => { };
        }

        public PatchEditModel Model => model;
        public NeoColor Accent => accent;

        // ------------------------------------------------------------------ view state

        public double Zoom { get; private set; } = 1;
        public double PanX { get; private set; } = 40;
        public double PanY { get; private set; } = 40;
        public bool Learn { get; set; }
        public PatchMenu Menu { get; private set; }

        /// <summary>The view as text, saved with the machine (zoom and pan), and restored.</summary>
        public string ViewText
        {
            get => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.###} {1:0.#} {2:0.#}", Zoom, PanX, PanY);
            set
            {
                var f = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3) return;
                if (double.TryParse(f[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double z)
                    && double.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double px)
                    && double.TryParse(f[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double py)
                    && double.IsFinite(z) && double.IsFinite(px) && double.IsFinite(py))
                {
                    Zoom = Math.Clamp(z, MinZoom, MaxZoom);
                    PanX = Math.Clamp(px, -20000, 20000);
                    PanY = Math.Clamp(py, -20000, 20000);
                }
            }
        }

        // ------------------------------------------------------------------ regions

        public NeoRect ToolbarRect => new NeoRect(0, 0, w, ToolbarH);
        public NeoRect CanvasRect => new NeoRect(0, ToolbarH, w - PanelW, h - ToolbarH - MacroH);
        public NeoRect PanelRect => new NeoRect(w - PanelW, ToolbarH, PanelW, h - ToolbarH - MacroH);
        public NeoRect MacroRect => new NeoRect(0, h - MacroH, w, MacroH);

        // ------------------------------------------------------------------ geometry

        public static double NodeHeight(Node n) => HeaderH + Math.Max(1, Math.Max(n.Info.Ins.Length, n.Info.Outs.Length)) * RowH + FooterH;

        /// <summary>A node's box on the surface.</summary>
        public NeoRect NodeRect(Node n) =>
            new NeoRect(CanvasRect.X + PanX + n.X * Zoom, CanvasRect.Y + PanY + n.Y * Zoom, NodeW * Zoom, NodeHeight(n) * Zoom);

        /// <summary>A port's dot on the surface.</summary>
        public NeoPoint PortPos(Node n, int port, bool output)
        {
            var r = NodeRect(n);
            return new NeoPoint(output ? r.Right : r.X, r.Y + (HeaderH + RowH * port + RowH / 2) * Zoom);
        }

        public NeoPoint ToWorld(double x, double y) => new NeoPoint((x - CanvasRect.X - PanX) / Zoom, (y - CanvasRect.Y - PanY) / Zoom);

        /// <summary>The wire's curve, from an output to an input, as points on the surface.</summary>
        public NeoPoint[] WireCurve(NeoPoint a, NeoPoint b)
        {
            double d = Math.Max(40 * Zoom, Math.Abs(b.X - a.X) * 0.5);
            var c1 = new NeoPoint(a.X + d, a.Y);
            var c2 = new NeoPoint(b.X - d, b.Y);
            const int N = 24;
            var pts = new NeoPoint[N + 1];
            for (int i = 0; i <= N; i++)
            {
                double t = i / (double)N, u = 1 - t;
                pts[i] = new NeoPoint(u * u * u * a.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * b.X,
                    u * u * u * a.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * b.Y);
            }
            return pts;
        }

        public NeoPoint[] WireCurve(Wire wire)
        {
            var from = model.Graph.Find(wire.FromNode);
            var to = model.Graph.Find(wire.ToNode);
            if (from == null || to == null) return new NeoPoint[0];
            return WireCurve(PortPos(from, wire.FromPort, true), PortPos(to, wire.ToPort, false));
        }

        static double SegDistance(NeoPoint p, NeoPoint a, NeoPoint b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
            double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        // ------------------------------------------------------------------ hit tests

        public readonly record struct PortHit(Node Node, int Port, bool Output);

        double PortHitRadius => Math.Max(9, 8 * Zoom);

        public PortHit? PortAt(double x, double y)
        {
            if (!CanvasRect.Contains(x, y)) return null;
            var nodes = model.Graph.Nodes;
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                var n = nodes[i];
                var r = NodeRect(n);
                if (x < r.X - PortHitRadius || x > r.Right + PortHitRadius || y < r.Y || y > r.Bottom) continue;
                for (int p = 0; p < n.Info.Ins.Length; p++)
                {
                    var c = PortPos(n, p, false);
                    if (Math.Abs(c.X - x) <= PortHitRadius && Math.Abs(c.Y - y) <= PortHitRadius * 0.9) return new PortHit(n, p, false);
                }
                for (int p = 0; p < n.Info.Outs.Length; p++)
                {
                    var c = PortPos(n, p, true);
                    if (Math.Abs(c.X - x) <= PortHitRadius && Math.Abs(c.Y - y) <= PortHitRadius * 0.9) return new PortHit(n, p, true);
                }
            }
            return null;
        }

        public Node NodeAt(double x, double y)
        {
            if (!CanvasRect.Contains(x, y)) return null;
            var nodes = model.Graph.Nodes;
            for (int i = nodes.Count - 1; i >= 0; i--)
                if (NodeRect(nodes[i]).Contains(x, y)) return nodes[i];
            return null;
        }

        public WireKey? WireAt(double x, double y)
        {
            if (!CanvasRect.Contains(x, y)) return null;
            var p = new NeoPoint(x, y);
            WireKey? best = null;
            double bestD = 6;
            foreach (var wire in model.Graph.Wires)
            {
                var pts = WireCurve(wire);
                for (int i = 1; i < pts.Length; i++)
                {
                    double d = SegDistance(p, pts[i - 1], pts[i]);
                    if (d < bestD) { bestD = d; best = WireKey.Of(wire); }
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ view

        /// <summary>Shows every node: zoom and pan to fit the canvas (no more than 1.2 times).</summary>
        /// <summary>A patch made without positions (an agent, a template) has its nodes stacked on one spot: lay them out in columns once, without a history step.</summary>
        public void TidyIfStacked()
        {
            var ns = model.Graph.Nodes;
            for (int i = 1; i < ns.Count; i++)
                for (int j = 0; j < i; j++)
                    if (Math.Abs(ns[i].X - ns[j].X) < NodeW && Math.Abs(ns[i].Y - ns[j].Y) < NodeHeight(ns[i]))
                    {
                        model.AutoLayout(NodeHeight, record: false);
                        return;
                    }
        }

        public void Fit()
        {
            var nodes = model.Graph.Nodes;
            var c = CanvasRect;
            if (nodes.Count == 0) { Zoom = 1; PanX = 40; PanY = 40; return; }
            double l = nodes.Min(n => n.X), t = nodes.Min(n => n.Y), r = nodes.Max(n => n.X + NodeW), b = nodes.Max(n => n.Y + NodeHeight(n));
            double z = Math.Clamp(Math.Min((c.Width - 48) / (r - l), (c.Height - 48) / (b - t)), MinZoom, 1.2);
            Zoom = z;
            PanX = (c.Width - (r - l) * z) / 2 - l * z;
            PanY = (c.Height - (b - t) * z) / 2 - t * z;
        }

        public void ZoomBy(double factor, double px, double py)
        {
            var c = CanvasRect;
            double nz = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
            var wp = ToWorld(px, py);
            Zoom = nz;
            PanX = px - c.X - wp.X * nz;
            PanY = py - c.Y - wp.Y * nz;
        }

        // ------------------------------------------------------------------ menus

        static readonly string[] GroupOrder = { "Notes", "Sources", "Modulation", "Tone", "Plumbing" };

        public static NeoColor GroupColour(string group) => group switch
        {
            "Notes" => Tokens.Identity(MachineCategory.Control),
            "Sources" => Tokens.Identity(MachineCategory.Synth),
            "Modulation" => Tokens.Identity(MachineCategory.Modulation),
            "Tone" => Tokens.Identity(MachineCategory.EqFilter),
            _ => Tokens.Identity(MachineCategory.Utility),
        };

        public static NeoColor PortColour(PortType t) => t switch
        {
            PortType.Audio => NeoColor.FromRgb(0xEE, 0xB2, 0x35),
            PortType.Control => NeoColor.FromRgb(0x0B, 0xD1, 0xB9),
            PortType.Gate => NeoColor.FromRgb(0xF8, 0xA9, 0x8D),
            _ => NeoColor.FromRgb(0xCF, 0xAA, 0xF8),
        };

        public void OpenAddMenu(double x, double y)
        {
            var at = ToWorld(x, y);
            var items = new List<MenuItemModel>();
            foreach (string group in GroupOrder)
            {
                items.Add(new MenuItemModel { Label = group, Header = true, Colour = GroupColour(group) });
                for (int k = 0; k < NodeInfo.KindCount; k++)
                {
                    var info = NodeInfo.For((NodeKind)k);
                    if (info.Group != group) continue;
                    var kind = (NodeKind)k;
                    items.Add(new MenuItemModel
                    {
                        Label = info.Name, Detail = info.Ins.Length + " in · " + info.Outs.Length + " out", Colour = GroupColour(group),
                        Pick = () => model.AddNode(kind, (float)at.X, (float)at.Y),
                    });
                }
            }
            // The words of the summary are searchable too ("filter" finds Filter and the Shaper's "bend").
            foreach (var it in items.Where(i => !i.Header))
            {
                var kind = Enum.GetValues(typeof(NodeKind)).Cast<NodeKind>().First(kk => NodeInfo.For(kk).Name == it.Label);
                it.Detail = it.Detail + " " + NodeHelp.For(kind).Summary;
            }
            Menu = new PatchMenu("Add a node", items, true, x, y);
        }

        void OpenNodeMenu(Node n, double x, double y)
        {
            var items = new List<MenuItemModel>
            {
                new MenuItemModel { Label = "Duplicate", Detail = "Ctrl+D", Pick = () => model.Duplicate() },
                new MenuItemModel { Label = "Copy", Detail = "Ctrl+C", Pick = () => model.Copy() },
                new MenuItemModel { Label = "Delete", Detail = "Delete", Pick = () => model.DeleteSelection() },
                new MenuItemModel { Label = "Disconnect all wires", Pick = () => { foreach (int id in model.Selection.ToArray()) model.DisconnectNode(id); } },
            };
            if (!n.Info.ForcedGlobal && !n.Info.ForcedPerVoice)
                items.Insert(3, new MenuItemModel
                {
                    Label = n.Global ? "Make per voice" : "Make global", Detail = n.Global ? "runs for each voice" : "runs once",
                    Pick = () => { foreach (int id in model.Selection.ToArray()) model.SetGlobal(id, !n.Global); },
                });
            Menu = new PatchMenu(n.Info.Name + " " + n.Id, items, false, x, y);
        }

        void OpenCanvasMenuForWire(WireKey wk, double x, double y)
        {
            var items = new List<MenuItemModel>
            {
                new MenuItemModel { Label = "Delete wire", Detail = "Delete", Pick = () => model.Disconnect(wk) },
                new MenuItemModel { Label = "Amount 1.0", Pick = () => model.SetWireAmount(wk, 1f) },
                new MenuItemModel { Label = "Amount 0.5", Pick = () => model.SetWireAmount(wk, 0.5f) },
                new MenuItemModel { Label = "Amount -1.0 (inverted)", Pick = () => model.SetWireAmount(wk, -1f) },
            };
            Menu = new PatchMenu("Wire", items, false, x, y);
        }

        public void CloseMenu() => Menu = null;

        // ------------------------------------------------------------------ pointer

        enum Drag { None, Pan, Move, Wire, Marquee, Slider, MacroValue, WireAmount, MapLow, MapHigh }

        Drag drag;
        double downX, downY, dragX, dragY;
        bool moved;
        Dictionary<int, (float X, float Y)> moveStart;
        PortHit dragFrom;
        (int Node, int Control) sliderTarget;
        int macroTarget;
        (int Node, int Control) mapTarget;
        // Hover.
        public PortHit? HoverPort { get; private set; }
        public Node HoverNode { get; private set; }
        public WireKey? HoverWire { get; private set; }
        public string HoverVerdict { get; private set; }
        double pointerX = -1, pointerY = -1;

        public bool Capturing => drag != Drag.None;
        public bool WantsText => Menu != null && Menu.WantsText;

        public bool Down(double x, double y, int clicks, bool fine)
        {
            pointerX = x; pointerY = y;
            downX = x; downY = y; dragX = x; dragY = y; moved = false;
            if (Menu != null) return MenuDown(x, y);
            if (ToolbarRect.Contains(x, y)) return ToolbarDown(x, y);
            if (MacroRect.Contains(x, y)) return MacroDown(x, y);
            if (PanelRect.Contains(x, y)) return PanelDown(x, y, clicks);
            if (!CanvasRect.Contains(x, y)) return false;
            if (model.Graph.Nodes.Count == 0 && StarterRect.Contains(x, y)) { AddStarter(); return true; }

            var port = PortAt(x, y);
            if (port is PortHit ph)
            {
                dragFrom = ph;
                drag = Drag.Wire;
                return true;
            }
            var node = NodeAt(x, y);
            if (node != null)
            {
                if (fine) model.Select(node.Id, add: true);
                else if (!model.Selection.Contains(node.Id)) model.Select(node.Id);
                moveStart = model.Selection.Select(model.Graph.Find).Where(n => n != null).ToDictionary(n => n.Id, n => (n.X, n.Y));
                if (moveStart.Count > 0)
                {
                    model.BeginGesture(moveStart.Count == 1 ? "Move " + node.Info.Name : "Move " + moveStart.Count + " nodes");
                    drag = Drag.Move;
                }
                return true;
            }
            var wk = WireAt(x, y);
            if (wk != null)
            {
                model.SelectWire(wk);
                return true;
            }
            // Empty canvas: a drag pans, with Shift it selects a box; a click clears.
            drag = fine ? Drag.Marquee : Drag.Pan;
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            pointerX = x; pointerY = y;
            if (drag == Drag.None) { UpdateHover(x, y); return true; }
            if (Math.Abs(x - downX) + Math.Abs(y - downY) > 3) moved = true;
            switch (drag)
            {
                case Drag.Pan:
                    PanX += x - dragX; PanY += y - dragY;
                    break;
                case Drag.Move:
                    model.MoveNodes(moveStart.Keys, moveStart, (float)((x - downX) / Zoom), (float)((y - downY) / Zoom));
                    break;
                case Drag.Wire:
                    UpdateHover(x, y);
                    break;
                case Drag.Slider:
                    SliderTo(x, fine);
                    break;
                case Drag.MacroValue:
                    MacroTo(x);
                    break;
                case Drag.WireAmount:
                    WireAmountTo(x);
                    break;
                case Drag.MapLow:
                case Drag.MapHigh:
                    MappingTo(x);
                    break;
            }
            dragX = x; dragY = y;
            return true;
        }

        public bool Up()
        {
            var was = drag;
            drag = Drag.None;
            switch (was)
            {
                case Drag.Move:
                    model.EndGesture();
                    moveStart = null;
                    break;
                case Drag.Wire:
                    FinishWire(pointerX, pointerY);
                    break;
                case Drag.Marquee:
                    if (moved)
                    {
                        var box = new NeoRect(Math.Min(downX, pointerX), Math.Min(downY, pointerY), Math.Abs(pointerX - downX), Math.Abs(pointerY - downY));
                        model.SelectMany(model.Graph.Nodes.Where(n => NodeRect(n).Overlaps(box)).Select(n => n.Id));
                    }
                    else model.ClearSelection();
                    break;
                case Drag.Pan:
                    if (!moved) model.ClearSelection();
                    break;
                case Drag.Slider:
                case Drag.WireAmount:
                case Drag.MapLow:
                case Drag.MapHigh:
                    model.EndGesture();
                    break;
                case Drag.MacroValue:
                    host.EndMacro();
                    break;
            }
            return true;
        }

        void FinishWire(double x, double y)
        {
            var target = PortAt(x, y);
            HoverVerdict = null;
            if (target is not PortHit t || t.Output == dragFrom.Output) return;
            var from = dragFrom.Output ? dragFrom : t;
            var to = dragFrom.Output ? t : dragFrom;
            model.Connect(from.Node.Id, from.Port, to.Node.Id, to.Port);
        }

        void UpdateHover(double x, double y)
        {
            HoverPort = null; HoverNode = null; HoverWire = null; HoverVerdict = null;
            hoverHit = null;
            if (Menu != null) return;
            if (CanvasRect.Contains(x, y))
            {
                HoverPort = PortAt(x, y);
                if (HoverPort == null)
                {
                    HoverNode = NodeAt(x, y);
                    if (HoverNode == null) HoverWire = WireAt(x, y);
                }
                if (drag == Drag.Wire && HoverPort is PortHit t && t.Output != dragFrom.Output)
                {
                    var from = dragFrom.Output ? dragFrom : t;
                    var to = dragFrom.Output ? t : dragFrom;
                    var verdict = model.CanConnect(from.Node.Id, from.Port, to.Node.Id, to.Port);
                    HoverVerdict = verdict.Ok ? "" : verdict.Message;
                }
            }
            else if (PanelRect.Contains(x, y)) HoverPanel(x, y);
            else if (MacroRect.Contains(x, y)) HoverMacro(x, y);
        }

        public bool Wheel(double x, double y, int delta, bool fine)
        {
            if (Menu != null)
            {
                Menu.Wheel(delta > 0 ? 1 : -1);
                return true;
            }
            if (CanvasRect.Contains(x, y))
            {
                if (fine) PanX += delta > 0 ? 48 : -48;
                else ZoomBy(delta > 0 ? 1.1 : 1 / 1.1, x, y);
                return true;
            }
            if (PanelRect.Contains(x, y))
            {
                panelScroll = Math.Max(0, panelScroll - Math.Sign(delta) * 32);
                return true;
            }
            return false;
        }

        public bool Leave()
        {
            if (drag == Drag.None) { HoverPort = null; HoverNode = null; HoverWire = null; hoverHit = null; }
            return true;
        }

        public bool RightDown(double x, double y)
        {
            pointerX = x; pointerY = y;
            if (Menu != null) { Menu = null; return true; }
            if (MacroRect.Contains(x, y)) return MacroRight(x, y);
            if (!CanvasRect.Contains(x, y)) return false;
            var node = NodeAt(x, y);
            if (node != null)
            {
                if (!model.Selection.Contains(node.Id)) model.Select(node.Id);
                OpenNodeMenu(node, x, y);
                return true;
            }
            var wk = WireAt(x, y);
            if (wk != null)
            {
                model.SelectWire(wk);
                OpenCanvasMenuForWire(wk.Value, x, y);
                return true;
            }
            OpenAddMenu(x, y);
            return true;
        }

        bool MenuDown(double x, double y)
        {
            int row = Menu.RowAt(x, y, w, h);
            if (row >= 0 && !Menu.Visible[row].Header)
            {
                var m = Menu;
                var item = m.Visible[row];
                Menu = null;
                item.Pick?.Invoke();
                return true;
            }
            if (!Menu.Box(w, h).Contains(x, y)) Menu = null;
            return true;
        }

        // ------------------------------------------------------------------ keys

        public bool Key(string key, bool ctrl, bool shift)
        {
            if (Menu != null)
            {
                switch (key)
                {
                    case "Escape": Menu = null; return true;
                    case "Up": Menu.Move(-1); return true;
                    case "Down": Menu.Move(1); return true;
                    case "Return":
                    case "Enter":
                    {
                        var m = Menu;
                        if (m.Accept()) { if (ReferenceEquals(Menu, m)) Menu = null; }
                        return true;
                    }
                    case "Back": Menu.Backspace(); return true;
                }
                return Menu.WantsText;
            }
            switch (key)
            {
                case "Escape":
                    if (drag == Drag.Wire) { drag = Drag.None; HoverVerdict = null; return true; }
                    model.ClearSelection();
                    return true;
                case "Delete":
                case "Back":
                    return model.DeleteSelection();
                case "C" when ctrl: return model.Copy();
                case "V" when ctrl:
                {
                    var c = CanvasRect;
                    var at = ToWorld(pointerX >= 0 && c.Contains(pointerX, pointerY) ? pointerX : c.X + c.Width / 2, pointerX >= 0 && c.Contains(pointerX, pointerY) ? pointerY : c.Y + c.Height / 2);
                    return model.Paste((float)at.X, (float)at.Y) > 0;
                }
                case "D" when ctrl: return model.Duplicate() > 0;
                case "A" when ctrl: model.SelectAll(); return true;
                case "F":
                case "Home":
                    Fit();
                    return true;
                case "Left": return model.MoveBy(shift ? -32 : -8, 0);
                case "Right": return model.MoveBy(shift ? 32 : 8, 0);
                case "Up": return model.MoveBy(0, shift ? -32 : -8);
                case "Down": return model.MoveBy(0, shift ? 32 : 8);
            }
            return false;
        }

        public bool Text(string text)
        {
            if (Menu == null || !Menu.WantsText) return false;
            Menu.Type(text);
            return true;
        }
    }
}
