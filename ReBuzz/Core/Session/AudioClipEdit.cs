// Fork (docs/specs/neo-audio.md, NA3): one change to an audio clip, as the clip panel's controls and the agent's audio_clip_set both make it. The fields
// that are null are left as they are. SessionLayout.EditAudio applies it (one undo step); the agent's tool builds one from its arguments and the studio's
// Audio page builds one from the control the user used, so both reach the model by the same calls.

using System;
using System.Collections.Generic;
using System.Linq;
using ReBuzz.Core.Session;

namespace ReBuzz.Core
{
    internal sealed class AudioClipEdit
    {
        /// <summary>A warp mode of <see cref="SessionDocument.AudioSettings.WarpModes"/> (any case).</summary>
        public string Warp;
        /// <summary>What Beats keeps, of <see cref="SessionDocument.AudioSettings.PreserveModes"/> (any case).</summary>
        public string Preserve;
        /// <summary>The audio's tempo (1 to 999). On a clip whose warp is Off and none is given, warp goes to the default (Pro): a tempo is what it was waiting for.</summary>
        public double? TempoBpm;
        /// <summary>Finds the tempo again from the audio (markers are cleared).</summary>
        public bool DetectTempo;
        /// <summary>
        /// Auto-warp (AW1): finds the beats of the audio, follows the tempo where it moves, and replaces the warp markers and the tempo with what it finds, as one step
        /// (<see cref="FromSeconds"/> from later in the audio: the markers before stay). Nothing is changed when no beats are found.
        /// </summary>
        public bool AutoWarp;
        /// <summary>
        /// With <see cref="AutoWarp"/> (AW5): then a warp marker on every transient, pulled to the nearest grid line by the song's Quantize strength (the clip's grid,
        /// sensitivity and reach). When no beats are found the clip's own tempo and markers are the line the transients are put on.
        /// </summary>
        public bool ToGrid;
        /// <summary>The clip's options of Auto-warp to grid (AW5), saved with the clip whether or not it is run now: a grid of <see cref="SessionDocument.AudioSettings.AutoGrids"/>, the sensitivity 0 to 100, the reach 0 to 50 %.</summary>
        public string AutoGrid;
        public int? AutoSensitivity, AutoReach;
        /// <summary>Leave long notes and pauses alone (no markers in quiet or sustained passages); saved with the clip.</summary>
        public bool? AutoLeaveSustained;
        /// <summary>With <see cref="AutoWarp"/> and <see cref="FromSeconds"/>: only the audio from there to this many seconds changes; its two ends are pinned with markers and every marker outside it stays (AW5, "Auto-warp selection").</summary>
        public double? ToSeconds;
        /// <summary>Auto-warp from this many seconds into the audio (default: from its start).</summary>
        public double? FromSeconds;
        public int? Transpose, Detune;
        public double? GainDb;
        /// <summary>The clip's fades in beats (0 none); they play in the arrangement.</summary>
        public double? FadeIn, FadeOut;
        /// <summary>Warp markers (audio sample, clip beat): at least two, or none to clear.</summary>
        public List<(double Sample, double Beat)> Markers;
        public bool? Reverse;
        /// <summary>The pattern's own loop and region, in ticks of the clip (the Session's slot). The arrangement's are the clip's: ClipEditor.</summary>
        public bool? Loop;
        public int? Start, LoopStart, LoopEnd;

        /// <summary>Why the change can't be made, or null; nothing is changed when it can't.</summary>
        public string Check()
        {
            if (Warp != null && Find(SessionDocument.AudioSettings.WarpModes, Warp) == null) return $"warp: one of {string.Join(", ", SessionDocument.AudioSettings.WarpModes)}.";
            if (Preserve != null && Find(SessionDocument.AudioSettings.PreserveModes, Preserve) == null) return $"preserve: one of {string.Join(", ", SessionDocument.AudioSettings.PreserveModes)}.";
            if (AutoGrid != null && !SessionDocument.AudioSettings.AutoGrids.Any(g => string.Equals(g.Name, AutoGrid, StringComparison.OrdinalIgnoreCase)))
                return $"grid: one of {string.Join(", ", SessionDocument.AudioSettings.AutoGrids.Select(g => g.Name))}.";
            if (AutoSensitivity is int sn && (sn < 0 || sn > 100)) return "grid_sensitivity: 0 to 100.";
            if (AutoReach is int rc && (rc < 0 || rc > 50)) return "grid_reach: 0 to 50 (percent of a grid step).";
            if (ToGrid && !AutoWarp) return "to grid needs auto-warp.";
            if (ToSeconds is double to && (!double.IsFinite(to) || to <= (FromSeconds ?? 0))) return "to_seconds: after from_seconds.";
            if (FromSeconds is double from && (!double.IsFinite(from) || from < 0)) return "from_seconds: 0 or more.";
            if (Markers != null && Markers.Count(x => double.IsFinite(x.Sample) && double.IsFinite(x.Beat)) == 1) return "warp_markers needs at least 2 markers (or [] for none).";
            return null;
        }

        static string Find(string[] among, string value) => among.FirstOrDefault(w => string.Equals(w, value, StringComparison.OrdinalIgnoreCase));

        /// <summary>The fields applied to a clip's settings (the pattern's, a copy): its audio and its own loop and region.</summary>
        internal void ApplyTo(SessionDocument.ClipSettings settings)
        {
            var a = settings.Audio;
            if (Warp != null) a.Warp = Find(SessionDocument.AudioSettings.WarpModes, Warp);
            if (Preserve != null) a.Preserve = Find(SessionDocument.AudioSettings.PreserveModes, Preserve);
            if (TempoBpm is double bpm)
            {
                a.Bpm = Math.Clamp(bpm, 1, 999);
                if (a.Warp == "Off" && Warp == null) a.Warp = SessionDocument.AudioSettings.DefaultWarp;
            }
            if ((DetectTempo || AutoWarp) && a.Warp == "Off" && Warp == null) a.Warp = SessionDocument.AudioSettings.DefaultWarp;
            if (AutoGrid != null) a.AutoGrid = SessionDocument.AudioSettings.AutoGrids.First(g => string.Equals(g.Name, AutoGrid, StringComparison.OrdinalIgnoreCase)).Name;
            if (AutoSensitivity is int sens) a.AutoSensitivity = Math.Clamp(sens, 0, 100);
            if (AutoReach is int reach) a.AutoReach = Math.Clamp(reach, 0, 50);
            if (AutoLeaveSustained is bool leave) a.AutoLeaveSustained = leave;
            if (FadeIn is double fi) { a.FadeIn = Math.Max(0, fi); if (fi <= 0) a.CrossIn = false; }
            if (FadeOut is double fo) a.FadeOut = Math.Max(0, fo);
            if (Transpose is int tr) a.Transpose = Math.Clamp(tr, -48, 48);
            if (Detune is int dt) a.Detune = Math.Clamp(dt, -50, 50);
            if (GainDb is double g) a.GainDb = Math.Clamp(g, -70, 24);
            if (Markers != null) a.Markers = Markers.Where(x => double.IsFinite(x.Sample) && double.IsFinite(x.Beat)).ToList();
            if (Loop is bool loop) settings.Loop = loop;
            if (Start is int s) settings.Start = Math.Max(0, s);
            if (LoopStart is int ls) settings.LoopStart = Math.Max(0, ls);
            if (LoopEnd is int le) settings.LoopEnd = Math.Max(0, le);
        }
    }
}
