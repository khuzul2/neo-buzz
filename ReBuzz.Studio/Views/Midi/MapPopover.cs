// Fork (docs/specs/midi-mapping.md, MM-B): the one popover that holds everything about mapping a control: the scope (This track: name | Global), the
// range, invert and takeover, and what is being listened for. Map mode's click, the per-control MIDI button, a badge's Edit and the Device menu's
// old "Bind to MIDI Controller" all open this.

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Core;
using ReBuzz.Midi.Mapping;
using ReBuzz.Studio.Shell.Design;
using AvControl = Avalonia.Controls.Control;

namespace ReBuzz.Studio.Views.Midi
{
    internal sealed class MapPopover
    {
        readonly MapMode mode;
        readonly MapTarget target;
        readonly Popup popup;
        readonly TextBlock title = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly RadioButton trackScope = new RadioButton { GroupName = "PopScope" };
        readonly RadioButton globalScope = new RadioButton { Content = "Global", GroupName = "PopScope" };
        readonly TextBlock state = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 250 };
        readonly Button listen = new Button { Content = "Listen" };
        readonly Button unmap = new Button { Content = "Unmap" };
        // One row of two compact fields (from, to), in percent of the parameter's range; they read as one range, not two settings.
        readonly NumericUpDown low = Field(0);
        readonly NumericUpDown high = Field(100);
        readonly TextBlock rangeText = new TextBlock { Foreground = StudioTokens.TextSecondaryBrush };
        readonly CheckBox invert = new CheckBox { Content = "Invert" };
        readonly ComboBox takeover = new ComboBox { ItemsSource = new[] { "Pickup", "Jump", "Scale" }, SelectedIndex = 0, MinWidth = 90 };
        readonly TextBlock takeoverHelp = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, MaxWidth = 250 };
        readonly Button done = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right };
        readonly StackPanel body;
        bool updating;

        static NumericUpDown Field(decimal value) =>
            new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 1, Value = value, FormatString = "0", ShowButtonSpinner = false, Width = 56, MinHeight = 0, Height = 26, HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = StudioTokens.FontBody, VerticalAlignment = VerticalAlignment.Center };

        public MapPopover(MapMode mode, MapTarget target, AvControl anchor, Rect? around = null)
        {
            this.mode = mode;
            this.target = target;
            AutomationProperties.SetName(trackScope, "Scope: This track");
            AutomationProperties.SetName(globalScope, "Scope: Global");
            AutomationProperties.SetName(state, "Listening state");
            AutomationProperties.SetName(listen, "Listen");
            AutomationProperties.SetName(unmap, "Unmap");
            AutomationProperties.SetName(low, "Range low");
            AutomationProperties.SetName(high, "Range high");
            AutomationProperties.SetName(invert, "Invert");
            AutomationProperties.SetName(takeover, "Takeover");
            AutomationProperties.SetName(done, "Done");
            ToolTip.SetTip(takeover, "Pickup: the control waits until it meets the value. Jump: the value goes where the control is. Scale: both reach the ends together.");

            var scopeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            scopeRow.Children.Add(trackScope);
            scopeRow.Children.Add(globalScope);
            var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            stateRow.Children.Add(listen);
            stateRow.Children.Add(unmap);
            body = new StackPanel { Spacing = 8, Width = 270 };
            body.Children.Add(title);
            if (!target.IsCommand) body.Children.Add(scopeRow);
            body.Children.Add(state);
            body.Children.Add(stateRow);
            if (!target.IsCommand)
            {
                var range = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                range.Children.Add(new TextBlock { Text = "Range", VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush, MinWidth = 40 });
                range.Children.Add(low);
                range.Children.Add(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush });
                range.Children.Add(high);
                range.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush });
                body.Children.Add(range);
                body.Children.Add(rangeText);
                var shape = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                shape.Children.Add(invert);
                shape.Children.Add(new TextBlock { Text = "Takeover", VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush });
                shape.Children.Add(takeover);
                body.Children.Add(shape);
                body.Children.Add(takeoverHelp);
            }
            body.Children.Add(done);
            var frame = new Border
            {
                Child = body,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = StudioTokens.SurfaceBorderBrush,
                Background = StudioTokens.SurfacePanelBrush,
            };
            AutomationProperties.SetName(frame, "MIDI mapping");
            popup = new Popup
            {
                Child = frame,
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                PlacementRect = around,
                IsLightDismissEnabled = true,
            };
            Host = popup;
            popup.Closed += (s, e) => { mode.Changed -= OnChanged; Closed?.Invoke(); };

            trackScope.IsCheckedChanged += (s, e) => { if (!updating && trackScope.IsChecked == true) { mode.Scope = MapScope.Track; Relisten(); } };
            globalScope.IsCheckedChanged += (s, e) => { if (!updating && globalScope.IsChecked == true) { mode.Scope = MapScope.Global; Relisten(); } };
            listen.Click += (s, e) => ToggleListen();
            unmap.Click += (s, e) => { mode.Unmap(target); Fill(); };
            done.Click += (s, e) => Hide();
            low.ValueChanged += (s, e) => Apply();
            high.ValueChanged += (s, e) => Apply();
            invert.IsCheckedChanged += (s, e) => Apply();
            takeover.SelectionChanged += (s, e) => Apply();
            mode.Changed += OnChanged;
            Fill();
        }

        public Popup Host { get; }

        public MapTarget Target => target;

        public event Action Closed;

        public bool IsOpen => popup.IsOpen;

        public void Show() => popup.IsOpen = true;

        public void Hide()
        {
            mode.Mapping?.CancelListening();
            if (ReferenceEquals(mode.Armed, target)) mode.Disarm();
            popup.IsOpen = false;
        }

        // The controls, for tests.
        internal RadioButton TrackScope => trackScope;
        internal RadioButton GlobalScope => globalScope;
        internal Button ListenButton => listen;
        internal Button UnmapButton => unmap;
        internal NumericUpDown Low => low;
        internal NumericUpDown High => high;
        internal CheckBox InvertBox => invert;
        internal ComboBox TakeoverBox => takeover;
        internal Button DoneButton => done;

        /// <summary>"Listening…" while it waits for a controller, the control it is mapped to, or "Not mapped".</summary>
        public string StateText => state.Text;

        public string TitleText => title.Text;

        /// <summary>"20% to 60% of the parameter's range", under the From and To fields.</summary>
        public string RangeSentence => rangeText.Text;

        void OnChanged() => Fill();

        bool Listening => mode.Mapping?.Listening is { Teach: false } l && target.Equals(l.Target);

        void ToggleListen()
        {
            if (Listening) { mode.Disarm(); return; }
            mode.Arm(target, out string error);
            Fill();
            if (error != null) state.Text = error;
        }

        /// <summary>The scope changed while listening: it listens again in the new scope.</summary>
        void Relisten()
        {
            if (!Listening) return;
            mode.Arm(target, out _);
        }

        void Apply()
        {
            if (updating || mode.Mapping == null || target.IsCommand) return;
            if (mode.Mapping.InfoOf(target) == null) { RangeLabel(); return; }
            double lo = (double)(low.Value ?? 0) / 100, hi = (double)(high.Value ?? 100) / 100;
            if (lo > hi) { hi = lo; updating = true; high.Value = (decimal)(hi * 100); updating = false; }
            mode.Mapping.SetDrive(target, lo, hi, invert.IsChecked == true, TakeoverOf(takeover.SelectedIndex));
            RangeLabel();
        }

        static Takeover TakeoverOf(int index) => index == 1 ? Takeover.Jump : index == 2 ? Takeover.Scale : Takeover.Pickup;

        void RangeLabel() => rangeText.Text = $"{(int)(low.Value ?? 0)}% to {(int)(high.Value ?? 100)}% of the parameter's range" + (invert.IsChecked == true ? ", inverted" : "");

        void Fill()
        {
            updating = true;
            try
            {
                var mm = mode.Mapping;
                title.Text = target.Text;
                string trackName = mm?.SelectedTrack?.Name;
                trackScope.Content = trackName != null ? "This track: " + trackName : "This track";
                trackScope.IsEnabled = trackName != null;
                var info = mm?.InfoOf(target);
                bool trackNow = info != null ? info.Scope == "track" : mode.Scope == MapScope.Track && trackName != null;
                trackScope.IsChecked = trackNow;
                globalScope.IsChecked = !trackNow;
                if (info != null && !target.IsCommand)
                {
                    var (lo, hi) = MapMode.Range(info.Range);
                    low.Value = (decimal)Math.Round(lo * 100);
                    high.Value = (decimal)Math.Round(hi * 100);
                    invert.IsChecked = info.Invert;
                    takeover.SelectedIndex = info.Takeover == Takeover.Jump ? 1 : info.Takeover == Takeover.Scale ? 2 : 0;
                }
                takeoverHelp.Text = takeover.SelectedIndex switch
                {
                    1 => "Jump: the value goes where the control is.",
                    2 => "Scale: the control and the value reach the ends together.",
                    _ => "Pickup: nothing changes until the control meets the value.",
                };
                RangeLabel();
                bool listening = Listening;
                listen.Content = listening ? "Stop" : info != null ? "Listen again" : "Listen";
                unmap.IsEnabled = info != null;
                if (listening) state.Text = "Listening… move a knob or press a key on any controller.";
                else if (info != null) state.Text = "Mapped to " + (mm.BadgeFullOf(target) ?? info.Control) + (info.Legacy ? " (an old Buzz binding)" : "") + ".";
                else state.Text = "Not mapped. Listen, then move a knob or press a key.";
            }
            finally
            {
                updating = false;
            }
        }
    }
}
