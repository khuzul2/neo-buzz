// Fork (docs/specs/studio-mvp.md S7): Preferences ▸ Sync, the Session View's Sync menu as a page
// (ReBuzz.exe's ⚙ › Sync): Ableton Link through the ReBuzzLink helper, MIDI clock in, and MIDI
// clock out to open MIDI outputs. SessionSync keeps them in the settings, not the song. Fork (ux-overhaul.md P3g, decision 79): they are
// entries of the one settings list (section "sync"), so settings_get/settings_set cover them and the window logs its changes.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class SyncPage : PrefsPage
    {
        readonly SessionSync sync;
        readonly StackPanel clockOuts = new StackPanel { Spacing = 2 };
        readonly TextBlock linkState = new TextBlock { Foreground = Dim, FontSize = 12 };
        readonly TextBlock problem = new TextBlock { Foreground = Problem, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        readonly TextBlock status = new TextBlock();
        readonly List<(string Name, CheckBox Box)> outRows = new List<(string, CheckBox)>();

        // Link, start/stop and MIDI clock in are the settings sync.Link, sync.LinkStartStop and sync.MidiClockIn (settings_get lists them,
        // the window's changes are logged as the agent's are); only the list of outputs is drawn here, as a table of the open ones.
        public SyncPage(SessionSync sync, PrefsModel model) : base(model)
        {
            this.sync = sync;
            Heading("Link and MIDI clock");
            Note("Link: tempo and bar phase follow the other Link apps on the network; alone, they follow the session. A start waits for Link's next bar line, " +
                 "so it lands in step. Link runs in a helper program (ReBuzzLink). MIDI clock in turns Link off, and the other way round.");
            AddSection("sync", "MidiClockOut");
            Body.Children.Add(linkState);
            Row("Send MIDI clock to", clockOuts, "Open MIDI outputs (Preferences ▸ MIDI). The clock runs while the transport plays, in step with what is heard.");
            Heading("Now");
            Body.Children.Add(status);
            Body.Children.Add(problem);
            sync.Changed += OnChanged;
            PollWhileShown(TimeSpan.FromSeconds(1));
        }

        public override string Title => "Sync";

        internal CheckBox LinkBox => (CheckBox)SettingControl("sync.Link");
        internal CheckBox StartStopBox => (CheckBox)SettingControl("sync.LinkStartStop");
        internal CheckBox ClockInBox => (CheckBox)SettingControl("sync.MidiClockIn");
        internal IReadOnlyList<(string Name, CheckBox Box)> ClockOutRows => outRows;
        internal string ProblemText => problem.Text;

        void OnChanged() => Dispatcher.UIThread.Post(Refresh);

        protected override void OnShown()
        {
            problem.Text = "";
            Refresh();
        }

        public override void Closed()
        {
            sync.Changed -= OnChanged;
        }

        public override void Refresh()
        {
            Filling = true;
            try
            {
                linkState.Text = !sync.LinkEnabled ? "" : sync.LinkPeers > 0 ? $"Link: {sync.LinkPeers} peer{(sync.LinkPeers == 1 ? "" : "s")}, {sync.LinkTempo:0.##} BPM" : "Link: no peers yet";
                linkState.IsVisible = linkState.Text.Length > 0;
                if (StartStopBox != null) StartStopBox.IsEnabled = sync.LinkEnabled;
                if (sync.Problem != null) problem.Text = sync.Problem;
                status.Text = sync.Status.Length > 0 ? sync.Status : "Nothing syncs: the session keeps its own tempo.";

                // Open outputs, then the chosen ones that aren't open (as the menu lists them).
                var open = sync.Outputs.Select(o => o.Name).Distinct().ToList();
                var names = open.Concat(sync.ClockOutNames.Where(n => !open.Contains(n))).ToList();
                if (!names.SequenceEqual(outRows.Select(r => r.Name)))
                {
                    clockOuts.Children.Clear();
                    outRows.Clear();
                    foreach (var name in names)
                    {
                        string device = name;
                        var box = new CheckBox { Content = open.Contains(name) ? name : $"{name} (not open)" };
                        box.IsCheckedChanged += (s, e) =>
                        {
                            if (Filling) return;
                            SetClockOut(device, box.IsChecked == true);
                            Refresh();
                        };
                        clockOuts.Children.Add(box);
                        outRows.Add((name, box));
                    }
                    if (names.Count == 0) clockOuts.Children.Add(new TextBlock { Text = "No MIDI outputs are open.", Foreground = Dim, Margin = new Thickness(0, 6, 0, 0) });
                }
                foreach (var (name, box) in outRows) box.IsChecked = sync.ClockOutNames.Contains(name);
            }
            finally
            {
                Filling = false;
            }
        }

        /// <summary>One output on or off: the setting sync.MidiClockOut ("|"-separated names), through the service, so it is logged.</summary>
        void SetClockOut(string device, bool on)
        {
            var names = sync.ClockOutNames.Where(n => n != device).ToList();
            if (on) names.Add(device);
            try { Model?.Apply("sync.MidiClockOut", string.Join("|", names)); }
            catch (Exception e) { problem.Text = e.Message; }
        }
    }
}
