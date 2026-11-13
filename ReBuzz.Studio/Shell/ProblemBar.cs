// Fork (docs/specs/ux-overhaul.md §5 P1, UX review mac-500, mac-520): problems told in plain words, each with a button
// that fixes it, in a bar under the transport: a machine that blew up and was muted, one that stopped working, machines
// a song needs that this computer can't run, the machine guard or the safety limiter off. The most important problem
// shows first, with how many more there are; × hides one until it changes. The status line keeps a short note of each.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    /// <summary>A problem: its key (what × hides), its text, the buttons that fix it, and whether it is about sound safety.</summary>
    internal sealed record StudioProblem(string Key, string Text, IReadOnlyList<(string Label, Action Fix)> Fixes, bool Severe);

    internal static class PlainWords
    {
        /// <summary>What a machine's guard saw, as a person would say it: "got too loud (+66.4 dBFS)", "made broken sound (NaN)".</summary>
        public static string Diverged(MachineCore m)
        {
            string reason = m.DivergedReason;
            return reason.EndsWith("dBFS", StringComparison.Ordinal) ? $"got far too loud ({reason})" : $"made broken sound ({reason})";
        }

        /// <summary>What a machine that can't load is, here: "Windows only" off Windows (a native machine), "not installed" on it.</summary>
        public static string Missing => OperatingSystem.IsWindows() ? "not installed" : "Windows only";

        public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

        public static string Names(IEnumerable<string> names, int max = 3)
        {
            var list = names.ToList();
            return list.Count <= max ? string.Join(", ", list) : string.Join(", ", list.Take(max)) + $" and {list.Count - max} more";
        }
    }

    internal sealed class ProblemBar : Border
    {
        readonly TextBlock text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        readonly TextBlock more = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(12, 0, 0, 0) };
        readonly StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly Button dismiss = new Button { Content = "×", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly HashSet<string> dismissed = new HashSet<string>();
        string shownKey;
        string shownSignature;

        static readonly IBrush SevereBack = StudioTokens.Brush(StudioTokens.SevereBack);
        static readonly IBrush NoticeBack = StudioTokens.Brush(StudioTokens.NoticeBack);
        static readonly IBrush SevereEdge = StudioTokens.RecordBrush;
        static readonly IBrush NoticeEdge = StudioTokens.Brush(StudioTokens.Warning);

        public ProblemBar()
        {
            IsVisible = false;
            Padding = new Thickness(12, 6);
            BorderThickness = new Thickness(4, 0, 0, 1);
            ToolTip.SetTip(dismiss, "Hide this until it changes");
            dismiss.Click += (s, e) =>
            {
                if (shownKey != null) dismissed.Add(shownKey);
                Show(Problems);
            };
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(dismiss, Dock.Right);
            DockPanel.SetDock(more, Dock.Right);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(dismiss);
            row.Children.Add(more);
            row.Children.Add(buttons);
            row.Children.Add(text);
            Child = row;
        }

        /// <summary>The problems found last (all of them, hidden ones too).</summary>
        public IReadOnlyList<StudioProblem> Problems { get; private set; } = Array.Empty<StudioProblem>();

        /// <summary>The problem shown now (tests), or null.</summary>
        public StudioProblem Shown { get; private set; }

        /// <summary>Its buttons now (tests press them).</summary>
        public IEnumerable<Button> Buttons => buttons.Children.OfType<Button>();

        public string Text => text.Text;

        /// <summary>Shows the first problem not hidden; hides the bar when there is none.</summary>
        public void Show(IReadOnlyList<StudioProblem> problems)
        {
            Problems = problems;
            // A hidden problem shows again once it is gone, so the next one like it isn't hidden.
            dismissed.RemoveWhere(k => problems.All(p => p.Key != k));
            var open = problems.Where(p => !dismissed.Contains(p.Key)).ToList();
            var first = open.FirstOrDefault();
            Shown = first;
            IsVisible = first != null;
            if (first == null)
            {
                shownKey = shownSignature = null;
                return;
            }
            string signature = first.Key + "\n" + first.Text + "\n" + open.Count;
            if (signature == shownSignature) return;
            shownSignature = signature;
            shownKey = first.Key;
            Background = first.Severe ? SevereBack : NoticeBack;
            BorderBrush = first.Severe ? SevereEdge : NoticeEdge;
            text.Text = first.Text;
            text.Foreground = StudioTokens.TextPrimaryBrush;
            more.Text = open.Count > 1 ? $"and {open.Count - 1} more" : "";
            more.Foreground = StudioTokens.TextSecondaryBrush;
            buttons.Children.Clear();
            foreach (var (label, fix) in first.Fixes)
            {
                var b = new Button { Content = label, Tag = label };
                b.Click += (s, e) => fix();
                buttons.Children.Add(b);
            }
        }

        /// <summary>
        /// The song's problems now, the worst first. <paramref name="showMachines"/> shows the Machine View with these machines
        /// selected; <paramref name="openParameters"/> opens a machine's parameters.
        /// </summary>
        public static List<StudioProblem> Find(ReBuzzCore core, Action<IReadOnlyList<MachineCore>> showMachines, Action<MachineCore> openParameters,
                                               Action<MachineCore> resetAndUnmute, Action<bool> setLimiter)
        {
            var list = new List<StudioProblem>();
            if (core?.SongCore == null) return list;
            var machines = core.SongCore.MachinesList.Where(m => !m.Hidden).ToList();

            foreach (var m in machines.Where(m => m.IsDiverged))
                list.Add(new StudioProblem("diverged:" + m.Name,
                    $"{m.Name} {PlainWords.Diverged(m)} and was muted to protect your ears.",
                    new (string, Action)[] { ("Reset and Unmute", () => resetAndUnmute(m)), ("Parameters…", () => openParameters(m)) },
                    true));

            if (!ReBuzz.Audio.MachineGuard.Enabled)
                list.Add(new StudioProblem("guard-off",
                    "The machine guard is off: a machine that blows up can reach your speakers at full scale.",
                    new (string, Action)[] { ("Turn On", () => ReBuzz.Audio.MachineGuard.Enabled = true) }, true));

            if (core.SafeMaster is { LimiterOn: false })
                list.Add(new StudioProblem("limiter-off",
                    "The safety limiter is off: nothing stops a loud or broken signal from reaching your speakers.",
                    new (string, Action)[] { ("Turn On", () => setLimiter(true)) }, true));

            var stopped = machines.Where(core.IsReportedCrashed).ToList();
            if (stopped.Count > 0)
                list.Add(new StudioProblem("stopped:" + string.Join(",", stopped.Select(m => m.Name)),
                    $"{PlainWords.Names(stopped.Select(m => m.Name))} stopped working, so {(stopped.Count == 1 ? "it is" : "they are")} left out of the sound. " +
                    "The song keeps their settings.",
                    new (string, Action)[] { (stopped.Count == 1 ? "Show It" : "Show Them", () => showMachines(stopped)) }, false));

            var missing = machines.Where(m => m.DLL?.IsMissing == true).ToList();
            if (missing.Count > 0)
            {
                string what = OperatingSystem.IsWindows()
                    ? $"{PlainWords.Count(missing.Count, "machine", "machines")} in this song {(missing.Count == 1 ? "isn't" : "aren't")} installed ({PlainWords.Names(missing.Select(m => m.DLL.Name).Distinct())}), "
                    : $"{PlainWords.Count(missing.Count, "machine", "machines")} in this song {(missing.Count == 1 ? "is" : "are")} Windows-only ({PlainWords.Names(missing.Select(m => m.DLL.Name).Distinct())}), ";
                list.Add(new StudioProblem("missing:" + missing.Count,
                    what + $"so {(missing.Count == 1 ? "it stays" : "they stay")} silent here. {(missing.Count == 1 ? "It is" : "They are")} kept and saved unchanged; " +
                    "right-click one to replace it.",
                    new (string, Action)[] { (missing.Count == 1 ? "Show It" : "Show Them", () => showMachines(missing)) }, false));
            }
            return list;
        }
    }
}
