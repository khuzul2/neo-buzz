// Fork (docs/specs/history-snapshots.md HS1b): the inspector's History tab. The steps newest first with name, time and source,
// the current one marked, the undone ones dimmed (still readable), a click or Enter goes there, and the Selection tab is as it was.

using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using BuzzGUI.Common;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Actions;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Inspector;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class HistoryPanelTests
    {
        static void PutBack()
        {
            var registry = Studio.Host.Registry;
            registry.Write(InspectorPanel.OpenKey, true, InspectorPanel.RegistrySection);
            registry.Write(InspectorPanel.WidthKey, InspectorPanel.DefaultWidth, InspectorPanel.RegistrySection);
        }

        static string Text(Avalonia.Controls.Control row) =>
            string.Join(" | ", row.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

        [AvaloniaTest]
        public void TheHistoryTabListsTheStepsNewestFirstAndAClickJumpsThere()
        {
            using var restore = Studio.AfterTest(PutBack);
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            using var song = new SessionRig("Lead");
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            try
            {
                var core = song.Core;
                var stack = core.SongCore.ActionStack;
                long now = 0;
                stack.Clock = () => now;
                stack.Clear();
                int bpm = core.BPM;
                TransportEdits.SetTempo(core, bpm + 10);
                now += 1000;
                using (stack.BeginSource(HistorySource.Agent, "Agent: song_set"))
                    TransportEdits.SetLooping(core, false);
                now += 1000;
                TransportEdits.SetTempo(core, bpm + 20);
                Settle();

                var panel = window.Inspector;
                Assert.That(panel.CurrentTab, Is.EqualTo("Selection"), "it still opens on the selection");
                Assert.That(panel.History.IsVisible, Is.False);
                song.Click(panel.HistoryTabButton);
                Settle();
                Assert.That(panel.CurrentTab, Is.EqualTo("History"));
                Assert.That(panel.History.IsEffectivelyVisible, Is.True);
                Assert.That(panel.ClipPanel.IsEffectivelyVisible, Is.False);
                panel.History.Rebuild();

                var rows = panel.History.Rows;
                Assert.That(rows.Count, Is.EqualTo(3));
                Assert.That(Text(rows[0]), Does.Contain("Set tempo").And.Contain("Manual"), "newest at the top");
                Assert.That(Text(rows[1]), Does.Contain("Agent: song_set").And.Contain("| Agent |"), "the agent's step is told apart");
                Assert.That(Text(rows[0]), Does.Contain("▶"), "the current position is marked");
                Assert.That(Text(rows[1]), Does.Not.Contain("▶"));

                // A click on the oldest step undoes the two after it, which stay listed, dimmed.
                var oldest = rows[2];
                oldest.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                Assert.That(core.BPM, Is.EqualTo(bpm + 10));
                Assert.That(core.Looping, Is.True);
                panel.History.Rebuild();
                rows = panel.History.Rows;
                Assert.That(rows.Count, Is.EqualTo(3), "the redo side is still listed");
                Assert.That(Text(rows[2]), Does.Contain("▶"), "the current position moved");
                Assert.That(rows[0].GetLogicalDescendants().OfType<TextBlock>().ElementAt(1).FontStyle, Is.EqualTo(FontStyle.Italic), "undone steps are dimmed");

                // And back to the newest.
                Assert.That(panel.History.Jump((int)rows[0].Tag!), Is.True);
                Assert.That(core.BPM, Is.EqualTo(bpm + 20));
                Assert.That(core.Looping, Is.False);
                Assert.That(panel.History.Jump(987654), Is.False, "a step that isn't there");

                // A step from anywhere (an agent on another thread) redraws the list.
                stack.Clear();
                Settle();
                Assert.That(panel.History.Rows, Is.Empty);

                song.Click(panel.SelectionTabButton);
                Assert.That(panel.CurrentTab, Is.EqualTo("Selection"));
                Assert.That(panel.History.IsEffectivelyVisible, Is.False);
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaTest]
        public void TheHistoryTabsTextIsReadableOnEveryRowBackground()
        {
            // The dimmed rows use the secondary text colour, not opacity: 4.5:1 or more on the panel, and on the current row's fill.
            var panel = StudioTokens.SurfacePanel;
            var current = StudioTokens.SelectionFill;
            Color Over(Color top, Color under)
            {
                double a = top.A / 255.0;
                byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
                return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
            }
            var fill = Over(current, panel);
            foreach (var (text, on, what) in new[]
            {
                (StudioTokens.TextPrimary, panel, "a done step"),
                (StudioTokens.TextSecondary, panel, "an undone step, the source and the time"),
                                (StudioTokens.TextPrimary, fill, "the current step, its source and time"),
                (StudioTokens.TextPrimary, fill, "the current marker"),
            })
                Assert.That(StudioTokens.Contrast(text, on), Is.GreaterThanOrEqualTo(4.5), what);
        }

        [AvaloniaTest]
        public void TheSourcesHaveTheirLabels()
        {
            Assert.That(HistoryTab.SourceLabel(HistorySource.Manual), Is.EqualTo("Manual"));
            Assert.That(HistoryTab.SourceLabel(HistorySource.Agent), Is.EqualTo("Agent"));
            Assert.That(HistoryTab.SourceLabel(HistorySource.Midi), Is.EqualTo("MIDI"));
        }
    }
}
