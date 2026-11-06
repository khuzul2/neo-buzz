// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): plays the notes of one keyboard (the one
// under a machine's window) into its machine, by the route KeyboardRoute chose, and lets go of them.
//
// MIDI: note-on and note-off with velocity through the way a MIDI keyboard's input goes. When a track
// of the machine is armed, the session takes it as that track's keyboard input, so the machine plays
// it and a take records it (session recording, Capture MIDI); only this machine's tracks, not every
// armed one, since this keyboard belongs to this machine. Otherwise the machine gets it directly, as
// the piano roll's and the agent's live notes do.
//
// Tracks: the Note track parameter, as notes_write and the patterns play such a machine: each note
// takes the lowest track free (its velocity beside it, scaled to the velocity parameter's range, as
// notes_write writes it), an off (255) lets go; with every track busy the oldest note is taken over
// and comes back when a track frees (a mono synth plays the last key held, then the one before).
// Machines with interface 42 or later hear it at once (SendControlChanges, as the parameter window
// sends its moves); older native machines at their next tick, as their parameter windows play them.
//
// Every note ends: AllOff lets go of whatever this keyboard holds, and the views call it when the
// pointer is let go anywhere, the window closes or loses focus, or the keyboard is hidden.

using System;
using System.Collections.Generic;
using System.Linq;
using ReBuzz.Agent.Model;

namespace ReBuzz.Core.Keyboards
{
    internal sealed class KeyboardPlayer
    {
        const int MaxTracks = 256;

        sealed class Held
        {
            public int Key;            // written pitch
            public int Sent;           // what the machine gets (plus the offset)
            public int Velocity;
            public int Track = -1;     // tracks: the track it sounds on, -1 while waiting for one
            public long Order;
        }

        readonly MachineCore machine;
        readonly Dictionary<int, Held> held = new Dictionary<int, Held>();
        readonly int[] trackKey = Enumerable.Repeat(-1, MaxTracks).ToArray();
        long order;

        public KeyboardPlayer(MachineCore machine)
        {
            this.machine = machine;
            Route = KeyboardRoute.For(machine);
        }

        public MachineCore Machine => machine;

        public KeyboardRoute Route { get; private set; }

        /// <summary>Asks the machine again how it takes notes (after it was replaced or its manifest changed); lets go of everything first.</summary>
        public void Refresh()
        {
            AllOff();
            Route = KeyboardRoute.For(machine);
        }

        /// <summary>The keys this keyboard holds (written pitches).</summary>
        public IReadOnlyCollection<int> Keys => held.Keys;

        public bool IsHeld(int key) => held.ContainsKey(key);

        /// <summary>The velocity a held key was struck with, 0 when not held.</summary>
        public int VelocityOf(int key) => held.TryGetValue(key, out var h) ? h.Velocity : 0;

        ReBuzzCore Core => machine.Graph?.Buzz as ReBuzzCore;

        bool Alive => machine.DLL != null && !machine.DLL.IsMissing && !machine.DLL.IsCrashed && machine.Ready && Core != null;

        /// <summary>Plays a written pitch at a velocity (1–127). False when it isn't one the machine takes, or is held already.</summary>
        public bool On(int key, int velocity)
        {
            var route = Route;
            if (!route.Plays(key) || held.ContainsKey(key) || !Alive) return false;
            velocity = Math.Clamp(velocity, 1, 127);
            var h = new Held { Key = key, Sent = key + route.Offset, Velocity = velocity, Order = ++order };
            held[key] = h;
            if (route.Input == KeyboardInput.Midi)
                SendMidi(0x90 | (h.Sent << 8) | (velocity << 16));
            else
                Sound(h, route);
            return true;
        }

        /// <summary>Lets go of a key (nothing when it isn't held).</summary>
        public void Off(int key)
        {
            if (!held.Remove(key, out var h)) return;
            var route = Route;
            if (route.Input == KeyboardInput.Midi)
            {
                SendMidi(0x80 | (h.Sent << 8));
                return;
            }
            if (h.Track < 0) return;               // taken over: nothing sounds
            int track = h.Track;
            trackKey[track] = -1;
            // A note waiting for a track (taken over while held) comes back, the latest first.
            var waiting = held.Values.Where(x => x.Track < 0).OrderByDescending(x => x.Order).FirstOrDefault();
            if (waiting != null)
            {
                Sound(waiting, route, track);
                return;
            }
            SetNote(route, track, TrackNotes.Off);
        }

        /// <summary>Lets go of everything this keyboard holds.</summary>
        public void AllOff()
        {
            if (held.Count == 0) return;
            var route = Route;
            var all = held.Values.OrderBy(h => h.Order).ToList();
            held.Clear();
            Array.Fill(trackKey, -1);
            foreach (var h in all)
            {
                if (route.Input == KeyboardInput.Midi) SendMidi(0x80 | (h.Sent << 8));
                else if (h.Track >= 0) SetNote(route, h.Track, TrackNotes.Off);   // taken-over notes sound nowhere
            }
        }

        // ------------------------------------------------------------------ MIDI

        void SendMidi(int data)
        {
            var core = Core;
            if (core == null) return;
            try
            {
                // An armed track of this machine takes it as its keyboard's input: it plays and records.
                if (core.SessionCore.IsArmedMachine(machine) && core.SessionCore.OnMidiInputFor(machine, data)) return;
                if (!Alive) return;
                core.MachineManager.SendMidiInput(machine, data, Route.Polac);
            }
            catch (Exception)
            {
                // The machine went away meanwhile: nothing to play into.
            }
        }

        // ------------------------------------------------------------------ tracks

        int TrackCount => Math.Clamp(machine.TrackCount, 0, MaxTracks);

        /// <summary>Puts a held note on a track: the one given, else the lowest free, else the oldest note's.</summary>
        void Sound(Held h, KeyboardRoute route, int track = -1)
        {
            int tracks = TrackCount;
            if (tracks == 0) return;
            if (track < 0)
            {
                for (int t = 0; t < tracks; t++)
                    if (trackKey[t] < 0) { track = t; break; }
            }
            if (track < 0)
            {
                // Every track busy: take over the oldest note's (it waits, and comes back when a track frees).
                var oldest = held.Values.Where(x => x.Track >= 0 && x != h).OrderBy(x => x.Order).FirstOrDefault();
                if (oldest == null) return;
                track = oldest.Track;
                oldest.Track = -1;
            }
            trackKey[track] = h.Key;
            h.Track = track;
            int buzz = TrackNotes.ToBuzz(h.Sent);
            if (buzz < 0) return;
            // The velocity goes beside the note, as notes_write writes it (TrackNotes.Events).
            if (route.Velocity is { } v && v.MaxValue > v.MinValue)
                v.SetValue(track, v.MinValue + (int)Math.Round((v.MaxValue - v.MinValue) * h.Velocity / 127.0));
            SetNote(route, track, buzz);
        }

        void SetNote(KeyboardRoute route, int track, int value)
        {
            if (route.Note == null || !Alive || track >= TrackCount) return;
            try
            {
                route.Note.SetValue(track, value);
                // As the parameter window sends what the user plays: at once where the machine takes it (interface 42 on).
                if (machine.DLL.Info.Version >= 42) machine.SendControlChanges();
            }
            catch (Exception)
            {
                // The machine went away meanwhile.
            }
        }
    }
}
