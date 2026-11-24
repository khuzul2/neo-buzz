using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.NoteWires;

namespace ReBuzz.Studio.Views.Machines
{
    /// <summary>NW1 (docs/specs/note-wires.md §6): the choice "Note wires" in the view's bar (wires hidden, ports stay).</summary>
    internal static class NoteWiresPreference
    {
        public const string Key = "NoteWires", Section = "Studio";

        public static bool On
        {
            get
            {
                try { return Shell.StudioHost.Current?.Registry?.Read(Key, true, Section) ?? true; }
                catch (Exception) { return true; }
            }
            set
            {
                if (On == value) return;
                try { Shell.StudioHost.Current?.Registry?.Write(Key, value, Section); }
                catch (Exception) { /* the choice lasts this session */ }
                Changed?.Invoke(value);
            }
        }

        public static event Action<bool> Changed;
    }

    /// <summary>NW1: note ports (a diamond on the top edge in, on the bottom edge out) and dotted note wires.</summary>
    internal sealed partial class MachineCanvas
    {
        static readonly DashStyle NoteDash = new DashStyle(new double[] { 1.2, 2.6 }, 0);

        bool noteWiresShown = true;
        NoteEdge? selectedNote, hoverNote;
        IMachine noteFrom, noteTo, noteHoverPort;
        string noteWhy;
        Point notePoint;

        /// <summary>Whether the note wires are drawn (the "Note wires" toggle).</summary>
        public bool NoteWiresShown
        {
            get => noteWiresShown;
            set { if (noteWiresShown == value) return; noteWiresShown = value; if (!value) { selectedNote = hoverNote = null; } InvalidateVisual(); }
        }

        void OnNoteWiresPref(bool on) => NoteWiresShown = on;
        void OnNoteWiresChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => { selectedNote = null; Redraw(); });

        /// <summary>The note wire selected now (tests).</summary>
        public (string From, string To)? SelectedNoteWire => selectedNote is { } e ? (e.From, e.To) : null;

        /// <summary>The wires drawn: free wires and chain wires whose two machines are in the song and shown.</summary>
        internal List<NoteEdge> NoteEdges()
        {
            var list = new List<NoteEdge>();
            var store = core.NoteWires;
            if (store == null || store.State.IsEmpty) return list;
            foreach (var e in store.State.AllEdges())
            {
                var a = MachineNamed(e.From); var b = MachineNamed(e.To);
                if (a == null || b == null || IsFolded(a) || IsFolded(b)) continue;
                list.Add(e);
            }
            return list;
        }

        IMachine MachineNamed(string name) => Machines.FirstOrDefault(m => m.Name == name);

        NotePorts PortsFor(IMachine m) => core.NoteWires?.PortsOf(m.Name) ?? default;

        internal bool HasNoteIn(IMachine m) => PortsFor(m) is { In: true, Control: false };
        internal bool HasNoteOut(IMachine m) => PortsFor(m) is { Out: true, Control: false };

        public static Point NoteInPort(Rect box) => new Point(box.Center.X, box.Y);
        public static Point NoteOutPort(Rect box) => new Point(box.Center.X, box.Bottom);

        /// <summary>The note-in port of a machine (logical pixels), for tests.</summary>
        public Point NoteInPortOf(IMachine m) => NoteInPort(BoxOf(m));
        public Point NoteOutPortOf(IMachine m) => NoteOutPort(BoxOf(m));

        IMachine NoteOutAt(Point p) => Machines.Reverse().FirstOrDefault(m => !IsFolded(m) && HasNoteOut(m) && MachineGeometry.Near(NoteOutPort(BoxOf(m)), p));
        IMachine NoteInAt(Point p) => Machines.Reverse().FirstOrDefault(m => !IsFolded(m) && HasNoteIn(m) && MachineGeometry.Near(NoteInPort(BoxOf(m)), p));

        Color NoteColour(IMachine source)
        {
            if (tracksOn && paints != null && paints.TryGetValue(source, out var tp)) return tp.Colour;
            return MachineIdentity.ColourOf(source) ?? Shell.Design.StudioTokens.TextSecondary;
        }

        (Point A, Point B) NoteLine(NoteEdge e)
        {
            var a = MachineNamed(e.From); var b = MachineNamed(e.To);
            return (NoteOutPort(BoxOf(a)), NoteInPort(BoxOf(b)));
        }

        static double DistanceToSegment(Point p, Point a, Point b)
        {
            var d = b - a;
            double len2 = d.X * d.X + d.Y * d.Y;
            double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / len2, 0, 1);
            var q = new Point(a.X + t * d.X, a.Y + t * d.Y);
            return Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
        }

        /// <summary>The note wire under a point (the last drawn on top).</summary>
        internal NoteEdge? NoteWireAt(Point p)
        {
            if (!noteWiresShown) return null;
            NoteEdge? hit = null;
            foreach (var e in NoteEdges())
            {
                var (a, b) = NoteLine(e);
                if (DistanceToSegment(p, a, b) <= 5) hit = e;
            }
            return hit;
        }

        static StreamGeometry Diamond(Point c, double r)
        {
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(c.X, c.Y - r), true);
                s.LineTo(new Point(c.X + r, c.Y));
                s.LineTo(new Point(c.X, c.Y + r));
                s.LineTo(new Point(c.X - r, c.Y));
                s.EndFigure(true);
            }
            return g;
        }

        void DrawNoteWires(DrawingContext ctx)
        {
            if (!noteWiresShown) return;
            var chainEdges = core.NoteWires.State.ChainEdges().ToHashSet();
            foreach (var e in NoteEdges())
            {
                var src = MachineNamed(e.From);
                var colour = NoteColour(src);
                bool hot = hoverNote == e || selectedNote == e;
                var (a, b) = NoteLine(e);
                var brush = new SolidColorBrush(colour);
                var pen = new Pen(brush, hot ? 2.6 : 1.8, NoteDash) { LineCap = PenLineCap.Round };
                bool dim = tracksOn && litSet != null && !litSet.Contains(src);
                using (ctx.PushOpacity(dim ? DimOpacity : 1))
                {
                    ctx.DrawLine(pen, a, b);
                    // The arrowhead: a small note-shaped (diamond) head, pointing down the wire into the target's port.
                    var d = b - a;
                    double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                    if (len > 12)
                    {
                        var u = new Vector(d.X / len, d.Y / len);
                        var n = new Vector(-u.Y, u.X);
                        var tip = b - u * 3;
                        var g = new StreamGeometry();
                        using (var s = g.Open())
                        {
                            s.BeginFigure(tip, true);
                            s.LineTo(tip - u * 9 + n * 4.5);
                            s.LineTo(tip - u * 6);
                            s.LineTo(tip - u * 9 - n * 4.5);
                            s.EndFigure(true);
                        }
                        ctx.DrawGeometry(brush, null, g);
                    }
                    if (chainEdges.Contains(e))
                    {
                        // A chain wire: its number in the track's chain, at the middle.
                        string inst = core.NoteWires.State.InstrumentOf(e.From) ?? e.To;
                        var chain = core.NoteWires.State.ChainOf(core.NoteWires.State.InstrumentOf(e.From) ?? e.From);
                        int idx = chain.ToList().IndexOf(e.From);
                        if (idx >= 0)
                        {
                            var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                            var ft = new FormattedText((idx + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, PlainFace, 10, brush);
                            ctx.DrawText(ft, mid + new Vector(5, -6));
                        }
                    }
                }
                if (hot)
                {
                    var ft = new FormattedText($"{e.From} sends notes to {e.To}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, PlainFace, 12, Shell.Design.StudioTokens.TextPrimaryBrush);
                    var at = new Point((a.X + b.X) / 2 + 8, (a.Y + b.Y) / 2 - ft.Height - 4);
                    ctx.DrawRectangle(Shell.Design.StudioTokens.SurfaceBaseBrush, new Pen(brush, 1), new Rect(at.X - 4, at.Y - 2, ft.Width + 8, ft.Height + 4), 3, 3);
                    ctx.DrawText(ft, at);
                }
            }
            if (drag == Drag.NoteConnect && noteFrom != null)
            {
                var a = NoteOutPort(BoxOf(noteFrom));
                var b = noteTo != null ? NoteInPort(BoxOf(noteTo)) : notePoint;
                using (ctx.PushOpacity(0.6))
                    ctx.DrawLine(new Pen(new SolidColorBrush(NoteColour(noteFrom)), 1.8, NoteDash) { LineCap = PenLineCap.Round }, a, b);
            }
        }

        /// <summary>The ports: drawn always (also when the wires are hidden), only on machines that have them.</summary>
        void DrawNotePorts(DrawingContext ctx, List<IMachine> machines)
        {
            var ring = Shell.Design.StudioTokens.TextPrimaryBrush;
            var bad = Shell.Design.StudioTokens.ErrorTextBrush;
            foreach (var m in machines)
            {
                if (IsFolded(m)) continue;
                var ports = PortsFor(m);
                if (ports.Control) continue;
                var box = BoxOf(m);
                if (ports.In)
                {
                    bool during = drag == Drag.NoteConnect;
                    bool valid = during && m != noteFrom && core.NoteWires.WhyNot(noteFrom.Name, m.Name) == null;
                    bool hot = noteHoverPort == m && !(drag == Drag.NoteConnect) || m == noteTo;
                    bool refusedHere = during && m != noteFrom && !valid && noteHoverPort == m;
                    var fill = new SolidColorBrush(NoteColour(m));
                    double r = hot ? 7 : valid ? 6.5 : 5;
                    ctx.DrawGeometry(during && !valid ? Shell.Design.StudioTokens.SurfaceBaseBrush : fill, new Pen(refusedHere ? bad : ring, hot || valid ? 2 : 1.2), Diamond(NoteInPort(box), r));
                }
                if (ports.Out)
                {
                    bool hot = noteHoverPort == m && drag != Drag.NoteConnect || (drag == Drag.NoteConnect && noteFrom == m);
                    var fill = new SolidColorBrush(NoteColour(m));
                    ctx.DrawGeometry(fill, new Pen(ring, hot ? 2 : 1.2), Diamond(NoteOutPort(box), hot ? 7 : 5));
                }
            }
            if (drag == Drag.NoteConnect && noteWhy != null)
            {
                var ft = new FormattedText(noteWhy, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, PlainFace, 12, bad);
                var at = notePoint + new Vector(12, 14);
                ctx.DrawRectangle(Shell.Design.StudioTokens.SurfaceBaseBrush, new Pen(bad, 1), new Rect(at.X - 4, at.Y - 2, Math.Min(ft.Width, 420) + 8, ft.Height + 4), 3, 3);
                ctx.DrawText(ft, at);
            }
        }

        // ---- pointer

        /// <summary>A press on a note port or note wire; true when it was one (and handled).</summary>
        bool NotePointerPressed(Point p, PointerUpdateKind kind, KeyModifiers mods, PointerPressedEventArgs e)
        {
            if (kind == PointerUpdateKind.LeftButtonPressed && mods == KeyModifiers.None && NoteOutAt(p) is { } from)
            {
                FocusMachine(from);
                drag = Drag.NoteConnect;
                dragButton = MouseButton.Left;
                noteFrom = from; noteTo = null; noteWhy = null; notePoint = p;
                e.Pointer.Capture(this);
                InvalidateVisual();
                return true;
            }
            if (MachineAt(p) != null || NoteInAt(p) != null) return false;
            if (NoteWireAt(p) is { } wire)
            {
                if (kind == PointerUpdateKind.LeftButtonPressed) { selectedNote = wire; Redraw(); return true; }
                if (kind == PointerUpdateKind.RightButtonPressed) { selectedNote = wire; Redraw(); return true; }
            }
            return false;
        }

        void DragNote(Point p)
        {
            notePoint = p;
            var target = NoteInAt(p) ?? MachineAt(p);
            noteTo = null; noteWhy = null; noteHoverPort = target;
            if (target != null && target != noteFrom)
            {
                if (!HasNoteIn(target)) noteWhy = $"{target.Name} doesn't take notes.";
                else
                {
                    noteWhy = core.NoteWires.WhyNot(noteFrom.Name, target.Name);
                    if (noteWhy == null) noteTo = target;
                }
            }
            ToolTip.SetTip(this, noteWhy);
            Cursor = noteTo != null ? UpArrowCursor : Cursor.Default;
            InvalidateVisual();
        }

        void EndNoteDrag(bool commit)
        {
            var from = noteFrom; var to = noteTo; string why = noteWhy;
            noteFrom = noteTo = null; noteWhy = null; noteHoverPort = null;
            ToolTip.SetTip(this, null);
            if (commit && from != null && to != null) core.NoteWires.Connect(from.Name, to.Name);
            else if (commit && from != null && why != null) Say(why, notePoint);
        }

        /// <summary>The hover over a note port or wire (idle pointer); true when it changed something.</summary>
        bool NoteHover(Point p)
        {
            IMachine port = NoteOutAt(p) ?? NoteInAt(p);
            NoteEdge? wire = port == null ? NoteWireAt(p) : null;
            if (port == noteHoverPort && wire == hoverNote) return false;
            noteHoverPort = port; hoverNote = wire;
            return true;
        }

        /// <summary>The note-related tip under the idle pointer, else null.</summary>
        string NoteTip()
        {
            if (noteHoverPort != null)
                return NoteOutAt(lastLogical) == noteHoverPort
                    ? "Notes out of " + noteHoverPort.Name + ": drag to a machine's note port to send it notes (dotted wire)"
                    : "Notes into " + noteHoverPort.Name + ": drop a note wire from another machine's note-out port here";
            if (hoverNote is { } e) return $"{e.From} sends notes to {e.To} (dotted wire: notes, not sound). Select it and press Delete to remove it";
            return null;
        }

        /// <summary>Delete on a selected note wire: a free wire goes; a chain wire takes its insert out of the chain. True when it did.</summary>
        internal bool DeleteSelectedNoteWire()
        {
            if (selectedNote is not { } e) return false;
            selectedNote = null;
            RemoveNoteWire(e);
            return true;
        }

        internal void RemoveNoteWire(NoteEdge e)
        {
            var store = core.NoteWires;
            if (store.State.Wires.Contains(e)) store.Disconnect(e.From, e.To);
            else if (store.State.InstrumentOf(e.From) != null) store.RemoveInsert(e.From);
            else if (store.State.InstrumentOf(e.To) is { } inst && store.State.ChainOf(inst).Contains(e.To)) store.RemoveInsert(e.To);
            Redraw();
        }
    }
}
