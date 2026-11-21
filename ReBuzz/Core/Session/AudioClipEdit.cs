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
            if (DetectTempo && a.Warp == "Off" && Warp == null) a.Warp = SessionDocument.AudioSettings.DefaultWarp;
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
