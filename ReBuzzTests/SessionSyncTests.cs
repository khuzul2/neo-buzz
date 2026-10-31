using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Core.Sync;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using ReBuzzTests.Automation;
using static ReBuzzTests.HardeningTests063;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: tempo and phase sync (docs/specs/session-view.md 0.8): the Link helper,
    /// the session following a Link peer, MIDI clock in and out. The pure parts (the phase lock,
    /// the clock's fit, MIDI clock timing) are tested in ReBuzz.SessionTests.
    /// </summary>
    public class SessionSyncTests
    {
        /// <summary>The helper build.ps1 built (the CI sets this); the Link tests are skipped without it.</summary>
        static string Helper => Environment.GetEnvironmentVariable("REBUZZ_LINK_HELPER");

        static void RequireHelper()
        {
            if (string.IsNullOrEmpty(Helper) || !System.IO.File.Exists(Helper))
                Assert.Ignore("ReBuzzLink.exe not built (set REBUZZ_LINK_HELPER)");
        }

        static bool WaitFor(Func<bool> condition, double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }

        static LinkClient Peer(double tempo)
        {
            var peer = new LinkClient(Helper);
            Assert.That(peer.Start(), Is.Null);
            peer.Send(LinkProtocol.Quantum(4));
            peer.Send(LinkProtocol.Tempo(tempo));
            peer.Send(LinkProtocol.Enable(true));
            return peer;
        }

        /// <summary>Renders like an audio device would ask: 256 frames every 256 / rate seconds.</summary>
        static void PlayRealTime(Driver.AudioReader reader, double seconds)
        {
            int rate = Math.Max(1, ReBuzzCore.masterInfo.SamplesPerSec);
            double period = 256 * 1e6 / rate;
            double next = HostClock.Now();
            double end = next + seconds * 1e6;
            while (next < end)
            {
                while (HostClock.Now() < next) Thread.SpinWait(50);
                reader.Read(256);
                next += period;
            }
        }

        [Test]
        public void TwoLinkHelpersFindEachOtherAndAgree()
        {
            RequireHelper();
            using var a = Peer(131.25);
            Thread.Sleep(1000);                                     // Link: the older session (by over 0.5 s) is the one joined
            using var b = new LinkClient(Helper);
            Assert.That(b.Start(), Is.Null);
            b.Send(LinkProtocol.Quantum(4));
            b.Send(LinkProtocol.Enable(true));
            if (!WaitFor(() => a.Peers > 0 && b.Peers > 0, 10))
                Assert.Inconclusive("no Link peer discovery on this machine (multicast)");
            Assert.That(WaitFor(() => Math.Abs(b.Tempo - 131.25) < 0.01, 5), Is.True, $"the joining peer takes the tempo: {b.Tempo}");
            Thread.Sleep(100);
            Assert.That(a.TryGetTimeline(out var la), Is.True);
            Assert.That(b.TryGetTimeline(out var lb), Is.True);
            double now = HostClock.Now();
            double diff = SyncMath.Wrap(la.BeatAt(now) - lb.BeatAt(now), 4) * 60e6 / la.Tempo;
            TestContext.Out.WriteLine($"Link peers' phase difference: {diff:F1} µs");
            Assert.That(Math.Abs(diff), Is.LessThan(1000));
        }

        /// <summary>
        /// The acceptance criterion's setting, shortened: a Link peer at a fractional tempo; the
        /// session joins, starts on Link's bar line and stays in phase within a millisecond (as
        /// the engine places its ticks; the audio device's own latency is the estimate's).
        /// </summary>
        [Test]
        public void TheSessionFollowsALinkPeersTempoAndBarPhase()
        {
            RequireHelper();
            using var rig = new KeysRig();
            using var peer = Peer(123.4);
            Thread.Sleep(1000);                                     // the peer's session is the older: the session joins it
            rig.Core.SessionSync?.Dispose();
            var sync = new SessionSync(rig.Core, null, () => Helper);
            rig.Core.SessionSync = sync;
            try
            {
                sync.SetLink(true);
                Assert.That(sync.Problem, Is.Null);
                if (!WaitFor(() => sync.LinkPeers > 0 && peer.Peers > 0, 10))
                    Assert.Inconclusive("no Link peer discovery on this machine (multicast)");
                Assert.That(WaitFor(() => Math.Abs(sync.LinkTempo - 123.4) < 0.01, 5), Is.True, "the session took the peer's tempo");

                var reader = rig.Driver.CreateAudioReader(256);
                PlayRealTime(reader, 1.0);                          // the audio clock is learnt
                rig.Core.Playing = true;
                PlayRealTime(reader, 6.0);
                var clock = rig.Core.SyncClock;
                Assert.That(clock.Ours.TryRead(out var before), Is.True);
                PlayRealTime(reader, 2.0);
                Assert.That(rig.Core.Playing, Is.True);
                Assert.That(clock.IsFollowing, Is.True);
                Assert.That(rig.Core.BPM, Is.EqualTo(123), "the song's (whole) tempo follows, rounded");
                Assert.That(clock.Ours.TryRead(out var ours), Is.True);
                // Each tick's own length moves a little as the lock follows; over two seconds, Link's tempo.
                double tempo = (ours.Beat - before.Beat) / (ours.Micros - before.Micros) * 60e6;
                Assert.That(Math.Abs(tempo - 123.4), Is.LessThan(0.05), $"plays at Link's exact tempo: {tempo:F3}");
                Assert.That(peer.TryGetState(out var line), Is.True);
                int tpb = Math.Max(1, rig.Core.TPB);
                double barBeat = ours.Beat - rig.Session.GridOrigin / (double)tpb;
                double error = PhaseLock.Error(line, rig.Session.BeatsPerBar, barBeat, ours.Micros) * 60e6 / line.Tempo;
                TestContext.Out.WriteLine($"phase against the peer: {error:F0} µs; the clock's own estimate {clock.ErrorMicros:F0} µs");
                Assert.That(Math.Abs(error), Is.LessThan(1000));
                Assert.That(Math.Abs(clock.ErrorMicros), Is.LessThan(1000));
            }
            finally
            {
                rig.Core.Playing = false;
                sync.Dispose();
                rig.Core.SessionSync = null;
            }
        }

        [Test]
        public void MidiClockInStartsAndStopsTheTransport()
        {
            using var rig = new KeysRig();
            var sync = rig.Core.SessionSync;
            Assert.That(sync, Is.Not.Null);
            sync.SetFollowMidiClock(true);
            try
            {
                Assert.That(rig.Core.SyncClock.Source, Is.Not.Null);
                for (int i = 0; i < 48; i++) rig.Core.SendMIDIInput(0xF8);
                Assert.That(sync.ClockIn.NextBeat, Is.EqualTo(0), "stopped, pulses keep the tempo, not the position");
                rig.Core.SendMIDIInput(0xFA);
                Assert.That(rig.Core.PlayRequested, Is.True, "Start starts");
                for (int i = 0; i < 24; i++) rig.Core.SendMIDIInput(0xF8);
                Assert.That(sync.ClockIn.NextBeat, Is.EqualTo(1), "after Start, 24 pulses make a beat");
                rig.Core.SendMIDIInput(0xFC);
                Assert.That(rig.Core.PlayRequested, Is.False, "Stop stops");
                rig.Core.SendMIDIInput(0xFB);
                Assert.That(rig.Core.PlayRequested, Is.True, "Continue starts");
                rig.Core.SendMIDIInput(0xFC);
            }
            finally
            {
                sync.SetFollowMidiClock(false);
            }
            Assert.That(rig.Core.SyncClock.Source, Is.Null);
            rig.Core.SendMIDIInput(0xFA);
            Assert.That(rig.Core.PlayRequested, Is.False, "not following: Start goes to the machines only");
        }

        [Test]
        public void MidiClockOutSendsStartThenAPulseEveryTwentyFourthOfABeat()
        {
            var ours = new TimelineCell();
            var sent = new ConcurrentQueue<(int Message, double At)>();
            double t0 = HostClock.Now() + 100_000;
            ours.Write(new Timeline(t0, 0, 120));
            using (new MidiClockOutput(() => true, ours, m => sent.Enqueue((m, HostClock.Now()))))
                Thread.Sleep(700);
            var list = sent.ToList();
            Assert.That(list[0].Message, Is.EqualTo(MidiClockSender.Start));
            Assert.That(Math.Abs(list[0].At - (t0 - MidiClockSender.StartLead)), Is.LessThan(3000));
            var pulses = list.Where(m => m.Message == MidiClockSender.Clock).ToList();
            double interval = 60e6 / (120 * 24);
            Assert.That(pulses.Count, Is.InRange(25, 31), "about 0.6 s of pulses");
            var errors = pulses.Select((p, i) => Math.Abs(p.At - (t0 + i * interval))).OrderBy(e => e).ToList();
            TestContext.Out.WriteLine($"MIDI clock out timing: median {errors[errors.Count / 2]:F0} µs, worst {errors[^1]:F0} µs");
            Assert.That(errors[errors.Count / 2], Is.LessThan(1000), "median within a millisecond");
            Assert.That(errors[^1], Is.LessThan(10_000), "none far off (a shared runner)");
            Assert.That(list[^1].Message, Is.EqualTo(MidiClockSender.Stop), "Stop when it ends while running");
        }
    }
}
