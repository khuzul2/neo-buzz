using BuzzGUI.Common;
using BuzzGUI.Common.Actions.PatternActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Clips;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Audio clips, layout side (docs/specs/session-view.md 1.0). An audio track is a track of a
    /// Session Clip Player machine; an audio clip is one of its patterns whose clip settings have
    /// audio (<see cref="SessionDocument.AudioSettings"/>): a wave of the song's wavetable, its
    /// tempo, warp mode and markers, gain and pitch. Dropping a file on a slot loads it into a
    /// free wave and makes the clip (a new audio track for it when the slot's track isn't one).
    ///
    /// After every edit (with the launch map), each audio clip's pattern gets how it plays now
    /// (<see cref="PatternCore.SessionAudio"/>): its audio read from the wave (kept until the
    /// wave changes), the warp map, Beats mode's slices (transients found once per wave). The
    /// Session Clip Player renders from that on the audio thread (<see cref="SessionClipEngine"/>).
    /// UI thread.
    /// </summary>
    internal sealed partial class SessionLayout
    {
        /// <summary>A wave as audio clips play it: read once, with its transients when Beats mode needs them.</summary>
        sealed class WaveAudio
        {
            public IWaveLayer Layer;
            public string Path;
            public int Count, Rate, Channels;
            public AudioSource Source;
            public List<double> Onsets;
            public int Version;
        }

        readonly Dictionary<int, WaveAudio> waveAudio = new Dictionary<int, WaveAudio>();
        readonly Dictionary<PatternCore, string> audioKeys = new Dictionary<PatternCore, string>();
        int waveVersion;
        bool audioRefreshPosted;

        /// <summary>Whether the machine is a Session Clip Player: its tracks are audio tracks.</summary>
        public static bool IsClipPlayer(IMachine machine) => machine?.DLL?.Info?.Name == SessionClipHost.MachineName;

        /// <summary>The Session Clip Player's DLL, or null when it isn't installed (Gear\Effects).</summary>
        IMachineDLL ClipPlayerDll => buzz.MachineDLLs.Values.FirstOrDefault(d => d?.Info?.Name == SessionClipHost.MachineName);

        /// <summary>Whether audio tracks can be made (the Session Clip Player is installed).</summary>
        public bool CanMakeAudioTracks => ClipPlayerDll != null;

        /// <summary>A pattern's audio, or null when it is no audio clip.</summary>
        public SessionDocument.AudioSettings AudioOf(IPattern pattern) => IsClipPlayer(pattern?.Machine) ? ClipOf(pattern).Audio : null;

        public bool IsAudioClip(IPattern pattern) => AudioOf(pattern) != null;

        void InitAudio()
        {
            if (song.Wavetable != null)
                song.Wavetable.WaveChanged += OnWaveChanged;
        }

        void OnWaveChanged(int index)
        {
            // Any thread: read again on the UI thread, once per burst.
            if (audioRefreshPosted) return;
            audioRefreshPosted = true;
            dispatcher.BeginInvoke(() =>
            {
                audioRefreshPosted = false;
                RefreshAudio();
            }, DispatcherPriority.Background);
        }

        void ResetAudio()
        {
            waveAudio.Clear();
            foreach (var p in audioKeys.Keys)
                if (p.SessionAudio != null) p.SessionAudio.Params = null;
            audioKeys.Clear();
        }

        // ------------------------------------------------------------------ audio tracks

        /// <summary>
        /// A new audio track: a Session Clip Player connected to the master (one step of the
        /// song's undo), with a track at the end. Null when the machine isn't installed.
        /// </summary>
        public IMachine NewAudioTrack(string name = null)
        {
            var dll = ClipPlayerDll;
            if (dll == null) return null;
            Ensure();
            string wanted = string.IsNullOrWhiteSpace(name) ? "Audio" : name.Trim();
            string machineName = wanted;
            for (int i = 2; song.Machines.Any(m => m.Name == machineName); i++)
                machineName = $"{wanted} {i}";
            int players = song.Machines.Count(IsClipPlayer);
            float x = Math.Clamp(-0.7f + 0.12f * (players % 6), -0.95f, 0.95f);
            float y = Math.Clamp(-0.7f + 0.15f * (players / 6 % 6), -0.95f, 0.95f);
            var master = song.Machines.FirstOrDefault(m => m.DLL?.Info?.Type == MachineType.Master);

            IMachine machine;
            song.BeginActionGroup();
            try
            {
                // The default pattern editor, as machines made from the menu get (none if it isn't there).
                string editor = buzz.DefaultPatternEditor != null && buzz.MachineDLLs.ContainsKey(buzz.DefaultPatternEditor) ? buzz.DefaultPatternEditor : null;
                song.CreateMachine(dll.Name, null, machineName, null, editor, null, -1, x, y);
                machine = song.Machines.LastOrDefault(m => m.Name == machineName && IsClipPlayer(m));
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

        /// <summary>The first track of a machine, or -1.</summary>
        internal int FirstTrackOf(IMachine machine)
        {
            Sync();
            for (int t = 0; t < TrackCount; t++)
                if (SequenceOf(t)?.Machine == machine) return t;
            return -1;
        }

        // ------------------------------------------------------------------ import

        /// <summary>
        /// Loads an audio file (WAV, AIFF, FLAC: what the wavetable reads) into a free wave and
        /// puts it in a slot as an audio clip at its tempo (found from its length and transients),
        /// warped in Beats mode. On a track that isn't an audio track, a new audio track gets it
        /// (in the same scene). The clip is one undo step of the session; the wave stays loaded.
        /// Null and a reason when it can't.
        /// </summary>
        public IPattern ImportAudio(int track, int scene, string path, out string error)
        {
            error = null;
            if (scene < 0 || string.IsNullOrEmpty(path))
            {
                error = "Nowhere to put it.";
                return null;
            }
            if (!CanMakeAudioTracks)
            {
                error = $"The {SessionClipHost.MachineName} machine isn't installed (Gear\\Effects), so ReBuzz can't play audio clips.";
                return null;
            }
            int wave = FreeWave();
            if (wave < 0)
            {
                error = "The wavetable is full.";
                return null;
            }
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            try
            {
                song.Wavetable.LoadWave(wave, path, name, false);
            }
            catch (Exception e)
            {
                ClearWave(wave);
                error = $"Couldn't read {System.IO.Path.GetFileName(path)}: {e.Message}";
                return null;
            }
            var audio = WaveAudioOf(wave);
            if (audio == null || audio.Source.Length == 0)
            {
                ClearWave(wave);
                error = $"{System.IO.Path.GetFileName(path)} holds no audio.";
                return null;
            }
            var (bpm, _) = AudioAnalysis.Tempo(audio.Source, Session.BeatsPerBar);

            var machine = SequenceOf(track)?.Machine;
            if (!IsClipPlayer(machine))
            {
                machine = NewAudioTrack(name);
                track = FirstTrackOf(machine);
                if (machine == null || track < 0)
                {
                    ClearWave(wave);
                    error = "Couldn't make an audio track.";
                    return null;
                }
            }
            return AddAudioClip(track, scene, machine, name, new SessionDocument.AudioSettings { Wave = wave, Bpm = Math.Round(bpm, 2) });
        }

        /// <summary>Makes an audio clip of a wave in a slot (undoable); the slot's clip, if any, makes way (its pattern stays).</summary>
        internal IPattern AddAudioClip(int track, int scene, IMachine machine, string name, SessionDocument.AudioSettings audio)
        {
            var source = WaveAudioOf(audio.Wave)?.Source;
            if (source == null || machine == null) return null;
            int length = AudioClipLength(source, audio);
            UndoStack.BeginActionGroup();
            try
            {
                var create = new SessionCreatePatternAction(this, machine, string.IsNullOrWhiteSpace(name) ? "Audio" : name.Trim(), length);
                UndoStack.Do(create);
                var created = create.Created;
                if (created == null) return null;
                Edit(doc =>
                {
                    doc.SetSlot(track, scene, created.Name);
                    doc.SetClip(machine.Name, created.Name, new SessionDocument.ClipSettings { Audio = audio.Clone() });
                });
                return created;
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
        }

        /// <summary>A clip's length in ticks: its audio's beats, rounded up to a tick.</summary>
        int AudioClipLength(AudioSource source, SessionDocument.AudioSettings audio)
        {
            double beats = MapOf(source, audio).BeatAt(source.Length);
            return Math.Clamp((int)Math.Ceiling(beats * Math.Max(1, buzz.TPB) - 1e-6), 1, 1 << 20);
        }

        int FreeWave()
        {
            var waves = song.Wavetable?.Waves;
            if (waves == null) return -1;
            for (int i = 0; i < waves.Count; i++)
                if (waves[i] == null || waves[i].Layers.Count == 0) return i;
            return -1;
        }

        void ClearWave(int index)
        {
            try { song.Wavetable.LoadWave(index, null, null, false); }
            catch (Exception) { }
            waveAudio.Remove(index);
        }

        // ------------------------------------------------------------------ edits

        /// <summary>
        /// Changes an audio clip's audio settings (undoable). Without warp markers its length
        /// follows its tempo (a clip of 4 beats at 120 BPM is 8 at 60).
        /// </summary>
        public void SetAudio(IPattern pattern, SessionDocument.AudioSettings audio)
        {
            if (pattern?.Machine == null || audio == null || !IsClipPlayer(pattern.Machine)) return;
            var settings = ClipOf(pattern);
            if (settings.Audio == null) return;
            var source = WaveAudioOf(audio.Wave)?.Source;
            UndoStack.BeginActionGroup();
            try
            {
                if (source != null)
                {
                    int length = AudioClipLength(source, audio);
                    if (length != pattern.Length)
                        UndoStack.Do(new SetLengthAction(pattern, length));
                }
                settings.Audio = audio.Clone();
                SetClipSettings(pattern, settings);
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
        }

        /// <summary>Finds an audio clip's tempo again from its audio (undoable); the tempo found, or 0.</summary>
        public double DetectTempo(IPattern pattern)
        {
            var audio = AudioOf(pattern);
            var source = audio == null ? null : WaveAudioOf(audio.Wave)?.Source;
            if (source == null) return 0;
            var (bpm, _) = AudioAnalysis.Tempo(source, Session.BeatsPerBar);
            audio.Bpm = Math.Round(bpm, 2);
            audio.Markers.Clear();
            SetAudio(pattern, audio);
            return audio.Bpm;
        }

        /// <summary>What the clip panel shows of an audio clip's audio: the wave's name, seconds, rate and channels; null if it is gone.</summary>
        public (string Wave, double Seconds, int Rate, int Channels, double Beats)? AudioInfo(IPattern pattern)
        {
            var audio = AudioOf(pattern);
            if (audio == null) return null;
            var w = WaveAudioOf(audio.Wave);
            if (w == null) return null;
            var wave = song.Wavetable.Waves[audio.Wave];
            return (wave?.Name ?? "", w.Source.Seconds, w.Rate, w.Channels, MapOf(w.Source, audio).BeatAt(w.Source.Length));
        }

        /// <summary>An audio clip's audio as it plays (for the warp marker editor and tests), or null.</summary>
        internal AudioSource AudioSourceOf(IPattern pattern)
        {
            var audio = AudioOf(pattern);
            return audio == null ? null : WaveAudioOf(audio.Wave)?.Source;
        }

        /// <summary>An audio clip's transients (audio samples), found once per wave; empty if none.</summary>
        internal IReadOnlyList<double> TransientsOf(IPattern pattern)
        {
            var audio = AudioOf(pattern);
            var w = audio == null ? null : WaveAudioOf(audio.Wave);
            if (w == null) return Array.Empty<double>();
            return w.Onsets ??= AudioAnalysis.Onsets(w.Source);
        }

        // ------------------------------------------------------------------ recording

        /// <summary>An audio take recording: its capture, and how its length is known.</summary>
        sealed class AudioTake
        {
            public SequenceCore Track;
            public PatternCore Take;
            public AudioCapture Capture;
            public SessionClipEngine.MasterCapture Master;
            public int Wave;
            public string Name;
            public double Bpm;
            public int TicksPerBeat;
            public bool UntilStopped;
            public bool Stopping;
        }

        readonly Dictionary<SequenceCore, AudioTake> audioTakes = new Dictionary<SequenceCore, AudioTake>(ReferenceEqualityComparer.Instance);

        /// <summary>The longest an audio take recorded until stopped can be.</summary>
        public const int MaxAudioTakeMinutes = 30;

        /// <summary>What a clip player records: 0 its input, 1 the master output (resampling).</summary>
        static int InputOf(IMachine machine) =>
            machine?.ParameterGroups.FirstOrDefault(g => g.Type == ParameterGroupType.Global)?.Parameters.FirstOrDefault(p => p.Name == "Input")?.GetValue(0) ?? 0;

        /// <summary>Sets a clip player's Input (0 its input, 1 the master output).</summary>
        internal static void SetInputOf(IMachine machine, int value) =>
            machine?.ParameterGroups.FirstOrDefault(g => g.Type == ParameterGroupType.Global)?.Parameters.FirstOrDefault(p => p.Name == "Input")?.SetValue(-1, value);

        /// <summary>Whether a track of a clip player records the master (resampling).</summary>
        public static bool IsResampling(IMachine machine) => IsClipPlayer(machine) && InputOf(machine) == 1;

        /// <summary>
        /// A new resampling track: an audio track recording the master output (Live's
        /// resampling), to consolidate what the session plays into audio clips. Armed.
        /// </summary>
        public IMachine NewResamplingTrack()
        {
            var machine = NewAudioTrack("Resample");
            if (machine == null) return null;
            SetInputOf(machine, 1);
            int track = FirstTrackOf(machine);
            if (track >= 0) SetArmed(track, true);
            return machine;
        }

        int SampleRate => Math.Max(1, ReBuzzCore.masterInfo?.SamplesPerSec ?? 44100);

        /// <summary>Frames of audio a number of ticks lasts at a tempo.</summary>
        long FramesOf(long ticks, double bpm, int ticksPerBeat) =>
            (long)Math.Round(ticks * 60.0 * SampleRate / (Math.Max(1, bpm) * Math.Max(1, ticksPerBeat)));

        /// <summary>
        /// Records a take of an audio track (an armed Session Clip Player): a new audio clip in an
        /// empty slot, launched on the grid, recording its input (or the master, resampling) for
        /// the record length, or until stopped (then whole bars). Its audio becomes a wave of the
        /// song when the take is done; it plays from its next pass. One undo step (the clip).
        /// </summary>
        IPattern RecordAudioTake(int track, int scene, SequenceCore seq)
        {
            var machine = seq.Machine;
            int wave = FreeWave();
            string name = machine?.GetNewPatternName();
            if (wave < 0 || name == null) return null;
            var doc = Ensure();
            int bar = BarTicks;
            int bars = doc.RecordBars;
            int ticks = (bars > 0 ? bars : TakeRoomBars) * bar;
            double bpm = buzz.BPM;
            int tpb = Math.Max(1, buzz.TPB);
            StopRecording(seq);

            // The wave's slot is kept (a frame of silence) until the take's audio fills it.
            string waveName = $"{machine.Name} {name}";
            song.Wavetable.AllocateWave(wave, "", waveName, 1, WaveFormat.Float32, true, 65, false, true);
            PatternCore take;
            UndoStack.BeginActionGroup();
            try
            {
                var create = new SessionCreatePatternAction(this, machine, name, ticks);
                UndoStack.Do(create);
                take = create.Created as PatternCore;
                if (take == null) return null;
                Edit(d =>
                {
                    d.SetSlot(track, scene, take.Name);
                    d.SetClip(machine.Name, take.Name, new SessionDocument.ClipSettings
                    {
                        Audio = new SessionDocument.AudioSettings { Wave = wave, Bpm = bpm, Warp = "Beats" }
                    });
                });
            }
            finally
            {
                UndoStack.EndActionGroup();
            }

            // From the bar heard: the input came back a round trip late (the output latency; the
            // input's own isn't known).
            long skip = Engine.LatencyCompensation ? buzz.OutputLatencySamples : 0;
            long length = bars > 0 ? FramesOf(ticks, bpm, tpb) : long.MaxValue;
            var capture = new AudioCapture(SampleRate, skip, length, (long)MaxAudioTakeMinutes * 60 * SampleRate);
            var t = new AudioTake { Track = seq, Take = take, Capture = capture, Wave = wave, Name = waveName, Bpm = bpm, TicksPerBeat = tpb, UntilStopped = bars == 0 };
            if (InputOf(machine) == 1)
            {
                t.Master = new SessionClipEngine.MasterCapture(take, capture);
                Engine.MasterCaptures = Engine.MasterCaptures.Append(t.Master).ToArray();
            }
            else
            {
                (take.SessionAudio ??= new ClipPlayback()).Capture = capture;
            }
            audioTakes[seq] = t;

            int countIn = 0;
            if (!buzz.PlayRequested && doc.CountInBars > 0)
            {
                long from = song.PlayPosition;
                long target = (from + (long)doc.CountInBars * bar + bar - 1) / bar * bar;
                countIn = (int)Math.Min(int.MaxValue, target - from);
            }
            Engine.LaunchTake(seq, take, countIn);
            UpdatePollTimer();
            RecordingChanged?.Invoke();
            return take;
        }

        /// <summary>
        /// Ends an audio take: one of a fixed length keeps what it has (the rest silence); one
        /// recorded until stopped gets its length in whole bars and records to the end of the last.
        /// </summary>
        void StopAudioTake(SequenceCore seq)
        {
            if (!audioTakes.TryGetValue(seq, out var t)) return;
            if (!t.UntilStopped)
            {
                t.Capture.SetLength(t.Capture.Written);
                FinishAudioTake(t);
                return;
            }
            if (t.Stopping) return;
            int bar = BarTicks;
            double framesPerTick = FramesOf(1000, t.Bpm, t.TicksPerBeat) / 1000.0;
            long ticks = Math.Max(1, (long)Math.Ceiling(t.Capture.Written / framesPerTick - 1e-6));
            long bars = Math.Clamp((ticks + bar - 1) / bar, 1, MaxTakeBars);
            if (!t.Take.IsDeleted && t.Take.Length != bars * bar)
                t.Take.Length = (int)(bars * bar);
            t.Capture.SetLength(FramesOf(bars * bar, t.Bpm, t.TicksPerBeat));
            t.Stopping = true;
            if (t.Capture.IsDone) FinishAudioTake(t);
        }

        /// <summary>UI thread (the take poll): chunks made ahead of each take; takes done, stopped or gone finish.</summary>
        void PollAudioTakes()
        {
            if (audioTakes.Count == 0 || Engine == null) return;
            foreach (var t in audioTakes.Values.ToList())
            {
                if (t.Track.IsRemoved || t.Take.IsDeleted)
                {
                    t.Capture.SetLength(0);
                    FinishAudioTake(t);
                    continue;
                }
                t.Capture.Prepare(8L * SampleRate);
                if (t.Capture.IsDone)
                {
                    FinishAudioTake(t);
                    continue;
                }
                if (!Engine.IsPlayingOrQueued(t.Track, t.Take))
                {
                    // Stopped (or replaced) before it was done: what it has.
                    if (t.UntilStopped && !t.Stopping) StopAudioTake(t.Track);
                    if (audioTakes.ContainsKey(t.Track))
                    {
                        t.Capture.SetLength(Math.Min(t.Capture.Length, t.Capture.Written));
                        FinishAudioTake(t);
                    }
                    continue;
                }
                if (t.UntilStopped && !t.Stopping)
                {
                    // Recording on: the take grows before its end, as MIDI takes do.
                    int bar = BarTicks;
                    double framesPerTick = FramesOf(1000, t.Bpm, t.TicksPerBeat) / 1000.0;
                    long elapsed = (long)(t.Capture.Written / framesPerTick);
                    if (t.Take.Length - elapsed < (long)TakeGrowMarginBars * bar && t.Take.Length < MaxTakeBars * bar)
                        t.Take.Length = (int)Math.Min((long)t.Take.Length + (long)TakeRoomBars * bar, (long)MaxTakeBars * bar);
                }
            }
        }

        /// <summary>The take is over: its audio goes into its wave (the clip plays it from then on).</summary>
        void FinishAudioTake(AudioTake t)
        {
            if (!audioTakes.TryGetValue(t.Track, out var current) || !ReferenceEquals(current, t)) return;
            audioTakes.Remove(t.Track);
            if (t.Master != null)
                Engine.MasterCaptures = Engine.MasterCaptures.Where(m => !ReferenceEquals(m, t.Master)).ToArray();
            else if (t.Take.SessionAudio != null)
                t.Take.SessionAudio.Capture = null;
            long frames = Math.Min(t.Capture.Length, t.Capture.Written);
            if (frames > 0 && !t.Take.IsDeleted)
            {
                var (left, right) = t.Capture.Collect(t.Capture.Length == long.MaxValue ? frames : t.Capture.Length);
                song.Wavetable.AllocateWave(t.Wave, "", t.Name, left.Length, WaveFormat.Float32, true, 65, false, false);
                var layer = song.Wavetable.Waves[t.Wave]?.Layers.FirstOrDefault();
                if (layer != null)
                {
                    layer.SampleRate = t.Capture.SampleRate;
                    layer.SetDataAsFloat(left, 0, 1, 0, 0, left.Length);
                    layer.SetDataAsFloat(right, 0, 1, 1, 0, right.Length);
                    layer.InvalidateData();
                }
                if (t.Capture.Dropped > 0)
                    buzz.DCWriteLine($"Session: audio take {t.Name}: {t.Capture.Dropped} frames lost (the UI was too slow)", DCLogLevel.Warning);
            }
            waveAudio.Remove(t.Wave);
            RefreshAudio();
            UpdatePollTimer();
            RecordingChanged?.Invoke();
        }

        void ResetAudioTakes()
        {
            audioTakes.Clear();
            if (Engine != null) Engine.MasterCaptures = null;
        }

        /// <summary>Whether an audio take records (or waits to) on the track.</summary>
        bool IsAudioRecording(ISequence track) => track is SequenceCore seq && audioTakes.ContainsKey(seq);

        bool IsAudioTake(IPattern clip) => clip != null && audioTakes.Values.Any(t => ReferenceEquals(t.Take, clip));

        // ------------------------------------------------------------------ playback objects

        static WarpMap MapOf(AudioSource source, SessionDocument.AudioSettings audio)
        {
            if (audio.Markers != null && audio.Markers.Count >= 2)
                return new WarpMap(audio.Markers.Select(m => new WarpMarker(m.Sample, m.Beat)));
            return WarpMap.Straight(source.Length, source.Seconds * Math.Max(1, audio.Bpm) / 60);
        }

        static WarpMode ModeOf(string warp) => Enum.TryParse<WarpMode>(warp, out var m) ? m : WarpMode.Beats;

        AudioClipParams ParamsOf(SessionDocument.AudioSettings audio, WaveAudio w)
        {
            var map = MapOf(w.Source, audio);
            var mode = ModeOf(audio.Warp);
            double[] slices = null;
            if (mode == WarpMode.Beats)
            {
                IReadOnlyList<double> transients = audio.PreservesTransients ? (w.Onsets ??= AudioAnalysis.Onsets(w.Source)) : Array.Empty<double>();
                slices = AudioAnalysis.Slices(w.Source, map, transients, audio.Grid);
            }
            return new AudioClipParams(w.Source, map, mode, slices, audio.Gain, audio.Semitones);
        }

        /// <summary>A wave's audio, read again when the wave isn't the one read; null when empty.</summary>
        WaveAudio WaveAudioOf(int index)
        {
            var waves = song.Wavetable?.Waves;
            if (waves == null || index < 0 || index >= waves.Count) return null;
            var layer = waves[index]?.Layers.FirstOrDefault();
            if (layer == null || layer.SampleCount <= 0)
            {
                waveAudio.Remove(index);
                return null;
            }
            if (waveAudio.TryGetValue(index, out var known) && ReferenceEquals(known.Layer, layer) && known.Path == layer.Path &&
                known.Count == layer.SampleCount && known.Rate == layer.SampleRate && known.Channels == layer.ChannelCount)
                return known;
            int n = layer.SampleCount;
            var left = new float[n];
            layer.GetDataAsFloat(left, 0, 1, 0, 0, n);
            var right = left;
            if (layer.ChannelCount > 1)
            {
                right = new float[n];
                layer.GetDataAsFloat(right, 0, 1, 1, 0, n);
            }
            var w = new WaveAudio
            {
                Layer = layer,
                Path = layer.Path,
                Count = n,
                Rate = layer.SampleRate,
                Channels = layer.ChannelCount,
                Source = new AudioSource(left, right, layer.SampleRate > 0 ? layer.SampleRate : 44100),
                Version = ++waveVersion
            };
            waveAudio[index] = w;
            return w;
        }

        /// <summary>
        /// Gives every audio clip's pattern how it plays now, and takes it from patterns that are
        /// audio clips no more. Only what changed is made again. After every edit, and when a wave changes.
        /// </summary>
        internal void RefreshAudio()
        {
            var live = new HashSet<PatternCore>();
            var doc = document;
            if (doc != null && doc.Clips.Count > 0)
            {
                var machines = new Dictionary<string, IMachine>();
                foreach (var m in song.Machines)
                    if (IsClipPlayer(m)) machines[m.Name] = m;
                // A wave changed since it was read: read again (the version tells the clips).
                foreach (var index in waveAudio.Keys.ToList()) WaveAudioOf(index);
                foreach (var entry in doc.Clips)
                {
                    var audio = entry.Value.Audio;
                    if (audio == null || !machines.TryGetValue(entry.Key.Machine, out var machine)) continue;
                    if (machine.Patterns.FirstOrDefault(p => p.Name == entry.Key.Pattern) is not PatternCore pattern) continue;
                    live.Add(pattern);
                    var w = WaveAudioOf(audio.Wave);
                    string key = audio.ToXml().ToString(System.Xml.Linq.SaveOptions.DisableFormatting) + "|" + (w?.Version ?? 0);
                    var playback = pattern.SessionAudio ??= new ClipPlayback();
                    if (audioKeys.TryGetValue(pattern, out var old) && old == key) continue;
                    try
                    {
                        playback.Params = w == null ? null : ParamsOf(audio, w);
                    }
                    catch (Exception)
                    {
                        playback.Params = null;               // never stops the layout; the clip is silent
                    }
                    audioKeys[pattern] = key;
                }
            }
            foreach (var gone in audioKeys.Keys.Where(p => !live.Contains(p)).ToList())
            {
                if (gone.SessionAudio != null) gone.SessionAudio.Params = null;
                audioKeys.Remove(gone);
            }
        }
    }
}
