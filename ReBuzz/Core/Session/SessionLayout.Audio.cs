using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Audio clips, layout side (docs/specs/session-view.md 1.0). An audio track is a track of a
    /// Neo Audio machine (once the Session Clip Player); an audio clip is one of its patterns whose clip settings have
    /// audio (<see cref="SessionDocument.AudioSettings"/>): an asset of the song's audio store (docs/specs/neo-audio.md; clips an
    /// older song made name a wave of the wavetable and are migrated to assets, the wave kept), its
    /// tempo, warp mode and markers, gain and pitch. Dropping a file on a slot imports it as an asset
    /// and makes the clip (a new audio track for it when the slot's track isn't one).
    ///
    /// After every edit (with the launch map), each audio clip's pattern gets how it plays now
    /// (<see cref="PatternCore.SessionAudio"/>): its audio (the asset's), the warp map, Beats mode's slices
    /// (transients found once per asset). Neo Audio renders from that on the audio thread (<see cref="SessionClipEngine"/>).
    /// UI thread.
    /// </summary>
    internal sealed partial class SessionLayout
    {
        /// <summary>An asset as audio clips play it, with its transients when Beats mode needs them (found once).</summary>
        sealed class WaveAudio
        {
            public AudioAsset Asset;
            public AudioSource Source;
            public List<double> Onsets;
            /// <summary>The audio backwards, and its transients, made once for clips that play it reversed (NA2).</summary>
            public AudioSource Reversed;
            public List<double> ReversedOnsets;
            public int Version;
        }

        /// <summary>A wave of the wavetable a clip of an older song played, migrated to an asset (the wave stays as it is).</summary>
        sealed class MigratedWave
        {
            public IWaveLayer Layer;
            public int Count, Rate, Channels;
            public string AssetId;
        }

        readonly Dictionary<string, WaveAudio> assetAudio = new Dictionary<string, WaveAudio>(StringComparer.Ordinal);
        readonly Dictionary<int, MigratedWave> migratedWaves = new Dictionary<int, MigratedWave>();
        AudioAssetStore Assets => buzz.AudioAssets;
        readonly Dictionary<PatternCore, string> audioKeys = new Dictionary<PatternCore, string>();
        int waveVersion;
        bool audioRefreshPosted;
        readonly HashSet<int> changedWaves = new HashSet<int>();

        /// <summary>Whether the machine is a Neo Audio: its tracks are audio tracks.</summary>
        public static bool IsClipPlayer(IMachine machine) => machine?.DLL?.Info?.Name == SessionClipHost.MachineName;

        /// <summary>Neo Audio's DLL, or null when it isn't installed (Gear\Effects).</summary>
        IMachineDLL ClipPlayerDll => buzz.MachineDLLs.Values.FirstOrDefault(d => d?.Info?.Name == SessionClipHost.MachineName);

        /// <summary>Whether audio tracks can be made (Neo Audio is installed).</summary>
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
            // Any thread: the wave is read again on the UI thread (an edit in place keeps its
            // layer and length: only this says it changed), once per burst.
            lock (changedWaves)
            {
                changedWaves.Add(index);
                if (audioRefreshPosted) return;
                audioRefreshPosted = true;
            }
            dispatcher.BeginInvoke(() =>
            {
                int[] changed;
                lock (changedWaves)
                {
                    changed = changedWaves.ToArray();
                    changedWaves.Clear();
                    audioRefreshPosted = false;
                }
                foreach (int i in changed)
                    migratedWaves.Remove(i);
                RefreshAudio();
            }, UiPriority.Background);
        }

        void ResetAudio()
        {
            assetAudio.Clear();
            migratedWaves.Clear();
            foreach (var p in audioKeys.Keys)
                if (p.SessionAudio != null) p.SessionAudio.Params = null;
            audioKeys.Clear();
            foreach (var pinned in pinnedAssets) pinned.Value.PinClip(pinned.Key, null, null);
            pinnedAssets.Clear();
        }

        // ------------------------------------------------------------------ audio tracks

        /// <summary>
        /// A new audio track: a Neo Audio connected to the master (one step of the
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
            (x, y) = MachinePlacement.Free(song.Machines, x, y);          // not on top of another machine
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
                var defaults = new TrackInput { Audio = DefaultAudioFrom() };
                Edit(doc => doc.SetInput(machine.Name, defaults));
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
        /// Imports an audio file (WAV, AIFF, FLAC, OGG, MP3; <see cref="AudioImporter"/>) as an asset of the song, converted to the song's
        /// sample rate, and puts it in a slot as an audio clip at its tempo (found from its length and transients), warped in Beats mode.
        /// On a track that isn't an audio track, a new audio track gets it (in the same scene). The clip is one undo step of the session;
        /// the asset stays in the song. Null and a reason when it can't.
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
                error = $"The {SessionClipHost.MachineName} machine isn't installed (Gear\\Effects), so {Product.Name} can't play audio clips.";
                return null;
            }
            var asset = ImportAsset(path, out error);
            if (asset == null) return null;
            var (bpm, _) = AudioAnalysis.Tempo(asset.Source, Session.BeatsPerBar, out bool tempoFound);

            var machine = SequenceOf(track)?.Machine;
            if (!IsClipPlayer(machine))
            {
                machine = NewAudioTrack(asset.Name);
                track = FirstTrackOf(machine);
                if (machine == null || track < 0)
                {
                    error = "Couldn't make an audio track.";
                    return null;
                }
            }
            // The tempo as found (a loop's bars exactly): rounding it would make the clip a tick longer.
            // A file with no tempo to find plays at its own speed (warp Off, at the song's tempo so it is as long as it sounds) until one is set.
            var settings = tempoFound
                ? new SessionDocument.AudioSettings { Asset = asset.Id, Bpm = bpm }
                : new SessionDocument.AudioSettings { Asset = asset.Id, Bpm = Math.Clamp(buzz.BPM, 1, 999), Warp = "Off" };
            return AddAudioClip(track, scene, machine, asset.Name, settings);
        }

        /// <summary>Decodes a file into the song's audio store at the song's sample rate; null and a reason when it can't.</summary>
        public AudioAsset ImportAsset(string path, out string error)
        {
            error = null;
            try
            {
                var asset = AudioImporter.Import(path, SampleRate);
                if (asset.Frames == 0) throw new InvalidDataException("it holds no audio");
                asset.Recipe = new FileRecipe(path, SampleRate);         // how a lost cache file is made again
                return Assets.Add(asset);
            }
            catch (Exception e)
            {
                error = $"Couldn't read {System.IO.Path.GetFileName(path)}: {e.Message}";
                return null;
            }
        }

        /// <summary>The longest audio file a clip takes.</summary>
        public const int MaxImportMinutes = AudioImporter.MaxMinutes;

        /// <summary>
        /// Makes an audio clip of an asset (or of a wave, which is migrated to an asset first) in a slot (undoable); the slot's clip, if
        /// any, makes way (its pattern stays).
        /// </summary>
        internal IPattern AddAudioClip(int track, int scene, IMachine machine, string name, SessionDocument.AudioSettings audio)
        {
            var source = ClipAudioOf(audio)?.Source;
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

        /// <summary>
        /// A pattern for an arrangement audio clip (NA2): the audio's length, its settings, in no slot. Part of the caller's undo group; null when
        /// the audio is missing.
        /// </summary>
        internal IPattern CreateAudioPattern(IMachine machine, string name, SessionDocument.AudioSettings audio, SessionDocument.ClipSettings settings = null)
        {
            var source = ClipAudioOf(audio)?.Source;
            if (source == null || machine == null) return null;
            int length = AudioClipLength(source, audio);
            var create = new SessionCreatePatternAction(this, machine, string.IsNullOrWhiteSpace(name) ? "Audio" : name.Trim(), length);
            UndoStack.Do(create);
            var created = create.Created;
            if (created == null) return null;
            settings = settings?.Clone() ?? new SessionDocument.ClipSettings();
            settings.Audio = audio.Clone();
            SetClipSettings(created, settings);
            return created;
        }

        /// <summary>What a view needs to draw an audio clip's waveform: the asset (and its peaks), the map from the clip's beats to the audio, and whether it plays backwards. Null when it isn't an audio clip or its audio is gone.</summary>
        internal AudioClipView ViewOf(IPattern pattern)
        {
            var audio = AudioOf(pattern);
            var w = audio == null ? null : ClipAudioOf(audio);
            if (w == null) return null;
            var map = MapOf(w.Source, audio);
            return new AudioClipView(w.Asset, w.Source, map, map.BeatAt(w.Source.Length), audio);
        }

        /// <summary>How the pattern plays now (null when it isn't an audio clip): offline renders play it with a voice of their own.</summary>
        internal AudioClipParams ParamsOfPattern(IPattern pattern) => (pattern as PatternCore)?.SessionAudio?.Params;

        /// <summary>The song's sample rate (what an offline render makes).</summary>
        internal int RenderRate => SampleRate;

        internal AudioAsset AddAsset(AudioAsset asset) => Assets.Add(asset);

        /// <summary>
        /// A clip's length in ticks: its audio's beats, rounded up to a tick, but to the nearest
        /// when that is within a fiftieth (a loop a sample longer than its bars is its bars).
        /// </summary>
        int AudioClipLength(AudioSource source, SessionDocument.AudioSettings audio) =>
            ClipTicks(MapOf(source, audio).BeatAt(source.Length), buzz.TPB);

        internal static int ClipTicks(double beats, int ticksPerBeat)
        {
            double ticks = beats * Math.Max(1, ticksPerBeat);
            double nearest = Math.Round(ticks);
            int n = Math.Abs(ticks - nearest) < 0.02 ? (int)nearest : (int)Math.Ceiling(ticks);
            return Math.Clamp(n, 1, 1 << 20);
        }

        /// <summary>A wave slot to fill (a .bmx save of audio clips): empty, and not one in <paramref name="taken"/>.</summary>
        int FreeWave(ICollection<int> taken = null)
        {
            var waves = song.Wavetable?.Waves;
            if (waves == null) return -1;
            for (int i = 0; i < waves.Count; i++)
                if ((waves[i] == null || waves[i].Layers.Count == 0) && (taken == null || !taken.Contains(i))) return i;
            return -1;
        }

        void ClearWave(int index)
        {
            try { song.Wavetable.LoadWave(index, null, null, false); }
            catch (Exception) { }
            migratedWaves.Remove(index);
        }

        // ------------------------------------------------------------------ .bmx and .bmxml

        /// <summary>
        /// Writes the document for a format that has no audio store: each audio clip names a wave of the wavetable. An asset migrated from a wave
        /// uses that wave while it is as it was; the others go into free slots (temporary: the song's wavetable is put back after the file
        /// is written, <see cref="SaveSongCore.RunCleanup"/>). An asset with no slot left is listed (the export's loss report) and its clips
        /// stay silent when the file is opened again. The live document is not changed.
        /// </summary>
        void SaveDocumentWithWaves(SaveSongCore ss, Stream stream)
        {
            var waves = song.Wavetable?.Waves;
            var placed = new Dictionary<string, int>(StringComparer.Ordinal);
            var taken = new HashSet<int>();
            var changed = new List<(SessionDocument.AudioSettings Audio, int Wave)>();
            foreach (var c in document.Clips.Values)
            {
                var a = c.Audio;
                if (a == null) continue;
                var asset = Assets.Get(EnsureAsset(a));
                if (asset == null) continue;
                if (!placed.TryGetValue(asset.Id, out int w))
                {
                    w = -1;
                    int origin = asset.OriginWave;
                    if (origin >= 0 && waves != null && origin < waves.Count && waves[origin]?.Layers.FirstOrDefault() is { } layer && layer.SampleCount == asset.Frames)
                        w = origin;
                    else
                    {
                        w = FreeWave(taken);
                        if (w >= 0 && !ss.DryRun)
                        {
                            if (!FillWave(w, asset)) w = -1;
                            else { int slot = w; ss.Cleanup.Add(() => ClearWave(slot)); }
                        }
                    }
                    placed[asset.Id] = w;
                    if (w >= 0) { taken.Add(w); ss.AudioWaves++; }
                    else ss.AudioLosses.Add(string.IsNullOrEmpty(asset.Name) ? asset.Id : asset.Name);
                }
                if (w >= 0) changed.Add((a, a.Wave));
                if (w >= 0) a.Wave = w;
            }
            try
            {
                document.Save(stream);
            }
            finally
            {
                // Put every clip's wave back (changed holds each clip's own).
                foreach (var (a, wave) in changed) a.Wave = wave;
            }
        }

        /// <summary>An asset as a wave of the wavetable (float, mono or stereo); false if it can't be made.</summary>
        bool FillWave(int index, AudioAsset asset)
        {
            var source = asset.Source;
            bool stereo = asset.Channels > 1;
            song.Wavetable.AllocateWave(index, asset.SourcePath ?? "", asset.Name ?? "", source.Length, WaveFormat.Float32, stereo, 65, false, false);
            var layer = song.Wavetable.Waves[index]?.Layers.FirstOrDefault();
            if (layer == null) return false;
            layer.SampleRate = source.SampleRate;
            var (l, r) = source.ToArrays();                 // exact: the cache file's audio too (neo-audio.md, NA1b)
            layer.SetDataAsFloat(l, 0, 1, 0, 0, source.Length);
            if (stereo) layer.SetDataAsFloat(r, 0, 1, 1, 0, source.Length);
            layer.InvalidateData();
            return true;
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
            audio = audio.Clone();
            if (string.IsNullOrEmpty(audio.Asset)) audio.Asset = settings.Audio.Asset;       // a caller that names only a wave or nothing keeps the clip's asset
            var source = ClipAudioOf(audio)?.Source;
            // Only a new tempo or new markers make it another length: a length set by hand stays through a gain change.
            bool timing = audio.Bpm != settings.Audio.Bpm || audio.Asset != settings.Audio.Asset || audio.Wave != settings.Audio.Wave ||
                          !(audio.Markers ?? new List<(double, double)>()).SequenceEqual(settings.Audio.Markers ?? new List<(double, double)>());
            UndoStack.BeginActionGroup();
            try
            {
                if (source != null && timing)
                {
                    int length = AudioClipLength(source, audio);
                    if (length != pattern.Length)
                        UndoStack.Do(new SessionPatternLengthAction(this, pattern, length));
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
            var source = audio == null ? null : ClipAudioOf(audio)?.Source;
            if (source == null) return 0;
            var (bpm, _) = AudioAnalysis.Tempo(source, Session.BeatsPerBar);
            audio.Bpm = bpm;
            audio.Markers.Clear();
            SetAudio(pattern, audio);
            return audio.Bpm;
        }

        /// <summary>
        /// Makes a change to an audio clip (NA3): the one place the agent's audio_clip_set and the studio's Audio page both go through, as one undo
        /// step. Returns why it can't be made, or null. The pattern's own loop and region are the Session slot's; an arrangement clip's are the clip's
        /// (<see cref="ClipEditor"/>). <paramref name="label"/> names the history step.
        /// </summary>
        internal string EditAudio(IPattern pattern, AudioClipEdit edit, ClipEditor editor, string label = null)
        {
            if (pattern?.Machine == null || edit == null) return "No clip.";
            if (!IsClipPlayer(pattern.Machine) || ClipOf(pattern)?.Audio == null) return $"\"{pattern.Name}\" of {pattern.Machine.Name} is not an audio clip (audio_import makes one).";
            string why = edit.Check();
            if (why != null) return why;
            string result = null;
            UndoStack.BeginActionGroup();
            try
            {
                if (label != null) UndoStack.Label(label);
                var settings = ClipOf(pattern).Clone();
                edit.ApplyTo(settings);
                SetAudio(pattern, settings.Audio);
                SetClipSettings(pattern, settings);
                if (edit.DetectTempo) DetectTempo(pattern);
                if (edit.Reverse is bool reverse && reverse != AudioOf(pattern).Reverse)
                    result = (editor ?? new ClipEditor(buzz)).ReversePattern(pattern);
            }
            finally
            {
                UndoStack.EndActionGroup();
            }
            return result;
        }

        /// <summary>What the clip panel shows of an audio clip's audio: the asset's name, seconds, rate and channels; null if it is gone.</summary>
        public (string Wave, double Seconds, int Rate, int Channels, double Beats)? AudioInfo(IPattern pattern)
        {
            var audio = AudioOf(pattern);
            if (audio == null) return null;
            var w = ClipAudioOf(audio);
            if (w == null) return null;
            return (w.Asset?.Name ?? "", w.Source.Seconds, w.Source.SampleRate, w.Asset?.Channels ?? 1, MapOf(w.Source, audio).BeatAt(w.Source.Length));
        }

        /// <summary>An audio clip's audio as it plays (for the warp marker editor and tests), or null.</summary>
        internal AudioSource AudioSourceOf(IPattern pattern)
        {
            var audio = AudioOf(pattern);
            return audio == null ? null : ClipAudioOf(audio)?.Source;
        }

        /// <summary>An audio clip's transients (audio samples), found once per wave; empty if none.</summary>
        internal IReadOnlyList<double> TransientsOf(IPattern pattern)
        {
            var audio = AudioOf(pattern);
            var w = audio == null ? null : ClipAudioOf(audio);
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
            public SessionClipEngine.TakeCapture Capturing;
            /// <summary>The id the clip names until the take is done (the store aliases it to the finished asset).</summary>
            public string PendingId;
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

        /// <summary>
        /// Frames a take drops at its start so it lines up with what was heard: a device input comes back the output latency plus its own
        /// late, a track source the tap's delay, a hardware input wired through ReBuzz Audio In the output latency. None for the rest.
        /// </summary>
        long CaptureSkip(IMachine machine)
        {
            var from = TrackInputOf(machine).Audio;
            switch (from.Kind)
            {
                case AudioFromKind.Track: return TrackTap.Delay;
                case AudioFromKind.Mono:
                case AudioFromKind.Stereo:
                    return Engine.LatencyCompensation ? buzz.OutputLatencySamples + buzz.EngineInput.LatencyFrames : 0;
                case AudioFromKind.None: return 0;
                default: return Engine.LatencyCompensation && HearsHardware(machine) ? buzz.OutputLatencySamples : 0;
            }
        }

        /// <summary>Frames an audio take of this machine drops at its start so it lines up (the round trip of what it records).</summary>
        internal long CaptureSkipOf(IMachine machine) => Engine == null || InputOf(machine) == 1 ? 0 : CaptureSkip(machine);

        /// <summary>The ReBuzz Audio In machine: an audio track fed by it records a hardware input.</summary>
        const string AudioInName = "ReBuzz Audio In";

        /// <summary>Whether what the machine records comes from a hardware input (ReBuzz Audio In, through any machines).</summary>
        static bool HearsHardware(IMachine machine)
        {
            var seen = new HashSet<IMachine>();
            var next = new Stack<IMachine>();
            next.Push(machine);
            while (next.Count > 0 && seen.Count < 256)
            {
                var m = next.Pop();
                if (m == null || !seen.Add(m)) continue;
                if (m.DLL?.Info?.Name == AudioInName) return true;
                foreach (var input in m.Inputs) next.Push(input.Source);
            }
            return false;
        }

        /// <summary>Frames of audio a number of ticks lasts at a tempo.</summary>
        long FramesOf(long ticks, double bpm, int ticksPerBeat) =>
            (long)Math.Round(ticks * 60.0 * SampleRate / (Math.Max(1, bpm) * Math.Max(1, ticksPerBeat)));

        /// <summary>
        /// Records a take of an audio track (an armed Session Clip Player): a new audio clip in an
        /// empty slot, launched on the grid, recording its input (or the master, resampling) for
        /// the record length, or until stopped (then whole bars). Its audio becomes an asset of the
        /// song when the take is done; it plays from its next pass. One undo step (the clip).
        /// </summary>
        IPattern RecordAudioTake(int track, int scene, SequenceCore seq)
        {
            var machine = seq.Machine;
            StopRecording(seq);
            if (audioTakes.TryGetValue(seq, out var stopping))
            {
                // One recording until stopped, finishing its bar: it keeps what it has now.
                stopping.Capture.SetLength(Math.Min(stopping.Capture.Length, stopping.Capture.Written));
                FinishAudioTake(stopping);
            }
            string name = machine?.GetNewPatternName();
            if (name == null) return null;
            var doc = Ensure();
            int bar = BarTicks;
            int bars = doc.RecordBars;
            int ticks = (bars > 0 ? bars : TakeRoomBars) * bar;
            double bpm = buzz.BPM;
            int tpb = Math.Max(1, buzz.TPB);

            // The clip names an id the store doesn't hold yet (silent) until the take's audio becomes an asset.
            string waveName = $"{machine.Name} {name}";
            string pendingId = "take-" + Guid.NewGuid().ToString("N");
            PatternCore take;
            UndoStack.BeginActionGroup();
            try
            {
                var create = new SessionCreatePatternAction(this, machine, name, ticks);
                UndoStack.Do(create);
                take = create.Created as PatternCore;
                if (take == null)
                    return null;
                Edit(d =>
                {
                    d.SetSlot(track, scene, take.Name);
                    d.SetClip(machine.Name, take.Name, new SessionDocument.ClipSettings
                    {
                        Audio = new SessionDocument.AudioSettings { Asset = pendingId, Bpm = bpm, Warp = "Off" }
                    });
                });
            }
            finally
            {
                UndoStack.EndActionGroup();
            }

            // A hardware input comes back a round trip late: what was played to the bar heard
            // arrives the output latency after it (the input's own isn't known). The master, and
            // machines played by the engine, are in time with what it renders.
            bool master = InputOf(machine) == 1;
            long skip = master ? 0 : CaptureSkip(machine);
            long length = bars > 0 ? FramesOf(ticks, bpm, tpb) : long.MaxValue;
            var capture = new AudioCapture(SampleRate, skip, length, (long)MaxAudioTakeMinutes * 60 * SampleRate, FramesOf(tpb, bpm, tpb), TakeFile.Create(waveName, SampleRate, bpm));
            var t = new AudioTake { Track = seq, Take = take, Capture = capture, PendingId = pendingId, Name = waveName, Bpm = bpm, TicksPerBeat = tpb, UntilStopped = bars == 0 };
            t.Capturing = new SessionClipEngine.TakeCapture(take, capture, master);
            if (!master) (take.SessionAudio ??= new ClipPlayback()).Capture = capture;
            Engine.TakeCaptures = Engine.TakeCaptures.Append(t.Capturing).ToArray();
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
            if (!t.UntilStopped || t.Capture.Written == 0)
            {
                // A fixed length stopped early keeps what it has (the rest silence); a take
                // stopped before it began records nothing, as a MIDI take does.
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
                    // Its clip stays with what it has, if it is still there.
                    t.Capture.SetLength(t.Take.IsDeleted ? 0 : Math.Min(t.Capture.Length, t.Capture.Written));
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

        /// <summary>The take is over: its audio becomes an asset of the song (the clip plays it from then on).</summary>
        void FinishAudioTake(AudioTake t)
        {
            if (!audioTakes.TryGetValue(t.Track, out var current) || !ReferenceEquals(current, t)) return;
            audioTakes.Remove(t.Track);
            Engine.TakeCaptures = Engine.TakeCaptures.Where(c => !ReferenceEquals(c, t.Capturing)).ToArray();
            if (t.Take.SessionAudio != null && ReferenceEquals(t.Take.SessionAudio.Capture, t.Capture))
                t.Take.SessionAudio.Capture = null;
            long frames = Math.Min(t.Capture.Length, t.Capture.Written);
            if (frames > 0 && !t.Take.IsDeleted)
            {
                // The take's temp file is committed into the song's store as an asset, then the file goes (a crash before this leaves it to recover).
                var (left, right) = t.Capture.Collect(t.Capture.Length == long.MaxValue ? frames : t.Capture.Length);
                var asset = Assets.Add(new AudioAsset(t.Name, "", null, new AudioSource(left, right, t.Capture.SampleRate), 2));
                Assets.Alias(t.PendingId, asset.Id);
                if (t.Capture.Dropped > 0)
                    buzz.DCWriteLine($"Session: audio take {t.Name}: {t.Capture.Dropped} frames lost (the UI was too slow)", DCLogLevel.Warning);
            }
            t.Capture.Close();
            RefreshAudio();
            UpdatePollTimer();
            RecordingChanged?.Invoke();
        }

        void ResetAudioTakes()
        {
            foreach (var t in audioTakes.Values)
            {
                if (t.Take.SessionAudio != null) t.Take.SessionAudio.Capture = null;
                t.Capture.Close();
            }
            audioTakes.Clear();
            if (Engine != null) Engine.TakeCaptures = null;
        }

        /// <summary>Whether an audio take records (or waits to) on the track.</summary>
        /// <summary>NA4b: the capture of the audio take recording into this slot (its peaks draw the growing waveform), or null.</summary>
        internal AudioCapture LiveCaptureOf(ISequence track, IPattern clip) => track is SequenceCore seq && audioTakes.TryGetValue(seq, out var t) && ReferenceEquals(t.Take, clip) ? t.Capture : null;

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
            var source = w.Source;
            var map = MapOf(source, audio);
            var mode = ModeOf(audio.Warp);
            if (audio.Reverse)
            {
                // Backwards: the audio reversed once (shared by every clip that plays it so), with the map mirrored over the clip's beats.
                map = map.Mirrored(source.Length, map.BeatAt(source.Length));
                source = w.Reversed ??= source.Reversed();
            }
            double[] slices = null;
            if (mode == WarpMode.Beats)
            {
                IReadOnlyList<double> transients;
                if (!audio.PreservesTransients) transients = Array.Empty<double>();
                else if (audio.Reverse) transients = w.ReversedOnsets ??= AudioAnalysis.Onsets(source);
                else transients = w.Onsets ??= AudioAnalysis.Onsets(source);
                slices = AudioAnalysis.Slices(source, map, transients, audio.Grid);
            }
            return new AudioClipParams(source, map, mode, slices, audio.Gain, audio.Semitones)
            {
                Fades = new ClipFades(audio.FadeIn, audio.FadeOut, audio.Tail, audio.CrossIn),
            };
        }

        /// <summary>
        /// The asset a clip plays, migrating a clip an older song made (it names a wave) to one: the wave's audio is copied into the
        /// store, once per wave, and the wave stays untouched. Empty when the clip has no audio (a wave that's empty, an asset gone).
        /// </summary>
        string EnsureAsset(SessionDocument.AudioSettings audio)
        {
            if (!string.IsNullOrEmpty(audio.Asset))
            {
                var held = Assets.Get(audio.Asset);
                if (held != null) return held.Id;
                // Not in the store: a take whose audio isn't made yet, or a song saved as .bmx (the asset went into a wave, which names it).
                return MigrateWave(audio) ?? audio.Asset;
            }
            return MigrateWave(audio);
        }

        string MigrateWave(SessionDocument.AudioSettings audio)
        {
            var waves = song.Wavetable?.Waves;
            int index = audio.Wave;
            if (waves == null || index < 0 || index >= waves.Count) return null;
            var layer = waves[index]?.Layers.FirstOrDefault();
            if (layer == null || layer.SampleCount <= 0)
            {
                migratedWaves.Remove(index);
                return null;
            }
            if (migratedWaves.TryGetValue(index, out var known) && ReferenceEquals(known.Layer, layer) &&
                known.Count == layer.SampleCount && known.Rate == layer.SampleRate && known.Channels == layer.ChannelCount && Assets.Get(known.AssetId) != null)
                return known.AssetId;
            int n = layer.SampleCount;
            var left = new float[n];
            layer.GetDataAsFloat(left, 0, 1, 0, 0, n);
            var right = left;
            if (layer.ChannelCount > 1)
            {
                right = new float[n];
                layer.GetDataAsFloat(right, 0, 1, 1, 0, n);
            }
            var asset = Assets.Add(new AudioAsset(waves[index].Name ?? "", layer.Path, null,
                new AudioSource(left, right, layer.SampleRate > 0 ? layer.SampleRate : 44100), layer.ChannelCount > 1 ? 2 : 1, index));
            migratedWaves[index] = new MigratedWave { Layer = layer, Count = n, Rate = layer.SampleRate, Channels = layer.ChannelCount, AssetId = asset.Id };
            return asset.Id;
        }

        /// <summary>The audio a clip plays (its asset's), or null; sets <see cref="SessionDocument.AudioSettings.Asset"/> on a clip that named a wave.</summary>
        WaveAudio ClipAudioOf(SessionDocument.AudioSettings audio)
        {
            if (audio == null) return null;
            string id = EnsureAsset(audio);
            var asset = id == null ? null : Assets.Get(id);
            if (asset == null) return null;
            if (audio.Asset != asset.Id) audio.Asset = asset.Id;
            if (assetAudio.TryGetValue(asset.Id, out var known) && ReferenceEquals(known.Asset, asset)) return known;
            var w = new WaveAudio { Asset = asset, Source = asset.Reader.InMemory ?? asset.Source, Version = ++waveVersion };
            assetAudio[asset.Id] = w;
            return w;
        }

        /// <summary>The assets the layout's audio clips play (what a .nbl embeds).</summary>
        internal IReadOnlyList<AudioAsset> ReferencedAssets()
        {
            var list = new List<AudioAsset>();
            var doc = document;
            if (doc == null) return list;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in doc.Clips.Values)
            {
                if (c.Audio == null) continue;
                var asset = Assets.Get(EnsureAsset(c.Audio));
                if (asset != null && seen.Add(asset.Id)) list.Add(asset);
            }
            return list.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
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
                foreach (var entry in doc.Clips)
                {
                    var audio = entry.Value.Audio;
                    if (audio == null || !machines.TryGetValue(entry.Key.Machine, out var machine)) continue;
                    if (machine.Patterns.FirstOrDefault(p => p.Name == entry.Key.Pattern) is not PatternCore pattern) continue;
                    live.Add(pattern);
                    var w = ClipAudioOf(audio);                 // migrates a wave to an asset, and a take's placeholder id to its asset's
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
                    PinClip(pattern, w?.Asset, playback.Params, entry.Value);
                    audioKeys[pattern] = key;
                }
            }
            foreach (var gone in audioKeys.Keys.Where(p => !live.Contains(p)).ToList())
            {
                if (gone.SessionAudio != null) gone.SessionAudio.Params = null;
                PinClip(gone, null, null, null);
                audioKeys.Remove(gone);
            }
        }

        readonly Dictionary<PatternCore, AudioAsset> pinnedAssets = new Dictionary<PatternCore, AudioAsset>();

        /// <summary>
        /// A streamed asset keeps the audio a clip may start from in memory (neo-audio.md, NA1b): the start of the audio (the asset's own pin), the
        /// clip's start marker and loop start through its warp map, and each warp marker, each for the head's length, so a launch is instant.
        /// </summary>
        void PinClip(PatternCore pattern, AudioAsset asset, AudioClipParams p, SessionDocument.ClipSettings settings)
        {
            if (pinnedAssets.TryGetValue(pattern, out var old) && !ReferenceEquals(old, asset))
            {
                old.PinClip(pattern, null, null);
                pinnedAssets.Remove(pattern);
            }
            if (asset == null || p == null || !StreamingPolicy.ShouldStream(asset.Frames, asset.Channels)) return;
            long head = Math.Max(1024, (long)(StreamingPolicy.HeadMilliseconds / 1000.0 * p.Source.SampleRate));
            var ranges = new List<(long From, long To)>();
            void At(double sample)
            {
                if (!double.IsFinite(sample) || Math.Abs(sample) > 1e12) return;
                ranges.Add(((long)sample - 4096, (long)sample + head));
            }
            double tpb = Math.Max(1, buzz.TPB);
            At(p.Map.SampleAt(0));
            if (settings != null)
            {
                At(p.Map.SampleAt(settings.Start / tpb));
                At(p.Map.SampleAt(settings.LoopStart / tpb));
            }
            foreach (var m in p.Map.Markers.Take(48)) At(m.Sample);
            asset.PinClip(pattern, p.Source, ranges);
            pinnedAssets[pattern] = asset;
        }
    }

    /// <summary>An audio clip as the arrangement draws it (NA2): see <see cref="SessionLayout.ViewOf"/>.</summary>
    internal sealed record AudioClipView(AudioAsset Asset, AudioSource Source, WarpMap Map, double EndBeat, SessionDocument.AudioSettings Audio)
    {
        public bool Reverse => Audio.Reverse;

        /// <summary>The audio sample (of the audio as the asset has it) that sounds at a beat of the clip, for any warp: backwards when reversed.</summary>
        public double SampleAt(double beat) => Audio.Reverse ? Map.SampleAt(EndBeat - beat) : Map.SampleAt(beat);

        /// <summary>The clip's beat of each warp marker (the markers are in the audio's own beats, so reversed they are mirrored).</summary>
        public IEnumerable<double> MarkerBeats() =>
            (Audio.Markers ?? new List<(double, double)>()).Select(m => Audio.Reverse ? EndBeat - m.Beat : m.Beat);
    }

    /// <summary>
    /// A pattern's length (an audio clip's following its tempo), as a session undo step that
    /// holds the pattern itself, not its name: renames and undone deletions don't lose it.
    /// </summary>
    [HistoryName("Set clip length")]
    internal sealed class SessionPatternLengthAction : IAction
    {
        readonly SessionLayout layout;
        readonly IPattern pattern;
        readonly int from, to;

        public SessionPatternLengthAction(SessionLayout layout, IPattern pattern, int length)
        {
            this.layout = layout;
            this.pattern = pattern;
            from = pattern.Length;
            to = length;
        }

        public void Do() => Set(to);

        public void Undo() => Set(from);

        void Set(int length)
        {
            var p = layout.Current(pattern);
            if (p?.Machine == null || !layout.IsInSong(p.Machine) || !p.Machine.Patterns.Contains(p)) return;
            p.Length = length;
        }
    }
}
