// Fork (docs/specs/neo-audio.md "Inputs and arm"): the choosers of a track's inputs, one control for every view that shows them (the Session
// View's I/O row, the Arrangement's track header, the Inspector) and the menu of the machine box's arm dot. Everything it shows is the layout's:
// the same state whichever view changes it.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Session
{
    using Control = Avalonia.Controls.Control;

    /// <summary>A track's input level: a bar that falls, a peak that holds, and a clip light (click it to clear).</summary>
    internal sealed class InputMeterBar : Control
    {
        double level, hold;
        bool clipped;

        public InputMeterBar()
        {
            Height = 8;
            MinWidth = 40;
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, "Input level (-60 to 0 dB); the square lights at full scale: click to clear it");
            Avalonia.Automation.AutomationProperties.SetName(this, "Input level");
        }

        public double Level => level;
        public bool Clipped => clipped;
        public Action ClearClip { get; set; }

        public void Set(InputMeterModel m)
        {
            double l = m?.Level ?? 0, h = m?.Hold ?? 0;
            bool c = m?.Clipped ?? false;
            if (Math.Abs(l - level) < 0.004 && Math.Abs(h - hold) < 0.004 && c == clipped) return;
            level = l;
            hold = h;
            clipped = c;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            ClearClip?.Invoke();
            e.Handled = true;
        }

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            double led = h;
            double bar = w - led - 2;
            dc.FillRectangle(SessionPalette.Groove, new Rect(0, 0, bar, h));
            double x = Math.Clamp(level, 0, 1) * bar;
            double yellow = 0.8 * bar, red = 0.95 * bar;
            if (x > 0) dc.FillRectangle(SessionPalette.MeterLow, new Rect(0, 0, Math.Min(x, yellow), h));
            if (x > yellow) dc.FillRectangle(SessionPalette.MeterMid, new Rect(yellow, 0, Math.Min(x, red) - yellow, h));
            if (x > red) dc.FillRectangle(SessionPalette.MeterHigh, new Rect(red, 0, x - red, h));
            if (hold > 0.01) dc.FillRectangle(SessionPalette.Text, new Rect(Math.Min(bar - 1.5, hold * bar), 0, 1.5, h));
            dc.FillRectangle(clipped ? SessionPalette.MeterHigh : SessionPalette.Groove, new Rect(bar + 2, 0, led, h));
        }
    }

    /// <summary>How the choosers are laid out: Full (the Inspector's), Strip (narrow, under a Session column) or Compact (the Arrangement's header).</summary>
    internal enum InputLayoutKind { Full, Strip, Compact }

    /// <summary>
    /// The inputs of a machine's tracks: the arm dot, Audio From (an audio track), MIDI From and its channel, Monitor and the input meter.
    /// Compact keeps one chooser and the meter (the Arrangement's header).
    /// </summary>
    internal sealed class TrackInputView : Border
    {
        readonly ReBuzzCore core;
        readonly IMachine machine;
        readonly InputLayoutKind kind;
        readonly bool compact;
        readonly bool audio;
        readonly ComboBox audioBox = new();
        readonly ComboBox midiBox = new();
        readonly ComboBox channelBox = new();
        readonly InputMeterBar meter = new();
        readonly List<Button> monitor = new();
        readonly Button arm;
        readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(50) };
        readonly List<AudioFrom> audioChoices = new();
        readonly List<(MidiFromKind Kind, string Device)> midiChoices = new();
        bool building;
        SessionLayout subscribed;
        string lastKey;

        public TrackInputView(ReBuzzCore core, IMachine machine, InputLayoutKind kind = InputLayoutKind.Full)
        {
            this.core = core;
            this.machine = machine;
            this.kind = kind;
            compact = kind == InputLayoutKind.Compact;
            audio = SessionLayout.HasAudioInput(machine);
            Padding = new Thickness(0);
            Avalonia.Automation.AutomationProperties.SetName(this, "Input of " + machine?.Name);

            arm = Small("●", "Arm: the track listens to its input, and Record records it", () =>
            {
                var layout = core.SessionLayout;
                layout?.SetMachineArmed(machine, !layout.IsMachineArmed(machine));
            });
            Avalonia.Automation.AutomationProperties.SetName(arm, "Arm " + machine?.Name);

            foreach (var box in new[] { audioBox, midiBox, channelBox })
            {
                box.FontSize = StudioTokens.MinFontSize;
                box.MinHeight = 22;
                box.Padding = new Thickness(6, 1);
                box.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            audioBox.SelectionChanged += (s, e) =>
            {
                if (building || audioBox.SelectedIndex < 0 || audioBox.SelectedIndex >= audioChoices.Count) return;
                core.SessionLayout?.SetAudioFrom(machine, audioChoices[audioBox.SelectedIndex]);
            };
            midiBox.SelectionChanged += (s, e) =>
            {
                if (building || midiBox.SelectedIndex < 0 || midiBox.SelectedIndex >= midiChoices.Count) return;
                var (kind, device) = midiChoices[midiBox.SelectedIndex];
                core.SessionLayout?.SetMidiFrom(machine, kind, device);
            };
            channelBox.SelectionChanged += (s, e) =>
            {
                if (building || channelBox.SelectedIndex < 0) return;
                core.SessionLayout?.SetMidiChannel(machine, channelBox.SelectedIndex);
            };
            ToolTip.SetTip(audioBox, "Audio From: None, an input of the sound card (In 1, In 1/2...), another track (to resample it), or Wired inputs (what is connected to it)");
            ToolTip.SetTip(midiBox, "MIDI From: all inputs (a control surface's pads stay its own), one device, or the computer keyboard");
            ToolTip.SetTip(channelBox, "MIDI channel: All or 1 to 16");
            Avalonia.Automation.AutomationProperties.SetName(audioBox, "Audio From");
            Avalonia.Automation.AutomationProperties.SetName(midiBox, "MIDI From");
            Avalonia.Automation.AutomationProperties.SetName(channelBox, "MIDI channel");

            channelBox.ItemsSource = new[] { "All" }.Concat(Enumerable.Range(1, 16).Select(i => i.ToString())).ToList();

            string[] names = { "In", "Auto", "Off" };
            string[] tips = { "Monitor In: always hear the input", "Monitor Auto: hear the input while armed", "Monitor Off: never hear the input" };
            for (int i = 0; i < 3; i++)
            {
                int mode = i;
                var b = Small(names[i], tips[i], () => core.SessionLayout?.SetMonitor(machine, new[] { 1, 0, 2 }[mode]));
                b.Width = kind == InputLayoutKind.Strip ? 34 : 38;
                Avalonia.Automation.AutomationProperties.SetName(b, "Monitor " + names[i]);
                monitor.Add(b);
            }
            meter.ClearClip = () => core.SessionLayout?.InputMeterOf(machine)?.ClearClip();

            Child = kind == InputLayoutKind.Compact ? BuildCompact() : kind == InputLayoutKind.Strip ? BuildStrip() : BuildFull();
            poll.Tick += (s, e) => Tick();
        }

        internal Button ArmButton => arm;
        internal ComboBox AudioBox => audioBox;
        internal ComboBox MidiBox => midiBox;
        internal ComboBox ChannelBox => channelBox;
        internal InputMeterBar Meter => meter;
        internal IReadOnlyList<Button> MonitorButtons => monitor;
        internal bool IsCompact => compact;

        Button Small(string text, string tip, Action click)
        {
            var b = new Button
            {
                Content = text,
                FontSize = StudioTokens.MinFontSize,
                Height = 22,
                MinWidth = 24,
                Padding = new Thickness(4, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => click();
            return b;
        }

        Control BuildFull()
        {
            var stack = new StackPanel { Spacing = 4 };
            var top = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(arm, Dock.Left);
            arm.Margin = new Thickness(0, 0, 4, 0);
            top.Children.Add(arm);
            var rows = new StackPanel { Spacing = 4 };
            if (audio) rows.Children.Add(Labelled("Audio", audioBox));
            var midiRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,56"), ColumnSpacing = 4 };
            Grid.SetColumn(midiBox, 0);
            var ch = new TextBlock { Text = "Ch", FontSize = StudioTokens.MinFontSize, Foreground = SessionPalette.LabelText, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(ch, 1);
            Grid.SetColumn(channelBox, 2);
            midiRow.Children.Add(midiBox);
            midiRow.Children.Add(ch);
            midiRow.Children.Add(channelBox);
            rows.Children.Add(Labelled("MIDI", midiRow));
            top.Children.Add(rows);
            stack.Children.Add(top);
            if (audio)
            {
                var mon = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                mon.Children.Add(new TextBlock { Text = "Monitor", FontSize = StudioTokens.MinFontSize, Foreground = SessionPalette.LabelText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
                foreach (var b in monitor) mon.Children.Add(b);
                stack.Children.Add(mon);
                meter.Margin = new Thickness(0, 2, 0, 0);
                stack.Children.Add(meter);
            }
            return stack;
        }

        static Control Labelled(string label, Control box)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("40,*") };
            var t = new TextBlock { Text = label, FontSize = StudioTokens.MinFontSize, Foreground = SessionPalette.LabelText, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(box, 1);
            g.Children.Add(t);
            g.Children.Add(box);
            return g;
        }

        /// <summary>The height of the strip layout, for the strips that have no inputs to keep their rows in line.</summary>
        public const double StripHeight = 112;

        Control BuildStrip()
        {
            var stack = new StackPanel { Spacing = 3, Height = StripHeight };
            if (audio) stack.Children.Add(audioBox);
            stack.Children.Add(midiBox);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4 };
            Grid.SetColumn(channelBox, 1);
            row.Children.Add(arm);
            row.Children.Add(channelBox);
            stack.Children.Add(row);
            if (audio)
            {
                var mon = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                foreach (var b in monitor) mon.Children.Add(b);
                stack.Children.Add(mon);
                stack.Children.Add(meter);
            }
            return stack;
        }

        Control BuildCompact()
        {
            // The Arrangement's header has its R for arm: the chooser and the meter.
            var g = new StackPanel { Spacing = 2 };
            g.Children.Add(audio ? audioBox : midiBox);
            if (audio) g.Children.Add(meter);
            return g;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            subscribed = core.SessionLayout;
            if (subscribed != null)
            {
                subscribed.Changed += Refresh;
                subscribed.InputsChanged += Refresh;
            }
            poll.Start();
            Refresh();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            poll.Stop();
            if (subscribed != null)
            {
                subscribed.Changed -= Refresh;
                subscribed.InputsChanged -= Refresh;
                subscribed = null;
            }
        }

        /// <summary>One poll: the meter, and the choosers if what they show changed (a timer does this; tests call it).</summary>
        internal void Poll() => Tick();

        void Tick()
        {
            var layout = core.SessionLayout;
            if (layout == null) return;
            if (audio) meter.Set(layout.InputMeterOf(machine));
            if (lastKey != Key(layout)) Refresh();
        }

        /// <summary>A string that changes whenever what the choosers show does (devices coming and going, arm).</summary>
        string Key(SessionLayout layout)
        {
            var input = layout.TrackInputOf(machine);
            return string.Join("|", layout.IsMachineArmed(machine), layout.MonitorOf(machine), input.Audio.ToText(), input.Midi, input.Device, input.Channel,
                layout.AudioChannelNames().Count, string.Join(",", layout.MidiDevices(machine).Select(d => d.Name + d.Connected)),
                audio ? string.Join(",", layout.TrackSources(machine).Select(m => m.Name)) : "");
        }

        /// <summary>Reads the state: the lists to choose from and what is chosen.</summary>
        public void Refresh()
        {
            var layout = core.SessionLayout;
            if (layout == null || machine == null) return;
            building = true;
            try
            {
                lastKey = Key(layout);
                var input = layout.TrackInputOf(machine);

                bool armed = layout.IsMachineArmed(machine);
                arm.Foreground = armed ? SessionPalette.TextOn(SessionPalette.Record) : SessionPalette.LabelText;
                arm.Background = armed ? SessionPalette.Record : SessionPalette.Scene;

                if (audio)
                {
                    audioChoices.Clear();
                    audioChoices.AddRange(layout.AudioChoices(machine));
                    if (!audioChoices.Contains(input.Audio)) audioChoices.Add(input.Audio);      // an input the device no longer has
                    var labels = audioChoices.Select(a => LabelOf(layout, a)).ToList();
                    audioBox.ItemsSource = labels;
                    audioBox.SelectedIndex = audioChoices.IndexOf(input.Audio);

                    int mode = layout.MonitorOf(machine);
                    int[] modes = { 1, 0, 2 };
                    for (int i = 0; i < 3; i++)
                    {
                        bool on = modes[i] == mode;
                        monitor[i].Background = on ? SessionPalette.Fader : SessionPalette.Scene;
                        monitor[i].Foreground = on ? SessionPalette.TextOn(SessionPalette.Fader) : SessionPalette.LabelText;
                    }
                }

                midiChoices.Clear();
                var midiLabels = new List<string>();
                midiChoices.Add((MidiFromKind.All, ""));
                midiLabels.Add("All inputs");
                foreach (var (name, connected) in layout.MidiDevices(machine))
                {
                    midiChoices.Add((MidiFromKind.Device, name));
                    midiLabels.Add(connected ? name : name + " (not connected)");
                }
                midiChoices.Add((MidiFromKind.Keyboard, ""));
                midiLabels.Add(MidiDevice.KeyboardName);
                midiBox.ItemsSource = midiLabels;
                int at = midiChoices.FindIndex(c => c.Kind == input.Midi && (c.Kind != MidiFromKind.Device || string.Equals(c.Device, input.Device, StringComparison.OrdinalIgnoreCase)));
                midiBox.SelectedIndex = Math.Max(0, at);
                channelBox.SelectedIndex = Math.Clamp(input.Channel, 0, 16);
            }
            finally
            {
                building = false;
            }
        }

        static string LabelOf(SessionLayout layout, AudioFrom from)
        {
            string label = layout.AudioLabel(from);
            return from.IsDevice && from.ChannelsNeeded > layout.AudioChannelNames().Count ? label + " (not available)" : label;
        }
    }

    /// <summary>The Input menu of a machine: the choosers as a menu (the right-click of a machine box's arm dot).</summary>
    internal static class TrackInputMenu
    {
        public static ContextMenu Build(ReBuzzCore core, IMachine machine)
        {
            var menu = new ContextMenu();
            var layout = core.SessionLayout;
            if (layout == null || !SessionLayout.CanArm(machine)) return menu;
            layout.Ensure();
            var input = layout.TrackInputOf(machine);
            menu.Items.Add(new MenuItem { Header = "Input: " + machine.Name, IsEnabled = false });
            menu.Items.Add(Radio(layout.IsMachineArmed(machine) ? "Disarm" : "Arm", false, () => layout.SetMachineArmed(machine, !layout.IsMachineArmed(machine))));
            menu.Items.Add(new Separator());
            if (SessionLayout.HasAudioInput(machine))
            {
                var audio = new MenuItem { Header = "Audio From" };
                foreach (var choice in layout.AudioChoices(machine))
                {
                    var c = choice;
                    audio.Items.Add(Radio(layout.AudioLabel(c), c == input.Audio, () => layout.SetAudioFrom(machine, c), "audio"));
                }
                menu.Items.Add(audio);
            }
            var midi = new MenuItem { Header = "MIDI From" };
            midi.Items.Add(Radio("All inputs", input.Midi == MidiFromKind.All, () => layout.SetMidiFrom(machine, MidiFromKind.All), "midi"));
            foreach (var (name, connected) in layout.MidiDevices(machine))
            {
                string n = name;
                midi.Items.Add(Radio(connected ? n : n + " (not connected)", input.Midi == MidiFromKind.Device && string.Equals(input.Device, n, StringComparison.OrdinalIgnoreCase),
                    () => layout.SetMidiFrom(machine, MidiFromKind.Device, n), "midi"));
            }
            midi.Items.Add(Radio(MidiDevice.KeyboardName, input.Midi == MidiFromKind.Keyboard, () => layout.SetMidiFrom(machine, MidiFromKind.Keyboard), "midi"));
            menu.Items.Add(midi);
            var channel = new MenuItem { Header = "Channel" };
            for (int ch = 0; ch <= 16; ch++)
            {
                int c = ch;
                channel.Items.Add(Radio(c == 0 ? "All" : c.ToString(), input.Channel == c, () => layout.SetMidiChannel(machine, c), "channel"));
            }
            menu.Items.Add(channel);
            if (SessionLayout.HasAudioInput(machine))
            {
                var monitor = new MenuItem { Header = "Monitor" };
                int mode = layout.MonitorOf(machine);
                monitor.Items.Add(Radio("In", mode == 1, () => layout.SetMonitor(machine, 1), "monitor"));
                monitor.Items.Add(Radio("Auto", mode == 0, () => layout.SetMonitor(machine, 0), "monitor"));
                monitor.Items.Add(Radio("Off", mode == 2, () => layout.SetMonitor(machine, 2), "monitor"));
                menu.Items.Add(monitor);
            }
            return menu;
        }

        static MenuItem Radio(string header, bool on, Action run, string group = null) => new MenuItem
        {
            Header = header,
            Command = new Command(run),
            ToggleType = group == null ? MenuItemToggleType.None : MenuItemToggleType.Radio,
            GroupName = group,
            IsChecked = on,
        };
    }
}
