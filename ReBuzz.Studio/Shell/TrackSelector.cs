// Fork (docs/specs/midi-mapping.md, MM1 M3): the top bar's track selector: "◀ Lead ▶" with the controllers that follow it ("Keys, Pads follow"). The
// arrows (and Alt+Up / Alt+Down, and a controller button mapped to them) step through the song's tracks; what the knobs and the keys of every
// controller drive is the track shown here. A line for a few seconds says a knob that moved nothing is free on this track ("K7 is free on Drums").

using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    internal sealed class TrackSelector : Border
    {
        readonly Func<ReBuzzCore> core;
        readonly Button previous = new Button { Content = new TextBlock { Text = "◀" }, };
        readonly Button next = new Button { Content = new TextBlock { Text = "▶" }, };
        readonly TextBlock name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        // A small muted chip: "2 devices" (who they are is its tooltip), so the bar keeps to one row; a "K7 is free on Drums" notice takes its place for a few seconds.
        readonly TextBlock followers = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush, FontSize = StudioTokens.FontCaption, MinWidth = 64, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        string followersText = "", freeText = "";
        int lastFree = -1;
        long freeUntil;

        public TrackSelector(Func<ReBuzzCore> core)
        {
            this.core = core;
            previous.Classes.Add("icon");                  // the style's icon button, no padding of its own
            next.Classes.Add("icon");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(previous);
            row.Children.Add(name);
            row.Children.Add(next);
            followers.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(followers);
            Child = row;
            Margin = new Thickness(0, 2, 10, 2);
            AutomationProperties.SetName(this, "Selected track");
            AutomationProperties.SetName(previous, "Previous track");
            AutomationProperties.SetName(next, "Next track");
            AutomationProperties.SetName(name, "Track name");
            ToolTip.SetTip(previous, Commands.StudioKeymap.Tip("track.previous"));
            ToolTip.SetTip(next, Commands.StudioKeymap.Tip("track.next"));
            ToolTip.SetTip(name, "The track the knobs and keys of your MIDI controllers drive");
            ToolTip.SetTip(followers, "No controller's keys follow the selected track");
            previous.Click += (s, e) => Step(-1);
            next.Click += (s, e) => Step(1);
        }

        public void Step(int delta) => core()?.MidiMapping?.StepTrack(delta, this);

        public string NameText => name.Text;

        public string FollowersText => followersText;

        public string FreeText => freeText;

        /// <summary>Which controllers follow the track ("Faders, Pads follow"), the chip's tooltip.</summary>
        public string FollowersTip => ToolTip.GetTip(followers) as string;

        /// <summary>The chip ("2 devices", or the free notice), for tests.</summary>
        internal TextBlock Chip => followers;

        internal Button PreviousButton => previous;
        internal Button NextButton => next;

        public void Refresh()
        {
            var c = core();
            var mm = c?.MidiMapping;
            if (mm == null) { IsVisible = false; return; }
            var track = mm.SelectedTrack;
            string trackName = track?.Name;
            name.Text = trackName ?? "No track";
            ToolTip.SetTip(name, (trackName != null ? trackName + ": the" : "The") + " track the knobs and keys of your MIDI controllers drive");
            var follow = mm.Controllers().Where(x => x.Connected && x.Keys).Select(x => x.Id).ToList();
            followersText = follow.Count == 0 ? "" : follow.Count == 1 ? "1 device" : follow.Count + " devices";
            string followersTip = follow.Count == 0 ? "No controller's keys follow this track" : string.Join(", ", follow) + (follow.Count == 1 ? " follows" : " follow") + " this track";
            var (count, text) = mm.FreeNotice;
            if (lastFree < 0) lastFree = count;                 // a notice from before this window was there is not news
            if (count != lastFree)
            {
                lastFree = count;
                if (count > 0) freeUntil = Environment.TickCount64 + 4000;
                freeText = text ?? "";
            }
            if (Environment.TickCount64 > freeUntil && !string.IsNullOrEmpty(freeText)) freeText = "";
            // One chip: the notice while there is one, else how many devices follow.
            bool notice = !string.IsNullOrEmpty(freeText);
            followers.Text = notice ? freeText : followersText;
            ToolTip.SetTip(followers, notice ? freeText : followersTip);
            followers.IsVisible = followers.Text.Length > 0;
            bool any = trackName != null || mm.Tracks.Count > 0;
            previous.IsEnabled = next.IsEnabled = mm.Tracks.Count > 0;
            IsVisible = any;
        }
    }
}
