// Pedal Peer Control – managed machine for Jeskola Buzz build 1503 (32-bit)
// Based on BTDSys PeerCtrl, original C++ © 2002-2008 Ed Powley (BTDSys)
//
// Target: net48 (Buzz 1503 hosts the .NET Framework 4 CLR).
// Build:  dotnet build -c Release   (BuzzDir set in Directory.Build.props)
// Deploy: DLL is written directly to $(BuzzDir)\Gear\Generators\

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using Buzz.MachineInterface;   // IBuzzMachine, IBuzzMachineHost, MachineDecl, ParameterDecl, MasterInfo
using BuzzGUI.Interfaces;      // IMenuItem, IMachine, IParameter, IParameterGroup, IBuzz, etc.
// Buzz 1503's BuzzGUI.Interfaces.dll exports both namespaces above.

namespace BTDSys.PeerCtrl
{
    // =========================================================================
    // Mapping envelope (piecewise-linear curve, identical to original)
    // =========================================================================

    [Serializable]
    public class EnvPoint
    {
        public int X { get; set; }   // 0-65535
        public int Y { get; set; }   // 0-65535
    }

    [Serializable]
    public class EnvData
    {
        // null by default: XmlSerializer replaces a null list cleanly.
        // If it were initialised to a non-null list, XmlSerializer would ADD
        // the deserialized items to it, causing duplicate/crossed points.
        public List<EnvPoint> Points { get; set; } = null;

        public static List<EnvPoint> DefaultPoints() => new List<EnvPoint>
        {
            new EnvPoint { X = 0,     Y = 0      },   // input 0%   → param 0%
            new EnvPoint { X = 65535, Y = 65535  }    // input 100% → param 100%
        };

        // Use ActivePoints everywhere instead of Points directly,
        // so null (not-yet-initialised) falls back to the default straight line.
        List<EnvPoint> ActivePoints => Points ?? DefaultPoints();

        public float Evaluate(float pos)
        {
            var pts = ActivePoints;
            if (pts.Count < 2) return pos;

            int p = 0;
            while (p < pts.Count - 2 && pts[p + 1].X <= pos) p++;

            var p0 = pts[p];
            var p1 = pts[p + 1];
            if (p1.X == p0.X) return p0.Y;

            float t = (pos - p0.X) / (float)(p1.X - p0.X);
            return p0.Y + t * (p1.Y - p0.Y);
        }

        public void Reset() => Points = DefaultPoints();
    }

    // =========================================================================
    // One assignment on a track  (a track may have many of these)
    // =========================================================================

    [Serializable]
    public class TrackAssignment : INotifyPropertyChanged
    {
        string _machineName    = "";
        int    _paramIndex     = -1;   // flat index into GetAllParams() list
        int    _targetTrack    = -1;   // -1 = all tracks on the target machine

        int    _midiChannel    = 0;    // 0=off, 1-16, 17=all
        int    _midiController = -1;   // -1=off, 0-127
        bool   _midiFeedback   = false;
        bool   _midiIncDec     = false;
        bool   _midiIncDecWrap = false;

        public string MachineName    { get => _machineName;    set { _machineName    = value ?? ""; N(); } }
        public int    ParamIndex     { get => _paramIndex;     set { _paramIndex     = value;        N(); } }
        public int    TargetTrack    { get => _targetTrack;    set { _targetTrack    = value;        N(); } }
        public int    MidiChannel    { get => _midiChannel;    set { _midiChannel    = value;        N(); } }
        public int    MidiController { get => _midiController; set { _midiController = value;        N(); } }
        public bool   MidiFeedback   { get => _midiFeedback;   set { _midiFeedback   = value;        N(); } }
        public bool   MidiIncDec     { get => _midiIncDec;     set { _midiIncDec     = value;        N(); } }
        public bool   MidiIncDecWrap { get => _midiIncDecWrap; set { _midiIncDecWrap = value;        N(); } }

        public EnvData Mapping { get; set; } = new EnvData();

        // Runtime – not serialised
        [System.Xml.Serialization.XmlIgnore]
        public IMachine   ResolvedMachine { get; set; }
        [System.Xml.Serialization.XmlIgnore]
        public IParameter ResolvedParam   { get; set; }

        [System.Xml.Serialization.XmlIgnore]
        public string Label =>
            ResolvedMachine == null
                ? $"[{_machineName}] (not found)"
                : ResolvedParam == null
                    ? $"{_machineName} / (no param)"
                    : _targetTrack < 0
                        ? $"{_machineName} / {ResolvedParam.Name}"
                        : $"{_machineName} / {ResolvedParam.Name} [tr {_targetTrack}]";

        /// <summary>
        /// Map value01 through the curve and set the target parameter.
        /// Mirrors CTrack::FloatToPVal + UpdateInertia send path from the original.
        /// </summary>
        // Track last sent feedback CC value (0-127, -1 = never sent) to avoid
        // flooding the controller with identical values
        [System.Xml.Serialization.XmlIgnore]
        public int LastFeedbackSent { get; set; } = -1;

        public void ApplyValue(float value01, BuzzGUI.Interfaces.IBuzz buzz, bool sendFeedback = true)
        {
            if (string.IsNullOrEmpty(_machineName) || _paramIndex < 0 || buzz == null) return;

            // Re-resolve fresh every call – IParameter/IMachine references become
            // stale after song reload when the host recreates these objects.
            var machine = buzz.Song?.Machines?.FirstOrDefault(m => m.Name == _machineName);
            if (machine == null) return;

            var allp = PeerCtrlMachine.GetAllParams(machine);
            if (_paramIndex >= allp.Count) return;
            var param = allp[_paramIndex];

            float mappedY = Mapping.Evaluate(value01 * 65535f);
            // mappedY is in [0,65535]: 0 = param min, 65535 = param max
            float f = mappedY / 65535f;

            int min  = param.MinValue;
            int max  = param.MaxValue + 1;
            int pval = (int)(f * (max - min) + min);
            pval = Math.Max(min, Math.Min(max - 1, pval));

            bool isTrackParam =
                param.Group != null &&
                param.Group.Type == ParameterGroupType.Track;

            if (_targetTrack < 0 && isTrackParam)
                for (int tr = 0; tr < machine.TrackCount; tr++)
                    param.SetValue(tr, pval);
            else
                param.SetValue(_targetTrack < 0 ? 0 : _targetTrack, pval);

            // MIDI feedback – only when requested and value has actually changed
            if (sendFeedback && _midiFeedback && _midiController >= 0 && _midiChannel != 0)
            {
                int ccVal = Math.Max(0, Math.Min(127, (int)(value01 * 127f + 0.5f)));
                if (ccVal != LastFeedbackSent)
                {
                    LastFeedbackSent = ccVal;
                    MidiOutputHelper.Send(_midiController, _midiChannel, ccVal);
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void N() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    // =========================================================================
    // Runtime per-track state (not serialised – rebuilt from parameters each session)
    // =========================================================================

    public class TrackState
    {
        public float ValueCurrent  = 0f;
        public float ValueTarget   = 0f;
        public float ValueStep     = 0f;
        public bool  Sliding       = false;
        public bool  SlidingFromMidi = false;
        public float LastSent      = -1f;
        public bool  Slaved        = false;

        // Cross-thread hand-off to the UI thread (see PeerCtrlMachine.Flush).
        public volatile bool Dirty;            // targets need ValueCurrent applied
        public volatile bool PendingFeedback;  // send MIDI feedback with it
        public int  WriteBack = -1;            // own Value param write-back (0-65534)
        public volatile int EchoGuard = -1;    // ignore our own write-back echo

        public List<TrackAssignment> Assignments = new List<TrackAssignment>();
    }

    // =========================================================================
    // Serialisable machine state  (assignments + configuration)
    // =========================================================================

    [Serializable]
    public class PeerCtrlState
    {
        public List<List<TrackAssignment>> TrackAssignments { get; set; }
            = new List<List<TrackAssignment>>();

        public int MidiIncDecAmount { get; set; } = 1024;
        public int SendFreq         { get; set; } = 2;
        public int StopOnMute       { get; set; } = 0;

        // Saved Value parameter for each track (0-65534), for feedback restoration
        // -1 means not saved
        public List<int> SavedTrackValues { get; set; } = new List<int>();
    }

    // =========================================================================
    // The machine
    // =========================================================================

    [MachineDecl(
        Name        = "Pedal Peer Control",
        ShortName   = "Peer Control",
        Author      = "thepedal (after BTDSys PeerCtrl by Ed Powley)",
        MaxTracks   = PeerCtrlMachine.MAX_TRACKS,
        InputCount  = 0,
        OutputCount = 0)]
    public class PeerCtrlMachine : IBuzzMachine
    {
        public const int MAX_TRACKS = 64;

        IBuzzMachineHost host;

        // ── Global parameter: Inertia ─────────────────────────────────────────
        [ParameterDecl(
            Name        = "Inertia",
            Description = "Glide time in ticks/10 (0 = off, 10 = 1 tick, 1280 = 128 ticks)",
            MinValue    = 0,
            MaxValue    = 1280,
            DefValue    = 0)]
        public int Inertia
        {
            get => _inertia;
            set
            {
                _inertia      = value;
                InertiaOn     = value > 0;
                InertiaLength = value / 10.0f;
                if (!InertiaOn)
                    for (int i = 0; i < MAX_TRACKS; i++)
                        StopInertia(_tracks[i]);
                // Deliberately no PropertyChanged here. Raising it from this
                // parameter setter hung Buzz 1503 (most likely a loop with
                // the host's own parameter-change handling).
            }
        }
        int   _inertia;
        public bool  InertiaOn     { get; private set; }
        public float InertiaLength { get; private set; }  // in ticks

        // ── Track parameter: Slaved ───────────────────────────────────────────
        // Method form: void MethodName(int value, int track)
        [ParameterDecl(
            Name        = "Slaved",
            Description = "Mirror the previous track's value (switch: 0=no, 1=yes)",
            MinValue    = 0,
            MaxValue    = 1,
            DefValue    = 0)]
        public void SetSlaved(int value, int track)
        {
            if ((uint)track >= MAX_TRACKS) return;
            // Track 0 can never be slaved (original behaviour)
            _tracks[track].Slaved = (track > 0) && (value == 1);
        }

        // ── Track parameter: Value ────────────────────────────────────────────
        [ParameterDecl(
            Name        = "Value",
            Description = "Control value (0 = 0%, 65534 = 100%) passed through the mapping curve",
            MinValue    = 0,
            MaxValue    = 65534,
            DefValue    = 32767)]
        public void SetValue(int value, int track)
        {
            if ((uint)track >= MAX_TRACKS) return;
            var tsv = _tracks[track];
            if (tsv.EchoGuard == value) { tsv.EchoGuard = -1; return; }  // our own MIDI write-back
            if (tsv.Slaved) return;
            if (_initialising)
            {
                _initialising = false;
                ResolveAllMachines();
            }
            ValueChange(_tracks[track], track, value / 65534.0f);
        }

        // ── "Attribute-equivalent" configuration ─────────────────────────────
        // The original used Buzz CMachineAttribute for these.  In the managed
        // API there is no AttributeDecl equivalent, so they live in
        // PeerCtrlState and are edited through the Settings dialog.

        /// <summary>Step size for MIDI CC 96/97 inc/dec encoders (0-65534).</summary>
        public int MidiIncDecAmount { get; set; } = 1024;

        /// <summary>
        /// How many Work() calls between inertia pushes.
        /// 0 = tick only (no sub-tick updates).
        /// Mirrors the original "Ctrl Rate" / "Send Freq" attribute.
        /// </summary>
        public int SendFreq { get; set; } = 2;

        /// <summary>When 1, no control changes are sent while the machine is muted.</summary>
        public int StopOnMute { get; set; } = 0;

        // ── Internal runtime state ────────────────────────────────────────────
        readonly TrackState[] _tracks = new TrackState[MAX_TRACKS];
        int  _updateCounter;
        int  _samplesAccum;
        bool _initialising = true;

        // Tick() is not part of the documented managed IBuzzMachine surface
        // (ReBuzz's host doesn't call it either), so the post-load work that
        // used to live in Tick() runs from the first Work() call after
        // construction, MachineState.set or ImportFinished.
        volatile bool _loadPending = true;
        int  _retryCounter;
        const int RetryEveryNWorks = 64;   // unresolved-target retry cadence

        // Control-machine Work() has no sample count, so elapsed samples are
        // derived from the wall clock and the song's sample rate.
        long _lastWorkTimestamp;
        const int MaxSamplesPerWork = 8192;  // clamp after stalls (song load etc.)

        // Threading rule (Buzz 1503): IParameter.SetValue / GetValue and song-
        // graph access happen ONLY on the UI thread. Calling SetValue on a
        // target from Work() (audio thread) hung Buzz 1503. Work(), MIDI and
        // parameter setters only update TrackState and request a Flush(),
        // which runs on the UI dispatcher.
        readonly Dispatcher _ui;
        int _flushQueued;   // 0/1, Interlocked
        int _retryQueued;   // 0/1, Interlocked

        IBuzz Buzz => host?.Machine?.Graph?.Buzz;

        // =====================================================================
        // Constructor
        // =====================================================================

        public PeerCtrlMachine(IBuzzMachineHost host)
        {
            this.host = host;
            for (int i = 0; i < MAX_TRACKS; i++)
                _tracks[i] = new TrackState();

            // The constructor runs on Buzz's UI thread ({U} in the managed API docs).
            _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        // =====================================================================
        // IBuzzMachine – host binding
        // =====================================================================

        public IBuzzMachineHost Host
        {
            get => host;
            set => host = value;   // kept for interface compliance; set via constructor in practice
        }

        // =====================================================================
        // State serialisation
        // MachineState property name must match the class name used by Buzz
        // to know which type to (de)serialise.
        // =====================================================================

        public PeerCtrlState MachineState
        {
            get
            {
                var st = new PeerCtrlState
                {
                    MidiIncDecAmount = MidiIncDecAmount,
                    SendFreq         = SendFreq,
                    StopOnMute       = StopOnMute,
                };
                for (int t = 0; t < MAX_TRACKS; t++)
                {
                    st.TrackAssignments.Add(
                        new List<TrackAssignment>(_tracks[t].Assignments));
                    // Save the current float value as a raw int (0-65534)
                    st.SavedTrackValues.Add((int)(_tracks[t].ValueCurrent * 65534f + 0.5f));
                }
                return st;
            }
            set
            {
                if (value == null) return;
                MidiIncDecAmount = value.MidiIncDecAmount;
                SendFreq         = value.SendFreq;
                StopOnMute       = value.StopOnMute;
                for (int t = 0; t < MAX_TRACKS; t++)
                {
                    _tracks[t].Assignments.Clear();
                    if (t < value.TrackAssignments.Count)
                        _tracks[t].Assignments.AddRange(value.TrackAssignments[t]);

                    // Restore saved value so feedback sends the correct position
                    if (t < value.SavedTrackValues.Count && value.SavedTrackValues[t] >= 0)
                    {
                        float v = Clamp01(value.SavedTrackValues[t] / 65534.0f);
                        _tracks[t].ValueCurrent = _tracks[t].ValueTarget = v;
                    }
                }
                _initialising = false;
                _loadPending  = true;
                ResolveAllMachines();
            }
        }

        // Buzz calls this after song load / template import / clone so that
        // machine renames can be applied to stored assignments.
        public void ImportFinished(IDictionary<string, string> nameMap)
        {
            if (nameMap != null)
                foreach (var ts in _tracks)
                    foreach (var a in ts.Assignments)
                        if (!string.IsNullOrEmpty(a.MachineName) &&
                            nameMap.TryGetValue(a.MachineName, out var n))
                            a.MachineName = n;
            ResolveAllMachines();
            _loadPending = true;
        }

        // =====================================================================
        // Machine-graph resolution
        // =====================================================================

        public void ResolveAssignment(TrackAssignment a)
        {
            a.ResolvedMachine = null;
            a.ResolvedParam   = null;
            if (Buzz == null || string.IsNullOrEmpty(a.MachineName)) return;

            a.ResolvedMachine = Buzz.Song.Machines
                .FirstOrDefault(m => m.Name == a.MachineName);

            if (a.ResolvedMachine == null || a.ParamIndex < 0) return;

            var allp = GetAllParams(a.ResolvedMachine);
            if (a.ParamIndex < allp.Count)
                a.ResolvedParam = allp[a.ParamIndex];
        }

        public void ResolveAllMachines()
        {
            if (Buzz == null) return;
            foreach (var ts in _tracks)
            {
                bool anyNewlyResolved = false;
                foreach (var a in ts.Assignments)
                {
                    bool wasMissing = a.ResolvedParam == null;
                    ResolveAssignment(a);
                    if (wasMissing && a.ResolvedParam != null)
                        anyNewlyResolved = true;
                }
                // Push the current value to any newly resolved targets immediately
                if (anyNewlyResolved && !_initialising)
                    SendNow(ts);
            }
        }

        /// <summary>
        /// Flat list of controllable parameters for a machine:
        /// global params first, then track params – skipping group 0 (internal).
        /// The list index is used as ParamIndex in TrackAssignment.
        /// </summary>
        public static List<IParameter> GetAllParams(IMachine m)
        {
            var list = new List<IParameter>();
            if (m?.ParameterGroups == null) return list;
            for (int g = 1; g < m.ParameterGroups.Count; g++)
            {
                var grp = m.ParameterGroups[g];
                if (grp?.Parameters != null)
                    list.AddRange(grp.Parameters);
            }
            return list;
        }

        // =====================================================================
        // Value-change + inertia engine
        // (mirrors CTrack::ValueChange + CTrack::UpdateInertia from original)
        // =====================================================================

        void ValueChange(TrackState ts, int trackIdx, float v01, bool fromMidi = false)
        {
            ts.ValueTarget = Clamp01(v01);

            if (InertiaOn && host != null)
            {
                int   spt   = host.MasterInfo?.SamplesPerTick ?? 256;
                float steps = InertiaLength * spt;
                if (steps > 0f)
                {
                    ts.ValueStep      = (ts.ValueTarget - ts.ValueCurrent) / steps;
                    ts.Sliding        = true;
                    ts.SlidingFromMidi = fromMidi;
                }
                else
                {
                    ts.ValueCurrent = ts.ValueTarget;
                    ts.Sliding      = false;
                    SendNow(ts, fromMidi);
                }
            }
            else
            {
                ts.ValueCurrent = ts.ValueTarget;
                ts.Sliding      = false;
                SendNow(ts, fromMidi);
            }

            int next = trackIdx + 1;
            if (next < MAX_TRACKS && _tracks[next].Slaved)
                ValueChange(_tracks[next], next, v01, fromMidi);
        }

        void SendNow(TrackState ts, bool fromMidi = false)
        {
            ts.ValueCurrent    = Clamp01(ts.ValueCurrent);
            ts.LastSent        = ts.ValueCurrent;
            ts.PendingFeedback = !fromMidi;
            ts.Dirty           = true;
            RequestFlush();
        }

        void RequestFlush()
        {
            if (Interlocked.Exchange(ref _flushQueued, 1) != 0) return;
            try { _ui.BeginInvoke(DispatcherPriority.Normal, new Action(Flush)); }
            catch { Interlocked.Exchange(ref _flushQueued, 0); }
        }

        // UI thread: push pending values to targets and write MIDI values back
        // to our own Value parameter.
        void Flush()
        {
            Interlocked.Exchange(ref _flushQueued, 0);
            var buzz = Buzz;
            IParameter ownValueParam = null;
            bool ownLooked = false;

            for (int t = 0; t < MAX_TRACKS; t++)
            {
                var ts = _tracks[t];
                if (!ts.Dirty) continue;
                ts.Dirty = false;

                float v  = ts.ValueCurrent;
                bool  fb = ts.PendingFeedback;
                TrackAssignment[] list;
                try { list = ts.Assignments.ToArray(); } catch { list = new TrackAssignment[0]; }
                foreach (var a in list)
                {
                    try { a.ApplyValue(v, buzz, sendFeedback: fb); } catch { }
                }

                int wb = Interlocked.Exchange(ref ts.WriteBack, -1);
                if (wb >= 0)
                {
                    try
                    {
                        if (!ownLooked)
                        {
                            ownLooked = true;
                            var machine = host?.Machine;
                            if (machine?.ParameterGroups?.Count > 2)
                                ownValueParam = machine.ParameterGroups[2].Parameters?.LastOrDefault();
                        }
                        if (ownValueParam != null)
                        {
                            ts.EchoGuard = wb;
                            ownValueParam.SetValue(t, wb);
                        }
                    }
                    catch { }
                }
            }
        }

        void RequestRetryResolve()
        {
            if (Interlocked.Exchange(ref _retryQueued, 1) != 0) return;
            try
            {
                _ui.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    Interlocked.Exchange(ref _retryQueued, 0);
                    try { ResolveAllMachines(); } catch { }
                }));
            }
            catch { Interlocked.Exchange(ref _retryQueued, 0); }
        }

        void StopInertia(TrackState ts)
        {
            ts.Sliding      = false;
            ts.ValueCurrent = ts.ValueTarget;
        }

        void UpdateInertia(TrackState ts, int numSamples)
        {
            bool shouldSend = false;

            if (ts.Sliding)
            {
                ts.ValueCurrent += ts.ValueStep * numSamples;

                bool overshot = (ts.ValueStep > 0f && ts.ValueCurrent >= ts.ValueTarget)
                             || (ts.ValueStep < 0f && ts.ValueCurrent <= ts.ValueTarget);
                if (overshot) StopInertia(ts);

                ts.ValueCurrent = Clamp01(ts.ValueCurrent);
                shouldSend      = true;
            }
            else if (ts.ValueCurrent != ts.LastSent)
            {
                shouldSend = true;
            }

            if (shouldSend && !_initialising)
                SendNow(ts, ts.SlidingFromMidi);
        }

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        // =====================================================================
        // Post-load step (replaces the ReBuzz-era Tick())
        // =====================================================================

        void RunLoadStep()
        {
            if (_initialising)
            {
                _initialising = false;
            }
            ResolveAllMachines();

            // Send feedback from the SAVED ValueCurrent (restored from
            // MachineState) BEFORE SyncFromParamValues can overwrite it.
            int numTracks = host?.Machine?.TrackCount ?? 0;
            for (int t = 0; t < numTracks && t < MAX_TRACKS; t++)
            {
                var ts = _tracks[t];
                foreach (var a in ts.Assignments)
                {
                    if (a.MidiFeedback && a.MidiController >= 0 && a.MidiChannel != 0)
                    {
                        int ccVal = Math.Max(0, Math.Min(127,
                            (int)(ts.ValueCurrent * 127f + 0.5f)));
                        a.LastFeedbackSent = ccVal;
                        MidiOutputHelper.Send(a.MidiController, a.MidiChannel, ccVal);
                    }
                }
            }

            SyncFromParamValues();
        }

        bool AnyUnresolved()
        {
            foreach (var ts in _tracks)
                foreach (var a in ts.Assignments)
                    if (a.ResolvedParam == null && !string.IsNullOrEmpty(a.MachineName))
                        return true;
            return false;
        }

        void SyncFromParamValues()
        {
            try
            {
                var machine = host?.Machine;
                if (machine?.ParameterGroups == null) return;

                IParameterGroup trackGroup = null;
                foreach (var g in machine.ParameterGroups)
                    if (g?.Type == ParameterGroupType.Track) { trackGroup = g; break; }
                if (trackGroup == null) return;

                IParameter valueParam = null;
                foreach (var p in trackGroup.Parameters)
                    if (p.Name == "Value") { valueParam = p; break; }
                if (valueParam == null) return;

                int numTracks = machine.TrackCount;
                for (int t = 0; t < numTracks && t < MAX_TRACKS; t++)
                {
                    int raw = valueParam.GetValue(t);
                    if (raw == valueParam.NoValue) continue;
                    float v = Clamp01(raw / 65534.0f);
                    var ts = _tracks[t];
                    if (Math.Abs(v - ts.ValueCurrent) > 0.00001f || ts.LastSent < 0f)
                    {
                        ts.ValueCurrent = ts.ValueTarget = v;
                        SendNow(ts);
                    }
                }
            }
            catch { }
        }

        // Control-machine Work signature (Buzz managed API): no audio I/O.
        public void Work()
        {
            // Audio thread: no IParameter / song-graph calls in here. Anything
            // that needs them is posted to the UI thread.
            try
            {
                if (_loadPending)
                {
                    _loadPending = false;
                    _ui.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        try { RunLoadStep(); } catch { }
                    }));
                }
                else if (++_retryCounter >= RetryEveryNWorks)
                {
                    _retryCounter = 0;
                    if (AnyUnresolved())
                        RequestRetryResolve();
                }
            }
            catch { /* never let the host's audio thread see an exception */ }

            int n = ElapsedSamples();
            _samplesAccum  += n;
            _updateCounter++;

            // Sub-tick inertia updates (mirrors original MDKWork behaviour).
            // There is no Tick() on Buzz 1503, so Send Freq 0 behaves as 1.
            int every = SendFreq < 1 ? 1 : SendFreq;
            if (_updateCounter >= every)
            {
                for (int t = 0; t < MAX_TRACKS; t++)
                {
                    var ts = _tracks[t];
                    if (ts.Sliding || ts.ValueCurrent != ts.LastSent)
                        UpdateInertia(ts, _samplesAccum);
                }

                _samplesAccum  = 0;
                _updateCounter = 0;
            }
        }

        int ElapsedSamples()
        {
            long now = Stopwatch.GetTimestamp();
            long prev = _lastWorkTimestamp;
            _lastWorkTimestamp = now;
            if (prev == 0) return 0;

            int sps = 44100;
            var mi = host?.MasterInfo;
            if (mi != null && mi.SamplesPerSec > 0) sps = mi.SamplesPerSec;

            double secs = (now - prev) / (double)Stopwatch.Frequency;
            int n = (int)(secs * sps + 0.5);
            return n < 0 ? 0 : (n > MaxSamplesPerWork ? MaxSamplesPerWork : n);
        }

        // =====================================================================
        // MIDI CC input  (mirrors miex::MidiControlChange from original)
        // =====================================================================

        // Fired on every incoming MIDI CC – the settings dialog uses this for MIDI learn.
        // All controllers are reported, including 96/97: those are ordinary
        // CCs for an absolute assignment, and the Inc/Dec protocol's
        // controller number is in the value byte (the dialog decides which).
        public event Action<int, int, int> MidiCCReceived;  // ctrl (0-127), channel (0-based), value

        public void MidiControlChange(int ctrl, int channel, int value)
        {
            MidiCCReceived?.Invoke(ctrl, channel, value);

            int numTracks = host?.Machine?.TrackCount ?? 0;
            for (int t = 0; t < numTracks && t < MAX_TRACKS; t++)
            {
                var ts = _tracks[t];
                bool handled = false;
                foreach (var a in ts.Assignments)
                {
                    if (a.MidiChannel == 0)   continue;
                    if (a.MidiController < 0) continue;

                    bool chanOk = a.MidiChannel == 17
                               || a.MidiChannel == channel + 1;
                    if (!chanOk) continue;

                    if (a.MidiIncDec && (ctrl == 96 || ctrl == 97))
                    {
                        if (value != a.MidiController) continue;

                        float step = MidiIncDecAmount / 65534.0f;
                        float newT = ts.ValueTarget + (ctrl == 96 ? step : -step);

                        if (newT > 1.0f)
                            newT = a.MidiIncDecWrap ? newT - 1.0f : 1.0f;
                        else if (newT < 0.0f)
                            newT = a.MidiIncDecWrap ? newT + 1.0f : 0.0f;

                        ValueChange(ts, t, newT, fromMidi: true);
                        handled = true;
                    }
                    else if (ctrl == a.MidiController)
                    {
                        ValueChange(ts, t, value / 127.0f, fromMidi: true);
                        handled = true;
                    }
                }

                // Feed the new value back into the pattern editor's Value parameter
                // so the slider follows MIDI input visually. Uses the glide
                // TARGET, and is written on the UI thread by Flush().
                if (handled)
                {
                    int pval = (int)(ts.ValueTarget * 65534.0f + 0.5f);
                    pval = Math.Max(0, Math.Min(65534, pval));
                    Interlocked.Exchange(ref ts.WriteBack, pval);
                    ts.PendingFeedback = false;   // never echo MIDI back to the controller
                    ts.Dirty = true;
                    RequestFlush();
                }
            }
        }

        // =====================================================================
        // Context-menu commands
        // =====================================================================

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuEntry(0, "Assignment Settings...", OpenSettings),
            new MenuEntry(1, "About...",               ShowAbout),
        };

        /// <summary>
        /// Called by Buzz when the user clicks a context-menu item,
        /// matching the original C++ CMachineInterface::Command(int i) pattern.
        /// </summary>
        public void Command(int id)
        {
            switch (id)
            {
                case 0: OpenSettings(); break;
                case 1: ShowAbout();    break;
            }
        }

        SettingsWindow _settingsWindow;

        void OpenSettings()
        {
            // If already open, bring it to the front on its own thread
            if (_settingsWindow != null)
            {
                _settingsWindow.Dispatcher.Invoke(() => _settingsWindow.Activate());
                return;
            }

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    _settingsWindow = new SettingsWindow(this);
                    _settingsWindow.Closed += (s, e) => _settingsWindow = null;
                    _settingsWindow.ShowDialog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"{ex.GetType().Name}\n{ex.Message}",
                        "Pedal Peer Control",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    _settingsWindow = null;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        void ShowAbout()
        {
            MessageBox.Show(
                "Pedal Peer Control v1.0.2 (Buzz 1503, 32-bit)\n\n" +
                "Based on BTDSys PeerCtrl\n" +
                "© 2002–2008 Ed Powley (BTDSys)\n\n" +
                "Not affiliated with or endorsed by BTDSys.\n" +
                "https://github.com/thepedal/pedal-peer-control-buzz1503",
                "Pedal Peer Control",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // =====================================================================
        // Helpers exposed to the settings dialog
        // =====================================================================

        public TrackState GetTrack(int i)    => _tracks[i];
        public int        ActiveTrackCount   => host?.Machine?.TrackCount ?? 1;
        public IBuzz      BuzzHost           => Buzz;
    }

    // =========================================================================
    // MenuEntry – derives from DependencyObject so WPF TwoWay bindings on
    // IsChecked etc. work without TypeDescriptor/interface read-only issues.
    // =========================================================================

    sealed class MenuEntry : System.Windows.DependencyObject, IMenuItem,
                             System.ComponentModel.INotifyPropertyChanged
    {
        // --- DependencyProperties (WPF can always bind TwoWay to these) ---

        public static readonly System.Windows.DependencyProperty IsCheckedProperty =
            System.Windows.DependencyProperty.Register(
                "IsChecked", typeof(bool), typeof(MenuEntry),
                new System.Windows.PropertyMetadata(false));

        public static readonly System.Windows.DependencyProperty IsEnabledProperty =
            System.Windows.DependencyProperty.Register(
                "IsEnabled", typeof(bool), typeof(MenuEntry),
                new System.Windows.PropertyMetadata(true));

        // --- Constructor ---

        public MenuEntry(int id, string text, Action action)
        {
            ID      = id;
            Text    = text;
            _action = action;
        }
        readonly Action _action;

        // --- IMenuItem ---

        public int    ID           { get; }
        public string Text         { get; }
        public string GestureText  => null;
        public object CommandParameter => null;

        public bool IsChecked
        {
            get => (bool)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }
        public bool IsEnabled
        {
            get => (bool)GetValue(IsEnabledProperty);
            set => SetValue(IsEnabledProperty, value);
        }

        public bool IsCheckable      { get; set; } = false;
        public bool IsDefault        { get; set; } = false;
        public bool IsSeparator      { get; set; } = false;
        public bool IsLabel          { get; set; } = false;
        public bool StaysOpenOnClick { get; set; } = false;

        public IEnumerable<IMenuItem> Children => null;

        // ICommand – Buzz may call Execute; we also implement Command(int id)
        public System.Windows.Input.ICommand Command =>
            new RelayCommand(() => _action?.Invoke());

        // INotifyPropertyChanged
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged
        { add { } remove { } }
    }

    sealed class RelayCommand : System.Windows.Input.ICommand
    {
        readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter)    => _execute?.Invoke();
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }

    // =========================================================================
    // Static MIDI output helper for feedback
    // =========================================================================

    static class MidiOutputHelper
    {
        // winmm directly instead of NAudio: no dependency on NAudio being
        // present in the Buzz folder, and the machine stays a single DLL.
        [DllImport("winmm.dll")] static extern uint midiOutGetNumDevs();
        [DllImport("winmm.dll")] static extern uint midiOutOpen(out IntPtr handle, uint deviceId,
                                                                IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern uint midiOutShortMsg(IntPtr handle, uint message);
        [DllImport("winmm.dll")] static extern uint midiOutClose(IntPtr handle);
        const uint MMSYSERR_NOERROR = 0;
        const uint CALLBACK_NULL    = 0;

        static readonly object _lock = new object();
        static readonly Dictionary<uint, IntPtr> _outs = new Dictionary<uint, IntPtr>();

        // Devices that refused to open (e.g. already held by Buzz or another
        // app on a single-client driver). Not retried until the device count
        // changes, so a busy port doesn't cost a midiOutOpen on every send.
        static readonly HashSet<uint> _failed = new HashSet<uint>();
        static uint _lastDeviceCount = uint.MaxValue;

        /// <summary>
        /// Send a CC value (0-127) to all MIDI output devices.
        /// channel is 1-based (1-16); 17 = all channels.
        /// </summary>
        public static void Send(int controller, int channel, int ccVal)
        {
            if (controller < 0 || controller > 127 || channel == 0) return;
            ccVal = Math.Max(0, Math.Min(127, ccVal));

            lock (_lock)
            {
                uint numDevices;
                try { numDevices = midiOutGetNumDevs(); } catch { return; }
                if (numDevices != _lastDeviceCount)
                {
                    _lastDeviceCount = numDevices;
                    _failed.Clear();
                }

                for (uint dev = 0; dev < numDevices; dev++)
                {
                    if (_failed.Contains(dev)) continue;
                    try
                    {
                        if (!_outs.TryGetValue(dev, out var h))
                        {
                            if (midiOutOpen(out h, dev, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL) != MMSYSERR_NOERROR)
                            {
                                _failed.Add(dev);
                                continue;
                            }
                            _outs[dev] = h;
                        }

                        bool ok = true;
                        if (channel == 17)
                        {
                            for (int ch = 1; ch <= 16 && ok; ch++)
                                ok = midiOutShortMsg(h, MakeCC(controller, ccVal, ch)) == MMSYSERR_NOERROR;
                        }
                        else
                        {
                            ok = midiOutShortMsg(h, MakeCC(controller, ccVal, channel)) == MMSYSERR_NOERROR;
                        }

                        if (!ok) Drop(dev);   // device probably disconnected
                    }
                    catch
                    {
                        Drop(dev);
                    }
                }
            }
        }

        static void Drop(uint dev)
        {
            if (_outs.TryGetValue(dev, out var dead))
            {
                try { midiOutClose(dead); } catch { }
                _outs.Remove(dev);
            }
        }

        static uint MakeCC(int ctrl, int val, int ch) =>
            (uint)((0xB0 | ((ch - 1) & 0x0F)) | (ctrl << 8) | (val << 16));

        public static void DisposeAll()
        {
            lock (_lock)
            {
                foreach (var h in _outs.Values) try { midiOutClose(h); } catch { }
                _outs.Clear();
                _failed.Clear();
            }
        }
    }
}
