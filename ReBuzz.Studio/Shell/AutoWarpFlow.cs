// Fork (docs/specs/neo-audio.md, AW5 as built): Auto-warp from any audio clip. The right-click menu of an audio clip in the Session, the Arrangement and the Audio page offers
// "Auto-warp ▸ Follow the beat / To grid / Options…" ("Auto-warp selection ▸" when a range of the audio is selected on the Audio page); Options… is a small dialog with the clip's saved
// options (mode, grid, strength, sensitivity, reach, long notes) that runs from there. Every command is one undo step, on every clip it is given.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    using Control = Avalonia.Controls.Control;

    internal static class AutoWarpFlow
    {
        /// <summary>Tests answer here instead of the dialog: the choice, or null for cancel.</summary>
        public static Func<AutoWarpChoice, AutoWarpChoice?> Responder { get; set; }

        /// <summary>Raised when a command ran (true: to grid), so the Audio page's button takes that mode.</summary>
        public static event Action<bool> Ran;

        /// <summary>The last command failed (LastSummary says why).</summary>
        public static bool LastWasError { get; private set; }

        /// <summary>What the last command did, or why it didn't (tests, and a line for the status).
        public static string LastSummary { get; private set; } = "";

        static ReBuzzCore Core => StudioHost.Current?.Core;

        /// <summary>A range of the audio, seconds (Auto-warp selection).</summary>
        internal readonly record struct Range(double From, double To);

        /// <summary>What the Options dialog holds and runs.</summary>
        internal struct AutoWarpChoice
        {
            public bool ToGrid;
            public string Grid;
            public int Strength, Sensitivity, Reach;
            public bool LeaveSustained;
        }

        /// <summary>
        /// "Auto-warp ▸ Follow the beat / To grid · 1/16 · 100 % / Options…" for the audio clips <paramref name="clips"/> gives when the menu opens; "Auto-warp selection" when
        /// <paramref name="range"/> gives a range. A menu is built each time one opens, so it names the clip's own grid and the song's strength.
        /// </summary>
        public static MenuItem Menu(Func<IReadOnlyList<IClip>> clips, Func<Range?> range = null, bool enabled = true)
        {
            var list = clips() ?? Array.Empty<IClip>();
            var r = range?.Invoke();
            var layout = Core?.SessionLayout;
            var first = list.FirstOrDefault();
            var audio = first == null ? null : layout?.AudioOf(first);
            string grid = audio?.AutoGrid ?? SessionDocument.AudioSettings.DefaultAutoGrid;
            int strength = layout?.QuantizeStrength ?? 100;
            string title = r != null ? "Auto-warp selection" : "Auto-warp";
            var root = new MenuItem { Header = title, Tag = title, IsEnabled = enabled && list.Count > 0 };
            var follow = Entry("Follow the beat", null, "Beats and bars on the grid", "Auto-warp: Follow the beat");
            follow.Click += (_, _) => Run(clips(), false, r);
            var toGrid = Entry("To grid", $"· {grid} · {strength} %", "Also snap every attack to the grid", "Auto-warp: To grid");
            toGrid.Click += (_, _) => Run(clips(), true, r);
            var options = new MenuItem { Header = "Options…", Tag = "Auto-warp: Options" };
            options.Click += (_, _) => _ = ShowOptions(clips(), r);
            root.Items.Add(follow);
            root.Items.Add(toGrid);
            root.Items.Add(new Separator());
            root.Items.Add(options);
            return root;
        }

        static MenuItem Entry(string title, string tail, string subtitle, string tag)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            head.Children.Add(new TextBlock { Text = title });
            if (tail != null) head.Children.Add(new TextBlock { Text = tail, FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, FontFamily = new Avalonia.Media.FontFamily("monospace"), VerticalAlignment = VerticalAlignment.Center });
            var col = new StackPanel { Spacing = 1 };
            col.Children.Add(head);
            col.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush });
            return new MenuItem { Header = col, Tag = tag };
        }

        /// <summary>Auto-warps the clips (one undo step), shows the first one on the Audio page, and says what it did.</summary>
        public static bool Run(IReadOnlyList<IClip> clips, bool toGrid, Range? range = null, AudioClipEdit options = null)
        {
            var layout = Core?.SessionLayout;
            if (layout == null || clips == null || clips.Count == 0) return false;
            string why = layout.AutoWarpClips(clips.Where(c => c != null).ToList(), toGrid, options, null, range?.From, range?.To);
            LastWasError = why != null;
            LastSummary = why ?? Summary(layout, clips, toGrid);
            if (why == null) Ran?.Invoke(toGrid);
            if (why != null) _ = Dialogs.Ask(why, Product.Name, UserButtons.Ok);
            else if (clips.Count == 1) Views.Audio.AudioClipPage.Opener?.Invoke(clips[0]);
            return why == null;
        }

        static string Summary(SessionLayout layout, IReadOnlyList<IClip> clips, bool toGrid)
        {
            if (!toGrid) return clips.Count == 1 ? "Auto-warped." : $"Auto-warped {clips.Count} clips.";
            int made = layout.LastGridReports.Sum(r => r.Report.Made), far = layout.LastGridReports.Sum(r => r.Report.OutOfReach);
            return $"Auto-warped to grid: {made} markers on the attacks" + (far > 0 ? $", {far} left alone (beyond reach)" : "") + ".";
        }

        /// <summary>The Options dialog: the clip's saved options; Auto-warp runs from there.</summary>
        public static async Task ShowOptions(IReadOnlyList<IClip> clips, Range? range = null)
        {
            var layout = Core?.SessionLayout;
            if (layout == null || clips == null || clips.Count == 0) return;
            var audio = layout.AudioOf(clips[0]);
            if (audio == null) return;
            var start = new AutoWarpChoice
            {
                ToGrid = true, Grid = audio.AutoGrid, Strength = layout.QuantizeStrength, Sensitivity = audio.AutoSensitivity, Reach = audio.AutoReach, LeaveSustained = audio.AutoLeaveSustained,
            };
            if (Responder != null)
            {
                if (Responder(start) is { } answer) RunChoice(clips, answer, range);
                return;
            }
            await StudioDialog.Run(new OptionsWindow(clips, range, start));
        }

        internal static void RunChoice(IReadOnlyList<IClip> clips, AutoWarpChoice c, Range? range)
        {
            var layout = Core?.SessionLayout;
            if (layout == null) return;
            if (c.Strength != layout.QuantizeStrength) layout.SetQuantizeStrength(c.Strength);
            Run(clips, c.ToGrid, range, new AudioClipEdit { AutoGrid = c.Grid, AutoSensitivity = c.Sensitivity, AutoReach = c.Reach, AutoLeaveSustained = c.LeaveSustained });
        }

        // ------------------------------------------------------------------ the dialog

        internal sealed class OptionsWindow : Window
        {
            static readonly string[] Bases = { "1/4", "1/8", "1/16", "1/32" };
            readonly IReadOnlyList<IClip> clips;
            readonly Range? range;
            readonly ToggleButton follow = Segment("Follow the beat"), toGrid = Segment("To grid");
            readonly ToggleButton[] bases = Bases.Select(Segment).ToArray();
            readonly CheckBox triplets = new CheckBox { Content = "Triplets" };
            readonly Slider strength = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1 }, sensitivity = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1 }, reach = new Slider { Minimum = 0, Maximum = 50, SmallChange = 1 };
            readonly TextBlock strengthText = new TextBlock(), sensitivityText = new TextBlock(), reachText = new TextBlock();
            readonly CheckBox sustained = new CheckBox { Content = "Leave long notes and pauses alone (no markers inside sustained sounds or silence)" };
            readonly Button go, cancel = StudioDialog.Secondary();

            internal ToggleButton FollowButton => follow;
            internal ToggleButton ToGridButton => toGrid;
            internal IReadOnlyList<ToggleButton> GridButtons => bases;
            internal CheckBox TripletsBox => triplets;
            internal Slider StrengthSlider => strength;
            internal Slider SensitivitySlider => sensitivity;
            internal Slider ReachSlider => reach;
            internal CheckBox SustainedBox => sustained;
            internal Button GoButton => go;
            internal Button CancelButton => cancel;

            static ToggleButton Segment(string text) => new ToggleButton { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 28 };

            public OptionsWindow(IReadOnlyList<IClip> clips, Range? range, AutoWarpChoice start)
            {
                this.clips = clips;
                this.range = range;
                Title = "Auto-warp";
                SizeToContent = SizeToContent.WidthAndHeight;
                CanResize = false;
                ShowInTaskbar = false;
                string what = clips.Count == 1 ? clips[0].Name : $"{clips[0].Name} + {clips.Count - 1} more";
                go = StudioDialog.Primary(range != null ? "Auto-warp selection" : clips.Count == 1 ? "Auto-warp" : $"Auto-warp {clips.Count} clips");
                var modeRow = new UniformGrid { Columns = 2, Children = { follow, toGrid } };
                var gridRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                gridRow.Children.Add(new UniformGrid { Columns = 4, MinWidth = 260, Children = { bases[0], bases[1], bases[2], bases[3] } });
                gridRow.Children.Add(triplets);
                var rows = new List<Control>
                {
                    new TextBlock { Text = (range != null ? "Auto-warp selection · " : "") + what, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    Labelled("Mode", modeRow),
                    Labelled("Grid", gridRow),
                    Slide("Strength", strength, strengthText, "How far each attack moves toward its grid line."),
                    Slide("Sensitivity", sensitivity, sensitivityText, "Higher finds softer attacks; lower keeps only the clear ones."),
                    Slide("Reach", reach, reachText, "Attacks farther than this from every line are left where they are."),
                    sustained,
                    new TextBlock { Text = "Saved with each clip · one undo step", FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush },
                    StudioDialog.Buttons(go, cancel),
                };
                Content = StudioDialog.Body(rows.ToArray());
                Set(start);
                follow.Click += (_, _) => { follow.IsChecked = true; toGrid.IsChecked = false; Sync(); };
                toGrid.Click += (_, _) => { toGrid.IsChecked = true; follow.IsChecked = false; Sync(); };
                for (int i = 0; i < bases.Length; i++)
                {
                    int k = i;
                    bases[i].Click += (_, _) => { foreach (var b in bases) b.IsChecked = ReferenceEquals(b, bases[k]); Sync(); };
                }
                triplets.IsCheckedChanged += (_, _) => Sync();
                foreach (var sl in new[] { strength, sensitivity, reach }) sl.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) Sync(); };
                go.Click += (_, _) => { RunChoice(clips, Choice(), range); Close(); };
                cancel.Click += (_, _) => Close();
            }

            static Control Labelled(string label, Control c) => new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush }, c } };

            static Control Slide(string label, Slider s, TextBlock value, string help)
            {
                var head = new DockPanel();
                value.FontFamily = new Avalonia.Media.FontFamily("monospace");
                DockPanel.SetDock(value, Dock.Right);
                head.Children.Add(value);
                head.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush });
                Avalonia.Automation.AutomationProperties.SetName(s, label);
                return new StackPanel { Spacing = 2, Children = { head, s, new TextBlock { Text = help, FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush } } };
            }

            void Set(AutoWarpChoice c)
            {
                follow.IsChecked = !c.ToGrid;
                toGrid.IsChecked = c.ToGrid;
                string baseName = c.Grid.EndsWith("T") ? c.Grid[..^1] : c.Grid;
                bool trip = c.Grid.EndsWith("T");
                for (int i = 0; i < bases.Length; i++) bases[i].IsChecked = Bases[i] == baseName || (trip && baseName == "1/16" && false);
                triplets.IsChecked = trip;
                strength.Value = c.Strength;
                sensitivity.Value = c.Sensitivity;
                reach.Value = c.Reach;
                sustained.IsChecked = c.LeaveSustained;
                Sync();
            }

            /// <summary>The choice the controls hold (a triplet grid is the base's triplet; 1/32 has none and is taken as 1/16 triplets).</summary>
            internal AutoWarpChoice Choice()
            {
                int i = Array.FindIndex(bases, b => b.IsChecked == true);
                string name = Bases[i < 0 ? 2 : i];
                if (triplets.IsChecked == true) name = (name == "1/32" ? "1/16" : name) + "T";
                return new AutoWarpChoice
                {
                    ToGrid = toGrid.IsChecked == true, Grid = name, Strength = (int)Math.Round(strength.Value), Sensitivity = (int)Math.Round(sensitivity.Value),
                    Reach = (int)Math.Round(reach.Value), LeaveSustained = sustained.IsChecked == true,
                };
            }

            void Sync()
            {
                strengthText.Text = $"{(int)Math.Round(strength.Value)} %";
                sensitivityText.Text = $"{(int)Math.Round(sensitivity.Value)} %";
                reachText.Text = $"{(int)Math.Round(reach.Value)} % of a step";
                bool grid = toGrid.IsChecked == true;
                foreach (var b in bases) b.IsEnabled = grid;
                triplets.IsEnabled = grid;
                bases[3].IsEnabled = grid && triplets.IsChecked != true;      // there is no 1/32 triplet
                foreach (var s in new Control[] { strength, sensitivity, reach, sustained }) s.IsEnabled = grid;
            }
        }
    }
}
