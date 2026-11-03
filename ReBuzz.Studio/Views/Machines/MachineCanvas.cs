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
        double wheelZoom;       // wheel notches not yet turned into a level

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

        public MachineCanvas(ReBuzzCore core)
        {
            this.core = core;
            Focusable = true;
            ClipToBounds = true;
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
            Watch(core.SongCore);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            Watch(null);
            base.OnDetachedFromVisualTree(e);
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
        }

        void WatchMachine(IMachine m)
        {
            if (watched.Add(m)) m.PropertyChanged += OnMachineChanged;
        }

        void OnMachineAdded(IMachine m)
        {
            WatchMachine(m);
            Redraw();
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
                    Redraw();
                    break;
            }
        }

        void Redraw()
        {
            if (Dispatcher.UIThread.CheckAccess()) InvalidateVisual();
            else Dispatcher.UIThread.Post(InvalidateVisual);
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
                new GradientStop(Colors.WhiteSmoke, 0.2),
                new GradientStop(Colors.Transparent, 0.4),
                new GradientStop(Colors.WhiteSmoke, 0.5),
                new GradientStop(Colors.Transparent, 0.75),
                new GradientStop(Colors.WhiteSmoke, 0.9),
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
                    DrawWire(ctx, WireOf(c), colours, line);

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

                if (drag == Drag.Select)
                    ctx.DrawRectangle(BandFill, BandPen, band, 2, 2);
            }
        }

        static void DrawWire(DrawingContext ctx, WireShape w, Palette colours, IPen line, bool arrow = true)
        {
            if (!w.Visible) return;
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

            // Shadow and body (MachineBackgroundColor = IMachine.GetThemeColor), border (MV Machine Border).
            ctx.DrawRectangle(new SolidColorBrush(BackgroundOf(m, colours)), null, box, 0, 0, Shadow);
            ctx.DrawRectangle(null, new Pen(colours["MV Machine Border"], 1), box.Deflate(0.5));

            // Mode: .NET / 32, the MIDI channel, oversampling (ModeText), Arial 8 at 13,2.
            string mode = ModeText(m);
            if (mode != null)
                ctx.DrawText(new FormattedText(mode, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, PlainFace, 8, text), new Point(box.X + 13, box.Y + 2));

            // Missing or crashed (ErrorText): dark red at the bottom.
            string error = m.DLL.IsMissing ? "missing" : m.DLL.IsCrashed ? "crashed" : null;
            if (error != null)
            {
                var ft = new FormattedText(error, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NameFace, 15, Brushes.DarkRed);
                ctx.DrawText(ft, new Point(box.X + (box.Width - ft.Width) / 2, box.Bottom - ft.Height));
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
                var pen = new Pen(hoverDelete ? Brushes.White : Brushes.Black, 2) { LineCap = PenLineCap.Round };
                ctx.DrawLine(pen, r.TopLeft, r.BottomRight);
                ctx.DrawLine(pen, r.BottomLeft, r.TopRight);
            }

            if (selected.Contains(m))
                ctx.FillRectangle(Glass, box);
        }

        /// <summary>IMachine.GetThemeColor (BuzzGUI.Common): by type, darker when muted.</summary>
        static Color BackgroundOf(IMachine m, Palette colours)
        {
            if (m.IsControlMachine) return colours.Colour("MV Control");
            return m.DLL.Info.Type switch
            {
                MachineType.Master => colours.Colour("MV Master"),
                MachineType.Generator => colours.Colour(m.IsMuted ? "MV Generator Mute" : "MV Generator"),
                MachineType.Effect => colours.Colour(m.IsMuted ? "MV Effect Mute" : "MV Effect"),
                _ => Colors.LightCyan,
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
            return kind == null ? Colors.LightCyan : colours.Colour($"MV {kind} LED {(on ? "On" : "Off")}");
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
            else if (m.DLL.IsManaged) s += ".NET ";
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
            foreach (var m in machines) selected.Add(m);
            InvalidateVisual();
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
            var machine = MachineAt(p);
            var wire = machine == null ? WireAt(p) : null;
            e.Handled = true;
            if (drag != Drag.None) return;
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

            if (mods.HasFlag(KeyModifiers.Shift)) BeginPan(view, e);
            else BeginSelect(p, add: mods.HasFlag(CommandKey), e);
        }

        Point TranslateToAmp(Point view) => Amp?.Parent is Visual parent ? this.TranslatePoint(view, parent) ?? view : view;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var view = e.GetPosition(this);
            var p = ToLogical(view);
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
                    var m = MachineAt(p);
                    bool overDelete = m != null && MachineGeometry.DeleteButton(BoxOf(m)).Contains(p);
                    if (m != hover || overDelete != hoverDelete)
                    {
                        hover = m;
                        hoverDelete = overDelete;
                        InvalidateVisual();
                    }
                    break;
            }
        }

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
            var target = MachineAt(p);
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
        }

        void DragSelect(Point p)
        {
            band = new Rect(Math.Min(p.X, dragOrigin.X), Math.Min(p.Y, dragOrigin.Y), Math.Abs(p.X - dragOrigin.X), Math.Abs(p.Y - dragOrigin.Y));
            selected.Clear();
            foreach (var m in Machines)
                if (bandBase.Contains(m) || BoxOf(m).Intersects(band)) selected.Add(m);
            InvalidateVisual();
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
                connectFrom = connectTo = null;
                if (commit && to != null) MachineEdits.Connect(core, from, to);
            }
            InvalidateVisual();
        }

        #endregion

        #region Keys

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var mods = e.KeyModifiers;
            bool cmd = mods == CommandKey;
            var sel = selected.Where(Machines.Contains).ToList();
            switch (e.Key)
            {
                case Key.Delete:
                case Key.Back when OperatingSystem.IsMacOS():
                    if (mods == KeyModifiers.None && sel.Count > 0)
                    {
                        MachineEdits.Delete(core, sel);
                        e.Handled = true;
                    }
                    break;
                case Key.A when cmd:
                    SelectAll();
                    e.Handled = true;
                    break;
                case Key.D when cmd:
                    ClearSelection();
                    e.Handled = true;
                    break;
                case Key.U when cmd:
                    MachineEdits.UnmuteAll(core);
                    e.Handled = true;
                    break;
                case Key.M when cmd:
                    if (sel.Count > 0) MachineEdits.Mute(core, sel, !sel[0].IsMuted);
                    e.Handled = true;
                    break;
            }
        }

        #endregion
    }
}
