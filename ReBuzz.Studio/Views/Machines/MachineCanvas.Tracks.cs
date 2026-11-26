// Fork (docs/specs/ux-overhaul.md §11, MT1): the Machines view's Tracks mode (option 2A). The free layout stays; the track model
// (ReBuzz.Core.Tracks) shows as colour: a box's border and the wires leaving it take its track's colour (a shared machine shows its
// members' colours as a stripe along its top; a return has a dashed border; a send is a dashed wire; what is unrouted is dashed grey),
// and one track at a time can be lit, the rest dimmed to about 35 %. Your boxes never move: nothing here writes a position.
// What is lit comes from, in this order: a chip the pointer is on, a chip that has the keyboard focus, the chip that was clicked (pinned).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Tracks;

namespace ReBuzz.Studio.Views.Machines
{
    /// <summary>What a chip stands for: an entry of the model by its head machine, or the unrouted machines.</summary>
    internal readonly record struct TrackTarget(IMachine Head, bool Unrouted)
    {
        public static TrackTarget Entry(IMachine head) => new TrackTarget(head, false);
        public static readonly TrackTarget AllUnrouted = new TrackTarget(null, true);
    }

    /// <summary>How one machine is painted in the Tracks mode.</summary>
    internal sealed class TrackPaint
    {
        public Color Colour;
        public IBrush Brush;
        public IPen Border;
        public Color[] Stripe = Array.Empty<Color>();
        public bool Routed;
        public TrackNode Node;
    }

    internal sealed partial class MachineCanvas
    {
        /// <summary>A machine that is not lit is drawn at this opacity while a track is lit.</summary>
        public const double DimOpacity = 0.35;

        bool tracksOn;
        MachineTracksLive live;
        bool liveHeld, shown;
        TrackTarget? hoverLit, focusLit, pinnedLit;
        Dictionary<IMachine, TrackPaint> paints;
        Dictionary<IMachine, TrackNode> nodes;
        TrackModel paintModel;
        HashSet<IMachine> litSet;
        TrackTarget? paintLit;
        bool paintLight;
        readonly Dictionary<(uint, bool), IPen> borderPens = new();
        readonly Dictionary<(uint, bool), IPen> wirePens = new();

        /// <summary>The Tracks mode: boxes and wires in their track's colour. Off, the canvas draws as it always did.</summary>
        public bool TracksOn
        {
            get => tracksOn;
            set
            {
                if (tracksOn == value) return;
                tracksOn = value;
                if (!value) { hoverLit = focusLit = pinnedLit = null; paints = null; litSet = null; }
                HoldLive();
                InvalidateVisual();
                LitChanged?.Invoke();
            }
        }

        /// <summary>The model the paint follows (null while the mode is off).</summary>
        public TrackModel TrackModel => tracksOn ? Live.Model : null;

        MachineTracksLive Live => live ??= MachineTracksLive.For(core);

        /// <summary>What is lit changed (the card follows).</summary>
        public event Action LitChanged;

        /// <summary>The model changed (a machine, a wire or a name): the chips are made again.</summary>
        public event Action TracksChanged;

        /// <summary>The meters were taken (the card's meter follows).</summary>
        public event Action LevelsTicked;

        void HoldLive(bool? attached = null)
        {
            if (attached != null) shown = attached.Value;
            bool want = tracksOn && shown;
            if (want == liveHeld) return;
            liveHeld = want;
            if (want) { Live.Retain(); Live.Changed += OnTracksChanged; if (core.TrackColours != null) core.TrackColours.Changed += OnTrackColoursChanged; }
            else { Live.Changed -= OnTracksChanged; if (core.TrackColours != null) core.TrackColours.Changed -= OnTrackColoursChanged; Live.Release(); }
        }

        // MX1: a track's colour changed (a person, an agent, undo, a song opening): the chips and wires draw it again.
        void OnTrackColoursChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(OnTracksChanged);

        void OnTracksChanged()
        {
            paints = null;
            InvalidateVisual();
            TracksChanged?.Invoke();
            LitChanged?.Invoke();
        }

        /// <summary>
        /// NW1 (note-wires.md §11): in the Tracks mode a note insert is drawn left of its instrument, in chain order (the last one nearest it), one box and a gap apart,
        /// on the instrument's row. Drawn only: no stored position is written. Null for a machine that is in no chain.
        /// </summary>
        Point? TracksInsertSpot(IMachine m)
        {
            var wires = core.NoteWires;
            if (wires == null) return null;
            string instrument = wires.State.InstrumentOf(m.Name);
            if (instrument == null) return null;
            var owner = Machines.FirstOrDefault(x => x.Name == instrument);
            if (owner == null) return null;
            var chain = wires.State.ChainOf(instrument);
            int index = -1;
            for (int i = 0; i < chain.Count; i++) if (chain[i] == m.Name) index = i;
            if (index < 0) return null;
            var o = CanvasMap.Clamp(new Point(owner.Position.Item1, owner.Position.Item2));
            double step = Math.Abs(map.PositionDelta(new Vector(CanvasMap.BoxWidth + 24, 0)).X);
            return CanvasMap.Clamp(new Point(ReBuzz.Core.NoteWires.NoteInsertLayout.X((float)o.X, index, chain.Count, (float)step), o.Y));
        }

        /// <summary>The target lit now, or null when nothing is.</summary>
        public TrackTarget? Lit => hoverLit ?? focusLit ?? pinnedLit;

        public TrackTarget? Pinned => pinnedLit;

        public void SetHoverLit(TrackTarget? target) => SetLit(ref hoverLit, target);

        public void SetFocusLit(TrackTarget? target) => SetLit(ref focusLit, target);

        /// <summary>A click on a chip: the track stays lit until another is, or the same chip is clicked again, or Esc.</summary>
        public void Pin(TrackTarget? target) => SetLit(ref pinnedLit, target);

        void SetLit(ref TrackTarget? slot, TrackTarget? target)
        {
            if (Equals(slot, target)) return;
            slot = target;
            InvalidateVisual();
            LitChanged?.Invoke();
        }

        /// <summary>The entry that is lit (a track, a group or a return), or null (nothing, or the unrouted).</summary>
        public TrackEntry LitEntry
        {
            get
            {
                if (!tracksOn || Lit is not { } t || t.Unrouted || t.Head == null) return null;
                var model = Live.Model;
                return model.EntryWithHead(model.Graph.Nodes.FirstOrDefault(n => n.Tag == t.Head));
            }
        }

        /// <summary>The machines lit now (null: nothing is, so nothing is dimmed).</summary>
        public IReadOnlyCollection<IMachine> LitMachines
        {
            get
            {
                if (!tracksOn) return null;
                Prepare();
                return litSet;
            }
        }

        /// <summary>Whether a machine is drawn dimmed now (a track is lit and the machine is not on it).</summary>
        public bool IsDimmed(IMachine m)
        {
            if (!tracksOn) return false;
            Prepare();
            return litSet != null && !litSet.Contains(m);
        }

        /// <summary>The paint of a machine in the Tracks mode (tests, the bar), or null.</summary>
        public TrackPaint PaintOf(IMachine m)
        {
            if (!tracksOn) return null;
            Prepare();
            return paints != null && paints.TryGetValue(m, out var p) ? p : null;
        }

        /// <summary>A track's colour: its generator's (a group's or a return's: its machine's), from the machine's identity.</summary>
        public static Color ColourOfHead(IMachine head) => Mixer.TrackColour.Of(head) ?? Shell.Design.StudioTokens.TextSecondary;

        /// <summary>Selecting a machine off the pinned track (or nothing) lets go of the pin; one on it keeps it.</summary>
        void Unpin(IMachine selected)
        {
            if (pinnedLit is not { } p) return;
            if (selected != null)
            {
                Prepare();
                if (litSetOf(p) is { } set && set.Contains(selected)) return;
            }
            SetLit(ref pinnedLit, null);
        }

        HashSet<IMachine> litSetOf(TrackTarget t) => paintModel == null ? null : BuildLit(paintModel, t);

        // ---- the paint, rebuilt when the model, the theme or what is lit changed

        void Prepare()
        {
            var model = Live.Model;
            bool light = NeoBuzz.Editor.Tokens.IsLight;
            var lit = Lit;
            if (paints != null && ReferenceEquals(model, paintModel) && light == paintLight && Equals(lit, paintLit)) return;
            Rebuild(model, light, lit);
        }

        void Rebuild(TrackModel model, bool light, TrackTarget? lit)
        {
            paintModel = model;
            paintLight = light;
            paintLit = lit;
            nodes = new Dictionary<IMachine, TrackNode>(model.Graph.Nodes.Count);
            foreach (var n in model.Graph.Nodes) if (n.Tag is IMachine m) nodes[m] = n;
            paints = new Dictionary<IMachine, TrackPaint>(nodes.Count);
            foreach (var e in model.Entries)
            {
                var head = (IMachine)e.Head.Tag;
                var colour = ColourOfHead(head);
                bool dashed = e.Kind == TrackKind.Return;
                foreach (var node in e.Machines)
                {
                    var paint = Make(colour, dashed, true);
                    paint.Node = node;
                    if (node == e.Head && e.Members.Count > 0)
                        paint.Stripe = e.Members.Select(x => ColourOfHead((IMachine)x.Head.Tag)).ToArray();
                    paints[(IMachine)node.Tag] = paint;
                }
            }
            foreach (var u in model.Unrouted)
            {
                var paint = Make(Shell.Design.StudioTokens.TextSecondary, true, false);
                paint.Node = u.Node;
                paints[(IMachine)u.Node.Tag] = paint;
            }
            litSet = lit is { } t ? BuildLit(model, t) : null;
        }

        TrackPaint Make(Color colour, bool dashed, bool routed) => new TrackPaint
        {
            Colour = colour,
            Brush = new SolidColorBrush(colour).ToImmutable(),
            Border = BorderPen(colour, dashed),
            Routed = routed,
        };

        IPen BorderPen(Color colour, bool dashed)
        {
            var key = (colour.ToUInt32(), dashed);
            if (!borderPens.TryGetValue(key, out var pen))
                borderPens[key] = pen = new Pen(new SolidColorBrush(colour).ToImmutable(), 2, dashed ? new DashStyle(new double[] { 3, 2 }, 0) : null).ToImmutable();
            return pen;
        }

        /// <summary>The line of a wire in a track's colour (a send is dashed).</summary>
        IPen WirePen(Color colour, bool dashed)
        {
            var key = (colour.ToUInt32(), dashed);
            if (!wirePens.TryGetValue(key, out var pen))
                wirePens[key] = pen = new Pen(new SolidColorBrush(colour).ToImmutable(), 2, dashed ? new DashStyle(new double[] { 4, 3 }, 0) : null).ToImmutable();
            return pen;
        }

        static HashSet<IMachine> BuildLit(TrackModel model, TrackTarget t)
        {
            var set = new HashSet<IMachine>();
            if (t.Unrouted)
            {
                foreach (var u in model.Unrouted) set.Add((IMachine)u.Node.Tag);
                return set;
            }
            var node = model.Graph.Nodes.FirstOrDefault(n => n.Tag == t.Head);
            var entry = model.EntryWithHead(node);
            if (entry == null) return null;
            foreach (var n in model.PathOf(entry)) set.Add((IMachine)n.Tag);
            return set;
        }

        // ---- what the drawing asks

        /// <summary>The colour a wire is drawn in and whether it is a send; null for a wire that keeps the canvas's own.</summary>
        (Color Colour, bool Send)? WireLook(IMachineConnection c)
        {
            if (paints == null || !paints.TryGetValue(c.Source, out var from) || !from.Routed) return null;
            bool send = paints.TryGetValue(c.Destination, out var to) && paintModel.IsSend(from.Node, to.Node);
            return (from.Colour, send);
        }

        bool WireDimmed(IMachineConnection c) => litSet != null && !(litSet.Contains(c.Source) && litSet.Contains(c.Destination));

        /// <summary>The stripe of a folded group: the track colours of the machines inside it.</summary>
        Color[] FoldedStripe(IMachineGroup g)
        {
            if (paints == null) return Array.Empty<Color>();
            var list = new List<Color>();
            foreach (var m in g.Machines)
                if (paints.TryGetValue(m, out var p) && p.Routed && !list.Contains(p.Colour)) list.Add(p.Colour);
            return list.ToArray();
        }

        /// <summary>A stripe along the top of a box: the colours in equal parts.</summary>
        static void DrawStripe(DrawingContext ctx, Rect box, Color[] colours, double height)
        {
            if (colours.Length == 0) return;
            double w = (box.Width - 2) / colours.Length;
            for (int i = 0; i < colours.Length; i++)
                ctx.FillRectangle(IdentityBand(colours[i]), new Rect(box.X + 1 + i * w, box.Y + 1, i == colours.Length - 1 ? box.Width - 2 - i * w : w, height));
        }
    }
}
