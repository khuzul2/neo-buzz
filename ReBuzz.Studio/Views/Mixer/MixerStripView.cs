// Fork (docs/specs/ux-overhaul.md §11, MX1): one strip of the one mixer, in one of three densities with the same parts in each:
//   Full    colour and name, kind, I/O, inserts, sends, pan, fader in dB with a meter and a peak hold, On, Solo, Arm, Cue;
//   Compact name, fader, meter, pan, On, Solo, Arm (and the sends, the crossfader's A and B, Cue and I/O where a view asks for them);
//   Header  a track header's row: the fader, the meter, the pan and the sends, laid out sideways.
// Every control acts on what the Session mixer always acted on: the machine's output connections (SessionMixer), so the Machine View
// shows the same numbers; a gesture is one undo step (MixerEdits). Values are polled (Refresh), the meters on the ticker (RefreshMeter).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Mixer
{
    using Control = Avalonia.Controls.Control;

    /// <summary>Which optional parts a mixer shows (the densities have their own defaults; a view can ask for more).</summary>
    internal sealed class MixerOptions
    {
        public bool Inserts { get; set; }
        public bool Sends { get; set; } = true;
        public bool IO { get; set; }
        public bool Cue { get; set; }
        public bool Arm { get; set; } = true;
        /// <summary>The crossfader's A and B chips on a strip, the crossfader itself on the Master (the Session View).</summary>
        public bool Crossfader { get; set; }
        /// <summary>The crossfader's menu (curve, MIDI learn) and its tooltip: the Session View's, which holds the MIDI map.</summary>
        public Action<Control> CrossfaderMenu { get; set; }
        public Func<string> CrossfaderTip { get; set; }
        /// <summary>The colour bar on top and the machine kind line (Full), or a dot (Compact).</summary>
        public double Width { get; set; }

        public static MixerOptions For(MixerDensity density) => density switch
        {
            MixerDensity.Full => new MixerOptions { Inserts = true, Sends = true, Cue = true, Arm = true },
            MixerDensity.Compact => new MixerOptions { Sends = false, Arm = true },
            _ => new MixerOptions { Sends = true, Arm = true },
        };
    }

    /// <summary>What a strip needs from the mixer around it.</summary>
    internal interface IMixerHost
    {
        ReBuzzCore Core { get; }
        IReadOnlyList<IMachine> Returns { get; }
        ICollection<IMachine> ReturnSet { get; }
        MixerOptions Options { get; }
        void ToggleFold(string groupId);
        void OpenDevice(IMachine machine);
    }

    internal sealed class MixerStripView : Border
    {
        const double FullWidth = 128, CompactWidth = 122;
        static readonly Cursor Hand = new Cursor(StandardCursorType.Hand);
        static readonly FontFamily Mono = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace");

        readonly IMixerHost host;
        readonly MixerDensity density;
        readonly List<MixerBar> sends = new();
        readonly List<IMachine> sendTargets = new();
        MixerBar pan, fader;
        MeterBar meter;
        TextBlock dbText, peakText, nameText, kindText, cueOutText;
        Border colourMark;
        Border onButton, soloButton, armButton, cueButton, sideA, sideB;
        TextBlock onText, soloText, armText, cueText, sideAText, sideBText;
        MixerBar crossfader;
        StackPanel insertsPanel;
        double levelL, levelR, holdL, holdR, holdAge, peakMax;
        double masterHoldDb = double.NegativeInfinity;

        public StripSpec Spec { get; private set; }
        public StripKind Kind => Spec.Kind;
        public IMachine Machine => Spec.Machine;
        public MixerDensity Density => density;

        ReBuzzCore Core => host.Core;
        SessionDocument Doc => Core.SessionLayout?.Document;
        ICollection<IMachine> Returns => host.ReturnSet;

        // For tests and diagnostics (the Session mixer's own surfaces).
        internal MixerBar Fader => fader;
        internal MixerBar PanBar => pan;
        internal IReadOnlyList<MixerBar> Sends => sends;
        internal MeterBar Meter => meter;
        internal string DbText => dbText?.Text ?? "";
        internal string PeakText => peakText?.Text ?? "";
        internal string CueOutText => cueOutText?.Text ?? "";
        internal string NameText => nameText?.Text ?? "";
        internal string KindText => kindText?.Text ?? "";
        internal MixerBar Crossfader => crossfader;
        internal Border SideA => sideA;
        internal Border SideB => sideB;
        internal Border Mute => onButton;
        internal Border Solo => soloButton;
        internal Border Arm => armButton;
        internal Border Cue => cueButton;
        internal Control ColourMark => colourMark;
        internal IReadOnlyList<Button> InsertButtons => insertsPanel?.Children.OfType<Button>().Where(b => b.Tag is IMachine).ToList() ?? new List<Button>();
        internal Button AddEffectButton => insertsPanel?.Children.OfType<Button>().FirstOrDefault(b => b.Tag as string == "+ Effect");
        internal bool IsOn => Machine == null || !Machine.IsMuted;
        internal bool IsSoloLit => Machine?.IsSoloed == true;
        // The arm state is looked up over every track: it is kept until the layout says an input or the structure changed (forty strips ask forty times a second).
        bool armKnown, armCached;
        int armAge;

        internal bool IsArmLit
        {
            get
            {
                if (Machine == null) return false;
                if (++armAge > 20) armKnown = false;       // an arm from elsewhere (the Arrangement's, an agent's) is seen within a second
                if (!armKnown) { armAge = 0; armCached = Core.SessionLayout?.IsMachineArmed(Machine) == true; armKnown = attached; }
                return armCached;
            }
        }

        bool attached;

        void ArmMayHaveChanged() => armKnown = false;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            attached = true;
            armKnown = false;
            if (Core.SessionLayout is { } layout)
            {
                layout.InputsChanged += ArmMayHaveChanged;
                layout.Changed += ArmMayHaveChanged;
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            attached = false;
            if (Core.SessionLayout is { } layout)
            {
                layout.InputsChanged -= ArmMayHaveChanged;
                layout.Changed -= ArmMayHaveChanged;
            }
            base.OnDetachedFromVisualTree(e);
        }

        public MixerStripView(IMixerHost host, StripSpec spec, MixerDensity density)
        {
            this.host = host;
            this.density = density;
            Spec = spec;
            var opts = host.Options;
            bool master = spec.Kind == StripKind.Master;
            bool full = density == MixerDensity.Full, header = density == MixerDensity.Header;
            AutomationProperties.SetName(this, $"{spec.Name} strip");

            if (header)
            {
                BuildHeader();
                return;
            }

            Background = SessionPalette.MixerBack;
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(full ? 6 : 3);
            Margin = new Thickness(0, 0, full ? 8 : 2, 0);
            Width = opts.Width > 0 ? opts.Width : full ? (master ? 150 : FullWidth) : CompactWidth;

            var rows = new List<RowDefinition>();
            var grid = new Grid();
            void Row(Control c, GridLength? h = null)
            {
                grid.RowDefinitions.Add(new RowDefinition(h ?? GridLength.Auto));
                Grid.SetRow(c, grid.RowDefinitions.Count - 1);
                grid.Children.Add(c);
            }

            // Colour and name.
            colourMark = new Border { Background = ColourBrush(), Height = full ? 6 : 9, Width = full ? double.NaN : 9, CornerRadius = new CornerRadius(full ? 0 : 5), HorizontalAlignment = full ? HorizontalAlignment.Stretch : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(colourMark, "Track colour");
            nameText = new TextBlock { Text = spec.Name, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            kindText = new TextBlock { Text = spec.Subtitle, FontSize = 11, Foreground = SessionPalette.LabelText, TextTrimming = TextTrimming.CharacterEllipsis };
            if (full)
            {
                Row(colourMark);
                var head = new StackPanel { Margin = new Thickness(8, 6, 8, 4), Spacing = 1, Children = { nameText, kindText } };
                Row(head);
            }
            else
            {
                var head = new StackPanel { Margin = new Thickness(6, 5, 6, 0), Spacing = 1 };
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { colourMark, nameText } };
                nameText.MaxWidth = CompactWidth - 30;
                head.Children.Add(line);
                head.Children.Add(kindText);
                Row(head);
            }
            if (spec.Kind == StripKind.Group)
            {
                kindText.Cursor = Hand;
                ToolTip.SetTip(kindText, "Click to fold the group's tracks into this strip, or to unfold them");
                kindText.PointerPressed += (s, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { host.ToggleFold(spec.Id); e.Handled = true; } };
            }
            PointerPressed += OnStripPressed;
            foreach (Control part in new Control[] { colourMark, nameText, kindText })
                part.AddHandler(PointerReleasedEvent, (s, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Right) return;
                    ShowColourMenu((Control)s);
                    e.Handled = true;
                }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

            // I/O: the track's inputs (Audio From and MIDI From), as the Session mixer showed them; the others keep the space.
            if (opts.IO && !master && (full || density == MixerDensity.Compact))
            {
                if (spec.Kind == StripKind.Track && SessionLayout.CanArm(spec.Machine))
                    Row(new TrackInputView(Core, spec.Machine, full ? InputLayoutKind.Strip : InputLayoutKind.Strip) { Margin = new Thickness(full ? 6 : 4, 2, full ? 6 : 4, 4) });
                else
                    Row(new Border { Height = TrackInputView.StripHeight + 6 });
            }

            // Inserts: the machines of the track in signal order; click opens one in Detail ▸ Device, "+ Effect" puts one after the last.
            if (full && opts.Inserts)
            {
                insertsPanel = new StackPanel { Spacing = 3, Margin = new Thickness(8, 4, 8, 6) };
                insertsPanel.Children.Add(Label("Inserts"));
                foreach (var m in spec.Inserts)
                    insertsPanel.Children.Add(InsertButton(m));
                insertsPanel.Children.Add(AddEffect());
                Row(WithTop(insertsPanel));
            }

            // Sends: one bar per return (tracks only; the others keep the rows so the strips line up).
            if (opts.Sends && (full || density == MixerDensity.Compact))
            {
                var sendBox = new StackPanel { Margin = new Thickness(full ? 8 : 5, 4, full ? 8 : 5, 0), Spacing = 2 };
                if (full) sendBox.Children.Add(Label("Sends"));
                for (int r = 0; r < host.Returns.Count; r++)
                {
                    if (spec.Kind == StripKind.Track)
                        sendBox.Children.Add(MakeSend(r));
                    else
                        sendBox.Children.Add(new Border { Height = 16 });
                }
                if (host.Returns.Count > 0) Row(full ? WithTop(sendBox) : sendBox);
            }

            // Pan
            if (!master)
            {
                pan = new MixerBar(false, true, SessionPalette.Pan) { Height = 16, Caption = "Pan", DefaultValue = 0.5, Margin = new Thickness(full ? 8 : 5, 4, full ? 8 : 5, 2) };
                ToolTip.SetTip(pan, "Pan: the pan of the machine's output connection(s)\ndrag or wheel · Shift for fine · double-click: centre");
                AutomationProperties.SetName(pan, $"Pan of {spec.Name}");
                WirePan();
                Row(pan);
            }
            else
            {
                Row(new Border { Height = 22 });
            }

            // Fader, meter and its dB.
            fader = new MixerBar(true, false, master ? SessionPalette.Thumb : SessionPalette.Fader)
            {
                Width = 26, MinHeight = full ? 150 : 96,
                DefaultValue = master ? 1 : SessionMixer.UnityFader,
                Mark = master ? null : SessionMixer.UnityFader,
            };
            ToolTip.SetTip(fader, master
                ? "Master volume: the Master's Volume parameter, in dB\ndrag or wheel · Shift for fine · double-click: 0 dB"
                : "Volume: the amp of the machine's output connection(s), the same as in the Machine View\ndrag or wheel · Shift for fine · double-click: 0 dB");
            AutomationProperties.SetName(fader, $"Volume of {spec.Name}");
            WireFader();
            meter = new MeterBar { Width = full ? 14 : 11, MinHeight = fader.MinHeight, Margin = new Thickness(6, 0, 0, 0) };
            var faderRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            faderRow.Children.Add(fader);
            faderRow.Children.Add(meter);
            var faderCell = new Grid { Children = { faderRow } };
            Row(faderCell, new GridLength(1, GridUnitType.Star));

            dbText = new TextBlock { FontFamily = Mono, FontSize = 11, Foreground = SessionPalette.LabelText, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
            Row(dbText);
            if (master && full)
            {
                peakText = new TextBlock { Text = "peak –", FontFamily = Mono, FontSize = 11, Foreground = SessionPalette.LabelText, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2), Cursor = Hand };
                ToolTip.SetTip(peakText, "The loudest level since the last reset, post-fader (click to reset). Peak only: the studio has no loudness meter on the Master yet.");
                peakText.PointerPressed += (s, e) => { masterHoldDb = double.NegativeInfinity; peakMax = 0; e.Handled = true; };
                Row(peakText);
                // The cue output (Session ⚙ > Cue output): the machine a strip's Cue sends to, named under the peak.
                cueOutText = new TextBlock { FontSize = 11, Foreground = SessionPalette.LabelText, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(cueOutText, "The cue output: the machine the strips' C sends to. Choose it in the Session View's ⚙ menu > Cue output.");
                AutomationProperties.SetName(cueOutText, "Cue output");
                Row(cueOutText);
            }

            // Crossfader (the Session View): the sides on a strip, the crossfader itself on the Master.
            if (opts.Crossfader)
            {
                var crossRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 20, Margin = new Thickness(0, 0, 0, 3) };
                if (master)
                {
                    crossfader = new MixerBar(false, true, SessionPalette.Thumb) { Width = 128, Height = 16, Caption = "A  ·  B", DefaultValue = 0.5 };
                    ToolTip.SetTip(crossfader, opts.CrossfaderTip?.Invoke() ?? "Crossfader: fades between the tracks assigned to A and to B (the A/B chips); others play on.\ndrag or wheel · Shift for fine · double-click: centre · right-click: curve");
                    AutomationProperties.SetName(crossfader, "Crossfader");
                    crossfader.Changed += v =>
                    {
                        crossfader.ValueText = CrossfaderText(v);
                        Core.SessionLayout?.SetCrossfaderPosition(v);
                    };
                    crossfader.AddHandler(PointerReleasedEvent, (s, e) =>
                    {
                        if (e.InitialPressMouseButton != MouseButton.Right) return;
                        opts.CrossfaderMenu?.Invoke(crossfader);
                        e.Handled = true;
                    }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                    crossRow.Children.Add(crossfader);
                }
                else if (spec.Machine != null)
                {
                    (sideA, sideAText) = Chip("A", "Crossfader side A: the crossfader fades this track out towards B", () => ToggleSide("A"));
                    (sideB, sideBText) = Chip("B", "Crossfader side B: the crossfader fades this track out towards A", () => ToggleSide("B"));
                    crossRow.Children.Add(sideA);
                    crossRow.Children.Add(sideB);
                }
                Row(crossRow);
            }

            // On, Solo, Arm, Cue.
            var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 2, 4, 6) };
            if (!master)
            {
                (onButton, onText) = Chip("On", "On: the machine plays. Off mutes it (the M of the Machine View)", ToggleOn);
                (soloButton, soloText) = Chip("S", "Solo", ToggleSolo);
                buttons.Children.Add(onButton);
                buttons.Children.Add(soloButton);
                if (opts.Arm && spec.Kind == StripKind.Track && SessionLayout.CanArm(spec.Machine))
                {
                    (armButton, armText) = Chip("●", "Arm: the track listens to its input (and the keyboard) and a take can be recorded", ToggleArm);
                    AutomationProperties.SetName(armButton, $"Arm {spec.Name}");
                    buttons.Children.Add(armButton);
                }
                if (full || opts.Cue)
                {
                    (cueButton, cueText) = Chip("C", "Cue: also play this to the cue output (Session ⚙ → Cue output)", ToggleCue);
                    buttons.Children.Add(cueButton);
                }
                AutomationProperties.SetName(onButton, $"On {spec.Name}");
                AutomationProperties.SetName(soloButton, $"Solo {spec.Name}");
            }
            Row(buttons);
            Child = grid;
            Refresh();
        }

        // ------------------------------------------------------------------ the Arrangement's header: the parts laid out sideways

        void BuildHeader()
        {
            MixerBar panBar, faderBar;
            MeterBar meterBar;
            var opts = host.Options;
            var spec = Spec;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            faderBar = new MixerBar(false, false, SessionPalette.Fader) { Width = 52, Height = 14, DefaultValue = SessionMixer.UnityFader, Caption = "" };
            ToolTip.SetTip(faderBar, "Volume: the same fader as the mixer's (the amp of the machine's output connection(s))\ndrag or wheel · Shift for fine · double-click: 0 dB");
            AutomationProperties.SetName(faderBar, $"Volume of {spec.Name}");
            meterBar = new MeterBar(sideways: true) { Width = 24, Height = 14 };
            panBar = new MixerBar(false, true, SessionPalette.Pan) { Width = 30, Height = 14, DefaultValue = 0.5 };
            ToolTip.SetTip(panBar, "Pan: the pan of the machine's output connection(s)\ndrag or wheel · Shift for fine · double-click: centre");
            AutomationProperties.SetName(panBar, $"Pan of {spec.Name}");
            pan = panBar;
            fader = faderBar;
            meter = meterBar;
            WireFader();
            WirePan();
            row.Children.Add(faderBar);
            row.Children.Add(meterBar);
            row.Children.Add(panBar);
            if (opts.Sends)
                for (int r = 0; r < Math.Min(2, host.Returns.Count); r++)
                {
                    var send = MakeSend(r, narrow: true);
                    send.Width = 14;
                    row.Children.Add(send);
                }
            Child = row;
            Refresh();
        }

        // ------------------------------------------------------------------ parts

        static TextBlock Label(string text) => new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, Foreground = SessionPalette.LabelText };

        static Control WithTop(Control c) => new Border { BorderBrush = StudioTokens.SurfaceBorderBrush, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 2, 0, 0), Child = c };

        IBrush ColourBrush() =>
            Spec.Kind == StripKind.Master ? SessionPalette.Thumb : TrackColour.BrushOf(Spec.Machine) ?? SessionPalette.Clip;

        Button InsertButton(IMachine machine)
        {
            var b = new Button { Content = machine.Name, Tag = machine, Height = 22, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            ToolTip.SetTip(b, $"{machine.Name}: click opens it in Detail ▸ Device");
            AutomationProperties.SetName(b, $"Insert {machine.Name}");
            b.Click += (s, e) => host.OpenDevice(machine);
            return b;
        }

        Button AddEffect()
        {
            var b = new Button { Content = "+ Effect", Tag = "+ Effect", Height = 22, MinHeight = 0, Padding = new Thickness(6, 0), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Foreground = SessionPalette.LabelText };
            ToolTip.SetTip(b, "Put an effect after the last insert of this strip");
            AutomationProperties.SetName(b, $"Add effect to {Spec.Name}");
            b.Click += (s, e) => ShowAddEffect(b);
            return b;
        }

        /// <summary>The wire an effect would go into: the last insert's (or the strip's machine's) main output.</summary>
        internal IMachineConnection LastWire()
        {
            var last = Spec.Kind == StripKind.Track ? (Spec.Inserts.LastOrDefault() ?? Spec.Machine)
                : Spec.Kind == StripKind.Master ? Spec.Inserts.LastOrDefault()
                : Spec.Inserts.LastOrDefault() ?? Spec.Machine;
            return SessionMixer.MainOutputs(Doc, last, Returns).FirstOrDefault();
        }

        void ShowAddEffect(Control anchor)
        {
            var wire = LastWire();
            if (wire == null) return;
            var menu = new MachineMenus(Core, null).ForInsert(wire);
            LastMenu = menu;
            menu.Placement = Avalonia.Controls.PlacementMode.Bottom;
            menu.Open(anchor);
        }

        internal ContextMenu LastMenu { get; private set; }

        MixerBar MakeSend(int r, bool narrow = false)
        {
            var target = host.Returns[r];
            var send = new MixerBar(false, false, SessionPalette.Send) { Height = 16, Caption = narrow ? "" : MixerModel.ReturnLetter(r), DefaultValue = 0 };
            ToolTip.SetTip(send, $"Send to {target.Name} (return {MixerModel.ReturnLetter(r)})\ndrag or wheel · Shift for fine · double-click: off");
            AutomationProperties.SetName(send, $"Send {MixerModel.ReturnLetter(r)} of {Spec.Name}");
            WireSend(send, target);
            sends.Add(send);
            sendTargets.Add(target);
            return send;
        }

        (Border, TextBlock) Chip(string label, string tip, Action click)
        {
            var text = new TextBlock { Text = label, FontSize = StudioTokens.MinFontSize, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = SessionPalette.LabelText };
            var chip = new Border
            {
                MinWidth = StudioTokens.MinControl, Height = StudioTokens.MinControl, Margin = new Thickness(2, 1, 0, 1), Padding = new Thickness(label.Length > 1 ? 4 : 0, 0),
                Background = SessionPalette.Scene, Cursor = Hand, Child = text, CornerRadius = new CornerRadius(3),
            };
            ToolTip.SetTip(chip, tip);
            chip.PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(chip).Properties.IsLeftButtonPressed) return;
                click();
                e.Handled = true;
            };
            return (chip, text);
        }

        static void SetChip(Border chip, TextBlock text, bool active, IBrush brush, bool enabled = true)
        {
            if (chip == null) return;
            chip.Background = active ? brush : SessionPalette.Scene;
            text.Foreground = active ? SessionPalette.TextOn(brush) : SessionPalette.LabelText;
            chip.Opacity = enabled ? 1 : 0.4;
            chip.IsHitTestVisible = enabled;
        }

        // ------------------------------------------------------------------ gestures

        /// <summary>The track's colour: the palette's choices (the state colours aren't offered), one undo step. The user's choice always wins.</summary>
        internal ContextMenu ShowColourMenu(Control anchor, bool open = true)
        {
            if (Spec.Machine == null || Spec.Kind == StripKind.Master) return null;
            var machine = Spec.Machine;
            int current = TrackColour.IndexOf(machine);
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Track colour", IsEnabled = false });
            int count = SessionPalette.ColorChoices.Length;
            foreach (var (index, name, colour) in SessionPalette.OfferedColours(current))
            {
                int pick = index;
                var item = new MenuItem { Header = name, Tag = name, Icon = new Border { Width = 14, Height = 14, Background = SessionPalette.BrushOf(colour) } };
                if (current >= 0 && current % count == index) item.FontWeight = FontWeight.Bold;
                item.Click += (s, e) => MixerEdits.SetTrackColour(Core, machine, pick);
                menu.Items.Add(item);
            }
            LastMenu = menu;
            if (open) menu.Open(anchor);
            return menu;
        }

        void OnStripPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Handled) return;
            if (Spec.Machine != null) StudioSelection.For(Core).SelectMachine(Spec.Machine, this);
        }

        void WireFader()
        {
            MixerEdits.WireSnapshot before = null;
            int masterBefore = 0;
            bool open = false;
            void End()
            {
                if (!open) return;
                open = false;
                if (Spec.Kind == StripKind.Master) MixerEdits.CommitMasterVolume(Core, masterBefore);
                else before?.Commit(Core, "Mixer volume", new[] { Spec.Machine });
                before = null;
            }
            fader.Changed += v =>
            {
                bool wheel = !fader.IsDragging;
                if (!open)
                {
                    open = true;
                    if (Spec.Kind == StripKind.Master) masterBefore = MixerEdits.MasterVolume(Core)?.GetValue(0) ?? 0;
                    else before = MixerEdits.WireSnapshot.Of(new[] { Spec.Machine });
                }
                if (Spec.Kind == StripKind.Master) Core.MasterVolume = SessionMixer.MasterGainOf(v);
                else SessionMixer.SetAmp(Doc, Spec.Machine, SessionMixer.AmpOf(v), Returns);
                UpdateDbText();
                if (wheel) End();
            };
            fader.DragEnded += End;
        }

        void WirePan()
        {
            MixerEdits.WireSnapshot before = null;
            void End() { before?.Commit(Core, "Mixer pan", new[] { Spec.Machine }); before = null; }
            pan.Changed += v =>
            {
                bool wheel = !pan.IsDragging;
                before ??= MixerEdits.WireSnapshot.Of(new[] { Spec.Machine });
                SessionMixer.SetPan(Doc, Spec.Machine, SessionMixer.PanValue(v * 2 - 1), Returns);
                // The text follows the drag, as the fader's does (not the next refresh).
                int p = SessionMixer.GetPan(Doc, Spec.Machine, Returns);
                pan.ValueText = p >= 0 && density != MixerDensity.Header ? SessionMixer.FormatPan(p) : "";
                if (wheel) End();
            };
            pan.DragEnded += End;
        }

        void WireSend(MixerBar send, IMachine target)
        {
            MixerEdits.WireSnapshot before = null;
            bool grouped = false;
            void End()
            {
                before?.Commit(Core, "Mixer send", new[] { Spec.Machine });
                before = null;
                if (grouped) { Core.SongCore.EndActionGroup(); grouped = false; }
            }
            send.Changed += v =>
            {
                bool wheel = !send.IsDragging;
                if (before == null)
                {
                    before = MixerEdits.WireSnapshot.Of(new[] { Spec.Machine });
                    // Raising a send from nothing makes the wire (a step of its own): the gesture is one step, the wire's with it.
                    if (SessionMixer.ConnectionTo(Spec.Machine, target) == null) { Core.SongCore.BeginActionGroup(); grouped = true; }
                }
                SessionMixer.SetSend(Spec.Machine, target, SessionMixer.AmpOf(v));
                int s = SessionMixer.GetSend(Spec.Machine, target);
                send.ValueText = density == MixerDensity.Header ? "" : s <= 0 ? "off" : SessionMixer.FormatAmp(s);
                if (wheel) End();
            };
            send.DragEnded += End;
        }

        void ToggleOn()
        {
            if (Machine == null) return;
            MixerEdits.SetMute(Core, Machine, !Machine.IsMuted);
            Refresh();
        }

        void ToggleSolo()
        {
            if (Machine == null) return;
            MixerEdits.SetSolo(Core, Machine, !Machine.IsSoloed);
            Refresh();
        }

        void ToggleArm()
        {
            var layout = Core.SessionLayout;
            if (Machine == null || layout == null) return;
            layout.SetMachineArmed(Machine, !layout.IsMachineArmed(Machine));
            Refresh();
        }

        void ToggleSide(string side)
        {
            var layout = Core.SessionLayout;
            if (Machine == null || layout == null) return;
            layout.SetCrossfade(Machine, layout.CrossfadeOf(Machine) == side ? null : side);
        }

        static string CrossfaderText(double v) =>
            Math.Abs(v - 0.5) < 0.005 ? "centre" : v < 0.5 ? $"A {(int)Math.Round((0.5 - v) * 200)}%" : $"B {(int)Math.Round((v - 0.5) * 200)}%";

        IMachine CueMachine => Core.SessionLayout?.MachineNamed(Doc?.CueMachine);

        void ToggleCue()
        {
            var cue = CueMachine;
            if (Machine == null || cue == null) return;
            SessionMixer.SetCue(Machine, cue, !SessionMixer.IsCued(Machine, cue));
            Refresh();
        }

        // ------------------------------------------------------------------ values

        void UpdateDbText()
        {
            if (density == MixerDensity.Header)
            {
                int a = SessionMixer.GetAmp(Doc, Spec.Machine, Returns);
                fader.ValueText = a < 0 ? "" : SessionMixer.FormatAmp(a);
                return;
            }
            if (dbText == null) return;
            if (Spec.Kind == StripKind.Master)
            {
                double gain = Core.MasterVolume;
                dbText.Text = gain <= 0 ? "-inf dB" : SessionMixer.FormatDb(BuzzGUI.Common.Decibel.FromAmplitude(gain)) + " dB";
                return;
            }
            int amp = SessionMixer.GetAmp(Doc, Spec.Machine, Returns);
            dbText.Text = amp < 0 ? "–" : SessionMixer.FormatAmp(amp) + " dB";
        }

        /// <summary>Reads volume, pan, sends, the flags and the colour from the machine graph.</summary>
        public void Refresh()
        {
            var doc = Doc;
            if (Spec.Kind == StripKind.Master)
            {
                if (!fader.IsDragging) fader.Value = SessionMixer.MasterFaderOf(Core.MasterVolume);
                UpdateDbText();
                if (cueOutText != null) cueOutText.Text = string.IsNullOrEmpty(doc?.CueMachine) ? "cue: none" : "cue: " + doc.CueMachine;
                if (crossfader != null)
                {
                    if (!crossfader.IsDragging) crossfader.Value = doc?.CrossfaderPosition ?? 0.5;
                    crossfader.ValueText = CrossfaderText(crossfader.Value);
                }
                return;
            }
            if (sideA != null)
            {
                string side = Core.SessionLayout?.CrossfadeOf(Machine);
                SetChip(sideA, sideAText, side == "A", SessionPalette.CrossA);
                SetChip(sideB, sideBText, side == "B", SessionPalette.CrossB);
            }

            int amp = SessionMixer.GetAmp(doc, Spec.Machine, Returns);
            fader.IsEnabled = amp >= 0;
            if (amp >= 0 && !fader.IsDragging) fader.Value = SessionMixer.FaderOf(amp);
            UpdateDbText();

            int p = SessionMixer.GetPan(doc, Spec.Machine, Returns);
            pan.IsEnabled = p >= 0;
            if (p >= 0 && !pan.IsDragging) pan.Value = (SessionMixer.PanOf(p) + 1) / 2;
            pan.ValueText = p >= 0 && density != MixerDensity.Header ? SessionMixer.FormatPan(p) : "";

            for (int r = 0; r < sends.Count; r++)
            {
                var send = sends[r];
                var target = sendTargets[r];
                bool can = SessionMixer.CanSend(Spec.Machine, target);
                send.IsEnabled = can;
                int s = SessionMixer.GetSend(Spec.Machine, target);
                if (!send.IsDragging) send.Value = SessionMixer.FaderOf(s);
                send.ValueText = !can || density == MixerDensity.Header ? "" : s <= 0 ? "off" : SessionMixer.FormatAmp(s);
            }

            if (onButton != null)
            {
                SetChip(onButton, onText, Machine?.IsMuted == false, SessionPalette.MeterLow, Machine != null);
                SetChip(soloButton, soloText, Machine?.IsSoloed == true, SessionPalette.Solo, Machine != null);
            }
            if (armButton != null) SetChip(armButton, armText, IsArmLit, SessionPalette.Record);
            if (cueButton != null)
            {
                var cue = CueMachine;
                bool canCue = Machine != null && cue != null && Machine != cue;
                SetChip(cueButton, cueText, canCue && SessionMixer.IsCued(Machine, cue), SessionPalette.Cue, canCue);
            }
            if (colourMark != null) colourMark.Background = ColourBrush();
            bool selected = Spec.Machine != null && StudioSelection.For(Core).Machine == Spec.Machine;
            if (density != MixerDensity.Header) BorderBrush = selected ? StudioTokens.AccentBrush : StudioTokens.SurfaceBorderBrush;
            if (density != MixerDensity.Header) BorderThickness = selected ? new Thickness(2) : new Thickness(1);
        }

        /// <summary>Moves the meter to the latest peak; it falls by at most <paramref name="fall"/> and the held peak waits a second and a half.</summary>
        public void RefreshMeter(double fall)
        {
            double l, r;
            if (Spec.Kind == StripKind.Master)
            {
                // The master's meter data (ReBuzz's VU meter, 0..1 over -80..0 dB).
                var vu = Core.VUMeterLevel;
                l = vu == null || vu.Item1 <= 0 ? 0 : SessionMixer.LevelOfDb(vu.Item1 * 80 - 80);
                r = vu == null || vu.Item2 <= 0 ? 0 : SessionMixer.LevelOfDb(vu.Item2 * 80 - 80);
            }
            else
            {
                var peak = SessionMixer.MainPeak(Doc, Spec.Machine, Returns);
                l = SessionMixer.MeterLevel(peak.Left);
                r = SessionMixer.MeterLevel(peak.Right);
            }
            levelL = Math.Max(l, levelL - fall);
            levelR = Math.Max(r, levelR - fall);
            double dt = fall / 0.9;
            double top = Math.Max(l, r);
            if (top >= Math.Max(holdL, holdR) - 1e-9) { holdL = l; holdR = r; holdAge = 0; }
            else
            {
                holdAge += dt;
                if (holdAge > 1.5) { holdL = Math.Max(levelL, holdL - fall * 0.5); holdR = Math.Max(levelR, holdR - fall * 0.5); }
            }
            meter?.Set(levelL, levelR, holdL, holdR);
            if (peakText != null)
            {
                if (top > peakMax) peakMax = top;
                double db = peakMax <= 0 ? double.NegativeInfinity : peakMax * (SessionMixer.MeterTopDb - SessionMixer.MeterFloorDb) + SessionMixer.MeterFloorDb;
                peakText.Text = double.IsNegativeInfinity(db) ? "peak –" : $"peak {SessionMixer.FormatDb(db)} dB";
            }
        }
    }
}
