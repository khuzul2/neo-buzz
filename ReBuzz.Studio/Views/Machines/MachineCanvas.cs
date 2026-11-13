// Fork (docs/specs/studio-mvp.md S3): the machine view's canvas, code-drawn: machines as boxes
// (the default theme's MachineControl), wires with their arrows (Connection), and the mouse and
// keys of BuzzGUI.MachineView: select, rubber band, move, Shift-drag to connect, drop an effect on a
// wire, the mute LED, the delete button, double click for the GUI, the context menus, the amp
// control on an arrow, zoom and scroll. Edits go through MachineEdits.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Views.Machines
{
    using Control = Avalonia.Controls.Control;

    internal sealed class MachineCanvas : Control
    {
        /// <summary>WPF's Zoomer: 2^(level/8), level -8..8.</summary>
        public const int ZoomLevels = 8;

        readonly ReBuzzCore core;
        readonly HashSet<IMachine> selected = new HashSet<IMachine>();
        readonly List<IMachine> order = new List<IMachine>();        // bottom to top
        readonly Dictionary<IMachine, Point> dragPositions = new Dictionary<IMachine, Point>();
        readonly HashSet<IMachine> watched = new HashSet<IMachine>();
        SongCore song;

        CanvasMap map = new CanvasMap(2, 2);
        int zoomLevel;
        Vector offset;          // scroll offset, in zoomed pixels
        double wheelZoom;       // wheel notches (or pinch) not yet turned into a level

        enum Drag { None, Move, Connect, Select, Pan }
        Drag drag;
        Point dragOrigin, dragLast;             // logical canvas pixels (Pan: view pixels)
        Vector panStart;
        MouseButton dragButton;                 // the button that started the drag ends it
        Vector moveDelta;
        IMachineConnection insertInto;
        IMachine connectFrom, connectTo;
        Point connectPoint;
        Rect band;
        HashSet<IMachine> bandBase = new HashSet<IMachine>();
        IMachine hover;
        bool hoverDelete;
        IMachine hoverPort, connectRefused;     // P3d: the machine whose port dot the pointer is on; the invalid target of a connect drag
        string connectWhy;

        public MachineCanvas(ReBuzzCore core)
        {
            this.core = core;
            Focusable = true;
            ClipToBounds = true;
            PointerTouchPadGestureMagnify += OnPinch;
        }

        /// <summary>Raised when zoom or scroll change (the view's scroll bars and slider follow).</summary>
        public event Action ViewChanged;

        /// <summary>The menu a right click opened (tests use it).</summary>
        public ContextMenu OpenMenu { get; private set; }

        /// <summary>The amp control; MachineView places it.</summary>
        public AmpControl Amp { get; set; }

        /// <summary>Builds the context menus (MachineMenus); set by MachineView.</summary>
        public MachineMenus Menus { get; set; }

        public CanvasMap Map => map;
        public double Zoom => Math.Pow(2.0, zoomLevel / (double)ZoomLevels);
        public int ZoomLevel => zoomLevel;
        public Vector Offset => offset;
        public Size Extent => new Size(map.Width * Zoom, map.Height * Zoom);
        public IReadOnlyCollection<IMachine> Selection => selected;

        /// <summary>P3d: why the last connect drag was refused (shown by the canvas for two seconds), else null.</summary>
        public string Refusal { get; private set; }

        Point refusalAt;

        /// <summary>P3d: the machine whose port dot the pointer is on (tests).</summary>
        public IMachine PortHover => hoverPort;

        /// <summary>P3d: a dot is drawn on a machine that is hovered or selected, and on every input while a wire is being dragged.</summary>
        bool PortShown(IMachine m, bool input) =>
            hover == m || selected.Contains(m) || (drag == Drag.Connect && input);

        static bool HasOutput(IMachine m) => !m.IsControlMachine && m.DLL.Info.Type is MachineType.Generator or MachineType.Effect;

        static bool HasInput(IMachine m) => !m.IsControlMachine && m.DLL.Info.Type is MachineType.Effect or MachineType.Master;

        /// <summary>The canvas point of a machine's port dot (tests).</summary>
        public Point OutPortOf(IMachine m) => MachineGeometry.OutPort(BoxOf(m));

        public Point InPortOf(IMachine m) => MachineGeometry.InPort(BoxOf(m));

        /// <summary>The machine whose output dot is at the point (a dot is found when the pointer arrives at it, and then drawn), or null.</summary>
        IMachine OutPortAt(Point p)
        {
            var list = Ordered();
            for (int i = list.Count - 1; i >= 0; i--)
                if (HasOutput(list[i]) && MachineGeometry.Near(MachineGeometry.OutPort(BoxOf(list[i])), p)) return list[i];
            return null;
        }

        /// <summary>The machine whose input dot is at the point, or null.</summary>
        IMachine InPortAt(Point p)
        {
            var list = Ordered();
            for (int i = list.Count - 1; i >= 0; i--)
                if (HasInput(list[i]) && MachineGeometry.Near(MachineGeometry.InPort(BoxOf(list[i])), p)) return list[i];
            return null;
        }

        IEnumerable<IMachine> Machines => song?.Machines ?? (IEnumerable<IMachine>)Array.Empty<IMachine>();

        static KeyModifiers CommandKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        static readonly Cursor HandCursor = new Cursor(StandardCursorType.Hand);
        static readonly Cursor UpArrowCursor = new Cursor(StandardCursorType.UpArrow);
        static readonly Cursor NoCursor = new Cursor(StandardCursorType.No);
        static readonly Cursor PanCursor = new Cursor(StandardCursorType.SizeAll);

        #region Engine events

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ModulationBoard.Changed += OnModulationChanged;
            modsDirty = true;
            StartMeters();
            Watch(core.SongCore);
            core.PropertyChanged += OnCoreChanged;
            StudioSelection.For(core).Changed += FollowSelection;
            FollowSelection(null);                  // shown again: on the selection the other views made meanwhile
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            ModulationBoard.Changed -= OnModulationChanged;
            StopMeters();
            core.PropertyChanged -= OnCoreChanged;
            StudioSelection.For(core).Changed -= FollowSelection;
            Watch(null);
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>Fork (machine-safety.md §3.3): the Master shows the clip indicator too.</summary>
        void OnCoreChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "MasterOverText") Redraw();
        }

        void Watch(SongCore s)
        {
            if (song != null)
            {
                song.MachineAdded -= OnMachineAdded;
                song.MachineRemoved -= OnMachineRemoved;
                song.ConnectionAdded -= OnConnectionChanged;
                song.ConnectionRemoved -= OnConnectionChanged;
                foreach (var m in watched) m.PropertyChanged -= OnMachineChanged;
                watched.Clear();
            }
            song = s;
            if (song != null)
            {
                song.MachineAdded += OnMachineAdded;
                song.MachineRemoved += OnMachineRemoved;
                song.ConnectionAdded += OnConnectionChanged;
                song.ConnectionRemoved += OnConnectionChanged;
                foreach (var m in song.Machines) WatchMachine(m);
            }
            Redraw();
            MachinesChanged?.Invoke();
        }

        void WatchMachine(IMachine m)
        {
            if (watched.Add(m)) m.PropertyChanged += OnMachineChanged;
        }

        /// <summary>P3d: the song's machines changed (the empty state follows).</summary>
        public event Action MachinesChanged;

        void OnMachineAdded(IMachine m)
        {
            WatchMachine(m);
            Redraw();
            MachinesChanged?.Invoke();
        }

        void OnMachineRemoved(IMachine m)
        {
            if (watched.Remove(m)) m.PropertyChanged -= OnMachineChanged;
            selected.Remove(m);
            order.Remove(m);
            dragPositions.Remove(m);
            if (hover == m) hover = null;
            if (connectFrom == m || connectTo == m) EndDrag(commit: false);
            Redraw();
            MachinesChanged?.Invoke();
        }

        void OnConnectionChanged(IMachineConnection c)
        {
            // A wire going away takes its amp control with it, without an undo step of its own.
            if (Amp != null && Amp.Connection == c) Amp.Hide(commit: false);
            Redraw();
        }

        void OnMachineChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "Position":
                case "Name":
                case "IsActive":
                case "IsMuted":
                case "IsSoloed":
                case "IsBypassed":
                case "MIDIInputChannel":
                case "OversampleFactor":
                case "IsDiverged":          // fork (machine-safety.md §3.2)
                    Redraw();
                    if (sender == hover) UpdateTip();
                    break;
            }
        }

        void Redraw()
        {
            if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
            else Dispatcher.UIThread.Post(InvalidateVisual);
        }

        #endregion

        #region Meters (P3g, decision 82)

        // The boxes and the wires brighten with the level that goes through them, from the engine's own post-fader peak of each connection
        // (SessionMixer.PeakOf: the audio thread measures it only while a view asks, one pass over each block it already has; nothing
        // else on it), taken here thirty times a second on the UI thread. A box shows the loudest of the wires it sends out (the Master:
        // of those that come in); a silent song looks as it always did.

        /// <summary>Where a connection's peak comes from (sample units, 32768 is 0 dBFS); the engine's, unless a test or a measurement gives its own.</summary>
        internal Func<IMachineConnection, (float Left, float Right)> PeakSource { get; set; } = SessionMixer.PeakOf;

        /// <summary>Meters on the boxes and wires (on unless a measurement turns them off).</summary>
        internal bool MetersShown { get; set; } = true;

        /// <summary>How many times a second the levels are taken.</summary>
        public const int MeterRate = 30;

        /// <summary>A level falls this much of its -60 to 0 dB scale in a second.</summary>
        const double MeterFall = 1.0;

        readonly DispatcherTimer meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / MeterRate) };
        readonly Dictionary<IMachineConnection, double> wireLevels = new Dictionary<IMachineConnection, double>();
        readonly Dictionary<IMachine, double> boxLevels = new Dictionary<IMachine, double>();
        bool metersRunning;

        void StartMeters()
        {
            if (metersRunning) return;
            metersRunning = true;
            SessionMixer.UseMeters(true);
            meterTimer.Tick -= OnMeterTick;
            meterTimer.Tick += OnMeterTick;
            meterTimer.Start();
        }

        void StopMeters()
        {
            if (!metersRunning) return;
            metersRunning = false;
            meterTimer.Stop();
            meterTimer.Tick -= OnMeterTick;
            SessionMixer.UseMeters(false);
            wireLevels.Clear();
            boxLevels.Clear();
        }

        void OnMeterTick(object sender, EventArgs e) => TickMeters(1.0 / MeterRate);

        /// <summary>The level of a peak on the meter's scale, 0 (-60 dB and below) to 1 (0 dB).</summary>
        public static double LevelOf(float peak) => peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak / 32768.0) + 60) / 60, 0, 1);

        /// <summary>Takes the peaks and lets the levels fall by <paramref name="seconds"/>; draws again when a level moved visibly (tests drive it).</summary>
        internal void TickMeters(double seconds)
        {
            if (!MetersShown) return;
            bool changed = false;
            boxLevels.Clear();
            var wires = Wires.ToList();
            foreach (var c in wires)
            {
                var (l, r) = PeakSource(c);
                double now = LevelOf(Math.Max(l, r));
                wireLevels.TryGetValue(c, out double before);
                double level = Math.Max(now, before - MeterFall * seconds);
                if (level < 0.003) level = 0;
                if (Math.Abs(level - before) > 0.004 || (level == 0) != (before == 0)) changed = true;
                wireLevels[c] = level;
                Raise(c.Source, level);
                if (c.Destination?.DLL?.Info?.Type == MachineType.Master) Raise(c.Destination, level);
            }
            if (wireLevels.Count > wires.Count)
            {
                var live = new HashSet<IMachineConnection>(wires);
                foreach (var gone in wireLevels.Keys.Where(k => !live.Contains(k)).ToList()) wireLevels.Remove(gone);
            }
            if (changed) InvalidateVisual();

            void Raise(IMachine m, double level)
            {
                if (m == null) return;
                boxLevels.TryGetValue(m, out double v);
                if (level > v) boxLevels[m] = level;
            }
        }

        /// <summary>A wire's level now, 0 to 1 (tests).</summary>
        internal double WireLevel(IMachineConnection c) => wireLevels.TryGetValue(c, out double v) ? v : 0;

        /// <summary>A box's level now, 0 to 1 (tests).</summary>
        internal double BoxLevel(IMachine m) => boxLevels.TryGetValue(m, out double v) ? v : 0;

        static readonly Color MeterGreen = Shell.Design.StudioTokens.Of(NeoBuzz.Editor.Tokens.MeterNormal);
        static readonly Color MeterYellow = Shell.Design.StudioTokens.Of(NeoBuzz.Editor.Tokens.MeterHot);

        /// <summary>The meter's colour at a level: green, yellow from -6 dB.</summary>
        static Color MeterColour(double level) => level > 0.9 ? MeterYellow : MeterGreen;

        static readonly Dictionary<(int, uint, uint), (IPen Pen, IBrush Fill)> litWires = new Dictionary<(int, uint, uint), (IPen, IBrush)>();

        /// <summary>
        /// How lit a level looks (0 to 1): the level is already on a dB scale (-60 to 0 dB is 0 to 1); the look is the square of it, so a path
        /// at -30 dB glows a quarter as much as one at 0 dB, and the paths of a song with a few loud and many quiet wires differ at a glance.
        /// </summary>
        internal static double Glow(double level) => level * level;

        /// <summary>The pen of a wire carrying a level, and the fill of its arrow: the line's colour and the arrow's, each towards the meter's colour.</summary>
        static (IPen Pen, IBrush Fill) LitWire(double level, Color track, Color arrow)
        {
            int step = Math.Clamp((int)Math.Round(Glow(level) * 16), 1, 16);
            var key = (step, track.ToUInt32(), arrow.ToUInt32());
            if (!litWires.TryGetValue(key, out var lit))
            {
                double g = step / 16.0;
                var meter = MeterColour(Math.Sqrt(g));
                double t = 0.12 + 0.88 * g;
                var brush = new SolidColorBrush(Mix(track, meter, t)).ToImmutable();
                var fill = new SolidColorBrush(Mix(arrow, meter, t)).ToImmutable();
                lit = (new Pen(brush, 1 + 2 * g), fill);
                if (litWires.Count > 512) litWires.Clear();
                litWires[key] = lit;
            }
            return lit;
        }

        static readonly Dictionary<Color, IBrush> bands = new Dictionary<Color, IBrush>();

        static IBrush IdentityBand(Color c)
        {
            if (!bands.TryGetValue(c, out var brush)) bands[c] = brush = new SolidColorBrush(c).ToImmutable();
            return brush;
        }

        static readonly IBrush[] Veils = Enumerable.Range(0, 17).Select(i => (IBrush)new SolidColorBrush(Color.FromArgb((byte)(90 * i / 16), 255, 255, 255)).ToImmutable()).ToArray();
        static readonly IBrush StripGreen = new SolidColorBrush(MeterGreen).ToImmutable();
        static readonly IBrush StripYellow = new SolidColorBrush(MeterYellow).ToImmutable();

        static Color Mix(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            byte M(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromRgb(M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
        }

        #endregion

        #region Layout: canvas, zoom and scroll

        /// <summary>
        /// The view's size changed: the canvas is twice the view and scrolls to its middle, as
        /// MachineView.UpdateCanvasSize does.
        /// </summary>
        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            var size = e.NewSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            map = new CanvasMap(size.Width * CanvasMap.CanvasSize, size.Height * CanvasMap.CanvasSize);
            ScrollTo(new Vector(Extent.Width / 2 - size.Width / 2, Extent.Height / 2 - size.Height / 2));
        }

        public void ScrollTo(Vector to)
        {
            double maxX = Math.Max(0, Extent.Width - Bounds.Width), maxY = Math.Max(0, Extent.Height - Bounds.Height);
            offset = new Vector(Math.Clamp(Math.Round(to.X), 0, maxX), Math.Clamp(Math.Round(to.Y), 0, maxY));
            InvalidateVisual();
            ViewChanged?.Invoke();
        }

        /// <summary>A zoom level, keeping the canvas point under a view point where it is (Zoomer).</summary>
        public void SetZoom(int level, Point? around = null)
        {
            level = Math.Clamp(level, -ZoomLevels, ZoomLevels);
            var at = around ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
            var logical = ToLogical(at);
            zoomLevel = level;
            if (around == null)
                ScrollTo(new Vector(Extent.Width / 2 - Bounds.Width / 2, Extent.Height / 2 - Bounds.Height / 2));
            else
                ScrollTo(new Vector(logical.X * Zoom - at.X, logical.Y * Zoom - at.Y));
        }

        /// <summary>A view point (this control's pixels) as a canvas point (unzoomed).</summary>
        public Point ToLogical(Point view) => new Point((view.X + offset.X) / Zoom, (view.Y + offset.Y) / Zoom);

        public Point ToView(Point logical) => new Point(logical.X * Zoom - offset.X, logical.Y * Zoom - offset.Y);

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var p = e.GetPosition(this);
            if (e.KeyModifiers.HasFlag(CommandKey) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Ctrl (Command) + wheel zooms around the pointer (Zoomer, CtrlMouseWheel).
                wheelZoom += e.Delta.Y;
                int level = zoomLevel;
                while (wheelZoom >= 1) { wheelZoom -= 1; level++; }
                while (wheelZoom <= -1) { wheelZoom += 1; level--; }
                if (level != zoomLevel) SetZoom(level, p);
            }
            else
            {
                // The wheel and a trackpad scroll (ScrollViewer); Shift turns a wheel sideways.
                var d = e.Delta;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && d.X == 0) d = new Vector(d.Y, 0);
                ScrollTo(offset - d * 50);
            }
            e.Handled = true;
        }

        /// <summary>A trackpad pinch zooms around the pointer too (a Mac's; there is no WPF counterpart).</summary>
        void OnPinch(object sender, PointerDeltaEventArgs e)
        {
            wheelZoom += Math.Log2(Math.Max(0.1, 1 + e.Delta.X)) * ZoomLevels;
            int level = zoomLevel;
            while (wheelZoom >= 1) { wheelZoom -= 1; level++; }
            while (wheelZoom <= -1) { wheelZoom += 1; level--; }
            if (level != zoomLevel) SetZoom(level, e.GetPosition(this));
            e.Handled = true;
        }

        #endregion

        #region Machines and wires

        Point PositionOf(IMachine m)
        {
            if (dragPositions.TryGetValue(m, out var p)) return p;
            var pos = m.Position;
            return CanvasMap.Clamp(new Point(pos.Item1, pos.Item2));
        }

        /// <summary>A machine's box, in canvas pixels (unzoomed).</summary>
        public Rect BoxOf(IMachine m)
        {
            var p = PositionOf(m);
            return map.BoxAt(p.X, p.Y);
        }

        /// <summary>The machines in drawing order, bottom to top (the last one clicked is on top).</summary>
        List<IMachine> Ordered()
        {
            var all = Machines.ToList();
            order.RemoveAll(m => !all.Contains(m));
            foreach (var m in all)
                if (!order.Contains(m)) order.Add(m);
            return order.ToList();
        }

        void BringToTop(IMachine m)
        {
            order.Remove(m);
            order.Add(m);
        }

        public IMachine MachineAt(Point logical)
        {
            var list = Ordered();
            for (int i = list.Count - 1; i >= 0; i--)
                if (BoxOf(list[i]).Contains(logical)) return list[i];
            return null;
        }

        public IEnumerable<IMachineConnection> Wires
        {
            get
            {
                var shown = Machines.ToHashSet();
                return shown.SelectMany(m => m.Inputs).Where(c => shown.Contains(c.Source)).ToList();
            }
        }

        public WireShape WireOf(IMachineConnection c) => MachineGeometry.Wire(BoxOf(c.Source), BoxOf(c.Destination));

        public IMachineConnection WireAt(Point logical) => Wires.LastOrDefault(c => MachineGeometry.HitsArrow(WireOf(c), logical));

        #endregion

        #region Drawing

        sealed class Palette
        {
            readonly ReBuzzCore core;
            readonly Dictionary<string, IBrush> brushes = new Dictionary<string, IBrush>();

            public Palette(ReBuzzCore core) => this.core = core;

            public Color Colour(string name)
            {
                // Fork (ux-overhaul.md §3.3): the dark theme's table first; Classic Buzz (and a name the table lacks) asks the
                // Buzz theme file, which a user's own theme sets.
                if (Shell.Design.StudioTheme.Machines.Colour(name) is { } own) return own;
                var c = core.GetThemeColour(name);
                return Color.FromArgb(c.A, c.R, c.G, c.B);
            }

            public IBrush this[string name]
            {
                get
                {
                    if (!brushes.TryGetValue(name, out var b)) brushes[name] = b = new SolidColorBrush(Colour(name));
                    return b;
                }
            }
        }

        /// <summary>The selection's glass (MachineControl.xaml, GlassBrush).</summary>
        static readonly IBrush Glass = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            Opacity = 0.5,
            GradientStops =
            {
                new GradientStop(Shell.Design.StudioTokens.Glass, 0.2),
                new GradientStop(Colors.Transparent, 0.4),
                new GradientStop(Shell.Design.StudioTokens.Glass, 0.5),
                new GradientStop(Colors.Transparent, 0.75),
                new GradientStop(Shell.Design.StudioTokens.Glass, 0.9),
                new GradientStop(Colors.Transparent, 1),
            },
        };

        /// <summary>The rubber band (MVResources.xaml, SelectionRectangleStyle).</summary>
        static readonly IBrush BandFill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x10, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0x20, 0, 0, 0), 1) },
        };

        static readonly IPen BandPen = new Pen(new SolidColorBrush(Colors.Black, 0.5), 1);

        /// <summary>The theme's machine shadow (gfx/shadow.png: the box, grey, 10 px larger down and right).</summary>
        static readonly BoxShadows Shadow = new BoxShadows(new BoxShadow { OffsetX = 4, OffsetY = 4, Blur = 6, Color = Color.FromArgb(0x60, 0, 0, 0) });

        static readonly Typeface NameFace = new Typeface(new FontFamily("Arial"), FontStyle.Normal, FontWeight.Bold);
        static readonly Typeface PlainFace = new Typeface(new FontFamily("Arial"));

        #region Modulation wires (neo-patch.md §3, phase NM)

        /// <summary>One dashed wire: a modulator machine to a machine it moves (all the parameters it moves there).</summary>
        internal sealed record ModLink(IMachine Source, IMachine Target, string Label);

        List<ModLink> modLinks = new List<ModLink>();
        bool modsDirty = true;
        long modsAt;
        ModLink hoverMod;

        void OnModulationChanged()
        {
            modsDirty = true;
            Redraw();
        }

        /// <summary>The modulation wires now: each modulator's active targets, one wire per (modulator, machine) pair.</summary>
        internal IReadOnlyList<ModLink> ModLinks
        {
            get
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                // The targets change on their own (a machine renamed or deleted, a base moved): look again a few times a second.
                if (modsDirty || now - modsAt > System.Diagnostics.Stopwatch.Frequency / 4)
                {
                    modsDirty = false;
                    modsAt = now;
                    var shown = new HashSet<IMachine>(Ordered());
                    var found = new List<ModLink>();
                    foreach (var mod in ModulationBoard.All())
                    {
                        var src = mod.Machine;
                        if (src == null || !shown.Contains(src)) continue;
                        IReadOnlyList<ModulationInfo> targets;
                        try { targets = mod.Targets; } catch (Exception) { continue; }
                        foreach (var group in targets.Where(t => t.State == ModulationState.Active)
                                     .GroupBy(t => ModulationBoard.Resolve(mod, t)).Where(g => g.Key != null && shown.Contains(g.Key)))
                            found.Add(new ModLink(src, group.Key, string.Join(", ", group.Select(t => $"{t.Param} {t.Depth:+0;-0;0} %"))));
                    }
                    if (hoverMod != null) hoverMod = found.FirstOrDefault(l => l.Source == hoverMod.Source && l.Target == hoverMod.Target);
                    modLinks = found;
                }
                return modLinks;
            }
        }

        static readonly DashStyle ModDash = new DashStyle(new double[] { 4, 3 }, 0);

        void DrawModulation(DrawingContext ctx)
        {
            foreach (var link in ModLinks)
            {
                var w = MachineGeometry.Wire(BoxOf(link.Source), BoxOf(link.Target));
                if (!w.Visible) continue;
                var colour = MachineIdentity.ColourOf(link.Source) ?? Colors.YellowGreen;
                bool hot = link == hoverMod;
                var pen = new Pen(new SolidColorBrush(colour), hot ? 2.5 : 1.5, ModDash) { LineCap = PenLineCap.Round };
                ctx.DrawLine(pen, w.Start, w.End);
                // The end on the target: a dot, not an arrow (an arrow is a signal wire).
                ctx.DrawEllipse(new SolidColorBrush(colour), null, w.End, hot ? 4 : 3, hot ? 4 : 3);
                if (hot)
                {
                    var ft = new FormattedText(link.Source.Name + " → " + link.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, PlainFace, 12, Brushes.White);
                    var at = new Point(w.Mid.X + 6, w.Mid.Y - ft.Height - 4);
                    ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x20, 0x20)), new Pen(new SolidColorBrush(colour), 1), new Rect(at.X - 4, at.Y - 2, ft.Width + 8, ft.Height + 4), 3, 3);
                    ctx.DrawText(ft, at);
                }
            }
        }

        /// <summary>The modulation wire under a logical point (within a few pixels of its line), or null.</summary>
        internal ModLink ModAt(Point p)
        {
            foreach (var link in ModLinks.Reverse())
            {
                var w = MachineGeometry.Wire(BoxOf(link.Source), BoxOf(link.Target));
                if (!w.Visible) continue;
                var d = w.End - w.Start;
                double len2 = d.X * d.X + d.Y * d.Y;
                double t = len2 <= 0 ? 0 : Math.Clamp(((p.X - w.Start.X) * d.X + (p.Y - w.Start.Y) * d.Y) / len2, 0, 1);
                double dx = p.X - (w.Start.X + t * d.X), dy = p.Y - (w.Start.Y + t * d.Y);
                if (dx * dx + dy * dy <= 36) return link;
            }
            return null;
        }

        #endregion

        public override void Render(DrawingContext ctx)
        {
            var colours = new Palette(core);
            ctx.FillRectangle(colours["MV Background"], new Rect(Bounds.Size));
            var machines = Ordered();
            bool solo = machines.Any(m => m.IsSoloed);
            using (ctx.PushTransform(Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(-offset.X, -offset.Y)))
            {
                var line = new Pen(colours["MV Line"], 1);
                foreach (var c in Wires)
                    DrawWire(ctx, WireOf(c), colours, line, level: MetersShown ? WireLevel(c) : 0, track: colours.Colour("MV Line"));

                DrawModulation(ctx);

                if (drag == Drag.Connect && connectFrom != null)
                {
                    // The wire being made (Connection with no destination yet): half opacity, to the pointer or the target.
                    var src = BoxOf(connectFrom);
                    var dst = connectTo != null ? BoxOf(connectTo) : new Rect(connectPoint.X, connectPoint.Y, 1, 1);
                    using (ctx.PushOpacity(0.5))
                        DrawWire(ctx, MachineGeometry.Wire(src, dst), colours, line, arrow: false);
                }

                foreach (var m in machines)
                    DrawMachine(ctx, m, colours, solo);

                DrawPorts(ctx, machines);

                if (drag == Drag.Select)
                    ctx.DrawRectangle(BandFill, BandPen, band, 2, 2);
            }
        }

        static readonly IBrush PortFill = Shell.Design.StudioTokens.SurfaceBaseBrush;

        /// <summary>P3d: the port dots (a filled output dot, a hollow input dot), the refused target and the refusal's words.</summary>
        void DrawPorts(DrawingContext ctx, List<IMachine> machines)
        {
            var accent = Shell.Design.StudioTokens.AccentBrush;
            var ring = Shell.Design.StudioTokens.TextPrimaryBrush;
            var bad = Shell.Design.StudioTokens.ErrorTextBrush;
            double r = MachineGeometry.PortRadius;
            foreach (var m in machines)
            {
                var box = BoxOf(m);
                bool refused = m == connectRefused;
                if (HasOutput(m) && PortShown(m, false))
                {
                    bool hot = hoverPort == m && MachineGeometry.Near(MachineGeometry.OutPort(box), lastLogical) || (drag == Drag.Connect && connectFrom == m);
                    ctx.DrawEllipse(accent, new Pen(ring, 1.5), MachineGeometry.OutPort(box), hot ? r + 2 : r, hot ? r + 2 : r);
                }
                if (HasInput(m) && PortShown(m, true))
                {
                    bool valid = drag == Drag.Connect && m != connectFrom && core.SongCore.CanConnectMachines(connectFrom, m);
                    bool hot = (hoverPort == m && !MachineGeometry.Near(MachineGeometry.OutPort(box), lastLogical)) || m == connectTo;
                    var pen = new Pen(refused ? bad : valid || !(drag == Drag.Connect) ? accent : ring, hot || refused ? 3 : 2);
                    ctx.DrawEllipse(PortFill, pen, MachineGeometry.InPort(box), hot ? r + 2 : r, hot ? r + 2 : r);
                }
                if (refused)
                    ctx.DrawRectangle(null, new Pen(bad, 2), box.Deflate(1));
            }
            string words = connectWhy ?? Refusal;
            if (words != null)
            {
                var at = connectWhy != null ? connectPoint + new Vector(12, 14) : refusalAt + new Vector(-40, 30);
                var ft = new FormattedText(words, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, PlainFace, 12, bad);
                var plate = new Rect(at.X - 4, at.Y - 2, ft.Width + 8, ft.Height + 4);
                ctx.DrawRectangle(new SolidColorBrush(Shell.Design.StudioTokens.SevereBack), new Pen(bad, 1), plate, 3, 3);
                ctx.DrawText(ft, at);
            }
        }

        static void DrawWire(DrawingContext ctx, WireShape w, Palette colours, IPen line, bool arrow = true, double level = 0, Color track = default)
        {
            if (!w.Visible) return;
            if (level > 0.01)
            {
                // The level lights the wire: its line colour towards the meter's colour, and a little thicker (sixteen steps, drawn with cached pens).
                var (pen, fill) = LitWire(level, track, colours.Colour("MV Arrow"));
                ctx.DrawLine(pen, w.Start, w.End);
                if (!arrow) return;
                var tri = new StreamGeometry();
                using (var gc = tri.Open())
                {
                    gc.BeginFigure(w.Apex, true);
                    gc.LineTo(w.Left);
                    gc.LineTo(w.Right);
                    gc.EndFigure(true);
                }
                ctx.DrawGeometry(fill, pen, tri);
                return;
            }
            ctx.DrawLine(line, w.Start, w.End);
            if (!arrow) return;
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(w.Apex, true);
                gc.LineTo(w.Left);
                gc.LineTo(w.Right);
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(colours["MV Arrow"], line, g);
        }

        void DrawMachine(DrawingContext ctx, IMachine m, Palette colours, bool solo)
        {
            var box = BoxOf(m);
            var type = m.DLL.Info.Type;
            var text = colours["MV Machine Text"];
            var look = Shell.Design.StudioTheme.Machines;

            // Shadow and body (MachineBackgroundColor = IMachine.GetThemeColor), border (MV Machine Border).
            ctx.DrawRectangle(new SolidColorBrush(BackgroundOf(m, colours)), null, box, 0, 0, Shadow);
            ctx.DrawRectangle(null, new Pen(colours["MV Machine Border"], 1), box.Deflate(0.5));

            // P3h (decision 85): in the dark theme the box wears its category's identity colour: a tint for the body (above) and the colour whole
            // along the top edge. The Master, a machine the guard muted and Classic Buzz keep theirs.
            if (!look.IsClassic && !IsDiverged(m) && MachineIdentity.ColourOf(m) is { } identity)
                ctx.FillRectangle(IdentityBand(MachineIdentity.BandOf(identity, m.IsMuted)), new Rect(box.X + 1, box.Y + 1, box.Width - 2, 3));

            // P3g: the level through it brightens the box and fills a strip along its foot, green, yellow from -6 dB.
            double level = MetersShown ? BoxLevel(m) : 0;
            if (level > 0.01)
            {
                ctx.FillRectangle(Veils[(int)Math.Round(Glow(level) * 16)], box);
                var strip = new Rect(box.X + 2, box.Bottom - 6, (box.Width - 4) * level, 4);
                ctx.FillRectangle(level > 0.9 ? StripYellow : StripGreen, strip);
            }

            // Mode: .NET / 32, the MIDI channel, oversampling (ModeText), Arial 8 at 13,2.
            string mode = ModeText(m);
            if (mode != null)
                ctx.DrawText(new FormattedText(mode, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, PlainFace, 11, text), new Point(box.X + 13, box.Y + 2));

            // Missing or crashed (ErrorText): dark red at the bottom. Fork (machine-safety.md §3.2): diverged, in white on red.
            bool diverged = IsDiverged(m);
            // Fork (ux-overhaul.md P1): plain words: a machine this computer can't run is "Windows only" off Windows, one
            // the guard muted "muted: blew up" ("missing" and "diverged" were the engine's words, gallery mac-500, mac-520).
            string error = m.DLL.IsMissing ? Shell.PlainWords.Missing : m.DLL.IsCrashed ? "stopped working" : diverged ? "muted: blew up" : null;
            if (error != null)
            {
                var ft = new FormattedText(error, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NameFace, diverged ? 11 : 15, diverged ? look.DivergedText : look.Error);
                ctx.DrawText(ft, new Point(box.X + (box.Width - ft.Width) / 2, box.Bottom - ft.Height));
            }

            // Fork (machine-safety.md §3.3): the Master's clip indicator, as the transport's: red with the latched over.
            if (type == MachineType.Master && core.MasterOver)
            {
                ctx.DrawRectangle(null, new Pen(Shell.MasterMeter.ClipBrush, 3), box.Deflate(1.5));
                var ft = new FormattedText(core.MasterOverText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NameFace, 11, Shell.MasterMeter.ClipBrush);
                ctx.DrawText(ft, new Point(box.X + (box.Width - ft.Width) / 2, box.Bottom - ft.Height - 2));
            }

            // The name, bold Arial 13, shrunk to fit (a Viewbox, DownOnly, 4 px margins).
            var name = new FormattedText(NameText(m, solo), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NameFace, 13, text);
            double scale = Math.Min(1, Math.Min((box.Width - 8) / Math.Max(1, name.Width), box.Height / Math.Max(1, name.Height)));
            var centre = box.Center;
            using (ctx.PushTransform(Matrix.CreateTranslation(-centre.X, -centre.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(centre.X, centre.Y)))
                ctx.DrawText(name, new Point(centre.X - name.Width / 2, centre.Y - name.Height / 2));

            // The mute LED, lit while the machine makes sound (not on control machines).
            if (!m.IsControlMachine)
            {
                var led = MachineGeometry.Led(box);
                ctx.DrawRectangle(new SolidColorBrush(LedOf(m, colours, m.IsActive)), new Pen(Brushes.Black, 1), led.Deflate(0.5));
            }

            // The delete button while the pointer is over the machine (HideDeleteButton), not on Master.
            if (hover == m && type != MachineType.Master)
            {
                var r = MachineGeometry.DeleteButton(box).Deflate(1);
                var pen = new Pen(hoverDelete ? look.DeleteXHot : look.DeleteX, 2) { LineCap = PenLineCap.Round };
                ctx.DrawLine(pen, r.TopLeft, r.BottomRight);
                ctx.DrawLine(pen, r.BottomLeft, r.TopRight);
            }

            if (selected.Contains(m))
            {
                if (look.Glass) ctx.FillRectangle(Glass, box);
                else
                {
                    // The dark theme: the accent over the box and its outline, a selection the same in every view (§3.1).
                    ctx.FillRectangle(look.Selection, box);
                    ctx.DrawRectangle(null, new Pen(look.SelectionBorder, 2), box.Deflate(1));
                }
            }
        }

        /// <summary>Fork (machine-safety.md §3.2): the machine's guard tripped.</summary>
        static bool IsDiverged(IMachine m) => (m as MachineCore)?.IsDiverged == true;

        /// <summary>A diverged machine's red.</summary>
        public static readonly Color DivergedColour = Shell.Design.StudioTokens.Diverged;

        /// <summary>IMachine.GetThemeColor (BuzzGUI.Common): by type, darker when muted; red when diverged (fork).</summary>
        static Color BackgroundOf(IMachine m, Palette colours)
        {
            if (IsDiverged(m)) return DivergedColour;
            if (!Shell.Design.StudioTheme.Machines.IsClassic && MachineIdentity.ColourOf(m) is { } identity)
                return MachineIdentity.BodyOf(identity, m.IsMuted);
            if (m.IsControlMachine) return colours.Colour("MV Control");
            return m.DLL.Info.Type switch
            {
                MachineType.Master => colours.Colour("MV Master"),
                MachineType.Generator => colours.Colour(m.IsMuted ? "MV Generator Mute" : "MV Generator"),
                MachineType.Effect => colours.Colour(m.IsMuted ? "MV Effect Mute" : "MV Effect"),
                _ => Shell.Design.StudioTokens.TextSecondary,
            };
        }

        static Color LedOf(IMachine m, Palette colours, bool on)
        {
            string kind = m.DLL.Info.Type switch
            {
                MachineType.Master => "Master",
                MachineType.Generator => "Generator",
                MachineType.Effect => "Effect",
                _ => null,
            };
            return kind == null ? Shell.Design.StudioTokens.TextSecondary : colours.Colour($"MV {kind} LED {(on ? "On" : "Off")}");
        }

        /// <summary>MachineControl.NameText: (muted), &lt;bypassed&gt;, [not soloed] while another generator is.</summary>
        public static string NameText(IMachine m, bool soloMode)
        {
            string s = m.Name;
            if (m.IsMuted) s = '(' + s + ')';
            if (m.IsBypassed) s = '<' + s + '>';
            if (soloMode && m.DLL.Info.Type == MachineType.Generator && !m.IsControlMachine && !m.IsSoloed) s = '[' + s + ']';
            return s;
        }

        /// <summary>MachineControl.ModeText.</summary>
        static string ModeText(IMachine m)
        {
            string s = "";
            if (m.DLL.IsOutOfProcess) s += "32 ";
            // Fork (ux-overhaul.md P1): no ".NET" tag on managed machines, an implementation detail (gallery mac-010).
            int midi = m.MIDIInputChannel;
            if (midi >= 0) s += midi == 0 ? "MIDI A" : "MIDI " + midi;
            if (m.OversampleFactor > 1) s += (s.Length > 0 ? ", " : "") + "2x";
            return s.Length > 0 ? s : null;
        }

        #endregion

        #region Selection

        public void Select(IEnumerable<IMachine> machines, bool add = false)
        {
            if (!add) selected.Clear();
            IMachine last = null;
            foreach (var m in machines) selected.Add(last = m);
            InvalidateVisual();
            PublishSelection(last);
        }

        /// <summary>Fork (ux-overhaul.md P2c): what the user selected here is the studio's selection (a band's: its first machine).</summary>
        void PublishSelection(IMachine primary = null)
        {
            if (following) return;
            var machine = primary != null && selected.Contains(primary) ? primary : selected.FirstOrDefault(Machines.Contains);
            StudioSelection.For(core).SelectMachine(machine, this);
        }

        bool following;

        /// <summary>The other views selected a machine (or nothing): it is the one selected here.</summary>
        void FollowSelection(object source)
        {
            if (source == this) return;
            var machine = StudioSelection.For(core).Machine;
            if (machine == null ? selected.Count == 0 : selected.Count == 1 && selected.Contains(machine)) return;
            following = true;
            try
            {
                selected.Clear();
                if (machine != null) selected.Add(machine);
                InvalidateVisual();
            }
            finally
            {
                following = false;
            }
        }

        public void SelectAll() => Select(Machines);

        public void ClearSelection() => Select(Array.Empty<IMachine>());

        /// <summary>The selection, or the machine if it isn't selected (as a right click or a key on a machine works).</summary>
        IReadOnlyList<IMachine> SelectionOr(IMachine m) => m != null && !selected.Contains(m) ? new[] { m } : selected.Where(Machines.Contains).ToList();

        /// <summary>A click on a machine: it gets MIDI focus (generators and control machines), as MachineControl's focus does.</summary>
        void FocusMachine(IMachine m)
        {
            BringToTop(m);
            if (m.DLL.Info.Type == MachineType.Generator || m.IsControlMachine)
                core.MIDIFocusMachine = m;
        }

        #endregion

        #region Mouse

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetCurrentPoint(this);
            var view = pt.Position;
            var p = ToLogical(view);
            var kind = pt.Properties.PointerUpdateKind;
            var mods = e.KeyModifiers;
            var outPort = pt.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed && mods == KeyModifiers.None ? OutPortAt(p) : null;
            var machine = MachineAt(p) ?? outPort ?? InPortAt(p);
            var wire = machine == null ? WireAt(p) : null;
            e.Handled = true;
            if (drag != Drag.None) return;
            Refusal = null;
            dragButton = kind switch
            {
                PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
                PointerUpdateKind.RightButtonPressed => MouseButton.Right,
                _ => MouseButton.Left,
            };

            // On a Mac, Control-click is a right click.
            if (OperatingSystem.IsMacOS() && kind == PointerUpdateKind.LeftButtonPressed && mods.HasFlag(KeyModifiers.Control))
                kind = PointerUpdateKind.RightButtonPressed;

            if (kind == PointerUpdateKind.MiddleButtonPressed)
            {
                if (machine != null) BeginConnect(machine, p, e);
                else if (wire != null) MachineEdits.Disconnect(core, wire);       // middle click on an arrow
                else if (e.ClickCount == 2) SetZoom(0);                             // Zoomer: middle double click resets
                else BeginPan(view, e);
                return;
            }

            if (kind == PointerUpdateKind.RightButtonPressed)
            {
                if (machine != null)
                {
                    if (!selected.Contains(machine)) Select(new[] { machine });
                    FocusMachine(machine);
                }
                else if (wire != null)
                    Amp?.Show(wire, TranslateToAmp(view), null);
                return;
            }

            if (kind != PointerUpdateKind.LeftButtonPressed) return;

            if (outPort != null)
            {
                // P3d: a drag from the output dot connects, as Shift-drag does from anywhere on the box.
                BeginConnect(outPort, p, e);
                return;
            }

            if (machine != null)
            {
                var box = BoxOf(machine);
                if (e.ClickCount == 2 && mods == KeyModifiers.None)
                {
                    MachineEdits.OpenGui(core, machine);
                    return;
                }
                if (mods.HasFlag(KeyModifiers.Shift))
                {
                    BeginConnect(machine, p, e);
                    return;
                }
                if (mods.HasFlag(CommandKey))
                {
                    BeginSelect(p, add: true, e);
                    return;
                }
                if (!machine.IsControlMachine && MachineGeometry.Led(box).Contains(p))
                {
                    // The LED is the mute button (MuteCommand).
                    if (!selected.Contains(machine)) Select(new[] { machine });
                    MachineEdits.Mute(core, SelectionOr(machine), !machine.IsMuted);
                    return;
                }
                if (hover == machine && machine.DLL.Info.Type != MachineType.Master && MachineGeometry.DeleteButton(box).Contains(p))
                {
                    MachineEdits.Delete(core, new[] { machine });
                    return;
                }
                BeginMove(machine, p, e);
                return;
            }

            if (wire != null)
            {
                // The amp control opens on the arrow, its amp knob already turning (Connection.HandleMouseDown).
                Amp?.Show(wire, TranslateToAmp(view), e);
                return;
            }

            if (e.ClickCount == 2 && mods == KeyModifiers.None)
            {
                // P3d: a double click on empty canvas opens the machine browser there.
                BrowseRequested?.Invoke(p, view, "");
                return;
            }
            if (mods.HasFlag(KeyModifiers.Shift)) BeginPan(view, e);
            else BeginSelect(p, add: mods.HasFlag(CommandKey), e);
        }

        /// <summary>P3d: the machine browser is wanted at a canvas point (logical pixels), and a view point.</summary>
        public event Action<Point, Point, string> BrowseRequested;

        Point lastLogical;

        Point TranslateToAmp(Point view) => Amp?.Parent is Visual parent ? this.TranslatePoint(view, parent) ?? view : view;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var view = e.GetPosition(this);
            var p = ToLogical(view);
            lastLogical = p;
            switch (drag)
            {
                case Drag.Move:
                    DragMove(p);
                    break;
                case Drag.Connect:
                    DragConnect(p);
                    break;
                case Drag.Select:
                    DragSelect(p);
                    break;
                case Drag.Pan:
                    ScrollTo(panStart - (view - dragLast));
                    break;
                default:
                    var port = OutPortAt(p) ?? InPortAt(p);
                    var m = port ?? MachineAt(p);
                    bool overDelete = port == null && m != null && MachineGeometry.DeleteButton(BoxOf(m)).Contains(p);
                    var mod = m == null ? ModAt(p) : null;
                    if (m != hover || overDelete != hoverDelete || port != hoverPort || mod != hoverMod)
                    {
                        hoverMod = mod;
                        hover = m;
                        hoverDelete = overDelete;
                        hoverPort = port;
                        UpdateTip();
                        InvalidateVisual();
                    }
                    break;
            }
        }

        /// <summary>The tip over the machine under the pointer: why a diverged machine is muted (machine-safety.md §3.2).</summary>
        void UpdateTip()
        {
            ToolTip.SetTip(this, HoverTip);
        }

        /// <summary>The tip the canvas shows now (tests).</summary>
        public string HoverTip => hoverPort != null
            ? (HasOutput(hoverPort) && MachineGeometry.Near(MachineGeometry.OutPort(BoxOf(hoverPort)), lastLogical)
                ? "Output of " + hoverPort.Name + ": drag to another machine's input to connect (Shift-drag from the box does the same)"
                : "Input of " + hoverPort.Name + ": drop a wire from another machine's output here")
            : hoverMod != null
            ? $"{hoverMod.Source.Name} moves {hoverMod.Target.Name}: {hoverMod.Label} (dashed wire: a modulation, not a signal)"
            : hover is MachineCore mc && mc.IsDiverged
            ? $"Muted to protect your ears: it {Shell.PlainWords.Diverged(mc)} at beat {mc.DivergedPosition / (double)Math.Max(1, core.TPB):0.##}, " +
              "so the guard silenced and stopped it. Reset and Unmute is on its menu and in the bar above."
            : null;

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (drag == Drag.None && hover != null)
            {
                hover = null;
                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            var view = e.GetPosition(this);
            var p = ToLogical(view);
            var kind = e.InitialPressMouseButton;
            if (drag != Drag.None)
            {
                if (kind != dragButton) return;
                if (drag == Drag.Move || drag == Drag.Connect) DragMoveOrConnect(p);
                EndDrag(commit: true);
                e.Pointer.Capture(null);
                e.Handled = true;
                return;
            }
            bool macRight = OperatingSystem.IsMacOS() && kind == MouseButton.Left && e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (kind == MouseButton.Right || macRight)
            {
                // The context menus open on release, as WPF's do.
                var machine = MachineAt(p);
                if (machine == null && WireAt(p) != null) return;
                OpenMenu = machine != null ? Menus?.ForMachine(machine, SelectionOr(machine)) : Menus?.ForCanvas(p);
                OpenMenu?.Open(this);
                e.Handled = true;
            }
        }

        void DragMoveOrConnect(Point p)
        {
            if (drag == Drag.Move) DragMove(p);
            else DragConnect(p);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            // A lost capture ends a move where it is (Dragger.CancelDrag → EndMoveSelectedMachines).
            if (drag != Drag.None) EndDrag(commit: drag == Drag.Move);
        }

        void BeginMove(IMachine machine, Point p, PointerPressedEventArgs e)
        {
            if (!selected.Contains(machine)) Select(new[] { machine });
            FocusMachine(machine);
            drag = Drag.Move;
            dragOrigin = p;
            moveDelta = default;
            insertInto = null;
            dragPositions.Clear();
            moveStart.Clear();
            var shown = Machines.ToHashSet();
            foreach (var m in selected.Where(shown.Contains)) moveStart[m] = PositionOf(m);
            foreach (var kv in moveStart) dragPositions[kv.Key] = kv.Value;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        readonly Dictionary<IMachine, Point> moveStart = new Dictionary<IMachine, Point>();

        void DragMove(Point p)
        {
            moveDelta = p - dragOrigin;
            var d = map.PositionDelta(moveDelta);
            foreach (var kv in moveStart)
                dragPositions[kv.Key] = CanvasMap.Clamp(kv.Value + d);

            // One effect with no wires over a wire's middle goes into it on release (MachineControl).
            insertInto = null;
            Cursor = HandCursor;
            if (dragPositions.Count == 1)
            {
                var m = dragPositions.Keys.First();
                if (m.DLL.Info.Type == MachineType.Effect && !m.IsControlMachine && m.Inputs.Count == 0 && m.Outputs.Count == 0)
                {
                    var at = dragPositions[m];
                    foreach (var c in Wires)
                    {
                        var centre = new Point(0.5 * (c.Source.Position.Item1 + c.Destination.Position.Item1), 0.5 * (c.Source.Position.Item2 + c.Destination.Position.Item2));
                        var px = map.ScaleToPixels(centre - at);
                        if (Math.Sqrt(px.X * px.X + px.Y * px.Y) <= 15)
                        {
                            insertInto = c;
                            Cursor = UpArrowCursor;
                            break;
                        }
                    }
                }
            }
            InvalidateVisual();
        }

        void BeginConnect(IMachine machine, Point p, PointerPressedEventArgs e)
        {
            FocusMachine(machine);
            drag = Drag.Connect;
            connectFrom = machine;
            connectTo = null;
            connectPoint = p;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        void DragConnect(Point p)
        {
            connectPoint = p;
            var target = MachineAt(p) ?? InPortAt(p);
            connectRefused = null;
            connectWhy = null;
            if (target == null || target == connectFrom)
            {
                connectTo = null;
                Cursor = Cursor.Default;
            }
            else if (core.SongCore.CanConnectMachines(connectFrom, target))
            {
                connectTo = target;
                Cursor = UpArrowCursor;
            }
            else
            {
                connectTo = null;
                connectRefused = target;
                connectWhy = MachineEdits.WhyNotConnect(core, connectFrom, target);
                Cursor = NoCursor;
            }
            InvalidateVisual();
        }

        void BeginSelect(Point p, bool add, PointerPressedEventArgs e)
        {
            drag = Drag.Select;
            dragOrigin = p;
            bandBase = add ? new HashSet<IMachine>(selected) : new HashSet<IMachine>();
            band = new Rect(p, p);
            selected.Clear();
            foreach (var m in Machines)
                if (bandBase.Contains(m) || BoxOf(m).Contains(p)) selected.Add(m);
            e.Pointer.Capture(this);
            InvalidateVisual();
            PublishSelection();
        }

        void DragSelect(Point p)
        {
            band = new Rect(Math.Min(p.X, dragOrigin.X), Math.Min(p.Y, dragOrigin.Y), Math.Abs(p.X - dragOrigin.X), Math.Abs(p.Y - dragOrigin.Y));
            selected.Clear();
            foreach (var m in Machines)
                if (bandBase.Contains(m) || BoxOf(m).Intersects(band)) selected.Add(m);
            InvalidateVisual();
            PublishSelection();
        }

        void BeginPan(Point view, PointerPressedEventArgs e)
        {
            drag = Drag.Pan;
            dragLast = view;
            panStart = offset;
            Cursor = PanCursor;
            e.Pointer.Capture(this);
        }

        void EndDrag(bool commit)
        {
            var was = drag;
            drag = Drag.None;
            Cursor = Cursor.Default;
            if (was == Drag.Move)
            {
                var moved = dragPositions.Select(kv => (kv.Key, (float)kv.Value.X, (float)kv.Value.Y)).ToList();
                var into = insertInto;
                insertInto = null;
                moveStart.Clear();
                if (commit && into != null && moved.Count == 1)
                    MachineEdits.MoveIntoWire(core, moved[0].Key, moved[0].Item2, moved[0].Item3, into);
                else if (moveDelta.X != 0 || moveDelta.Y != 0)
                    MachineEdits.Move(core, moved);
                dragPositions.Clear();
            }
            else if (was == Drag.Connect)
            {
                var from = connectFrom;
                var to = connectTo;
                var refused = connectRefused;
                string why = connectWhy;
                connectFrom = connectTo = connectRefused = null;
                connectWhy = null;
                if (commit && to != null) MachineEdits.Connect(core, from, to);
                else if (commit && refused != null && why != null)
                {
                    // P3d: a refused drop says why, on the canvas, for two seconds; the song is untouched.
                    Refusal = why;
                    refusalAt = BoxOf(refused).Center;
                    string shown = why;
                    DispatcherTimer.RunOnce(() => { if (Refusal == shown) { Refusal = null; InvalidateVisual(); } }, TimeSpan.FromSeconds(2));
                }
            }
            InvalidateVisual();
        }

        #endregion

        #region Commands

        // The view's shortcuts call these (MachineView's key bindings); the shell's Edit menu can too.

        IReadOnlyList<IMachine> SelectedNow()
        {
            var shown = Machines.ToHashSet();
            return selected.Where(shown.Contains).ToList();
        }

        /// <summary>Delete (Backspace on a Mac): the selected machines (MachineView.DeleteSelectedCommand).</summary>
        public void DeleteSelection()
        {
            var sel = SelectedNow();
            if (sel.Count > 0) MachineEdits.Delete(core, sel);
        }

        /// <summary>Ctrl+M (M on a Mac): mutes the selection, or unmutes it if the first is muted (MachineControl.MuteCommand).</summary>
        public void ToggleMuteSelection()
        {
            var sel = SelectedNow();
            if (sel.Count > 0) MachineEdits.Mute(core, sel, !sel[0].IsMuted);
        }

        /// <summary>Ctrl+U.</summary>
        public void UnmuteAll() => MachineEdits.UnmuteAll(core);

        /// <summary>S (ux-overhaul.md §4): solos the selected generators, or unsolos them if the first is soloed.</summary>
        public void ToggleSoloSelection()
        {
            var sel = SelectedNow().Where(m => m.DLL.Info.Type == MachineType.Generator && !m.IsControlMachine).ToList();
            if (sel.Count > 0) MachineEdits.Solo(core, sel, !sel[0].IsSoloed);
        }

        /// <summary>B: bypasses the selected effects, or brings them back if the first is bypassed.</summary>
        public void ToggleBypassSelection()
        {
            var sel = SelectedNow().Where(m => m.DLL.Info.Type == MachineType.Effect && !m.IsControlMachine).ToList();
            if (sel.Count > 0) MachineEdits.Bypass(core, sel, !sel[0].IsBypassed);
        }

        /// <summary>
        /// ⌘D (ux-overhaul.md §4): the selected machines again, with their settings, a little below and to the right, one
        /// undo step; the copies are selected. Not the Master.
        /// </summary>
        public bool DuplicateSelection()
        {
            var sel = SelectedNow().Where(m => m.DLL.Info.Type != MachineType.Master).ToList();
            if (sel.Count == 0) return false;
            var copies = MachineEdits.Duplicate(core, sel);
            if (copies.Count > 0) Select(copies);
            return true;
        }

        /// <summary>⌘R: renames the selected machine (one).</summary>
        public bool RenameSelection()
        {
            var sel = SelectedNow();
            if (sel.Count != 1) return false;
            MachineEdits.Rename(sel[0]);
            return true;
        }

        #endregion
    }
}
