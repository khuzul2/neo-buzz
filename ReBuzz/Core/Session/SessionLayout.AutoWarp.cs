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
        internal void ApplyAutoWarp(SessionDocument.AudioSettings audio, AudioSource source, AutoWarpResult found, double fromSeconds, double? toSeconds = null)
        {
            int perBar = Math.Max(1, found.BeatsPerBar);
            if (toSeconds is double until) { ApplyAutoWarpRange(audio, source, found, fromSeconds, until); return; }
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

        /// <summary>What the last "to grid" did (the page's message and the agent's answer read it right after the edit); null before the first.</summary>
        internal GridWarpReport LastGridReport { get; private set; }

        /// <summary>
        /// "To grid" (AW5): a marker on each transient, pulled toward the clip's grid by the song's Quantize strength. The markers are the clip's now (auto-warp has put
        /// its beats in them, or the clip's own line when it found none).
        /// </summary>
        internal void ApplyToGrid(SessionDocument.AudioSettings audio, AudioSource source, List<double> transients, GridWarpReport report, double fromSeconds, bool beatsFound, double? toSeconds = null)
        {
            LastGridReport = report;
            report.BeatsFound = beatsFound;
            var m = WarpMath.Effective(audio.Markers, source.Length, source.SampleRate, audio.Bpm);
            var r = WarpMath.ToGrid(m, transients, audio.AutoGridBeats, QuantizeStrength / 100.0, audio.AutoReach / 100.0, fromSeconds * source.SampleRate, source.Length, report, toSeconds is double u ? u * source.SampleRate : double.PositiveInfinity);
            if (r.Ok) audio.Markers = r.Markers;
            else report.Made = 0;
        }

        /// <summary>
        /// Auto-warp of a range (AW5, "Auto-warp selection"): the markers before <paramref name="fromSeconds"/> and after <paramref name="toSeconds"/> stay, the range's two ends are pinned
        /// with a marker on the line the clip has there (so the audio outside doesn't move), and inside it the beats auto-warp found take the place of the markers. The tempo is left.
        /// </summary>
        void ApplyAutoWarpRange(SessionDocument.AudioSettings audio, AudioSource source, AutoWarpResult found, double fromSeconds, double toSeconds)
        {
            int perBar = Math.Max(1, found.BeatsPerBar);
            double rate = source.SampleRate, from = Math.Max(0, fromSeconds) * rate, to = Math.Min(source.Length, toSeconds * rate);
            var old = WarpMath.Effective(audio.Markers, source.Length, rate, audio.Bpm);
            var result = old.Where(m => m.Sample < from - WarpMath.MinSamples || m.Sample > to + WarpMath.MinSamples).ToList();
            if (from > WarpMath.MinSamples) result.Add((from, WarpMath.BeatAt(old, from)));
            if (to < source.Length - WarpMath.MinSamples) result.Add((to, WarpMath.BeatAt(old, to)));
            // The beats found inside the range, numbered on from the bar line of the clip's own grid nearest the first of them.
            var inside = new List<(double Sample, double Beat)>();
            if (found.Found)
            {
                double beat0 = Math.Round(WarpMath.BeatAt(old, found.FirstDownbeat) / perBar) * perBar;
                if (found.Steady && found.Beats.Length > 0)
                    for (int j = 0; j < found.Beats.Length; j += perBar) inside.Add((found.Beats[j], beat0 + j));
                else
                    foreach (var (sample, beat) in found.Markers) inside.Add((sample, beat0 + beat));
            }
            // Only those that keep the markers in order and apart (the pins and the clip's own markers win).
            foreach (var m in inside.Where(m => m.Sample > from + WarpMath.MinSamples && m.Sample < to - WarpMath.MinSamples).OrderBy(m => m.Sample))
            {
                var trial = result.Append(m).OrderBy(x => x.Beat).ToList();
                if (WarpMath.Validate(trial) == null) result = trial;
            }
            audio.Markers = result.OrderBy(x => x.Beat).ToList();
        }

        /// <summary>Auto-warps a clip (undoable as one step); the tempo it found, or 0 and a reason in <paramref name="error"/>.</summary>
        public double AutoWarp(IPattern pattern, double fromSeconds, out string error, bool toGrid = false)
        {
            error = EditAudio(pattern, new AudioClipEdit { AutoWarp = true, ToGrid = toGrid, FromSeconds = fromSeconds }, null, toGrid ? "Auto-warp to grid" : "Auto-warp");
            return error == null ? AudioOf(pattern)?.Bpm ?? 0 : 0;
        }

        /// <summary>
        /// Auto-warps several audio clips as one undo step (the command the clip menus will call): each by the beats, or to grid when <paramref name="toGrid"/>. <paramref name="options"/>
        /// (grid, sensitivity, reach) are saved with every clip first when given. A clip with no beats found is named in the answer and left as it was; the others are done.
        /// Returns why nothing was done, or null (then <see cref="LastGridReports"/> has what "to grid" did to each).
        /// </summary>
        internal string AutoWarpClips(IReadOnlyList<IPattern> clips, bool toGrid, AudioClipEdit options = null, string label = null, double? fromSeconds = null, double? toSeconds = null)
        {
            if (clips == null || clips.Count == 0) return "No clip.";
            var reports = new List<(IPattern Clip, GridWarpReport Report)>();
            var skipped = new List<string>();
            UndoStack.BeginActionGroup();
            try
            {
                UndoStack.Label(label ?? ((toSeconds != null ? "Auto-warp selection" : "Auto-warp") + (toGrid ? " to grid" : "")));
                foreach (var clip in clips)
                {
                    var edit = new AudioClipEdit { AutoWarp = true, ToGrid = toGrid, AutoGrid = options?.AutoGrid, AutoSensitivity = options?.AutoSensitivity, AutoReach = options?.AutoReach, AutoLeaveSustained = options?.AutoLeaveSustained, FromSeconds = fromSeconds, ToSeconds = toSeconds };
                    string why = EditAudio(clip, edit, null);
                    if (why != null) skipped.Add($"{clip?.Name}: {why}");
                    else if (toGrid && LastGridReport != null) reports.Add((clip, LastGridReport));
                }
            }
            finally { UndoStack.EndActionGroup(); }
            LastGridReports = reports;
            return skipped.Count == clips.Count ? string.Join(" ", skipped) : null;
        }

        /// <summary>What "to grid" did to each clip of the last <see cref="AutoWarpClips"/>.</summary>
        internal IReadOnlyList<(IPattern Clip, GridWarpReport Report)> LastGridReports { get; private set; } = Array.Empty<(IPattern, GridWarpReport)>();

        /// <summary>
        /// A file just imported (not yet in a slot) that is longer than eight bars and has a tempo is warped by its beats where they move; false when it isn't
        /// (the preference is off, it is shorter, or no beats were found: it keeps the one tempo the import found).
        /// </summary>
        bool AutoWarpOnImport(SessionDocument.AudioSettings settings, AudioSource source, double beats)
        {
            if (!AutoWarpPolicy.Enabled || beats <= AutoWarpPolicy.LongBars * Math.Max(1, Session.BeatsPerBar)) return false;
            var found = AudioAnalysis.AutoWarp(source, Session.BeatsPerBar);
            bool grid = AutoWarpPolicy.Mode == AutoWarpMode.Grid;
            if (!found.Found && !grid) return false;
            if (found.Found) ApplyAutoWarp(settings, source, found, 0);
            if (grid)
            {
                var onsets = AudioAnalysis.GridOnsets(source, settings.AutoSensitivity, out int gated);
                ApplyToGrid(settings, source, onsets, new GridWarpReport { Gated = gated }, 0, found.Found);
                if (!found.Found && settings.Markers.Count < 2) return false;
            }
            return true;
        }
    }
}
