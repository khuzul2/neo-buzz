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
        readonly Button previous = new Button { Content = "◀", Padding = new Thickness(6, 2), MinWidth = 26 };
        readonly Button next = new Button { Content = "▶", Padding = new Thickness(6, 2), MinWidth = 26 };
        readonly TextBlock name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        readonly TextBlock followers = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush, FontSize = StudioTokens.FontCaption, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock free = new TextBlock { Text = "", VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush, FontSize = StudioTokens.FontCaption };
        int lastFree;
        long freeUntil;

        public TrackSelector(Func<ReBuzzCore> core)
        {
            this.core = core;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(previous);
            row.Children.Add(name);
            row.Children.Add(next);
            row.Children.Add(followers);
            row.Children.Add(free);
            Child = row;
            Margin = new Thickness(0, 2, 12, 2);
            AutomationProperties.SetName(this, "Selected track");
            AutomationProperties.SetName(previous, "Previous track");
            AutomationProperties.SetName(next, "Next track");
            AutomationProperties.SetName(name, "Track name");
            ToolTip.SetTip(previous, Commands.StudioKeymap.Tip("track.previous"));
            ToolTip.SetTip(next, Commands.StudioKeymap.Tip("track.next"));
            ToolTip.SetTip(name, "The track the knobs and keys of your MIDI controllers drive");
            previous.Click += (s, e) => Step(-1);
            next.Click += (s, e) => Step(1);
        }

        public void Step(int delta) => core()?.MidiMapping?.StepTrack(delta, this);

        public string NameText => name.Text;

        public string FollowersText => followers.Text;

        public string FreeText => free.Text;

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
            var follow = mm.Controllers().Where(x => x.Connected && x.Keys).Select(x => x.Id).ToList();
            followers.Text = follow.Count == 0 ? "" : string.Join(", ", follow) + (follow.Count == 1 ? " follows" : " follow");
            var (count, text) = mm.FreeNotice;
            if (count != lastFree)
            {
                lastFree = count;
                if (count > 0) freeUntil = Environment.TickCount64 + 4000;
                free.Text = text ?? "";
            }
            if (Environment.TickCount64 > freeUntil && !string.IsNullOrEmpty(free.Text)) free.Text = "";
            bool any = trackName != null || mm.Tracks.Count > 0;
            previous.IsEnabled = next.IsEnabled = mm.Tracks.Count > 0;
            IsVisible = any;
        }
    }
}
