// Fork (docs/specs/notes.md G1, CU6a): the canvas modulators hear the notes a machine is given as notes, from every source. Neo Envelope's Note
// mode and Neo LFO's note retrigger used to hear only Note-parameter events, so a clip, the piano roll, Session, live MIDI or a note wire on a Neo
// synth (which takes MIDI) never started them. Each source is tested on a real Neo Poly and a real Neo Envelope; the Envelope's Velocity
// amount (G4) takes the velocity of the note it heard.

using System;
using System.Linq;
using System.Threading;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using static ReBuzz.Studio.Tests.AudioHygieneTests;

namespace ReBuzz.Studio.Tests
{
    public class NoteTapTests
    {
        const int TB = ClipEvent.TimeBase;

        static ReBuzzCore Core => AudioHygieneTests.Core;

        static MachineCore Add(string gear, string name)
        {
            Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, 0.5f, 0.5f);
            return Core.SongCore.MachinesList.Last(m => m.Name == name);
        }

        static IParameter Param(IMachine m, string name) => m.ParameterGroups[1].Parameters.First(p => p.Name == name);

        static IModulator Mod(IMachine m) => ModulationBoard.Of(m) ?? throw new AssertionException(m.Name + " is not a modulator");

        /// <summary>A Neo Envelope that opens in 1 ms, holds, and listens to <paramref name="poly"/>.</summary>
        static MachineCore Listener(MachineCore poly, int velocityRaw = 0)
        {
            var env = Add("Neo Envelope", "Tap Env");
            Param(env, "Attack").SetValue(1 << 16, 0);
            Param(env, "Sustain").SetValue(1 << 16, 1000);
            Param(env, "Release").SetValue(1 << 16, 200);
            Param(env, "Curve").SetValue(1 << 16, 500);
            Param(env, "Velocity").SetValue(1 << 16, velocityRaw);
            Mod(env).ListenMachine = poly.Name;
            return env;
        }

        static void Midi(MachineCore m, int status, int note, int velocity) => Core.MachineManager.SendMidiInput(m, status | (note << 8) | (velocity << 16), false);

        /// <summary>Reads buffers (as the device does) until the modulator's output satisfies the test, for up to a few seconds.</summary>
        static bool ReadUntil(AudioThread audio, IModulator mod, Func<float, bool> ok, int ms = 6000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                audio.Read(8);
                if (ok(mod.Output)) return true;
                Pump();
                Thread.Sleep(10);
            }
            return ok(mod.Output);
        }

        /// <summary>The listener subscribes on its maintenance timer: send notes until it has heard one, then let go.</summary>
        static void HearsNotes(AudioThread audio, MachineCore poly, IModulator mod, Action<int> play)
        {
            var end = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < end)
            {
                play(127);
                if (ReadUntil(audio, mod, y => y > 0.5f, 400)) return;
                Midi(poly, 0x80, 60, 0);
                Thread.Sleep(50);
            }
            Assert.Fail("the envelope never heard a note");
        }

        [AvaloniaTest]
        public void ALiveMidiNoteStartsAnEnvelopeListeningToAnInstrumentAndItsNoteOffReleasesIt()
        {
            NewSong();
            try
            {
                var poly = AddPoly("Tap Poly");
                var env = Listener(poly);
                var mod = Mod(env);
                using var audio = new AudioThread(1);
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                HearsNotes(audio, poly, mod, v => Midi(poly, 0x90, 60, v));
                Assert.That(mod.Output, Is.GreaterThan(0.5f), "a MIDI note-on starts it");
                Midi(poly, 0x80, 60, 0);
                Assert.That(ReadUntil(audio, mod, y => y < 0.05f, 4000), Is.True, "the note-off releases it");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        [AvaloniaTest]
        public void ANoteOfAClipOnTheInstrumentStartsIt()
        {
            NewSong();
            try
            {
                var poly = AddPoly("Tap Poly");
                poly.CreateClip("00", 16);
                var clip = (ClipCore)poly.Clips.First();
                Core.SessionLayout.EditNotes(clip, null, Enumerable.Range(0, 16).Select(k => new ClipNote(k * TB, TB / 2, 48 + k % 12, 100)).ToArray());
                Arrange(poly, clip);
                var env = Listener(poly);
                var mod = Mod(env);
                using var audio = new AudioThread(1);
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                Assert.That(ReadUntil(audio, mod, y => y > 0.5f, 15000), Is.True, "the clip's notes (the MIDI column the engine plays) start it");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        [AvaloniaTest]
        public void TheEnvelopesVelocityAmountFollowsTheVelocityOfTheNoteItHeard()
        {
            NewSong();
            try
            {
                var poly = AddPoly("Tap Poly");
                var env = Listener(poly, velocityRaw: 1000);
                var mod = Mod(env);
                using var audio = new AudioThread(1);
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                HearsNotes(audio, poly, mod, v => Midi(poly, 0x90, 60, v));
                Midi(poly, 0x80, 60, 0);
                Assert.That(ReadUntil(audio, mod, y => y < 0.02f, 4000), Is.True);
                float Peak(int velocity)
                {
                    Midi(poly, 0x90, 64, velocity);
                    float peak = 0;
                    for (int i = 0; i < 20; i++) { audio.Read(2); peak = Math.Max(peak, mod.Output); }
                    Midi(poly, 0x80, 64, 0);
                    ReadUntil(audio, mod, y => y < 0.02f, 4000);
                    return peak;
                }
                float soft = Peak(30), hard = Peak(127);
                Assert.That(soft, Is.EqualTo(30 / 127f).Within(0.06f), "a soft note opens it a little");
                Assert.That(hard, Is.EqualTo(1f).Within(0.06f), "a hard note opens it fully");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        [AvaloniaTest]
        public void AnEnvelopeListeningToATrackStaysDeafToNotesOfNoTrack()
        {
            NewSong();
            try
            {
                var poly = AddPoly("Tap Poly");
                var env = Listener(poly);
                var mod = Mod(env);
                mod.ListenTrack = 0;
                using var audio = new AudioThread(1);
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                for (int i = 0; i < 40; i++)
                {
                    Midi(poly, 0x90, 60, 127);
                    audio.Read(8);
                    Pump();
                    Thread.Sleep(30);
                }
                Assert.That(mod.Output, Is.LessThan(0.05f), "MIDI notes carry no track: one track's listener hears its Note parameter only");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }
    
        /// <summary>
        /// The route guard (notes.md G7): a Neo instrument's clip notes are the MIDI column (note, velocity, length, any number at once), never the
        /// tracks' Note parameter, so a chord is not cut to the 16 tracks. The tracks route stays for Neo Sampler's Tracker mode and an explicit "tracks".
        /// </summary>
        [AvaloniaTest]
        public void EveryNeoInstrumentTakesItsClipNotesByTheMidiColumn()
        {
            var gear = new[] { "Neo Poly", "Neo Wave", "Neo FM6", "Neo Mallet", "Neo Organ", "Neo Sampler", "Neo Drums", "Neo Patch", "Dye Essence" }
                .Concat(Core.MachineDLLs.Keys.Where(k => k.StartsWith("Neo Learn "))).Distinct().Where(k => Core.MachineDLLs.ContainsKey(k)).ToList();
            Assert.That(gear.Count, Is.GreaterThanOrEqualTo(9), "the instruments are in the tests' gear");
            NewSong();
            try
            {
                foreach (var g in gear)
                {
                    var m = Add(g, "Route " + g);
                    m.CreateClip("00", 4);
                    var clip = m.ClipsList.Single();
                    Assert.That(ReBuzz.Core.Notes.SongNotes.For(Core, clip), Is.InstanceOf<ReBuzz.Core.Notes.MidiColumnNotes>(), g + ": clip notes are the MIDI column");
                }
            }
            finally { NewSong(); }
        }

        [AvaloniaTest]
        public void NeoPlugTakesNotesAndNeoPlugFXDoesNot()
        {
            NewSong();
            try
            {
                var inst = Add("Neo Plug", "Notes Plug");
                var fx = Add("Neo Plug FX", "Notes Plug FX");
                Assert.That(ReBuzz.Core.NoteWires.NoteWireStore.PortsOf(inst)?.In, Is.True, "Neo Plug takes notes");
                Assert.That(ReBuzz.Core.NoteWires.NoteWireStore.PortsOf(fx)?.In, Is.False, "Neo Plug FX has no note ports");
            }
            finally { NewSong(); }
        }
    }
}
