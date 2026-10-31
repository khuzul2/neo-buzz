using NAudio.Midi;
using ReBuzz.AppViews.Session;
using ReBuzz.Control;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ReBuzz.Core.Session
{
    /// <summary>A controller's MIDI ports, as the controllers see them (tests use a fake).</summary>
    internal interface IControllerPort : IDisposable
    {
        void Send(int message);
        void Send(byte[] message);
        /// <summary>A short message came (the driver's thread).</summary>
        event Action<int> Received;
    }

    /// <summary>
    /// Control surfaces for the Session View (docs/specs/session-view.md 0.8): finds
    /// controllers a script knows among the MIDI ports, connects the ones the user turned on,
    /// gives each a session ring, turns their presses into launches (from the MIDI thread, as
    /// <see cref="SessionMidi"/> does, so a busy UI doesn't delay them), and keeps their lights
    /// in step with the session (every 20 ms; only changed lights are sent).
    ///
    /// Scripts: the built-in ones (<see cref="BuiltInScripts"/>), and any class deriving from
    /// <see cref="ControllerScript"/> in a .dll in the Controllers folder beside ReBuzz.exe.
    /// </summary>
    internal sealed class SessionControllers : IDisposable
    {
        const string RegistryPath = "Session";
        const string RegistryKey = "Controllers";
        public const int RefreshMs = 20;

        readonly ReBuzzCore buzz;
        readonly IRegistryEx registry;
        readonly List<ControllerScript> scripts;
        readonly List<Connection> connections = new List<Connection>();
        readonly List<string> scriptProblems = new List<string>();
        Timer timer;
        readonly bool autoRefresh;
        int pending, ticks;
        HashSet<string> wanted;
        volatile bool disposed;

        /// <summary>Controllers connected or let go, rings moved (UI thread).</summary>
        public event Action Changed;

        /// <summary>A controller the MIDI ports offer: its script and ports.</summary>
        public sealed record Candidate(ControllerScript Script, int Input, string InputName, int Output, string OutputName);

        /// <summary>A connected controller.</summary>
        internal sealed class Connection
        {
            public ControllerScript Script;
            public IControllerPort Port;
            public string Label;
            public SurfaceState State;
            public LedFrame Shown;
            public volatile bool Shift;
            public int TrackOffset, SceneOffset;     // UI thread
            public volatile bool Broken;
            /// <summary>Pads held (driver thread): the slot each press played, so its release goes there.</summary>
            public readonly Dictionary<(int X, int Y), (int Track, int Scene)> Held = new Dictionary<(int, int), (int, int)>();
        }

        /// <param name="autoRefresh">Lights refreshed every 20 ms (tests call <see cref="Refresh"/> themselves).</param>
        public SessionControllers(ReBuzzCore buzz, IRegistryEx registry, bool loadScripts = true, bool autoRefresh = true)
        {
            this.buzz = buzz;
            this.registry = registry;
            this.autoRefresh = autoRefresh;
            scripts = BuiltInScripts.All().ToList();
            if (loadScripts) scripts.AddRange(LoadScripts(Path.Combine(AppContext.BaseDirectory, "Controllers")));
            wanted = new HashSet<string>(Read().Split('|', StringSplitOptions.RemoveEmptyEntries));
            if (wanted.Count > 0)
            {
                EnsureTimer();
                ConnectWanted();
            }
        }

        public IReadOnlyList<ControllerScript> Scripts => scripts;

        /// <summary>Scripts in the Controllers folder that didn't load, and why.</summary>
        public IReadOnlyList<string> ScriptProblems => scriptProblems;

        internal IReadOnlyList<Connection> Connections => connections;

        /// <summary>The controllers turned on (connected or waiting for their ports).</summary>
        public IReadOnlyCollection<string> Wanted => wanted;

        public bool IsConnected(string script) => connections.Any(c => c.Script.Name == script && !c.Broken);

        // ------------------------------------------------------------------ finding and connecting

        IEnumerable<ControllerScript> LoadScripts(string folder)
        {
            var found = new List<ControllerScript>();
            if (!Directory.Exists(folder)) return found;
            foreach (var file in Directory.GetFiles(folder, "*.dll"))
            {
                try
                {
                    var assembly = Assembly.LoadFrom(file);
                    foreach (var type in assembly.GetTypes().Where(t => typeof(ControllerScript).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null))
                        found.Add((ControllerScript)Activator.CreateInstance(type));
                }
                catch (Exception e)
                {
                    scriptProblems.Add($"{Path.GetFileName(file)}: {e.Message}");
                }
            }
            return found;
        }

        /// <summary>The controllers the MIDI ports offer now (one a script: its best-fitting ports).</summary>
        public List<Candidate> Available()
        {
            var inputs = new List<string>();
            var outputs = new List<string>();
            try
            {
                for (int i = 0; i < MidiIn.NumberOfDevices; i++) inputs.Add(MidiIn.DeviceInfo(i).ProductName);
                for (int i = 0; i < MidiOut.NumberOfDevices; i++) outputs.Add(MidiOut.DeviceInfo(i).ProductName);
            }
            catch (Exception)
            {
                // No MIDI system (tests, a server): nothing to offer.
            }
            return Match(scripts, inputs, outputs);
        }

        internal static List<Candidate> Match(IEnumerable<ControllerScript> scripts, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs)
        {
            var found = new List<Candidate>();
            foreach (var script in scripts)
            {
                int bestIn = -1, bestOut = -1, inScore = 0, outScore = 0;
                try
                {
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        int score = script.MatchInput(inputs[i]);
                        if (score > inScore) { inScore = score; bestIn = i; }
                    }
                    for (int i = 0; i < outputs.Count; i++)
                    {
                        int score = script.MatchOutput(outputs[i]);
                        if (score > outScore) { outScore = score; bestOut = i; }
                    }
                }
                catch (Exception)
                {
                    continue;                                   // a script that throws offers nothing
                }
                if (bestIn >= 0 && bestOut >= 0)
                    found.Add(new Candidate(script, bestIn, inputs[bestIn], bestOut, outputs[bestOut]));
            }
            return found;
        }

        /// <summary>Turns a controller on (and connects it if its ports are there); null when connected, else why not.</summary>
        public string TurnOn(ControllerScript script)
        {
            wanted.Add(script.Name);
            Write();
            EnsureTimer();
            var candidate = Available().FirstOrDefault(c => c.Script == script);
            if (candidate == null) return $"{script.Name} isn't connected (no MIDI ports of its).";
            return Connect(candidate);
        }

        /// <summary>Turns a controller off: its lights go out, it gets its own mode back, its ports close.</summary>
        public void TurnOff(ControllerScript script)
        {
            wanted.Remove(script.Name);
            Write();
            foreach (var c in connections.Where(c => c.Script == script).ToList())
                Detach(c);
            RaiseChanged();
        }

        string Connect(Candidate candidate)
        {
            if (IsConnected(candidate.Script.Name)) return null;
            foreach (var old in connections.Where(c => c.Script == candidate.Script).ToList()) Detach(old);
            IControllerPort port;
            try
            {
                port = new NAudioPort(candidate.Input, candidate.Output);
            }
            catch (Exception e)
            {
                return $"{candidate.Script.Name}: its ports ({candidate.InputName}, {candidate.OutputName}) didn't open ({e.Message}). " +
                       "If ReBuzz's MIDI settings have them on, turn them off there: the controller needs them to itself.";
            }
            Attach(candidate.Script, port, $"{candidate.Script.Name} ({candidate.InputName})");
            return null;
        }

        /// <summary>Starts driving a controller through a port (UI thread).</summary>
        internal Connection Attach(ControllerScript script, IControllerPort port, string label)
        {
            var c = new Connection
            {
                Script = script,
                Port = port,
                Label = label,
                State = new SurfaceState(script.Width, script.Height)
            };
            try
            {
                foreach (var message in script.Hello()) Send(c, message);
            }
            catch (Exception e)
            {
                buzz.DCWriteLine($"{script.Name}: {e.Message}");
            }
            port.Received += m => OnInput(c, m);
            connections.Add(c);
            EnsureTimer();
            Refresh(c);
            RaiseChanged();
            return c;
        }

        /// <summary>Lets a controller go: lights off, its own mode back, ports closed (UI thread).</summary>
        internal void Detach(Connection c)
        {
            connections.Remove(c);
            if (!c.Broken)
            {
                try
                {
                    // Every light it was sent, off; then its own mode back.
                    if (c.Shown != null)
                        foreach (int m in c.Shown.Off()) c.Port.Send(m);
                    foreach (var message in c.Script.Goodbye()) Send(c, message);
                }
                catch (Exception) { }
            }
            try { c.Port.Dispose(); } catch (Exception) { }
        }

        static void Send(Connection c, byte[] message)
        {
            if (message == null || message.Length == 0) return;
            if (message[0] == 0xF0) c.Port.Send(message);
            else c.Port.Send(message[0] | (message.Length > 1 ? message[1] << 8 : 0) | (message.Length > 2 ? message[2] << 16 : 0));
        }

        // ------------------------------------------------------------------ input (the driver's thread)

        void OnInput(Connection c, int message)
        {
            try
            {
                var e = c.Script.Read(message, c.Shift);
                if (e.Kind == ControlKind.None || disposed) return;
                var layout = buzz.SessionLayout;
                var midi = buzz.SessionMidi;
                if (layout == null || midi == null) return;
                var map = layout.LaunchMap;
                int track = Volatile.Read(ref c.TrackOffset) + e.X;
                int scene = Volatile.Read(ref c.SceneOffset) + e.Y;
                switch (e.Kind)
                {
                    case ControlKind.Shift:
                        c.Shift = e.Pressed;
                        break;
                    case ControlKind.Pad:
                        {
                            if (!e.Pressed)
                            {
                                // The release goes where the press went (the ring may have moved since).
                                if (c.Held.Remove((e.X, e.Y), out var held))
                                    midi.ReleaseNow(held.Track, held.Scene, map);
                                break;
                            }
                            var seq = map.Track(track);
                            if (seq == null || map.Scene(scene) == null) break;      // outside the session: nothing
                            c.Held[(e.X, e.Y)] = (track, scene);
                            if (map.Slot(track, scene) == null && (buzz.Session as SessionCore)?.IsArmed(seq) == true)
                                buzz.dispatcher.BeginInvoke(() => buzz.SessionLayout?.RecordTake(track, scene));
                            else
                                midi.ExecuteNow(SessionTarget.Slot, track, scene, map, e.Velocity > 0 ? e.Velocity : 127);
                            break;
                        }
                    case ControlKind.Scene:
                        if (e.Pressed) midi.ExecuteNow(SessionTarget.Scene, 0, scene, map, 127);
                        break;
                    case ControlKind.TrackStop:
                        midi.ExecuteNow(SessionTarget.TrackStop, track, 0, map, 127);
                        break;
                    case ControlKind.StopAll:
                        midi.ExecuteNow(SessionTarget.StopAll, 0, 0, map, 127);
                        break;
                    case ControlKind.Play:
                        midi.ExecuteNow(SessionTarget.Transport, 0, 0, map, 127);
                        break;
                    case ControlKind.TrackArm:
                        buzz.dispatcher.BeginInvoke(() =>
                        {
                            var l = buzz.SessionLayout;
                            if (l != null && track < l.TrackCount) l.SetArmed(track, !l.IsArmed(track));
                        });
                        break;
                    case ControlKind.SessionRecord:
                        buzz.dispatcher.BeginInvoke(() =>
                        {
                            var l = buzz.SessionLayout;
                            if (l != null) l.SetSessionRecord(!l.IsSessionRecording, Volatile.Read(ref c.SceneOffset));
                        });
                        break;
                    case ControlKind.Up:
                    case ControlKind.Down:
                    case ControlKind.Left:
                    case ControlKind.Right:
                        {
                            int step = c.Shift ? (e.Kind == ControlKind.Left || e.Kind == ControlKind.Right ? c.Script.Width : c.Script.Height) : 1;
                            int dx = e.Kind == ControlKind.Left ? -step : e.Kind == ControlKind.Right ? step : 0;
                            int dy = e.Kind == ControlKind.Up ? -step : e.Kind == ControlKind.Down ? step : 0;
                            if (e.Pressed) buzz.dispatcher.BeginInvoke(() => MoveRing(c, dx, dy));
                            break;
                        }
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine($"{c.Script.Name}: {e.Message}");
            }
        }

        /// <summary>Moves a controller's ring, kept on the session (UI thread).</summary>
        internal void MoveRing(Connection c, int dx, int dy)
        {
            var layout = buzz.SessionLayout;
            if (layout == null) return;
            // As Live: the ring stays on the session, as full as it can be.
            int maxTrack = Math.Max(0, layout.TrackCount - c.Script.Width);
            int maxScene = Math.Max(0, layout.SceneCount - c.Script.Height);
            Volatile.Write(ref c.TrackOffset, Math.Clamp(c.TrackOffset + dx, 0, maxTrack));
            Volatile.Write(ref c.SceneOffset, Math.Clamp(c.SceneOffset + dy, 0, maxScene));
            Refresh(c);
            RaiseChanged();
        }

        // ------------------------------------------------------------------ lights (the UI thread)

        /// <summary>The rings (UI thread): where each controller's box is, for the Session View.</summary>
        public IEnumerable<(int Track, int Scene, int Width, int Height)> Rings =>
            connections.Where(c => !c.Broken).Select(c => (c.TrackOffset, c.SceneOffset, c.Script.Width, c.Script.Height));

        /// <summary>Whether a slot is inside a controller's ring.</summary>
        public bool InRing(int track, int scene) =>
            connections.Any(c => !c.Broken && track >= c.TrackOffset && track < c.TrackOffset + c.Script.Width &&
                                 scene >= c.SceneOffset && scene < c.SceneOffset + c.Script.Height);

        void EnsureTimer()
        {
            if (timer != null || !autoRefresh || disposed) return;
            timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref pending, 1) == 1) return;      // the UI hasn't caught up: skip
                buzz.dispatcher.BeginInvoke(() =>
                {
                    Volatile.Write(ref pending, 0);
                    Tick();
                });
            }, null, RefreshMs, RefreshMs);
        }

        void Tick()
        {
            if (disposed) return;
            foreach (var c in connections.ToList()) Refresh(c);
            if (++ticks % (2000 / RefreshMs) != 0) return;
            // Now and then: a controller unplugged with its lights unchanged would go unnoticed; one
            // light sent again finds out.
            foreach (var c in connections.Where(c => !c.Broken && c.Shown != null && c.Shown.Count > 0).ToList())
            {
                try
                {
                    var first = LedFrame.Changes(null, c.Shown);
                    c.Port.Send(first[0]);
                }
                catch (Exception)
                {
                    c.Broken = true;
                    RaiseChanged();
                }
            }
            // And connect controllers turned on whose ports came (back).
            if (wanted.Count > connections.Count(c => !c.Broken))
            {
                foreach (var c in connections.Where(c => c.Broken).ToList()) Detach(c);
                foreach (var candidate in Available().Where(a => wanted.Contains(a.Script.Name) && !IsConnected(a.Script.Name)))
                    if (Connect(candidate) == null) buzz.DCWriteLine($"{candidate.Script.Name} connected.");
            }
        }

        /// <summary>Fills the controller's state from the session and sends the lights that changed (UI thread).</summary>
        internal void Refresh(Connection c)
        {
            if (c.Broken) return;
            Fill(c);
            var frame = new LedFrame();
            try
            {
                c.Script.Render(c.State, frame);
            }
            catch (Exception e)
            {
                // A script's fault: let its controller go rather than throw every 20 ms.
                c.Broken = true;
                buzz.DCWriteLine($"{c.Script.Name}: its lights failed ({e.Message}); turn it off and on to try again.");
                RaiseChanged();
                return;
            }
            var changes = LedFrame.Changes(c.Shown, frame);
            try
            {
                foreach (int m in changes) c.Port.Send(m);
                c.Shown = frame;
            }
            catch (Exception e)
            {
                c.Broken = true;
                buzz.DCWriteLine($"{c.Script.Name} stopped answering: {e.Message}");
                RaiseChanged();
            }
        }

        void Fill(Connection c)
        {
            var s = c.State;
            s.Clear();
            var layout = buzz.SessionLayout;
            var session = buzz.Session;
            if (layout == null || session == null) return;
            int tracks = layout.TrackCount, scenes = layout.SceneCount;
            s.TrackCount = tracks;
            s.SceneCount = scenes;
            s.TrackOffset = c.TrackOffset;
            s.SceneOffset = c.SceneOffset;
            s.Playing = buzz.Playing;
            s.SessionRecording = layout.IsSessionRecording;
            s.Shift = c.Shift;
            for (int y = 0; y < s.Height; y++) s.SceneExists[y] = c.SceneOffset + y < scenes;
            for (int x = 0; x < s.Width; x++)
            {
                int track = c.TrackOffset + x;
                if (track >= tracks) continue;
                var seq = layout.SequenceOf(track);
                if (seq == null) continue;
                bool armed = layout.IsArmedTrack(seq);
                s.TrackExists[x] = true;
                s.TrackArmed[x] = armed;
                s.TrackPlaying[x] = session.GetPlaying(seq) != null;
                for (int y = 0; y < s.Height; y++)
                {
                    int scene = c.SceneOffset + y;
                    if (scene >= scenes) continue;
                    var pattern = layout.PatternAt(track, scene);
                    if (pattern == null)
                    {
                        s.SetPad(x, y, armed ? PadState.ArmedEmpty : PadState.Empty, Rgb.Black);
                        continue;
                    }
                    var state = session.GetState(seq, pattern);
                    PadState pad;
                    if (layout.IsRecordingInto(seq, pattern))
                        pad = state == BuzzGUI.Interfaces.ClipState.Playing || state == BuzzGUI.Interfaces.ClipState.Stopping ? PadState.Recording : PadState.RecordQueued;
                    else
                        pad = state switch
                        {
                            BuzzGUI.Interfaces.ClipState.Queued => PadState.Queued,
                            BuzzGUI.Interfaces.ClipState.Playing => PadState.Playing,
                            BuzzGUI.Interfaces.ClipState.Stopping => PadState.Stopping,
                            _ => PadState.Stopped,
                        };
                    s.SetPad(x, y, pad, ColorOf(PatternColors.Get(pattern)));
                    s.SceneHasClips[y] = true;
                    if (pad == PadState.Queued || pad == PadState.RecordQueued) s.SceneQueued[y] = true;
                }
            }
        }

        /// <summary>A clip's colour (its colour index in the theme's list); uncoloured clips: the Session View's clip grey-blue.</summary>
        static Rgb ColorOf(int index)
        {
            var choices = SessionPalette.ColorChoices;
            if (index < 0 || choices == null || choices.Length == 0) return new Rgb(0x8E, 0xB9, 0xC2);
            var color = choices[index % choices.Length].Color;
            return new Rgb(color.R, color.G, color.B);
        }

        void RaiseChanged()
        {
            if (buzz.dispatcher.CheckAccess()) Changed?.Invoke();
            else buzz.dispatcher.BeginInvoke(() => Changed?.Invoke());
        }

        string Read()
        {
            try { return registry?.Read(RegistryKey, "", RegistryPath) ?? ""; } catch (Exception) { return ""; }
        }

        void Write()
        {
            try { registry?.Write(RegistryKey, string.Join("|", wanted), RegistryPath); } catch (Exception) { }
        }

        /// <summary>Connects the controllers turned on last time (at start).</summary>
        public void ConnectWanted()
        {
            if (wanted.Count == 0) return;
            try
            {
                foreach (var candidate in Available().Where(a => wanted.Contains(a.Script.Name)))
                {
                    string problem = Connect(candidate);
                    if (problem != null) buzz.DCWriteLine(problem);
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Controllers: " + e.Message);
            }
        }

        public void Dispose()
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
            foreach (var c in connections.ToList()) Detach(c);
        }

        // ------------------------------------------------------------------ ports

        /// <summary>A controller's ports through NAudio (WinMM), opened for the controller alone.</summary>
        sealed class NAudioPort : IControllerPort
        {
            readonly MidiIn input;
            readonly MidiOut output;
            readonly Lock sendLock = new Lock();

            public event Action<int> Received;

            public NAudioPort(int inputIndex, int outputIndex)
            {
                output = new MidiOut(outputIndex);
                try
                {
                    input = new MidiIn(inputIndex);
                    input.MessageReceived += (s, e) => Received?.Invoke(e.RawMessage);
                    input.Start();
                }
                catch
                {
                    output.Dispose();
                    input?.Dispose();
                    throw;
                }
            }

            public void Send(int message)
            {
                lock (sendLock) output.Send(message);
            }

            public void Send(byte[] message)
            {
                lock (sendLock) output.SendBuffer(message);
            }

            public void Dispose()
            {
                try { input.Stop(); } catch (Exception) { }
                try { input.Dispose(); } catch (Exception) { }
                try { output.Dispose(); } catch (Exception) { }
            }
        }
    }
}
