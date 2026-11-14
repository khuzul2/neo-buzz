// Fork (docs/specs/history-snapshots.md HS2): the Snapshots section of the History tab: the list (name, time, manual or auto),
// save, rename, delete, the diff, the A/B toggle, and restore, which applies only while stopped and says so while the song plays.

using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.SongHistory;
using ReBuzz.Studio.Views.Inspector;

namespace ReBuzz.Studio.Tests
{
    public class SnapshotPanelTests
    {
        static string Text(Avalonia.Controls.Control row) => string.Join(" | ", row.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

        static void Settle() => Dispatcher.UIThread.RunJobs();

        [AvaloniaTest]
        public void TheSectionSavesListsRenamesDeletesAndDiffs()
        {
            string root = Path.Combine(Path.GetTempPath(), "snapshot-panel-" + Environment.ProcessId);
            string old = SongSnapshots.UntitledRoot;
            SongSnapshots.UntitledRoot = root;
            using var restore = Studio.AfterTest(() => { SongSnapshots.UntitledRoot = old; try { Directory.Delete(root, true); } catch (IOException) { } });
            using var rig = new SessionRig("Lead");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var tab = new HistoryTab(core);
            var window = new Window { Width = 360, Height = 700, Content = tab };
            window.Show();
            try
            {
                var section = tab.Snapshots;
                Assert.That(section.Rows.Count, Is.EqualTo(0), "none yet");
                section.NameBox.Text = "first";
                var a = section.Save();
                core.Snapshots.Flush();
                Settle();
                section.Refresh();
                Assert.That(section.Rows.Count, Is.EqualTo(1));
                Assert.That(Text(section.Rows[0]), Does.Contain("first").And.Contain("manual"));

                rig.Add("Neo Poly", "Extra");
                var auto = core.Snapshots.Auto("before thing");
                core.Snapshots.Flush();
                section.Refresh();
                Assert.That(section.Rows.Count, Is.EqualTo(2));
                Assert.That(Text(section.Rows[0]), Does.Contain("Auto: before thing").And.Contain("auto"), "the newest first, labelled auto");

                section.Select(a.Id);
                Assert.That(section.NameBox.Text, Is.EqualTo("first"));
                Assert.That(section.ShowDiff(), Does.Contain("Extra").And.Contain("added").Or.Contain("removed"), "the snapshot against the song now");
                section.NameBox.Text = "renamed";
                Assert.That(core.Snapshots.Rename(a.Id, section.NameBox.Text));
                section.Refresh();
                Assert.That(Text(section.Rows.Last()), Does.Contain("renamed"));
                Assert.That(core.Snapshots.Delete(auto.Id));
                core.Snapshots.Flush();
                section.Refresh();
                Assert.That(section.Rows.Count, Is.EqualTo(1));
            }
            finally { window.Close(); tab.Dispose(); Settle(); core.Modified = false; }
        }

        [AvaloniaTest]
        public void RestoreWaitsForStopAndAbSwitchesBetweenTwoSnapshots()
        {
            string root = Path.Combine(Path.GetTempPath(), "snapshot-panel-ab-" + Environment.ProcessId);
            string old = SongSnapshots.UntitledRoot;
            SongSnapshots.UntitledRoot = root;
            using var restore = Studio.AfterTest(() => { SongSnapshots.UntitledRoot = old; try { Directory.Delete(root, true); } catch (IOException) { } });
            using var rig = new SessionRig("Lead");
            var core = rig.Core;
            core.Snapshots.Bind(null);
            var tab = new HistoryTab(core);
            var window = new Window { Width = 360, Height = 700, Content = tab };
            window.Show();
            try
            {
                var section = tab.Snapshots;
                var one = core.Snapshots.Capture("one");
                rig.Add("Neo Poly", "Two");
                var two = core.Snapshots.Capture("two");
                core.Snapshots.Flush();
                section.Refresh();
                section.Select(one.Id);
                Assert.That(section.RestoreButton.IsEnabled, Is.True);

                core.Playing = true;
                try
                {
                    section.Refresh();
                    Assert.That(section.RestoreButton.IsEnabled, Is.True, "restore works while playing (HS3)");
                    // No engine ticks run in this test, so there is no launch clock to apply on: the restore waits for the stop and says so.
                    Assert.That(section.Restore(one.Id), Is.True);
                    Assert.That(section.StatusText, Does.Contain("waits for the transport to stop"));
                    Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Two"), "the song is untouched while it plays");
                }
                finally { core.Playing = false; Settle(); }
                core.Snapshots.Pump();
                Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Two"), Is.False, "applied at the stop");
                section.Refresh();
                Assert.That(section.RestoreButton.IsEnabled, Is.True);

                // A/B: A is "one" (no Two), B is "two" (with it); the toggle goes to A first, then back and forth.
                section.MarkA(one.Id);
                section.MarkB(two.Id);
                Assert.That(section.ToggleButton.IsEnabled, Is.True);
                section.ToggleAb();
                Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Two"), Is.False, "A");
                section.ToggleAb();
                Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Two"), Is.True, "B");
                section.ToggleAb();
                Assert.That(core.SongCore.MachinesList.Any(m => m.Name == "Two"), Is.False, "A again");
                Assert.That(section.StatusText, Does.Contain("Now A"));
            }
            finally { window.Close(); tab.Dispose(); Settle(); core.Modified = false; }
        }
    }
}
