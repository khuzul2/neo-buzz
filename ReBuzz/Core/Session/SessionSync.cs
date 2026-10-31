using ReBuzz.Core.Sync;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Tempo and phase sync for the session (docs/specs/session-view.md 0.8): Ableton Link
    /// (through the helper process), MIDI clock in and out. The user's settings, kept in the
    /// registry, not the song. The audio thread follows whichever clock is on
    /// (<see cref="ReBuzzCore.SyncClock"/>); this class starts, stops and talks to them.
    ///
    /// Link: with peers, the session follows Link's tempo and bar phase; alone, Link follows
    /// the session (its tempo, and its beat while playing). Start/stop sync shares the
    /// transport with the peers that have it on. MIDI clock in: the session follows the
    /// clock's tempo and beat, and its Start, Continue and Stop. Link and MIDI clock in are
    /// one or the other. MIDI clock out goes to the chosen MIDI outputs while the transport
    /// plays, in step with what is heard.
    /// </summary>
    internal sealed class SessionSync : IDisposable
    {
        const string Path = "Session";

        readonly ReBuzzCore buzz;
        readonly IRegistryEx registry;
        readonly Func<string> helperPath;
        readonly MidiClockReceiver clockIn = new MidiClockReceiver();
        readonly ClockInSource clockInSource;
        readonly Timer timer;
        LinkClient link;
        MidiClockOutput clockOut;
        volatile bool followMidiClock;
        volatile int[] clockOutDevices = Array.Empty<int>();
        List<string> clockOutNames = new List<string>();
        bool linkStartStop;
        string status = "";
        string problem;
        int devicesRefresh;

        /// <summary>The settings or the status changed (UI thread).</summary>
        public event Action Changed;

        public SessionSync(ReBuzzCore buzz, IRegistryEx registry, Func<string> helperPath = null)
        {
            this.buzz = buzz;
            this.registry = registry;
            this.helperPath = helperPath ?? (() => LinkClient.DefaultPath);
            clockInSource = new ClockInSource(clockIn);
            buzz.PropertyChanged += OnBuzzChanged;
            if (buzz.SessionCore != null) buzz.SessionCore.PropertyChanged += OnSessionChanged;

            linkStartStop = Read("LinkStartStop", 0) != 0;
            clockOutNames = Read("MidiClockOut", "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (Read("MidiClockIn", 0) != 0) SetFollowMidiClock(true);
            if (Read("LinkEnabled", 0) != 0) SetLink(true);
            UpdateClockOut();
            timer = new Timer(_ => Tick(), null, 250, 250);
        }

        // ------------------------------------------------------------------ state

        public bool LinkEnabled => link != null;
        public bool LinkStartStop => linkStartStop;
        public bool FollowMidiClock => followMidiClock;
        public IReadOnlyList<string> ClockOutNames => clockOutNames;
        public int LinkPeers => link?.Peers ?? 0;
        public double LinkTempo => link?.Tempo ?? 0;

        /// <summary>The Link helper's process, while Link is on (tests).</summary>
        internal LinkClient Link => link;

        /// <summary>MIDI clock in's state (tests).</summary>
        internal MidiClockReceiver ClockIn => clockIn;

        /// <summary>A short status for the Session View ("" when nothing syncs).</summary>
        public string Status => status;

        /// <summary>Why the last change didn't take (null if it did).</summary>
        public string Problem => problem;

        // ------------------------------------------------------------------ Link

        /// <summary>Link on or off (off: MIDI clock in stays as it is; on: it goes off).</summary>
        public void SetLink(bool on)
        {
            problem = null;
            if (on == LinkEnabled) return;
            if (on)
            {
                if (followMidiClock) SetFollowMidiClock(false);
                var client = new LinkClient(helperPath());
                problem = client.Start();
                if (problem != null)
                {
                    client.Dispose();
                    Write("LinkEnabled", 0);
                    RaiseChanged();
                    return;
                }
                client.StateChanged += OnLinkState;
                client.Exited += () => buzz.dispatcher.BeginInvoke(() =>
                {
                    if (link != client) return;
                    problem = "The Link helper stopped.";
                    TurnLinkOff();
                });
                link = client;
                // The tempo before joining: on joining, Link gives the session's to us.
                client.Send(LinkProtocol.Quantum(Quantum));
                client.Send(LinkProtocol.Tempo(buzz.BPM));
                client.Send(LinkProtocol.StartStopSync(linkStartStop));
                client.Send(LinkProtocol.Enable(true));
                buzz.SyncClock.Source = client;
            }
            else
                TurnLinkOff();
            Write("LinkEnabled", on ? 1 : 0);
            RaiseChanged();
        }

        void TurnLinkOff()
        {
            var client = link;
            link = null;
            if (buzz.SyncClock.Source == client) buzz.SyncClock.Source = null;
            client?.Dispose();
            UpdateStatus();
        }

        /// <summary>Start/stop sync with Link's peers (Live's).</summary>
        public void SetLinkStartStop(bool on)
        {
            linkStartStop = on;
            link?.Send(LinkProtocol.StartStopSync(on));
            Write("LinkStartStop", on ? 1 : 0);
            RaiseChanged();
        }

        double Quantum => Math.Max(1, buzz.SessionCore?.BeatsPerBar ?? 4);

        void OnLinkState(LinkState state)
        {
            // The helper's reader thread.
            buzz.dispatcher.BeginInvoke(() =>
            {
                var client = link;
                if (client == null) return;
                if (state.Peers > 0)
                {
                    // Follow the session's tempo (the audio thread follows its exact tempo and phase).
                    double tempo = state.Timeline.Tempo;
                    int rounded = Math.Clamp((int)Math.Round(tempo), 16, 500);
                    if (rounded != buzz.BPM && Math.Abs(buzz.BPM - tempo) > 0.5)
                        buzz.SetBPMFromEngine(rounded);
                }
                if (linkStartStop && state.Peers > 0)
                {
                    if (state.Playing && !buzz.PlayRequested) buzz.Playing = true;
                    else if (!state.Playing && buzz.PlayRequested) buzz.Playing = false;
                }
                UpdateStatus();
            });
        }

        void OnBuzzChanged(object sender, PropertyChangedEventArgs e)
        {
            var client = link;
            if (client == null) return;
            if (e.PropertyName == "BPM")
            {
                if (Math.Abs(buzz.BPM - client.Tempo) > 0.5)
                    client.Send(LinkProtocol.Tempo(buzz.BPM));
            }
            else if (e.PropertyName == "Playing" && linkStartStop)
                client.Send(LinkProtocol.Play(buzz.Playing, HostClock.Now()));
        }

        void OnSessionChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SessionCore.BeatsPerBar))
                buzz.dispatcher.BeginInvoke(() => link?.Send(LinkProtocol.Quantum(Quantum)));
        }

        // ------------------------------------------------------------------ MIDI clock in

        /// <summary>Follow MIDI clock in (and its Start, Continue and Stop); Link goes off.</summary>
        public void SetFollowMidiClock(bool on)
        {
            problem = null;
            if (on && LinkEnabled) SetLink(false);
            followMidiClock = on;
            buzz.SyncClock.Source = on ? clockInSource : (link != null ? link : null);
            Write("MidiClockIn", on ? 1 : 0);
            UpdateStatus();
            RaiseChanged();
        }

        /// <summary>
        /// The MIDI thread, for clock, real-time and song position messages: true when MIDI clock
        /// in takes them (then they go no further).
        /// </summary>
        internal bool OnClockInput(int data)
        {
            if (!followMidiClock) return false;
            switch (clockIn.OnMessage(data, HostClock.Now()))
            {
                case MidiClockEvent.None:
                    return false;
                case MidiClockEvent.Start:
                case MidiClockEvent.Continue:
                    if (!buzz.PlayRequested && !buzz.SongLoading) buzz.Playing = true;
                    break;
                case MidiClockEvent.Stop:
                    if (buzz.PlayRequested) buzz.Playing = false;     // from this thread, the stop goes through the UI's
                    break;
            }
            return true;
        }

        /// <summary>MIDI clock in as a clock to follow: while it runs and its pulses keep coming.</summary>
        sealed class ClockInSource : ISyncSource
        {
            readonly MidiClockReceiver receiver;

            public ClockInSource(MidiClockReceiver receiver) => this.receiver = receiver;

            public bool TryGetTimeline(out Timeline line)
            {
                line = default;
                if (!receiver.Running || !receiver.Timeline.TryRead(out line)) return false;
                return HostClock.Now() - line.Micros < MidiClockReceiver.GapMicros;
            }

            /// <summary>MIDI clock has beats, not bars: phases match over a beat.</summary>
            public double QuantumFor(int beatsPerBar) => 1;

            /// <summary>Not before beat 0 (the pulse after Start); late for it, the next beat.</summary>
            public double EarliestStart(in Timeline line, double heardMicros) => Math.Max(heardMicros, line.MicrosAt(0));
        }

        // ------------------------------------------------------------------ MIDI clock out

        /// <summary>MIDI clock to an output (by its name, as the MIDI settings list it) on or off.</summary>
        public void SetClockOut(string device, bool on)
        {
            if (string.IsNullOrEmpty(device)) return;
            var names = clockOutNames.Where(n => n != device).ToList();
            if (on) names.Add(device);
            clockOutNames = names;
            Write("MidiClockOut", string.Join("|", names));
            UpdateClockOut();
            RaiseChanged();
        }

        /// <summary>The MIDI outputs open now (index, name).</summary>
        public IReadOnlyList<(int Index, string Name)> Outputs =>
            buzz.MidiInOutEngine?.GetMidiOuts().Select(o => (o.Item1, o.Item2)).ToList() ?? new List<(int, string)>();

        void UpdateClockOut()
        {
            var names = clockOutNames;
            var outs = Outputs;
            clockOutDevices = outs.Where(o => names.Contains(o.Name)).Select(o => o.Index).ToArray();
            if (names.Count > 0 && clockOut == null)
                clockOut = new MidiClockOutput(() => buzz.Playing, buzz.SyncClock.Ours, SendClock);
            else if (names.Count == 0 && clockOut != null)
            {
                clockOut.Dispose();
                clockOut = null;
            }
        }

        void SendClock(int message)
        {
            var engine = buzz.MidiInOutEngine;
            if (engine == null) return;
            foreach (int device in clockOutDevices)
                engine.SendMidiOut(device, message);
        }

        // ------------------------------------------------------------------ housekeeping

        void Tick()
        {
            try
            {
                // Alone, Link follows the session: its beat as heard, so peers that join find it in phase.
                var client = link;
                if (client != null && client.Peers == 0 && buzz.Playing && buzz.SyncClock.Ours.TryRead(out var ours))
                {
                    int tpb = Math.Max(1, buzz.TPB);
                    double barBeat = ours.Beat - (buzz.SessionCore?.GridOrigin ?? 0) / (double)tpb;
                    client.Send(LinkProtocol.Beat(barBeat, ours.Micros));
                }
                // Outputs come and go (unplugged, opened in the MIDI settings): look again now and then.
                if (clockOutNames.Count > 0 && ++devicesRefresh % 8 == 0)
                    clockOutDevices = Outputs.Where(o => clockOutNames.Contains(o.Name)).Select(o => o.Index).ToArray();
                buzz.dispatcher.BeginInvoke(UpdateStatus);
            }
            catch (Exception)
            {
                // A timer thread: never take the process down.
            }
        }

        void UpdateStatus()
        {
            string s = "";
            var client = link;
            if (client != null)
                s = client.Peers > 0 ? $"Link · {client.Peers} peer{(client.Peers == 1 ? "" : "s")} · {client.Tempo:0.##}" : "Link · alone";
            else if (followMidiClock)
                s = clockIn.Timeline.TryRead(out var line) && HostClock.Now() - line.Micros < MidiClockReceiver.GapMicros
                    ? $"MIDI clock · {line.Tempo:0.#}" + (clockIn.Running ? "" : " · stopped")
                    : "MIDI clock · waiting";
            if (clockOutNames.Count > 0)
                s = s.Length > 0 ? s + " · clock out" : "Clock out";
            if (s == status) return;
            status = s;
            Changed?.Invoke();
        }

        void RaiseChanged()
        {
            UpdateStatus();
            Changed?.Invoke();
        }

        int Read(string key, int def)
        {
            try { return registry?.Read(key, def, Path) ?? def; } catch (Exception) { return def; }
        }

        string Read(string key, string def)
        {
            try { return registry?.Read(key, def, Path) ?? def; } catch (Exception) { return def; }
        }

        void Write<T>(string key, T value)
        {
            try { registry?.Write(key, value, Path); } catch (Exception) { }
        }

        public void Dispose()
        {
            timer.Dispose();
            buzz.PropertyChanged -= OnBuzzChanged;
            if (buzz.SessionCore != null) buzz.SessionCore.PropertyChanged -= OnSessionChanged;
            buzz.SyncClock.Source = null;
            link?.Dispose();
            link = null;
            clockOut?.Dispose();
            clockOut = null;
        }
    }
}
