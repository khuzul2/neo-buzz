#nullable disable
// Neo Sampler (docs/specs/machines.md §5): plays the song's waves (wavetable slots) and built-in
// samples in 16 zones; Kit mode makes it a drum machine. 32 tracks of Note and Velocity, plus MIDI.
// (docs/specs/mcp-server.md §12.5).
// DSP in Engine/, the wavetable's waves in through WaveSource.cs, parameters generated from
// Params.def, editor in NeoSamplerEditor.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Sampler
{
    [MachineDecl(Name = "Neo Sampler", ShortName = "Sampler", Author = "neo-buzz", MaxTracks = 32, InputCount = 0, OutputCount = 1)]
    public partial class NeoSamplerMachine : NeoSynthBase, IBuzzMachine, IDisposable, INoteSampleProvider
    {
        readonly float[] left = new float[SamplerEngine.MaxBlock], right = new float[SamplerEngine.MaxBlock];
        int rate;

        public NeoSamplerMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            SamplerText.Install(Params.Table);
            Bank = new SampleBank();
            Engine = new SamplerEngine(Values, Bank);
            Source = new WaveSource(() => Wavetable, Bank, ZoneWave);
            SamplerText.SongWaveName = Source.NameOf;
            // The built-in samples are made once per process, in the background, not when a note first asks.
            BuiltIns.StartBuilding();
        }

        // ---- Tracker mode (TM1): a sample per note -----------------------------------------------------------------------------------

        /// <summary>The track parameters after Note and Velocity (generated: Sample, Offset, Offset hi, Tune) go to the next note on their track.</summary>
        void OnTrackParameter(int parameter, int track, int value)
        {
            int kind = parameter switch
            {
                TP.Sample => NoteInput.ExtraSample,
                TP.Offset => NoteInput.ExtraOffset,
                TP.OffsetHi => NoteInput.ExtraOffsetHi,
                TP.Tune => NoteInput.ExtraTune,
                _ => -1,
            };
            if (kind >= 0) Notes.SetExtra(track, kind, value);
        }

        /// <summary>Versioned state (machines.md §3.4): version 1 added the Tracker mode and the Sample, Offset, Offset hi and Tune track parameters, after the old ones, so a song saved before opens as it was.</summary>
        internal override StateMigrations Migrations => migrations;

        static readonly StateMigrations migrations = new StateMigrations(Params.Table)
            .Step(Params.Table.Select(p => p.Id).ToArray(), step => { });

        /// <summary>Whether the notes name their samples (Mode: Tracker): the roll then shows a chip on each note.</summary>
        public bool PlaysSamplePerNote => Raw(P.Mode) == SamplerEngine.ModeTracker;

        public int NoteSampleVersion
        {
            get
            {
                int h = NoteMapHash.Add(NoteMapHash.Seed, Raw(P.Mode));
                try
                {
                    var waves = Wavetable?.Waves;
                    if (waves != null)
                        for (int i = 0; i < waves.Count && i < SampleBank.SongSlots; i++)
                        {
                            var w = waves[i];
                            if (w == null || w.Layers.Count == 0) continue;
                            h = NoteMapHash.Add(h, i + 1);
                            h = NoteMapHash.Add(h, w.Name ?? "");
                            h = NoteMapHash.Add(h, w.Layers[0].SampleCount);
                            h = NoteMapHash.Add(h, Bank.Get(i + 1) != null ? 1 : 0);
                        }
                }
                catch (Exception) { }
                h = NoteMapHash.Add(h, BuiltIns.Ready ? 1 : 0);
                return h;
            }
        }

        const int PeakColumns = 64;

        public IReadOnlyList<NoteSample> ReadNoteSamples()
        {
            var list = new List<NoteSample>();
            try
            {
                var waves = Wavetable?.Waves;
                if (waves != null)
                    for (int i = 0; i < waves.Count && i < SampleBank.SongSlots; i++)
                    {
                        var w = waves[i];
                        if (w == null || w.Layers.Count == 0 || w.Layers[0].SampleCount < 1) continue;
                        string name = string.IsNullOrWhiteSpace(w.Name) ? "Slot " + (i + 1) : w.Name.Trim();
                        var layer = Bank.Get(i + 1)?.Layers is { Length: > 0 } layers ? layers[0] : null;
                        list.Add(new NoteSample(i + 1, name, w.Layers[0].SampleCount, w.Layers[0].SampleRate, Peaks(layer)));
                    }
            }
            catch (Exception) { }
            for (int n = 1; n <= BuiltIns.Count; n++)
            {
                var slot = BuiltIns.Get(n);
                var layer = slot?.Layers.Length > 0 ? slot.Layers[0] : null;
                list.Add(new NoteSample(SampleBank.SongSlots + n, BuiltIns.Names[n - 1], layer?.Length ?? 0, layer?.Rate ?? BuiltIns.Rate, Peaks(layer)));
            }
            return list;
        }

        /// <summary>The wave's outline: the largest absolute value in each of 64 parts, as a share of the largest.</summary>
        static float[] Peaks(SampleLayer layer)
        {
            var peaks = new float[PeakColumns];
            var ov = layer?.Overview;
            if (ov == null) return peaks;
            int per = SampleLayer.OverviewColumns / PeakColumns;
            float top = 0;
            for (int c = 0; c < PeakColumns; c++)
            {
                float m = 0;
                for (int k = c * per; k < (c + 1) * per; k++) m = Math.Max(m, Math.Max(Math.Abs(ov[2 * k]), Math.Abs(ov[2 * k + 1])));
                peaks[c] = m;
                top = Math.Max(top, m);
            }
            if (top > 0)
                for (int c = 0; c < PeakColumns; c++) peaks[c] /= top;
            return peaks;
        }

        public void PreviewNoteSample(int number, bool on) => Notes.PreviewSample(number, on);

        internal SamplerEngine Engine { get; }
        internal SampleBank Bank { get; }
        internal WaveSource Source { get; }

        /// <summary>The song's wavetable (null before the machine is in a song).</summary>
        internal IWavetable Wavetable
        {
            get
            {
                try { return host?.Machine?.Graph?.Buzz?.Song?.Wavetable; }
                catch (Exception) { return null; }
            }
        }

        /// <summary>
        /// Agent server (mcp-server.md §3.5): true while the built-in samples are being made or the
        /// song's waves copied in. Renders ask first (on the song's thread, where reading the waves is
        /// safe), and asking copies them all at once, so a render starts with every wave.
        /// </summary>
        public bool Busy => !BuiltIns.Ready || Source.Synchronise();

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            // Under the audio lock: the song's waves can't change while they are copied in.
            Source.Pump(WaveSource.FramesPerBlock);
            BeginBlock(Engine);
            n = Math.Min(n, SamplerEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public void Dispose() => Source.Dispose();

        /// <summary>
        /// Hosts (the keyboard under the machine's window, docs/specs/studio-mvp.md §3.12): in Kit mode, the key each zone
        /// with a wave plays (Kit base + zone − 1) by its wave's name ("Kick", a song wave's own); null in Instrument mode,
        /// where every key plays. The same names as the note map's (docs/specs/daw-modes.md §8.9).
        /// </summary>
        public IReadOnlyDictionary<int, string> KeyNames
        {
            get
            {
                if (Raw(P.Mode) != 1) return null;
                var names = new Dictionary<int, string>();
                foreach (var e in BuildNoteMap(-1) ?? Array.Empty<NoteMapEntry>())
                    if (e.Name != null) names[e.Key] = e.Name;
                return names;
            }
        }

        // ---- Note map (docs/specs/daw-modes.md §8.9, NM1): the keys that make a sound, from the zones --------------------------------

        /// <summary>
        /// Kit mode: the key of each zone that has a wave (Kit base + zone − 1), named by its wave ("Kick"). Instrument mode: every key
        /// inside a zone's key range (a zone with a wave and a velocity range that plays something), grouped by the first zone's wave.
        /// Keys outside every zone make no sound. Read from the host's parameter values (a value just set shows at once), off the audio thread.
        /// </summary>
        internal override IReadOnlyList<NoteMapEntry> BuildNoteMap(int channel)
        {
            var entries = new List<NoteMapEntry>();
            bool kit = Raw(P.Mode) == 1;
            int first = Math.Clamp(Raw(P.KitBase), 0, 127);
            var seen = new bool[128];
            for (int z = 0; z < SamplerEngine.ZoneCount; z++)
            {
                int wave = Raw(SamplerEngine.ZoneParam(z, SamplerEngine.FWave));
                if (wave <= 0) continue;
                string name = ZoneName(wave);
                if (kit)
                {
                    int key = first + z;
                    if (key <= 127 && !seen[key]) { seen[key] = true; entries.Add(new NoteMapEntry(key, name, "Kit")); }
                    continue;
                }
                int velLo = Raw(SamplerEngine.ZoneParam(z, SamplerEngine.FVelLo)), velHi = Raw(SamplerEngine.ZoneParam(z, SamplerEngine.FVelHi));
                if (Math.Max(velLo, velHi) < 1) continue;       // no velocity plays it
                int lo = Raw(SamplerEngine.ZoneParam(z, SamplerEngine.FKeyLo)), hi = Raw(SamplerEngine.ZoneParam(z, SamplerEngine.FKeyHi));
                for (int key = Math.Max(0, Math.Min(lo, hi)); key <= Math.Min(127, Math.Max(lo, hi)); key++)
                    if (!seen[key]) { seen[key] = true; entries.Add(new NoteMapEntry(key, null, name)); }
            }
            entries.Sort((a, b) => a.Key.CompareTo(b.Key));
            return entries;
        }

        /// <summary>A hash of what the map follows (mode, Kit base, each zone's wave, name and ranges): cheap, so the host can poll it.</summary>
        internal override int NoteMapSignature()
        {
            int h = NoteMapHash.Add(NoteMapHash.Seed, Raw(P.Mode));
            h = NoteMapHash.Add(h, Raw(P.KitBase));
            for (int z = 0; z < SamplerEngine.ZoneCount; z++)
            {
                int wave = Raw(SamplerEngine.ZoneParam(z, SamplerEngine.FWave));
                h = NoteMapHash.Add(h, wave);
                if (wave <= 0) continue;
                h = NoteMapHash.Add(h, ZoneName(wave));
                for (int f = SamplerEngine.FKeyLo; f <= SamplerEngine.FVelHi; f++) h = NoteMapHash.Add(h, Raw(SamplerEngine.ZoneParam(z, f)));
            }
            return h;
        }

        /// <summary>A zone's wave as a key name: a built-in sample's ("Kick"), a song wave's own name, or "Slot 12" when it has none.</summary>
        string ZoneName(int wave)
        {
            if (wave > SampleBank.SongSlots)
            {
                int n = wave - SampleBank.SongSlots;
                return n <= BuiltIns.Count ? BuiltIns.Names[n - 1] : "Sample " + n;
            }
            string name = Source.NameOf(wave);
            return string.IsNullOrWhiteSpace(name) ? "Slot " + wave : name.Trim();
        }

        IParameter[] globals;

        /// <summary>A global parameter's value as the host holds it (a value just set, before the engine's next tick); the engine's when there is no host.</summary>
        int Raw(int index)
        {
            try
            {
                var list = globals ??= HostGlobals();
                if (list != null && index < list.Length) return list[index].GetValue(0);
            }
            catch (Exception) { }
            return Values[index];
        }

        IParameter[] HostGlobals()
        {
            var groups = host?.Machine?.ParameterGroups;
            if (groups == null || groups.Count < 2) return null;
            var found = new List<IParameter>(groups[1].Parameters);
            return found.Count == Params.Table.Length ? found.ToArray() : null;
        }

        IParameter[] zoneWaves;

        /// <summary>
        /// Zone z's wave as the host holds it: a value set (param_set, automation) reaches the engine at the next
        /// tick, the host at once, so a render waiting on Busy reads in a wave a zone has just been given. Off the audio thread
        /// (WaveSource's scans); the engine's value where there's no host.
        /// </summary>
        int ZoneWave(int z)
        {
            try
            {
                var ps = zoneWaves ??= HostZoneWaves();
                if (ps != null) return ps[z].GetValue(0);
            }
            catch (Exception) { }
            return Values[SamplerEngine.ZoneParam(z, SamplerEngine.FWave)];
        }

        IParameter[] HostZoneWaves()
        {
            var groups = host?.Machine?.ParameterGroups;
            if (groups == null || groups.Count < 2) return null;
            var globals = groups[1].Parameters;
            var found = new IParameter[SamplerEngine.ZoneCount];
            for (int z = 0; z < found.Length; z++)
            {
                int i = SamplerEngine.ZoneParam(z, SamplerEngine.FWave);
                if (i >= globals.Count || globals[i].Name != $"Z{z + 1} wave") return null;
                found[z] = globals[i];
            }
            return found;
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Sampler...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Sampler — plays the song's waves (its wavetable, saved with the song) and built-in " +
                        "samples in 16 zones by key and velocity, each with its root, tune, level, pan, start and end, " +
                        "a loop with crossfade, gate or one-shot, reverse and a choke group; a slot's layers split the " +
                        "keys by root or alternate round robin. Kit mode puts zone n on key Kit base + n − 1 as a " +
                        "one-shot drum. Amp and filter envelopes, velocity to level and filter, pitch bend, 32 voices. " +
                        "32 tracks (Note, Velocity) and MIDI.\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo Sampler"),
                },
            },
        };
    }

#if AVALONIA   // fork: the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoSamplerEditor();
    }
#endif
}
