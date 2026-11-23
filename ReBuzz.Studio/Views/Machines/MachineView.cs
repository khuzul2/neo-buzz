// Fork (docs/specs/studio-mvp.md S3): the studio's machine view, after BuzzGUI.MachineView: a top
// bar with the zoom slider, the code-drawn canvas (MachineCanvas) with its scroll bars, and the
// amp control above it. The WPF view's sidebar (machine list, parameters, templates) isn't here:
// machines come from the canvas's context menu.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Machines
{
    using Control = Avalonia.Controls.Control;

    /// <summary>The "Machines" tab.</summary>
    internal sealed class MachinesTab : IStudioView
    {
        public string Title => "Machines";

        public Control Create(StudioHost host) => new MachineView(host.Core, host.Registry);
    }

    internal sealed class MachineView : DockPanel, IStudioEditTarget
    {
        readonly ScrollBar horizontal = new ScrollBar { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, AllowAutoHide = true };
        readonly ScrollBar vertical = new ScrollBar { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, AllowAutoHide = true };
        readonly Slider zoom = new Slider { Minimum = -MachineCanvas.ZoomLevels, Maximum = MachineCanvas.ZoomLevels, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 100, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        bool syncing;

        readonly Views.Browser.MachineBrowserPopup popup;
        readonly Border starters;
        readonly Button tracksButton;
        readonly TrackBar trackBar;
        readonly TrackCard card;

        public MachineView(ReBuzzCore core, IRegistryEx registry = null)
        {
            Core = core;
            Model = Views.Browser.MachineBrowserModel.For(core, registry ?? StudioHost.Current?.Registry);
            Canvas = new MachineCanvas(core);
            Menus = new MachineMenus(core, Canvas);
            Amp = new AmpControl(core) { InsertMenu = Menus.ForInsert };
            Canvas.Menus = Menus;
            Canvas.Amp = Amp;

            // MT1 (ux-overhaul.md §11): the Tracks toggle, the bar of chips under the top bar, and the card of the lit track.
            tracksButton = Shell.Design.StudioButtons.KeepFocus(new Button { Content = "Tracks", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1), Padding = new Thickness(10, 3), FontSize = Shell.Design.StudioTokens.FontBody });
            Avalonia.Automation.AutomationProperties.SetName(tracksButton, "Tracks");
            ToolTip.SetTip(tracksButton, "Colour the machines by track: a generator and what only it feeds, up to the Master or a shared machine. Hover or click a chip to light one track");
            tracksButton.Click += (s, e) => MachineTracksPreference.On = !MachineTracksPreference.On;
            trackBar = new TrackBar(core, Canvas);
            card = new TrackCard(core, Canvas);
            Canvas.LitChanged += UpdateCard;
            Canvas.ViewChanged += UpdateCard;
            Canvas.LevelsTicked += card.Refresh;

            // The top bar (TopBorderStyle; 32 px, for Fluent's slider) with the zoom slider on the right.
            var bar = new Border
            {
                Height = 32,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Shell.Design.StudioTokens.SurfaceBorderBrush,
                Child = new DockPanel { LastChildFill = false, Children = { Left(tracksButton), Right(zoom) } },
            };
            SetDock(bar, Dock.Top);
            Children.Add(bar);
            SetDock(trackBar, Dock.Top);
            Children.Add(trackBar);

            // The canvas fills the view; the amp control's layer is above it (WPF's ampCanvas).
            Canvas.Browser = Model;
            popup = new Views.Browser.MachineBrowserPopup(Model);
            var layer = new Avalonia.Controls.Canvas { Children = { Amp, popup, card } };
            starters = BuildStarters();
            Children.Add(new Panel { Children = { Canvas, starters, horizontal, vertical, layer } });
            Canvas.BrowseRequested += OnBrowse;
            Canvas.PointerPressed += (s, e) => popup.Hide();
            Canvas.MachinesChanged += UpdateStarters;
            Canvas.ViewChanged += SchedulePlacing;
            // The canvas's size is taken (its map made and its offset set) in its own layout, after Bounds has changed: place again once
            // the layout pass is done (a placement made inside it measured with the old map, and one made in a layout handler loops).
            Canvas.PropertyChanged += (s, e) => { if (e.Property == BoundsProperty) SchedulePlacing(); };
            popup.Added += m => { Canvas.Select(new[] { m }); Canvas.Focus(); };
            popup.Closed += () => { UpdateStarters(); Canvas.Focus(); };
            AddHandler(KeyDownEvent, (s, e) => { if (e.Key == Key.Escape && popup.IsOpen) { popup.Hide(); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            UpdateStarters();

            AddKeys();
            Canvas.ViewChanged += Sync;
            horizontal.ValueChanged += (s, e) => { if (!syncing) Canvas.ScrollTo(new Vector(e.NewValue, Canvas.Offset.Y)); };
            vertical.ValueChanged += (s, e) => { if (!syncing) Canvas.ScrollTo(new Vector(Canvas.Offset.X, e.NewValue)); };
            zoom.ValueChanged += (s, e) => { if (!syncing) Canvas.SetZoom((int)Math.Round(e.NewValue)); };
        }

        /// <summary>
        /// The view's shortcuts as its own key bindings (MachineView.Commands' input bindings), so they
        /// work wherever the focus is inside it. Ctrl is Command on a Mac.
        /// </summary>
        void AddKeys()
        {
            var cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            void Bind(Key key, KeyModifiers mods, Action run) => KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, mods), Command = new Command(run) });
            // Fork (ux-overhaul.md §4): one keymap: ⌫ and Delete delete, ⌘D duplicates (it deselected), ⇧⌘A and Esc deselect.
            Bind(Key.Delete, KeyModifiers.None, Canvas.DeleteSelection);
            Bind(Key.Back, KeyModifiers.None, Canvas.DeleteSelection);
            Bind(Key.A, cmd, Canvas.SelectAll);
            Bind(Key.D, cmd, () => Canvas.DuplicateSelection());
            Bind(Key.A, cmd | KeyModifiers.Shift, Canvas.ClearSelection);
            Bind(Key.Escape, KeyModifiers.None, Canvas.ClearSelection);
            Bind(Key.R, cmd, () => Canvas.RenameSelection());
            Bind(Key.U, cmd, Canvas.UnmuteAll);
            // Mute: M on the canvas in both apps (on a Mac ⌘M minimizes the window), and Ctrl+M as ReBuzz.exe has it off a
            // Mac; S solos, B bypasses.
            // Fork (decision 58): letters always keep their shortcuts; the browser opens by a double click on empty canvas or Add Machine….
            Canvas.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.M), Command = new Command(Canvas.ToggleMuteSelection) });
            Canvas.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S), Command = new Command(Canvas.ToggleSoloSelection) });
            Canvas.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.B), Command = new Command(Canvas.ToggleBypassSelection) });
            if (!OperatingSystem.IsMacOS())
                Bind(Key.M, cmd, Canvas.ToggleMuteSelection);
        }

        // ------------------------------------------------------------------ the machine browser (P3d)

        /// <summary>The model the left panel's browser shares.</summary>
        public Views.Browser.MachineBrowserModel Model { get; }

        public Views.Browser.MachineBrowserPopup Popup => popup;

        /// <summary>Add Machine… (the dedicated key): the browser opens in the middle of what is shown.</summary>
        public void OpenBrowser()
        {
            var view = new Point(Math.Max(0, Canvas.Bounds.Width / 2 - Views.Browser.MachineBrowserPopup.PopupWidth / 2), Math.Max(0, Canvas.Bounds.Height / 3));
            OnBrowse(Canvas.ToLogical(view), view, "");
        }

        /// <summary>A double click on empty canvas, or Add Machine…: the browser opens at that point (clamped into the view).</summary>
        void OnBrowse(Point logical, Point view, string seed)
        {
            var at = CanvasMap.Clamp(Canvas.Map.PositionAt(logical));
            double x = Math.Clamp(view.X, 0, Math.Max(0, Canvas.Bounds.Width - Views.Browser.MachineBrowserPopup.PopupWidth));
            double y = Math.Clamp(view.Y + 36, 36, Math.Max(36, Canvas.Bounds.Height - Views.Browser.MachineBrowserPopup.PopupHeight + 36));
            Avalonia.Controls.Canvas.SetLeft(popup, x);
            Avalonia.Controls.Canvas.SetTop(popup, y - 36);
            popup.Open(at, seed);
            UpdateStarters();
        }

        /// <summary>The empty state: a song with only its Master offers the three starters, one click each.</summary>
        Border BuildStarters()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var name in Views.Browser.MachineBrowserModel.StarterNames)
            {
                var b = new Button { Content = name, Tag = name };
                b.Classes.Add("large");
                Avalonia.Automation.AutomationProperties.SetName(b, "Start with " + name);
                b.Click += (s, e) => AddStarter(name);
                row.Children.Add(b);
            }
            var panel = new StackPanel { Spacing = 10, MaxWidth = 460 };
            panel.Children.Add(new TextBlock { Text = "Start with a sound", FontSize = 15, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Shell.Design.StudioTokens.TextPrimaryBrush, HorizontalAlignment = HorizontalAlignment.Center });
            panel.Children.Add(row);
            startersHint = new TextBlock
            {
                Text = $"Or double-click the canvas, or press {Shell.Commands.StudioKeymap.Get("view.addmachine").KeyLabel()}, to search every machine: bass, reverb, compressor…",
                FontSize = 12,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Foreground = Shell.Design.StudioTokens.TextSecondaryBrush,
            };
            panel.Children.Add(startersHint);
            return new Border { Child = panel, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsVisible = false };
        }

        TextBlock startersHint;

        /// <summary>The empty state's place on the canvas, P3j fix (mac-018, 019, 029): never over a machine box, at any window size.</summary>
        public const double StartersGap = 16;

        /// <summary>
        /// The starters sit where no machine box is (a box and the gap round it are kept clear): the middle of the view when it
        /// is free, else the nearest free place; when the view is too small for the whole of it the hint line goes, and when
        /// even the buttons don't fit it stays hidden (the popup and the buttons in the panels still add a machine).
        /// </summary>
        bool placing;

        void SchedulePlacing()
        {
            if (placing) return;
            placing = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { placing = false; PlaceStarters(); }, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        internal void PlaceStarters()
        {
            if (!starters.IsVisible) return;
            double w = Canvas.Bounds.Width, h = Canvas.Bounds.Height;
            if (w <= 0 || h <= 0) return;
            var boxes = new System.Collections.Generic.List<Rect>();
            foreach (var m in Canvas.Machines)
            {
                var b = Canvas.BoxOf(m);
                var tl = Canvas.ToView(b.TopLeft);
                boxes.Add(new Rect(tl.X - StartersGap, tl.Y - StartersGap, b.Width * Canvas.Zoom + 2 * StartersGap, b.Height * Canvas.Zoom + 2 * StartersGap));
            }
            startersHint.IsVisible = true;
            starters.Margin = default;                  // measured as it is, not with the margin of the last placement
            for (int attempt = 0; attempt < 2; attempt++)
            {
                starters.InvalidateMeasure();           // the hint's visibility changed what it needs
                starters.Measure(new Size(w, h));
                var size = starters.DesiredSize;
                if (size.Width <= w && size.Height <= h)
                {
                    var best = FreeSpot(size, w, h, boxes);
                    if (best != null)
                    {
                        starters.Margin = new Thickness(best.Value.X, best.Value.Y, 0, 0);
                        starters.Opacity = 1;
                        starters.IsHitTestVisible = true;
                        return;
                    }
                }
                startersHint.IsVisible = false;
            }
            starters.Opacity = 0;       // no room: hidden, and not clickable
            starters.IsHitTestVisible = false;
        }

        /// <summary>The free place for a block of this size nearest the middle of the view, or null.</summary>
        internal static Point? FreeSpot(Size size, double w, double h, System.Collections.Generic.List<Rect> boxes)
        {
            double cx = (w - size.Width) / 2, cy = (h - size.Height) / 2;
            Point? best = null;
            double bestDistance = double.MaxValue;
            // Candidates: the middle, and the places against each box's sides and the view's edges (the free place nearest the
            // middle is always one of these corners or edges).
            var xs = new System.Collections.Generic.List<double> { cx, 0, w - size.Width };
            var ys = new System.Collections.Generic.List<double> { cy, 0, h - size.Height };
            foreach (var b in boxes)
            {
                xs.Add(b.Left - size.Width); xs.Add(b.Right);
                ys.Add(b.Top - size.Height); ys.Add(b.Bottom);
            }
            foreach (double x0 in xs)
                foreach (double y0 in ys)
                {
                    double x = Math.Clamp(x0, 0, w - size.Width), y = Math.Clamp(y0, 0, h - size.Height);
                    var r = new Rect(x, y, size.Width, size.Height);
                    if (boxes.Any(b => b.Intersects(r))) continue;
                    double d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if (d < bestDistance) { bestDistance = d; best = new Point(x, y); }
                }
            return best;
        }

        internal Border Starters => starters;

        // The model lives as long as the engine: the view hears it only while it is shown (a closed window is released).
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Model.Changed += UpdateStarters;
            popup.Attach();
            UpdateStarters();
            MachineTracksPreference.Changed += ApplyTracks;
            ApplyTracks(MachineTracksPreference.On);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Model.Changed -= UpdateStarters;
            popup.Detach();
            MachineTracksPreference.Changed -= ApplyTracks;
        }

        // ------------------------------------------------------------------ Tracks (MT1)

        /// <summary>The Tracks toggle changed (here or in another Machines view): the canvas, the bar and the button follow.</summary>
        void ApplyTracks(bool on)
        {
            Canvas.TracksOn = on;
            trackBar.IsVisible = on;
            if (on) trackBar.Rebuild();
            tracksButton.Background = on ? Shell.Design.StudioTokens.SelectionFillBrush : Avalonia.Media.Brushes.Transparent;
            tracksButton.BorderBrush = on ? Shell.Design.StudioTokens.AccentBrush : Avalonia.Media.Brushes.Transparent;
            tracksButton.Foreground = on ? Shell.Design.StudioTokens.TextPrimaryBrush : Shell.Design.StudioTokens.TextSecondaryBrush;
            UpdateCard();
        }

        /// <summary>
        /// The card follows what is lit. It sits in a fixed spot that covers no box and no wire: a corner of the canvas (the one nearest the top
        /// left that is free), then the edges and the middle of the sides; with no free spot, the one that covers least. It never follows the path.
        /// </summary>
        void UpdateCard()
        {
            var entry = Canvas.TracksOn ? Canvas.LitEntry : null;
            card.Show(entry);
            if (entry?.Head.Tag is not IMachine) return;
            // One width and one reference height: the spot does not depend on the text, so it does not move from track to track.
            card.Width = 280;
            var size = new Size(280, 150);
            double w = Canvas.Bounds.Width, h = Canvas.Bounds.Height;
            const double Gap = 8;
            var boxes = new List<Rect>();
            foreach (var m in Canvas.Machines)
            {
                var b = Canvas.BoxOf(m);
                boxes.Add(new Rect(Canvas.ToView(b.TopLeft), Canvas.ToView(b.BottomRight)).Inflate(4));
            }
            var wires = new List<(Point, Point)>();
            foreach (var c in Canvas.Wires)
            {
                var from = Canvas.BoxOf(c.Source);
                var to = Canvas.BoxOf(c.Destination);
                wires.Add((Canvas.ToView(from.Center), Canvas.ToView(to.Center)));
            }
            double xs = Math.Max(Gap, w - size.Width - Gap), ys = Math.Max(Gap, h - size.Height - Gap);
            var spots = new List<Point>
            {
                new Point(Gap, ys), new Point(xs, ys), new Point(xs, Gap), new Point(Gap, Gap),
                new Point((xs + Gap) / 2, ys), new Point((xs + Gap) / 2, Gap), new Point(Gap, ys / 2), new Point(xs, ys / 2),
            };
            // Then every free place on a coarse grid, from the bottom left up.
            for (double gy = ys; gy >= Gap; gy -= 24)
                for (double gx = Gap; gx <= xs; gx += 24)
                    spots.Add(new Point(gx, gy));
            Point best = spots[0];
            double bestCost = double.MaxValue;
            foreach (var spot in spots)
            {
                var r = new Rect(spot, size);
                double cost = 0;
                foreach (var b in boxes)
                {
                    var i = r.Intersect(b);
                    if (i.Width > 0 && i.Height > 0) cost += 1000 + i.Width * i.Height;
                }
                foreach (var (a, z) in wires)
                    if (SegmentHits(r, a, z)) cost += 500;
                if (cost < bestCost) { bestCost = cost; best = spot; }
                if (cost == 0) break;
            }
            double x = best.X, y = best.Y;
            Avalonia.Controls.Canvas.SetLeft(card, x);
            Avalonia.Controls.Canvas.SetTop(card, y);
        }

        /// <summary>A line from a to z crosses the rectangle (it is inside it, or crosses one of its sides).</summary>
        static bool SegmentHits(Rect r, Point a, Point z)
        {
            if (r.Contains(a) || r.Contains(z)) return true;
            bool Cross(Point p1, Point p2, Point p3, Point p4)
            {
                double d(Point p, Point q, Point t) => (q.X - p.X) * (t.Y - p.Y) - (q.Y - p.Y) * (t.X - p.X);
                return d(p1, p2, p3) * d(p1, p2, p4) < 0 && d(p3, p4, p1) * d(p3, p4, p2) < 0;
            }
            return Cross(a, z, r.TopLeft, r.TopRight) || Cross(a, z, r.TopRight, r.BottomRight) || Cross(a, z, r.BottomRight, r.BottomLeft) || Cross(a, z, r.BottomLeft, r.TopLeft);
        }

        /// <summary>The Tracks toggle (tests).</summary>
        public Button TracksButton => tracksButton;

        /// <summary>The chips of the tracks, groups and returns, and Unrouted.</summary>
        public TrackBar Tracks => trackBar;

        /// <summary>The card of the lit track.</summary>
        public TrackCard Card => card;

        void UpdateStarters()
        {
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStarters);
                return;
            }
            starters.IsVisible = Model.SongIsEmpty && !popup.IsOpen;
            PlaceStarters();
            SchedulePlacing();
        }

        /// <summary>One click: the starter machine near the middle of the view (one undo step; it is selected).</summary>
        internal IMachine AddStarter(string name)
        {
            var entry = Model.Find(name);
            if (entry == null) return null;
            var centre = Canvas.ToLogical(new Point(Canvas.Bounds.Width / 2, Canvas.Bounds.Height / 2));
            var at = CanvasMap.Clamp(Canvas.Map.PositionAt(centre + new Vector(-170, -70)));
            var made = Model.Add(entry, (float)at.X, (float)at.Y);
            if (made != null) Canvas.Select(new[] { made });
            UpdateStarters();
            return made;
        }

        // IStudioEditTarget (ux-overhaul.md §4): the Edit menu's commands on the selected machines. Machine edits are on the
        // song's history, so Undo and Redo fall through to it.
        public bool Undo() => false;
        public bool Redo() => false;
        public bool Duplicate() => Canvas.DuplicateSelection();
        public bool Cut() => Canvas.CutSelection();
        public bool Copy() => Canvas.CopySelection();
        public bool Paste() => Canvas.PasteSelection();
        public bool Rename() => Canvas.RenameSelection();

        public bool Delete()
        {
            if (Canvas.Selection.Count == 0) return false;
            Canvas.DeleteSelection();
            return true;
        }

        public bool SelectAll()
        {
            Canvas.SelectAll();
            return true;
        }

        public bool DeselectAll()
        {
            if (Canvas.Selection.Count == 0) return false;
            Canvas.ClearSelection();
            return true;
        }

        static Control Right(Control c)
        {
            DockPanel.SetDock(c, Dock.Right);
            return c;
        }

        static Control Left(Control c)
        {
            DockPanel.SetDock(c, Dock.Left);
            return c;
        }

        public ReBuzzCore Core { get; }
        public MachineCanvas Canvas { get; }
        public MachineMenus Menus { get; }
        public AmpControl Amp { get; }
        public Slider ZoomSlider => zoom;

        /// <summary>The scroll bars and the slider follow the canvas.</summary>
        void Sync()
        {
            syncing = true;
            try
            {
                var extent = Canvas.Extent;
                var view = Canvas.Bounds.Size;
                horizontal.Maximum = Math.Max(0, extent.Width - view.Width);
                horizontal.ViewportSize = view.Width;
                horizontal.Value = Canvas.Offset.X;
                vertical.Maximum = Math.Max(0, extent.Height - view.Height);
                vertical.ViewportSize = view.Height;
                vertical.Value = Canvas.Offset.Y;
                zoom.Value = Canvas.ZoomLevel;
            }
            finally
            {
                syncing = false;
            }
        }

        /// <summary>A machine's centre in another visual's pixels (tests point the mouse with it).</summary>
        public Point PointOf(IMachine machine, Visual relativeTo) => FromCanvas(Canvas.BoxOf(machine).Center, relativeTo);

        /// <summary>A song position in another visual's pixels.</summary>
        public Point PointOf(double x, double y, Visual relativeTo) => FromCanvas(Canvas.Map.PointAt(x, y), relativeTo);

        /// <summary>A wire's arrow in another visual's pixels.</summary>
        public Point ArrowOf(IMachineConnection wire, Visual relativeTo) => FromCanvas(Canvas.WireOf(wire).Mid, relativeTo);

        public Point FromCanvas(Point logical, Visual relativeTo) => Canvas.TranslatePoint(Canvas.ToView(logical), relativeTo) ?? default;
    }
}
