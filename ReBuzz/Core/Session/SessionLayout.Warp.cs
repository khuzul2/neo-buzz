// Fork (docs/specs/neo-audio.md, WM1): editing an audio clip's warp markers. One entry point, SessionLayout.WarpEdit, for the Audio page (its mouse, keys and
// context menu) and the agent's audio_warp_markers: it works the new markers out with ReBuzz.Core.Clips.WarpMath (pure arithmetic), then hands them to
// EditAudio as one AudioClipEdit, so each edit is one undo step with the same validation as every other change to an audio clip. In its own file so the
// auto-warp work (AW1) merges beside it.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Clips;

namespace ReBuzz.Core.Session
{
    /// <summary>One edit of an audio clip's warp markers, as the page or the agent asks for it. Positions are samples of the audio, or beats of the clip as the page shows them (a reversed clip runs the other way).</summary>
    internal sealed class WarpRequest
    {
        public const string Add = "add", Move = "move", MoveAlong = "move_along", SegmentTempo = "segment_tempo", HalveSegment = "halve_segment",
            DoubleSegment = "double_segment", SetBarOne = "set_bar_one", WarpStraight = "warp_straight", WarpFollow = "warp_follow", WarpLoop = "warp_loop",
            InsertAtTransients = "insert_at_transients", InsertEveryBar = "insert_every_bar", Delete = "delete", RemoveAll = "remove_all",
            Quantize = "quantize", QuantizeStrength = "quantize_strength", List = "list";

        public static readonly string[] Actions =
        {
            Add, Move, MoveAlong, SegmentTempo, HalveSegment, DoubleSegment, SetBarOne, WarpStraight, WarpFollow, WarpLoop, InsertAtTransients, InsertEveryBar, Delete, RemoveAll,
            Quantize, QuantizeStrength, List,
        };

        public string Action;
        /// <summary>The markers acted on: indices into the clip's markers in beat order (the first is 0).</summary>
        public int[] Markers;
        public double? Sample, Beat;
        public double? FromSample, ToSample, FromBeat, ToBeat;
        /// <summary>move: how far (beats), or where to (one marker).</summary>
        public double? DeltaBeats;
        /// <summary>segment_tempo: the tempo. warp_straight: the tempo of the audio from there, else it is found.</summary>
        public double? Bpm;
        /// <summary>move_along: the new sample, or how far (samples).</summary>
        public double? DeltaSamples;
        /// <summary>warp_loop: how many bars (default: the nearest of 1, 2, 4, 8, 16).</summary>
        public int? Bars;
        /// <summary>quantize: percent 0 to 100 (default: the song's, <see cref="SessionDocument.QuantizeStrength"/>). quantize_strength: the value to keep.</summary>
        public double? Strength;
        /// <summary>quantize: the grid in beats (default a sixteenth of a bar's beat: 0.25).</summary>
        public double? GridBeats;

        public string Label => Action switch
        {
            Add => "Add warp marker", Move => "Move warp markers", MoveAlong => "Move warp marker along the audio", SegmentTempo => "Segment tempo",
            HalveSegment => "Halve segment tempo", DoubleSegment => "Double segment tempo", SetBarOne => "Set bar 1 here", WarpStraight => "Warp from here, straight", WarpFollow => "Warp from here",
            WarpLoop => "Warp as a loop", InsertAtTransients => "Insert markers at the transients", InsertEveryBar => "Insert a marker on every bar",
            Delete => "Delete warp markers", RemoveAll => "Remove all warp markers", Quantize => "Quantize warp markers", _ => "Warp markers",
        };
    }

    internal sealed partial class SessionLayout
    {
        /// <summary>Quantize's strength for the song, 0 to 100 (kept with the song).</summary>
        public int QuantizeStrength => Document?.QuantizeStrength ?? 100;

        public void SetQuantizeStrength(int percent)
        {
            var doc = Ensure();
            percent = Math.Clamp(percent, 0, 100);
            if (doc.QuantizeStrength == percent) return;
            doc.QuantizeStrength = percent;
            ContentChanged();
        }

        /// <summary>The clip's markers as they play: its own, or the two ends of its straight map; null if it isn't an audio clip.</summary>
        internal List<(double Sample, double Beat)> WarpMarkersOf(IClip clip)
        {
            var audio = AudioOf(clip);
            var source = audio == null ? null : AudioSourceOf(clip);
            return source == null ? null : WarpMath.Effective(audio.Markers, source.Length, source.SampleRate, audio.Bpm);
        }

        /// <summary>The tempo of the audio in each segment between the clip's markers (the last marker has none), beats of the clip per second of audio.</summary>
        internal List<double> SegmentTempos(IClip clip)
        {
            var m = WarpMarkersOf(clip);
            var source = AudioSourceOf(clip);
            return m == null ? new() : Enumerable.Range(0, m.Count - 1).Select(i => WarpMath.SegmentTempo(m, i, source.SampleRate)).ToList();
        }

        /// <summary>
        /// Edits an audio clip's warp markers (undoable, one step). Returns why it can't be made, or null; <paramref name="result"/> says what it made (which markers
        /// are the new ones, how many). Nothing changes when it can't, or when it would change nothing.
        /// </summary>
        internal string WarpEdit(IClip clip, WarpRequest request, ClipEditor editor, out WarpResult result, string label = null)
        {
            result = null;
            if (clip?.Machine == null || request == null) return "No clip.";
            var audio = AudioOf(clip);
            if (audio == null) return $"\"{clip.Name}\" of {clip.Machine.Name} is not an audio clip (audio_import makes one).";
            var source = AudioSourceOf(clip);
            if (source == null) return "The audio of this clip is missing.";
            if (audio.Warp == "Off" && request.Action != WarpRequest.QuantizeStrength && request.Action != WarpRequest.List)
                return "Warp is off for this clip: turn a warp mode on (audio_clip_set warp) before editing warp markers.";
            double rate = source.SampleRate, length = source.Length;
            var m = WarpMarkersOf(clip);
            int bpb = Math.Max(1, Session?.BeatsPerBar ?? 4);
            double baseBeat = WarpMath.BeatAt(m, length);
            bool reverse = audio.Reverse;

            double? SampleOf(double? sample, double? beat) => sample ?? (beat is double b ? WarpMath.SampleAt(m, reverse ? baseBeat - b : b) : null);
            double? at = SampleOf(request.Sample, request.Beat);
            double? from = SampleOf(request.FromSample, request.FromBeat), to = SampleOf(request.ToSample, request.ToBeat);
            if (from.HasValue && to.HasValue && from > to) (from, to) = (to, from);
            var marks = request.Markers ?? Array.Empty<int>();
            if (marks.Any(i => i < 0 || i >= m.Count)) return $"Marker numbers run from 0 to {m.Count - 1} (the markers in beat order).";

            switch (request.Action)
            {
                case WarpRequest.List:
                    result = new WarpResult { Markers = m };
                    return null;
                case WarpRequest.QuantizeStrength:
                    if (request.Strength is not double pct || !double.IsFinite(pct) || pct < 0 || pct > 100) return "strength is a percent, 0 to 100.";
                    SetQuantizeStrength((int)Math.Round(pct));
                    result = new WarpResult { Markers = m };
                    return null;
                case WarpRequest.Add:
                    if (at == null) return "add needs a sample, or a beat.";
                    result = WarpMath.Add(m, at.Value);
                    break;
                case WarpRequest.Move:
                    if (marks.Length == 0) return "move needs markers (their numbers).";
                    double? delta = request.DeltaBeats ?? (marks.Length == 1 && request.Beat is double toBeat ? (reverse ? baseBeat - toBeat : toBeat) - m[marks[0]].Beat : null);
                    if (delta == null) return "move needs delta_beats (how far, in the audio's own beats; a reversed clip runs the other way) or, for one marker, beat (where to).";
                    result = WarpMath.Move(m, marks, delta.Value);
                    break;
                case WarpRequest.MoveAlong:
                    if (marks.Length != 1) return "move_along needs one marker.";
                    double target = request.Sample ?? (request.DeltaSamples is double ds ? m[marks[0]].Sample + ds : double.NaN);
                    if (double.IsNaN(target)) return "move_along needs sample (where the marker goes) or delta_samples.";
                    result = WarpMath.MoveAlong(m, marks[0], target);
                    break;
                case WarpRequest.SegmentTempo:
                case WarpRequest.HalveSegment:
                case WarpRequest.DoubleSegment:
                    if (marks.Length != 1) return $"{request.Action} needs one marker (the segment starts at it).";
                    result = request.Action == WarpRequest.SegmentTempo
                        ? request.Bpm is double segBpm ? WarpMath.SegmentTempoSet(m, marks[0], segBpm, rate) : WarpResult.Fail("segment_tempo needs bpm.")
                        : WarpMath.SegmentTempoScale(m, marks[0], request.Action == WarpRequest.HalveSegment ? 0.5 : 2, rate);
                    break;
                case WarpRequest.SetBarOne:
                    if (reverse) return "Set bar 1 isn't offered for a reversed clip (the first downbeat is the audio's end): turn reverse off first.";
                    if (at == null) return "set_bar_one needs a sample, or a beat.";
                    result = WarpMath.SetBarOne(m, at.Value);
                    break;
                case WarpRequest.WarpStraight:
                {
                    if (reverse) return "Warp from here isn't offered for a reversed clip: turn reverse off first.";
                    if (at == null) return "warp_straight needs a sample, or a beat.";
                    double bpm = request.Bpm ?? 0;
                    if (bpm <= 0)
                    {
                        int first = (int)Math.Clamp(at.Value, 0, length - 1);
                        var tail = TailOf(source, first);
                        bool ok = false;
                        if (tail != null) bpm = AudioAnalysis.Tempo(tail, bpb, out ok).Bpm;
                        if (!ok) return "No tempo found from there: give bpm.";
                    }
                    result = WarpMath.StraightFrom(m, at.Value, bpm, length, rate);
                    break;
                }
                case WarpRequest.WarpFollow:
                {
                    // Auto-warp (AW1) from here: the markers before stay, the beats after are found and followed. The same edit as audio_clip_set(auto_warp, from_seconds).
                    if (reverse) return "Warp from here isn't offered for a reversed clip: turn reverse off first.";
                    if (at == null) return "warp_follow needs a sample, or a beat.";
                    string why = EditAudio(clip, new AudioClipEdit { AutoWarp = true, FromSeconds = at.Value / rate }, editor, label ?? request.Label);
                    if (why != null) return why;
                    var now = WarpMarkersOf(clip);
                    result = new WarpResult { Markers = now, Count = now.Count(x => x.Sample >= at.Value - 1) };
                    for (int i = 0; i < now.Count; i++) if (now[i].Sample >= at.Value - 1) result.Touched.Add(i);
                    return null;
                }
                case WarpRequest.WarpLoop:
                {
                    double a = from ?? double.NaN, b = to ?? double.NaN;
                    if (double.IsNaN(a) || double.IsNaN(b))
                    {
                        // The clip's region (the slot's start and loop end; the whole clip when they are not set).
                        var settings = ClipOf(clip);
                        int end = settings.LoopEnd > 0 && settings.LoopEnd < clip.Length ? settings.LoopEnd : clip.Length;
                        double t0 = settings.Start / (double)Math.Max(1, buzz.TPB), t1 = end / (double)Math.Max(1, buzz.TPB);
                        a = WarpMath.SampleAt(m, reverse ? baseBeat - t1 : t0);
                        b = WarpMath.SampleAt(m, reverse ? baseBeat - t0 : t1);
                        a = Math.Clamp(a, m[0].Sample, m[^1].Sample);
                        b = Math.Clamp(b, m[0].Sample, m[^1].Sample);
                    }
                    if (request.Bars is int bars && !WarpMath.LoopBars.Contains(bars)) return "bars is one of 1, 2, 4, 8, 16.";
                    result = WarpMath.AsLoop(m, a, b, request.Bars, bpb);
                    break;
                }
                case WarpRequest.InsertAtTransients:
                    result = WarpMath.InsertAtTransients(m, TransientsOf(clip), from, to);
                    break;
                case WarpRequest.InsertEveryBar:
                    result = WarpMath.InsertEveryBar(m, bpb, from, to);
                    break;
                case WarpRequest.Delete:
                    if (marks.Length == 0) return "delete needs markers (their numbers).";
                    result = WarpMath.Delete(m, marks);
                    break;
                case WarpRequest.RemoveAll:
                    if ((audio.Markers?.Count ?? 0) < 2) return "The clip is straight already.";
                    result = new WarpResult { Markers = new(), Count = audio.Markers.Count };
                    break;
                case WarpRequest.Quantize:
                {
                    double percent = request.Strength ?? QuantizeStrength;
                    if (!double.IsFinite(percent) || percent < 0 || percent > 100) return "strength is a percent, 0 to 100.";
                    result = WarpMath.Quantize(m, marks, from, to, TransientsOf(clip), request.GridBeats ?? 0.25, percent / 100);
                    break;
                }
                default:
                    return $"No action \"{request.Action}\": one of {string.Join(", ", WarpRequest.Actions)}.";
            }
            if (!result.Ok) return result.Error;
            if (WarpMath.Same(result.Markers, m) && (audio.Markers?.Count ?? 0) >= 2) { result = null; return "Nothing changed."; }
            var edit = new AudioClipEdit { Markers = result.Markers.ToList() };
            return EditAudio(clip, edit, editor, label ?? request.Label);
        }

        /// <summary>The audio from a sample on, as a source of its own (to find its tempo), or null when it is too short.</summary>
        static AudioSource TailOf(AudioSource source, int first)
        {
            int n = source.Length - first;
            if (n < source.SampleRate / 4) return null;
            var l = new float[n];
            Array.Copy(source.Left, first, l, 0, n);
            if (source.IsMono) return new AudioSource(l, null, source.SampleRate);
            var r = new float[n];
            Array.Copy(source.Right, first, r, 0, n);
            return new AudioSource(l, r, source.SampleRate);
        }
    }
}
