// Fork (docs/specs/neo-audio.md "Takes and comping"): audio recorded into the arrangement. Armed Neo Audio tracks record beside the MIDI ones:
// from the play position (or each pass of the song's loop) into a take that goes to a temp file as it records; when recording ends each take
// is committed to the song's store as an asset and becomes a clip on the track's main lane. A loop recording makes a take of every pass on
// take lanes (the newest also plays on the main lane), to be comped.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;

namespace ReBuzz.Core
{
    internal sealed partial class ArrangementRecorder
    {
        /// <summary>The longest an audio recording into the arrangement can be.</summary>
        public const int MaxMinutes = 30;

        sealed class AudioRec
        {
            public IMachine Machine;
            public ISequence Lane;
            public AudioCapture Capture;
            public ArrangementAudioTake Entry;
            public string Name;
            public int Origin;
            public int StartTick;
            public bool Loops;
            public int LoopStart, LoopEnd;
            public double Bpm;
            public int TicksPerBeat;
            public long Skip;
        }

        readonly List<AudioRec> audioTakes = new();

        /// <summary>The audio tracks recording now: a view draws their growing waveform from <see cref="LiveAudio"/>.</summary>
        public IReadOnlyList<(ISequence Lane, int Origin, AudioCapture Capture)> LiveAudio => audioTakes.Select(a => (a.Lane, a.StartTick, a.Capture)).ToList();

        int Rate => Math.Max(1, ReBuzzCore.masterInfo?.SamplesPerSec ?? 44100);

        /// <summary>Starts a take for each armed audio track. Null when started, else why not.</summary>
        string StartAudio(List<IMachine> machines, bool loopRecording, int position, int loopStart, int loopEnd)
        {
            var layout = buzz.SessionLayout;
            double bpm = Math.Max(1, buzz.BPM);
            int tpb = Math.Max(1, buzz.TPB);
            foreach (var machine in machines)
            {
                if (MainLaneOf(machine) is not SequenceCore lane) continue;
                if (SessionLayout.IsResampling(machine)) return $"{machine.Name} records the master: it records in the Session (switch its Input to its own).";
                string name = $"{machine.Name} take";
                var file = TakeFile.Create(name, Rate, bpm);
                long skip = layout?.CaptureSkipOf(machine) ?? 0;
                var capture = new AudioCapture(Rate, skip, long.MaxValue, (long)MaxMinutes * 60 * Rate, Rate * 60.0 / bpm, file);
                int origin = loopRecording ? loopStart : position;
                var entry = new ArrangementAudioTake(machine, capture, position, loopStart, loopEnd, loopRecording);
                audioTakes.Add(new AudioRec
                {
                    Machine = machine, Lane = lane, Capture = capture, Entry = entry, Name = name, Origin = origin, StartTick = position,
                    Loops = loopRecording, LoopStart = loopStart, LoopEnd = loopEnd, Bpm = bpm, TicksPerBeat = tpb, Skip = skip,
                });
            }
            return null;
        }

        void PublishAudio(SessionCore session) => session.ArrangementAudio = audioTakes.Select(a => a.Entry).ToArray();

        /// <summary>Recording ended: the streams leave the engine and keep what they have. Returns them for writing.</summary>
        List<AudioRec> EndAudio()
        {
            var done = audioTakes.ToList();
            if (buzz.SessionCore != null) buzz.SessionCore.ArrangementAudio = null;
            audioTakes.Clear();
            foreach (var a in done) a.Capture.SetLength(a.Capture.Written);
            return done;
        }

        /// <summary>A new song or an opened one: the streams are let go and their files deleted.</summary>
        void AbandonAudio()
        {
            if (buzz.SessionCore != null) buzz.SessionCore.ArrangementAudio = null;
            foreach (var a in audioTakes) a.Capture.Close();
            audioTakes.Clear();
        }

        /// <summary>Commits each stream's temp file as an asset and writes the clips. Part of the caller's undo group. Returns the clips written.</summary>
        int WriteAudio(List<AudioRec> done)
        {
            int count = 0;
            var layout = buzz.SessionLayout;
            if (layout == null)
            {
                foreach (var a in done) a.Capture.Close();
                return 0;
            }
            var editor = new ClipEditor(buzz);
            foreach (var a in done)
            {
                long written = a.Capture.Written;
                if (written <= 0)
                {
                    a.Capture.Close();
                    continue;
                }
                var (left, right) = a.Capture.Collect(written);
                int dropped = (int)Math.Min(int.MaxValue, a.Capture.Dropped);
                a.Capture.Close();                                     // committed: the temp file goes
                if (dropped > 0) buzz.DCWriteLine($"Arrangement recording: {a.Name}: {dropped} frames lost (the UI was too slow)", DCLogLevel.Warning);
                double framesPerTick = a.Capture.SampleRate * 60.0 / (a.Bpm * a.TicksPerBeat);
                if (!a.Loops)
                {
                    int ticks = Math.Max(1, (int)Math.Ceiling(left.Length / framesPerTick - 1e-6));
                    var clip = MakeTake(layout, a, left, right, 1);
                    if (clip == null) continue;
                    Status = editor.Place(a.Lane, clip, a.Origin, ticks);
                    if (Status == null) count++;
                    continue;
                }
                count += WriteAudioTakes(layout, editor, a, left, right, framesPerTick);
            }
            return count;
        }

        /// <summary>Loop recording: every pass of the loop is a take of the exact length of the loop; the newest also plays on the main lane.</summary>
        int WriteAudioTakes(SessionLayout layout, ClipEditor editor, AudioRec a, float[] left, float[] right, double framesPerTick)
        {
            int loopTicks = Math.Max(1, a.LoopEnd - a.LoopStart);
            long full = (long)Math.Round(loopTicks * framesPerTick);
            var bounds = new List<(long From, long To)>();
            long previous = 0;
            for (int i = 0; i < a.Entry.MarkCount; i++)
            {
                long mark = Math.Min(a.Entry.MarkAt(i), left.Length);
                bounds.Add((previous, mark));
                previous = mark;
            }
            bounds.Add((previous, left.Length));
            long lead = (long)Math.Round((a.StartTick - a.LoopStart) * framesPerTick);      // a recording that began inside the loop: the first pass starts late
            int made = 0;
            IClip newest = null;
            int newestTicks = loopTicks;
            for (int p = 0; p < bounds.Count; p++)
            {
                var (from, to) = bounds[p];
                long pad = p == 0 ? Math.Max(0, lead) : 0;
                long frames = to - from + pad;
                if (frames < framesPerTick) continue;                                      // a pass that is less than a tick isn't a take
                long length = frames;
                int ticks = loopTicks;
                if (full - frames > a.Skip + 4096)
                {
                    ticks = Math.Max(1, (int)Math.Ceiling(frames / framesPerTick - 1e-6));  // stopped inside a pass: the take is as long as it was played
                    length = frames;
                }
                else
                    length = full;                                                         // within the round trip of the end: a whole pass, the rest silence
                var l = new float[length];
                var r = new float[length];
                Array.Copy(left, from, l, pad, Math.Min(to - from, length - pad));
                Array.Copy(right, from, r, pad, Math.Min(to - from, length - pad));
                var clip = MakeTake(layout, a, l, r, p + 1);
                if (clip == null) continue;
                var lane = FreeTakeLane(a.Machine, editor, a.Origin, a.Origin + ticks);
                if (lane == null) continue;
                Status = editor.Place(lane, clip, a.Origin, ticks);
                if (Status != null) continue;
                made++;
                newest = clip;
                newestTicks = ticks;
            }
            // The newest take plays: a copy of it on the main lane (an audio clip is never shared between places).
            if (newest != null)
            {
                Status = editor.Place(a.Lane, newest, a.Origin, newestTicks);
                if (Status != null) made--;
            }
            return made;
        }

        // ------------------------------------------------------------------ recovery

        /// <summary>Takes a crash left in the take folder (the app's own folder; not those still recording).</summary>
        public List<TakeLeftover> LeftoverTakes() => IsRecording ? new List<TakeLeftover>() : TakeFile.Leftovers();

        /// <summary>
        /// "Recover takes": each leftover take becomes a clip on a new recovery track (one after the other from the song's start), one undo step; its
        /// temp file goes once it is in the song's store. Returns the clips made.
        /// </summary>
        public int Recover(IReadOnlyList<TakeLeftover> takes)
        {
            var layout = buzz.SessionLayout;
            if (layout == null || takes == null || takes.Count == 0) return 0;
            layout.Ensure();
            int made = 0;
            var recovered = new List<TakeLeftover>();
            var stack = buzz.SongCore.ActionStack;
            stack.BeginActionGroup();
            try
            {
                stack.Label("Recover takes");
                var machine = layout.NewAudioTrack("Recovered");
                var lane = machine == null ? null : Song.Sequences.FirstOrDefault(s => s.Machine == machine);
                if (lane == null) return 0;
                var editor = new ClipEditor(buzz);
                int tick = 0;
                foreach (var take in takes)
                {
                    var (left, right) = TakeFile.Read(take);
                    if (left.Length == 0) continue;
                    double bpm = take.Bpm > 0 ? take.Bpm : Math.Max(1, buzz.BPM);
                    var asset = layout.AddAsset(new AudioAsset(take.Name, "", null, new AudioSource(left, right, take.SampleRate), 2));
                    var audio = new SessionDocument.AudioSettings { Asset = asset.Id, Bpm = Math.Clamp(bpm, 1, 999), Warp = "Off" };
                    var clip = layout.CreateAudioClip(machine, $"Recovered {made + 1}", audio);
                    if (clip == null) continue;
                    int ticks = Math.Max(1, (int)Math.Ceiling(left.Length / (take.SampleRate * 60.0 / (Math.Max(1, buzz.BPM) * Math.Max(1, buzz.TPB))) - 1e-6));
                    if (editor.Place(lane, clip, tick, ticks) != null) continue;
                    tick += ticks;
                    made++;
                    recovered.Add(take);
                }
            }
            finally
            {
                stack.EndActionGroup();
            }
            foreach (var take in recovered) TakeFile.Discard(take);
            if (made > 0) buzz.SetModifiedFlag();
            return made;
        }

        IClip MakeTake(SessionLayout layout, AudioRec a, float[] left, float[] right, int number)
        {
            string name = number > 1 || a.Loops ? $"{a.Machine.Name} take {number}" : $"{a.Machine.Name} rec";
            var asset = layout.AddAsset(new AudioAsset(name, "", null, new AudioSource(left, right, a.Capture.SampleRate), 2));
            var audio = new SessionDocument.AudioSettings { Asset = asset.Id, Bpm = Math.Clamp(a.Bpm, 1, 999), Warp = "Off" };
            return layout.CreateAudioClip(a.Machine, name, audio);
        }
    }
}
