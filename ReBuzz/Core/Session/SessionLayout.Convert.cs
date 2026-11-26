// Fork (docs/specs/pitch.md section 9): Convert to MIDI, layout side. The source is an audio clip of the song; the conversion runs on a worker
// (the converters are in ReBuzz/Core/AudioLibrary/AudioToMidi) and its notes become a new MIDI track with an instrument (Neo Poly for a melody or
// harmony, Neo Drums for drums) and a clip at the same place and length as the source: the same Session scene, the same arrangement
// positions, the source clip's region and loop. The whole application, track included, is one undo step.

using BuzzGUI.Common.Actions.SequenceActions;
using BuzzGUI.Common.Actions.SongActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.SequenceEditor.Actions;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.AudioLibrary.AudioToMidi;
using ReBuzz.Core.Notes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Core.Session
{
    /// <summary>What the clip gets when the conversion is put in the song.</summary>
    internal sealed class ConvertApply
    {
        /// <summary>Snap the note starts to this fraction of a beat (4 = sixteenth notes); 0 for no quantizing.</summary>
        public int QuantizeDivision;
        /// <summary>How far notes move to the grid, 0 to 1.</summary>
        public double QuantizeStrength = 1;
    }

    internal sealed class ConvertOutcome
    {
        public IMachine Machine { get; init; }
        public IClip Clip { get; init; }
        public string Instrument { get; init; } = "";
        public int Notes { get; init; }
        public int Bends { get; init; }
        public int Scene { get; init; } = -1;
        public List<int> Ticks { get; } = new();
    }

    internal sealed partial class SessionLayout
    {
        /// <summary>The instrument a conversion plays on: Neo Drums for drums, Neo Poly otherwise.</summary>
        public static string InstrumentFor(ConvertMode mode) => mode == ConvertMode.Drums ? "Neo Drums" : "Neo Poly";

        /// <summary>The source of a conversion: an audio clip of the song; null and a reason when it isn't one.</summary>
        public StemSource ConvertSourceOfClip(IClip clip, out string error) => StemSourceOfClip(clip, out error);

        /// <summary>Why a conversion of this mode can't be put in the song (the instrument isn't installed), or null.</summary>
        public string ConvertBlocker(ConvertMode mode)
        {
            string name = InstrumentFor(mode);
            var dll = buzz.MachineDLLs.Values.FirstOrDefault(d => d?.Info?.Name == name);
            if (dll == null) return $"{name} isn't installed, so {Product.Name} can't make the MIDI track.";
            if (dll.IsMissing || dll.IsCrashed) return $"{name} isn't usable ({(dll.IsMissing ? "missing" : "crashed")}).";
            return null;
        }

        /// <summary>
        /// Converts the source's audio on a worker thread. <paramref name="progress"/> is called from the worker with 0 to 1; cancel stops it
        /// (the task is cancelled). The audio is read as it is in the store (before the clip's gain, warp and reverse).
        /// </summary>
        public Task<ConvertResult> ConvertAsync(StemSource source, ConvertOptions options, Action<double> progress, CancellationToken cancel)
        {
            var asset = source.Asset;
            if (asset == null) return Task.FromException<ConvertResult>(new InvalidOperationException("The clip has no audio."));
            var left = asset.Source.Left;
            var right = asset.Channels > 1 ? asset.Source.Right : null;
            int rate = asset.SampleRate;
            return Task.Run(() =>
            {
                float[] mono = left;
                if (right != null && !ReferenceEquals(right, left))
                {
                    mono = new float[left.Length];
                    for (int i = 0; i < mono.Length; i++) mono[i] = 0.5f * (left[i] + right[i]);
                }
                return AudioConverter.Run(mono, rate, options, progress, cancel);
            }, cancel);
        }

        /// <summary>
        /// Puts a conversion in the song: a new MIDI track named after the clip, an instrument on it, and a clip of the notes where the source
        /// is. One undo step. Null and a reason when it can't.
        /// </summary>
        internal ConvertOutcome ApplyConversion(StemSource source, ConvertResult result, ConvertApply apply, out string error)
        {
            error = null;
            apply ??= new ConvertApply();
            if (source?.Clip == null || source.Asset == null || result == null)
            {
                error = "There is nothing to put in the song.";
                return null;
            }
            error = ConvertBlocker(result.Mode);
            if (error != null) return null;
            var sourceClip = source.Clip;
            var audio = AudioOf(sourceClip);
            if (audio == null)
            {
                error = $"\"{sourceClip.Name}\" isn't an audio clip any more.";
                return null;
            }

            int tpb = Math.Max(1, buzz.TPB);
            var asset = source.Asset;
            var map = MapOf(asset.Source, audio);
            if (audio.Reverse) map = map.Mirrored(asset.Source.Length, map.BeatAt(asset.Source.Length));
            double Beat(double seconds)
            {
                double sample = seconds * asset.SampleRate;
                if (audio.Reverse) sample = asset.Source.Length - sample;
                return map.BeatAt(Math.Clamp(sample, 0, asset.Source.Length));
            }
            int clipTime = sourceClip.Length * ClipNotes.TimeBase;
            int shift = result.Mode == ConvertMode.Drums ? 0 : audio.Transpose + (int)Math.Round(audio.Detune / 100.0);
            var notes = new List<ClipNote>();
            var controllers = new List<ControllerPoint>();
            foreach (var n in result.Notes)
            {
                double b0 = Beat(n.Start), b1 = Beat(n.End);
                if (b1 < b0) (b0, b1) = (b1, b0);
                int time = (int)Math.Round(b0 * tpb * ClipNotes.TimeBase);
                int end = (int)Math.Round(b1 * tpb * ClipNotes.TimeBase);
                if (time < 0 || time >= clipTime) continue;
                int pitch = Math.Clamp(n.Pitch + shift, 0, 127);
                var note = new ClipNote(time, Math.Max(ClipNotes.MinDuration, Math.Min(end, clipTime) - time), pitch, Math.Clamp(n.Velocity, 1, 127));
                notes.Add(note);
                if (n.Bends != null && n.Bends.Count > 0 && !audio.Reverse)
                {
                    foreach (var (t, cents) in n.Bends)
                    {
                        int bt = (int)Math.Round(Beat(t) * tpb * ClipNotes.TimeBase);
                        if (bt < time || bt >= note.End) continue;
                        int value = ControllerPoint.BendCentre + (int)Math.Round(Math.Clamp(cents, -200, 200) / 200.0 * 8191);
                        controllers.Add(new ControllerPoint(bt, ControllerPoint.PitchBend, Math.Clamp(value, 0, 16383)));
                    }
                    controllers.Add(new ControllerPoint(Math.Min(note.End, clipTime - 1), ControllerPoint.PitchBend, ControllerPoint.BendCentre));   // back to centre where it ends
                }
            }
            if (apply.QuantizeDivision > 0 && apply.QuantizeStrength > 0 && notes.Count > 0)
            {
                int grid = Math.Max(1, tpb * ClipNotes.TimeBase / apply.QuantizeDivision);
                notes = ClipNotes.Quantize(notes, grid, Math.Clamp(apply.QuantizeStrength, 0, 1), false, clipTime);
            }

            // Where the source is: its Session scene and its arrangement positions.
            SourcePlace(sourceClip, out int sourceScene, out var sourceEvents, out var sourceSettings);

            UndoStack.BeginActionGroup();
            try
            {
                string label = result.Mode switch { ConvertMode.Drums => "Drums", ConvertMode.Harmony => "Harmony", _ => "Melody" };
                string name = $"{label} - {source.Name}";
                var machine = NewInstrumentTrack(InstrumentFor(result.Mode), name)
                    ?? throw new InvalidOperationException("Couldn't make the MIDI track.");
                int track = FirstTrackOf(machine);
                if (track < 0) throw new InvalidOperationException("Couldn't make the MIDI track.");
                var added = notes.Select(ToEvent).Concat(controllers.Select(c => { var (t, v, d) = c.ToEvent(); return new ClipEvent(t, v, d); })).ToArray();
                var (clip, ticks) = PutMidiClip(sourceClip, machine, track, name, added, sourceScene, sourceEvents, sourceSettings);
                var outcome = new ConvertOutcome
                {
                    Machine = machine, Clip = clip, Instrument = InstrumentFor(result.Mode), Notes = notes.Count,
                    Bends = controllers.Count(c => c.Value != ControllerPoint.BendCentre), Scene = sourceScene,
                };
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

        /// <summary>Where an audio clip is: its Session scene (-1 for none), the arrangement positions it plays at, and its settings (a copy).</summary>
        internal void SourcePlace(IClip clip, out int scene, out List<int> events, out SessionDocument.ClipSettings settings)
        {
            scene = -1;
            for (int t = 0; t < TrackCount && scene < 0; t++)
                for (int s = 0; s < SceneCount; s++)
                    if (ReferenceEquals(ClipAt(t, s), clip)) { scene = s; break; }
            events = new List<int>();
            foreach (var seq in song.Sequences.Where(q => q.Machine == clip.Machine))
                foreach (var e in seq.Events)
                    if (e.Value.Type == SequenceEventType.PlayClip && ReferenceEquals(e.Value.Clip, clip))
                        events.Add(e.Key);
            settings = ClipOf(clip).Clone();
        }

        /// <summary>
        /// A MIDI clip of <paramref name="added"/> where the audio clip <paramref name="clip"/> is: the same length, Session scene, arrangement positions,
        /// region, loop and colour, on <paramref name="machine"/> (its Session track is <paramref name="track"/>). Part of the caller's undo group.
        /// </summary>
        internal (IClip Clip, List<int> Ticks) PutMidiClip(IClip sourceClip, IMachine machine, int track, string name, ClipEvent[] added,
                                                                 int sourceScene, List<int> sourceEvents, SessionDocument.ClipSettings sourceSettings)
        {
            string clipName = machine.GetNewClipName() ?? name;
            var create = new SessionCreateClipAction(this, machine, clipName, sourceClip.Length, midiColumn: true);
            UndoStack.Do(create);
            var clip = create.Created ?? throw new InvalidOperationException("Couldn't make a clip.");
            var settings = sourceSettings.Clone();
            settings.Audio = null;
            if (sourceScene >= 0)
            {
                while (SceneCount <= sourceScene) InsertScene(SceneCount);
                Edit(doc => doc.SetSlot(track, sourceScene, clip.Name));
            }
            SetClipSettings(clip, settings);
            int color = ClipColors.Get(sourceClip);
            if (color >= 0) SetClipColor(clip, color);
            if (added.Length > 0) UndoStack.Do(new ClipEventsReplaceAction(this, clip, ColumnKey.Midi, Array.Empty<ClipEvent>(), added));
            var ticks = new List<int>();
            var sequence = song.Sequences.FirstOrDefault(q => q.Machine == machine);
            if (sequence != null)
                foreach (int time in sourceEvents)
                {
                    UndoStack.Do(new SetEventAction(sequence, time, new SequenceEvent(SequenceEventType.PlayClip, clip)));
                    ticks.Add(time);
                }
            return (clip, ticks);
        }

        /// <summary>A new instrument track: <paramref name="dllName"/> connected to the master, with a track at the end. Part of the caller's undo group.</summary>
        internal IMachine NewInstrumentTrack(string dllName, string name)
        {
            var dll = buzz.MachineDLLs.Values.FirstOrDefault(d => d?.Info?.Name == dllName);
            if (dll == null) return null;
            Ensure();
            string wanted = string.IsNullOrWhiteSpace(name) ? dllName : name.Trim();
            string machineName = wanted;
            for (int i = 2; song.Machines.Any(m => m.Name == machineName); i++)
                machineName = $"{wanted} {i}";
            int placed = song.Machines.Count(m => m.DLL?.Info?.Type == MachineType.Generator);
            float x = Math.Clamp(-0.7f + 0.12f * (placed % 6), -0.95f, 0.95f);
            float y = Math.Clamp(0.1f + 0.15f * (placed / 6 % 5), -0.95f, 0.95f);
            (x, y) = MachinePlacement.Free(song.Machines, x, y);
            var master = song.Machines.FirstOrDefault(m => m.DLL?.Info?.Type == MachineType.Master);
            IMachine machine;
            song.BeginActionGroup();
            try
            {
                string editor = buzz.DefaultClipEditor != null && buzz.MachineDLLs.ContainsKey(buzz.DefaultClipEditor) ? buzz.DefaultClipEditor : null;
                song.CreateMachine(dll.Name, null, machineName, null, editor, null, -1, x, y);
                machine = song.Machines.LastOrDefault(m => m.Name == machineName);
                if (machine == null) return null;
                if (master != null)
                    song.ConnectMachines(machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            }
            finally
            {
                song.EndActionGroup();
            }
            if (!song.Sequences.Any(s => s.Machine == machine))
                song.AddSequence(machine, song.Sequences.Count);
            return machine;
        }
    }
}
