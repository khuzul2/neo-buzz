using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Midi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Track inputs, layout side (docs/specs/neo-audio.md "Inputs and arm"): what a machine's tracks listen to (saved with the song, one undo step
    /// per change), the lists to choose from, the input meter and the arm of a machine, one state for every view.
    /// </summary>
    internal sealed partial class SessionLayout
    {
        /// <summary>The Neo Audio parameter that says when its input is heard: 0 auto (while armed), 1 in, 2 off.</summary>
        const string MonitorParameter = "Monitor";

        /// <summary>Raised when a machine's input or arm changed (views refresh).</summary>
        public event Action InputsChanged;

        /// <summary>The input device the engine has open.</summary>
        EngineInput EngineIn => buzz.EngineInput;

        // ------------------------------------------------------------------ state

        /// <summary>What the machine's tracks listen to (a copy; the defaults when nothing is set: wired audio, MIDI from all inputs).</summary>
        public TrackInput TrackInputOf(IMachine machine) => machine == null ? new TrackInput() : Document?.GetInput(machine.Name) ?? new TrackInput();

        /// <summary>Whether a machine can be armed: an audio track, or a generator (it takes notes). Not the master or an effect.</summary>
        public static bool CanArm(IMachine machine)
        {
            if (machine?.DLL?.Info == null) return false;
            return IsClipPlayer(machine) || machine.DLL.Info.Type == MachineType.Generator;
        }

        /// <summary>Whether the machine's audio can be chosen (Neo Audio); other machines only take MIDI.</summary>
        public static bool HasAudioInput(IMachine machine) => IsClipPlayer(machine);

        /// <summary>Whether any track of the machine is armed.</summary>
        public bool IsMachineArmed(IMachine machine)
        {
            Sync();
            for (int t = 0; t < TrackCount; t++)
                if (SequenceOf(t)?.Machine == machine && IsArmed(t)) return true;
            return false;
        }

        /// <summary>
        /// Arms or disarms every track of a machine (not undoable, not saved); a machine without a track gets one first, as the agent does.
        /// False when the machine can't be armed.
        /// </summary>
        public bool SetMachineArmed(IMachine machine, bool on)
        {
            if (!CanArm(machine)) return false;
            Ensure();
            int first = FirstTrackOf(machine);
            if (first < 0)
            {
                if (!on) return true;
                AddTrackFor(machine);
                first = FirstTrackOf(machine);
                if (first < 0) return false;
            }
            for (int t = 0; t < TrackCount; t++)
                if (SequenceOf(t)?.Machine == machine && IsArmed(t) != on)
                    SetArmed(t, on);
            InputsChanged?.Invoke();
            return true;
        }

        // ------------------------------------------------------------------ edits

        /// <summary>Sets what a machine listens to (one undo step); false when nothing changed.</summary>
        public bool SetInput(IMachine machine, TrackInput input)
        {
            if (machine == null || input == null || !CanArm(machine)) return false;
            input = input.Clone();
            if (!HasAudioInput(machine)) input.Audio = AudioFrom.Wired;
            if (input.Channel < 0 || input.Channel > 16) input.Channel = 0;
            if (input.Midi != MidiFromKind.Device) input.Device = "";
            bool changed = Edit(doc => doc.SetInput(machine.Name, input));
            if (changed) InputsChanged?.Invoke();
            return changed;
        }

        public bool SetAudioFrom(IMachine machine, AudioFrom from)
        {
            if (!HasAudioInput(machine)) return false;
            var input = TrackInputOf(machine);
            input.Audio = from;
            return SetInput(machine, input);
        }

        public bool SetMidiFrom(IMachine machine, MidiFromKind kind, string device = "")
        {
            var input = TrackInputOf(machine);
            input.Midi = kind;
            input.Device = kind == MidiFromKind.Device ? device ?? "" : "";
            return SetInput(machine, input);
        }

        /// <summary>The MIDI channel an armed track listens to: 0 all, 1 to 16.</summary>
        public bool SetMidiChannel(IMachine machine, int channel)
        {
            var input = TrackInputOf(machine);
            input.Channel = Math.Clamp(channel, 0, 16);
            return SetInput(machine, input);
        }

        /// <summary>The Monitor of an audio track: 0 auto, 1 in, 2 off.</summary>
        public int MonitorOf(IMachine machine) =>
            HasAudioInput(machine) ? ParameterOf(machine, MonitorParameter)?.GetValue(0) ?? 0 : 0;

        /// <summary>Sets the Monitor (one undo step).</summary>
        public bool SetMonitor(IMachine machine, int mode)
        {
            var p = HasAudioInput(machine) ? ParameterOf(machine, MonitorParameter) : null;
            if (p == null) return false;
            mode = Math.Clamp(mode, 0, 2);
            int before = p.GetValue(0);
            if (before == mode) return false;
            p.SetValue(-1, mode);
            UndoStack.Do(new ParameterEditAction(p, before, mode));
            InputsChanged?.Invoke();
            return true;
        }

        static IParameter ParameterOf(IMachine machine, string name) =>
            machine?.ParameterGroups.FirstOrDefault(g => g.Type == ParameterGroupType.Global)?.Parameters.FirstOrDefault(p => p.Name == name);

        /// <summary>A parameter change the song can undo (the engine's parameters have no history of their own).</summary>
        sealed class ParameterEditAction : IAction, IHistoryAction
        {
            readonly IParameter parameter;
            readonly int before, after;
            bool skipDo = true;

            public ParameterEditAction(IParameter parameter, int before, int after)
            {
                this.parameter = parameter;
                this.before = before;
                this.after = after;
            }

            string IHistoryAction.HistoryName => "Set monitor";
            long IHistoryAction.HistoryBytes => 64;
            IEnumerable<KeyValuePair<object, long>> IHistoryAction.HistoryBlobs => null;

            public void Do()
            {
                if (skipDo) { skipDo = false; return; }
                parameter.SetValue(-1, after);
            }

            public void Undo() => parameter.SetValue(-1, before);
        }

        /// <summary>The input a new audio track starts with: In 1/2 when a device with two inputs is open, In 1 for one, else none.</summary>
        AudioFrom DefaultAudioFrom() => EngineIn.Channels >= 2 ? AudioFrom.Stereo(0) : EngineIn.Channels == 1 ? AudioFrom.Mono(0) : AudioFrom.None;

        // ------------------------------------------------------------------ choices

        /// <summary>The names of the input device's channels ("In 1"... when the driver gives none).</summary>
        public IReadOnlyList<string> AudioChannelNames()
        {
            var names = EngineIn.ChannelNames;
            int n = EngineIn.Channels;
            var list = new List<string>(n);
            for (int i = 0; i < n; i++)
                list.Add(names != null && i < names.Length && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : $"In {i + 1}");
            return list;
        }

        /// <summary>What Audio From can be: None, the device's inputs (mono, then stereo pairs), the other tracks, Wired inputs.</summary>
        public IReadOnlyList<AudioFrom> AudioChoices(IMachine machine)
        {
            var list = new List<AudioFrom> { AudioFrom.None };
            int n = EngineIn.Channels;
            for (int i = 0; i < n; i++) list.Add(AudioFrom.Mono(i));
            for (int i = 0; i + 1 < n; i += 2) list.Add(AudioFrom.Stereo(i));
            foreach (var other in TrackSources(machine))
                list.Add(AudioFrom.Track(other.Name));
            list.Add(AudioFrom.Wired);
            return list;
        }

        /// <summary>The machines that can be a track source: the other audio tracks and generators of the session.</summary>
        public IReadOnlyList<IMachine> TrackSources(IMachine machine)
        {
            var doc = Document;
            if (doc == null) return Array.Empty<IMachine>();
            var seen = new HashSet<IMachine>();
            var list = new List<IMachine>();
            for (int t = 0; t < TrackCount; t++)
            {
                var m = SequenceOf(t)?.Machine;
                if (m != null && m != machine && seen.Add(m)) list.Add(m);
            }
            return list;
        }

        /// <summary>The label of an audio source: "In 1", "In 1/2", the track, "Wired inputs", "None".</summary>
        public string AudioLabel(AudioFrom from) => from.Label(AudioChannelNames());

        /// <summary>The MIDI inputs to choose from: those that are open, and the one the machine is set to when it isn't connected.</summary>
        public IReadOnlyList<(string Name, bool Connected)> MidiDevices(IMachine machine = null)
        {
            var open = buzz.MidiInOutEngine?.OpenInputNames() ?? Array.Empty<string>();
            var list = open.Select(n => (n, true)).ToList();
            var set = machine == null ? null : TrackInputOf(machine);
            if (set != null && set.Midi == MidiFromKind.Device && !string.IsNullOrEmpty(set.Device) && !open.Any(n => string.Equals(set.Device, n, StringComparison.OrdinalIgnoreCase)))
                list.Add((set.Device, false));
            return list;
        }

        /// <summary>The label of the machine's MIDI source: "All inputs", a device (or "name (not connected)"), "Computer keyboard".</summary>
        public string MidiLabel(IMachine machine)
        {
            var input = TrackInputOf(machine);
            if (input.Midi != MidiFromKind.Device) return input.MidiLabel;
            bool connected = (buzz.MidiInOutEngine?.OpenInputNames() ?? Array.Empty<string>()).Any(n => string.Equals(input.Device, n, StringComparison.OrdinalIgnoreCase));
            return connected ? input.Device : input.Device + " (not connected)";
        }

        // ------------------------------------------------------------------ meter

        readonly Dictionary<IMachine, (long Time, InputMeterModel Model, float Peak)> meters = new Dictionary<IMachine, (long, InputMeterModel, float)>();

        /// <summary>
        /// The machine's input meter (UI thread): the peak since the last look (read at most every 15 ms), with hold and clip.
        /// Null for a machine that has no audio input. The level is 0 while the input is neither armed nor monitored.
        /// </summary>
        public InputMeterModel InputMeterOf(IMachine machine)
        {
            if (!HasAudioInput(machine)) return null;
            long now = Environment.TickCount64;
            if (!meters.TryGetValue(machine, out var entry))
                entry = (now - 1000, new InputMeterModel(), 0);
            if (now - entry.Time >= 15)
            {
                var player = SessionClipEngine.PlayerOf(machine);
                var (l, r) = player?.TakePeak() ?? (0f, 0f);
                bool live = IsMachineArmed(machine) || MonitorOf(machine) == 1;
                float peak = live ? Math.Max(l, r) : 0;
                entry.Model.Update(peak, Math.Min(1.0, (now - entry.Time) / 1000.0), InputMeterScale);
                entry = (now, entry.Model, peak);
                if (meters.Count > 256) meters.Clear();
                meters[machine] = entry;
            }
            return entry.Model;
        }

        /// <summary>Sample units (32768 full scale) to the 0 to 1 of the meter, on a dB scale from -60.</summary>
        static double InputMeterScale(float peak)
        {
            if (peak <= 0) return 0;
            double db = 20 * Math.Log10(peak / 32768.0);
            return Math.Clamp((db + 60) / 60, 0, 1);
        }

        /// <summary>Frames the input ring dropped or ran dry for (the dropout stats).</summary>
        public (long Underruns, long Overruns) InputDropouts() => (EngineIn.UnderrunFrames, EngineIn.OverrunFrames);

        // ------------------------------------------------------------------ publishing

        /// <summary>Gives the engine the inputs of every machine that has them (every change of the layout, undo and a loaded song included).</summary>
        void PublishInputs(SessionCore core, SessionDocument doc)
        {
            var map = new Dictionary<MachineCore, TrackInput>();
            HashSet<string> aux = new HashSet<string>(StringComparer.Ordinal);
            if (doc != null)
            {
                foreach (var n in doc.Returns) aux.Add(n);
                if (!string.IsNullOrEmpty(doc.CueMachine)) aux.Add(doc.CueMachine);
                foreach (var (name, input) in doc.Inputs)
                    if (MachineNamed(name) is MachineCore m) map[m] = input;
            }
            core.UseInputs(map, n => MachineNamed(n) as MachineCore, aux);
        }
    }
}
