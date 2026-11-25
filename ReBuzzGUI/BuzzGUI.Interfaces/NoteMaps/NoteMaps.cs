#nullable enable
// Fork (docs/specs/daw-modes.md §8.9, NM1): where the views get a machine's note map. One answer per machine, in this order:
//   1. the machine's own provider (INoteMapProvider: a Neo machine, Neo Plug with the hosted plug-in's note names);
//   2. a map its manifest declares (the host registers the reader as <see cref="Manifest"/>);
//   3. none: <see cref="NoteMap.None"/>, and the views behave as they always did.
// Snapshots are immutable and cached; a provider's version moving makes a new one (its Version rising) and raises <see cref="Changed"/> on
// the UI thread (<see cref="Marshal"/>). Nothing here runs on the audio thread.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Buzz.MachineInterface;

namespace BuzzGUI.Interfaces
{
    public static class NoteMaps
    {
        /// <summary>
        /// Runs an action on the UI thread. The studio sets it (at once when already there); the default runs it on the calling thread,
        /// which is the UI thread of a headless host that has none.
        /// </summary>
        public static Action<Action> Marshal { get; set; } = a => a();

        /// <summary>The manifest's map for a machine (the host reads the "note_map" of its .machine.json); null: it declares none.</summary>
        public static Func<IMachine, IReadOnlyList<NoteMapEntry>?>? Manifest { get; set; }

        /// <summary>Raised on the UI thread after a machine's map changed: its snapshot (<see cref="For"/>) is a new one with a higher Version.</summary>
        public static event Action<IMachine>? Changed;

        sealed class State
        {
            public readonly object Gate = new();
            public INoteMapProvider? Provider;
            public Action? Handler;
            public int ProviderVersion = int.MinValue;
            public int Serial;
            public bool ManifestRead;
            public IReadOnlyList<NoteMapEntry>? ManifestEntries;
            public readonly Dictionary<int, NoteMap> Snapshots = new();
        }

        static readonly ConditionalWeakTable<IMachine, State> states = new();

        /// <summary>
        /// The machine's note map for a channel (0 to 15, or -1 for any; a provider that doesn't tell channels apart answers the same).
        /// <see cref="NoteMap.None"/> when it has none. Looks at the provider's version first, so a change shows at the next call.
        /// </summary>
        public static NoteMap For(IMachine? machine, int channel = -1)
        {
            if (machine == null) return NoteMap.None;
            var state = states.GetValue(machine, _ => new State());
            bool changed = Check(machine, state);
            NoteMap map;
            lock (state.Gate) map = Snapshot(machine, state, channel < 0 || channel > 15 ? -1 : channel);
            if (changed) Raise(machine);
            return map;
        }

        /// <summary>
        /// Looks at the machine's provider and raises <see cref="Changed"/> if its map moved. The studio's rolls call it a few times a second
        /// (the Neo machines' maps follow their parameters and the song's waves, which have no event to wait for).
        /// </summary>
        public static void Poll(IMachine? machine)
        {
            if (machine == null) return;
            var state = states.GetValue(machine, _ => new State());
            if (Check(machine, state)) Raise(machine);
        }

        /// <summary>The machine's map is different now, whatever its provider says (the manifest was edited, a test changed a source).</summary>
        public static void Invalidate(IMachine? machine)
        {
            if (machine == null) return;
            var state = states.GetValue(machine, _ => new State());
            lock (state.Gate)
            {
                state.ManifestRead = false;
                state.ProviderVersion = int.MinValue;
            }
            if (Check(machine, state, force: true)) Raise(machine);
        }

        // Whether the answer changed (and a new snapshot generation started).
        static bool Check(IMachine machine, State state, bool force = false)
        {
            INoteMapProvider? provider = null;
            try { provider = machine.ManagedMachine as INoteMapProvider; }
            catch (Exception) { }
            lock (state.Gate)
            {
                if (!ReferenceEquals(provider, state.Provider))
                {
                    if (state.Provider != null && state.Handler != null) state.Provider.NoteMapChanged -= state.Handler;
                    state.Provider = provider;
                    state.Handler = null;
                    if (provider != null)
                    {
                        // A machine that knows it changed says so (Neo Plug, from the plug-in's callback): look again on the UI thread.
                        state.Handler = () => Marshal(() => Poll(machine));
                        provider.NoteMapChanged += state.Handler;
                    }
                    state.ProviderVersion = int.MinValue;
                    force = true;
                }
                int version = state.ProviderVersion;
                if (provider != null)
                {
                    try { version = provider.NoteMapVersion; }
                    catch (Exception) { version = state.ProviderVersion; }
                }
                bool first = state.Serial == 0;
                if (!force && !first && version == state.ProviderVersion) return false;
                state.ProviderVersion = version;
                state.Snapshots.Clear();
                if (first) { state.Serial = 1; return false; }
                state.Serial++;
                return true;
            }
        }

        static NoteMap Snapshot(IMachine machine, State state, int channel)
        {
            if (state.Snapshots.TryGetValue(channel, out var cached)) return cached;
            IReadOnlyList<NoteMapEntry>? entries = null;
            string source = "machine";
            try { entries = state.Provider?.ReadNoteMap(channel); }
            catch (Exception) { }
            if (entries == null || entries.Count == 0)
            {
                // No map of its own: the one its manifest declares, read once.
                source = "manifest";
                if (!state.ManifestRead)
                {
                    state.ManifestRead = true;
                    try { state.ManifestEntries = Manifest?.Invoke(machine); }
                    catch (Exception) { state.ManifestEntries = null; }
                }
                entries = state.ManifestEntries;
            }
            // (A machine with no map keeps NoteMap.None, Version 0: the views then behave as they always did.)
            var map = entries == null || entries.Count == 0 ? NoteMap.None : NoteMap.Create(entries, state.Serial, source, channel);
            state.Snapshots[channel] = map;
            return map;
        }

        static void Raise(IMachine machine)
        {
            var handler = Changed;
            if (handler == null) return;
            Marshal(() => handler(machine));
        }
    }
}
