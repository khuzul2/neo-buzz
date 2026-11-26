// Fork (docs/specs/neo-audio.md "Takes and comping"): takes and comps of a track, as the Arrange view's take lanes and the agent's `takes` tool use them.
// A take is a clip on a take lane (a lane the engine never plays); a comp is the main lane's clips made from ranges of takes. Nothing is copied or
// destroyed: an audio comp piece is a clip of its own clip over the same asset, trimmed to its range, with a crossfade of a few milliseconds at
// each cut. Every edit here is one undo step.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core
{
    /// <summary>A take of a track: a clip on one of its take lanes.</summary>
    internal sealed record TakeInfo(int Number, int LaneNumber, ISequence Lane, ClipItem Clip, bool Used);

    /// <summary>An empty take lane taken away, on the song's history; undo puts it back where it was, as a take lane.</summary>
    [HistoryName("Remove take lane")]
    internal sealed class RemoveTakeLaneAction : IAction
    {
        readonly ReBuzzCore core;
        readonly string machine;
        readonly int index;

        public RemoveTakeLaneAction(ReBuzzCore core, ISequence lane)
        {
            this.core = core;
            machine = lane.Machine.Name;
            index = core.SongCore.Sequences.IndexOf(lane);
        }

        public void Do()
        {
            if (index < 0 || index >= core.SongCore.Sequences.Count) return;
            core.SongCore.RemoveSequence(core.SongCore.Sequences[index]);
        }

        public void Undo()
        {
            var m = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == machine);
            if (m == null || index < 0 || index > core.SongCore.Sequences.Count) return;
            core.SongCore.AddSequence(m, index);
            core.Arrangement?.SetRole(core.SongCore.Sequences[index], LaneRole.Take);
        }
    }

    internal sealed partial class ClipEditor
    {
        /// <summary>The length of the crossfade at each cut of an audio comp, in milliseconds.</summary>
        public const double CompCrossfadeMs = 5;

        ArrangementStore Arrangement => core.Arrangement;

        /// <summary>A machine's lanes, in the song's order.</summary>
        public List<ISequence> LanesOf(IMachine machine) => Song.Sequences.Where(s => s.Machine == machine).ToList();

        /// <summary>The machine's take lanes in order.</summary>
        public List<ISequence> TakeLanesOf(IMachine machine) => LanesOf(machine).Where(s => Arrangement.RoleOf(s) == LaneRole.Take).ToList();

        /// <summary>The machine's first main lane (what the comp is made on).</summary>
        public ISequence MainLaneOf(IMachine machine) => LanesOf(machine).FirstOrDefault(s => Arrangement.RoleOf(s) == LaneRole.Main);

        /// <summary>The takes of a machine, numbered from 1 in lane order and then time: whether the main lane plays any of each.</summary>
        public List<TakeInfo> TakesOf(IMachine machine)
        {
            var result = new List<TakeInfo>();
            var main = MainLaneOf(machine);
            var played = main == null ? new List<ClipItem>() : Read(main);
            int number = 1;
            var lanes = TakeLanesOf(machine);
            for (int l = 0; l < lanes.Count; l++)
                foreach (var clip in Read(lanes[l]).OrderBy(c => c.Time))
                    result.Add(new TakeInfo(number++, l + 1, lanes[l], clip, IsUsed(clip, played)));
            return result;
        }

        /// <summary>A take is used while the main lane plays it (its clip, or an audio comp piece of its asset) anywhere over it.</summary>
        bool IsUsed(ClipItem take, List<ClipItem> main)
        {
            string asset = IsAudio(take.Clip) ? AudioOf(take.Clip)?.Asset : null;
            foreach (var m in main)
            {
                if (m.End <= take.Time || m.Time >= take.End) continue;
                if (ReferenceEquals(m.Clip, take.Clip)) return true;
                if (asset != null && IsAudio(m.Clip) && AudioOf(m.Clip)?.Asset == asset) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ comping audio

        string CompAudio(ClipItem item, int from, int to, ISequence main)
        {
            string result = null;
            Group(() =>
            {
                var copy = CloneAudioClip(item.Clip, main.Machine);
                if (copy == null) { result = "The take couldn't be copied."; return; }
                // The piece plays the take's audio over the same song time: its start in the clip is where the range starts in the take.
                var spec = new ClipSpec(0, copy, from, to - from, item.Spec.ClipPositionAt(from) ?? 0, 0, 0, false, item.Spec.Gain);
                var plan = new Plan(this) { NoCrossfade = true };
                plan.Overwrite(main, from, to);
                plan.Write(main, spec, newId: true);
                Commit(plan);
                CompCrossfades(main, copy);
            });
            return result;
        }

        /// <summary>Crossfades a comp piece with the audio clips that touch it, a few milliseconds long (the earlier one's tail under the later one's fade in).</summary>
        void CompCrossfades(ISequence main, IClip piece)
        {
            var items = Read(main).Where(i => IsAudio(i.Clip)).ToList();
            var me = items.FirstOrDefault(i => ReferenceEquals(i.Clip, piece));
            if (me == null) return;
            double beats = CompCrossfadeMs / 1000.0 * Math.Max(1, core.BPM) / 60.0;
            var before = items.FirstOrDefault(i => i.End == me.Time && !ReferenceEquals(i.Clip, piece));
            var after = items.FirstOrDefault(i => i.Time == me.End && !ReferenceEquals(i.Clip, piece));
            if (before != null)
            {
                SetAudioFields(before.Clip, a => a.Tail = beats);
                SetAudioFields(piece, a => { a.FadeIn = beats; a.CrossIn = true; });
            }
            if (after != null)
            {
                SetAudioFields(piece, a => a.Tail = beats);
                SetAudioFields(after.Clip, a => { a.FadeIn = beats; a.CrossIn = true; });
            }
            FixCrossfades(main);
        }

        // ------------------------------------------------------------------ the takes tool

        /// <summary>Comps several ranges (each from a take, in song ticks) onto the machine's main lane as one undo step.</summary>
        public string CompRanges(IMachine machine, IReadOnlyList<(int Take, int From, int To)> ranges)
        {
            var main = MainLaneOf(machine);
            if (main == null) return $"{machine?.Name} has no main lane to comp onto.";
            var takes = TakesOf(machine);
            if (takes.Count == 0) return $"{machine.Name} has no takes.";
            if (ranges == null || ranges.Count == 0) return "Give at least one range.";
            foreach (var r in ranges)
                if (takes.All(t => t.Number != r.Take)) return $"There is no take {r.Take} (takes are numbered 1 to {takes.Count}).";
            string result = null;
            Group(() =>
            {
                foreach (var r in ranges)
                {
                    var take = takes.First(t => t.Number == r.Take);
                    int from = Math.Max(r.From, take.Clip.Time), to = Math.Min(r.To, take.Clip.End);
                    result = Comp(take.Lane, from, to, main);
                    if (result != null) return;
                }
            });
            return result;
        }

        /// <summary>Plays one whole take on the main lane (its range, or all of it).</summary>
        public string ChooseTake(IMachine machine, int number, int? from = null, int? to = null)
        {
            var take = TakesOf(machine).FirstOrDefault(t => t.Number == number);
            if (take == null) return $"There is no take {number}.";
            return CompRanges(machine, new[] { (number, from ?? take.Clip.Time, to ?? take.Clip.End) });
        }

        /// <summary>
        /// Flatten comp: renders what the main lane plays between two ticks (the comp, crossfades and all) into one new asset and one clip.
        /// Without a range, the whole span of the lane's audio. Audio only.
        /// </summary>
        public string FlattenComp(IMachine machine, int? from, int? to, out IClip made)
        {
            made = null;
            var main = MainLaneOf(machine);
            if (main == null) return $"{machine?.Name} has no main lane.";
            var audio = Read(main).Where(i => IsAudio(i.Clip)).ToList();
            if (audio.Count == 0) return "Flatten renders audio: there is no audio clip on the main lane.";
            return Consolidate(main, from ?? audio.Min(i => i.Time), to ?? audio.Max(i => i.End), out made);
        }

        /// <summary>Deletes the takes the main lane doesn't play (their clips, their clips, and take lanes left empty). Returns how many.</summary>
        public int DeleteUnusedTakes(IMachine machine)
        {
            var unused = TakesOf(machine).Where(t => !t.Used).ToList();
            if (unused.Count == 0) return 0;
            Group(() =>
            {
                var plan = new Plan(this);
                foreach (var t in unused) plan.Remove(t.Clip);
                Commit(plan);
                foreach (var t in unused)
                    if (t.Clip.Clip?.Machine != null && Uses(t.Clip.Clip) == 0)
                        Layout?.DeleteClip(t.Clip.Clip);
                // Take lanes with nothing on them go (from the last: the indices of the earlier ones stay).
                foreach (var lane in TakeLanesOf(machine).Where(l => Read(l).Count == 0 && l.Events.Count == 0).Reverse().ToList())
                    core.SongCore.ActionStack.Do(new RemoveTakeLaneAction(core, lane));
            });
            return unused.Count;
        }
    }
}
