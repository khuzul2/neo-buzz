// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): how the keyboard under a machine's window
// plays it, and whether it shows one at all. A machine that takes MIDI notes gets MIDI, as a MIDI
// keyboard plays it; one that takes notes only in its Note track parameter (most native machines, the
// Pedal synths) gets them there, a note per track, as its patterns play it. The agent server decides
// the same way (ReBuzz/Agent/MachineNotes.cs: the manifest's note input, its pitch offset, the Note and
// velocity track parameters), with one thing first: a managed machine that has MidiNote takes MIDI,
// whatever a manifest profiled before it had it says (the Pedal synths are getting MIDI input).
//
// No keyboard: effects, the Master, control machines (peer controllers, LFOs, chord machines),
// generators with no note input of any kind (audio inputs, the Session Clip Player, Rout 909's
// triggers, noise generators).

using System;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;

namespace ReBuzz.Core.Keyboards
{
    internal enum KeyboardInput { None, Midi, Tracks }

    internal sealed class KeyboardRoute
    {
        public static readonly KeyboardRoute None = new KeyboardRoute { Input = KeyboardInput.None };

        public KeyboardInput Input { get; init; }

        /// <summary>Semitones added to a written pitch before the machine gets it (its manifest's), so the written pitch plays.</summary>
        public int Offset { get; init; }

        /// <summary>Tracks: the Note track parameter, and the velocity beside it (null when it has none).</summary>
        public IParameter Note { get; init; }

        public IParameter Velocity { get; init; }

        /// <summary>Polac's VST hosts take pitch bend and pressure in their own controller numbers.</summary>
        public bool Polac { get; init; }

        /// <summary>The written pitches (MIDI numbers, A4 = 69 = 440 Hz) that reach the machine.</summary>
        public int Lowest { get; init; }

        public int Highest { get; init; } = -1;

        /// <summary>One line on how it plays (the keyboard's tooltip).</summary>
        public string Description { get; init; } = "";

        public bool Shows => Input != KeyboardInput.None;

        public bool Plays(int written) => Shows && written >= Lowest && written <= Highest;

        /// <summary>A managed machine takes MIDI notes when it has MidiNote (as machine_describe's takes_midi).</summary>
        static bool HasMidiNote(MachineCore m) =>
            m.ManagedMachine?.GetType().GetMethod("MidiNote", new[] { typeof(int), typeof(int), typeof(int) }) != null;

        static bool IsPolac(IMachineDLL dll) => dll.Name == "Polac VSTi 1.1" || dll.Name == "Polac VST 1.1";

        public static KeyboardRoute For(IMachine machine)
        {
            try
            {
                return Decide(machine);
            }
            catch (Exception)
            {
                // A machine half made or going away: no keyboard rather than a broken window.
                return None;
            }
        }

        static KeyboardRoute Decide(IMachine machine)
        {
            if (machine is not MachineCore m || m.Hidden || m.DLL?.Info == null || m.DLL.IsMissing) return None;
            var info = m.DLL.Info;
            if (info.Type != MachineType.Generator || m.IsControlMachine || (info.Flags & MachineInfoFlags.CONTROL_MACHINE) != 0) return None;
            var manifest = SongModel.Manifest(m.DLL);
            if (manifest?.Type == "control") return None;
            var notes = manifest?.Notes;
            var noteParameter = m.ParameterGroups.Count > 2 ? m.ParameterGroups[2].Parameters.FirstOrDefault(p => p.Type == ParameterType.Note) : null;
            bool polac = IsPolac(m.DLL);
            string mode;
            if (HasMidiNote(m) || polac || notes?.Input == "midi") mode = "midi";
            // "tracks", "none" with a Note parameter (silent until it is given a wave), or not profiled.
            else if (noteParameter != null && Math.Max(info.MinTracks, info.MaxTracks) > 0) mode = "tracks";
            else return None;

            var input = MachineNotes.For(m, mode);
            int offset = input.Offset;
            int lowest = Math.Max(0, -offset), highest = Math.Min(127, input.Highest - offset);
            if (highest < lowest) return None;
            string pitch = offset == 0 ? "" :
                $" Keys play as written: its pitch offset from its manifest ({offset:+0;-0} semitones) is added on the way.";
            if (mode == "midi")
                return new KeyboardRoute
                {
                    Input = KeyboardInput.Midi,
                    Offset = offset,
                    Polac = polac,
                    Lowest = lowest,
                    Highest = highest,
                    Description = "Plays MIDI notes, as a MIDI keyboard does (an armed track records them)." + pitch,
                };
            return new KeyboardRoute
            {
                Input = KeyboardInput.Tracks,
                Offset = offset,
                Note = input.NoteParameter,
                Velocity = input.VelocityParameter,
                Lowest = lowest,
                Highest = highest,
                Description = $"Plays its Note track parameter, a note per track, as its patterns do: {m.DLL.Name} takes no MIDI notes." + pitch,
            };
        }
    }
}
