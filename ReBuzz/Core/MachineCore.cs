using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using ReBuzz.Common;
using ReBuzz.Common.Interfaces;
using ReBuzz.MachineManagement;
using ReBuzz.ManagedMachine;
using ReBuzz.NativeMachine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ReBuzz.Core
{
    public class MachineCore : IMachine, IMachineExtension, UiEventQueue.ITarget
    {
        public static long machineHostId = 1;
        public readonly Lock workLock = new();

        IMachineGraph graph;
        public IMachineGraph Graph { get => graph; set => graph = value; }

        MachineDLL machineDLL;
        public IMachineDLL DLL { get => machineDLL; }

        // Fork (docs/specs/machines.md 3.1): by parameter and track. By parameter alone, a
        // parameter changed on several tracks in one tick (a chord) reached managed machines and
        // value listeners for the last track only. Fork (engine hardening): preallocated bits, no
        // longer a ConcurrentDictionary that allocated on the audio thread for every change.
        internal ParameterChanges ParameterChanges { get; } = new ParameterChanges();
        internal ConcurrentBag<int> wavesEventsPending = new ConcurrentBag<int>();

        internal MachineDLL MachineDLL { get => machineDLL; set => machineDLL = value; }

        readonly List<IMachineConnection> inputs = new List<IMachineConnection>();
        public ReadOnlyCollection<IMachineConnection> Inputs
        {
            get
            {
                // Return only input connections that are coming from a) visible machines and b) non control machines
                List<IMachineConnection> connections = new List<IMachineConnection>();

                foreach (var input in inputs)
                {
                    if (input.Source.OutputChannelCount > 0 && !(input.Source as MachineCore).Hidden)
                            connections.Add(input);
                }
                return connections.AsReadOnly();
            }
        }

        public List<IMachineConnection> AllInputs { get => inputs; }

        readonly List<IMachineConnection> outputs = new List<IMachineConnection>();
        public ReadOnlyCollection<IMachineConnection> Outputs
        {
            get
            {
                List<IMachineConnection> connections = new List<IMachineConnection>();
                foreach (var output in outputs)
                {
                    if (output.Destination.InputChannelCount > 0 && !(output.Destination as MachineCore).Hidden)
                        connections.Add(output);
                }
                return connections.AsReadOnly();
            }
        }

        public List<IMachineConnection> AllOutputs { get => outputs; }

        // Visibility filters matching the public Inputs/Outputs facades exactly
        // (see the property bodies above). The facades build a fresh List +
        // ReadOnlyCollection on every get, which is fine for the GUI but not for
        // the per-chunk engine loops below; hot engine code iterates the raw
        // lists through these predicates instead. Same elements, same order,
        // same body - bit-exact with the facade-based loops they replace.
        internal static bool IsVisibleInput(IMachineConnection input)
            => input.Source.OutputChannelCount > 0 && !(input.Source as MachineCore).Hidden;

        internal static bool IsVisibleOutput(IMachineConnection output)
            => output.Destination.InputChannelCount > 0 && !(output.Destination as MachineCore).Hidden;

        /// <summary>Fork (engine hardening): Inputs[index] without building Inputs; null past its end.</summary>
        internal IMachineConnection VisibleInputAt(int index)
        {
            foreach (var input in inputs)
                if (IsVisibleInput(input) && index-- == 0)
                    return input;
            return null;
        }

        /// <summary>Fork (engine hardening): Inputs.IndexOf(connection) without building Inputs.</summary>
        internal int VisibleInputIndexOf(IMachineConnection connection)
        {
            int index = 0;
            foreach (var input in inputs)
            {
                if (!IsVisibleInput(input)) continue;
                if (Equals(input, connection)) return index;
                index++;
            }
            return -1;
        }

        readonly Dictionary<int, string> inputChannelNames = new Dictionary<int, string>();
        readonly Dictionary<int, string> outputChannelNames = new Dictionary<int, string>();

        int inputChannelCount = 0;
        public int InputChannelCount
        {
            get => inputChannelCount;
            set
            {
                inputChannelNames.Clear();
                if (graph != null)
                {
                    var bc = graph.Buzz as ReBuzzCore;

                    inputChannelCount = value;

                    for (int i = 0; i < inputChannelCount; i++)
                    {
                        // Get channel name
                        string name = bc.MachineManager.GetChannelName(this, true, i);
                        inputChannelNames[i] = name;
                    }
                }
                else
                {
                    inputChannelCount = value;
                }

                // Update inputs
                foreach (var input in inputs)
                {
                    if (input.DestinationChannel >= inputChannelCount)
                        (input as MachineConnectionCore).DestinationChannel = 0;
                }
                PropertyChanged.Raise(this, "InputChannelCount");
            }
        }

        int outputChannelCount = 0;
        public int OutputChannelCount
        {
            get => outputChannelCount;
            set
            {
                //if (outputChannelCount != value)
                //lock (ReBuzzCore.AudioLock)
                {
                    outputChannelNames.Clear();
                    if (graph != null)
                    {
                        // Needs a lock since audio might be running?
                        var bc = graph.Buzz as ReBuzzCore;

                        outputChannelCount = value;
                        for (int i = 0; i < outputChannelCount; i++)
                        {
                            // Get channel name

                            string name = bc.MachineManager.GetChannelName(this, false, i);
                            outputChannelNames[i] = name;
                        }
                    }
                    else
                    {
                        outputChannelCount = value;
                    }

                    // Update inputs
                    foreach (var output in outputs)
                    {
                        if (output.SourceChannel >= outputChannelCount)
                            (output as MachineConnectionCore).SourceChannel = 0;
                    }

                    PropertyChanged.Raise(this, "OutputChannelCount");
                }
            }
        }


        string name;
        public string Name
        {
            get => name;
            set
            {
                string oldName = name;
                name = value;
                PropertyChanged.Raise(this, "Name");
                var bc = graph.Buzz as ReBuzzCore;
                foreach (var o in outputs)
                {
                    bc.MachineManager.RenameInput(o.Destination as MachineCore, oldName, name);
                }
            }
        }

        Tuple<float, float> position = new Tuple<float, float>(0, 0);
        public Tuple<float, float> Position { get => position; set { position = value; PropertyChanged.Raise(this, "Position"); graph.Buzz.SetModifiedFlag(); } }

        int overSampleFactor = 1;
        public int OversampleFactor { get => overSampleFactor; set { overSampleFactor = Math.Max(1, Math.Min(2, value)); PropertyChanged.Raise(this, "OversampleFactor"); graph.Buzz.SetModifiedFlag(); } }

        int midiInputChannel = -1;
        public int MIDIInputChannel { get => midiInputChannel; set { midiInputChannel = value; PropertyChanged.Raise(this, "MIDIInputChannel"); graph.Buzz.SetModifiedFlag(); } }

        List<ParameterGroup> parameterGroups;
        // Fork (engine hardening): the copy is kept while the list holds the same groups (it was
        // made on every call). Pattern editors set values through it, on the audio thread.
        ReadOnlyCollection<IParameterGroup> parameterGroupsView;
        public ReadOnlyCollection<IParameterGroup> ParameterGroups { get => ParameterGroup.ViewOf(ref parameterGroupsView, parameterGroups); }
        internal List<ParameterGroup> ParameterGroupsList { get => parameterGroups; set => parameterGroups = value; }

        List<AttributeCore> attributes = new List<AttributeCore>();
        public ReadOnlyCollection<IAttribute> Attributes { get => attributes.Cast<IAttribute>().ToReadOnlyCollection(); }

        List<IMenuItem> commands = new List<IMenuItem>();
        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                SetCommands(); return commands;
            }
        }

        internal Dictionary<string, Envelope> envelopes = new Dictionary<string, Envelope>();

        public ReadOnlyCollection<string> EnvelopeNames { get => envelopes.Keys.ToReadOnlyCollection(); }

        List<PatternCore> patterns = new List<PatternCore>();
        public ReadOnlyCollection<IPattern> Patterns
        {
            get
            {
                return patterns.Cast<IPattern>().ToReadOnlyCollection();
            }
        }

        internal List<PatternCore> PatternsList { get => patterns; set => patterns = value; }

        public bool IsControlMachine { get; set; }

        bool isActive;
        public bool IsActive
        {
            get => isActive;
            set
            {
                if (isActive != value)
                {
                    isActive = value;
                    // Fork (audio hygiene): through the engine's UI event queue, once until delivered
                    // (it was a closure posted on every change, on the audio thread: with the
                    // recursive work algorithm, twice a buffer for a machine whose two activity
                    // checks disagree). The listeners read the value as it is then.
                    if (uiEvents != null && Interlocked.Exchange(ref isActiveEventPending, 1) == 0)
                        uiEvents.Enqueue(this, 0, 0);
                    else if (uiEvents == null)
                        dispatcher?.BeginInvoke(() => PropertyChanged.Raise(this, "IsActive"));
                }
            }
        }

        bool muted;
        // Fork (machine-safety.md §3.2): a diverged machine reads as muted until it is cleared, and
        // unmuting it is Reset and unmute (the guard trips again if it is still broken).
        public bool IsMuted
        {
            get => muted || diverged;
            set
            {
                bool reset = !value && diverged;
                muted = value;
                if (reset) ClearDivergence();
                PropertyChanged.Raise(this, "IsMuted");
                if (reset) PropertyChanged.Raise(this, "IsDiverged");
                graph.Buzz.SetModifiedFlag();
            }
        }

        // Fork (machine-safety.md §3.2): the guard's state. The audio thread writes the details,
        // then the flag (a volatile write publishes them); the UI thread clears it.
        volatile bool diverged;
        global::ReBuzz.Audio.DivergeKind divergedKind;
        float divergedPeak;
        long divergedAtTicks;
        int divergedPosition;
        Action onDiverged;

        /// <summary>The guard tripped on this machine: it is silent, its Work isn't called, until it is reset.</summary>
        internal bool IsDiverged => diverged;

        /// <summary>Why: "NaN", "infinity" or "+87.3 dBFS" (empty when not diverged).</summary>
        internal string DivergedReason => diverged ? global::ReBuzz.Audio.MachineGuard.Reason(divergedKind, divergedPeak) : "";

        /// <summary>When the guard tripped (UTC).</summary>
        internal DateTime DivergedAt => new DateTime(divergedAtTicks, DateTimeKind.Utc);

        /// <summary>The song position (ticks) when it tripped.</summary>
        internal int DivergedPosition => divergedPosition;

        /// <summary>
        /// Audio thread (the guard): marks the machine diverged and posts the rest to the UI thread:
        /// Buzz's Stop, which clears most machines' buffers, and the views' notice. No allocation
        /// here but the post.
        /// </summary>
        internal void MarkDiverged(global::ReBuzz.Audio.DivergeKind kind, float peak)
        {
            if (diverged) return;
            divergedKind = kind;
            divergedPeak = peak;
            divergedAtTicks = DateTime.UtcNow.Ticks;
            divergedPosition = (graph?.Buzz as ReBuzzCore)?.SongCore?.PlayPosition ?? 0;
            diverged = true;
            onDiverged ??= OnDiverged;
            dispatcher?.BeginInvoke(onDiverged);
        }

        void OnDiverged()
        {
            if (!diverged) return;
            var buzz = graph?.Buzz as ReBuzzCore;
            buzz?.MachineManager?.Stop(this);
            PropertyChanged.Raise(this, "IsMuted");
            PropertyChanged.Raise(this, "IsDiverged");
            buzz?.RaiseMachineDiverged(this);
        }

        /// <summary>Reset and unmute (UI thread): stops it once more and lets it play again.</summary>
        internal void ResetDiverged()
        {
            if (!diverged) return;
            IsMuted = false;
        }

        void ClearDivergence()
        {
            (graph?.Buzz as ReBuzzCore)?.MachineManager?.Stop(this);
            divergedKind = global::ReBuzz.Audio.DivergeKind.None;
            diverged = false;
        }

        bool soloed;
        public bool IsSoloed
        {
            get => soloed;
            set
            {
                soloed = value;
                var buzz = graph.Buzz as ReBuzzCore;
                buzz.SongCore.UpdateSoloMode();
                PropertyChanged.Raise(this, "IsSoloed");
                buzz.SetModifiedFlag();
            }
        }

        bool bypassed;
        public bool IsBypassed
        {
            get => bypassed;
            set
            {
                bypassed = value; PropertyChanged.Raise(this, "IsBypassed");
                (graph.Buzz as ReBuzzCore).UpdateMachineDelayCompensation();
                graph.Buzz.SetModifiedFlag();
            }
        }
        bool wireless;
        public bool IsWireless
        {
            get => wireless;
            set
            {
                wireless = value; PropertyChanged.Raise(this, "IsWireless");
                graph.Buzz.SetModifiedFlag();
            }
        }

        bool hasStereoInput;
        public bool HasStereoInput { get => hasStereoInput; set { hasStereoInput = value; PropertyChanged.Raise(this, "HasStereoInput"); } }

        bool hasStereoOutput;
        public bool HasStereoOutput { get => hasStereoOutput; set { hasStereoOutput = value; PropertyChanged.Raise(this, "HasStereoOutput"); } }

        int lastEngineThread = 0;
        public int LastEngineThread
        {
            get => lastEngineThread;
            set
            {
                int oldValue = lastEngineThread;
                int newValue = value % 7;
                lastEngineThread = newValue;

                if (MachineViewState.ShowsEngineThreads)   // engine-split.md S7
                {
                    if (newValue != oldValue)
                    PropertyChanged?.Raise(this, "LastEngineThread");
                }
            }
        }

        public int EngineThreadId { get; internal set; }

        internal void UpdateLastEngineThread()
        {
            LastEngineThread = EngineThreadId;
        }

        private int latency = 0;
        public int Latency { get => latency;
            set
            {
                latency = value;
                engineLatency = value;
                if (engineSettings.MachineDelayCompensation)
                {
                    var bc = graph.Buzz as ReBuzzCore;
                    bc.UpdateMachineDelayCompensation();
                    PropertyChanged.Raise(this, " Latency");
                }
            }
        }

        int overrideLatency = -1;
        public int OverrideLatency { get => overrideLatency;
            set
            {
                overrideLatency = Math.Min(value, 10000);
                if (engineSettings.MachineDelayCompensation)
                {
                    var bc = graph.Buzz as ReBuzzCore;
                    bc.UpdateMachineDelayCompensation();
                    PropertyChanged.Raise(this, "OverrideLatency");
                }
            }
        }

        // Fork (docs/specs/mastering-tools.md §4.1): the latency delay compensation uses for this machine, as the
        // audio thread sees it: set with Latency, and by the machine's own report at its tick (a lookahead
        // setting), which reaches the compensation in that same block, before the work; Latency follows on the
        // UI thread through the UI event queue.
        int engineLatency;
        int latencyEventPending;

        /// <summary>The latency the compensation counts: the override if set, else the machine's; a bypass that can't keep it (not a stereo effect) counts 0.</summary>
        internal int LatencyCompensated =>
            IsBypassed && !BypassKeepsLatency ? 0 : overrideLatency >= 0 ? overrideLatency : engineLatency;

        /// <summary>
        /// Fork (docs/specs/machines.md 3.1, mastering-tools.md §4.1): the machine's own latency changed (a lookahead setting),
        /// reported at its tick on the audio thread: the compensation applies it before this block's work (no allocation),
        /// and the UI thread then updates Latency and grows the delays if they need room.
        /// </summary>
        internal void UpdateLatencyFromMachine(int samples)
        {
            if (samples == engineLatency) return;
            engineLatency = samples;
            if (graph.Buzz is ReBuzzCore bc) bc.LatencyDirty = true;
            if (uiEvents != null)
            {
                if (Interlocked.Exchange(ref latencyEventPending, 1) == 0) uiEvents.Enqueue(this, 0, LatencyEvent);
            }
            else
            {
                dispatcher?.BeginInvoke(DeliverLatency);
            }
        }

        const int LatencyEvent = 1;

        void DeliverLatency()
        {
            Volatile.Write(ref latencyEventPending, 0);
            if (latency != engineLatency)
            {
                latency = engineLatency;
                PropertyChanged.Raise(this, "Latency");
            }
            // The delays already moved on the audio thread; this grows any ring that needed more room.
            if (engineSettings.MachineDelayCompensation) (graph.Buzz as ReBuzzCore)?.UpdateMachineDelayCompensation();
        }

        // ── the bypass keeps the latency (mastering-tools.md §4.1) ──
        //
        // A bypassed effect with latency still counts it in the compensation, and passes its dry input delayed by
        // it, so bypassing doesn't move the mix. It keeps working while bypassed (its output unused), so it comes
        // back with its state current, and switching crossfades the wet output and the delayed dry over 5 ms.
        // The dry input goes through BypassRing every block while the machine has latency, so a bypass has its
        // history at once. Without latency, or with compensation off, a bypass is what it was: no work, the input
        // passed on. Multi-I/O machines too.

        /// <summary>The dry input delayed by the machine's latency (null until the machine has latency).</summary>
        internal LatencyRing BypassRing;
        readonly Sample[] dryDelayed = new Sample[256];
        float bypassAmount;           // 0: the machine's output, 1: the delayed dry

        /// <summary>A bypass here can keep the latency: a stereo effect (not multi-I/O).</summary>
        internal bool BypassKeepsLatency =>
            DLL != null && DLL.Info.Type == MachineType.Effect && (DLL.Info.Flags & MachineInfoFlags.MULTI_IO) == 0;

        /// <summary>UI thread, audio lock held: room for the dry input's delay (the compensation's plan calls it).</summary>
        internal void EnsureBypassRing(int samples)
        {
            if (!BypassKeepsLatency) return;
            BypassRing ??= new LatencyRing();
            BypassRing.EnsureCapacity(samples);
        }

        /// <summary>
        /// Audio thread, at the start of the machine's work: this block goes through the latency-keeping bypass (the machine is
        /// worked, its dry input delayed and mixed in by <see cref="BypassMix"/>): compensation on, a stereo effect with
        /// latency or a bypass crossfade under way.
        /// </summary>
        internal bool BypassLatent(bool compensation)
        {
            var ring = BypassRing;
            bool latent = compensation && ring != null && BypassKeepsLatency &&
                          (LatencyCompensated > 0 || ring.Live || (bypassAmount > 0f && bypassAmount < 1f));
            // Otherwise the plain bypass (or none) plays: the crossfade starts from where it stands.
            if (!latent) bypassAmount = IsBypassed ? 1f : 0f;
            return latent;
        }

        /// <summary>Audio thread: the block's dry input (before the machine's work changes it) into the bypass's delay.</summary>
        internal void BypassCapture(Sample[] input, int n)
        {
            var ring = BypassRing;
            ring.Request(LatencyCompensated, Math.Max(64, ReBuzzCore.masterInfo.SamplesPerSec / 200));
            if (ring.Live)
            {
                ring.Write(input, n);
                ring.Read(dryDelayed, n);
            }
            else
            {
                // No delay (the latency went to 0): the dry as it is.
                ring.Passed();
                Array.Copy(input, dryDelayed, n);
            }
        }

        /// <summary>
        /// Audio thread: the machine's output <paramref name="wet"/> (in place) as the bypass has it: itself, the delayed dry once
        /// bypassed, a 5 ms crossfade between them when the bypass switches.
        /// </summary>
        internal Sample[] BypassMix(Sample[] wet, int n)
        {
            float target = IsBypassed ? 1f : 0f;
            float a = bypassAmount;
            if (a == target && a == 0f) return wet;
            if (a == target)
            {
                Array.Copy(dryDelayed, wet, n);
                return wet;
            }
            float step = 1f / Math.Max(64, ReBuzzCore.masterInfo.SamplesPerSec / 200);
            for (int i = 0; i < n; i++)
            {
                a = target > a ? Math.Min(target, a + step) : Math.Max(target, a - step);
                float g = a * a * (3f - 2f * a);
                if (a == 1f) wet[i] = dryDelayed[i];
                else if (a > 0f)
                {
                    wet[i].L += (dryDelayed[i].L - wet[i].L) * g;
                    wet[i].R += (dryDelayed[i].R - wet[i].R) * g;
                }
            }
            bypassAmount = a;
            return wet;
        }

        public IMachineDLL PatternEditorDLL
        {
            get
            {
                if (EditorMachine != null)
                {
                    return EditorMachine.DLL;
                }

                return null;
            }
        }

        /// <summary>
        /// Fork (0.5): volume of the machine's output while a session clip launched with
        /// velocity, or an arrangement event recorded from one, plays: 0..1 (1 otherwise).
        /// Set by SessionCore on each tick (0.5.1: the most recently started of the clips and
        /// events playing on the machine's tracks). Applied by its output connections, ramped
        /// over a block (MachineConnectionCore), so it never shows as a changed amp.
        /// </summary>
        internal volatile float ClipGain = 1f;

        /// <summary>
        /// Fork (0.6): the crossfader's volume for the machine (1 when it isn't assigned to a
        /// side), set by SessionCore.SetCrossfader from any thread; multiplies <see cref="ClipGain"/>.
        /// </summary>
        internal volatile float CrossfadeGain = 1f;

        // Audio thread scratch for SessionCore.ResolveGains (0.5.1).
        internal long GainBestStamp;
        internal float GainBest = 1f;

        int baseOctave = 4;
        public int BaseOctave { get => baseOctave;
            set
            {
                baseOctave = value;
                baseOctave = baseOctave < 0 ? 0 : baseOctave;
                baseOctave = baseOctave > 9 ? 9 : baseOctave;
                dispatcher.BeginInvoke(() => PropertyChanged.Raise(this, "BaseOctave"));   // fork: set from the IPC thread too
            }
        }

        byte[] data = null; 

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public byte[] Data
        {
            get
            {
                if (DLL.IsMissing)
                    return data;
                else
                    return (graph.Buzz as ReBuzzCore).MachineManager.GetMachineData(this);
            }
            set
            {
                if (DLL.IsMissing)
                    data = value;
                else
                    (graph.Buzz as ReBuzzCore).MachineManager.SetMachineData(this, value);
            }
        }

        public byte[] PatternEditorData
        {
            get
            {
                var buzz = graph.Buzz as ReBuzzCore;
                if (EditorMachine != null)
                {
                    return buzz.MachineManager.GetMachineData(EditorMachine);
                }
                else
                {
                    return null;
                }
            }
        }

        IntPtr cMachinePtr;
        public IntPtr CMachinePtr { get => cMachinePtr; set => cMachinePtr = value; }

        // IMachine.ParameterWindow (a WPF Window) is answered by the UI host through the Windows
        // build's default body (engine-split.md S3, WpfMachineApi.ParameterWindow).

        readonly MachinePerformanceData performanceDataCurrent = new MachinePerformanceData();
        public MachinePerformanceData PerformanceDataCurrent { get => performanceDataCurrent; }

        MachinePerformanceData performanceData = new MachinePerformanceData();
        public MachinePerformanceData PerformanceData { get => performanceData; set => performanceData = value; }

        IBuzzMachine managedMachine;
        public IBuzzMachine ManagedMachine { get => managedMachine; set => managedMachine = value; }

        int trackCount = 0;
        public int TrackCount
        {
            get => trackCount;
            set
            {
                if (value < DLL.Info.MinTracks || value > DLL.Info.MaxTracks)
                {
                    return;
                }
                if (trackCount != value)
                {
                    trackCount = value;
                    if (parameterGroups.Count == 3)
                    {
                        parameterGroups[2].TrackCount = value;

                        if (graph != null)
                        {
                            var bc = graph.Buzz as ReBuzzCore;

                            // Notify native machines
                            bc.MachineManager.SetNumTracks(this, trackCount);
                            PropertyChanged?.Raise(this, "TrackCount");
                        }
                    }

                    graph.Buzz.SetModifiedFlag();
                }
            }
        }

        internal void SetTrackCount(int tracks)
        {
            trackCount = tracks;
            var bc = graph.Buzz as ReBuzzCore;

            // Notify native machines
            bc.MachineManager.SetNumTracks(this, trackCount);
            dispatcher.BeginInvoke(() =>
            {
                PropertyChanged?.Raise(this, "TrackCount");
                bc.SetModifiedFlag();
            });
        }

        internal List<AttributeCore> AttributesList
        {
            get => attributes;
            set { attributes = value; PropertyChanged.Raise(this, "Attributes"); }
        }
        public bool Ready { get; internal set; }


        public long CMachineHost { get; internal set; }
        public List<CMachineEvent> CMachineEventType { get; internal set; }

        public event Action<IPattern> PatternAdded;
        public event Action<IPattern> PatternRemoved;
        public event PropertyChangedEventHandler PropertyChanged;

        public ICommand MachineMenuCommand { get; private set; }
        public string InstrumentName { get; internal set; }
        public bool Hidden { get; internal set; }
        public MachineCore EditorMachine { get; internal set; }

        /// <summary>
        /// Fork (mac-engine.md M2): the song's pattern editor for this machine when it isn't
        /// installed and no default editor replaces it (rebuzz-headless off Windows has none). It is
        /// a hidden missing machine that keeps its data; it isn't <see cref="EditorMachine"/>, which
        /// the engine drives, but the song saves back with it as the machine's editor.
        /// </summary>
        internal MachineCore PlaceholderEditor { get; set; }

        UiEventQueue uiEvents;
        int isActiveEventPending;

        void UiEventQueue.ITarget.Deliver(int arg, int kind)
        {
            if (kind == LatencyEvent)
            {
                DeliverLatency();
                return;
            }
            Volatile.Write(ref isActiveEventPending, 0);
            PropertyChanged.Raise(this, "IsActive");
        }

        public MachineCore(SongCore machineGraph, string buzzPath, IUiDispatcher dispatcher, EngineSettings settings, bool is64Bit = false)
        {
            engineSettings = settings;
            this.buzzPath = buzzPath;
            graph = machineGraph;
            if (dispatcher != null)
            {
                uiEvents = UiEventQueue.For(dispatcher);
                uiEvents.Reserve(2);                // its IsActive and Latency events
            }

            baseOctave = Global.GeneralSettings.DefaultMachineBaseOctave;

            parameterGroups = new List<ParameterGroup>();
            parameterGroups.Add(ParameterGroup.CreateInputGroup(this, dispatcher)); // Inputs

            MachineDLL = new MachineDLL();
            MachineDLL.Is64Bit = is64Bit;
            MachineDLL.IsOutOfProcess = (IntPtr.Size == 4 && is64Bit) || (IntPtr.Size == 8 && !is64Bit);

            CMachineEventType = new List<CMachineEvent>();
            CMachineHost = machineHostId;
            machineHostId++;

            MachineMenuCommand = new SimpleCommand
            {
                CanExecuteDelegate = x => true,
                ExecuteDelegate = x =>
                {
                    // Wait a small amount of time to let context menu close. Use a UI timer to ensure we are in UI thread.
                    IUiTimer dt = null;
                    dt = dispatcher.CreateTimer(TimeSpan.FromMilliseconds(200), () =>
                    {
                        dt.Stop();
                        (Graph.Buzz as ReBuzzCore).MachineManager.Command(this, (int)x);

                        // Update machine latency if instrument or something else changed
                        Latency = (graph.Buzz as ReBuzzCore).MachineManager.GetMachineLatency(this);
                    });
                    dt.Start();
                }
            };
            this.dispatcher = dispatcher;
        }

        public void ClonePattern(string name, IPattern p)
        {
            if (p != null)
            {
                PatternCore newp = new PatternCore(this, name, p.Length, dispatcher);
                newp.BuiltInData = (p as PatternCore)?.BuiltInData;    // fork (mac-engine.md M2): kept contents are never changed, so the copy shares them
                lock (ReBuzzCore.AudioLock)
                {
                    this.patterns.Add(newp);
                    var buzz = graph.Buzz as ReBuzzCore;
                    buzz.MachineManager.CreatePatternCopy(EditorMachine, newp, p);
                }
                // Fork (0.4.1): events and UI after releasing AudioLock.
                PatternAdded?.Invoke(newp);
                PropertyChanged.Raise(this, "Patterns");
                graph.Buzz.SetPatternEditorPattern(newp);
                graph.Buzz.SetModifiedFlag();
            }
        }

        public void CopyParameters()
        {
        }

        public void CreatePattern(string name, int length)
        {
            PatternCore pc = new PatternCore(this, name, length, dispatcher);

            // Don't call these from "Work()"
            lock (ReBuzzCore.AudioLock)
            {
                this.patterns.Add(pc);
            }
            PatternAdded?.Invoke(pc);
            PropertyChanged.Raise(this, "Patterns");
            graph.Buzz.SetPatternEditorPattern(pc);
            graph.Buzz.SetModifiedFlag();
        }

        public void DeletePattern(IPattern p)
        {
            var buzz = graph.Buzz as ReBuzzCore;
            var changedSequences = new List<SequenceCore>();
            lock (ReBuzzCore.AudioLock)
            {
                patterns.Remove(p as PatternCore);
                (p as PatternCore).IsDeleted = true;                    // fork (0.6.1): before the session forgets it
                buzz.SessionCore.OnPatternDeleted(p as PatternCore);   // fork: Session View
                // Remove from sequences
                foreach (var seq in buzz.SongCore.SequencesList.Where(s => s.Machine == p.Machine))
                {
                    if (seq.RemoveEventsOf(p))
                        changedSequences.Add(seq);
                }
            }
            // Fork (0.4.1): events after releasing AudioLock (listeners redraw editors).
            PatternRemoved?.Invoke(p);
            PropertyChanged.Raise(this, "Patterns");
            (p as PatternCore).ClearEvents();
            foreach (var seq in changedSequences)
                seq.InvokeEvents();
        }

        public void RenamePattern(IPattern p, string newName)
        {
            if (p != null && !patterns.Any(x => x.Name == newName))
            {
                (p as PatternCore).Name = newName;
                PropertyChanged?.Raise(this, "Patterns");

                foreach (var sequence in Global.Buzz.Song.Sequences)
                {
                    if (sequence.Events.Values.FirstOrDefault(e => e.Pattern == p) != null)
                    {
                        (sequence as SequenceCore).InvokeEvents();
                    }
                }

                (graph.Buzz as ReBuzzCore).SetModifiedFlag();
            }
        }

        public void DoubleClick()
        {
            if (!Ready)
                return;

#if DEBUG
            if ((graph.Buzz as ReBuzzCore)?.Keyboard.Modifiers == KeyModifiers.Control)
            {
                this.MachineDLL.IsCrashed = true;
                return;
            }
#endif

            if (!DLL.IsManaged)
            {
                var me = CMachineEventType.FirstOrDefault(e => e.Type == BEventType.DoubleClickMachine);
                if (me.Event_Handler != IntPtr.Zero)
                {
                    // Let native machine handle double click
                    var buzzCore = (ReBuzzCore)graph.Buzz;

                    bool handled = buzzCore.MachineManager.MachineEvent(this, me, 0);
                    if (!handled)
                    {
                        // Open parameter windows
                        OpenEditorOrParameterWindow();
                    }
                }
                else
                {
                    if (DLL.GUIFactoryDecl != null && DLL.GUIFactoryDecl.PreferWindowedGUI)
                    {
                        OpenWindowedGUI();
                    }
                    else
                    {
                        OpenEditorOrParameterWindow();
                    }
                }
            }
            else
            {
                if (DLL.GUIFactoryDecl != null && DLL.GUIFactoryDecl.PreferWindowedGUI)
                {
                    OpenWindowedGUI();
                }
                else
                {
                    OpenEditorOrParameterWindow();
                }
            }
        }

        /// <summary>
        /// Fork (docs/specs/mcp-server.md §12.11, U4): what a double click opens on a machine without a GUI of its own:
        /// its manifest editor where the per-gear setting says so (on by default for curated manifests), else the
        /// parameter window, as before. The parameter window stays on the machine's menu.
        /// </summary>
        void OpenEditorOrParameterWindow()
        {
            var host = UiHostOf(graph);
            if (host != null && ReBuzz.Agent.ManifestEditorSettings.OpensEditor(DLL) && host.OpenEditor(this)) return;
            OpenParameterWindow();
        }

        // Fork (engine-split.md S3): the windows themselves belong to the UI host (WpfMachineUiHost
        // in the app). Without one the machine only remembers which windows a song wants open, so
        // saving the song keeps them.
        static IMachineUiHost UiHostOf(IMachineGraph graph) => (graph?.Buzz as ReBuzzCore)?.UiHost;
        MachineWindowState windowsWanted;

        internal string BuzzPath => buzzPath;

        internal void OpenParameterWindow(WindowPlacement? at = null)
        {
            var host = UiHostOf(graph);
            if (host != null)
            {
                host.OpenParameterWindow(this, at);
                return;
            }
            windowsWanted = windowsWanted with
            {
                ParameterWindowVisible = true,
                ParameterWindow = at is { Width: not 0 } p ? p : windowsWanted.ParameterWindow,
            };
        }

        internal void OpenWindowedGUI(WindowPlacement? at = null)
        {
            if (!(DLL.GUIFactoryDecl != null && DLL.GUIFactoryDecl.PreferWindowedGUI))
            {
                return;
            }

            var host = UiHostOf(graph);
            if (host != null)
            {
                host.OpenGui(this, at);
                return;
            }
            windowsWanted = windowsWanted with
            {
                GuiWindowVisible = true,
                GuiWindow = at is { Width: not 0 } p ? p : windowsWanted.GuiWindow,
            };
        }

        internal void CloseWindows()
        {
            var host = UiHostOf(graph);
            if (host != null)
            {
                host.CloseWindows(this);
                return;
            }
            // The windows only hide when closed (their Closing is cancelled).
            windowsWanted = windowsWanted with { ParameterWindowVisible = false, GuiWindowVisible = false };
        }

        /// <summary>Which windows are open and where (saved with the song).</summary>
        internal MachineWindowState GetWindowState() => UiHostOf(graph)?.GetWindowState(this) ?? windowsWanted;

        public void ExecuteCommand(int id)
        {

        }

        // MULTI IO
        public string GetChannelName(bool input, int index)
        {
            string ret = "";
            if (input)
            {
                if (inputChannelNames.ContainsKey(index))
                {
                    ret = inputChannelNames[index];
                }
            }
            else if (outputChannelNames.ContainsKey(index))
            {
                ret = outputChannelNames[index];
            }
            return ret;
        }

        internal bool sendControlChangesFlag;

        public bool IsSeqThru { get; internal set; }
        public bool IsSeqMute { get; internal set; }


        internal bool workDone;

        public void SendControlChanges()
        {
            //var machineManager = (graph.Buzz as ReBuzzCore).MachineManager;

            // Control machines !MUST! send the changes immediately before Work() so that events get triggered correctly
            //machineManager.Tick(this);

            // Below does not work because the parameter changes might slip to next tick.
            sendControlChangesFlag = true;
        }

        public byte[] SendGUIMessage(byte[] message)
        {
            var buzz = graph.Buzz as ReBuzzCore;
            return buzz.MachineManager.SendGUIMessage(this, message);
        }

        public void SendMIDIControlChange(int ctrl, int channel, int value)
        {
            if (Ready)
            {
                var buzz = graph.Buzz as ReBuzzCore;
                buzz.MachineManager.SendMIDIControlChange(this, ctrl, channel, value);
            }
        }

        public void SendMIDINote(int channel, int value, int velocity)
        {
            if (Ready)
            {
                // MIDI Polyphonic Expression
                // Not implemented, don't know why the 0x80 bit is set in channel but convert to 0 for now
                if ((channel & 0x80) != 0)
                    //channel &= 0x7F;
                    channel = 0;
                var buzz = graph.Buzz as ReBuzzCore;
                buzz.MachineManager.SendMIDINote(this, channel, value, velocity);
            }
        }

        public void ShowContextMenu(int x, int y)
        {
        }

        public void ShowDialog(MachineDialog d, int x, int y)
        {
            switch (d)
            {
                case MachineDialog.Parameters:
                    OpenParameterWindow();
                    break;
                case MachineDialog.Patterns:
                    if (PatternsList.Count > 0)
                    {
                        var buzz = graph.Buzz as ReBuzzCore;
                        buzz.SetPatternEditorPattern(PatternsList[0]);
                        buzz.ActiveView = BuzzView.PatternView;
                    }
                    break;
                case MachineDialog.Attributes:
                    OpenParameterWindow();
                    break;
                case MachineDialog.Rename:
                    var newName = UiHostOf(graph)?.AskName("Rename Machine", name, x, y);
                    if (newName != null)
                    {
                        (graph.Buzz as ReBuzzCore).SongCore.RenameMachineUndoable(this, newName.Trim());
                    }
                    break;
                case MachineDialog.Delay:
                    break;
            }
        }

        public void ShowHelp()
        {
            string[] extensions = new string[] { ".html", ".htm", ".pdf", ".txt" };

            foreach (var ext in extensions)
            {
                string helpFile = DLL.Path.Substring(0, DLL.Path.Length - 4) + ext;
                if (File.Exists(helpFile))
                {
                    var ps = new ProcessStartInfo(helpFile)
                    {
                        UseShellExecute = true,
                        Verb = "open"
                    };
                    Process.Start(ps);
                    return;
                }
                if (DLL.Path.ToLower().EndsWith(".x64.dll"))
                {
                    helpFile = DLL.Path.Substring(0, DLL.Path.Length - 8) + ext;
                    if (File.Exists(helpFile))
                    {

                        var ps = new ProcessStartInfo(helpFile)
                        {
                            UseShellExecute = true,
                            Verb = "open"
                        };
                        Process.Start(ps);
                        return;
                    }
                }
            }
        }

        public void ShowPresetEditor()
        {
            OpenParameterWindow();
        }

        public void UnbindAllMIDIControllers()
        {
            var buzz = graph.Buzz as ReBuzzCore;
            buzz.MidiControllerAssignments.UnbindAllMIDIControllers(this);
        }

        internal void AddParameterGroup(ParameterGroup group)
        {
            parameterGroups.Add(group);
        }

        internal void AddOutput(MachineConnectionCore mcc)
        {
            this.outputs.Add(mcc);

        }

        internal void AddInput(MachineConnectionCore mcc)
        {
            this.inputs.Add(mcc);
            var buzz = graph.Buzz as ReBuzzCore;
            buzz.TopologyGeneration++; // #107: invalidate cached work order
            buzz.LatencyGraphChanged(); // fork (mastering-tools.md §4.1): the compensation's plan follows
            //if (mcc.Source.DLL.Info.Flags.HasFlag(MachineInfoFlags.DOES_INPUT_MIXING))
            buzz.MachineManager.AddInput(this, mcc.Source, true);
        }

        internal void ResetParametersToNoValue()
        {
            foreach (ParameterCore p in ParameterGroupsList[1].Parameters)
            {
                p.SetValue(0, p.NoValue);
            }

            // Tracks
            for (int i = 0; i < TrackCount; i++)
            {
                foreach (ParameterCore p in ParameterGroupsList[2].Parameters)
                {
                    p.SetValue(i, p.NoValue);
                }
            }
        }

        internal void SetParametersToDefaulValue()
        {
            foreach (ParameterCore p in ParameterGroupsList[0].Parameters)
            {
                if (p.Flags.HasFlag(ParameterFlags.State))
                {
                    p.SetValue(0, p.DefValue);
                }
                else
                {
                    p.SetValue(0, p.NoValue);
                }
            }

            foreach (ParameterCore p in ParameterGroupsList[1].Parameters)
            {
                if (p.Flags.HasFlag(ParameterFlags.State))
                {
                    p.SetValue(0, p.DefValue);
                }
                else
                {
                    // Might fix some old machines...
                    if (p.Type == ParameterType.Note && DLL.Info.Version <= MachineManager.BUZZ_MACHINE_INTERFACE_VERSION_12)
                    {
                        // Is this the default note?
                        p.SetValue(0, BuzzNote.Parse("C-4"));
                    }
                    else
                    {
                        p.SetValue(0, p.NoValue);
                    }
                }
            }

            // Tracks
            for (int i = 0; i < TrackCount; i++)
            {
                foreach (ParameterCore p in ParameterGroupsList[2].Parameters)
                {
                    if (p.Flags.HasFlag(ParameterFlags.State))
                    {
                        p.SetValue(i, p.DefValue);
                    }
                    else
                    {
                        p.SetValue(i, p.NoValue);
                    }
                }
            }
        }

        internal void RemoveOutput(IMachineConnection mc)
        {
            outputs.Remove(mc);
        }
        internal void RemoveInput(IMachineConnection mc)
        {
            var buzz = graph.Buzz as ReBuzzCore;
            buzz.TopologyGeneration++; // #107: invalidate cached work order
            buzz.LatencyGraphChanged(); // fork (mastering-tools.md §4.1): the compensation's plan follows
            var group = parameterGroups[0];
            int trackToRemove = Inputs.IndexOf(mc);

            if (inputs.Remove(mc) == true && group.TrackCount > 0)
            {
                var parameters = group.ParametersList;
                foreach (var p in parameters)
                {
                    buzz.MidiControllerAssignments.BindParameter(p, trackToRemove, -1);     // Remove MIDI binding
                }

                // Move rest of the parameters to correct positions
                for (int i = trackToRemove; i < group.TrackCount - 1; i++)
                {
                    foreach (var p in parameters)
                    {
                        p.SetDisplayName(i, p.GetDisplayName(i + 1));
                        p.SetValue(i, p.GetValue(i + 1));
                        var b = p.GetMIDIBinding(i + 1);
                        buzz.MidiControllerAssignments.BindParameter(p, i, -1);     // Remove MIDI binding
                        buzz.MidiControllerAssignments.BindParameter(p, i + 1, -1); // Remove MIDI binding
                        if (b.Item1 != -1)
                        {
                            buzz.MidiControllerAssignments.BindParameter(p, i, b.Item1, b.Item2);   // Set midi biding
                        }
                    }
                }

                group.TrackCount--;
            }
            PropertyChanged.Raise(this, "Inputs");
            buzz.MachineManager.DeleteInput(this, mc.Source);
        }

        // Managed
        internal void SetCommands()
        {
            var buzz = graph.Buzz as ReBuzzCore;
            var cmds = buzz.MachineManager.GetCommands(this);
            if (cmds != null)
                commands = cmds.ToList();
        }

        // Native
        internal void SetCommands(string commandsStr)
        {
            if (commandsStr == "")
                return;

            // Clear menu tree
            this.commands.Clear();
            var comnds = commandsStr.Split('\n');
            for (int i = 0; i < comnds.Length; i++)
            {
                var cmd = comnds[i].Trim();
                int c_index = cmd.IndexOf("&");
                if (c_index >= 0)
                {
                    cmd = cmd.Remove(c_index, 1).Insert(c_index, "_");
                }
                /*
                else if (cmd.StartsWith("/"))
                {
                    //string subMenu = buzz.MachineManager.GetSubMenu(this, i);
                }
                */

                this.commands.Add(new MenuItemCore() { Command = MachineMenuCommand, CommandParameter = i, Text = cmd.Trim(), IsEnabled = true });
            }
            CreateSubMenus();
        }

        internal void CreateSubMenus()
        {
            // Submenu 0: CommandParameter == 256..511
            // Submenu 1: CommandParameter == 512..767
            // ...
            int index = 256;
            foreach (var cmd in commands)
            {
                var command = cmd as MenuItemCore;
                if (command.Text.StartsWith("/"))
                {
                    command.Text = command.Text.Remove(0, 1);
                    PopulateSubMenu(command, index);
                    index += 256;
                }
            }
        }

        private void PopulateSubMenu(MenuItemCore rootSubmenuItem, int index)
        {
            var buzz = graph.Buzz as ReBuzzCore;

            // Get submenu commands.
            string[] comnds = buzz.MachineManager.GetSubMenu(this, (int)rootSubmenuItem.CommandParameter);
            for (int i = 0; i < comnds.Length; i++)
            {
                var stringMenuCommandPath = comnds[i].Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                var subMenuItem = rootSubmenuItem;

                // Create sub sub menus if needed and find the target menu item. subMenuItem will be the menu item for command.
                for (int j = 0; j < stringMenuCommandPath.Length - 1; j++)
                {
                    string text = stringMenuCommandPath[j].Trim();

                    var foundItem = subMenuItem.ChildrenList.FirstOrDefault(item => item.Text == text);
                    if (foundItem != null)
                    {
                        subMenuItem = foundItem;
                    }
                    else
                    {
                        var menuCommand = new MenuItemCore() { Command = MachineMenuCommand, Text = stringMenuCommandPath[j].Trim(), IsEnabled = true };
                        subMenuItem.ChildrenList.Add(menuCommand);
                        subMenuItem = menuCommand;
                    }
                }
                // The actual command
                string name = stringMenuCommandPath.Last().Trim();

                MenuItemCore finalMenuCommand = null;

                // Is the CommandParameter correct?
                finalMenuCommand = new MenuItemCore() { Command = MachineMenuCommand, CommandParameter = index, Text = name, IsEnabled = true };

                index++;
                subMenuItem.ChildrenList.Add(finalMenuCommand);
            }
        }

        readonly Sample[] stereoSamples = new Sample[256];
        internal Sample[] GetStereoSamples(int nSamples)
        {
            Array.Clear(stereoSamples, 0, nSamples);

            // Sample is a struct of two sequential floats {L, R} (8 bytes, no
            // padding), so Sample[0..nSamples) reinterprets exactly as
            // float[0..2*nSamples) with interleaved L,R. The accumulation below
            // is an elementwise single-precision add (dst[j] = dst[j] + src[j]):
            // identical operations on identical elements, with each slot seeing
            // its inputs in the same order as the scalar loop - no reduction,
            // no FMA => bit-exact.
            var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<Sample, float>(stereoSamples.AsSpan(0, nSamples));

            foreach (var input in inputs)
            {
                if (!IsVisibleInput(input))
                    continue;
                var inputCore = input as MachineConnectionCore;
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<Sample, float>(inputCore.Buffer.AsSpan(0, nSamples));

                int n = dst.Length; // 2 * nSamples
                int vw = Vector<float>.Count;
                int j = 0;
                for (; j <= n - vw; j += vw)
                {
                    (new Vector<float>(dst.Slice(j, vw)) + new Vector<float>(src.Slice(j, vw))).CopyTo(dst.Slice(j, vw));
                }
                for (; j < n; j++)
                {
                    dst[j] += src[j];
                }
            }

            //Utils.FlushDenormalToZero(samples);
            return stereoSamples;
        }

        internal void UpdateOutputs(Sample[] samples, int nSamples, bool denormal = true)
        {
            // No need to update outputs if machine is hidden, because the outputs are not used in the audio graph.
            if (Hidden) return;

            foreach (var output in outputs)
            {
                if (!IsVisibleOutput(output))
                    continue;
                var outputCore = output as MachineConnectionCore;
                outputCore.ClearBuffer(nSamples);
                outputCore.PreUpdateBufferActions(nSamples);
                outputCore.UpdateBuffer(samples, nSamples);
                outputCore.PostUpdateBufferActions(nSamples);
            }
        }

        readonly List<Sample[]> activeChannels = new List<Sample[]>(256);
        readonly Sample[][] multiSamplesInBuffers = new Sample[256][];
        internal List<Sample[]> GetMultiIOSamples(int nSamples)
        {
            // Check if enough channels to mix input destinations
            activeChannels.Clear();

            for (int i = 0; i < InputChannelCount; i++)
            {
                activeChannels.Add(null);
            }

            // Clear all active channels
            foreach (var input in inputs)
            {
                if (!IsVisibleInput(input))
                    continue;
                var inputCore = input as MachineConnectionCore;
                var targetChannelList = inputCore.DestinationChannels;

                // Output to multiple channels, so we need to mix the input to all target channels
                for (int targetChannelIndex = 0; targetChannelIndex < targetChannelList.Count(); targetChannelIndex++)
                {
                    int targetChannel = targetChannelList.ElementAt(targetChannelIndex);

                    if (multiSamplesInBuffers[targetChannel] == null)
                    {
                        multiSamplesInBuffers[targetChannel] = new Sample[256];
                    }

                    if (activeChannels[targetChannel] == null)
                    {
                        activeChannels[targetChannel] = multiSamplesInBuffers[targetChannel];
                    }

                    Sample[] samples = activeChannels[targetChannel];

                    for (int i = 0; i < nSamples; i++)
                    {
                        samples[i].L = 0;
                        samples[i].R = 0;
                    }
                }
            }

            // Mix all inputs to their destination channels
            foreach (var input in inputs)
            {
                if (!IsVisibleInput(input))
                    continue;
                var inputCore = input as MachineConnectionCore;
                var targetChannelList = inputCore.DestinationChannels;

                // Output to multiple channels, so we need to mix the input to all target channels
                for (int targetChannelIndex = 0; targetChannelIndex < targetChannelList.Count(); targetChannelIndex++)
                {
                    int targetChannel = targetChannelList.ElementAt(targetChannelIndex);

                    Sample[] samples = activeChannels[targetChannel];

                    for (int i = 0; i < nSamples; i++)
                    {
                        samples[i].L += inputCore.Buffer[i].L;
                        samples[i].R += inputCore.Buffer[i].R;
                    }
                }
            }

            return activeChannels;
        }

        internal void UpdateOutputs(List<Sample[]> multiSamplesOut, int nSamples)
        {
            // No need to update outputs if machine is hidden, because the outputs are not used in the audio graph.
            if (Hidden) return;

            for (int o = 0; o < outputs.Count; o++)
            {
                var output = outputs[o];

                if (!IsVisibleOutput(output))
                    continue;

                var outputCore = output as MachineConnectionCore;
                outputCore.ClearBuffer(nSamples);
                outputCore.PreUpdateBufferActions(nSamples);

                for (int channelIndex = 0; channelIndex < outputCore.SourceChannels.Count(); channelIndex++)
                {
                    int channel = outputCore.SourceChannels.ElementAt(channelIndex);

                    if (channel < multiSamplesOut.Count &&
                        multiSamplesOut[channel] != null)
                    {
                        Sample[] samples = multiSamplesOut[channel];
                        outputCore.UpdateBuffer(samples, nSamples);
                    }
                }

                outputCore.PostUpdateBufferActions(nSamples);
            }
        }

        private readonly float AMP_EPS = 1.0f;
        public bool GetActivity(int num)
        {
            bool isActive = false;

            float maxSample = 0.0f;

            bool useInputs = Name == "Master";
            var connections = useInputs ? inputs : outputs;

            foreach (var output in connections)
            {
                if (useInputs ? !IsVisibleInput(output) : !IsVisibleOutput(output))
                    continue;
                var outputCore = output as MachineConnectionCore;
                Sample[] samples = outputCore.Buffer;
                for (int i = 0; i < num; i++)
                {
                    maxSample = Math.Max(Math.Abs(samples[i].L), maxSample);
                    maxSample = Math.Max(Math.Abs(samples[i].R), maxSample);
                }
            }

            if (maxSample > AMP_EPS)
                isActive = true;

            return isActive;
        }

        // Fork (audio hygiene): the recursive work algorithm's per-machine scratch, made once (its
        // inputs in the order they go out, the jobs it queued, and the join it waits on), instead
        // of a Task per input and LINQ's sort on every buffer. Used under workLock.
        internal MachineCore[] workInputs = new MachineCore[4];
        internal long[] workInputKeys = new long[4];
        internal ReBuzz.Audio.RenderJobs.Job[] workJobs = new ReBuzz.Audio.RenderJobs.Job[4];
        internal readonly ReBuzz.Audio.RenderJoin workJoin = new ReBuzz.Audio.RenderJoin();

        internal List<int> setMachineTrackCountList = new List<int>();
        internal bool updateWaveInfo;
        internal long performanceLastCount;
        internal long performanceBranchCount;
        internal Dictionary<int, int> remappedLoadedMachineParameterIndexes;
        internal IMachineGUI gui;
        internal int oversampleFactorOnTick = 1;    // Changes value on Tick
        private readonly string buzzPath;
        private readonly IUiDispatcher dispatcher;
        private readonly EngineSettings engineSettings;

        internal void SetMachineTrackCount(int trackCount)
        {
            setMachineTrackCountList.Add(trackCount);
        }

        internal void RaiseTrackCount()
        {
            PropertyChanged?.Raise(this, "TrackCount");
        }

        internal void ClearEvents()
        {
            PropertyChanged = null;
            PatternAdded = null;
            PatternRemoved = null;
            foreach (var pg in parameterGroups)
            {
                foreach (ParameterCore p in pg.ParametersList)
                {
                    p.ClearEvents();
                }
            }
        }

        internal void AttributesChanged()
        {
            PropertyChanged.Raise(this, "Attributes");
        }

        internal void UpdateIsActive()
        {
            bool active = false;
            foreach (var input in inputs)
            {
                if (!IsVisibleInput(input))
                    continue;
                active |= input.Source.IsActive;
            }

            IsActive = active;
        }
    }
}
