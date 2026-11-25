// Fork (docs/specs/audio-to-midi.md, "Slice to new MIDI track (SL1 as built)"): Slice to new MIDI track, layout side. An audio clip is cut into at most 16
// slices (at its transients, its warp markers, or a grid of beats); each slice is cut from the audio as the clip plays it (its gain, its reverse; unwarped)
// into its own wave of the song's wavetable, a new MIDI track gets a Neo Sampler in Kit mode with zone n on slice n (all in choke group 1, so the replay is
// monophonic like the loop), and a MIDI clip of one note per slice, where the source is: the same Session scene, arrangement positions, region and loop.
// The waves, the track, the clip and the settings are one undo step.

using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Agent.Model;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Notes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Core.Session
{
    /// <summary>An audio clip as slicing reads it: the audio as the clip plays it, the clip's warp map, and the part of the clip that is cut.</summary>
    internal sealed class SliceSource
    {
        public string Name { get; init; } = "";
        public IClip Clip { get; init; }
        public AudioAsset Asset { get; init; }
        /// <summary>The audio as the clip plays it: backwards when the clip is reversed.</summary>
        public AudioSource Audio { get; init; }
        /// <summary>Beats of the clip to samples of <see cref="Audio"/> (mirrored with it).</summary>
        public WarpMap Map { get; init; }
        public SessionDocument.AudioSettings Settings { get; init; }
        public double StartBeat { get; init; }
        public double EndBeat { get; init; }
        public double BeatsPerBar { get; init; } = 4;
        /// <summary>Note time units in a beat (ticks per beat times the clip notes' time base): the grid the slices' notes sit on.</summary>
        public double UnitsPerBeat { get; init; }
        /// <summary>What the region is, in words: "the clip's loop", "the whole clip".</summary>
        public string Region { get; init; } = "";
        internal Func<IReadOnlyList<double>> Onsets { get; init; }

        public int Channels => Asset?.Channels > 1 ? 2 : 1;
        public int Rate => Audio?.SampleRate ?? 44100;

        /// <summary>The audio's level (the larger magnitude of the channels) for <paramref name="count"/> samples from <paramref name="start"/>; silence outside it.</summary>
        public float[] Level(long start, int count)
        {
            count = Math.Max(0, count);
            var l = new float[count];
            Audio.CopyExact(0, start, l);
            if (Channels > 1)
            {
                var r = new float[count];
                Audio.CopyExact(1, start, r);
                for (int i = 0; i < count; i++) l[i] = Math.Max(Math.Abs(l[i]), Math.Abs(r[i]));
            }
            else
                for (int i = 0; i < count; i++) l[i] = Math.Abs(l[i]);
            return l;
        }

        /// <summary>The audio's samples, the channels' mean, for <paramref name="count"/> samples from <paramref name="start"/>; silence outside it.</summary>
        public float[] Mono(long start, int count)
        {
            count = Math.Max(0, count);
            var l = new float[count];
            Audio.CopyExact(0, start, l);
            if (Channels > 1)
            {
                var r = new float[count];
                Audio.CopyExact(1, start, r);
                for (int i = 0; i < count; i++) l[i] = 0.5f * (l[i] + r[i]);
            }
            return l;
        }

        /// <summary>The plan of a cut (the transients take a moment on long audio the first time: do it off the UI thread).</summary>
        public SlicePlan Plan(SliceBy by) =>
            SliceMath.Plan(by, Map, Audio.Length, Rate, StartBeat, EndBeat, BeatsPerBar, by == SliceBy.Transients ? Onsets() : Array.Empty<double>(), Level, Mono, UnitsPerBeat);
    }

    /// <summary>One slice as it was put in the song.</summary>
    internal sealed class SliceWave
    {
        /// <summary>The wavetable slot, from 1.</summary>
        public int Slot { get; init; }
        public string Name { get; init; } = "";
        /// <summary>Samples of the audio as the clip plays it.</summary>
        public long Start { get; init; }
        public long Length { get; init; }
        /// <summary>The MIDI note that plays it.</summary>
        public int Key { get; init; }
        /// <summary>Where the note is, in beats of the clip.</summary>
        public double Beat { get; init; }
        public double Beats { get; init; }
    }

    internal sealed class SliceOutcome
    {
        public IMachine Machine { get; init; }
        public IClip Clip { get; init; }
        public string Instrument { get; init; } = "Neo Sampler";
        public List<SliceWave> Slices { get; } = new();
        public int Scene { get; init; } = -1;
        public List<int> Ticks { get; } = new();
        public SliceBy By { get; init; }
        /// <summary>How many slices the choice found before the limit.</summary>
        public int Found { get; init; }
        public bool Capped { get; init; }
    }

    internal sealed partial class SessionLayout
    {
        public const string SliceInstrument = "Neo Sampler";

        /// <summary>The first key of the slices: Neo Sampler's default Kit base (36, General MIDI's kick).</summary>
        public const int SliceKeyBase = 36;

        /// <summary>The source of a slice: an audio clip of the song; null and a reason when it isn't one.</summary>
        internal SliceSource SliceSourceOfClip(IClip clip, out string error)
        {
            var stem = StemSourceOfClip(clip, out error);
            if (stem == null) return null;
            var audio = AudioOf(clip);
            var w = ClipAudioOf(audio);
            if (w == null)
            {
                error = $"\"{clip.Name}\" has no audio in the song.";
                return null;
            }
            var source = w.Source;
            var map = MapOf(source, audio);
            AudioSource play = source;
            Func<IReadOnlyList<double>> onsets;
            if (audio.Reverse)
            {
                map = map.Mirrored(source.Length, map.BeatAt(source.Length));
                play = w.Reversed ??= source.Reversed();
                onsets = () => w.ReversedOnsets ??= AudioAnalysis.Onsets(play);
            }
            else
                onsets = () => w.Onsets ??= AudioAnalysis.Onsets(play);

            // The region: what the clip plays. With the loop on, from the earlier of the start marker and the loop start; to the loop end (the clip's end for 0).
            var settings = ClipOf(clip);
            int tpb = Math.Max(1, buzz.TPB);
            int end = settings.LoopEnd > 0 ? Math.Min(settings.LoopEnd, clip.Length) : clip.Length;
            int start = Math.Clamp(settings.Loop ? Math.Min(settings.Start, settings.LoopStart) : settings.Start, 0, Math.Max(0, end - 1));
            string region = settings.LoopEnd > 0 || start > 0 ? (settings.Loop ? "the clip's loop" : "the clip's region") : "the whole clip";
            if (end <= start) { start = 0; end = clip.Length; region = "the whole clip"; }
            return new SliceSource
            {
                Name = clip.Name, Clip = clip, Asset = stem.Asset, Audio = play, Map = map, Settings = audio.Clone(),
                StartBeat = start / (double)tpb, EndBeat = end / (double)tpb, BeatsPerBar = Math.Max(1, Session?.BeatsPerBar ?? 4), UnitsPerBeat = tpb * (double)ClipNotes.TimeBase,
                Region = region, Onsets = onsets,
            };
        }

        /// <summary>Plans a cut on a worker (finding the transients of long audio takes a moment the first time).</summary>
        internal Task<SlicePlan> PlanSlicesAsync(SliceSource source, SliceBy by, CancellationToken cancel) =>
            Task.Run(() => source.Plan(by), cancel);

        /// <summary>Why a cut into <paramref name="count"/> slices can't be put in the song (no Neo Sampler, no free wave slots), or null.</summary>
        internal string SliceBlocker(int count)
        {
            var dll = buzz.MachineDLLs.Values.FirstOrDefault(d => d?.Info?.Name == SliceInstrument);
            if (dll == null) return $"{SliceInstrument} isn't installed, so {Product.Name} can't make the MIDI track.";
            if (dll.IsMissing || dll.IsCrashed) return $"{SliceInstrument} isn't usable ({(dll.IsMissing ? "missing" : "crashed")}).";
            int free = FreeWaveSlots(count + 1).Count;
            if (free < count) return $"The wavetable has {free} free slot{(free == 1 ? "" : "s")}; the slices need {count}. Clear some waves first.";
            return null;
        }

        /// <summary>The first <paramref name="count"/> empty wave slots (0-based), fewer when there aren't that many.</summary>
        List<int> FreeWaveSlots(int count)
        {
            var list = new List<int>();
            var waves = song.Wavetable?.Waves;
            for (int i = 0; waves != null && i < waves.Count && i < 200 && list.Count < count; i++)
                if (waves[i] == null || waves[i].Layers.Count == 0) list.Add(i);
            return list;
        }

        // ------------------------------------------------------------------ cutting

        const double FadeInMs = 1, FadeOutMs = 2;

        /// <summary>
        /// A slice's audio: the clip's gain applied, a fade in of 1 ms (none when it starts at a zero crossing: the sample before is silence or the
        /// signal crosses zero there) and a fade out of 2 ms. Mono for a mono clip, else left and right.
        /// </summary>
        internal static (float[] Left, float[] Right) CutSlice(SliceSource source, SliceCut cut)
        {
            int n = (int)Math.Min(int.MaxValue / 4, cut.Length);
            var left = new float[n];
            source.Audio.CopyExact(0, cut.Start, left);
            float[] right = null;
            if (source.Channels > 1)
            {
                right = new float[n];
                source.Audio.CopyExact(1, cut.Start, right);
            }
            float gain = source.Settings.Gain;
            var channels = right == null ? new[] { left } : new[] { left, right };
            // Whether the slice begins on a zero crossing, in every channel.
            bool zero = true;
            for (int c = 0; c < channels.Length && n > 0; c++)
            {
                var before = new float[1];
                float first = channels[c][0];
                if (cut.Start > 0) source.Audio.CopyExact(c, cut.Start - 1, before);
                else before[0] = first;                          // the audio's own start: no sample before it to cross zero with
                if (!(Math.Abs(first) <= 1e-4f || before[0] * first <= 0f)) zero = false;
            }
            int rate = source.Rate;
            int fin = zero ? 0 : (int)Math.Round(FadeInMs * rate / 1000.0), fout = (int)Math.Round(FadeOutMs * rate / 1000.0);
            if (fin + fout > n) { double k = n / (double)Math.Max(1, fin + fout); fin = (int)(fin * k); fout = (int)(fout * k); }
            foreach (var x in channels)
            {
                for (int i = 0; i < n; i++) x[i] *= gain;
                for (int i = 0; i < fin; i++) x[i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * (i + 0.5) / fin));
                for (int i = 0; i < fout; i++) x[n - 1 - i] *= (float)(0.5 - 0.5 * Math.Cos(Math.PI * (i + 0.5) / fout));
            }
            return (left, right);
        }

        /// <summary>A wave of the wavetable from a slice's audio (float; mono or stereo), as the song's own thread does it.</summary>
        void FillSliceWave(int index, string name, float[] left, float[] right, int rate)
        {
            var table = song.Wavetable;
            int n = left.Length;
            lock (ReBuzzCore.AudioLock)
            {
                table.AllocateWave(index, "", name, n, WaveFormat.Float32, right != null, Pitch.ToBuzz(48), false, false);
                var layer = table.Waves[index]?.Layers.FirstOrDefault() ?? throw new InvalidOperationException("Couldn't make a wave.");
                layer.SampleRate = rate;
                layer.LoopStart = 0;
                layer.LoopEnd = n;
                layer.SetDataAsFloat(left, 0, 1, 0, 0, n);
                if (right != null) layer.SetDataAsFloat(right, 0, 1, 1, 0, n);
            }
            table.Waves[index]?.Layers.FirstOrDefault()?.InvalidateData();
        }

        // ------------------------------------------------------------------ the song

        /// <summary>
        /// Puts a cut in the song: the slices as waves in the first free slots, a new MIDI track named "‹clip› slices" with a Neo Sampler in Kit mode (zone n
        /// plays slice n on key 36 + n - 1, all in choke group 1), and a clip of one note per slice where the source is. One undo step. Null and a reason when it can't.
        /// </summary>
        internal SliceOutcome ApplySlices(SliceSource source, SlicePlan plan, out string error)
        {
            error = null;
            if (source?.Clip == null || plan == null)
            {
                error = "There is nothing to put in the song.";
                return null;
            }
            if (!plan.Ok)
            {
                error = plan.Why ?? "There is nothing to slice.";
                return null;
            }
            error = SliceBlocker(plan.Slices.Count);
            if (error != null) return null;
            var sourceClip = source.Clip;
            var audio = AudioOf(sourceClip);
            if (audio == null)
            {
                error = $"\"{sourceClip.Name}\" isn't an audio clip any more.";
                return null;
            }

            int tpb = Math.Max(1, buzz.TPB);
            int clipTime = sourceClip.Length * ClipNotes.TimeBase;
            var slots = FreeWaveSlots(plan.Slices.Count);
            var cuts = plan.Slices.Select(s => CutSlice(source, s)).ToList();
            string baseName = source.Name.Length > 24 ? source.Name[..24] : source.Name;
            var names = Enumerable.Range(1, cuts.Count).Select(i => $"{baseName} {i:00}").ToList();
            long bytes = cuts.Sum(c => (c.Left.Length + (c.Right?.Length ?? 0)) * 4L);
            int rate = source.Rate;

            var notes = new List<ClipNote>();
            var waves = new List<SliceWave>();
            for (int i = 0; i < plan.Slices.Count; i++)
            {
                var s = plan.Slices[i];
                int time = (int)Math.Round(s.Beat * tpb * ClipNotes.TimeBase);
                int length = (int)Math.Round(s.Beats * tpb * ClipNotes.TimeBase);
                int key = SliceKeyBase + i;
                waves.Add(new SliceWave { Slot = slots[i] + 1, Name = names[i], Start = s.Start, Length = s.Length, Key = key, Beat = s.Beat, Beats = s.Beats });
                if (time < 0 || time >= clipTime) continue;
                notes.Add(new ClipNote(time, Math.Max(ClipNotes.MinDuration, Math.Min(length, clipTime - time)), key, 100));
            }
            SourcePlace(sourceClip, out int sourceScene, out var sourceEvents, out var sourceSettings);

            UndoStack.BeginActionGroup();
            try
            {
                UndoStack.Do(new DelegateAction(
                    () => { for (int i = 0; i < cuts.Count; i++) FillSliceWave(slots[i], names[i], cuts[i].Left, cuts[i].Right, rate); },
                    () => { foreach (int slot in slots) ClearWave(slot); },
                    "Slice to new MIDI track: waves", bytes));
                string name = $"{source.Name} slices";
                var machine = NewInstrumentTrack(SliceInstrument, name) ?? throw new InvalidOperationException("Couldn't make the MIDI track.");
                int track = FirstTrackOf(machine);
                if (track < 0) throw new InvalidOperationException("Couldn't make the MIDI track.");
                SetSamplerZones(machine, slots, audio);
                var added = notes.Select(ToEvent).ToArray();
                var (clip, ticks) = PutMidiClip(sourceClip, machine, track, name, added, sourceScene, sourceEvents, sourceSettings);
                var outcome = new SliceOutcome { Machine = machine, Clip = clip, Scene = sourceScene, By = plan.By, Found = plan.Found, Capped = plan.Capped };
                outcome.Slices.AddRange(waves);
                outcome.Ticks.AddRange(ticks);
                UndoStack.EndActionGroup();
                buzz.SetModifiedFlag();
                return outcome;
            }
            catch (Exception e)
            {
                UndoStack.EndActionGroup();
                try { UndoStack.Undo(); } catch (Exception) { }
                error = e.Message;
                return null;
            }
        }

        /// <summary>
        /// Neo Sampler in Kit mode with zone n on slot slots[n] and all zones in choke group 1; velocity doesn't change the level (so a slice plays as it was cut),
        /// the clip's transpose and detune retune the zones (the slices are not warped: they are repitched). Looked up by name when it runs, so redo
        /// finds the machine made again.
        /// </summary>
        void SetSamplerZones(IMachine machine, IReadOnlyList<int> slots, SessionDocument.AudioSettings audio)
        {
            string machineName = machine.Name;
            var values = new List<(string Name, int Value)> { ("Mode", 1), ("Kit base", SliceKeyBase), ("Vel → amp", 0) };
            for (int z = 1; z <= slots.Count; z++)
            {
                values.Add(($"Z{z} wave", slots[z - 1] + 1));
                values.Add(($"Z{z} choke", 1));
                if (audio.Transpose != 0) values.Add(($"Z{z} tune", Math.Clamp(48 + audio.Transpose, 0, 96)));
                if (audio.Detune != 0) values.Add(($"Z{z} fine", Math.Clamp(1000 + audio.Detune * 10, 0, 2000)));
            }
            var targets = new List<(string Name, int Old, int New)>();
            var global = machine.ParameterGroups.FirstOrDefault(g => g.Type == ParameterGroupType.Global)
                ?? throw new InvalidOperationException($"{SliceInstrument} has no parameters.");
            foreach (var (n, v) in values)
            {
                var p = global.Parameters.FirstOrDefault(q => q.Name == n) ?? throw new InvalidOperationException($"{SliceInstrument} has no \"{n}\".");
                targets.Add((n, p.GetValue(0), v));
            }
            void Set(bool forward)
            {
                var m = song.Machines.FirstOrDefault(q => q.Name == machineName);
                var g = m?.ParameterGroups.FirstOrDefault(q => q.Type == ParameterGroupType.Global);
                if (g == null) return;
                foreach (var (n, old, value) in targets)
                    g.Parameters.FirstOrDefault(q => q.Name == n)?.SetValue(1 << 16, forward ? value : old);
            }
            UndoStack.Do(new DelegateAction(() => Set(true), () => Set(false), "Slice to new MIDI track: sampler"));
        }
    }
}
