// Fork (docs/specs/daw-modes.md AR5, §6): the links between the arrangement and the Session View. An arrangement clip
// launches in the session (its region as the launch options) and the arrangement takes the track back
// (ISession.BackToArrangement); a clip is given to a slot, and a slot is dragged onto a lane. A clip in both modes is
// the same pattern (one play position, the session clip keeps it), so a slot given a clip shares its pattern; a clip
// made from a slot takes its region from the slot's clip settings, as scene insert does (SceneToArrangement.LoopOf).
// Every clip made on a lane gets a clip id of its own (ids are never reused, §8.1). UI thread.

using System;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core
{
    internal sealed class ArrangementLinks
    {
        readonly ReBuzzCore buzz;

        public ArrangementLinks(ReBuzzCore buzz) => this.buzz = buzz;

        SessionLayout Layout => buzz.SessionLayout;

        /// <summary>The clip's region as the session clip settings that would play it.</summary>
        static SessionDocument.ClipSettings SettingsOf(ClipSpec spec) =>
            new SessionDocument.ClipSettings { Loop = spec.Loop, Start = spec.Start, LoopStart = spec.LoopStart, LoopEnd = spec.LoopEnd };

        /// <summary>Launches an arrangement clip in the Session View, on its lane's sequence: that track plays it (looped as the clip is, from where it starts in its pattern) until the arrangement takes it back.</summary>
        public string Launch(ClipItem item)
        {
            if (item?.Sequence == null || item.Pattern == null) return "Select a clip first.";
            var layout = Layout;
            if (layout == null) return "The Session View isn't ready.";
            var options = SessionLayout.ToOptions(SettingsOf(item.Spec), 127, -1, buzz.Session.BeatsPerBar, buzz.TPB);
            buzz.Session.Launch(item.Sequence, item.Pattern, options);
            return null;
        }

        /// <summary>Whether any track has been taken over by the session.</summary>
        public bool AnyLaunched => buzz.SessionCore?.IsAnyArrangementOverridden == true;

        /// <summary>The arrangement plays on every track again.</summary>
        public void BackToArrangement() => buzz.SessionCore?.BackToArrangement();

        /// <summary>
        /// Gives a slot an arrangement clip's pattern (shared: a copy of the place, not of the notes). With no slot named, the first empty one of the machine's
        /// session track (a scene is added if there is none). A pattern whose clip settings are the defaults takes the clip's region as its settings.
        /// </summary>
        public string ToSlot(ClipItem item, out int track, out int scene, int toTrack = -1, int toScene = -1)
        {
            track = scene = -1;
            if (item?.Pattern?.Machine == null) return "Select a clip first.";
            var layout = Layout;
            var doc = layout?.Ensure();
            if (doc == null) return "The Session View isn't ready.";
            string machine = item.Pattern.Machine.Name;
            int tr = toTrack >= 0 ? toTrack : doc.Tracks.FindIndex(t => t.Machine == machine);
            if (tr < 0 || tr >= doc.Tracks.Count || doc.Tracks[tr].Machine != machine) return "The machine has no track in the Session View.";
            int sc = toScene;
            if (sc < 0)
            {
                sc = Enumerable.Range(0, layout.SceneCount).FirstOrDefault(s => layout.PatternAt(tr, s) == null, -1);
                if (sc < 0)
                {
                    sc = layout.SceneCount;
                    layout.AddScenesTo(sc + 1);
                }
            }
            track = tr;
            scene = sc;
            var stack = buzz.SongCore.ActionStack;
            stack.BeginActionGroup();
            try
            {
                stack.Label("Clip to slot");
                layout.SetSlot(track, scene, item.Pattern);
                if (layout.ClipOf(item.Pattern).IsDefault && !SettingsOf(item.Spec).IsDefault)
                    layout.SetClipSettings(item.Pattern, SettingsOf(item.Spec));
            }
            finally
            {
                stack.EndActionGroup();
            }
            return layout.PatternAt(track, scene) == item.Pattern ? null : "The slot couldn't take the clip.";
        }

        /// <summary>
        /// Makes a clip of a slot's pattern on a lane of its machine (the first main lane when none is given) at a song tick (after the lane's last clip, on a bar line, when none is given): shared with the slot,
        /// its region from the slot's clip settings, a clip id of its own.
        /// </summary>
        public string SlotToLane(int track, int scene, int time = -1, ISequence lane = null)
        {
            var layout = Layout;
            var pattern = layout?.PatternAt(track, scene);
            if (pattern == null) return "The slot is empty.";
            lane ??= buzz.Arrangement.Takes.MainLaneOf(pattern.Machine);
            if (lane == null) return "The machine has no lane in the arrangement.";
            var editor = new ClipEditor(buzz);
            if (time < 0)
            {
                var clips = editor.Read(lane);
                time = clips.Count == 0 ? 0 : editor.RoundUpToBar(clips.Max(i => i.End));      // after the lane's last clip
            }
            return editor.Place(lane, pattern, time);
        }
    }
}
