#nullable disable
// Dye Essence (docs/specs/dye-essence.md): the hybrid wavetable synth, a Neo-family machine. 16
// tracks of Note and Velocity, plus MIDI. Each engine slot can play a user wavetable (from a WAV
// file), and each LFO a drawn shape, both kept in the machine state, so songs and presets carry
// them. DSP in Engine/ (NoteBank, the lanes), parameters generated from Params.def, the editor in
// DyeEssenceEditor.cs (its pages) and the other *Editor.cs files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;
using NeoBuzz.Wave;

namespace NeoBuzz.DyeEssence
{
    /// <summary>
    /// Saved with the song: each slot's user wavetable (32-bit float samples, base64) and each LFO's drawn
    /// shape (DrawnShape's text; empty: the default). Songs from before a field load it empty.
    /// </summary>
    public class DyeEssenceState
    {
        public string TableAName { get; set; } = "";
        public int TableAFrameSize { get; set; }
        public string TableAData { get; set; } = "";
        public string TableBName { get; set; } = "";
        public int TableBFrameSize { get; set; }
        public string TableBData { get; set; } = "";
        public string TableCName { get; set; } = "";
        public int TableCFrameSize { get; set; }
        public string TableCData { get; set; } = "";
        public string SampleAName { get; set; } = "";
        public int SampleARate { get; set; }
        public bool SampleAStereo { get; set; }
        public string SampleAData { get; set; } = "";
        public string SampleBName { get; set; } = "";
        public int SampleBRate { get; set; }
        public bool SampleBStereo { get; set; }
        public string SampleBData { get; set; } = "";
        public string SampleCName { get; set; } = "";
        public int SampleCRate { get; set; }
        public bool SampleCStereo { get; set; }
        public string SampleCData { get; set; } = "";
        /// <summary>
        /// The Spectral engine's analysis of each slot's user sample (SpectralModel's text: quantised partials and noise bands, deflated, base64), made when
        /// the sample is loaded so a reloaded song gives the same sound without analysing again. Empty: none yet (a song from before P5a is analysed on load).
        /// </summary>
        public string SpectrumAData { get; set; } = "";
        public string SpectrumBData { get; set; } = "";
        public string SpectrumCData { get; set; } = "";
        public string Lfo1Shape { get; set; } = "";
        public string Lfo2Shape { get; set; } = "";
        public string Lfo3Shape { get; set; } = "";
        public string Lfo4Shape { get; set; } = "";
        /// <summary>The sequencer's steps (SeqPattern's text; empty: the default, 32 plain steps).</summary>
        public string SeqSteps { get; set; } = "";
        /// <summary>The effects rack's chains (FxRouting's text: "1 2 3 4 | 5 6 7 | 8 9 10", slots from 1; empty: that default).</summary>
        public string FxRouting { get; set; } = "";
        /// <summary>The convolution reverb's imported impulse response (24-bit PCM, base64; mono or stereo; at most 10 s at 48 kHz, 480000 frames).</summary>
        public string IrName { get; set; } = "";
        public int IrRate { get; set; }
        public bool IrStereo { get; set; }
        public string IrData { get; set; } = "";
        /// <summary>The vocoder's imported modulator (mono 24-bit PCM, base64; at most 20 s at 48 kHz).</summary>
        public string ModulatorName { get; set; } = "";
        public int ModulatorRate { get; set; }
        public string ModulatorData { get; set; } = "";

        internal DyeEssenceState WithIr(string name, int rate, bool stereo, string data)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            s.IrName = name; s.IrRate = rate; s.IrStereo = stereo; s.IrData = data;
            return s;
        }

        internal DyeEssenceState WithModulator(string name, int rate, string data)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            s.ModulatorName = name; s.ModulatorRate = rate; s.ModulatorData = data;
            return s;
        }

        internal DyeEssenceState WithRouting(string text)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            s.FxRouting = text;
            return s;
        }

        internal DyeEssenceState WithSteps(string text)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            s.SeqSteps = text;
            return s;
        }

        internal string Shape(int lfo) => lfo switch { 0 => Lfo1Shape, 1 => Lfo2Shape, 2 => Lfo3Shape, _ => Lfo4Shape } ?? "";

        internal DyeEssenceState WithShape(int lfo, string text)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            switch (lfo)
            {
                case 0: s.Lfo1Shape = text; break;
                case 1: s.Lfo2Shape = text; break;
                case 2: s.Lfo3Shape = text; break;
                default: s.Lfo4Shape = text; break;
            }
            return s;
        }

        internal (string Name, int FrameSize, string Data) Get(int slot) => slot switch
        {
            0 => (TableAName, TableAFrameSize, TableAData),
            1 => (TableBName, TableBFrameSize, TableBData),
            _ => (TableCName, TableCFrameSize, TableCData),
        };

        internal (string Name, int Rate, bool Stereo, string Data) GetSample(int slot) => slot switch
        {
            0 => (SampleAName, SampleARate, SampleAStereo, SampleAData),
            1 => (SampleBName, SampleBRate, SampleBStereo, SampleBData),
            _ => (SampleCName, SampleCRate, SampleCStereo, SampleCData),
        };

        internal DyeEssenceState WithSample(int slot, string name, int rate, bool stereo, string data)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            // A new sample has no analysis yet.
            switch (slot)
            {
                case 0: s.SampleAName = name; s.SampleARate = rate; s.SampleAStereo = stereo; s.SampleAData = data; s.SpectrumAData = ""; break;
                case 1: s.SampleBName = name; s.SampleBRate = rate; s.SampleBStereo = stereo; s.SampleBData = data; s.SpectrumBData = ""; break;
                default: s.SampleCName = name; s.SampleCRate = rate; s.SampleCStereo = stereo; s.SampleCData = data; s.SpectrumCData = ""; break;
            }
            return s;
        }

        internal string GetSpectrum(int slot) => slot switch { 0 => SpectrumAData, 1 => SpectrumBData, _ => SpectrumCData };

        internal DyeEssenceState WithSpectrum(int slot, string data)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            switch (slot)
            {
                case 0: s.SpectrumAData = data; break;
                case 1: s.SpectrumBData = data; break;
                default: s.SpectrumCData = data; break;
            }
            return s;
        }

        internal DyeEssenceState With(int slot, string name, int frameSize, string data)
        {
            var s = (DyeEssenceState)MemberwiseClone();
            switch (slot)
            {
                case 0: s.TableAName = name; s.TableAFrameSize = frameSize; s.TableAData = data; break;
                case 1: s.TableBName = name; s.TableBFrameSize = frameSize; s.TableBData = data; break;
                default: s.TableCName = name; s.TableCFrameSize = frameSize; s.TableCData = data; break;
            }
            return s;
        }
    }

    [MachineDecl(Name = "Dye Essence", ShortName = "Dye", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class DyeEssenceMachine : NeoSynthBase, IBuzzMachine
    {
        internal static readonly string[] SlotNames = { "A", "B", "C" };
        static readonly int[] TableParam = { P.ATable, P.BTable, P.CTable };
        internal static readonly int[] LfoShapeParam = { P.L1Shape, P.L2Shape, P.L3Shape, P.L4Shape };
        internal const int Lfos = 4;

        readonly float[] left = new float[DyeEngine.MaxBlock], right = new float[DyeEngine.MaxBlock];
        int rate;
        DyeEssenceState state = new DyeEssenceState();

        /// <summary>The longest single cycle a file may hold (longer ones are sounds, not cycles).</summary>
        internal const int MaxSingleCycle = 8192;

        public DyeEssenceMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new DyeEngine(Values);
            // Build the built-in tables in the background now, not when the audio thread first asks.
            for (int i = 0; i < TableBank.Count; i++) TableBank.Get(i);
            for (int i = 0; i < SampleSources.Count; i++) SampleSources.Get(i);
        }

        internal DyeEngine Engine { get; }

        /// <summary>Slot <paramref name="slot"/>'s user table's name ("" when none is loaded).</summary>
        internal string UserTableName(int slot) => state.Get(slot).Name;

        /// <summary>The "User" choice of a slot's table parameter.</summary>
        internal static int UserChoice => TableBank.Count;

        internal static int TableParameter(int slot) => TableParam[slot];

        static readonly int[] SampleParam = { P.ASmpSrc, P.BSmpSrc, P.CSmpSrc };
        static readonly int[] TypeParam = { P.AType, P.BType, P.CType };

        /// <summary>Slot <paramref name="slot"/>'s user sample's name ("" when none is loaded).</summary>
        internal string UserSampleName(int slot) => state.GetSample(slot).Name;

        /// <summary>The "User" choice of a slot's sample parameter.</summary>
        internal static int UserSampleChoice => SampleSources.Count;

        internal static int SampleParameter(int slot) => SampleParam[slot];

        public DyeEssenceState MachineState
        {
            get => state;
            set
            {
                state = value ?? new DyeEssenceState();
                for (int l = 0; l < Lfos; l++) Engine.SetLfoShape(l, DrawnShape.Parse(state.Shape(l)));
                Engine.SetPattern(SeqPattern.Parse(state.SeqSteps));
                Engine.Fx.SetRouting(FxRouting.Parse(state.FxRouting));
                LoadRackClips();
                ShapesVersion++;
                for (int slot = 0; slot < SlotNames.Length; slot++)
                {
                    var (name, frameSize, data) = state.Get(slot);
                    try
                    {
                        if (!string.IsNullOrEmpty(data) && data.Length >= 64)
                        {
                            var bytes = Convert.FromBase64String(data);
                            var samples = new float[bytes.Length / 4];
                            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
                            Engine.SetUserTable(slot, new TableArena(WavReader.ToTable(name, samples, frameSize)), name);
                        }
                        else Engine.SetUserTable(slot, null, "");
                    }
                    catch (Exception)
                    {
                        // A damaged table loads as "no user table" rather than failing the song.
                        Engine.SetUserTable(slot, null, "");
                    }
                    var (sname, srate, sstereo, sdata) = state.GetSample(slot);
                    try
                    {
                        if (!string.IsNullOrEmpty(sdata) && srate > 0)
                        {
                            var (l, r) = SampleSources.Decode(sdata, sstereo);
                            Engine.SetUserSample(slot, l.Length >= 16 ? SampleSources.Layer(sname, l, r, srate) : null, sname);
                            // The Spectral engine's model: as the song kept it, else made now (a song from before the engine, or a damaged text).
                            var model = SpectralModel.FromBase64(state.GetSpectrum(slot), sname);
                            if (model != null && model.Length == l.Length && model.Rate == srate) { CancelAnalysis(slot); Engine.SetUserModel(slot, model); }
                            else if (l.Length >= 16) AnalyseSample(slot, sname, l, r, srate);
                            else Engine.SetUserModel(slot, null);
                        }
                        else { CancelAnalysis(slot); Engine.SetUserSample(slot, null, ""); Engine.SetUserModel(slot, null); }
                    }
                    catch (Exception)
                    {
                        CancelAnalysis(slot);
                        Engine.SetUserSample(slot, null, "");
                        Engine.SetUserModel(slot, null);
                    }
                }
            }
        }

        /// <summary>The rack's imported clips (the convolution's response, the vocoder's modulator) from the state, damaged ones as none.</summary>
        void LoadRackClips()
        {
            var user = Engine.Fx.User;
            try
            {
                if (!string.IsNullOrEmpty(state.IrData) && state.IrRate > 0)
                {
                    var (l, r) = SampleSources.Decode(state.IrData, state.IrStereo);
                    user.SetIr(state.IrName, l.Length >= 16 ? l : null, state.IrStereo ? r : null, state.IrRate);
                }
                else user.SetIr("", null, null, 0);
            }
            catch (Exception)
            {
                user.SetIr("", null, null, 0);
            }
            try
            {
                if (!string.IsNullOrEmpty(state.ModulatorData) && state.ModulatorRate > 0)
                {
                    var (l, _) = SampleSources.Decode(state.ModulatorData, false);
                    user.SetModulator(state.ModulatorName, l.Length >= 16 ? l : null, state.ModulatorRate);
                }
                else user.SetModulator("", null, 0);
            }
            catch (Exception)
            {
                user.SetModulator("", null, 0);
            }
        }

        /// <summary>The imported impulse response's name ("" when none).</summary>
        internal string UserIrName => state.IrName;

        /// <summary>The imported modulator's name ("" when none).</summary>
        internal string UserModulatorName => state.ModulatorName;

        /// <summary>
        /// Loads a WAV file as the convolution reverb's impulse response (UI thread): mono or stereo, kept as 24-bit PCM in the machine state, at most
        /// <see cref="FxUserData.MaxIrFrames"/> frames (10 s at 48 kHz; the rest is cut and the text says so). Throws with a readable message on bad files.
        /// </summary>
        internal string LoadUserIr(string path)
        {
            var (left, right, rate) = SampleSources.ReadWav(File.ReadAllBytes(path));
            if (left.Length < 16) throw new InvalidDataException("the file holds no audio");
            int keep = Math.Min(left.Length, FxUserData.MaxIrFrames);
            string note = keep < left.Length ? $" The file is longer than {FxUserData.MaxIrFrames} frames ({FxUserData.MaxIrFrames / (double)rate:0.0} s at {rate} Hz, the limit for a response); the first {keep} frames are kept." : "";
            if (keep < left.Length)
            {
                Array.Resize(ref left, keep);
                if (right != null) Array.Resize(ref right, keep);
            }
            string name = Path.GetFileNameWithoutExtension(path);
            string data = SampleSources.Encode(left, right);
            // What plays is what the state keeps (24-bit), so a reloaded song sounds the same.
            var (l, r) = SampleSources.Decode(data, right != null);
            state = state.WithIr(name, rate, right != null, data);
            Engine.Fx.User.SetIr(name, l, right != null ? r : null, rate);
            ShapesVersion++;
            return note;
        }

        /// <summary>Loads a WAV file as the vocoder's modulator (UI thread): mixed to mono, 24-bit PCM in the state, at most <see cref="FxUserData.MaxModulatorFrames"/> frames.</summary>
        internal string LoadUserModulator(string path)
        {
            var (left, right, rate) = SampleSources.ReadWav(File.ReadAllBytes(path));
            if (left.Length < 16) throw new InvalidDataException("the file holds no audio");
            int keep = Math.Min(left.Length, FxUserData.MaxModulatorFrames);
            string note = keep < left.Length ? $" The file is longer than {FxUserData.MaxModulatorFrames} frames ({FxUserData.MaxModulatorFrames / (double)rate:0.0} s at {rate} Hz); the first {keep} frames are kept." : "";
            var mono = new float[keep];
            for (int i = 0; i < keep; i++) mono[i] = right == null ? left[i] : 0.5f * (left[i] + right[i]);
            string name = Path.GetFileNameWithoutExtension(path);
            string data = SampleSources.Encode(mono, null);
            var (l, _) = SampleSources.Decode(data, false);
            state = state.WithModulator(name, rate, data);
            Engine.Fx.User.SetModulator(name, l, rate);
            ShapesVersion++;
            return note;
        }

        /// <summary>
        /// Agent server (mcp-server.md A3): true while the built-in tables are still being built in
        /// the background. Offline renders wait for it, so they don't start silent.
        /// </summary>
        public bool Busy
        {
            get
            {
                if (Engine.TablesBuilding || AnalysisPending) return true;
                int sr = SampleRate;
                if (sr > 0) Engine.Fx.PrepareRate(sr);
                return Engine.Fx.PendingFor(HostFxType, HostFxValue);
            }
        }

        /// <summary>
        /// A rack slot's type as the host holds it now: a set parameter or a preset is there at once, the engine's own values
        /// only at the next audio tick (the renders ask whether the machine is busy before that).
        /// </summary>
        int HostFxType(int slot) => HostFxValue(FxRack.TypeParam(slot));

        /// <summary>A parameter of the rack as the host holds it now (the engine's own value when there is no host machine).</summary>
        int HostFxValue(int index)
        {
            try
            {
                var globals = host?.Machine?.ParameterGroups?.FirstOrDefault(g => g.Type == ParameterGroupType.Global);
                var p = globals != null && index < globals.Parameters.Count ? globals.Parameters[index] : null;
                if (p != null) return p.GetValue(0);
            }
            catch (Exception)
            {
                // No host machine (tests): the engine's own values.
            }
            return Values[index];
        }

        /// <summary>The effects rack's chains.</summary>
        internal FxRouting FxChains => Engine.Fx.Routing;

        /// <summary>Sets the rack's chains (UI thread; the audio thread takes them at its next block). The default clears them from the state.</summary>
        internal void SetRouting(FxRouting routing)
        {
            routing ??= FxRouting.Default;
            state = state.WithRouting(routing.IsDefault ? "" : routing.ToString());
            Engine.Fx.SetRouting(routing);
            ShapesVersion++;
        }

        /// <summary>
        /// The rack's latency (a slot with the clipper has one): the same whatever the audio. The host reads it at every tick on
        /// the audio thread (so no allocation) and its delay compensation counts it for this generator too.
        /// </summary>
        public int GetLatency() => Engine.Fx.Latency;

        /// <summary>LFO <paramref name="lfo"/>'s drawn shape (the default when none was drawn).</summary>
        internal DrawnShape LfoShape(int lfo) => Engine.LfoShape(lfo);

        /// <summary>
        /// Draws LFO <paramref name="lfo"/>'s shape (UI thread; the audio thread takes it at its next block). Null or the
        /// default shape clears it from the state.
        /// </summary>
        internal void SetLfoShape(int lfo, DrawnShape shape)
        {
            shape ??= DrawnShape.Default;
            state = state.WithShape(lfo, shape.SameAs(DrawnShape.Default) ? "" : shape.ToString());
            Engine.SetLfoShape(lfo, shape);
            ShapesVersion++;
        }

        /// <summary>The sequencer's steps.</summary>
        internal SeqPattern Pattern => Engine.Pattern;

        /// <summary>Sets the sequencer's steps (UI thread; the audio thread takes them at its next block).</summary>
        internal void SetPattern(SeqPattern pattern)
        {
            pattern ??= SeqPattern.Default;
            state = state.WithSteps(pattern.SameAs(SeqPattern.Default) ? "" : pattern.ToString());
            Engine.SetPattern(pattern);
            ShapesVersion++;
        }

        /// <summary>Counts changes of the drawn shapes (drawn, a song or preset loaded, undone): the editor redraws when it moves.</summary>
        internal int ShapesVersion { get; private set; }

        /// <summary>
        /// Agent server (mcp-server.md A3, machine_data_import): "wavetable" loads a WAV as a slot's user table
        /// (item 0, 1, 2: slot A, B, C) and sets that slot's table to User; "sample" loads a WAV as a slot's
        /// user sample, sets that slot's sample to User and its engine to Sample (unless it is Granular);
        /// "lfo_shape" reads a drawn shape from a text file (item 0–3: LFO 1–4) and sets that LFO's shape to Drawn;
        /// "ir" loads a WAV as the convolution reverb's impulse response and "modulator" a WAV as the vocoder's (item 0–9: the
        /// effect slot, set to that type with its first parameter on User / Sample).
        /// </summary>
        public string ImportData(string kind, string path, int item, Action<int, int> setParameter)
        {
            if (kind == "lfo_shape")
            {
                if (item < 0 || item >= Lfos) throw new ArgumentException($"Dye Essence has {Lfos} LFOs; item {item} doesn't exist (0–{Lfos - 1}: LFO 1–{Lfos})");
                int lfo = item;
                if (!DrawnShape.TryParse(File.ReadAllText(path), out var shape, out string error)) throw new InvalidDataException(error);
                SetLfoShape(lfo, shape);
                setParameter?.Invoke(LfoShapeParam[lfo], (int)WaveShape.Drawn);
                return $"Drew LFO {lfo + 1}'s shape ({shape.Count} points: {shape}) and set LFO{lfo + 1} shape to Drawn.";
            }
            if (kind == "fx_routing")
            {
                if (!FxRouting.TryParse(File.ReadAllText(path).Trim(), out var routing))
                    throw new InvalidDataException("the routing must list the slots 1 to 10 once each in three groups separated by |, as \"1 2 3 4 | 5 6 7 | 8 9 10\" (bus A or insert 1 | bus B or insert 2 | main or aux)");
                SetRouting(routing);
                return $"Set the effects rack's chains: {routing}.";
            }
            if (kind == "ir" || kind == "modulator")
            {
                if (item < 0 || item >= FxRack.Slots) throw new ArgumentException($"Dye Essence has {FxRack.Slots} effect slots; item {item} doesn't exist (0–{FxRack.Slots - 1}: FX1–FX{FxRack.Slots})");
                bool ir = kind == "ir";
                string note = ir ? LoadUserIr(path) : LoadUserModulator(path);
                var type = ir ? FxType.Convolution : FxType.Vocoder;
                int current = Values.Choice(FxRack.TypeParam(item));
                if (current != (int)type) setParameter?.Invoke(FxRack.TypeParam(item), (int)type);
                // The IR / Modulator choice is the slot's first generic parameter: its last choice (User / Sample) is the top of the range.
                setParameter?.Invoke(FxRack.GenericParam(item, 0), 1000);
                string slotName = $"FX{item + 1}";
                return ir
                    ? $"Loaded the impulse response \"{UserIrName}\" ({Engine.Fx.User.Ir?.Left.Length ?? 0} frames at {Engine.Fx.User.Ir?.Rate ?? 0} Hz) and set {slotName} type to Convolution, {slotName} p1 (IR) to User.{note}"
                    : $"Loaded the modulator \"{UserModulatorName}\" ({Engine.Fx.User.Modulator?.Left.Length ?? 0} frames at {Engine.Fx.User.Modulator?.Rate ?? 0} Hz) and set {slotName} type to Vocoder, {slotName} p1 (Modulator) to Sample.{note}";
            }
            if (kind == "sequence")
            {
                if (!SeqPattern.TryParse(File.ReadAllText(path), out var pattern, out string error)) throw new InvalidDataException(error);
                SetPattern(pattern);
                return $"Set the sequencer's steps ({pattern.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} written; the rest are plain steps). Set Seq mode to Notes or Mod to run them.";
            }
            if (kind == "sample")
            {
                if (item < 0 || item >= SlotNames.Length) throw new ArgumentException($"Dye Essence has {SlotNames.Length} slots; item {item} doesn't exist (0 A, 1 B, 2 C)");
                string note = LoadUserSample(item, path);
                setParameter?.Invoke(SampleParam[item], UserSampleChoice);
                int engine = Values.Choice(TypeParam[item]);
                if (engine != (int)SlotType.Sample && engine != (int)SlotType.Granular && engine != (int)SlotType.Spectral) setParameter?.Invoke(TypeParam[item], (int)SlotType.Sample);
                return $"Loaded the sample \"{UserSampleName(item)}\" as slot {SlotNames[item]}'s User sample, and set {SlotNames[item]} sample to User{(engine != (int)SlotType.Sample && engine != (int)SlotType.Granular && engine != (int)SlotType.Spectral ? $" and {SlotNames[item]} engine to Sample" : "")}.{note}";
            }
            if (kind != "wavetable") throw new ArgumentException($"Dye Essence doesn't import \"{kind}\"; it takes wavetable, sample, lfo_shape, sequence, fx_routing, ir and modulator.");
            if (item < 0 || item >= SlotNames.Length) throw new ArgumentException($"Dye Essence has {SlotNames.Length} slots; item {item} doesn't exist (0 A, 1 B, 2 C)");
            int slot = item;
            LoadUserTable(slot, path);
            setParameter?.Invoke(TableParam[slot], UserChoice);
            return $"Loaded the wavetable \"{UserTableName(slot)}\" as slot {SlotNames[slot]}'s User table, and set {SlotNames[slot]} table to User.";
        }

        /// <summary>Loads a WAV file as a slot's user table (UI thread). Throws with a readable message on bad files.</summary>
        internal void LoadUserTable(int slot, string path)
        {
            var (samples, frameSize) = WavReader.Read(File.ReadAllBytes(path));
            if (samples == null || samples.Length < 16) throw new InvalidDataException("the file holds no audio");
            int size = frameSize > 0 ? frameSize : samples.Length >= 2 * Wavetable.FrameSize && samples.Length % Wavetable.FrameSize == 0 ? Wavetable.FrameSize : samples.Length;
            if (size > MaxSingleCycle)
                throw new InvalidDataException($"it reads as one cycle of {samples.Length} samples; wavetables are frames of 2048 samples (or say their frame size in a clm chunk) or one short cycle (up to {MaxSingleCycle})");
            int keep = Math.Min(samples.Length, size * Wavetable.MaxFrames);
            var kept = new float[keep];
            Array.Copy(samples, kept, keep);
            string name = Path.GetFileNameWithoutExtension(path);
            var table = new TableArena(WavReader.ToTable(name, kept, frameSize));
            var bytes = new byte[keep * 4];
            Buffer.BlockCopy(kept, 0, bytes, 0, bytes.Length);
            state = state.With(slot, name, frameSize, Convert.ToBase64String(bytes));
            Engine.SetUserTable(slot, table, name);
        }

        /// <summary>
        /// Loads a WAV file as a slot's user sample (UI thread): mono or stereo, kept as 24-bit PCM in the machine
        /// state, at most <see cref="SampleSources.MaxSeconds"/> s (the rest is cut; the text says so). Throws with a
        /// readable message on bad files. Returns "" or a note for the user.
        /// </summary>
        internal string LoadUserSample(int slot, string path)
        {
            var (left, right, rate) = SampleSources.ReadWav(File.ReadAllBytes(path));
            int keep = (int)Math.Min(left.Length, (long)SampleSources.MaxSeconds * rate);
            string note = keep < left.Length ? $" The file is longer than {SampleSources.MaxSeconds} s; the first {SampleSources.MaxSeconds} s are kept." : "";
            if (keep < left.Length)
            {
                Array.Resize(ref left, keep);
                if (right != null) Array.Resize(ref right, keep);
            }
            string name = Path.GetFileNameWithoutExtension(path);
            string data = SampleSources.Encode(left, right);
            // What plays is what the state keeps (24-bit), so a reloaded song sounds the same.
            var (l, r) = SampleSources.Decode(data, right != null);
            var layer = SampleSources.Layer(name, l, r, rate);
            state = state.WithSample(slot, name, rate, right != null, data);
            Engine.SetUserSample(slot, layer, name);
            Engine.SetUserModel(slot, null);
            AnalyseSample(slot, name, l, r, rate);
            return note;
        }

        // The Spectral engine's analysis of a user sample runs in the background (one per slot; a new sample cancels the old one's) and, when done, is kept
        // in the machine state and handed to the engine. Not on the audio thread: the audio keeps the slot silent (or on its previous model) until it is done.
        readonly System.Threading.CancellationTokenSource[] analysing = new System.Threading.CancellationTokenSource[3];
        readonly float[] analysisProgress = { -1f, -1f, -1f };
        int analysesRunning;

        /// <summary>True while a user sample is being analysed (offline renders and the agent wait for it).</summary>
        internal bool AnalysisPending => System.Threading.Volatile.Read(ref analysesRunning) > 0;

        /// <summary>How far slot <paramref name="slot"/>'s analysis is, 0 … 1 (negative: none running).</summary>
        internal float AnalysisProgress(int slot) => System.Threading.Volatile.Read(ref analysisProgress[slot]);

        void CancelAnalysis(int slot)
        {
            var old = System.Threading.Interlocked.Exchange(ref analysing[slot], null);
            old?.Cancel();
        }

        void AnalyseSample(int slot, string name, float[] left, float[] right, int rate)
        {
            CancelAnalysis(slot);
            var cts = new System.Threading.CancellationTokenSource();
            System.Threading.Volatile.Write(ref analysing[slot], cts);
            var mono = new float[left.Length];
            for (int i = 0; i < mono.Length; i++) mono[i] = right == null ? left[i] : 0.5f * (left[i] + right[i]);
            System.Threading.Interlocked.Increment(ref analysesRunning);
            System.Threading.Volatile.Write(ref analysisProgress[slot], 0f);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var model = SpectralAnalysis.Analyse(mono, rate, p => System.Threading.Volatile.Write(ref analysisProgress[slot], p), cts.Token, name);
                    if (cts.IsCancellationRequested) return;
                    string text = model.ToBase64();
                    DyeEssenceState cur, next;
                    do
                    {
                        cur = state;
                        // Only while the slot still holds this sample.
                        if (cur.GetSample(slot).Data.Length == 0 || cur.GetSample(slot).Rate != rate) return;
                        next = cur.WithSpectrum(slot, text);
                    } while (!ReferenceEquals(System.Threading.Interlocked.CompareExchange(ref state, next, cur), cur));
                    Engine.SetUserModel(slot, model);
                }
                catch (OperationCanceledException) { }
                catch (Exception) { Engine.SetUserModel(slot, null); }
                finally
                {
                    System.Threading.Interlocked.CompareExchange(ref analysing[slot], null, cts);
                    System.Threading.Volatile.Write(ref analysisProgress[slot], -1f);
                    System.Threading.Interlocked.Decrement(ref analysesRunning);
                }
            });
        }

        /// <summary>Where the song is now (valid in Work), for the arpeggiator and the sequencer, which follow it.</summary>
        DriverTransport Transport()
        {
            var t = new DriverTransport();
            try
            {
                var info = host?.MasterInfo;
                var buzz = host?.Machine?.Graph?.Buzz;
                if (info != null && buzz != null)
                {
                    t.Playing = buzz.Playing;
                    t.Tick = buzz.Song?.PlayPosition ?? 0;
                    t.PosInTick = info.PosInTick;
                    t.SamplesPerTick = info.SamplesPerTick;
                    t.TicksPerBeat = info.TicksPerBeat;
                }
            }
            catch
            {
                t.Playing = false;
            }
            return t;
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            Engine.Tempo = BeatsPerMinute > 0 ? BeatsPerMinute : 120;
            Engine.Transport = Transport();
            BeginBlock(Engine);
            n = Math.Min(n, DyeEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Dye Essence...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoAbout.Show(
                        "Dye Essence — hybrid wavetable synth: three engine slots, each a morphing wavetable or an " +
                        "analog oscillator with 1–16 unison voices and two warps (bend, squeeze, mirror, asym, sync, " +
                        "window sync, FM, PM, AM, ring), a sub and a noise, two filters per note (ladder, state-variable, " +
                        "comb) from serial to parallel, oversampled 2× or 4×; two modulation envelopes, four LFOs " +
                        "(built-in or drawn shapes), four macros and sixteen routes. Ten generated tables plus your own " +
                        "(WAV files of 2048-sample frames, a clm chunk's frame size, or a single cycle).\n\nPart of " +
                        "neo-buzz (docs/specs/dye-essence.md).", "Dye Essence"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new DyeEssenceEditor();
    }
#endif
}
