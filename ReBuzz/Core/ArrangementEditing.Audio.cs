// Fork (docs/specs/neo-audio.md, NA2): what the arrangement does with audio clips, as the studio's mouse and keys and the agent's tools edit them.
// An audio clip is a clip of a Neo Audio track whose clip is an audio clip (SessionLayout.IsAudioClip). Every arrangement audio clip has a clip of its
// own and its own settings (warp, gain, fades, reverse); copies share the audio asset only. Each edit is one undo step (the clip copies, the settings and
// the events go in one group). Nothing here runs on the audio thread.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;

namespace ReBuzz.Core
{
    internal sealed partial class ClipEditor
    {
        SessionLayout Layout => core.SessionLayout;

        double BeatsOf(int ticks) => ticks / (double)Math.Max(1, core.TPB);

        /// <summary>Whether the clip is an audio clip (of a Neo Audio machine, with audio settings).</summary>
        public bool IsAudio(IClip clip) => clip?.Machine != null && Layout?.IsAudioClip(clip) == true;

        /// <summary>The audio settings of a clip's clip (a copy), or null.</summary>
        public SessionDocument.AudioSettings AudioOf(IClip clip) => IsAudio(clip) ? Layout.AudioOf(clip) : null;

        /// <summary>
        /// A clip of its own for an audio clip, on <paramref name="target"/> (the source's machine, or another Neo Audio machine): the
        /// settings are copied, the audio asset is the same one. Part of the caller's undo group. Null when it can't be made.
        /// </summary>
        internal IClip CloneAudioClip(IClip source, IMachine target)
        {
            var layout = Layout;
            if (layout == null || source?.Machine == null || target == null || !SessionLayout.IsClipPlayer(target)) return null;
            string name = SessionLayout.CopyName(target, source.Name);
            var create = new SessionCreateClipAction(layout, target, name, source.Length);
            core.SongCore.ActionStack.Do(create);
            var copy = create.Created;
            if (copy == null) return null;
            layout.SetClipSettings(copy, layout.ClipOf(source));
            int color = ClipColors.Get(source);
            if (color >= 0) core.SongCore.ActionStack.Do(new ClipColorAction(layout, copy, color));
            return copy;
        }

        /// <summary>Changes a clip's audio settings that don't change its timing (one undo step on its own, or part of the group it is in).</summary>
        void SetAudioFields(IClip clip, Action<SessionDocument.AudioSettings> change)
        {
            var layout = Layout;
            var settings = layout?.ClipOf(clip);
            if (settings?.Audio == null) return;
            change(settings.Audio);
            layout.SetClipSettings(clip, settings);
        }

        // ------------------------------------------------------------------ crossfades

        /// <summary>
        /// Where a clip was cut to make room for an audio clip, the overlap becomes a crossfade: the earlier clip's audio goes on under the later
        /// one for the overlap (its Tail) and the later one fades in over it, equal power (FadeIn with CrossIn). Then every lane touched is checked.
        /// </summary>
        void ApplyCrossfades(Plan plan)
        {
            if (Layout == null) return;
            if (plan.Writer != null)
                foreach (var (clip, earlier, ticks) in plan.Cuts)
                {
                    if (ticks <= 0) continue;
                    double beats = BeatsOf(ticks);
                    var (first, second) = earlier ? (clip, plan.Writer) : (plan.Writer, clip);
                    SetAudioFields(first, a => a.Tail = beats);
                    SetAudioFields(second, a => { a.FadeIn = beats; a.CrossIn = true; });
                }
            foreach (var lane in plan.Lanes) FixCrossfades(lane);
        }

        /// <summary>A tail needs a clip starting where its clip ends, and a crossfading fade in needs a tail before it; any other is cleared.</summary>
        void FixCrossfades(ISequence lane)
        {
            var layout = Layout;
            var items = Read(lane).Where(i => IsAudio(i.Clip)).OrderBy(i => i.Time).ToList();
            foreach (var i in items)
            {
                var a = layout.AudioOf(i.Clip);
                if (a == null) continue;
                if (a.Tail > 0 && !items.Any(n => n.Time == i.End && !ReferenceEquals(n.Clip, i.Clip)))
                    SetAudioFields(i.Clip, x => x.Tail = 0);
            }
            foreach (var i in items)
            {
                var a = layout.AudioOf(i.Clip);
                if (a == null || !a.CrossIn) continue;
                bool ok = items.Any(p => p.End == i.Time && !ReferenceEquals(p.Clip, i.Clip) && (layout.AudioOf(p.Clip)?.Tail ?? 0) > 0);
                if (!ok) SetAudioFields(i.Clip, x => x.CrossIn = false);
            }
        }

        /// <summary>The audio clip that ends where this one starts, if it has a tail (the other half of this clip's crossfade).</summary>
        ClipItem CrossfadePartner(ClipItem item)
        {
            if (item == null) return null;
            return Read(item.Sequence).FirstOrDefault(p => p.End == item.Time && IsAudio(p.Clip) && !ReferenceEquals(p.Clip, item.Clip) && (AudioOf(p.Clip)?.Tail ?? 0) > 0);
        }

        // ------------------------------------------------------------------ fades, gain, crossfade, reverse

        /// <summary>The clip's fades (ticks; null leaves one as it is). A fade in that is half of a crossfade moves the other half with it.</summary>
        public string Fade(ClipItem item, int? fadeIn, int? fadeOut)
        {
            if (!IsAudio(item?.Clip)) return "Only audio clips have fades.";
            int length = Math.Max(1, item.Spec.Length);
            Group(() =>
            {
                if (fadeIn is int fi)
                {
                    int ticks = Math.Clamp(fi, 0, length);
                    var partner = CrossfadePartner(item);
                    SetAudioFields(item.Clip, a => { a.FadeIn = BeatsOf(ticks); if (ticks == 0) a.CrossIn = false; });
                    if (partner != null && AudioOf(item.Clip).CrossIn) SetAudioFields(partner.Clip, a => a.Tail = BeatsOf(ticks));
                }
                if (fadeOut is int fo)
                    SetAudioFields(item.Clip, a => a.FadeOut = BeatsOf(Math.Clamp(fo, 0, length)));
                FixCrossfades(item.Sequence);
            });
            return null;
        }

        /// <summary>The clip's gain in dB (-70 to 24).</summary>
        public string SetGain(ClipItem item, double db)
        {
            if (!IsAudio(item?.Clip)) return "Only audio clips have a gain in dB.";
            if (!double.IsFinite(db)) return "That isn't a gain.";
            SetAudioFields(item.Clip, a => a.GainDb = Math.Clamp(db, -70, 24));
            return null;
        }

        /// <summary>Crossfades the clip with the one that starts where it ends, over <paramref name="ticks"/> (0 removes the crossfade).</summary>
        public string Crossfade(ClipItem first, int ticks)
        {
            if (!IsAudio(first?.Clip)) return "Only audio clips crossfade.";
            var second = Read(first.Sequence).FirstOrDefault(i => i.Time == first.End && IsAudio(i.Clip) && !ReferenceEquals(i.Clip, first.Clip));
            if (second == null) return "No audio clip starts where this one ends: a crossfade needs two clips that touch.";
            ticks = Math.Clamp(ticks, 0, Math.Min(first.Spec.Length, second.Spec.Length));
            double beats = BeatsOf(ticks);
            Group(() =>
            {
                SetAudioFields(first.Clip, a => a.Tail = beats);
                SetAudioFields(second.Clip, a => { a.FadeIn = beats; a.CrossIn = ticks > 0; });
            });
            return null;
        }

        /// <summary>Every clip of the arrangement that plays a clip (one, for an audio clip: each has a clip of its own; an older song may share).</summary>
        public List<ClipItem> ClipsOf(IClip clip) =>
            clip?.Machine == null ? new List<ClipItem>()
                : Song.Sequences.Where(q => q.Machine == clip.Machine).SelectMany(q => Read(q)).Where(i => ReferenceEquals(i.Clip, clip)).ToList();

        /// <summary>
        /// Reverse: the audio plays backwards. The part of the audio the clip plays stays the same (mirrored in the clip), so reversing
        /// a trimmed clip reverses what it plays.
        /// </summary>
        public string Reverse(ClipItem item)
        {
            if (!IsAudio(item?.Clip)) return "Only audio clips reverse.";
            Group(() =>
            {
                int p = Math.Max(1, item.Spec.ClipLength);
                MirrorSettings(item.Clip);
                var plan = new Plan(this);
                var uses = ClipsOf(item.Clip);
                if (!uses.Contains(item)) uses.Add(item);
                foreach (var use in uses)
                {
                    var s = use.Spec;
                    int offset = Math.Clamp(s.Start, 0, s.RegionEnd - 1);
                    int first = Math.Min(Math.Max(1, s.Length), s.RegionEnd - offset);
                    int newStart = Math.Clamp(p - (offset + first), 0, Math.Max(0, p - 1));
                    int newLoopStart = Math.Clamp(p - s.RegionEnd, 0, Math.Max(0, p - 1));
                    int newLoopEnd = s.LoopStart == 0 ? 0 : Math.Clamp(p - s.LoopStart, 1, p);
                    plan.Remove(use);
                    plan.Write(use.Sequence, s with { Start = newStart, LoopStart = newLoopStart, LoopEnd = newLoopEnd });
                }
                Commit(plan);
            });
            return null;
        }

        /// <summary>Reverse for a clip by name (the agent's audio_clip_set): its clips in the arrangement are mirrored too; a clip with none has its settings mirrored.</summary>
        public string ReverseClip(IClip clip)
        {
            if (!IsAudio(clip)) return "Only audio clips reverse.";
            var uses = ClipsOf(clip);
            if (uses.Count > 0) return Reverse(uses[0]);
            Group(() => MirrorSettings(clip));
            return null;
        }

        /// <summary>Switches the clip's reverse and mirrors its start and loop markers (ticks into the clip) so the same audio is meant.</summary>
        void MirrorSettings(IClip clip)
        {
            int p = Math.Max(1, clip.Length);
            var settings = Layout.ClipOf(clip);
            settings.Audio.Reverse = !settings.Audio.Reverse;
            if (settings.Start != 0) settings.Start = Math.Clamp(p - settings.Start, 0, Math.Max(0, p - 1));
            if (settings.LoopStart != 0 || settings.LoopEnd != 0)
            {
                int end = settings.LoopEnd > 0 ? Math.Min(settings.LoopEnd, p) : p;
                int start = settings.LoopStart;
                settings.LoopStart = Math.Clamp(p - end, 0, Math.Max(0, p - 1));
                settings.LoopEnd = start == 0 ? 0 : Math.Clamp(p - start, 1, p);
            }
            Layout.SetClipSettings(clip, settings);
        }

        // ------------------------------------------------------------------ stretch

        /// <summary>
        /// Alt + right edge: the audio is warped to fill the clip's new length (it ends at <paramref name="newEnd"/>): the whole audio's tempo,
        /// markers and the clip's length and regions scale by the same factor, so what the clip plays now plays over the new length and
        /// stays in time with the song. Not for a clip with warp off (it plays at its own speed) or with its loop on (its edge repeats the loop).
        /// </summary>
        public string Stretch(ClipItem item, int newEnd)
        {
            if (!IsAudio(item?.Clip)) return "Only audio clips stretch.";
            var s = item.Spec;
            var audio = AudioOf(item.Clip);
            if (audio.Warp == "Off") return "Warp is off, so the clip plays at its own speed: turn warp on (Beats, Repitch, Complex or Pro) to stretch it.";
            if (s.Loop) return "Switch the loop off to stretch the clip (a looped clip's edge repeats the loop).";
            int newLength = Math.Max(1, newEnd - s.Time);
            if (newLength == s.Length) return null;
            string result = null;
            Group(() =>
            {
                var clip = item.Clip;
                if (Uses(clip) > 1)
                {
                    clip = CloneAudioClip(clip, clip.Machine);
                    if (clip == null) { result = "The clip couldn't be copied."; return; }
                }
                int oldClipLength = Math.Max(1, clip.Length);
                double f = newLength / (double)Math.Max(1, s.Length);
                int newClipLength = Math.Clamp((int)Math.Round(oldClipLength * f), 1, 1 << 20);
                double g = newClipLength / (double)oldClipLength;
                var settings = Layout.ClipOf(clip);
                var a = settings.Audio;
                double bpm = a.Bpm * g;
                if (!(bpm >= 1 && bpm <= 999)) { result = "That is too far from the audio's own speed: stretch it by less."; return; }
                a.Bpm = bpm;
                a.Tail *= g;
                a.Markers = a.Markers.Select(m => (m.Sample, m.Beat * g)).ToList();
                settings.Start = (int)Math.Round(settings.Start * g);
                settings.LoopStart = (int)Math.Round(settings.LoopStart * g);
                if (settings.LoopEnd > 0) settings.LoopEnd = Math.Max(1, (int)Math.Round(settings.LoopEnd * g));
                var spec = s with
                {
                    Clip = clip,
                    Length = newLength,
                    Start = (int)Math.Round(s.Start * g),
                    LoopStart = (int)Math.Round(s.LoopStart * g),
                    LoopEnd = s.LoopEnd > 0 ? Math.Max(1, (int)Math.Round(s.LoopEnd * g)) : 0,
                };
                newClipLength = Math.Max(newClipLength, spec.Start + newLength);
                Layout.SetAudio(clip, a);                       // the audio's tempo, and so the clip's length (set again below to the exact factor)
                Layout.SetClipSettings(clip, settings);
                if (clip.Length != newClipLength)
                    core.SongCore.ActionStack.Do(new SessionClipLengthAction(Layout, clip, newClipLength));
                var plan = new Plan(this) { Writer = clip };
                plan.Remove(item);
                if (newLength > s.Length) plan.Overwrite(item.Sequence, s.End, s.Time + newLength);
                plan.Write(item.Sequence, spec);
                Commit(plan);
            });
            return result;
        }

        // ------------------------------------------------------------------ split

        string SplitAudio(ClipItem item, int at)
        {
            string result = null;
            Group(() =>
            {
                var s = item.Spec;
                var own = CloneAudioClip(item.Clip, item.Clip.Machine);
                if (own == null) { result = "The clip couldn't be copied."; return; }
                var plan = new Plan(this);
                plan.Remove(item);
                plan.Write(item.Sequence, s.CutRight(at));
                plan.Write(item.Sequence, s.CutLeft(at) with { Clip = own }, newId: true);
                Commit(plan);
                // The new edge is a plain cut: the left clip keeps its fade in, the right one its fade out and tail.
                SetAudioFields(item.Clip, a => { a.FadeOut = 0; a.Tail = 0; });
                SetAudioFields(own, a => { a.FadeIn = 0; a.CrossIn = false; });
            });
            return result;
        }

        // ------------------------------------------------------------------ consolidate

        /// <summary>
        /// Renders what the audio clips of a lane play between two ticks (warp, loops, fades, crossfades, gain, reverse) into a new audio
        /// asset, and puts one clip of it there instead of them. It is rendered at the song's tempo as it is now (a tempo change inside the
        /// range isn't followed). One undo step. <paramref name="made"/> is the new clip's clip.
        /// </summary>
        public string Consolidate(ISequence lane, int from, int to, out IClip made)
        {
            made = null;
            if (lane?.Machine == null || Layout == null) return "There's no lane here.";
            var items = Read(lane).Where(i => IsAudio(i.Clip) && i.End > from && i.Time < to).OrderBy(i => i.Time).ToList();
            if (items.Count == 0) return "There's no audio clip in the range.";
            from = Math.Max(from, items[0].Time);
            to = Math.Min(to, items.Max(i => i.End));
            if (to <= from) return "There's nothing to consolidate in the range.";
            string result = null;
            IClip clip = null;
            Group(() =>
            {
                var (left, right) = RenderLane(lane, items, from, to);
                string name = items.Count == 1 ? items[0].Clip.Name + " consolidated" : "Consolidated";
                var asset = Layout.AddAsset(new AudioAsset(name, "", null, new AudioSource(left, right, Layout.RenderRate), 2));
                var audio = new SessionDocument.AudioSettings { Asset = asset.Id, Bpm = Math.Clamp(core.BPM, 1, 999) };
                clip = Layout.CreateAudioClip(lane.Machine, name, audio);
                if (clip == null) { result = "The audio couldn't be made into a clip."; return; }
                var spec = new ClipSpec(0, clip, from, Math.Min(to - from, Math.Max(1, clip.Length)), 0, 0, 0, false, 1.0);
                var plan = new Plan(this) { NoCrossfade = true };
                plan.Overwrite(lane, from, to);
                plan.Write(lane, spec, newId: true);
                Commit(plan);
            });
            made = clip;
            return result;
        }

        /// <summary>An offline render must not go through the audio thread's lossy page reads: a streamed source is read exactly into memory for the render (NA1b).</summary>
        internal static AudioClipParams Exact(AudioClipParams p)
        {
            if (!p.Source.IsStreaming && !p.Source.IsReversedView) return p;
            var (l, r) = p.Source.ToArrays();
            var source = new AudioSource(l, p.Source.IsMono ? null : r, p.Source.SampleRate);
            return new AudioClipParams(source, p.Map, p.Mode, p.Slices, p.Gain, p.Semitones) { Fades = p.Fades };
        }

        /// <summary>The lane's audio clips as they play in a range of ticks, offline, at the song's tempo: the engine's mixer and voices, each clip with a voice of its own.</summary>
        (float[] Left, float[] Right) RenderLane(ISequence lane, List<ClipItem> items, int from, int to)
        {
            int rate = Layout.RenderRate, tpb = Math.Max(1, core.TPB);
            double bpm = Math.Max(1, core.BPM);
            double samplesPerTick = rate * 60.0 / (bpm * tpb);
            double bps = 1.0 / (samplesPerTick * tpb);
            long total = Math.Max(1, (long)Math.Round((to - from) * samplesPerTick));
            var left = new float[total];
            var right = new float[total];
            var mixer = new ClipMixer();
            var playbacks = new Dictionary<IClip, ClipPlayback>();
            var parts = items.SelectMany(i => i.Actual.Select(a => (Item: i, Segment: a))).OrderBy(x => x.Segment.Time).ToList();
            int next = 0;
            for (int tick = from; tick < to; tick++)
            {
                long tickStart = (long)Math.Round((tick - from) * samplesPerTick);
                long tickEnd = tick + 1 == to ? total : (long)Math.Round((tick + 1 - from) * samplesPerTick);
                while (next < parts.Count && parts[next].Segment.Time + parts[next].Segment.Length <= tick) next++;
                var part = next < parts.Count && parts[next].Segment.Time <= tick ? parts[next] : default;
                ClipPlayback playback = null;
                if (part.Item != null)
                {
                    var clip = part.Item.Clip;
                    if (!playbacks.TryGetValue(clip, out playback))
                    {
                        var p = Layout.ParamsOfClip(clip);
                        playbacks[clip] = playback = p == null ? null : new ClipPlayback { Params = Exact(p) };
                    }
                }
                for (long at = tickStart; at < tickEnd; at += 256)
                {
                    int n = (int)Math.Min(256, tickEnd - at);
                    double inTick = (at - tickStart) / samplesPerTick;
                    mixer.Begin(n);
                    if (playback != null)
                    {
                        var seg = part.Segment;
                        double beat = (seg.Offset + (tick - seg.Time) + inTick) / tpb;
                        double pos = (tick - part.Item.Time + inTick) / tpb, remaining = (part.Item.End - tick - inTick) / tpb;
                        mixer.Play(playback, beat, bps, rate, n, pos, remaining);
                    }
                    mixer.End(n, bps, rate);
                    Array.Copy(mixer.Left, 0, left, at, n);
                    Array.Copy(mixer.Right, 0, right, at, n);
                }
            }
            return (left, right);
        }
    }
}
