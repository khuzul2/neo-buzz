#nullable enable
// Fork (docs/specs/daw-modes.md §8.9, NM1): note maps. A machine that plays sounds on notes can say which keys make a sound and
// what to call them ("Kick" on C2), so the piano roll can name its rows and fold to the keys that play. A machine says it by
// implementing this interface on its machine class (Neo machines through NeoMachineBase.BuildNoteMap; Neo Plug from the plug-in's
// own note names), or declares a fixed map in its manifest ("note_map" in the .machine.json). The studio reads it through
// BuzzGUI.Interfaces.NoteMaps, which turns it into immutable NoteMap snapshots.
//
// Threads: everything here is for the UI thread (the host polls it; the machine may raise NoteMapChanged from any thread). Nothing
// here is called on the audio thread, and a provider must not take a lock the audio thread holds.

using System;
using System.Collections.Generic;

namespace Buzz.MachineInterface
{
    /// <summary>One playable key of a machine: its MIDI key (0 to 127), a short name, an optional group and an optional colour.</summary>
    public readonly struct NoteMapEntry : IEquatable<NoteMapEntry>
    {
        public NoteMapEntry(int key, string? name = null, string? group = null, uint color = 0)
        {
            Key = key;
            Name = name;
            Group = group;
            Color = color;
        }

        /// <summary>The MIDI key, in the pitch a clip's Note column and the piano roll use (a Neo machine: MIDI, A4 = 440 Hz).</summary>
        public int Key { get; }

        /// <summary>A short name ("Kick", "Closed hat"); null or empty: the key plays but has no name of its own.</summary>
        public string? Name { get; }

        /// <summary>A group the key belongs to ("Drums", a plug-in's note-name port or a zone's wave); null: none.</summary>
        public string? Group { get; }

        /// <summary>A colour hint as ARGB (a drum group's colour); 0: none.</summary>
        public uint Color { get; }

        public bool Equals(NoteMapEntry other) => Key == other.Key && Name == other.Name && Group == other.Group && Color == other.Color;
        public override bool Equals(object? obj) => obj is NoteMapEntry e && Equals(e);
        public override int GetHashCode() => HashCode.Combine(Key, Name, Group, Color);
    }

    /// <summary>
    /// A machine's note map: which keys make a sound and their names. <see cref="NoteMapVersion"/> changes whenever the answer to
    /// <see cref="ReadNoteMap"/> would (a zone renamed, a plug-in's names changed); the studio compares it, it never asks for the map twice
    /// in a row without a reason.
    /// </summary>
    public interface INoteMapProvider
    {
        /// <summary>A number that changes whenever the map does. Any thread, cheap, never blocks the audio thread or waits for one.</summary>
        int NoteMapVersion { get; }

        /// <summary>
        /// The playable keys now, for a MIDI channel (0 to 15) or -1 for any: a machine whose map doesn't depend on the channel answers
        /// the same for all. Null: this machine has no map (the manifest's, if it has one, is used). UI thread.
        /// </summary>
        IReadOnlyList<NoteMapEntry>? ReadNoteMap(int channel);

        /// <summary>Raised, from any thread, when the machine knows its map changed (the host then asks <see cref="NoteMapVersion"/> again).</summary>
        event Action? NoteMapChanged;
    }
}
