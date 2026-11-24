#nullable disable
// Neo Drums (docs/specs/machines.md §5): a synthesised drum kit
// (docs/specs/mcp-server.md §12.5). Twelve voices on the General MIDI drum keys from C2 (as Neo Sampler's Kit mode),
// or per-track hit columns (BD, SD, … with a velocity) and an Accent column, as Buzz drum machines play. 16 tracks of
// Note, Velocity, Accent and the hit columns, plus MIDI. One stereo output: the voices' buses mix into it in the engine
// (Engine/DrumsEngine.cs), where per-voice outputs would come from (machines.md, Neo Drums). DSP in Engine/, parameters
// generated from Params.def, editor in NeoDrumsEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Drums
{
    [MachineDecl(Name = "Neo Drums", ShortName = "Drums", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoDrumsMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[DrumsEngine.MaxBlock], right = new float[DrumsEngine.MaxBlock];
        readonly PendingHits pending = new PendingHits(Params.Tracks);
        int rate;

        public NeoDrumsMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new DrumsEngine(Values);
        }

        internal DrumsEngine Engine { get; }

        /// <summary>The hit columns and Accent (generated setters, Machine.Params.g.cs).</summary>
        void OnTrackParameter(int parameter, int track, int value)
        {
            if (parameter == TP.Accent)
            {
                if (value != 0) pending.Accent();
            }
            else pending.Set(track, parameter - TP.HitBD, value);
        }

        static readonly IReadOnlyDictionary<int, string> keyNames = KeysByName();

        static Dictionary<int, string> KeysByName()
        {
            var names = new Dictionary<int, string>();
            for (int key = 0; key < 128; key++)
                if (DrumsEngine.VoiceOfKey(key) is int voice and >= 0) names[key] = DrumsEngine.Names[voice];
            return names;
        }

        /// <summary>
        /// Hosts (the keyboard under the machine's window, docs/specs/studio-mvp.md §3.12): the name of the voice each
        /// key plays, General MIDI's drum keys with the TR-909's pairs (DrumsEngine.VoiceOfKey); keys not listed play nothing.
        /// </summary>
        public IReadOnlyDictionary<int, string> KeyNames => keyNames;

        // ---- Note map (docs/specs/daw-modes.md §8.9, NM1): the keys that play a voice, by the voice's name ---------------------------

        static readonly string[] voiceGroups = { "Kick", "Snare and clap", "Snare and clap", "Snare and clap", "Hats", "Hats", "Toms", "Toms", "Toms", "Percussion", "Cymbals", "Cymbals" };
        static readonly uint[] voiceColors =
        {
            0xFFE0457B, 0xFFF0A040, 0xFFF0A040, 0xFFF0A040, 0xFF4FC3F7, 0xFF4FC3F7, 0xFF8BC34A, 0xFF8BC34A, 0xFF8BC34A, 0xFFBA68C8, 0xFFFFD54F, 0xFFFFD54F,
        };

        static readonly IReadOnlyList<NoteMapEntry> noteMap = BuildKeys();

        static IReadOnlyList<NoteMapEntry> BuildKeys()
        {
            var entries = new List<NoteMapEntry>();
            for (int key = 0; key < 128; key++)
                if (DrumsEngine.VoiceOfKey(key) is int voice and >= 0)
                    entries.Add(new NoteMapEntry(key, DrumsEngine.Names[voice], voiceGroups[voice], voiceColors[voice]));
            return entries;
        }

        /// <summary>Every key that plays a voice (General MIDI's drum keys with the TR-909's pairs), by the voice's name; the same for every channel.</summary>
        internal override IReadOnlyList<NoteMapEntry> BuildNoteMap(int channel) => noteMap;

        /// <summary>The kit's keys never change.</summary>
        internal override int NoteMapSignature() => 1;

        static readonly IReadOnlyDictionary<int, string> keyShortNames = KeysByShortName();

        static Dictionary<int, string> KeysByShortName()
        {
            var names = new Dictionary<int, string>();
            for (int key = 0; key < 128; key++)
                if (DrumsEngine.VoiceOfKey(key) is int voice and >= 0) names[key] = DrumsEngine.Short[voice];
            return names;
        }

        /// <summary>Hosts (the pattern editor, ux-overhaul.md P3c): the same keys by their hit columns' names, "BD", which fit the tracker's three characters.</summary>
        public IReadOnlyDictionary<int, string> KeyShortNames => keyShortNames;

        /// <summary>The editor's pads (any thread): a hit at the next block, as a MIDI note on the voice's key.</summary>
        internal void Audition(int voice, int velocity)
        {
            if ((uint)voice < DrumsEngine.VoiceCount) MidiNote(0, DrumsEngine.Keys[voice], Math.Clamp(velocity, 1, 127));
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            // This tick's accent first: the track notes drained next and the hit columns after take it.
            bool accent = pending.TakeAccent();
            Engine.TickAccent = accent;
            BeginBlock(Engine);
            pending.Drain(Engine, accent);
            Engine.TickAccent = false;
            n = Math.Min(n, DrumsEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Drums...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Drums — a synthesised drum kit in the TR-909 and TR-808 manner: kick, snare, clap, rim, closed " +
                        "and open hats (the closed one chokes the open one), three toms, cowbell, crash and ride, each with " +
                        "its tune, decay, tone, level and pan. It plays the General MIDI drum keys from C2, as Neo Sampler's " +
                        "Kit mode does, and per-track hit columns (BD, SD, … at a velocity) with an Accent column, as Buzz " +
                        "drum machines do. Velocity sets the level and opens the tone.\n\nPart of neo-buzz (docs/specs/machines.md).",
                        "Neo Drums"),
                },
            },
        };
    }

#if AVALONIA   // fork: the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoDrumsEditor();
    }
#endif
}
