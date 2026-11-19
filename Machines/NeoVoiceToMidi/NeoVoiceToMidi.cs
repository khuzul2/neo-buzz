#nullable disable
// Neo Voice to MIDI (docs/specs/voice-to-midi.md): a voice in, MIDI out, the voice passing through at the Monitor level. DSP in Engine/,
// parameters generated from Params.def, editor in NeoVoiceToMidiEditor.cs. The MIDI goes to a target machine (SendMIDINote on it, so the
// notes land in its next block), an external MIDI device, and the host's machine MIDI bus (the MIDI From source "Machine: <name>" of
// armed tracks). The target, the device, the sounds' names and the training examples are saved with the song (MachineState) and are in presets.

using System;
using System.Collections.Generic;
using System.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.VoiceToMidi
{
    /// <summary>Saved with the song and in presets: where the MIDI goes, the sounds' names and the training examples (the profile).</summary>
    public class NeoVoiceToMidiState
    {
        /// <summary>The machine the notes are sent to ("" none).</summary>
        public string Target { get; set; } = "";
        /// <summary>The external MIDI output device ("" none).</summary>
        public string Device { get; set; } = "";
        /// <summary>The eight sounds' names, separated by "|" ("" for the defaults).</summary>
        public string Names { get; set; } = "";
        /// <summary>The trained examples as text (VoiceClassifier.Serialize); empty when nothing is trained.</summary>
        public string Training { get; set; } = "";
    }

    [MachineDecl(Name = "Neo Voice to MIDI", ShortName = "Voice MIDI", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoVoiceToMidiMachine : NeoMachineBase, IBuzzMachine, IMachineMidiSource
    {
        public static readonly string[] DefaultNames = { "Kick", "Snare", "Closed hat", "Open hat", "Clap", "Rim", "Crash", "Ride" };

        readonly float[] mono = new float[VoiceEngine.MaxBlock];
        int rate;
        int quietBlocks;
        NeoVoiceToMidiState state = new NeoVoiceToMidiState();
        volatile IMachine target;
        volatile string targetName = "";
        volatile int deviceIndex = -1;
        volatile string deviceName = "";
        long lastResolve;
        IBuzz buzzCache;
        Timer pump;
        readonly object pumpLock = new object();
        readonly float[] drain = new float[VoiceFeatures.Dimension];
        public event Action StateChanged;

        public NeoVoiceToMidiMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new VoiceEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal VoiceEngine Engine { get; }

        // ---------------------------------------------------------------------------------------------------------- state

        public NeoVoiceToMidiState MachineState
        {
            get
            {
                state.Target = targetName;
                state.Device = deviceName;
                state.Training = Engine.Classifier.Count > 0 ? Engine.Classifier.Serialize() : "";
                return state;
            }
            set
            {
                state = value ?? new NeoVoiceToMidiState();
                // A preset carries the sounds and their training, not where the MIDI goes: an empty Target or Device leaves the routing as it is.
                if (!string.IsNullOrEmpty(state.Target)) SetTarget(state.Target);
                if (!string.IsNullOrEmpty(state.Device)) SetDevice(state.Device);
                if (string.IsNullOrEmpty(state.Training)) Engine.Classifier.Clear();
                else if (!Engine.Classifier.Deserialize(state.Training)) Engine.Classifier.Clear();
                StateChanged?.Invoke();
            }
        }

        /// <summary>The name of sound <paramref name="i"/> (0 to 7) as the user gave it, or its default.</summary>
        public string SoundName(int i)
        {
            var parts = (state.Names ?? "").Split('|');
            return i < parts.Length && parts[i].Length > 0 ? parts[i] : DefaultNames[i];
        }

        public void SetSoundName(int i, string name)
        {
            var parts = new string[8];
            for (int k = 0; k < 8; k++) parts[k] = k == i ? (name ?? "").Replace("|", " ").Trim() : SoundName(k);
            for (int k = 0; k < 8; k++) if (parts[k] == DefaultNames[k]) parts[k] = "";
            state.Names = string.Join("|", parts);
            StateChanged?.Invoke();
        }

        public string TargetName => targetName;
        public string DeviceName => deviceName;
        public bool TargetFound => target != null;

        public void SetTarget(string name)
        {
            targetName = name ?? "";
            target = null;
            Engine.Flush();
            ResolveTarget(true);
            StateChanged?.Invoke();
        }

        public void SetDevice(string name)
        {
            deviceName = name ?? "";
            deviceIndex = -1;
            ResolveDevice();
            StateChanged?.Invoke();
        }

        IBuzz Buzz
        {
            get
            {
                if (buzzCache != null) return buzzCache;
                try { buzzCache = host?.Machine?.Graph?.Buzz; } catch (Exception) { buzzCache = null; }
                return buzzCache;
            }
        }

        void ResolveTarget(bool force)
        {
            string name = targetName;
            if (string.IsNullOrEmpty(name)) { target = null; return; }
            try
            {
                var t = target;
                if (t != null && t.Graph != null && t.Name == name && !force) return;
                var machines = host.Machine.Graph.Machines;
                for (int i = 0; i < machines.Count; i++)
                {
                    var m = machines[i];
                    if (m.Name == name && !ReferenceEquals(m, host.Machine)) { target = m; return; }
                }
                target = null;
            }
            catch (Exception) { target = null; }
        }

        void ResolveDevice()
        {
            string name = deviceName;
            if (string.IsNullOrEmpty(name)) { deviceIndex = -1; return; }
            try
            {
                var outs = Buzz?.GetMidiOuts();
                if (outs == null) return;
                foreach (var o in outs)
                    if (string.Equals(o.Item2, name, StringComparison.OrdinalIgnoreCase)) { deviceIndex = o.Item1; return; }
                deviceIndex = -1;
            }
            catch (Exception) { deviceIndex = -1; }
        }

        /// <summary>The machines the notes can go to (every machine but this one) and the external MIDI outputs, for the pickers.</summary>
        public IEnumerable<string> TargetNames()
        {
            var list = new List<string>();
            try
            {
                foreach (var m in host.Machine.Graph.Machines)
                    if (!ReferenceEquals(m, host.Machine)) list.Add(m.Name);
            }
            catch (Exception) { }
            return list;
        }

        public IEnumerable<string> DeviceNames()
        {
            var list = new List<string>();
            try { foreach (var o in Buzz?.GetMidiOuts() ?? Array.Empty<Tuple<int, string>>()) list.Add(o.Item2); }
            catch (Exception) { }
            return list;
        }

        // ---------------------------------------------------------------------------------------------------------- training

        /// <summary>Starts a pass for sound <paramref name="sound"/> (0 to 7): its examples are replaced; say the sound 10 to 20 times; it stops by itself at 20.</summary>
        public void StartTraining(int sound)
        {
            if (sound < 0 || sound >= VoiceEngine.Sounds) return;
            Engine.Classifier.Clear(sound);
            Engine.StartLearning(sound);
            lock (pumpLock) pump ??= new Timer(_ => Pump(), null, 200, 200);
        }

        /// <summary>Ends the pass and trains.</summary>
        public void StopTraining()
        {
            Engine.StopLearning();
            Pump();
        }

        public void ClearSound(int sound)
        {
            Engine.Classifier.Clear(sound);
            Engine.Classifier.Train();
            StateChanged?.Invoke();
        }

        public void ClearTraining()
        {
            Engine.Classifier.Clear();
            StateChanged?.Invoke();
        }

        /// <summary>Files what the audio thread learned as examples and, when the pass is over, trains (the editor and the timer call it).</summary>
        public void Pump()
        {
            bool any = false;
            lock (pumpLock)
            {
                while (Engine.TakeLearned(out int cls, drain)) { Engine.Classifier.Add(cls, drain); any = true; }
                bool learning = Engine.Learning >= 0;
                if (any || !learning) Engine.Classifier.Train();
                if (!learning && pump != null) { pump.Dispose(); pump = null; }
            }
            if (any) StateChanged?.Invoke();
        }

        // ---------------------------------------------------------------------------------------------------------- audio

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            if (n > VoiceEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
            float monitor = (float)Values.Value(P.Monitor);
            if (hasInput)
            {
                quietBlocks = 0;
                const float s = 0.5f / DspMath.BuzzScale;
                for (int i = 0; i < n; i++) mono[i] = (input[i].L + input[i].R) * s;
            }
            else
            {
                // The voice has stopped: run on with silence until the note, the chord and the trigger notes have ended (at most two seconds).
                if (!Engine.Busy || quietBlocks > 2 * rate / Math.Max(1, n))
                {
                    if (quietBlocks != int.MaxValue)
                    {
                        quietBlocks = int.MaxValue;
                        int c = Engine.Flush();
                        Send(c);
                    }
                    return false;
                }
                quietBlocks++;
                Array.Clear(mono, 0, n);
            }
            ResolveWhenDue();
            int count = Engine.Process(mono, n);
            Send(count);
            if (!hasInput) return false;
            if (monitor <= 0f) return false;
            for (int i = 0; i < n; i++)
            {
                output[i].L = input[i].L * monitor;
                output[i].R = input[i].R * monitor;
            }
            return true;
        }

        void ResolveWhenDue()
        {
            long now = Environment.TickCount64;
            if (now - lastResolve < 1000) return;
            lastResolve = now;
            try
            {
                // The song's key: the "Key" entry of the song's associations ("D minor", "F# major"); Lock = Song key follows it.
                if (Buzz?.Song?.Associations != null && Buzz.Song.Associations.TryGetValue("Key", out var k)) Engine.SetSongKey(k as string);
            }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(targetName) && target == null || target != null && target.Graph == null) ResolveTarget(target != null);
            if (!string.IsNullOrEmpty(deviceName) && deviceIndex < 0) ResolveDevice();
        }

        void Send(int count)
        {
            if (count <= 0) return;
            var events = Engine.Events;
            var t = target;
            int dev = deviceIndex;
            var bus = Buzz as IMachineMidiBus;
            var self = host?.Machine;
            for (int i = 0; i < count; i++)
            {
                ref var e = ref events[i];
                int status, d1, d2;
                switch (e.Kind)
                {
                    case MidiKind.NoteOn: status = 0x90 | e.Channel; d1 = e.Data1; d2 = e.Data2; break;
                    case MidiKind.NoteOff: status = 0x80 | e.Channel; d1 = e.Data1; d2 = 0; break;
                    case MidiKind.Controller: status = 0xB0 | e.Channel; d1 = e.Data1; d2 = e.Data2; break;
                    default: status = 0xE0 | e.Channel; d1 = e.Data2 & 127; d2 = e.Data2 >> 7; break;
                }
                int data = status | (d1 << 8) | (d2 << 16);
                try
                {
                    if (t != null)
                    {
                        switch (e.Kind)
                        {
                            case MidiKind.NoteOn: t.SendMIDINote(e.Channel, e.Data1, Math.Max(1, e.Data2)); break;
                            case MidiKind.NoteOff: t.SendMIDINote(e.Channel, e.Data1, 0); break;
                            case MidiKind.Controller: t.SendMIDIControlChange(e.Data1, e.Channel, e.Data2); break;
                            default: t.SendMIDIControlChange(0xE0, e.Channel, e.Data2); break;
                        }
                    }
                    if (dev >= 0) Buzz.SendMIDIOutput(dev, data);
                    bus?.SendMachineMIDI(self, data);
                }
                catch (Exception) { }
            }
        }

        /// <summary>For agents (machine_readings).</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        // ---------------------------------------------------------------------------------------------------------- menu

        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                var items = new List<IMenuItem>();
                var targets = new List<IMenuItem>
                {
                    new MenuItemVM { Text = "(none)", IsCheckable = true, IsChecked = targetName.Length == 0, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => SetTarget("") } },
                };
                foreach (var name in TargetNames())
                {
                    string tn = name;
                    targets.Add(new MenuItemVM { Text = tn, IsCheckable = true, IsChecked = targetName == tn, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => SetTarget(tn) } });
                }
                items.Add(new MenuItemVM { Text = "Send MIDI to machine", Children = targets });
                var devices = new List<IMenuItem>
                {
                    new MenuItemVM { Text = "(none)", IsCheckable = true, IsChecked = deviceName.Length == 0, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => SetDevice("") } },
                };
                foreach (var name in DeviceNames())
                {
                    string dn = name;
                    devices.Add(new MenuItemVM { Text = dn, IsCheckable = true, IsChecked = deviceName == dn, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => SetDevice(dn) } });
                }
                items.Add(new MenuItemVM { Text = "Send MIDI to device", Children = devices });
                items.Add(new MenuItemVM
                {
                    Text = "Forget all trained sounds",
                    Command = new SimpleCommand { CanExecuteDelegate = _ => Engine.Classifier.Count > 0, ExecuteDelegate = _ => ClearTraining() },
                });
                items.Add(new MenuItemVM
                {
                    Text = "About Neo Voice to MIDI...",
                    Command = new SimpleCommand
                    {
                        CanExecuteDelegate = _ => true,
                        ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                            "Neo Voice to MIDI — turns a voice into MIDI and lets it pass through (Monitor). Pitch: the sung note, locked to a key or scale, with stickiness, " +
                            "octave shift, pitch bend (free or anchored), dynamic velocity, minimum note length and legato. Triggers: up to eight sounds you train in one pass " +
                            "play notes. Chords follow the sung root. The voice level and the vowels aa, oo and ee are controllers. The MIDI goes to a target machine, an external " +
                            "device, and is a MIDI From source (Machine: this machine's name) for armed tracks.\n\nPart of neo-buzz (docs/specs/voice-to-midi.md).", "Neo Voice to MIDI"),
                    },
                });
                return items;
            }
        }
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoVoiceToMidiEditor();
    }
#endif
}
