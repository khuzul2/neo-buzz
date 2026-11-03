// Fork (docs/specs/studio-mvp.md S7): Preferences ▸ Sync, the Session View's Sync menu as a page
// (ReBuzz.exe's ⚙ › Sync): Ableton Link through the ReBuzzLink helper, MIDI clock in, and MIDI
// clock out to open MIDI outputs. SessionSync keeps them in the settings, not the song.

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
        readonly CheckBox link = new CheckBox { Content = "Ableton Link" };
        readonly CheckBox startStop = new CheckBox { Content = "Start and stop with Link peers" };
        readonly CheckBox clockIn = new CheckBox { Content = "Follow MIDI clock in" };
        readonly StackPanel clockOuts = new StackPanel { Spacing = 2 };
        readonly TextBlock linkState = new TextBlock { Foreground = Dim, FontSize = 12 };
        readonly TextBlock problem = new TextBlock { Foreground = Problem, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        readonly TextBlock status = new TextBlock();
        readonly List<(string Name, CheckBox Box)> outRows = new List<(string, CheckBox)>();

        public SyncPage(SessionSync sync)
        {
            this.sync = sync;
            Heading("Ableton Link");
            Row("", new StackPanel { Spacing = 2, Children = { link, linkState } },
                "Tempo and bar phase follow Live and other Link apps on the network; alone, they follow the session. " +
                "A start waits for Link's next bar line, so it lands in step. Link runs in a helper program (ReBuzzLink).");
            Row("", startStop, "Play and stop are shared with peers that have start/stop sync on (Live: Link › Start Stop Sync).");
            Heading("MIDI clock");
            Row("", clockIn, "Tempo and beat follow the MIDI clock coming in (any open MIDI input), and so do Start, Continue and Stop. Turns Link off.");
            Row("Send MIDI clock to", clockOuts, "Open MIDI outputs (Preferences ▸ MIDI). The clock runs while the transport plays, in step with what is heard.");
            Heading("Now");
            Body.Children.Add(status);
            Body.Children.Add(problem);

            void OnCheck(CheckBox box, Action<bool> set) => box.IsCheckedChanged += (s, e) =>
            {
                if (Filling) return;
                set(box.IsChecked == true);
                Refresh();
            };
            OnCheck(link, on =>
            {
                problem.Text = "";
                sync.SetLink(on);
            });
            OnCheck(startStop, sync.SetLinkStartStop);
            OnCheck(clockIn, sync.SetFollowMidiClock);
            sync.Changed += OnChanged;
            PollWhileShown(TimeSpan.FromSeconds(1));
        }

        public override string Title => "Sync";

        internal CheckBox LinkBox => link;
        internal CheckBox StartStopBox => startStop;
        internal CheckBox ClockInBox => clockIn;
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
                link.IsChecked = sync.LinkEnabled;
                linkState.Text = !sync.LinkEnabled ? "" : sync.LinkPeers > 0 ? $"{sync.LinkPeers} peer{(sync.LinkPeers == 1 ? "" : "s")}, {sync.LinkTempo:0.##} BPM" : "No peers yet";
                linkState.IsVisible = linkState.Text.Length > 0;
                startStop.IsChecked = sync.LinkStartStop;
                startStop.IsEnabled = sync.LinkEnabled;
                clockIn.IsChecked = sync.FollowMidiClock;
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
                            sync.SetClockOut(device, box.IsChecked == true);
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
    }
}
