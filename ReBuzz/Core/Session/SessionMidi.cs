using BuzzGUI.Interfaces;
using System;
using System.Threading;
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
    /// (the layout's <see cref="LaunchMap"/>) that the UI thread rebuilds whenever the layout changes; so a busy
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

        (SessionTarget Target, int Track, int Scene)? learning;
        volatile bool isLearning;          // learning, for the MIDI thread

        /// <summary>Learn mode started, completed or was cancelled; or the mapping changed.</summary>
        public event Action Changed;

        internal SessionMidi(ReBuzzCore buzz, SessionLayout layout, IRegistryEx registry)
        {
            this.buzz = buzz;
            this.layout = layout;
            this.registry = registry;
            userMap = SessionMidiMap.Parse(ReadUserMap());
            UpdateActive();

            // What slots and scenes launch comes from the layout's launch map (0.6), rebuilt with it.
            layout.Changed += UpdateActive;
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

            // 0.6.3: a release does what its press did, whatever changed in between (a key learnt
            // while held, the map edited, the grid moved): a note that went to machines ends
            // there, a held Gate pad releases the clip it launched.
            int key = HeldKey(control);
            if (!isPress && Interlocked.Exchange(ref held[key], null) is Held pressed)
            {
                if (!pressed.Session) return false;
                if (pressed.Clip != null && !buzz.SongLoading)
                    buzz.Session.Release(pressed.Track, pressed.Clip, pressed.Options);
                if (pressed.Nudge)
                    (buzz.Session as SessionCore)?.Nudge(0);
                return true;
            }

            var map = active;
            bool consumed = map != null && !map.IsEmpty && map.Handles(control);

            if (isLearning)
            {
                if (isPress) Remember(control, consumed ? Consumed : PassedOn);
                buzz.dispatcher.BeginInvoke(() => Learn(control, isPress));
                return consumed;
            }

            if (map == null || !map.TryResolve(control, out var target, out int track, out int scene))
            {
                if (isPress) Remember(control, consumed ? Consumed : PassedOn);
                return consumed;
            }
            if (isPress) Remember(control, Consumed);
            if (buzz.SongLoading) return consumed;      // 0.6.1: nothing launches or starts while a song closes or loads

            if (target == SessionTarget.Quantization)
            {
                buzz.dispatcher.BeginInvoke(() => SetQuantizationFromMidi(control, value, isPress));
                return consumed;
            }
            if (target == SessionTarget.Crossfader)
            {
                // 0.6: a CC across its range moves it, at once, from here; the song keeps the position.
                if (control.Type == MidiControlType.ControlChange)
                {
                    double position = value / 127.0;
                    (buzz.Session as SessionCore)?.SetCrossfader(position);
                    buzz.dispatcher.BeginInvoke(() => layout.NoteCrossfaderPosition(position));
                }
                return consumed;
            }
            if (target == SessionTarget.NudgeDown || target == SessionTarget.NudgeUp)
            {
                // 0.6: while held.
                if (isPress) Remember(control, NudgeHeld);
                (buzz.Session as SessionCore)?.Nudge(isPress ? (target == SessionTarget.NudgeUp ? 1 : -1) : 0);
                return consumed;
            }
            // Velocity (0.5): a note's; a CC launches at full velocity.
            int velocity = control.Type == MidiControlType.Note ? value : 127;
            var launchMap = layout.LaunchMap;
            if (isPress)
            {
                if (target == SessionTarget.Slot && launchMap.Track(track) is ISequence seq && launchMap.Slot(track, scene) is LaunchMap.SlotEntry slot)
                    Remember(control, new Held { Session = true, Track = seq, Clip = slot.Pattern, Options = slot.Options });
                ExecuteNow(target, track, scene, launchMap, velocity);
            }
            else if (target == SessionTarget.Slot)
                ReleaseNow(track, scene, launchMap);
            return consumed;
        }

        // ------------------------------------------------------------------ held controls (0.6.3)

        /// <summary>What a pressed note or controller did: went to machines, or to the session (and which clip it launched).</summary>
        sealed class Held
        {
            public bool Session;
            public ISequence Track;
            public IPattern Clip;
            public LaunchOptions Options;
            public bool Nudge;
        }

        static readonly Held PassedOn = new Held();
        static readonly Held Consumed = new Held { Session = true };
        static readonly Held NudgeHeld = new Held { Session = true, Nudge = true };

        // Notes, then controllers, per channel. Each MIDI device calls from its own thread: entries are swapped whole.
        readonly Held[] held = new Held[2 * 16 * 128];

        static int HeldKey(MidiControl control) =>
            ((control.Type == MidiControlType.Note ? 0 : 1) * 16 + (control.Channel & 15)) * 128 + (control.Number & 127);

        void Remember(MidiControl control, Held what)
        {
            // Controllers are often knobs, where 0 is a value rather than a release: only a
            // launch is remembered for them (a pad sending CC 127/0 for Gate).
            if (control.Type == MidiControlType.ControlChange && what.Clip == null) return;
            Volatile.Write(ref held[HeldKey(control)], what);
        }

        /// <summary>A slot or scene was launched from MIDI (UI thread, after the launch): the view may select it.</summary>
        public event Action<int, int> Launched;      // track (-1 for a scene), scene

        void AnnounceLaunch(int track, int scene)
        {
            if (Launched != null)
                buzz.dispatcher.BeginInvoke(() => Launched?.Invoke(track, scene));
        }

        /// <summary>
        /// Any thread: what a pressed target does. Launch, stop and play use the engine (thread-safe)
        /// and the resolved <paramref name="t"/>; everything else is posted to the UI thread.
        /// </summary>
        internal void ExecuteNow(SessionTarget target, int track, int scene, LaunchMap t, int velocity)
        {
            var session = buzz.Session;
            switch (target)
            {
                case SessionTarget.Slot:
                    {
                        var seq = t.Track(track);
                        if (seq == null) return;
                        var slot = t.Slot(track, scene);
                        if (slot != null)
                            session.Launch(seq, slot.Pattern, slot.OptionsAt(velocity));
                        else
                            session.Stop(seq);
                        AnnounceLaunch(track, scene);
                        break;
                    }
                case SessionTarget.Scene:
                    {
                        var entry = t.Scene(scene);
                        if (entry != null)
                        {
                            SessionLayout.LaunchScene(session, entry, velocity);
                            AnnounceLaunch(-1, scene);
                        }
                        break;
                    }
                case SessionTarget.UndoLaunch:
                    (session as SessionCore)?.UndoLaunch();
                    break;
                case SessionTarget.TapTempo:
                    {
                        long when = System.Diagnostics.Stopwatch.GetTimestamp();   // the tap's time, not the UI's
                        buzz.dispatcher.BeginInvoke(() => layout.TapTempo(when));
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
        internal void ReleaseNow(int track, int scene, LaunchMap t)
        {
            var seq = t.Track(track);
            var slot = t.Slot(track, scene);
            if (seq == null || slot == null) return;
            var mode = slot.Options.Mode;
            if (mode != ClipLaunchMode.Gate && mode != ClipLaunchMode.Repeat) return;
            buzz.Session.Release(seq, slot.Pattern, slot.Options);
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
                            layout.LaunchClip(seq, pattern, 127, scene);
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
                case SessionTarget.UndoLaunch:
                    (session as SessionCore)?.UndoLaunch();
                    break;
                case SessionTarget.TapTempo:
                    layout.TapTempo();
                    break;
                case SessionTarget.FollowActions:
                    layout.SetFollowActionsEnabled(!(layout.Document?.FollowActionsEnabled ?? true));
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
