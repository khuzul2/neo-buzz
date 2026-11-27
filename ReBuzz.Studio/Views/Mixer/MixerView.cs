// Fork (docs/specs/ux-overhaul.md §11, MX1): the one mixer. A row of strips from the track model (MixerModel), in the density the place
// asks for: Full when it is large (the Detail panel expanded, a double click on the Master), Compact below a view (Detail ▸ Mixer, the
// bottom of the Session View). The same strip parts everywhere (MixerStripView), the same values (the machines' connections), one meter
// ticker for all of them.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Mixer
{
    using Control = Avalonia.Controls.Control;

    /// <summary>One timer for every meter and every poll of the mixers on screen; the engine measures peaks only while one is shown.</summary>
    internal static class MixerTicker
    {
        sealed class Target
        {
            public Action<double> Meter;
            public Action Poll;
        }

        static readonly List<Target> targets = new();
        static DispatcherTimer timer;
        static long last;
        static int tick;

        public static int Count => targets.Count;

        public static IDisposable Add(Action<double> meter, Action poll)
        {
            var target = new Target { Meter = meter, Poll = poll };
            targets.Add(target);
            if (targets.Count == 1)
            {
                SessionMixer.UseMeters(true);
                timer ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
                timer.Tick -= OnTick;
                timer.Tick += OnTick;
                last = 0;
                timer.Start();
            }
            return new Remover(target);
        }

        sealed class Remover : IDisposable
        {
            Target target;
            public Remover(Target t) => target = t;
            public void Dispose()
            {
                if (target == null) return;
                targets.Remove(target);
                target = null;
                if (targets.Count == 0)
                {
                    timer?.Stop();
                    SessionMixer.UseMeters(false);
                }
            }
        }

        static void OnTick(object sender, EventArgs e)
        {
            long now = Environment.TickCount64;
            double seconds = last == 0 ? 0.033 : Math.Min(0.2, (now - last) / 1000.0);
            last = now;
            double fall = MixerView.MeterFallPerSecond * seconds;
            bool poll = (tick++ & 1) == 0;                       // the values every 50 ms, the meters every 25
            foreach (var t in targets.ToArray())
            {
                t.Meter(fall);
                if (poll) t.Poll?.Invoke();
            }
        }
    }

    internal sealed class MixerView : Border, IMixerHost
    {
        public const double MeterFallPerSecond = 0.9;         // of the meter's 66 dB range per second
        const string Section = "Studio";

        readonly ReBuzzCore core;
        readonly ScrollViewer scroller;
        readonly StackPanel row;
        readonly Border bar;
        readonly TextBlock title;
        readonly ToggleButton insertsToggle, sendsToggle, ioToggle;
        readonly List<MixerStripView> strips = new();
        readonly bool masterSeparate;
        MixerModel model;
        string builtKey;
        MixerDensity density;
        IDisposable ticker;
        bool attached, rebuildPosted, dirty, shown = true;
        readonly bool customOptions;
        bool syncing;

        /// <summary>Whether the place the mixer is in is open and on screen (the Detail panel says; a closed panel's mixer costs nothing).</summary>
        public bool IsShown
        {
            get => shown;
            set
            {
                if (shown == value) return;
                shown = value;
                UpdateTicker();
            }
        }
        MixerStripView master;

        /// <summary>Raised when a strip's insert is clicked: the window shows the machine in Detail ▸ Device.</summary>
        public static Action<IMachine> DeviceOpener { get; set; }

        public MixerView(ReBuzzCore core, MixerDensity density, MixerOptions options = null, bool masterSeparate = false)
        {
            this.core = core;
            this.density = density;
            customOptions = options != null;
            Options = options ?? MixerOptions.For(density);
            this.masterSeparate = masterSeparate;
            Background = SessionPalette.Window;
            AutomationProperties.SetName(this, "Mixer");

            row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 8) };
            scroller = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = row,
                Focusable = false,
                Background = SessionPalette.Window,
            };
            title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextPrimaryBrush };
            insertsToggle = Toggle("Inserts", "Show each strip's machines in signal order", v => { if (syncing) return; Options.Inserts = v; Save("MixerInserts", v); Rebuild(true); });
            sendsToggle = Toggle("Sends", "Show a send to each return on every track", v => { if (syncing) return; Options.Sends = v; Save("MixerSends", v); Rebuild(true); });
            ioToggle = Toggle("I/O", "Show each track's inputs: Audio From and MIDI From", v => { if (syncing) return; Options.IO = v; Save("MixerIO", v); Rebuild(true); });
            var show = new TextBlock { Text = "Show", FontSize = 12, Foreground = SessionPalette.LabelText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { title } };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { show, insertsToggle, sendsToggle, ioToggle } };
            var barGrid = new DockPanel { LastChildFill = true, Margin = new Thickness(12, 0) };
            DockPanel.SetDock(right, Dock.Right);
            barGrid.Children.Add(right);
            barGrid.Children.Add(left);
            bar = new Border { Height = 40, Background = StudioTokens.SurfacePanelBrush, BorderBrush = StudioTokens.SurfaceBorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = barGrid };
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(scroller);
            Child = dock;
            bar.IsVisible = density == MixerDensity.Full;
        }

        static ToggleButton Toggle(string text, string tip, Action<bool> changed)
        {
            var t = new ToggleButton { Content = text, Height = 24, MinHeight = 0, Padding = new Thickness(8, 0), FontSize = 12 };
            ToolTip.SetTip(t, tip);
            AutomationProperties.SetName(t, text);
            t.IsCheckedChanged += (s, e) => changed(t.IsChecked == true);
            return t;
        }

        void Save(string key, bool value)
        {
            try { StudioHost.Current?.Registry?.Write(key, value, Section); } catch (Exception) { /* a choice that can't be saved lasts this session */ }
        }

        static bool Read(string key, bool def)
        {
            try { return StudioHost.Current?.Registry?.Read(key, def, Section) ?? def; } catch (Exception) { return def; }
        }

        // ------------------------------------------------------------------ IMixerHost

        public ReBuzzCore Core => core;
        public MixerOptions Options { get; }
        public IReadOnlyList<IMachine> Returns => model?.Returns ?? Array.Empty<IMachine>();
        public ICollection<IMachine> ReturnSet => model?.ReturnSet ?? new HashSet<IMachine>();

        public void OpenDevice(IMachine machine)
        {
            StudioSelection.For(core).SelectMachine(machine, this);
            DeviceOpener?.Invoke(machine);
        }

        // The folded groups are the song's (MixerFoldStore): saved with it, and restored when it opens.
        ISet<string> Folded => new HashSet<string>(core.MixerFolds?.Folded ?? (IReadOnlyCollection<string>)Array.Empty<string>(), StringComparer.Ordinal);

        public void ToggleFold(string groupId)
        {
            if (core.MixerFolds is { } store) store.Set(groupId, !store.IsFolded(groupId));
            Rebuild(true);
        }

        // ------------------------------------------------------------------ the strips

        public MixerDensity Density
        {
            get => density;
            set
            {
                if (value == density) return;
                density = value;
                ApplyDensityOptions();
                bar.IsVisible = density == MixerDensity.Full;
                Rebuild(true);
            }
        }

        /// <summary>
        /// The parts a density shows: Full has inserts, sends and (when chosen) I/O, as the toolbar's toggles say (kept per person);
        /// Compact has the sends (a bar per return on every track) and none of the others. A mixer a view made with options of its own (the Session View's) keeps them.
        /// </summary>
        void ApplyDensityOptions()
        {
            if (customOptions) return;
            bool full = density == MixerDensity.Full;
            Options.Inserts = full && Read("MixerInserts", true);
            Options.Sends = !full || Read("MixerSends", true);   // Compact shows each track's sends; Full as the toolbar says
            Options.IO = full && Read("MixerIO", false);
        }

        public IReadOnlyList<MixerStripView> Strips => strips;

        /// <summary>The Master's strip (in the row, or apart when the mixer was made so).</summary>
        public MixerStripView MasterStrip => master;

        public MixerModel Model => model;

        /// <summary>The strips were made again.</summary>
        public event Action Rebuilt;

        public ScrollViewer Scroller => scroller;

        /// <summary>The strips are made again when the model says they differ, or <paramref name="force"/>.</summary>
        public void Rebuild(bool force = false)
        {
            if (core.SongCore == null) return;
            model = MixerModel.Build(core, Folded);
            string key = $"{(int)density}|{Options.Inserts}{Options.Sends}{Options.IO}{Options.Cue}{Options.Crossfader}{Options.Arm}|{model.Signature}|{string.Join(",", model.Returns.Select(r => r.Name))}";
            if (!force && key == builtKey && strips.Count > 0)
            {
                Refresh();
                return;
            }
            builtKey = key;
            dirty = false;
            row.Children.Clear();
            strips.Clear();
            master = null;
            foreach (var spec in model.Strips)
            {
                var strip = new MixerStripView(this, spec, density);
                strips.Add(strip);
                if (spec.Kind == StripKind.Master) master = strip;
                if (spec.Kind == StripKind.Master && masterSeparate) continue;
                row.Children.Add(strip);
            }
            title.Text = $"Mixer · {Describe()}";
            Rebuilt?.Invoke();
            syncing = true;        // showing what the options are is not a choice to keep
            try
            {
                insertsToggle.IsChecked = Options.Inserts;
                sendsToggle.IsChecked = Options.Sends;
                ioToggle.IsChecked = Options.IO;
            }
            finally { syncing = false; }
            Refresh();
        }

        string Describe()
        {
            int tracks = model.Tracks, groups = model.Groups, returns = model.Returns.Count;
            string Part(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
            return Part(tracks, "track") + (groups > 0 ? ", " + Part(groups, "group") : "") + (returns > 0 ? ", " + Part(returns, "return") : "");
        }

        /// <summary>Reads every strip's values from the machine graph.</summary>
        public void Refresh()
        {
            foreach (var strip in strips) strip.Refresh();
        }

        /// <summary>Takes the peaks since the last call and moves the meters (falling at a fixed rate).</summary>
        public void RefreshMeters(double elapsedSeconds = 0.033)
        {
            double fall = MeterFallPerSecond * elapsedSeconds;
            foreach (var strip in strips) strip.RefreshMeter(fall);
        }

        public bool IsMetering => ticker != null;

        /// <summary>Scrolls the Master's strip into view (and selects it in the studio).</summary>
        public void RevealMaster()
        {
            if (master == null) return;
            if (master.Parent == null) return;
            Dispatcher.UIThread.Post(() =>
            {
                scroller.Offset = new Vector(Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width), 0);
                master.BringIntoView();
            }, DispatcherPriority.Loaded);
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            attached = true;
            ApplyDensityOptions();
            MachineTracksLive.For(core).Retain();
            MachineTracksLive.For(core).Changed += OnStructure;
            StudioSelection.For(core).Changed += OnSelection;
            if (core.TrackColours is { } colours) colours.Changed += OnColours;
            if (core.SessionLayout is { } layout) layout.Changed += OnStructure;
            if (core.MixerFolds is { } folds) folds.Changed += OnStructure;
            Rebuild(true);
            UpdateTicker();
        }

        /// <summary>The meters run while the mixer is attached and shown (a hidden page, another tab, costs nothing).</summary>
        void UpdateTicker()
        {
            bool want = attached && shown && IsVisible;
            if (want == (ticker != null)) return;
            if (want)
            {
                if (dirty) Rebuild();
                else Refresh();
                ticker = MixerTicker.Add(f => RefreshMeters(f / MeterFallPerSecond), Refresh);
            }
            else
            {
                ticker.Dispose();
                ticker = null;
            }
        }

        protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty) UpdateTicker();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            attached = false;
            UpdateTicker();
            MachineTracksLive.For(core).Changed -= OnStructure;
            MachineTracksLive.For(core).Release();
            StudioSelection.For(core).Changed -= OnSelection;
            if (core.TrackColours is { } colours) colours.Changed -= OnColours;
            if (core.SessionLayout is { } layout) layout.Changed -= OnStructure;
            if (core.MixerFolds is { } folds) folds.Changed -= OnStructure;
            base.OnDetachedFromVisualTree(e);
        }

        void OnStructure()
        {
            if (!attached) return;
            if (!shown || !IsVisible) { dirty = true; return; }
            if (rebuildPosted) return;
            rebuildPosted = true;
            Dispatcher.UIThread.Post(() =>
            {
                rebuildPosted = false;
                if (attached) Rebuild();
            }, DispatcherPriority.Loaded);
        }

        void OnSelection(object source) { if (attached) Refresh(); }

        void OnColours() { if (attached) Refresh(); }
    }
}
