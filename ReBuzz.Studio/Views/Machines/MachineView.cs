// Fork (docs/specs/studio-mvp.md S3): the studio's machine view, after BuzzGUI.MachineView: a top
// bar with the zoom slider, the code-drawn canvas (MachineCanvas) with its scroll bars, and the
// amp control above it. The WPF view's sidebar (machine list, parameters, templates) isn't here:
// machines come from the canvas's context menu.

using System;
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

        public MachineView(ReBuzzCore core, IRegistryEx registry = null)
        {
            Core = core;
            Model = Views.Browser.MachineBrowserModel.For(core, registry ?? StudioHost.Current?.Registry);
            Canvas = new MachineCanvas(core);
            Menus = new MachineMenus(core, Canvas);
            Amp = new AmpControl(core) { InsertMenu = Menus.ForInsert };
            Canvas.Menus = Menus;
            Canvas.Amp = Amp;

            // The top bar (TopBorderStyle; 32 px, for Fluent's slider) with the zoom slider on the right.
            var bar = new Border
            {
                Height = 32,
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Shell.Design.StudioTokens.SurfaceBorderBrush,
                Child = new DockPanel { LastChildFill = false, Children = { Right(zoom) } },
            };
            SetDock(bar, Dock.Top);
            Children.Add(bar);

            // The canvas fills the view; the amp control's layer is above it (WPF's ampCanvas).
            popup = new Views.Browser.MachineBrowserPopup(Model);
            var layer = new Avalonia.Controls.Canvas { Children = { Amp, popup } };
            starters = BuildStarters();
            Children.Add(new Panel { Children = { Canvas, starters, horizontal, vertical, layer } });
            Canvas.BrowseRequested += OnBrowse;
            Canvas.PointerPressed += (s, e) => popup.Hide();
            Canvas.MachinesChanged += UpdateStarters;
            Canvas.ViewChanged += PlaceStarters;
            Canvas.PropertyChanged += (s, e) => { if (e.Property == BoundsProperty) PlaceStarters(); };
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
                var b = new Button { Content = name, FontSize = 13, Padding = new Thickness(14, 6), Tag = name };
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
            for (int attempt = 0; attempt < 2; attempt++)
            {
                starters.Measure(new Size(w, h));
                var size = starters.DesiredSize;
                if (size.Width <= w && size.Height <= h)
                {
                    var best = FreeSpot(size, w, h, boxes);
                    if (best != null)
                    {
                        starters.Margin = new Thickness(best.Value.X, best.Value.Y, 0, 0);
                        starters.Opacity = 1;
                        return;
                    }
                }
                startersHint.IsVisible = false;
            }
            starters.Opacity = 0;       // no room: hidden, and not clickable
            starters.Margin = new Thickness(-10000, 0, 0, 0);
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
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Model.Changed -= UpdateStarters;
            popup.Detach();
        }

        void UpdateStarters()
        {
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStarters);
                return;
            }
            starters.IsVisible = Model.SongIsEmpty && !popup.IsOpen;
            PlaceStarters();
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
