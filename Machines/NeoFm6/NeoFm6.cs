#nullable disable
// Neo FM6 (docs/specs/machines.md §5): the six-operator FM machine. 16 tracks of Note and
// Velocity, plus MIDI. A DX7 bank loaded in the editor is kept in the machine state (so the song
// carries it); its voices load onto the parameters, which keep the DX7's own values. DSP in
// Engine/, parameters generated from Params.def, editor in NeoFm6Editor.cs.

using System;
using System.Collections.Generic;
using System.IO;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Fm6
{
    /// <summary>Saved with the song: the DX7 bank loaded in the editor (155 bytes a voice, base64).</summary>
    public class NeoFm6State
    {
        public string BankName { get; set; } = "";
        public string BankData { get; set; } = "";
    }

    [MachineDecl(Name = "Neo FM6", ShortName = "FM6", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoFm6Machine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[FmEngine.MaxBlock], right = new float[FmEngine.MaxBlock];
        int rate;
        NeoFm6State state = new NeoFm6State();
        List<Sysex.Voice> bank = new List<Sysex.Voice>();

        public NeoFm6Machine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new FmEngine(Values);
        }

        internal FmEngine Engine { get; }

        /// <summary>The loaded bank's voices (empty when none is loaded).</summary>
        internal IReadOnlyList<Sysex.Voice> Bank => bank;
        internal string BankName => state.BankName;

        public NeoFm6State MachineState
        {
            get => state;
            set
            {
                state = value ?? new NeoFm6State();
                bank = new List<Sysex.Voice>();
                try
                {
                    var data = string.IsNullOrEmpty(state.BankData) ? Array.Empty<byte>() : Convert.FromBase64String(state.BankData);
                    for (int at = 0; at + Sysex.VoiceSize <= data.Length; at += Sysex.VoiceSize)
                    {
                        var v = new byte[Sysex.VoiceSize];
                        Array.Copy(data, at, v, 0, Sysex.VoiceSize);
                        bank.AddRange(Sysex.Read(v));
                    }
                }
                catch (Exception)
                {
                    // A damaged state loads as "no bank" rather than failing the song.
                    bank = new List<Sysex.Voice>();
                }
            }
        }

        /// <summary>Loads a .syx file (a 32-voice bank or one voice) as the bank. UI thread; throws with a readable message.</summary>
        internal void LoadBank(string path)
        {
            var voices = Sysex.Read(File.ReadAllBytes(path));
            var data = new byte[voices.Count * Sysex.VoiceSize];
            for (int i = 0; i < voices.Count; i++) Array.Copy(voices[i].Data, 0, data, i * Sysex.VoiceSize, Sysex.VoiceSize);
            bank = voices;
            state = new NeoFm6State { BankName = Path.GetFileNameWithoutExtension(path), BankData = Convert.ToBase64String(data) };
        }

        /// <summary>
        /// Agent server (mcp-server.md A3, machine_data_import): "dx7_bank" loads a .syx bank and
        /// plays voice <paramref name="item"/> (1–32, 0 = 1) by setting its parameters through
        /// <paramref name="setParameter"/> (index, raw value), so the host can make them undoable.
        /// </summary>
        public string ImportData(string kind, string path, int item, Action<int, int> setParameter)
        {
            if (kind != "dx7_bank") throw new ArgumentException($"Neo FM6 doesn't import \"{kind}\"; it takes dx7_bank.");
            LoadBank(path);
            if (bank.Count == 0) return $"Loaded {Path.GetFileName(path)}, which holds no voices.";
            int voice = item <= 0 ? 1 : item;
            if (voice > bank.Count) throw new ArgumentException($"the bank has {bank.Count} voice(s); item {voice} doesn't exist");
            foreach (var (p, value) in Sysex.ToParams(bank[voice - 1].Data)) setParameter(p, value);
            return $"Loaded the bank {state.BankName} ({bank.Count} voices) and set voice {voice}, \"{bank[voice - 1].Name.Trim()}\".";
        }

        /// <summary>Writes the current parameters as a DX7 single-voice .syx file.</summary>
        internal void ExportVoice(string path, string name)
        {
            File.WriteAllBytes(path, Sysex.WriteVoice(Sysex.FromParams(p => Values[p], name)));
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            BeginBlock(Engine);
            n = Math.Min(n, FmEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo FM6...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo FM6 — six-operator FM synth: the 32 DX7 algorithms, DX7 envelopes, keyboard and " +
                        "velocity scaling, pitch envelope and LFO with the DX7's own parameter values; loads " +
                        "DX7 banks (.syx). Beyond the DX7: four operator waveforms, unison, glide, a tone " +
                        "control and a clean 2× oversampled engine (or Raw, with the original's aliasing).\n\n" +
                        "Part of neo-buzz (docs/specs/machines.md).", "Neo FM6"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoFm6Editor();
    }
#endif
}
