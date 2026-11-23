// Fork (docs/specs/midi-mapping.md, MM-A and MM-B): the layer over the main window that draws MIDI mapping on the controls under it, so no view
// needs to know about it. Every control marked mappable (MidiMap.SetTarget) gets
//  - a badge when it is mapped ("K3", "Pad 5", "CC 21 · Name"; right-click: edit or unmap),
//  - a small MIDI button while the pointer is over it (opens the popover: scope, range, takeover, listening),
//  - in map mode an outline (accent for the control waiting for a controller, with "Listening…"), and the map-mode banner.
// Out of map mode the layer is not hit-testable: the window's own pointer handlers (tunnelled) feed it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using ReBuzz.Core;
using ReBuzz.Midi.Mapping;
using ReBuzz.Studio.Shell.Design;
using AvControl = Avalonia.Controls.Control;

namespace ReBuzz.Studio.Views.Midi
{
    internal sealed class MapLayer : Grid
    {
        /// <summary>A mappable control now: where it is on the layer, what it maps to and what badge it shows.</summary>
        internal sealed record Spot(AvControl Control, Rect Bounds, MapTarget Target, string Badge);

        const double ButtonW = 34, ButtonH = 15;

        readonly ReBuzzCore core;
        readonly MapMode mode;
        readonly Visual scan;
        readonly Surface surface;
        readonly Border banner;
        readonly TextBlock bannerText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 760 };
        readonly RadioButton trackScope = new RadioButton { GroupName = "MapScope", VerticalAlignment = VerticalAlignment.Center };
        readonly RadioButton globalScope = new RadioButton { GroupName = "MapScope", Content = "Global", VerticalAlignment = VerticalAlignment.Center };
        readonly Button done = new Button { Content = "Done", VerticalAlignment = VerticalAlignment.Center };
        readonly List<Spot> spots = new List<Spot>();
        AvControl hover;
        bool dirty = true;
        bool updating;
        MapPopover popover;

        public MapLayer(ReBuzzCore core, Visual scan)
        {
            this.core = core;
            this.scan = scan;
            mode = MapMode.For(core);
            IsHitTestVisible = false;
            Background = null;
            ClipToBounds = true;
            AutomationProperties.SetName(this, "MIDI map layer");
            surface = new Surface(this);
            Children.Add(surface);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            AutomationProperties.SetName(trackScope, "Scope: This track");
            AutomationProperties.SetName(globalScope, "Scope: Global");
            AutomationProperties.SetName(done, "End map mode");
            row.Children.Add(bannerText);
            row.Children.Add(trackScope);
            row.Children.Add(globalScope);
            row.Children.Add(done);
            banner = new Border
            {
                Child = row,
                Padding = new Thickness(12, 5),
                BorderThickness = new Thickness(0, 0, 0, 2),
                IsVisible = false,
                BorderBrush = StudioTokens.AccentBrush,
                Background = StudioTokens.SurfacePanelBrush,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(banner, "Map mode");      // the main window docks it under the top bar (Banner); it is not part of the layer
            trackScope.IsCheckedChanged += (s, e) => { if (!updating && trackScope.IsChecked == true) mode.Scope = MapScope.Track; };
            globalScope.IsCheckedChanged += (s, e) => { if (!updating && globalScope.IsChecked == true) mode.Scope = MapScope.Global; };
            done.Click += (s, e) => mode.Exit();
            mode.Changed += OnModeChanged;
            if (core.MidiMapping is { } mm) mm.SelectionChanged += _ => OnModeChanged();
            Sync();
        }

        public MapMode Mode => mode;

        public IReadOnlyList<Spot> Spots => spots;

        public Border Banner => banner;

        /// <summary>The control the pointer is over (a MIDI button is drawn on it), for tests.</summary>
        public AvControl Hovered => hover;

        public MapPopover Popover => popover;

        void OnModeChanged()
        {
            dirty = true;
            Sync();
            surface.InvalidateVisual();
        }

        /// <summary>The window feeds the layer: <paramref name="window"/>'s pointer and keys, before the controls take them (tunnelled).</summary>
        public void Attach(TopLevel window)
        {
            window.AddHandler(InputElement.PointerMovedEvent, OnWindowMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.PointerPressedEvent, OnWindowPressed, RoutingStrategies.Tunnel);
            window.AddHandler(InputElement.KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel);
        }

        // ------------------------------------------------------------------ the spots

        /// <summary>Scans the window for mappable controls (cheap: a walk over the visual tree); the main window calls it with its timer, and map mode does after a change.</summary>
        public void Refresh()
        {
            if (mode.Mapping == null) return;
            bool any = mode.Active || popover != null || mode.Mapping.Active().Count > 0 || hover != null;
            if (!any && spots.Count == 0) return;
            Rescan();
        }

        /// <summary>Scans now.</summary>
        public void Rescan()
        {
            var next = new List<Spot>();
            var mm = mode.Mapping;
            if (mm != null && scan != null)
            {
                foreach (var v in scan.GetVisualDescendants())
                {
                    if (v is not AvControl c || !c.IsVisible || c.Bounds.Width < 4 || c.Bounds.Height < 4) continue;
                    if (c.GetValue(MidiMap.TargetProperty) == null) continue;
                    if (!c.IsEffectivelyVisible) continue;
                    var target = MidiMap.GetTarget(c);
                    if (target == null) continue;
                    var origin = c.TranslatePoint(default, this);
                    if (origin == null) continue;
                    var rect = new Rect(origin.Value, c.Bounds.Size);
                    if (rect.Right < 0 || rect.Bottom < 0 || rect.X > Bounds.Width || rect.Y > Bounds.Height) continue;
                    if (!InsideClips(c, rect)) continue;
                    next.Add(new Spot(c, rect, target, mm.BadgeOf(target)));
                }
            }
            if (!next.SequenceEqual(spots))
            {
                spots.Clear();
                spots.AddRange(next);
                surface.InvalidateVisual();
            }
            dirty = false;
            var under = SpotAt(lastPointer)?.Control;
            if (!ReferenceEquals(under, hover)) { hover = under; surface.InvalidateVisual(); }
        }

        /// <summary>Whether the control's rectangle is in view: not scrolled out of a scroll viewer (or any clipping panel) it sits in.</summary>
        bool InsideClips(AvControl c, Rect rect)
        {
            foreach (var a in c.GetVisualAncestors())
            {
                if (ReferenceEquals(a, scan)) break;
                if (a is not AvControl ac || (ac is not ScrollViewer && !ac.ClipToBounds)) continue;
                var at = ac.TranslatePoint(default, this);
                if (at == null) continue;
                var view = new Rect(at.Value, ac.Bounds.Size);
                if (ac is ScrollViewer sv) view = new Rect(at.Value, sv.Viewport.Width > 0 ? new Size(Math.Min(sv.Viewport.Width, ac.Bounds.Width), Math.Min(sv.Viewport.Height, ac.Bounds.Height)) : ac.Bounds.Size);
                var overlap = view.Intersect(rect);
                if (overlap.Width < rect.Width * 0.6 || overlap.Height < rect.Height * 0.6) return false;
            }
            return true;
        }

        Spot SpotAt(Point p) => spots.LastOrDefault(s => s.Bounds.Contains(p));

        // A control may be rebuilt under its target (the Inspector remakes its cells when it refreshes): the spot of a control is found by it, else by what it maps.
        Spot SpotOf(AvControl control)
        {
            if (spots.FirstOrDefault(s => ReferenceEquals(s.Control, control)) is { } same) return same;
            var target = MidiMap.GetTarget(control);
            return target == null ? null : spots.FirstOrDefault(s => s.Target.Equals(target));
        }

        Point lastPointer;

        Rect ButtonOf(Spot s) => new Rect(s.Bounds.X + 1, s.Bounds.Y + 1, ButtonW, ButtonH);

        Rect BadgeOf(Spot s)
        {
            if (s.Badge == null) return default;
            var t = Text(s.Badge, StudioTokens.FontCaption, StudioTokens.TextPrimaryBrush);
            // On the cell's top edge, half out of it, as wide as its text (the cell's width at most); a small control (a transport button) gets it below.
            bool small = s.Bounds.Width < 64;
            double w = Math.Min(t.Width + 10, small ? 120 : s.Bounds.Width);
            return small ? new Rect(s.Bounds.X, s.Bounds.Bottom - 2, w, 14) : new Rect(s.Bounds.Right - w, s.Bounds.Y - 5, w, 14);
        }

        /// <summary>The badge's rectangle of a control, for tests.</summary>
        internal Rect BadgeRect(AvControl control) => SpotOf(control) is { } s ? BadgeOf(s) : default;

        /// <summary>The MIDI button's rectangle of a control, for tests.</summary>
        internal Rect ButtonRect(AvControl control) => SpotOf(control) is { } s ? ButtonOf(s) : default;

        static FormattedText Text(string text, double size, IBrush brush) =>
            new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(StudioTokens.UiFont), size, brush);

        // ------------------------------------------------------------------ pointer and keys

        Point OnLayer(PointerEventArgs e) => e.GetPosition(this);

        void OnWindowMoved(object sender, PointerEventArgs e)
        {
            if (mode.Mapping == null) return;
            lastPointer = OnLayer(e);
            if (dirty) Rescan();
            var spot = SpotAt(lastPointer);
            var now = spot?.Control;
            if (!ReferenceEquals(now, hover))
            {
                hover = now;
                surface.InvalidateVisual();
            }
        }

        void OnWindowPressed(object sender, PointerPressedEventArgs e)
        {
            if (mode.Mapping == null) return;
            var pos = lastPointer = OnLayer(e);
            var props = e.GetCurrentPoint(this).Properties;
            if (mode.Active)
            {
                if (IsInBanner(e)) return;
                Rescan();
                if (SpotAt(pos) is { } spot)
                {
                    if (props.IsRightButtonPressed && spot.Badge != null) ShowBadgeMenu(spot);
                    else if (props.IsLeftButtonPressed) Arm(spot);
                }
                else if (props.IsLeftButtonPressed && mode.Armed != null) mode.Disarm();
                e.Handled = true;                                            // nothing under it acts in map mode
                return;
            }
            Rescan();
            foreach (var spot in spots)
            {
                if (spot.Badge != null && BadgeOf(spot).Contains(pos) && props.IsRightButtonPressed) { ShowBadgeMenu(spot); e.Handled = true; return; }
                if (props.IsLeftButtonPressed && ReferenceEquals(SpotAt(pos), spot) && ButtonOf(spot).Contains(pos) && popover == null) { Open(spot); e.Handled = true; return; }
                if (props.IsLeftButtonPressed && spot.Badge != null && BadgeOf(spot).Contains(pos)) { Open(spot); e.Handled = true; return; }
            }
        }

        bool IsInBanner(PointerEventArgs e)
        {
            if (!banner.IsVisible) return false;
            var p = e.GetPosition(banner);
            return new Rect(banner.Bounds.Size).Contains(p);
        }

        void OnWindowKey(object sender, KeyEventArgs e)
        {
            if (!mode.Active) return;
            if (e.Key == Key.Escape) { mode.Exit(); e.Handled = true; }
            else if (e.Key is Key.Delete or Key.Back && mode.Armed != null) { mode.Unmap(); e.Handled = true; }
        }

        /// <summary>A click in map mode: the control waits for a controller (or stops waiting when it already did).</summary>
        public void Arm(Spot spot)
        {
            if (spot.Target.Equals(mode.Armed)) { mode.Disarm(); return; }
            mode.Arm(spot.Target, out _);
        }

        /// <summary>Arms a control by its marked target (for tests and the Device menu).</summary>
        public bool ArmControl(AvControl control)
        {
            if (dirty) Rescan();
            if (SpotOf(control) is not { } s) return false;
            Arm(s);
            return mode.Armed != null;
        }

        /// <summary>Arms the control that maps to a target, wherever it is on screen now (the gallery and the tests: cells are rebuilt, a target stays).</summary>
        public bool ArmTarget(MapTarget target)
        {
            Rescan();
            if (spots.FirstOrDefault(s => s.Target.Equals(target)) is not { } spot) return false;
            Arm(spot);
            return mode.Armed != null;
        }

        // ------------------------------------------------------------------ the popover and the badge's menu

        /// <summary>Opens the MIDI popover of a control (the button, the badge, the Device menu).</summary>
        public MapPopover Open(AvControl control)
        {
            if (dirty) Rescan();
            return SpotOf(control) is { } s ? Open(s) : null;
        }

        MapPopover Open(Spot spot)
        {
            Close();
            popover = new MapPopover(mode, spot.Target, this, spot.Bounds);        // anchored to the layer: the controls under it may be rebuilt (the Inspector remakes its cells)
            popover.Closed += () =>
            {
                if (popover != null) Children.Remove(popover.Host);
                popover = null;
                surface.InvalidateVisual();
            };
            Children.Add(popover.Host);
            popover.Show();
            return popover;
        }

        /// <summary>Opens the popover of a target that has no control on screen of its own (a menu on a machine's parameter): anchored to <paramref name="anchor"/>.</summary>
        public MapPopover OpenFor(MapTarget target, AvControl anchor)
        {
            Close();
            popover = new MapPopover(mode, target, this, anchor != null && anchor.TranslatePoint(default, this) is { } at ? new Rect(at, anchor.Bounds.Size) : null);
            popover.Closed += () =>
            {
                if (popover != null) Children.Remove(popover.Host);
                popover = null;
            };
            Children.Add(popover.Host);
            popover.Show();
            return popover;
        }

        /// <summary>Opens the popover of a parameter or command wherever its control is on screen now (the Device menu's Bind item).</summary>
        public MapPopover OpenForTarget(MapTarget target)
        {
            if (target == null) return null;
            Rescan();
            return spots.FirstOrDefault(s => s.Target.Equals(target)) is { } spot ? Open(spot) : null;
        }

        public void Close() => popover?.Hide();

        void ShowBadgeMenu(Spot spot)
        {
            var edit = new MenuItem { Header = "Edit…" };
            edit.Click += (s, e) => Open(spot);
            var unmap = new MenuItem { Header = "Unmap" };
            unmap.Click += (s, e) => mode.Unmap(spot.Target);
            var menu = new ContextMenu { ItemsSource = new object[] { edit, unmap } };
            menu.Open(spot.Control);
            BadgeMenu = menu;
        }

        /// <summary>The badge's right-click menu (tests).</summary>
        internal ContextMenu BadgeMenu { get; private set; }

        // ------------------------------------------------------------------ the banner

        void Sync()
        {
            updating = true;
            try
            {
                var mm = mode.Mapping;
                banner.IsVisible = mode.Active;
                IsHitTestVisible = mode.Active;
                Background = mode.Active ? Brushes.Transparent : null;
                string trackName = mm?.SelectedTrack?.Name;
                trackScope.Content = trackName != null ? "This track: " + trackName : "This track";
                trackScope.IsEnabled = trackName != null;
                trackScope.IsChecked = mode.Scope == MapScope.Track && trackName != null;
                globalScope.IsChecked = mode.Scope == MapScope.Global || trackName == null;
                bannerText.Text = BannerText();
            }
            finally
            {
                updating = false;
            }
        }

        string BannerText()
        {
            if (mode.Mapping?.Listening is { Teach: true }) return "Map mode – turn the knobs of a controller, in order.";
            if (mode.Armed != null) return $"Listening for {mode.Armed.Text}… move a knob or press a key on any controller. Delete unmaps it, Esc ends map mode.";
            string last = mode.LastMapped;
            return (last != null ? last + ".  " : "") + "Map mode – click a control, then move a knob or press a key. Esc ends it.";
        }

        public string BannerMessage => bannerText.Text;

        // ------------------------------------------------------------------ drawing

        /// <summary>The layer's drawing: a control of its own, since a panel's Render is sealed.</summary>
        sealed class Surface : AvControl
        {
            readonly MapLayer layer;
            public Surface(MapLayer layer) { this.layer = layer; IsHitTestVisible = false; }
            public override void Render(DrawingContext ctx) => layer.Paint(ctx);
        }

        void Paint(DrawingContext ctx)
        {
            var accent = StudioTokens.Accent;
            var pen = new Pen(new ImmutableSolidColorBrush(Color.FromArgb(0xE0, accent.R, accent.G, accent.B)), 1.4, new DashStyle(new double[] { 3, 2 }, 0));
            var armedPen = new Pen(StudioTokens.AccentBrush, 2.4);
            foreach (var s in spots)
            {
                bool armed = mode.Armed != null && s.Target.Equals(mode.Armed);
                var r = s.Bounds.Inflate(1);
                if (mode.Active)
                {
                    if (armed)
                    {
                        ctx.DrawRectangle(new ImmutableSolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B)), armedPen, r, 5, 5);
                        DrawChip(ctx, new Rect(r.X + 1, r.Y - 15, 74, 14), "Listening…", true);
                    }
                    else ctx.DrawRectangle(null, pen, r, 5, 5);
                }
                if (s.Badge != null) DrawChip(ctx, BadgeOf(s), s.Badge, armed);
                if (!mode.Active && ReferenceEquals(s.Control, hover) && popover == null) DrawChip(ctx, ButtonOf(s), "MIDI", false);
            }
        }

        static void DrawChip(DrawingContext ctx, Rect rect, string text, bool strong)
        {
            var fill = strong ? StudioTokens.AccentBrush : StudioTokens.SurfaceControlBrush;
            var ink = strong ? StudioTokens.TextOnBrush(StudioTokens.Accent) : StudioTokens.TextPrimaryBrush;
            ctx.DrawRectangle(fill, new Pen(strong ? StudioTokens.AccentBrush : StudioTokens.SurfaceBorderBrush, 1), rect, 7, 7);
            var t = Text(text, StudioTokens.FontCaption, ink);
            t.MaxTextWidth = Math.Max(1, rect.Width - 6);
            t.MaxTextHeight = rect.Height;
            t.Trimming = TextTrimming.CharacterEllipsis;
            ctx.DrawText(t, new Point(rect.X + Math.Max(3, (rect.Width - t.Width) / 2), rect.Y + (rect.Height - t.Height) / 2));
        }
    }
}
