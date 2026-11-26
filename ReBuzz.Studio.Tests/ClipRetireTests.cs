// Fork (docs/specs/clips.md §10.0e, CU7): the old clip paths are gone. The clip editor keeps no events of its own (its columns are views of the
// store's lanes, however the machine came to be), and the data it saves holds the shape of its view and no events.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class ClipRetireTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static string RepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "ReBuzz.sln"))) return dir.FullName;
            return null;
        }

        /// <summary>What every clip editor of the song saves: the blob's clips, columns and events.</summary>
        static List<(string Machine, BlobData Blob)> Blobs()
        {
            var list = new List<(string, BlobData)>();
            foreach (var m in Core.SongCore.MachinesList.Where(m => m.EditorMachine != null))
            {
                var data = m.ClipEditorData;
                if (data == null || data.Length == 0) continue;
                list.Add((m.Name, ClipBlob.Parse(data)));
            }
            return list;
        }

        /// <summary>The events of every lane of every clip of the song, one line a lane, in a fixed order.</summary>
        static List<string> AllLanes()
        {
            var lines = new List<string>();
            foreach (var m in Core.SongCore.MachinesList)
                foreach (var clip in m.Clips)
                    foreach (var c in clip.Columns)
                    {
                        var events = c.GetEvents(int.MinValue, int.MaxValue).OrderBy(e => e.Time).ThenBy(e => e.Value).ToList();
                        if (events.Count == 0) continue;
                        lines.Add($"{m.Name}|{clip.Name}|{c.Type}|{c.Parameter?.Name}|{c.Track}|" +
                                  string.Join(",", c.Metadata.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value)) + "|" +
                                  string.Join(" ", events.Select(e => $"{e.Time}:{e.Value}+{e.Duration}")));
                    }
                    ;
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        [AvaloniaTest]
        public void TheEditorsDataHoldsTheShapeOfItsViewAndNoEvents()
        {
            var machine = ClipOpenTests.MigratedProbe();
            var lanes = AllLanes();
            Assert.That(lanes, Is.Not.Empty, "the clip's events are lanes in the store");
            var blobs = Blobs().Where(b => b.Machine == machine.Name).ToList();
            Assert.That(blobs.Count, Is.EqualTo(1), "the machine's editor has data");
            var blob = blobs[0].Blob;
            Assert.That(blob.Problem, Is.Null, "it reads whole");
            Assert.That(blob.Format, Is.EqualTo(BlobFormat.Mpe1), "in the editor's own format (the PatternXP format is read, never written)");
            Assert.That(blob.EventCount, Is.EqualTo(0), "no events: the store is the source");
            var clip = blob.Clips.Single(c => c.Name == "P");
            Assert.That(clip.Columns, Is.Not.Empty, "the columns of the view are in it");
            Assert.That(clip.Columns.All(c => c.BeatRows.Count == clip.Beats), "with the rows of each of their beats");
        }

        [AvaloniaTest]
        public void TheRepoSongsSaveOpenAndSaveToTheSameBytesAndKeepEveryLane()
        {
            string root = RepoRoot();
            if (root == null) Assert.Inconclusive("the repository isn't beside the tests");
            var songs = Directory.GetFiles(Path.Combine(root, "Other", "Templates", "Songs"), "*.nbl").OrderBy(p => p, StringComparer.Ordinal).ToList();
            Assert.That(songs, Is.Not.Empty);
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; Core.Modified = false; });
            string dir = Path.Combine(Path.GetTempPath(), "clip-retire-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                foreach (var song in songs)
                {
                    string name = Path.GetFileNameWithoutExtension(song);
                    Core.Modified = false;
                    Dialogs.Responder = (m, c, b) => UserAnswer.No;
                    Assert.That(Core.TryOpenSongFile(song), Is.True, name + " opens");
                    var lanes = AllLanes();
                    string a = Path.Combine(dir, name + "-a.nbl"), b = Path.Combine(dir, name + "-b.nbl");
                    Core.SaveSongCopy(a);
                    foreach (var (machine, blob) in Blobs())
                        Assert.That(blob.EventCount, Is.EqualTo(0), $"{name}: {machine}'s editor data holds no events");

                    Core.Modified = false;
                    Assert.That(Core.TryOpenSongFile(a), Is.True, name + " saved opens");
                    Assert.That(AllLanes(), Is.EqualTo(lanes), $"{name}: every lane is as it was after the save and the reopen");
                    Core.SaveSongCopy(b);
                    Assert.That(File.ReadAllBytes(b), Is.EqualTo(File.ReadAllBytes(a)), $"{name}: open, save, open, save gives the same bytes");
                    TestContext.Out.WriteLine($"{name}: {lanes.Count} lanes with events, {Blobs().Count} editor blobs with 0 events, {new FileInfo(a).Length} bytes, saved twice the same");
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>
        /// A machine made again (the undo of a delete: its editor is made from the data it saved and its clips' lanes are put back) shows the
        /// store's lanes: the editor keeps no list of its own, so there is nothing for the two to disagree about.
        /// </summary>
        [AvaloniaTest]
        public void AMachineMadeAgainByUndoShowsTheStoresLanesInItsEditor()
        {
            using var rig = new SessionRig("Lead");
            var song = Core.SongCore;
            var lead = rig.Machine("Lead");
            var riff = rig.Clip(lead, "Riff", 16);
            var parameter = lead.ParameterGroups[1].Parameters[0];
            riff.InsertColumn(riff.Columns.Count, parameter, 0);
            var lane = riff.Columns.Last(c => c.Type == ClipColumnType.Parameter);
            lane.SetEvents(new[] { new ClipEvent { Time = 0, Value = 5 }, new ClipEvent { Time = 4 * ClipEvent.TimeBase, Value = 7 } }, true);
            Assert.That(Shown(riff, parameter.Name), Is.EqualTo(new[] { 5, 7 }), "the editor shows the lane");

            song.DeleteMachines(new[] { (IMachine)lead });
            Assert.That(song.MachinesList.Any(m => m.Name == "Lead"), Is.False, "deleted");
            song.Undo();
            var again = song.MachinesList.First(m => m.Name == "Lead");
            var clip = again.Clips.Single(c => c.Name == "Riff");
            Assert.That(clip.Columns.Any(c => c.Type == ClipColumnType.Parameter && c.GetEvents(int.MinValue, int.MaxValue).Count() == 2), Is.True, "the lane is back");
            Assert.That(Shown(clip, parameter.Name), Is.EqualTo(new[] { 5, 7 }), "and the new editor shows it");
        }

        static int[] Shown(IClip clip, string parameter) =>
            clip.ClipEditorMachineEvents.Where(c => c.Parameter.Name == parameter).SelectMany(c => c.Events).OrderBy(e => e.Time).Select(e => e.Value).ToArray();
    }
}
