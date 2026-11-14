#nullable disable
// Neo Patch NP2: the toolbar, the side panel and the macro strip of the inner canvas (docs/specs/neo-patch.md §4.5). The side panel and the
// macro strip are laid out by one pass (PanelPass) that both draws and lists what can be hit, so a click always lands on what is drawn.

using System;
using System.Collections.Generic;
using System.Linq;
using NeoBuzz.Editor;

namespace NeoBuzz.Patch
{
    internal sealed partial class PatchSurface
    {
        public enum PK { Slider, Chip, Dropdown, MapChip, Scope, Button, WireAmount, MapRange, MapRemove }

        public sealed class PItem
        {
            public PK Kind;
            public NeoRect Rect;
            public int Node, Control, Index;
            public string Id;
            public bool Disabled;
        }

        delegate void BtnFn(string id, string label, double x, double width, bool danger = false);

        double panelScroll;
        PItem hoverHit;
        (int Node, int Control) explain = (0, -1);
        int hoverMacro = -1;
        double sliderBaseT, sliderBaseX;
        NeoRect sliderRect;
        int mapIndex;

        /// <summary>The side panel's hit items from the last pass (for tests).</summary>
        public IReadOnlyList<PItem> PanelItems => panelItems;
        readonly List<PItem> panelItems = new List<PItem>();

        // ------------------------------------------------------------------ toolbar

        public readonly record struct Button(string Id, NeoRect Rect, string Label);

        public IEnumerable<Button> Buttons()
        {
            double x = 230, y = 6;
            yield return new Button("fit", new NeoRect(x, y, 44, 24), "Fit");
            yield return new Button("zoomout", new NeoRect(x + 50, y, 26, 24), "−");
            yield return new Button("zoomin", new NeoRect(x + 122, y, 26, 24), "+");
            yield return new Button("learn", new NeoRect(x + 158, y, 64, 24), "Learn");
            yield return new Button("tidy", new NeoRect(x + 228, y, 44, 24), "Tidy");
        }

        public NeoRect NameRect => new NeoRect(8, 6, 214, 24);

        bool ToolbarDown(double x, double y)
        {
            if (NameRect.Contains(x, y)) { OpenRename(); return true; }
            foreach (var b in Buttons())
            {
                if (!b.Rect.Contains(x, y)) continue;
                switch (b.Id)
                {
                    case "fit": Fit(); break;
                    case "zoomout": ZoomBy(1 / 1.25, CanvasRect.X + CanvasRect.Width / 2, CanvasRect.Y + CanvasRect.Height / 2); break;
                    case "zoomin": ZoomBy(1.25, CanvasRect.X + CanvasRect.Width / 2, CanvasRect.Y + CanvasRect.Height / 2); break;
                    case "learn": Learn = !Learn; break;
                    case "tidy": if (model.AutoLayout(NodeHeight)) Fit(); break;
                }
                return true;
            }
            return true;
        }

        void OpenRename()
        {
            Menu = new PatchMenu("Name the patch", new MenuItemModel[0], false, 12, ToolbarH + 4, t => { model.Rename(t); Menu = null; }, model.Graph.Name);
        }

        void OpenMacroRename(int macro)
        {
            Menu = new PatchMenu("Name macro " + (macro + 1), new MenuItemModel[0], false, 12 + macro * (w - 24) / 16, h - MacroH - 70, t => { model.SetMacroLabel(macro, t); Menu = null; }, model.Graph.MacroLabels[macro]);
        }

        // ------------------------------------------------------------------ macro strip

        public NeoRect MacroCell(int i)
        {
            double cw = (w - 24) / 16.0;
            return new NeoRect(12 + i * cw, MacroRect.Y + 24, cw - 4, MacroH - 32);
        }

        int MacroAt(double x, double y)
        {
            for (int i = 0; i < 16; i++) if (MacroCell(i).Contains(x, y)) return i;
            return -1;
        }

        bool MacroDown(double x, double y)
        {
            int i = MacroAt(x, y);
            if (i < 0) return true;
            model.SelectMacro(i);
            macroTarget = i;
            host.BeginMacro();
            drag = Drag.MacroValue;
            MacroTo(x);
            return true;
        }

        bool MacroRight(double x, double y)
        {
            int i = MacroAt(x, y);
            if (i < 0) return true;
            model.SelectMacro(i);
            var items = new List<MenuItemModel>
            {
                new MenuItemModel { Label = "Name…", Pick = () => OpenMacroRename(i) },
                new MenuItemModel { Label = "Clear its mappings", Pick = () => { foreach (var m in model.Graph.Macros.Where(m => m.Macro == i).ToArray()) model.UnmapControl(m.NodeId, m.Control); } },
                new MenuItemModel { Label = "Reset to 50 %", Pick = () => { host.BeginMacro(); host.SetMacro(i, 0.5f); host.EndMacro(); } },
            };
            Menu = new PatchMenu("Macro " + (i + 1), items, false, x, y - 90);
            return true;
        }

        void MacroTo(double x)
        {
            var c = MacroCell(macroTarget);
            host.SetMacro(macroTarget, (float)Math.Clamp((x - c.X - 4) / (c.Width - 8), 0, 1));
        }

        void HoverMacro(double x, double y) => hoverMacro = MacroAt(x, y);

        // ------------------------------------------------------------------ side panel: hits

        bool PanelDown(double x, double y, int clicks)
        {
            PanelPass(null);
            var it = panelItems.LastOrDefault(i => i.Rect.Contains(x, y));
            if (it == null) return true;
            var g = model.Graph;
            switch (it.Kind)
            {
                case PK.Slider when !it.Disabled:
                {
                    var n = g.Find(it.Node);
                    if (n == null) return true;
                    var info = n.Info.Controls[it.Control];
                    explain = (it.Node, it.Control);
                    if (clicks >= 2) { model.ResetControl(it.Node, it.Control); return true; }
                    sliderTarget = (it.Node, it.Control);
                    sliderRect = it.Rect;
                    sliderBaseX = x;
                    sliderBaseT = ControlScale.ToUnit(info, n.Controls[it.Control]);
                    model.BeginGesture(n.Info.Name + " " + info.Name);
                    drag = Drag.Slider;
                    SliderTo(x, false, jump: true);
                    return true;
                }
                case PK.Chip:
                    explain = (it.Node, it.Control);
                    model.SetControl(it.Node, it.Control, it.Index);
                    return true;
                case PK.Dropdown:
                {
                    explain = (it.Node, it.Control);
                    var n = g.Find(it.Node);
                    if (n == null) return true;
                    var info = n.Info.Controls[it.Control];
                    int cur = (int)(n.Controls[it.Control] + 0.5f);
                    var items = new List<MenuItemModel>();
                    for (int k = 0; k < info.Choices.Length; k++)
                    {
                        int kk = k;
                        items.Add(new MenuItemModel { Label = info.Choices[k], Checked = k == cur, Pick = () => model.SetControl(it.Node, it.Control, kk) });
                    }
                    Menu = new PatchMenu(info.Name, items, info.Choices.Length > 10, it.Rect.X, it.Rect.Bottom + 2);
                    return true;
                }
                case PK.MapChip:
                {
                    var n = g.Find(it.Node);
                    if (n == null) return true;
                    explain = (it.Node, it.Control);
                    var items = new List<MenuItemModel>();
                    var cur = model.MappingOf(it.Node, it.Control);
                    if (cur != null) items.Add(new MenuItemModel { Label = "No macro", Pick = () => model.UnmapControl(it.Node, it.Control) });
                    for (int m = 0; m < 16; m++)
                    {
                        int mm = m;
                        items.Add(new MenuItemModel
                        {
                            Label = "Macro " + (m + 1) + "  " + PatchEditModel.MacroName(g, m), Checked = cur != null && cur.Value.Macro == m,
                            Pick = () => model.MapMacro(mm, it.Node, it.Control),
                        });
                    }
                    Menu = new PatchMenu("Move " + n.Info.Controls[it.Control].Name + " with a macro", items, false, it.Rect.X - 200, it.Rect.Bottom + 2);
                    return true;
                }
                case PK.Scope:
                    foreach (int id in model.Selection.ToArray()) model.SetGlobal(id, it.Index == 1);
                    return true;
                case PK.Button:
                    PanelButton(it.Id);
                    return true;
                case PK.WireAmount when model.SelectedWire != null:
                {
                    sliderRect = it.Rect;
                    model.BeginGesture("Wire amount");
                    drag = Drag.WireAmount;
                    if (clicks >= 2) { model.SetWireAmount(model.SelectedWire.Value, 1f); return true; }
                    WireAmountTo(x);
                    return true;
                }
                case PK.MapRange:
                {
                    var m = g.Macros.Where(q => q.Macro == model.SelectedMacro).ElementAtOrDefault(it.Index);
                    var n = g.Find(m.NodeId);
                    if (n == null) return true;
                    var info = n.Info.Controls[m.Control];
                    sliderRect = it.Rect;
                    mapTarget = (m.NodeId, m.Control);
                    double tLow = ControlScale.ToUnit(info, m.Min), tHigh = ControlScale.ToUnit(info, m.Max);
                    double t = (x - it.Rect.X) / it.Rect.Width;
                    drag = Math.Abs(t - tLow) <= Math.Abs(t - tHigh) ? Drag.MapLow : Drag.MapHigh;
                    model.BeginGesture("Macro range");
                    MappingTo(x);
                    return true;
                }
                case PK.MapRemove:
                {
                    var m = g.Macros.Where(q => q.Macro == model.SelectedMacro).ElementAtOrDefault(it.Index);
                    model.UnmapControl(m.NodeId, m.Control);
                    return true;
                }
            }
            return true;
        }

        void PanelButton(string id)
        {
            switch (id)
            {
                case "duplicate": model.Duplicate(); break;
                case "delete": model.DeleteSelection(); break;
                case "deletewire": model.DeleteSelection(); break;
                case "rename": OpenRename(); break;
                case "renamemacro": OpenMacroRename(model.SelectedMacro); break;
                case "starter": AddStarter(); break;
            }
        }

        /// <summary>The empty patch's one click: Keys, Osc, Envelope, VCA and Out, wired as a basic voice (sound in three steps: play, shape, hear).</summary>
        public void AddStarter()
        {
            model.BeginGesture("Add a basic voice");
            try
            {
                var keys = model.AddNode(NodeKind.Keys, 0, 40);
                var osc = model.AddNode(NodeKind.Osc, 208, 0);
                var env = model.AddNode(NodeKind.Envelope, 208, 128);
                var vca = model.AddNode(NodeKind.Vca, 416, 48);
                var o = model.AddNode(NodeKind.Out, 624, 32);
                if (keys == null || osc == null || env == null || vca == null || o == null) return;
                model.Connect(keys.Id, 0, osc.Id, NodeInfo.OscInPitch);
                model.Connect(keys.Id, 1, env.Id, NodeInfo.EnvInGate);
                model.Connect(osc.Id, 0, vca.Id, NodeInfo.VcaInAudio);
                model.Connect(env.Id, 0, vca.Id, NodeInfo.VcaInCv);
                model.Connect(vca.Id, 0, o.Id, 0);
                model.ClearSelection();
                model.AutoLayout(NodeHeight, record: false);
            }
            finally { model.EndGesture(); }
            Fit();
        }

        public NeoRect StarterRect
        {
            get
            {
                var c = CanvasRect;
                return new NeoRect(c.X + c.Width / 2 - 120, c.Y + c.Height / 2 + 14, 240, 30);
            }
        }

        void SliderTo(double x, bool fine, bool jump = false)
        {
            var n = model.Graph.Find(sliderTarget.Node);
            if (n == null) return;
            var info = n.Info.Controls[sliderTarget.Control];
            double t;
            if (jump) t = (x - sliderRect.X) / sliderRect.Width;
            else if (fine) t = sliderBaseT + (x - sliderBaseX) / (sliderRect.Width * 8);
            else t = (x - sliderRect.X) / sliderRect.Width;
            model.SetControl(sliderTarget.Node, sliderTarget.Control, ControlScale.FromUnit(info, t));
        }

        void WireAmountTo(double x)
        {
            if (model.SelectedWire == null) return;
            double t = Math.Clamp((x - sliderRect.X) / sliderRect.Width, 0, 1);
            double v = Math.Round((-2 + 4 * t) * 20) / 20;
            model.SetWireAmount(model.SelectedWire.Value, (float)v);
        }

        void MappingTo(double x)
        {
            var existing = model.MappingOf(mapTarget.Node, mapTarget.Control);
            var n = model.Graph.Find(mapTarget.Node);
            if (existing == null || n == null) return;
            var info = n.Info.Controls[mapTarget.Control];
            float v = ControlScale.FromUnit(info, (x - sliderRect.X) / sliderRect.Width);
            var m = existing.Value;
            if (drag == Drag.MapLow) model.SetMapping(mapTarget.Node, mapTarget.Control, v, m.Max);
            else model.SetMapping(mapTarget.Node, mapTarget.Control, m.Min, v);
        }

        void HoverPanel(double x, double y)
        {
            PanelPass(null);
            hoverHit = panelItems.LastOrDefault(i => i.Rect.Contains(x, y));
            if (hoverHit != null && hoverHit.Control >= 0 && hoverHit.Node > 0 && hoverHit.Kind != PK.MapRange && hoverHit.Kind != PK.MapRemove) explain = (hoverHit.Node, hoverHit.Control);
        }

        // ------------------------------------------------------------------ side panel: layout and drawing (one pass)

        static IEnumerable<string> Wrap(INeoTextMeasure m, string text, double size, double width, bool bold = false)
        {
            var line = "";
            foreach (var word in (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && m.MeasureText(t, size, bold) > width) { yield return line; line = word; }
                else line = t;
            }
            if (line.Length > 0) yield return line;
        }

        void Text(INeoCanvas cv, string s, double x, double y, double size, NeoColor c, bool bold = false, NeoAlign align = NeoAlign.Left, double max = 0)
        {
            cv?.Text(s, x, y, size, c, bold, align, max);
        }

        /// <summary>Lays the panel out; draws it when a canvas is given; leaves <see cref="PanelItems"/> filled.</summary>
        void PanelPass(INeoCanvas cv)
        {
            panelItems.Clear();
            var P = PanelRect;
            double x0 = P.X + 12, wi = P.Width - 24, y = P.Y + 10 - panelScroll;
            const double explainH = 124;
            double limit = P.Bottom - explainH - 6;
            var g = model.Graph;
            cv?.Rect(P, Palette.Panel, NeoStroke.None);
            cv?.Line(P.X, P.Y, P.X, P.Bottom, new NeoStroke(Palette.PanelEdge, 1));
            // A selected node or nodes: Duplicate and Delete stay on a bar above the Explain box, so a long list of controls scrolls above them and never under.
            bool bar = model.SelectedMacro < 0 && model.SelectedWire == null && model.Selection.Count >= 1;
            double barH = bar ? 34 : 0;
            cv?.PushClip(new NeoRect(P.X, P.Y, P.Width, limit - barH - P.Y));

            void Btn(string id, string label, double bx, double bw, bool danger = false)
            {
                var r = new NeoRect(bx, y, bw, 24);
                panelItems.Add(new PItem { Kind = PK.Button, Rect = r, Id = id });
                if (cv == null) return;
                bool hot = hoverHit != null && hoverHit.Kind == PK.Button && hoverHit.Id == id;
                cv.Rect(r, hot ? Tokens.SurfaceBorder : Tokens.SurfaceControl, new NeoStroke(Tokens.SurfaceBorder, 1), 4);
                cv.Text(label, r.X + r.Width / 2, r.Y + 5, Tokens.FontCaption + 1, danger ? Tokens.Error : Palette.Text, true, NeoAlign.Center);
            }

            if (model.SelectedMacro >= 0) MacroPanel(cv, ref y, x0, wi, Btn);
            else if (model.SelectedWire != null) WirePanel(cv, ref y, x0, wi, Btn);
            else if (model.Selection.Count == 1) NodePanel(cv, g.Find(model.Selection.First()), ref y, x0, wi, Btn);
            else if (model.Selection.Count > 1)
            {
                Text(cv, model.Selection.Count + " nodes selected", x0, y, Tokens.FontTitle, Palette.Text, true);
                y += 30;
                foreach (string line in Wrap(cv ?? (INeoTextMeasure)Measure, "Drag one to move them all. Copy and paste, duplicate or delete them together. Click one to edit its controls.", 11, wi))
                {
                    Text(cv, line, x0, y, 11, Palette.DimText);
                    y += 15;
                }
            }
            else PatchPanel(cv, ref y, x0, wi, Btn);
            cv?.PopClip();
            panelContentBottom = y + panelScroll;
            if (panelScroll > 0 && panelContentBottom < limit - barH) panelScroll = Math.Max(0, panelScroll - (limit - barH - panelContentBottom));
            double viewH = limit - barH - P.Y, contentH = panelContentBottom - P.Y + 6;
            if (cv != null && contentH > viewH + 1)
            {
                double th = Math.Max(24, viewH * viewH / contentH);
                double ty = P.Y + (viewH - th) * (panelScroll / Math.Max(1, contentH - viewH));
                cv.Rect(new NeoRect(P.Right - 5, ty, 3, th), Tokens.SurfaceBorder, NeoStroke.None, 1.5);
            }
            // What is scrolled out of the visible part can't be clicked either.
            panelItems.RemoveAll(i => i.Rect.Y + i.Rect.Height / 2 > limit - barH || i.Rect.Y + i.Rect.Height / 2 < P.Y);
            if (bar)
            {
                y = limit - 30;
                if (cv != null) cv.Line(P.X + 8, y - 5, P.Right - 8, y - 5, new NeoStroke(Palette.PanelEdge, 1));
                Btn("duplicate", "Duplicate", x0, (wi - 6) / 2);
                Btn("delete", "Delete", x0 + (wi + 6) / 2, (wi - 6) / 2, true);
            }
            ExplainBox(cv, new NeoRect(P.X + 8, P.Bottom - explainH, P.Width - 16, explainH - 8));
        }

        double panelContentBottom;

        sealed class NullMeasure : INeoTextMeasure
        {
            public double MeasureText(string text, double size, bool bold = false) => (text ?? "").Length * size * 0.55;
        }
        static readonly NullMeasure Measure = new NullMeasure();

        void PatchPanel(INeoCanvas cv, ref double y, double x0, double wi, BtnFn Btn)
        {
            var g = model.Graph;
            Text(cv, "Patch", x0, y, Tokens.FontTitle, Palette.Text, true);
            y += 28;
            Text(cv, string.IsNullOrEmpty(g.Name) ? "Untitled patch" : g.Name, x0, y, Tokens.FontBody, accent, true, max: wi);
            y += 22;
            Text(cv, g.Nodes.Count + " of " + NodeInfo.Cap + " nodes · " + g.Wires.Count + " wires · " + host.Voices + " voices", x0, y, 11, Palette.DimText, max: wi);
            y += 24;
            string hint = g.Nodes.Count == 0
                ? "An empty patch makes no sound. Right-click the canvas to add a node, or start from a basic voice: Keys, Osc, Envelope, VCA and Out."
                : "Click a node to edit its controls here. Right-click for the node menu. Drag from a dot to wire it to another; a wire into a Feedback node is the only way to loop. Press Learn to hear what each wire carries.";
            foreach (string line in Wrap(cv ?? (INeoTextMeasure)Measure, hint, 11, wi))
            {
                Text(cv, line, x0, y, 11, Palette.DimText);
                y += 15;
            }
            y += 8;
            Btn("rename", "Name the patch…", x0, wi);
            y += 30;
            if (g.Nodes.Count == 0) { Btn("starter", "Add a basic voice", x0, wi); y += 30; }
        }

        void NodePanel(INeoCanvas cv, Node n, ref double y, double x0, double wi, BtnFn Btn)
        {
            if (n == null) return;
            var info = n.Info;
            var doc = NodeHelp.For(n.Kind);
            var col = GroupColour(info.Group);
            cv?.Rect(new NeoRect(x0 - 6, y + 2, 3, 20), col, NeoStroke.None, 1.5);
            Text(cv, info.Name, x0 + 2, y, Tokens.FontTitle, Palette.Text, true);
            Text(cv, "#" + n.Id + " · " + info.Group, x0 + wi, y + 3, 11, Palette.DimText, align: NeoAlign.Right);
            y += 28;
            int lines = 0;
            foreach (string line in Wrap(cv ?? (INeoTextMeasure)Measure, doc.Summary, 11, wi))
            {
                if (lines++ < 3) { Text(cv, line, x0, y, 11, Palette.DimText); y += 15; }
            }
            y += Math.Max(0, 3 - lines) * 15 + 6;

            bool heard = model.IsHeard(n.Id), glob = model.IsGlobal(n.Id);
            if (!info.ForcedGlobal && !info.ForcedPerVoice)
            {
                Text(cv, "Runs", x0, y + 4, 11, Palette.DimText);
                for (int k = 0; k < 2; k++)
                {
                    var r = new NeoRect(x0 + 46 + k * 110, y, 106, 22);
                    panelItems.Add(new PItem { Kind = PK.Scope, Rect = r, Index = k, Node = n.Id });
                    bool on = (k == 1) == n.Global;
                    if (cv != null)
                    {
                        cv.Rect(r, on ? col.WithAlpha(0x50) : Tokens.SurfaceControl, new NeoStroke(on ? col : Tokens.SurfaceBorder, 1), 4);
                        cv.Text(k == 0 ? "for each voice" : "once (global)", r.X + r.Width / 2, r.Y + 4, 11, Palette.Text, on, NeoAlign.Center);
                    }
                }
                y += 28;
            }
            else
            {
                Text(cv, info.ForcedGlobal ? "Runs once for the whole patch" : "Runs for each voice", x0, y, 11, Palette.DimText);
                y += 22;
            }
            if (!heard)
            {
                Text(cv, "Not heard: nothing wired from it leads to an Out.", x0, y, 11, Tokens.Warning, max: wi);
                y += 20;
            }

            for (int c = 0; c < info.Controls.Length; c++)
            {
                var ci = info.Controls[c];
                var map = model.MappingOf(n.Id, c);
                bool hot = explain == (n.Id, c);
                Text(cv, ci.Name, x0, y, 11, hot ? Palette.Text : Palette.DimText, hot);
                string val = ControlScale.Format(ci, n.Controls[c]);
                if (ci.Choices != null)
                {
                    if (ci.Choices.Length <= 4)
                    {
                        double cw = (wi - 60) / ci.Choices.Length;
                        for (int k = 0; k < ci.Choices.Length; k++)
                        {
                            var r = new NeoRect(x0 + 60 + k * cw, y - 2, cw - 3, 22);
                            panelItems.Add(new PItem { Kind = PK.Chip, Rect = r, Node = n.Id, Control = c, Index = k });
                            bool on = (int)(n.Controls[c] + 0.5f) == k;
                            if (cv != null)
                            {
                                cv.Rect(r, on ? col.WithAlpha(0x50) : Tokens.SurfaceControl, new NeoStroke(on ? col : Tokens.SurfaceBorder, 1), 4);
                                cv.Text(ci.Choices[k], r.X + r.Width / 2, r.Y + 4, 11, Palette.Text, on, NeoAlign.Center, r.Width - 4);
                            }
                        }
                    }
                    else
                    {
                        var r = new NeoRect(x0 + 60, y - 2, wi - 60, 22);
                        panelItems.Add(new PItem { Kind = PK.Dropdown, Rect = r, Node = n.Id, Control = c });
                        if (cv != null)
                        {
                            cv.Rect(r, Tokens.SurfaceControl, new NeoStroke(Tokens.SurfaceBorder, 1), 4);
                            cv.Text(val, r.X + 8, r.Y + 4, 11, Palette.Text, maxWidth: r.Width - 28);
                            cv.Text("▾", r.Right - 8, r.Y + 4, 11, Palette.DimText, align: NeoAlign.Right);
                        }
                    }
                    y += 30;
                    continue;
                }
                var chip = new NeoRect(x0 + wi - 26, y - 1, 26, 15);
                panelItems.Add(new PItem { Kind = PK.MapChip, Rect = chip, Node = n.Id, Control = c });
                if (cv != null)
                {
                    cv.Rect(chip, map != null ? accent.WithAlpha(0x60) : NeoColor.FromArgb(0, 0, 0, 0), new NeoStroke(map != null ? accent : Tokens.SurfaceBorder, 1), 3);
                    cv.Text(map != null ? "M" + (map.Value.Macro + 1) : "M", chip.X + chip.Width / 2, chip.Y + 1, 10, map != null ? Palette.Text : Palette.DimText, map != null, NeoAlign.Center);
                    cv.Text(val, chip.X - 6, y, 11, accent, true, NeoAlign.Right);
                }
                var bar = new NeoRect(x0, y + 17, wi, 8);
                panelItems.Add(new PItem { Kind = PK.Slider, Rect = new NeoRect(bar.X, bar.Y - 4, bar.Width, 16), Node = n.Id, Control = c, Disabled = map != null });
                if (cv != null)
                {
                    double t = ControlScale.ToUnit(ci, n.Controls[c]);
                    var track = new NeoRect(bar.X, bar.Y + 1, bar.Width, 6);
                    cv.Rect(track, Tokens.SurfaceBorder, NeoStroke.None, 3);
                    var fillCol = map != null ? accent : col;
                    cv.Rect(new NeoRect(track.X, track.Y, Math.Max(2, track.Width * t), track.Height), fillCol.WithAlpha(map != null ? (byte)0xA0 : (byte)0xFF), NeoStroke.None, 3);
                    cv.Ellipse(new NeoPoint(track.X + track.Width * t, track.Y + 3), 6, 6, Palette.Text, new NeoStroke(Palette.Background, 1.5));
                }
                y += 34;
            }
            _ = glob;
        }

        void WirePanel(INeoCanvas cv, ref double y, double x0, double wi, BtnFn Btn)
        {
            var wk = model.SelectedWire.Value;
            var g = model.Graph;
            var from = g.Find(wk.FromNode);
            var to = g.Find(wk.ToNode);
            Text(cv, "Wire", x0, y, Tokens.FontTitle, Palette.Text, true);
            y += 28;
            if (from == null || to == null) return;
            var type = from.Info.Outs[wk.FromPort].Type;
            cv?.Ellipse(new NeoPoint(x0 + 4, y + 7), 5, 5, PortColour(type), NeoStroke.None);
            Text(cv, from.Info.Name + " " + from.Id + " · " + from.Info.Outs[wk.FromPort].Name, x0 + 16, y, 12, Palette.Text, true, max: wi - 16);
            y += 18;
            Text(cv, "to " + to.Info.Name + " " + to.Id + " · " + to.Info.Ins[wk.ToPort].Name, x0 + 16, y, 12, Palette.Text, true, max: wi - 16);
            y += 24;
            foreach (string line in Wrap(cv ?? (INeoTextMeasure)Measure, NodeHelp.TypeWords(type), 11, wi))
            {
                Text(cv, line, x0, y, 11, Palette.DimText);
                y += 15;
            }
            y += 8;
            var wire = g.Wires.FirstOrDefault(q => WireKey.Of(q).Equals(wk));
            Text(cv, "Amount", x0, y, 11, Palette.DimText);
            Text(cv, "×" + wire.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), x0 + wi, y, 11, accent, true, NeoAlign.Right);
            var bar = new NeoRect(x0, y + 17, wi, 8);
            panelItems.Add(new PItem { Kind = PK.WireAmount, Rect = new NeoRect(bar.X, bar.Y - 4, bar.Width, 16) });
            if (cv != null)
            {
                var track = new NeoRect(bar.X, bar.Y + 1, bar.Width, 6);
                cv.Rect(track, Tokens.SurfaceBorder, NeoStroke.None, 3);
                double mid = track.X + track.Width / 2, px = track.X + track.Width * (wire.Amount + 2) / 4;
                cv.Rect(new NeoRect(Math.Min(mid, px), track.Y, Math.Abs(px - mid), track.Height), PortColour(type), NeoStroke.None, 3);
                cv.Ellipse(new NeoPoint(px, track.Y + 3), 6, 6, Palette.Text, new NeoStroke(Palette.Background, 1.5));
            }
            y += 38;
            foreach (string line in Wrap(cv ?? (INeoTextMeasure)Measure, "The amount scales the signal where the wire arrives; negative flips it. Double-click the slider to put it back to 1.", 11, wi))
            {
                Text(cv, line, x0, y, 11, Palette.DimText);
                y += 15;
            }
            y += 10;
            Btn("deletewire", "Delete the wire", x0, wi, true);
            y += 30;
        }

        void MacroPanel(INeoCanvas cv, ref double y, double x0, double wi, BtnFn Btn)
        {
            int m = model.SelectedMacro;
            var g = model.Graph;
            Text(cv, "Macro " + (m + 1), x0, y, Tokens.FontTitle, Palette.Text, true);
            Text(cv, Math.Round(host.Macro(m) * 100) + " %", x0 + wi, y + 3, 11, accent, true, NeoAlign.Right);
            y += 26;
            Text(cv, PatchEditModel.MacroName(g, m), x0, y, Tokens.FontBody, accent, true, max: wi);
            y += 24;
            var maps = g.Macros.Where(q => q.Macro == m).ToList();
            if (maps.Count == 0)
            {
                foreach (string line in Wrap(cv ?? (INeoTextMeasure)Measure, "Nothing is mapped to this macro. Select a node and press M beside one of its controls to move that control with this macro.", 11, wi))
                {
                    Text(cv, line, x0, y, 11, Palette.DimText);
                    y += 15;
                }
                y += 8;
            }
            for (int i = 0; i < maps.Count; i++)
            {
                var mp = maps[i];
                var n = g.Find(mp.NodeId);
                if (n == null) continue;
                var ci = n.Info.Controls[mp.Control];
                Text(cv, n.Info.Name + " " + n.Id + " · " + ci.Name, x0, y, 11, Palette.Text, true, max: wi - 24);
                var rm = new NeoRect(x0 + wi - 18, y - 1, 18, 16);
                panelItems.Add(new PItem { Kind = PK.MapRemove, Rect = rm, Index = i });
                if (cv != null) { cv.Rect(rm, Tokens.SurfaceControl, NeoStroke.None, 3); cv.Text("×", rm.X + 9, rm.Y, 12, Palette.DimText, align: NeoAlign.Center); }
                var bar = new NeoRect(x0, y + 22, wi, 8);
                panelItems.Add(new PItem { Kind = PK.MapRange, Rect = new NeoRect(bar.X, bar.Y - 4, bar.Width, 16), Index = i });
                if (cv != null)
                {
                    var track = new NeoRect(bar.X, bar.Y + 1, bar.Width, 6);
                    cv.Rect(track, Tokens.SurfaceBorder, NeoStroke.None, 3);
                    double a = track.X + track.Width * ControlScale.ToUnit(ci, mp.Min), b = track.X + track.Width * ControlScale.ToUnit(ci, mp.Max);
                    cv.Rect(new NeoRect(Math.Min(a, b), track.Y, Math.Max(2, Math.Abs(b - a)), track.Height), accent, NeoStroke.None, 3);
                    cv.Ellipse(new NeoPoint(a, track.Y + 3), 5, 5, Palette.Text, new NeoStroke(Palette.Background, 1.5));
                    cv.Ellipse(new NeoPoint(b, track.Y + 3), 5, 5, Palette.Text, new NeoStroke(Palette.Background, 1.5));
                    cv.Text(ControlScale.Format(ci, mp.Min) + " to " + ControlScale.Format(ci, mp.Max), x0, y + 36, 10, Palette.DimText, maxWidth: wi);
                }
                y += 56;
            }
            Btn("renamemacro", "Name the macro…", x0, wi);
            y += 30;
        }

        void ExplainBox(INeoCanvas cv, NeoRect r)
        {
            if (cv == null) return;
            cv.Rect(r, Tokens.SurfaceBase, new NeoStroke(Palette.PanelEdge, 1), 6);
            double x = r.X + 10, wi = r.Width - 20;
            var n = model.Graph.Find(explain.Node);
            if (n == null || explain.Control < 0 || explain.Control >= n.Controls.Length)
            {
                cv.Text("EXPLAIN", x, r.Y + 8, 10, accent, true);
                int k = 0;
                foreach (string line in Wrap(cv, "Touch a control to see what it does and what you will hear.", 11, wi))
                    cv.Text(line, x, r.Y + 26 + 15 * k++, 11, Palette.DimText);
                return;
            }
            var ci = n.Info.Controls[explain.Control];
            var doc = NodeHelp.For(n.Kind);
            string title = n.Info.Name + " · " + ci.Name;
            cv.Text(title, x, r.Y + 7, 12, Palette.Text, true, maxWidth: wi * 0.6);
            cv.Text(ControlScale.Format(ci, n.Controls[explain.Control]), r.Right - 10, r.Y + 7, 12, accent, true, NeoAlign.Right);
            double y = r.Y + 26;
            var map = model.MappingOf(n.Id, explain.Control);
            void Block(string head, string text, NeoColor col, int maxLines)
            {
                int k = 0;
                foreach (string line in Wrap(cv, head + "  " + text, 11, wi))
                {
                    if (k++ >= maxLines) break;
                    cv.Text(line, x, y, 11, col);
                    y += 14;
                }
                y += 3;
            }
            if (map != null) Block("Driven", "by Macro " + (map.Value.Macro + 1) + ", so the slider follows it.", accent, 1);
            Block("How", doc.How[explain.Control], Palette.Text, 3);
            Block("Sound", doc.Sound[explain.Control], Palette.DimText, 3);
        }

        public ControlExplain ExplainNow()
        {
            var n = model.Graph.Find(explain.Node);
            if (n == null || explain.Control < 0 || explain.Control >= n.Controls.Length) return null;
            var doc = NodeHelp.For(n.Kind);
            return new ControlExplain(n.Info.Name + " · " + n.Info.Controls[explain.Control].Name, doc.How[explain.Control], doc.Sound[explain.Control]);
        }

        public sealed record ControlExplain(string Title, string How, string Sound);
    }
}
