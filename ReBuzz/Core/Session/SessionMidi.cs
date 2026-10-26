using BuzzGUI.Interfaces;
using System;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Plays the Session View from MIDI (fork addition, docs/specs/session-view.md 0.2): applies
    /// the active <see cref="SessionMidiMap"/> to incoming notes and CCs, and runs MIDI learn.
    ///
    /// The user's map is stored in the registry and applies to every song; a song can carry its
    /// own map (<see cref="SessionDocument.SongMidi"/>), which then replaces the user's.
    /// Controls the map uses don't reach machines (<see cref="Consumes"/>, called on the MIDI
    /// thread from ReBuzzCore.SendMIDIInput), so pads launching clips don't also play notes.
    /// Everything else runs on the UI thread.
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

        /// <summary>Learn mode started, completed or was cancelled; or the mapping changed.</summary>
        public event Action Changed;

        internal SessionMidi(ReBuzzCore buzz, SessionLayout layout, IRegistryEx registry)
        {
            this.buzz = buzz;
            this.layout = layout;
            this.registry = registry;
            userMap = SessionMidiMap.Parse(ReadUserMap());
            UpdateActive();

            buzz.MIDIInput += OnMidiInput;
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

        // ------------------------------------------------------------------ learn and edit

        /// <summary>The next note or CC that arrives gets bound to this target.</summary>
        public void BeginLearn(SessionTarget target, int track, int scene)
        {
            learning = (target, track, scene);
            Changed?.Invoke();
        }

        public void CancelLearn()
        {
            if (learning == null) return;
            learning = null;
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

        /// <summary>UI thread (ReBuzzCore raises MIDIInput through the dispatcher).</summary>
        internal void OnMidiInput(int data)
        {
            if (!MidiControl.TryDecode(data, out var control, out int value, out bool isPress)) return;

            if (learning is { } learn)
            {
                if (!isPress) return;     // learn on press, so the release of the previous key doesn't count
                learning = null;
                Modify(m => m.Bind(control, learn.Target, learn.Track, learn.Scene));
                return;
            }

            var map = active;
            if (map == null || !map.TryResolve(control, out var t, out var tr, out var sc)) return;

            if (t == SessionTarget.Quantization)
            {
                if (control.Type == MidiControlType.ControlChange)
                    layout.SetLaunchQuantization(QuantizationCycle[Math.Min(QuantizationCycle.Length - 1, value * QuantizationCycle.Length / 128)]);
                else if (isPress)
                    layout.SetLaunchQuantization(QuantizationCycle[(Array.IndexOf(QuantizationCycle, buzz.Session.LaunchQuantization) + 1) % QuantizationCycle.Length]);
                return;
            }

            if (!isPress) return;
            Execute(t, tr, sc);
        }

        /// <summary>Does what a target does when its control is pressed.</summary>
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
                            session.Launch(seq, pattern);
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
                    buzz.Playing = !buzz.Playing;
                    break;
                case SessionTarget.Quantization:
                    layout.SetLaunchQuantization(QuantizationCycle[(Array.IndexOf(QuantizationCycle, session.LaunchQuantization) + 1) % QuantizationCycle.Length]);
                    break;
            }
        }
    }
}
