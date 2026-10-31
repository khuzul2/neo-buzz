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
        bool linkPlaying;                    // Link's play state as last acted on (start/stop sync acts on changes)
        int bpmFromLink = -1;                // the BPM set from Link's tempo (not sent back: it may be clamped)
        readonly object clockLock = new object();     // every MIDI input's thread feeds the one receiver
        volatile bool stopPending;           // a MIDI Stop posted to the UI, not yet done

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
                if (client.Peers > 0)
                {
                    // Follow the session's tempo (the audio thread follows its exact tempo and phase).
                    double tempo = client.Tempo;
                    int rounded = Math.Clamp((int)Math.Round(tempo), 16, 500);
                    if (rounded != buzz.BPM && Math.Abs(buzz.BPM - tempo) > 0.5)
                    {
                        bpmFromLink = rounded;
                        buzz.SetBPMFromEngine(rounded);
                    }
                }
                // Start/stop sync acts on changes of Link's play state (read now, not as the line
                // said): a start of ours waiting for Link's bar line isn't undone by a line from
                // before it.
                bool playing = client.IsPlaying;
                if (linkStartStop && client.Peers > 0 && playing != linkPlaying)
                {
                    if (playing && !buzz.PlayRequested) buzz.Playing = true;
                    else if (!playing && buzz.PlayRequested) buzz.Playing = false;
                }
                linkPlaying = playing;
                UpdateStatus();
            });
        }

        void OnBuzzChanged(object sender, PropertyChangedEventArgs e)
        {
            var client = link;
            if (client == null) return;
            if (e.PropertyName == "BPM")
            {
                // Not the tempo Link gave (rounded, maybe clamped to ReBuzz's 16-500): only the user's or a scene's.
                if (buzz.BPM != bpmFromLink && Math.Abs(buzz.BPM - client.Tempo) > 0.5)
                    client.Send(LinkProtocol.Tempo(buzz.BPM));
                bpmFromLink = -1;
            }
            else if (e.PropertyName == "Playing" && linkStartStop)
            {
                // A start as heard (it landed on Link's bar line), so peers start on that bar.
                double at = buzz.SyncClock.StartedHeardMicros;
                if (!buzz.Playing || double.IsNaN(at) || HostClock.Now() - at > 1_000_000) at = HostClock.Now();
                client.Send(LinkProtocol.Play(buzz.Playing, at));
                linkPlaying = buzz.Playing;
            }
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
            MidiClockEvent e;
            lock (clockLock) e = clockIn.OnMessage(data, HostClock.Now());
            switch (e)
            {
                case MidiClockEvent.None:
                    return false;
                case MidiClockEvent.Start:
                case MidiClockEvent.Continue:
                    // After a Stop still on its way through the UI (a master relocating sends Stop,
                    // position, Continue), the start goes the same way, after it.
                    if (stopPending)
                        buzz.dispatcher.BeginInvoke(() => { if (!buzz.PlayRequested && !buzz.SongLoading) buzz.Playing = true; });
                    else if (!buzz.PlayRequested && !buzz.SongLoading)
                        buzz.Playing = true;
                    break;
                case MidiClockEvent.Stop:
                    stopPending = true;
                    buzz.dispatcher.BeginInvoke(() =>
                    {
                        stopPending = false;
                        if (buzz.PlayRequested) buzz.Playing = false;
                    });
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

            /// <summary>Bars count from the clock's Start (and its song positions), the session's length.</summary>
            public double QuantumFor(int beatsPerBar) => beatsPerBar;

            /// <summary>Beat 0 is one pulse after Start: a start a little late catches up.</summary>
            public double CatchUpBeats => 0.25;

            /// <summary>Not before beat 0 (the pulse after Start).</summary>
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
                // Following MIDI clock: the song's (whole) tempo shows the clock's.
                if (followMidiClock && clockInSource.TryGetTimeline(out var clock))
                {
                    int rounded = Math.Clamp((int)Math.Round(clock.Tempo), 16, 500);
                    if (rounded != buzz.BPM && Math.Abs(buzz.BPM - clock.Tempo) > 0.5)
                        buzz.dispatcher.BeginInvoke(() => buzz.SetBPMFromEngine(rounded));
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
            if (client == null && problem != null && !followMidiClock)
                s = "Link stopped";
            else if (client != null)
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
