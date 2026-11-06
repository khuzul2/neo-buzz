#nullable disable
// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): the piano keyboard under the window of every
// machine that plays notes, in both apps: one source for WPF (ReBuzz.exe) and Avalonia (the studio,
// AVALONIA, the kit's WpfNames.cs), on the Neo editor kit, as the manifest editor is. The hosts wrap a
// machine window's content (a Neo editor, the parameter window, a machine's own GUI, its manifest
// editor) in a MachineKeyboardFrame, which adds the keyboard below and grows the window by its height,
// so the GUI keeps its size.
//
//  - A strip over the keys: the toggle (shown or hidden for this machine type; Ctrl+K, ⌘K on a Mac),
//    the octave (◀ C3 ▶), what is played (its name, its drum, its velocity) and how the machine gets it
//    (MIDI or its Note tracks: Core/Keyboards/KeyboardRoute.cs).
//  - The keys, as many octaves as fit the width (two at least), each C named; a drum machine's keys by
//    their drums (the machine's KeyNames, or its manifest's note map), the keys that play no drum dimmed.
//    A strike's height is its velocity (top soft, bottom loud), shown as how far the key fills; dragging
//    glides from key to key; keys light up for every note the machine plays, from any source (patterns,
//    MIDI, this keyboard: Core/Keyboards/NoteMonitor.cs). Each key is an element with its name, for
//    screen readers.
//  - Every note ends: the pointer let go anywhere (it is captured), leaving the keys, the window losing
//    focus, closing or hiding, the keyboard hidden. Space stays the transport's (the hosts' TransportKeys
//    take it before the keyboard sees it).
// Notes go out through KeyboardPlayer (Core/Keyboards/KeyboardPlayer.cs).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Keyboards;
#if AVALONIA
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Size = Avalonia.Size;
using Rect = Avalonia.Rect;
using Point = Avalonia.Point;
using Panel = Avalonia.Controls.Panel;
using DockPanel = Avalonia.Controls.DockPanel;
using Dock = Avalonia.Controls.Dock;
using Window = Avalonia.Controls.Window;
using GridUnitType = Avalonia.Controls.GridUnitType;
using KeyEventArgs = Avalonia.Input.KeyEventArgs;
using ScrollViewer = Avalonia.Controls.ScrollViewer;
using ScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility;
#else
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
#endif
using KitTheme = NeoBuzz.Editor.Theme;      // inside an Avalonia control, Theme is its own (StyledElement.Theme)

namespace NeoBuzz.Editor
{
    /// <summary>The keyboard: the strip, and the keys under it.</summary>
    internal sealed class MachineKeyboard : DockPanel
    {
        /// <summary>The keys' margins on the panel.</summary>
        public const double Side = 6, Bottom = 6;
        /// <summary>The window's height the keyboard takes: the strip alone while hidden, and the keys while shown.</summary>
        public const double CollapsedHeight = KeyboardLayout.BarHeight, ExpandedHeight = KeyboardLayout.BarHeight + KeyboardLayout.KeysHeight + Bottom;

        /// <summary>Lit keys: the synths' identity (the classic app: the manifest editor's warm accent), unless the editor above has its own.</summary>
        public static readonly NeoColor DefaultAccent = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xE8, 0xA8, 0x4A));

        readonly MachineCore machine;
        readonly NeoColor accent;
        readonly KeyboardPlayer player;
        readonly KeyboardKeys keys;
        readonly BarButton toggle, down, up;
        readonly BarText octave, readout, badge;
        readonly DispatcherTimer poll;
        bool shown, attached, wasPlaying;
        int seenVersion = -1, pollCount;
        Window window;
        INotifyPropertyChanged general;

        public MachineKeyboard(MachineCore machine, NeoColor accent)
        {
            this.machine = machine;
            this.accent = accent;
            player = new KeyboardPlayer(machine);
            Background = KitTheme.Panel;
            LastChildFill = true;
            UseLayoutRounding = true;
#if AVALONIA
            // Grey-scale text, as the Neo editors draw it: no coloured fringes on the dark theme.
            Avalonia.Media.RenderOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
#endif
            AutomationProperties.SetName(this, "Keyboard");

            var bar = new DockPanel { Height = KeyboardLayout.BarHeight, LastChildFill = true, Margin = new Thickness(Side, 0, Side, 0) };
            toggle = new BarButton("▾ Keyboard", 84, accent) { Name = "KeyboardToggle" };
            AutomationProperties.SetName(toggle, "Keyboard");
            toggle.Click += () => SetShown(!shown, remember: true);
            down = new BarButton("◀", 20, accent);
            AutomationProperties.SetName(down, "Octave down");
            down.ToolTip = "An octave down";
            down.Click += () => ShiftOctave(-1);
            octave = new BarText(40, KitTheme.Text, center: true);
            up = new BarButton("▶", 20, accent);
            AutomationProperties.SetName(up, "Octave up");
            up.ToolTip = "An octave up";
            up.Click += () => ShiftOctave(+1);
            badge = new BarText(78, KitTheme.DimText, right: true);
            readout = new BarText(0, KitTheme.DimText);
            AddDocked(bar, toggle, Dock.Left);
            AddDocked(bar, new Spacer(10), Dock.Left);
            AddDocked(bar, down, Dock.Left);
            AddDocked(bar, octave, Dock.Left);
            AddDocked(bar, up, Dock.Left);
            AddDocked(bar, new Spacer(10), Dock.Left);
            AddDocked(bar, badge, Dock.Right);
            bar.Children.Add(readout);
            AddDocked(this, bar, Dock.Top);

            keys = new KeyboardKeys(this) { Margin = new Thickness(Side, 0, Side, Bottom) };
            Children.Add(keys);
            keys.RefreshNames();     // a drum machine's keys before the first layout: it starts at its drums

            var route = player.Route;
            badge.Text = route.Input == KeyboardInput.Midi ? "MIDI" : route.Input == KeyboardInput.Tracks ? "Note tracks" : "";
            UpdateBadge();
            poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            poll.Tick += (s, e) => Poll();
            shown = KeyboardSettings.Shown(machine.DLL);
            Apply();
#if !AVALONIA
            Loaded += (s, e) => Attach();
            Unloaded += (s, e) => Detach();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { if (shown) poll.Start(); }
                else { player.AllOff(); keys.Released(); poll.Stop(); }
            };
#endif
        }

        static void AddDocked(DockPanel panel, UIElement child, Dock dock)
        {
            DockPanel.SetDock(child, dock);
            panel.Children.Add(child);
        }

        public MachineCore Machine => machine;
        public KeyboardPlayer Player => player;
        public NeoColor Accent => accent;
        internal KeyboardKeys Keys => keys;
        internal BarButton Toggle => toggle;
        internal BarButton OctaveDown => down;
        internal BarButton OctaveUp => up;
        internal string OctaveText => octave.Text;
        internal string ReadoutText => readout.Text;
        internal string BadgeText => badge.Text;

        /// <summary>Whether the keys show (the strip always does).</summary>
        public bool Shown => shown;

        /// <summary>The window's height the keyboard takes now.</summary>
        public double ExtraHeight => shown ? ExpandedHeight : CollapsedHeight;

        /// <summary>The keys were shown or hidden: the height the window gains (or loses, negative).</summary>
        public event Action<double> HeightChanged;

        /// <summary>Shows or hides the keys; <paramref name="remember"/>: as this machine type's choice (KeyboardSettings).</summary>
        public void SetShown(bool value, bool remember = false)
        {
            if (remember && machine.DLL?.Name is { Length: > 0 })
            {
                KeyboardSettings.Set(machine.DLL, value);      // this keyboard follows through Changed, with the gear's others
                if (shown == value) return;
            }
            if (shown == value) return;
            double before = ExtraHeight;
            shown = value;
            Apply();
            HeightChanged?.Invoke(ExtraHeight - before);
        }

        void Apply()
        {
            if (!shown)
            {
                player.AllOff();
                keys.Released();
            }
            keys.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            foreach (var e in new UIElement[] { down, octave, up, readout, badge }) e.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            toggle.Text = shown ? "▾ Keyboard" : "▸ Keyboard";
            toggle.On = shown;
            string key = OperatingSystem.IsMacOS() ? "⌘K" : "Ctrl+K";
            toggle.ToolTip = (shown ? "Hide the keyboard" : "Show the keyboard") + $" ({key}): for every {machine.DLL?.Name} window.";
            if (attached && shown) poll.Start();
            else poll.Stop();
            Height = ExtraHeight;
            UpdateOctave();
        }

        /// <summary>Moves the keys an octave up (+1) or down (−1), within what the machine takes.</summary>
        public void ShiftOctave(int octaves)
        {
            keys.Shift(octaves);
            UpdateOctave();
        }

        internal void UpdateOctave()
        {
            var l = keys.Layout;
            if (l == null)
            {
                octave.Text = "";
                return;
            }
            octave.Text = KeyboardFace.NoteName(l.First);
            octave.ToolTip = $"The lowest key: {KeyboardFace.NoteName(l.First)}, to {KeyboardFace.NoteName(l.Last)}";
            down.Enabled = keys.CanShift(-1);
            up.Enabled = keys.CanShift(+1);
        }

        /// <summary>How the machine gets the notes; for one that takes them in its tracks, how many it plays at once.</summary>
        void UpdateBadge()
        {
            var route = player.Route;
            string tip = route.Description;
            if (route.Input == KeyboardInput.Tracks)
            {
                int tracks = Math.Max(1, machine.TrackCount);
                tip += tracks == 1 ? " It has one track: one note at a time (add tracks for chords)." : $" It has {tracks} tracks: {tracks} notes at once.";
            }
            if (!Equals(badge.ToolTip, tip)) badge.ToolTip = tip;
        }

        internal void ShowReadout(int note, int velocity)
        {
            readout.Text = note >= 0 ? KeyboardFace.Readout(note, keys.NameOf(note), velocity)
                : "Lower on a key plays louder";
        }

        // ------------------------------------------------------------------ life

#if AVALONIA
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Attach();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Detach();
        }
#endif

        void Attach()
        {
            if (attached) return;
            attached = true;
            machine.NoteMonitor ??= new NoteMonitor();
            KeyboardSettings.Changed += GearChanged;
            general = Global.GeneralSettings;
            if (general != null) general.PropertyChanged += GeneralChanged;
            NoteNames.Changed += NamingChanged;
#if AVALONIA
            window = TopLevel.GetTopLevel(this) as Window;
#else
            window = Window.GetWindow(this);
#endif
            if (window != null) window.Deactivated += WindowDeactivated;
            keys.RefreshNames();
            ShowReadout(-1, 0);
            if (shown) poll.Start();
        }

        /// <summary>
        /// The machine is going away: every note ends and the keyboard lets go of the settings it follows (WPF hides machine
        /// windows instead of closing them, so they never unload).
        /// </summary>
        public void Release()
        {
            Detach();
            player.AllOff();
        }

        void Detach()
        {
            if (!attached) return;
            attached = false;
            poll.Stop();
            player.AllOff();
            keys.Released();
            KeyboardSettings.Changed -= GearChanged;
            if (general != null) general.PropertyChanged -= GeneralChanged;
            NoteNames.Changed -= NamingChanged;
            general = null;
            if (window != null) window.Deactivated -= WindowDeactivated;
            window = null;
        }

        void WindowDeactivated(object sender, EventArgs e)
        {
            // Losing focus ends every note (the release would go to another window).
            player.AllOff();
            keys.Released();
        }

        void GearChanged(string gear)
        {
            if (gear == machine.DLL?.Name) SetShown(KeyboardSettings.Shown(machine.DLL));
        }

        void GeneralChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(BuzzGUI.Common.Settings.GeneralSettings.MachineKeyboard) || string.IsNullOrEmpty(e.PropertyName))
                SetShown(KeyboardSettings.Shown(machine.DLL));
        }

        /// <summary>Preferences name notes another way (docs/specs/ux-overhaul.md §3.5): the octave and the keys say it.</summary>
        void NamingChanged()
        {
            UpdateOctave();
            keys.Renamed();
        }

        /// <summary>
        /// 30 times a second while shown: the notes the machine plays now (the monitor moved), and its key names now and then
        /// (Neo Sampler's Kit mode follows its zones). The transport stopping ends what the patterns played.
        /// </summary>
        internal void Poll()
        {
            var core = machine.Graph?.Buzz as ReBuzzCore;
            var monitor = machine.NoteMonitor;
            bool playing = core?.Playing == true;
            if (wasPlaying && !playing) monitor?.Stopped();
            wasPlaying = playing;
            if (++pollCount % 15 == 0)
            {
                keys.RefreshNames();
                UpdateBadge();
            }
            if (monitor == null) return;
            int v = monitor.Version;
            if (v == seenVersion) return;
            seenVersion = v;
            keys.RefreshSounding(monitor);
        }
    }

    /// <summary>The keys: an element per key (each with its name), laid out across the width; the pointer plays them.</summary>
    internal sealed class KeyboardKeys : Panel
    {
        readonly MachineKeyboard owner;
        readonly Dictionary<int, KeyElement> elements = new Dictionary<int, KeyElement>();
        readonly byte[] sounding = new byte[NoteMonitor.Keys];
        IReadOnlyDictionary<int, string> names = new Dictionary<int, string>();
        KeyboardLayout layout;
        int? first;                 // the lowest key the user moved to; null: where DefaultFirst puts it
        int current = -1, hover = -1;
        bool pressing;

        public KeyboardKeys(MachineKeyboard owner)
        {
            this.owner = owner;
            Background = KitTheme.Panel;
            Focusable = true;
            ClipToBounds = true;
            Height = KeyboardLayout.KeysHeight;
            Cursor = Cursors.Hand;
            AutomationProperties.SetName(this, "Keys");
#if !AVALONIA
            LostKeyboardFocus += (s, e) => LetGo();
#else
            LostFocus += (s, e) => LetGo();
#endif
        }

        public KeyboardLayout Layout => layout;

        public NeoColor Accent => owner.Accent;

        /// <summary>The key currently played by the pointer, or −1.</summary>
        public int Current => current;

        public KeyElement ElementOf(int note) => elements.TryGetValue(note, out var e) ? e : null;

        public IEnumerable<KeyElement> Elements => elements.Values;

        KeyboardRoute Route => owner.Player.Route;

        /// <summary>A key's drum name (written pitch), or null.</summary>
        public string NameOf(int note) => names.TryGetValue(note, out var n) ? n : null;

        bool Kit => names.Count > 0;

        // ------------------------------------------------------------------ the range

        public bool CanShift(int octaves)
        {
            if (layout == null) return false;
            int to = KeyboardLayout.ClampFirst(layout.First + 12 * octaves, layout.Octaves);
            if (to == layout.First) return false;
            // Not past what the machine takes: some key of the new range must play.
            int last = Math.Min(127, to + 12 * layout.Octaves);
            return last >= Route.Lowest && to <= Route.Highest;
        }

        public void Shift(int octaves)
        {
            if (!CanShift(octaves)) return;
            first = KeyboardLayout.ClampFirst(layout.First + 12 * octaves, layout.Octaves);
            owner.Player.AllOff();
            Released();
            Rebuild(layout.Width);
        }

        /// <summary>
        /// Where the keys start until the user moves them: a drum machine's octaves that show the most of its drums
        /// (by name: Neo Drums' B1 is a second kick; the higher on a tie), else around middle C (C4), within what the machine takes.
        /// </summary>
        int DefaultFirst(int octaves)
        {
            if (names.Count > 0) return KeyboardLayout.BestFirstForKit(names, octaves).First;
            int centre = Math.Clamp(60, Route.Lowest, Math.Max(Route.Lowest, Route.Highest));
            return KeyboardLayout.ClampFirst(centre - 12 * (octaves / 2), octaves);
        }

        /// <summary>The octaves that fit a width: a drum machine's fewer and wider, room for its drums' names.</summary>
        int OctavesFor(double width) => Kit ? KeyboardLayout.OctavesForKit(width, names) : KeyboardLayout.OctavesFor(width);

        void Rebuild(double width)
        {
            int octaves = OctavesFor(width);
            int start = KeyboardLayout.ClampFirst(first ?? DefaultFirst(octaves), octaves);
            var next = new KeyboardLayout(start, octaves, width);
            bool same = layout != null && layout.First == next.First && layout.Octaves == next.Octaves;
            layout = next;
            if (!same)
            {
                if (pressing) { owner.Player.AllOff(); Released(); }
                Children.Clear();
                elements.Clear();
                foreach (int note in layout.Keys())
                {
                    var e = new KeyElement(this, note);
                    elements[note] = e;
                    Children.Add(e);
                }
                UpdateLooks();
            }
            owner.UpdateOctave();
        }

        // ------------------------------------------------------------------ layout

        protected override Size MeasureOverride(Size available)
        {
            foreach (UIElement c in Children) c.Measure(new Size(KeyboardLayout.TargetWhite, KeyboardLayout.KeysHeight));
            // Asks for no more than two octaves of the narrowest keys: the window's GUI sets the width.
            return new Size(KeyboardLayout.MinWidth, KeyboardLayout.KeysHeight);
        }

        protected override Size ArrangeOverride(Size final)
        {
            if (layout == null || Math.Abs(layout.Width - final.Width) > 0.5) Rebuild(Math.Max(KeyboardLayout.MinWidth, final.Width));
            foreach (var (note, e) in elements)
            {
                var r = layout.Rect(note);
                e.Arrange(new Rect(r.X, r.Y, r.Width, r.Height));
            }
            return final;
        }

        // ------------------------------------------------------------------ names and lights

        /// <summary>A drum machine's key names, as written pitches: the machine's own KeyNames, else its manifest's note map.</summary>
        public void RefreshNames()
        {
            var found = new Dictionary<int, string>();
            int offset = Route.Offset;
            try
            {
                if (owner.Machine.ManagedMachine?.GetType().GetProperty("KeyNames")?.GetValue(owner.Machine.ManagedMachine) is IEnumerable<KeyValuePair<int, string>> own)
                    foreach (var (key, name) in own) found[key - offset] = name;
                else if (ReBuzz.Agent.SongModel.Manifest(owner.Machine.DLL)?.Notes?.Map is { Count: > 0 } map)
                    foreach (var (name, key) in map) found.TryAdd(key - offset, name);
            }
            catch (Exception)
            {
                // A machine going away: no names.
            }
            if (found.Count == names.Count && found.All(kv => names.TryGetValue(kv.Key, out var n) && n == kv.Value)) return;
            names = found;
            // A drum machine now (Neo Sampler's Kit mode on), or not: the keys follow where they weren't moved, or where
            // none of its drums shows.
            if (layout != null && (first == null || (found.Count > 0 && !found.Keys.Any(layout.Contains))))
            {
                first = null;
                int octaves = OctavesFor(layout.Width);
                if (octaves != layout.Octaves || DefaultFirst(octaves) != layout.First)
                {
                    owner.Player.AllOff();
                    Released();
                    var width = layout.Width;
                    layout = null;
                    Rebuild(width);
                }
            }
            foreach (var e in elements.Values) e.UpdateName();
            UpdateLooks();
        }

        /// <summary>The notes are named another way: each key's label and name again.</summary>
        public void Renamed()
        {
            foreach (var e in elements.Values) e.UpdateName();
        }

        /// <summary>What the machine plays now, from the monitor (pitches as it gets them, less its offset).</summary>
        public void RefreshSounding(NoteMonitor monitor)
        {
            monitor.Read(sounding);
            UpdateLooks();
        }

        void UpdateLooks()
        {
            foreach (var e in elements.Values) e.SetLook(LookOf(e.Note));
        }

        KeyLook LookOf(int note)
        {
            var player = owner.Player;
            int sent = note + Route.Offset;
            int velocity = player.VelocityOf(note);
            if (velocity == 0 && sent >= 0 && sent < sounding.Length) velocity = sounding[sent];
            return new KeyLook(Route.Plays(note), note == hover && !pressing, velocity, player.IsHeld(note), Kit);
        }

        void Refresh(int note)
        {
            if (elements.TryGetValue(note, out var e)) e.SetLook(LookOf(note));
        }

        // ------------------------------------------------------------------ the pointer

        /// <summary>A press at (x, y): plays the key there at the strike's velocity.</summary>
        internal void Down(double x, double y)
        {
            pressing = true;
            int old = hover;
            hover = -1;
            Refresh(old);
            Play(layout?.KeyAt(x, y) ?? -1, y);
        }

        /// <summary>The pointer moved: while pressing, glides to the key under it (none outside the keys).</summary>
        internal void Move(double x, double y)
        {
            int key = layout?.KeyAt(x, y) ?? -1;
            if (!pressing)
            {
                if (key == hover) return;
                int old = hover;
                hover = key;
                Refresh(old);
                Refresh(key);
                owner.ShowReadout(key, 0);
                return;
            }
            if (key != current) Play(key, y);
        }

        /// <summary>Released: the note ends.</summary>
        internal void Up()
        {
            pressing = false;
            Play(-1, 0);
        }

        /// <summary>The pointer left without a press.</summary>
        internal void Leave()
        {
            if (pressing) return;
            int old = hover;
            hover = -1;
            Refresh(old);
            owner.ShowReadout(-1, 0);
        }

        /// <summary>Whatever the pointer held ends (capture lost, focus gone, the range moved, hidden).</summary>
        internal void LetGo()
        {
            pressing = false;
            if (current >= 0) Play(-1, 0);
        }

        /// <summary>The player let go of everything: nothing is held by the pointer.</summary>
        internal void Released()
        {
            pressing = false;
            current = -1;
            UpdateLooks();
        }

        void Play(int key, double y)
        {
            int old = current;
            current = -1;
            // Gliding: the new key first, then the old one off (a mono synth slides).
            if (key >= 0 && Route.Plays(key) && layout != null)
            {
                int velocity = layout.VelocityAt(key, y);
                if (owner.Player.On(key, velocity)) current = key;
                owner.ShowReadout(key, velocity);
            }
            if (old >= 0 && old != current) owner.Player.Off(old);
            if (key < 0) owner.ShowReadout(hover, 0);
            Refresh(old);
            Refresh(key);
        }

        // ------------------------------------------------------------------ toolkit events

#if AVALONIA
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Pointer.Capture(this);
            Focus();
            var p = e.GetPosition(this);
            Down(p.X, p.Y);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            Move(p.X, p.Y);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            Up();
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            LetGo();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            Leave();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
            if (e.Key == Key.Left) { owner.ShiftOctave(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { owner.ShiftOctave(+1); e.Handled = true; }
        }
#else
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            CaptureMouse();
            Focus();
            var p = e.GetPosition(this);
            Down(p.X, p.Y);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            Move(p.X, p.Y);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            Up();
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            LetGo();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            Leave();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Left) { owner.ShiftOctave(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { owner.ShiftOctave(+1); e.Handled = true; }
        }
#endif
    }

    /// <summary>One key: drawn by KeyboardFace, named for screen readers ("C#4", "D2, Snare").</summary>
    internal sealed class KeyElement : FrameworkElement
    {
        readonly KeyboardKeys keys;
        readonly WpfCanvas canvas;
        KeyLook look;

        public KeyElement(KeyboardKeys keys, int note)
        {
            this.keys = keys;
            Note = note;
            canvas = new WpfCanvas(this);
            IsHitTestVisible = false;
            UpdateName();
        }

        public int Note { get; }

        public KeyLook Look => look;

        /// <summary>The key's drum, or null.</summary>
        public string Drum => keys.NameOf(Note);

        public void UpdateName()
        {
            AutomationProperties.SetName(this, KeyboardFace.AccessibleName(Note, Drum));
            InvalidateVisual();
        }

        public void SetLook(KeyLook value)
        {
            if (value == look) return;
            look = value;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
#if AVALONIA
            double w = Bounds.Width, h = Bounds.Height;
#else
            double w = ActualWidth, h = ActualHeight;
#endif
            if (w <= 0 || h <= 0) return;
            KeyboardFace.DrawKey(canvas.On(dc), w, h, Note, look, Drum, keys.Accent);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new KeyPeer(this);

#if AVALONIA
        sealed class KeyPeer : ControlAutomationPeer
        {
            public KeyPeer(Control owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

            protected override string GetClassNameCore() => "PianoKey";
        }
#else
        sealed class KeyPeer : FrameworkElementAutomationPeer
        {
            public KeyPeer(FrameworkElement owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

            protected override string GetClassNameCore() => "PianoKey";
        }
#endif
    }

    /// <summary>A small button of the strip, drawn in the kit's look.</summary>
    internal sealed class BarButton : FrameworkElement
    {
        readonly NeoColor accent;
        readonly WpfCanvas canvas;
        string text;
        bool hover, on, enabled = true;

        public BarButton(string text, double width, NeoColor accent)
        {
            this.text = text;
            this.accent = accent;
            canvas = new WpfCanvas(this);
            Width = width;
            Height = 18;
            VerticalAlignment = VerticalAlignment.Center;
            Cursor = Cursors.Hand;
        }

        public string Text
        {
            get => text;
            set { text = value; InvalidateVisual(); }
        }

        public bool On
        {
            get => on;
            set { on = value; InvalidateVisual(); }
        }

        public bool Enabled
        {
            get => enabled;
            set { enabled = value; InvalidateVisual(); }
        }

        public event Action Click;

        /// <summary>Clicks it (tests, and the hosts' Ctrl+K).</summary>
        public void Press()
        {
            if (enabled) Click?.Invoke();
        }

        protected override void OnMouseEnter(MouseEventArgs e) { hover = true; InvalidateVisual(); }
        protected override void OnMouseLeave(MouseEventArgs e) { hover = false; InvalidateVisual(); }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => e.Handled = true;

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            Press();
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
#if AVALONIA
            double w = Bounds.Width > 0 ? Bounds.Width : Width, h = Bounds.Height > 0 ? Bounds.Height : Height;
#else
            double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
#endif
            KeyboardFace.DrawButton(canvas.On(dc), w, h, text, hover, on, enabled, accent);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new ButtonPeer(this);

#if AVALONIA
        sealed class ButtonPeer : ControlAutomationPeer
        {
            public ButtonPeer(Control owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        }
#else
        sealed class ButtonPeer : FrameworkElementAutomationPeer
        {
            public ButtonPeer(FrameworkElement owner) : base(owner) { }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        }
#endif
    }

    /// <summary>A line of the strip's text, drawn (cut to its width; asks for no more than <c>width</c>).</summary>
    internal sealed class BarText : FrameworkElement
    {
        readonly WpfCanvas canvas;
        readonly NeoColor colour;
        readonly bool center, right;
        string text = "";

#if AVALONIA
        public BarText(double width, Avalonia.Media.IBrush brush, bool center = false, bool right = false)
#else
        public BarText(double width, Brush brush, bool center = false, bool right = false)
#endif
        {
            canvas = new WpfCanvas(this);
            colour = ColourOf(brush);
            this.center = center;
            this.right = right;
            if (width > 0) Width = width;
            Height = KeyboardLayout.BarHeight;
        }

#if AVALONIA
        static NeoColor ColourOf(Avalonia.Media.IBrush b) =>
            b is Avalonia.Media.ISolidColorBrush s ? NeoColor.FromArgb(s.Color.A, s.Color.R, s.Color.G, s.Color.B) : Palette.Text;
#else
        static NeoColor ColourOf(Brush b) =>
            b is SolidColorBrush s ? NeoColor.FromArgb(s.Color.A, s.Color.R, s.Color.G, s.Color.B) : Palette.Text;
#endif

        public string Text
        {
            get => text;
            set
            {
                if (text == (value ?? "")) return;
                text = value ?? "";
                AutomationProperties.SetName(this, text);
                InvalidateVisual();
            }
        }

        protected override Size MeasureOverride(Size available) => new Size(double.IsNaN(Width) ? 0 : Width, KeyboardLayout.BarHeight);

        protected override void OnRender(DrawingContext dc)
        {
#if AVALONIA
            double w = Bounds.Width;
#else
            double w = ActualWidth;
#endif
            if (w <= 4 || text.Length == 0) return;
            var c = canvas.On(dc);
            double y = (KeyboardLayout.BarHeight - 15) / 2;
            if (center) c.Text(text, w / 2, y, 11.5, colour, true, NeoAlign.Center, w);
            else if (right) c.Text(text, w, y, 11, colour, false, NeoAlign.Right, w);
            else c.Text(text, 0, y, 11, colour, false, NeoAlign.Left, w);
        }
    }

    /// <summary>A gap in the strip.</summary>
    internal sealed class Spacer : FrameworkElement
    {
        public Spacer(double width)
        {
            Width = width;
            IsHitTestVisible = false;
        }
    }

    /// <summary>
    /// A machine window's content with the keyboard under it, for machines that play notes (others keep
    /// their content as it is). Grows the window by the keyboard's height when the keys show or hide, so
    /// the GUI above keeps its size; Ctrl+K (⌘K) in the window shows or hides them. A GUI of a fixed layout
    /// (a Neo editor, a managed GUI, a manifest editor) sits in a scroll viewer, with or without a keyboard:
    /// where the window is smaller than it and the keyboard (the screen's work area holds machine windows,
    /// MachineWindowFit, and a user may resize them; Neo Poly's editor is 972 px tall), it keeps its layout and
    /// scrolls rather than being cut or squashed. A parameter window scrolls its rows itself.
    /// </summary>
    internal sealed class MachineKeyboardFrame : Grid
    {
        readonly UIElement content;
        Window window;

        MachineKeyboardFrame(UIElement content, MachineKeyboard keyboard, bool scroll)
        {
            this.content = content;
            Keyboard = keyboard;
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            UIElement top = content;
            if (scroll)
            {
                top = new ScrollViewer
                {
                    Content = content,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Focusable = false,
                    // A window resized larger than the GUI shows the GUI's own background around it.
#if AVALONIA
                    Background = (content as Avalonia.Controls.Primitives.TemplatedControl)?.Background,
#else
                    Background = (content as System.Windows.Controls.Control)?.Background,
#endif
                };
            }
            Top = top;
            Grid.SetRow(top, 0);
            Children.Add(top);
            if (keyboard == null) return;          // a GUI of a fixed layout on a machine that plays no notes: the scroller alone
            Grid.SetRow(keyboard, 1);
            Children.Add(keyboard);
            keyboard.HeightChanged += Grow;
#if AVALONIA
            AttachedToVisualTree += (s, e) => Hook(TopLevel.GetTopLevel(this) as Window);
            DetachedFromVisualTree += (s, e) => Hook(null);
#else
            Loaded += (s, e) => Hook(Window.GetWindow(this));
            Unloaded += (s, e) => Hook(null);
#endif
        }

        /// <summary>The keyboard, or null in the frame of a machine that plays no notes (its GUI only scrolls).</summary>
        public MachineKeyboard Keyboard { get; }

        public UIElement Content => content;

        /// <summary>What shows the content above the keyboard: the content, or the scroller it's in.</summary>
        public UIElement Top { get; }

        /// <summary>
        /// A machine window's content: with the keyboard under it when the machine plays notes, in a scroll viewer when
        /// <paramref name="scroll"/>; else the content itself.
        /// </summary>
        /// <param name="scroll">A GUI of a fixed layout: scrolled where the window is smaller than it (and the keyboard).</param>
        public static UIElement Wrap(IMachine machine, UIElement content, bool scroll = false)
        {
            if (content == null) return null;
            if (machine is MachineCore m && KeyboardRoute.For(m).Shows) return new MachineKeyboardFrame(content, new MachineKeyboard(m, AccentOf(content)), scroll);
            return scroll ? new MachineKeyboardFrame(content, null, true) : content;
        }

        /// <summary>The height a window's keyboard takes (0 without one): songs keep the GUI's own size without it.</summary>
        public static double ExtraHeightOf(object windowContent) => windowContent is MachineKeyboardFrame f && f.Keyboard != null ? f.Keyboard.ExtraHeight : 0;

        /// <summary>A window's own content: inside the frame when it has one.</summary>
        public static object ContentOf(object windowContent) => windowContent is MachineKeyboardFrame f ? f.Content : windowContent;

        /// <summary>What shows a window's own content above its keyboard (the content, or its scroller).</summary>
        public static object TopOf(object windowContent) => windowContent is MachineKeyboardFrame f ? f.Top : windowContent;

        /// <summary>The keyboard in a window's content, or null.</summary>
        public static MachineKeyboard Of(object windowContent) => (windowContent as MachineKeyboardFrame)?.Keyboard;

        /// <summary>A Neo editor's accent (each machine's own colour, in its own copy of the kit: found by name), else the default.</summary>
        static NeoColor AccentOf(object content)
        {
            try
            {
                for (var t = content?.GetType(); t != null; t = t.BaseType)
                {
                    var p = t.GetProperty("Accent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (p == null || p.GetIndexParameters().Length != 0) continue;
                    var value = p.GetValue(content);
                    if (value?.GetType().GetProperty("Argb")?.GetValue(value) is uint argb) return new NeoColor(argb);
                }
            }
            catch (Exception)
            {
            }
            return MachineKeyboard.DefaultAccent;
        }

        void Hook(Window w)
        {
            if (window != null)
            {
#if AVALONIA
                window.RemoveHandler(InputElement.KeyDownEvent, WindowKeyDown);
#else
                window.PreviewKeyDown -= WindowKeyDown;
#endif
            }
            window = w;
            if (window != null)
            {
#if AVALONIA
                window.AddHandler(InputElement.KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
#else
                window.PreviewKeyDown += WindowKeyDown;
#endif
            }
        }

        void WindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || e.Key != Key.K) return;
#if AVALONIA
            var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.KeyModifiers != command) return;
#else
            if (System.Windows.Input.Keyboard.Modifiers != ModifierKeys.Control) return;
#endif
            Keyboard.Toggle.Press();
            e.Handled = true;
        }

        /// <summary>The keys showed or hid: a window that doesn't size to its content grows or shrinks by them.</summary>
        void Grow(double delta)
        {
#if AVALONIA
            var w = TopLevel.GetTopLevel(this) as Window;
            if (w == null || (w.SizeToContent & SizeToContent.Height) != 0) return;
            double height = double.IsNaN(w.Height) ? w.Bounds.Height : w.Height;
            w.Height = Math.Max(KeyboardLayout.BarHeight, height + delta);
#else
            var w = Window.GetWindow(this);
            if (w == null || w.SizeToContent == SizeToContent.Height || w.SizeToContent == SizeToContent.WidthAndHeight) return;
            double height = double.IsNaN(w.Height) ? w.ActualHeight : w.Height;
            w.Height = Math.Max(KeyboardLayout.BarHeight, height + delta);
#endif
        }
    }
}
