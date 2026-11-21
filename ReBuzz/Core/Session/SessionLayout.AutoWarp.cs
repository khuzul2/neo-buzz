// Fork (docs/specs/neo-audio.md, AW1 as built): auto-warp in the layout. A clip's audio is analysed (BeatTracker, cached with the audio) and its markers
// and tempo replaced, as one undo step (EditAudio), on import for a long file, from the Audio page's Auto-warp button and from the agent's audio_clip_set.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Clips;

namespace ReBuzz.Core.Session
{
    internal sealed partial class SessionLayout
    {
        /// <summary>What the page and the agent say when no beats are found.</summary>
        internal const string NoBeatsFound = "No beats were found in the audio (no steady pulse to follow), so the clip is as it was.";

        /// <summary>The beats of a clip's audio as auto-warp would mark them (null and a reason when it can't), from <paramref name="fromSeconds"/> on.</summary>
        internal AutoWarpResult AnalyseAutoWarp(IPattern pattern, double fromSeconds, out string error)
        {
            error = null;
            var audio = AudioOf(pattern);
            var source = audio == null ? null : ClipAudioOf(audio)?.Source;
            if (source == null) { error = "The clip's audio is not there."; return null; }
            if (!double.IsFinite(fromSeconds) || fromSeconds < 0 || fromSeconds >= source.Seconds) { error = $"from_seconds: 0 to {source.Seconds:0.###} (the audio's length)."; return null; }
            var result = AudioAnalysis.AutoWarp(source, Session.BeatsPerBar, fromSeconds);
            if (!result.Found) error = NoBeatsFound;
            return result.Found ? result : null;
        }

        /// <summary>
        /// Puts what auto-warp found into the clip's settings: from the start (<paramref name="fromSeconds"/> 0) its markers and tempo are replaced; from
        /// later in the audio the markers before it stay and the rest are replaced, numbered on from the bar line of the clip's own grid nearest where
        /// they start, and the tempo is left. A tempo that is steady and starts on the downbeat is one tempo and no marker.
        /// </summary>
        internal void ApplyAutoWarp(SessionDocument.AudioSettings audio, AudioSource source, AutoWarpResult found, double fromSeconds)
        {
            int perBar = Math.Max(1, found.BeatsPerBar);
            if (fromSeconds <= 0)
            {
                audio.Markers = found.Markers.ToList();
                audio.Bpm = Math.Clamp(found.Bpm, 1, 999);
                return;
            }
            var old = MapOf(source, audio);
            double first = found.FirstDownbeat;
            double beat0 = Math.Round(old.BeatAt(first) / perBar) * perBar;
            var markers = (audio.Markers ?? new List<(double Sample, double Beat)>()).Where(m => m.Sample < first - 1).ToList();
            // The audio before has to keep the line it had: a marker at its start when it had none.
            if (markers.Count == 0) markers.Add((0, old.BeatAt(0)));
            foreach (var (sample, beat) in found.Markers) markers.Add((sample, beat0 + beat));
            audio.Markers = markers;
        }

        /// <summary>Auto-warps a clip (undoable as one step); the tempo it found, or 0 and a reason in <paramref name="error"/>.</summary>
        public double AutoWarp(IPattern pattern, double fromSeconds, out string error)
        {
            error = EditAudio(pattern, new AudioClipEdit { AutoWarp = true, FromSeconds = fromSeconds }, null, "Auto-warp");
            return error == null ? AudioOf(pattern)?.Bpm ?? 0 : 0;
        }

        /// <summary>
        /// A file just imported (not yet in a slot) that is longer than eight bars and has a tempo is warped by its beats where they move; false when it isn't
        /// (the preference is off, it is shorter, or no beats were found: it keeps the one tempo the import found).
        /// </summary>
        bool AutoWarpOnImport(SessionDocument.AudioSettings settings, AudioSource source, double beats)
        {
            if (!AutoWarpPolicy.Enabled || beats <= AutoWarpPolicy.LongBars * Math.Max(1, Session.BeatsPerBar)) return false;
            var found = AudioAnalysis.AutoWarp(source, Session.BeatsPerBar);
            if (!found.Found) return false;
            ApplyAutoWarp(settings, source, found, 0);
            return true;
        }
    }
}
