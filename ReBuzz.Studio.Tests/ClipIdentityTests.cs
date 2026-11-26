// Fork (docs/specs/clips.md §11, CU7): a clip is keyed by its name in the song file (its settings, its Session slots, the tracker's view, the
// arrangement's placements), so a rename must carry every one of those keys with it. These tests rename a clip by each path the app has and
// read everything back, in the song and after a save and a reopen.

using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class ClipIdentityTests
    {
        const int TB = ClipEvent.TimeBase;
        static ReBuzzCore Core => Studio.Host.Core;

        static bool Holds(byte[] data, string clipName)
        {
            if (data == null) return false;
            var needle = Encoding.ASCII.GetBytes(clipName + "\0");
            for (int i = 0; i + needle.Length <= data.Length; i++)
            {
                int k = 0;
                while (k < needle.Length && data[i + k] == needle[k]) k++;
                if (k == needle.Length) return true;
            }
            return false;
        }

        /// <summary>Everything that is keyed by the clip's name, read from the song as it is now (after a rename or a reopen).</summary>
        static void AssertIntact(string machineName, string name, string gone, string where)
        {
            var layout = Core.SessionLayout;
            var machine = Core.SongCore.MachinesList.First(m => m.Name == machineName);
            var clip = machine.Clips.Single(c => c.Name == name);
            Assert.That(machine.Clips.Any(c => c.Name == gone), Is.False, where + ": the old name is gone");

            var settings = layout.ClipOf(clip);
            Assert.That(settings.Legato, Is.True, where + ": its clip settings (legato)");
            Assert.That(settings.Velocity, Is.EqualTo(40), where + ": its clip settings (velocity)");
            Assert.That(settings.LoopEnd, Is.EqualTo(8), where + ": its clip settings (loop end)");
            Assert.That(layout.Document.GetClip(machineName, gone).IsDefault, Is.True, where + ": nothing is left under the old name");

            Assert.That(layout.ClipAt(0, 1)?.Name, Is.EqualTo(name), where + ": its Session slot");

            var seq = Core.SongCore.SequencesList.First(s => s.Machine == machine);
            var placed = seq.Events.Values.Where(e => e.Type == SequenceEventType.PlayClip).Select(e => e.Clip?.Name).ToList();
            Assert.That(placed, Is.EqualTo(new[] { name }), where + ": its arrangement placement");

            var lane = clip.Columns.Single(c => c.Type == ClipColumnType.Parameter && c.Metadata.ContainsKey(ClipMeta.Grid));
            Assert.That(lane.Metadata[ClipMeta.Grid], Is.EqualTo("rpb=8"), where + ": the tracker's rows per beat");
            Assert.That(lane.Metadata[ClipMeta.Mute], Is.EqualTo("1"), where + ": the tracker's mute");
            Assert.That(lane.GetEvents(int.MinValue, int.MaxValue).Select(e => e.Value), Is.EqualTo(new[] { 5 }), where + ": the lane's events");

            var blob = machine.ClipEditorData;
            Assert.That(Holds(blob, name), Is.True, where + ": the editor's view is saved under the new name");
            Assert.That(Holds(blob, gone), Is.False, where + ": and not under the old one");
        }

        [AvaloniaTest]
        public void RenamingAClipCarriesItsSettingsSlotPlacementAndViewThroughASaveAndReopen([Values("editor", "session", "agent")] string how)
        {
            string path = Path.Combine(Path.GetTempPath(), "clip-identity-" + Guid.NewGuid().ToString("N") + ".nbl");
            try
            {
                using (var rig = new SessionRig("Lead"))
                {
                    var lead = rig.Machine("Lead");
                    var riff = rig.Clip(lead, "Riff", 16, (0, 60), (4, 64));
                    var layout = rig.Layout;

                    var settings = layout.ClipOf(riff);
                    settings.Legato = true;
                    settings.Velocity = 40;
                    settings.LoopEnd = 8;
                    layout.SetClipSettings(riff, settings);
                    layout.SetSlot(0, 1, riff);
                    ReBuzz.Core.Session.ClipColors.Set(riff, 3);
                    var seq = Core.SongCore.SequencesList.First(s => s.Machine == lead);
                    seq.SetEvent(0, new SequenceEvent(SequenceEventType.PlayClip, riff) { Span = 16 });

                    var parameter = lead.ParameterGroups[1].Parameters[0];
                    riff.InsertColumn(riff.Columns.Count, parameter, 0);
                    var lane = riff.Columns.Last(c => c.Type == ClipColumnType.Parameter);
                    lane.Metadata[ClipMeta.Grid] = "rpb=8";
                    lane.Metadata[ClipMeta.Mute] = "1";
                    lane.SetEvents(new[] { new ClipEvent { Time = 0, Value = 5 } }, true);

                    switch (how)
                    {
                        case "editor": lead.RenameClip(riff, "Hook"); break;
                        case "session": Assert.That(layout.RenameClip(riff, "Hook"), Is.True); break;
                        default: AgentActions.RenameClip(Core, riff, "Hook"); break;
                    }
                    rig.Settle();
                    AssertIntact("Lead", "Hook", "Riff", "after the rename");
                    Assert.That(ReBuzz.Core.Session.ClipColors.Get(riff), Is.EqualTo(3), "its colour (kept by the clip, not its name)");

                    Core.SaveSongCopy(path);
                }
                AudioHygieneTests.NewSong();
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                try { Assert.That(Core.TryOpenSongFile(path), Is.True, "reopened"); }
                finally { Dialogs.Responder = null; }
                AssertIntact("Lead", "Hook", "Riff", "after a save and reopen");
            }
            finally { File.Delete(path); }
        }

        [AvaloniaTest]
        public void AnUndoAndARedoOfARenameCarryTheKeysBackAndForth()
        {
            using var rig = new SessionRig("Lead");
            var song = Core.SongCore;
            var lead = rig.Machine("Lead");
            var riff = rig.Clip(lead, "Riff", 16, (0, 60));
            var layout = rig.Layout;
            var settings = layout.ClipOf(riff);
            settings.Velocity = 40;
            layout.SetClipSettings(riff, settings);
            layout.SetSlot(0, 2, riff);
            song.ActionStack.Clear();

            lead.RenameClip(riff, "Hook");
            rig.Settle();
            Assert.That(layout.Document.GetClip("Lead", "Hook").Velocity, Is.EqualTo(40), "renamed");
            Assert.That(layout.ClipAt(0, 2)?.Name, Is.EqualTo("Hook"));
            song.Undo();
            rig.Settle();
            Assert.That(riff.Name, Is.EqualTo("Riff"));
            Assert.That(layout.Document.GetClip("Lead", "Riff").Velocity, Is.EqualTo(40), "the settings came back with the name");
            Assert.That(layout.Document.GetClip("Lead", "Hook").IsDefault, Is.True, "and none are left under the other");
            Assert.That(layout.ClipAt(0, 2)?.Name, Is.EqualTo("Riff"));
            song.Redo();
            rig.Settle();
            Assert.That(layout.Document.GetClip("Lead", "Hook").Velocity, Is.EqualTo(40), "and forth again");
            Assert.That(layout.ClipAt(0, 2)?.Name, Is.EqualTo("Hook"));
        }

        [AvaloniaTest]
        public void TwoClipsThatSwapNamesEachKeepTheirOwnSettings()
        {
            using var rig = new SessionRig("Lead");
            var lead = rig.Machine("Lead");
            var a = rig.Clip(lead, "A", 16, (0, 60));
            var b = rig.Clip(lead, "B", 16, (0, 62));
            var layout = rig.Layout;
            var sa = layout.ClipOf(a); sa.Velocity = 10; layout.SetClipSettings(a, sa);
            var sb = layout.ClipOf(b); sb.Velocity = 20; layout.SetClipSettings(b, sb);

            lead.RenameClip(a, "Tmp"); rig.Settle();
            lead.RenameClip(b, "A"); rig.Settle();
            lead.RenameClip(a, "B"); rig.Settle();
            Assert.That(layout.ClipOf(a).Velocity, Is.EqualTo(10), "the first clip keeps its settings under its new name");
            Assert.That(layout.ClipOf(b).Velocity, Is.EqualTo(20), "the second keeps its own");
            Assert.That(layout.Document.GetClip("Lead", "Tmp").IsDefault, Is.True, "nothing is left under the name in between");
        }
    }
}
