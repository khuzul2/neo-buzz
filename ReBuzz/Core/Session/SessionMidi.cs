using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Plays the Session View from MIDI (fork addition, docs/specs/session-view.md 0.2): applies
    /// the active <see cref="SessionMidiMap"/> to incoming notes and CCs, and runs MIDI learn.
    ///
    /// The user's map is stored in the registry and applies to every song; a song can carry its
    /// own map (<see cref="SessionDocument.SongMidi"/>), which then replaces the user's.
    /// Controls the map uses don't reach machines, so pads launching clips don't also play notes.
    ///
    /// Threading (0.4.1): ReBuzzCore.SendMIDIInput calls <see cref="OnMidiThread"/> on the MIDI
    /// driver's thread. Launches, stops and play go straight to the session engine (which takes
    /// requests from any thread), through a snapshot of what each slot, scene and track holds
    /// (<see cref="Targets"/>) that the UI thread rebuilds whenever the layout changes; so a busy
    /// UI never delays a launch. Learn, quantization and moving the note grid edit UI-owned
    /// state and are posted to the UI thread.
    /// </summary>
    internal sealed class SessionMidi
    {
        const string RegistryPath = "Session";
        const string RegistryKey = "MidiMap";

        static readonly LaunchQuantization[] QuantizationCycle =
        {
            LaunchQuantization.None, LaunchQuantization.Beat, LaunchQuantization.Bar,
            LaunchQuantization.TwoBars, LaunchQuantization.FourBars, LaunchQuantization.EightBars
        };

        readonly ReBuzzCore buzz;
        readonly SessionLayout layout;
        readonly IRegistryEx registry;
        SessionMidiMap userMap;
        volatile SessionMidiMap active;    // read on the MIDI thread; replaced, never modified
        volatile Targets targets = Targets.Empty;   // likewise

        (SessionTarget Target, int Track, int Scene)? learning;
        volatile bool isLearning;          // learning, for the MIDI thread

        /// <summary>
        /// What each slot, scene and track stop launches, resolved on the UI thread so the MIDI
        /// thread needs no layout lookups. Immutable; replaced as a whole.
        /// </summary>
        internal sealed class Targets
        {
            public static readonly Targets Empty = new Targets(
                Array.Empty<ISequence>(), Array.Empty<IPattern[]>(), Array.Empty<SessionDocument.ClipSettings[]>(),
                Array.Empty<(ISequence, IPattern, SessionDocument.ClipSettings)[]>(), Array.Empty<SceneOptions>());

            public Targets(ISequence[] tracks, IPattern[][] slots, SessionDocument.ClipSettings[][] settings,
                           (ISequence, IPattern, SessionDocument.ClipSettings)[][] scenes, SceneOptions[] sceneOptions)
            {
                Tracks = tracks;
                Slots = slots;
                Settings = settings;
                Scenes = scenes;
                SceneSettings = sceneOptions;
            }

            public ISequence[] Tracks { get; }
            /// <summary>[track][scene]; null for an empty slot.</summary>
            public IPattern[][] Slots { get; }
            /// <summary>[track][scene]: clip settings of the slot's pattern (0.5); copies, never modified.</summary>
            public SessionDocument.ClipSettings[][] Settings { get; }
            /// <summary>Filled slots of each scene, with their clip settings.</summary>
            public (ISequence, IPattern, SessionDocument.ClipSettings)[][] Scenes { get; }
            /// <summary>Tempo and beats per bar of each scene (0.5).</summary>
            public SceneOptions[] SceneSettings { get; }

            public ISequence Track(int track) => track >= 0 && track < Tracks.Length ? Tracks[track] : null;

            public IPattern Slot(int track, int scene) =>
                track >= 0 && track < Slots.Length && scene >= 0 && scene < Slots[track].Length ? Slots[track][scene] : null;

            public SessionDocument.ClipSettings SettingsOf(int track, int scene) =>
                track >= 0 && track < Settings.Length && scene >= 0 && scene < Settings[track].Length ? Settings[track][scene] : null;

            public (ISequence, IPattern, SessionDocument.ClipSettings)[] Scene(int scene) =>
                scene >= 0 && scene < Scenes.Length ? Scenes[scene] : Array.Empty<(ISequence, IPattern, SessionDocument.ClipSettings)>();

            public SceneOptions OptionsOfScene(int scene) =>
                scene >= 0 && scene < SceneSettings.Length ? SceneSettings[scene] : null;
        }

        /// <summary>Learn mode started, completed or was cancelled; or the mapping changed.</summary>
        public event Action Changed;

        internal SessionMidi(ReBuzzCore buzz, SessionLayout layout, IRegistryEx registry)
        {
            this.buzz = buzz;
            this.layout = layout;
            this.registry = registry;
            userMap = SessionMidiMap.Parse(ReadUserMap());
            UpdateActive();
            RebuildTargets();

            layout.Changed += () =>
            {
                UpdateActive();
                RebuildTargets();
            };
        }

        /// <summary>UI thread: resolves every slot, scene and track for the MIDI thread.</summary>
        internal void RebuildTargets()
        {
            var doc = layout.Document;
            if (doc == null)
            {
                targets = Targets.Empty;
                return;
            }
            int trackCount = doc.Tracks.Count;
            int sceneCount = doc.Scenes.Count;
            var tracks = new ISequence[trackCount];
            var slots = new IPattern[trackCount][];
            var settings = new SessionDocument.ClipSettings[trackCount][];
            for (int t = 0; t < trackCount; t++)
            {
                tracks[t] = layout.SequenceOf(t);
                slots[t] = new IPattern[sceneCount];
                settings[t] = new SessionDocument.ClipSettings[sceneCount];
                for (int s = 0; s < sceneCount; s++)
                {
                    slots[t][s] = tracks[t] == null ? null : layout.PatternAt(t, s);
                    if (slots[t][s] != null)
                        settings[t][s] = layout.ClipOf(slots[t][s]);
                }
            }
            var scenes = new (ISequence, IPattern, SessionDocument.ClipSettings)[sceneCount][];
            var sceneOptions = new SceneOptions[sceneCount];
            for (int s = 0; s < sceneCount; s++)
            {
                var list = new List<(ISequence, IPattern, SessionDocument.ClipSettings)>();
                for (int t = 0; t < trackCount; t++)
                    if (tracks[t] != null && slots[t][s] != null)
                        list.Add((tracks[t], slots[t][s], settings[t][s]));
                scenes[s] = list.ToArray();
                sceneOptions[s] = SessionLayout.SceneOptionsOf(doc.Scenes[s]);
            }
            targets = new Targets(tracks, slots, settings, scenes, sceneOptions);
        }

        string ReadUserMap()
        {
            try
            {
                return registry?.Read(RegistryKey, "", RegistryPath) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>The map in use: the song's if it has one, else the user's.</summary>
        public SessionMidiMap ActiveMap => active;

        /// <summary>Whether the current song stores its own map.</summary>
        public bool UsesSongMap => layout.Document?.SongMidi != null;

        public bool IsLearning => learning != null;
        public (SessionTarget Target, int Track, int Scene)? LearnTarget => learning;

        /// <summary>MIDI thread: whether the message is for the session only.</summary>
        public bool Consumes(int data)
        {
            var map = active;
            if (map == null || map.IsEmpty) return false;
            return MidiControl.TryDecode(data, out var control, out _, out _) && map.Handles(control);
        }

        /// <summary>
        /// MIDI thread: plays a mapped control (launches and stops go to the engine from here;
        /// UI-owned changes are posted) and feeds learn. Returns whether the message is for the
        /// session only, i.e. must not reach machines.
        /// </summary>
        internal bool OnMidiThread(int data)
        {
            if (!MidiControl.TryDecode(data, out var control, out int value, out bool isPress)) return false;
            var map = active;
            bool consumed = map != null && !map.IsEmpty && map.Handles(control);

            if (isLearning)
            {
                buzz.dispatcher.BeginInvoke(() => Learn(control, isPress));
                return consumed;
            }

            if (map == null || !map.TryResolve(control, out var target, out int track, out int scene)) return consumed;

            if (target == SessionTarget.Quantization)
            {
                buzz.dispatcher.BeginInvoke(() => SetQuantizationFromMidi(control, value, isPress));
                return consumed;
            }
            // Velocity (0.5): a note's; a CC launches at full velocity.
            int velocity = control.Type == MidiControlType.Note ? value : 127;
            if (isPress)
                ExecuteNow(target, track, scene, targets, velocity);
            else if (target == SessionTarget.Slot)
                ReleaseNow(track, scene, targets);
            return consumed;
        }

        /// <summary>
        /// Any thread: what a pressed target does. Launch, stop and play use the engine (thread-safe)
        /// and the resolved <paramref name="t"/>; everything else is posted to the UI thread.
        /// </summary>
        void ExecuteNow(SessionTarget target, int track, int scene, Targets t, int velocity)
        {
            var session = buzz.Session;
            switch (target)
            {
                case SessionTarget.Slot:
                    {
                        var seq = t.Track(track);
                        if (seq == null) return;
                        var pattern = t.Slot(track, scene);
                        if (pattern != null)
                            session.Launch(seq, pattern, SessionLayout.ToOptions(t.SettingsOf(track, scene), velocity));
                        else
                            session.Stop(seq);
                        break;
                    }
                case SessionTarget.Scene:
                    {
                        var slots = t.Scene(scene);
                        var options = t.OptionsOfScene(scene);
                        if (slots.Length > 0 || options != null)
                        {
                            var launches = new (ISequence, IPattern, LaunchOptions)[slots.Length];
                            for (int i = 0; i < slots.Length; i++)
                                launches[i] = (slots[i].Item1, slots[i].Item2, SessionLayout.ToOptions(slots[i].Item3, velocity));
                            session.LaunchScene(launches, options ?? new SceneOptions());
                        }
                        break;
                    }
                case SessionTarget.TrackStop:
                    if (t.Track(track) is ISequence s)
                        session.Stop(s);
                    break;
                case SessionTarget.StopAll:
                    session.StopAll();
                    break;
                case SessionTarget.Transport:
                    // Starting only arms the next tick; stopping stops machines and raises UI
                    // events, so it goes through the UI thread.
                    if (!buzz.PlayRequested)
                        buzz.Playing = true;
                    else
                        buzz.dispatcher.BeginInvoke(() => buzz.Playing = false);
                    break;
                default:
                    buzz.dispatcher.BeginInvoke(() => Execute(target, track, scene));
                    break;
            }
        }

        /// <summary>Any thread: the release of a slot's control (Gate and Repeat clips, 0.5).</summary>
        void ReleaseNow(int track, int scene, Targets t)
        {
            var seq = t.Track(track);
            var pattern = t.Slot(track, scene);
            if (seq == null || pattern == null) return;
            var settings = t.SettingsOf(track, scene);
            if (settings == null || (settings.Mode != nameof(ClipLaunchMode.Gate) && settings.Mode != nameof(ClipLaunchMode.Repeat))) return;
            buzz.Session.Release(seq, pattern, SessionLayout.ToOptions(settings, 127));
        }

        // ------------------------------------------------------------------ learn and edit

        /// <summary>The next note or CC that arrives gets bound to this target.</summary>
        public void BeginLearn(SessionTarget target, int track, int scene)
        {
            learning = (target, track, scene);
            isLearning = true;
            Changed?.Invoke();
        }

        public void CancelLearn()
        {
            if (learning == null) return;
            learning = null;
            isLearning = false;
            Changed?.Invoke();
        }

        public MidiBinding? FindBinding(SessionTarget target, int track, int scene) => active?.Find(target, track, scene);

        public void Unbind(SessionTarget target, int track, int scene) => Modify(m => m.Unbind(target, track, scene));

        public void SetGrid(NoteGrid grid) => Modify(m => m.Grid = grid?.Clone());

        public void ClearAll() => Modify(m => { m.Bindings.Clear(); m.Grid = null; });

        /// <summary>
        /// Stores the mapping with the song (starting from the user's) or goes back to the user's
        /// mapping, dropping the song's.
        /// </summary>
        public void SetUseSongMap(bool useSongMap)
        {
            if (useSongMap == UsesSongMap) return;
            layout.SetSongMidi(useSongMap ? userMap.Clone() : null);
            UpdateActive();
        }

        /// <summary>Text for tooltips, e.g. "MIDI: Note C1, ch 1"; null when unmapped.</summary>
        public string Describe(SessionTarget target, int track, int scene)
        {
            var map = active;
            if (map == null) return null;
            if (map.Find(target, track, scene) is MidiBinding b)
                return "MIDI: " + b.Control;
            if (target == SessionTarget.Slot && map.Grid != null)
            {
                var g = map.Grid;
                for (int note = g.FirstNote; note < g.FirstNote + g.Columns * g.Rows && note < 128; note++)
                {
                    var control = new MidiControl(MidiControlType.Note, Math.Max(0, g.Channel), note);
                    if (g.TryMap(control, out var t, out var s) && t == track && s == scene)
                        return "MIDI (note grid): Note " + MidiControl.NoteName(note) + (g.Channel < 0 ? ", any channel" : ", ch " + (g.Channel + 1));
                }
            }
            return null;
        }

        void Modify(Action<SessionMidiMap> change)
        {
            var song = layout.Document?.SongMidi;
            var copy = (song ?? userMap).Clone();
            change(copy);
            if (song != null)
            {
                layout.SetSongMidi(copy);
            }
            else
            {
                userMap = copy;
                try
                {
                    registry?.Write(RegistryKey, copy.ToXml().ToString(System.Xml.Linq.SaveOptions.DisableFormatting), RegistryPath);
                }
                catch (Exception e)
                {
                    buzz.DCWriteLine("Session View: could not save the MIDI mapping: " + e.Message, DCLogLevel.Warning);
                }
            }
            UpdateActive();
        }

        void UpdateActive()
        {
            var map = layout.Document?.SongMidi ?? userMap;
            if (ReferenceEquals(map, active)) return;
            active = map;
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ playing

        /// <summary>UI thread: binds the control to the target being learnt.</summary>
        void Learn(MidiControl control, bool isPress)
        {
            if (learning is not { } learn) return;
            if (!isPress) return;     // learn on press, so the release of the previous key doesn't count
            learning = null;
            isLearning = false;
            Modify(m => m.Bind(control, learn.Target, learn.Track, learn.Scene));
        }

        /// <summary>UI thread: a CC sets the quantization by range, a note steps through it.</summary>
        void SetQuantizationFromMidi(MidiControl control, int value, bool isPress)
        {
            if (control.Type == MidiControlType.ControlChange)
                layout.SetLaunchQuantization(QuantizationCycle[Math.Min(QuantizationCycle.Length - 1, value * QuantizationCycle.Length / 128)]);
            else if (isPress)
                layout.SetLaunchQuantization(QuantizationCycle[(Array.IndexOf(QuantizationCycle, buzz.Session.LaunchQuantization) + 1) % QuantizationCycle.Length]);
        }

        /// <summary>UI thread: does what a target does when its control is pressed.</summary>
        internal void Execute(SessionTarget target, int track, int scene)
        {
            var session = buzz.Session;
            switch (target)
            {
                case SessionTarget.Slot:
                    {
                        var seq = layout.SequenceOf(track);
                        if (seq == null) return;
                        var pattern = layout.PatternAt(track, scene);
                        if (pattern != null)
                            layout.LaunchClip(seq, pattern);
                        else
                            session.Stop(seq);
                        break;
                    }
                case SessionTarget.Scene:
                    layout.LaunchScene(scene);
                    break;
                case SessionTarget.TrackStop:
                    if (layout.SequenceOf(track) is ISequence s)
                        session.Stop(s);
                    break;
                case SessionTarget.StopAll:
                    session.StopAll();
                    break;
                case SessionTarget.Transport:
                    buzz.Playing = !buzz.PlayRequested;
                    break;
                case SessionTarget.Quantization:
                    layout.SetLaunchQuantization(QuantizationCycle[(Array.IndexOf(QuantizationCycle, session.LaunchQuantization) + 1) % QuantizationCycle.Length]);
                    break;
                case SessionTarget.GridUp: MoveGrid(0, -1); break;
                case SessionTarget.GridDown: MoveGrid(0, 1); break;
                case SessionTarget.GridLeft: MoveGrid(-1, 0); break;
                case SessionTarget.GridRight: MoveGrid(1, 0); break;
            }
        }

        /// <summary>Moves the note grid over the session (like Live's session ring), staying inside it.</summary>
        public void MoveGrid(int tracks, int scenes)
        {
            var grid = active?.Grid;
            if (grid == null) return;
            int maxTrack = Math.Max(0, layout.TrackCount - 1);
            int maxScene = Math.Max(0, layout.SceneCount - 1);
            int track = Math.Clamp(grid.OriginTrack + tracks, 0, maxTrack);
            int scene = Math.Clamp(grid.OriginScene + scenes, 0, maxScene);
            if (track == grid.OriginTrack && scene == grid.OriginScene) return;
            Modify(m =>
            {
                if (m.Grid == null) return;
                m.Grid.OriginTrack = track;
                m.Grid.OriginScene = scene;
            });
        }
    }
}
