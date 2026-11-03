// Pedal Tracker — managed generator machine for ReBuzz.
// A modern, .NET-based reimagining of the BTDSys Matilde tracker.
//
// One pattern column per voice (Matilde model).  Polyphony is achieved
// by adding tracks.  All sample bit depths and sample rates supported
// by the ReBuzz wavetable are accepted; everything is converted to
// 32-bit float on load and rendered through the interpolator selected
// at the bottom of the global parameters list (or via right-click ▸
// Interpolation Quality).
//
// Build:
//   dotnet build PedalTracker.csproj -c Release
// Output:
//   <BuzzDir>\Gear\Generators\Pedal Tracker.NET.dll
//
// Implemented effect commands:
//   01/02 pitch slides, 03 tone portamento, 04 vibrato,
//   09 sample offset, 0A volume slide, 0C set volume,
//   0E1x/0E2x fine pitch slides, 0E9x retrigger,
//   0EAx/0EBx fine volume slides, 0ECx note cut, 0EDx note delay.

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace WDE.PedalTracker
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Machine declaration
    //  - 1 stereo output, 0 inputs (generator)
    //  - MaxTracks defines the upper bound on pattern columns
    //  - The parameter declaration below establishes the per-track schema
    //  - OutputCount is set to MAX_VOICES + 1 so all possible per-track
    //    outputs exist from machine creation.  This is critical for save/
    //    load: a song saved with connections from "Track N" must have
    //    output slot N available the moment it's reloaded, before any
    //    Work() runs.  ReBuzz only allocates Sample[] buffers for outputs
    //    that have an active connection (MachineWorkInstance.cs:150-160),
    //    so unconnected slots cost nothing.
    // ─────────────────────────────────────────────────────────────────────────
    [MachineDecl(
        Name        = "Pedal Tracker",
        ShortName   = "PdlTrk",
        Author      = "WDE",
        MaxTracks   = MAX_VOICES,
        InputCount  = 0,
        OutputCount = MAX_VOICES + 1)]      // master + one per track
    public class PedalTrackerMachine : IBuzzMachine, INotifyPropertyChanged
    {
        // ── Constants ────────────────────────────────────────────────────────
        public const int MAX_VOICES = 16;        // matches Matilde's track limit
        const   int     MAX_WAVE   = 200;        // ReBuzz wavetable max slot

        // ── Host & cached IBuzz reference ────────────────────────────────────
        readonly IBuzzMachineHost host;
        IBuzz Buzz => host?.Machine?.Graph?.Buzz;

        // ── Voice pool (per-track state) ─────────────────────────────────────
        readonly Voice[] _voices;

        // ── Wave cache: slot -> array of layer snapshots, sorted by RootNote ─
        // Built on UI thread / setters; read on audio thread via reference
        // load (atomic for reference types in .NET).
        readonly ConcurrentDictionary<int, WaveSnapshot[]> _waveCache =
            new ConcurrentDictionary<int, WaveSnapshot[]>();

        // ── Auto-invalidation subscriptions ──────────────────────────────────
        // We hook IWave.PropertyChanged on each cached wave so edits to
        // the wave (loop points, volume, name, etc) clear the cache entry.
        // Re-snapshotting happens lazily on the next access.
        readonly object _subLock = new object();
        readonly Dictionary<int, (IWave wave, PropertyChangedEventHandler handler)> _waveSubs =
            new Dictionary<int, (IWave, PropertyChangedEventHandler)>();

        // ── Multi-track simultaneous-note + dropped-setter workaround ────────
        // (notes §14 + observed reality: ReBuzz's parametersChanged dict is
        //  keyed by parameter, so when several tracks change the same param
        //  on one row only one setter fires.  And occasionally — apparently
        //  more often on song-loop wraps — even single-writer parameters
        //  fail to deliver a setter call at all.  Both cases are recovered
        //  here by polling the raw pvalues dictionary directly: pvalues
        //  contains the value that ReBuzz wrote, before its post-tick
        //  NoValue reset, so we can read what the pattern data was even if
        //  no setter call was made for it.)
        IParameter _ownNoteParam;
        IParameter _ownWaveParam;
        IParameter _ownVolumeParam;
        IParameter _ownCmd0Param;
        IParameter _ownArg0Param;
        IParameter _ownCmd1Param;
        IParameter _ownArg1Param;
        // pvalue readers: track -> raw pvalue (NoValue if unavailable).
        // The backing field changed type between ReBuzz builds — it was a
        // ConcurrentDictionary<int,int> up to ~1818 and is an int[256] in
        // 1827.  GetPValuesField returns a reader that closes over whichever
        // form is present, so the same DLL works on both.
        System.Func<int, int> _ownNotePValues;
        System.Func<int, int> _ownWavePValues;
        System.Func<int, int> _ownVolumePValues;
        System.Func<int, int> _ownCmd0PValues;
        System.Func<int, int> _ownArg0PValues;
        System.Func<int, int> _ownCmd1PValues;
        System.Func<int, int> _ownArg1PValues;

        // ── Per-row gating ───────────────────────────────────────────────────
        // The previous implementation watched MasterInfo.PosInTick wrapping
        // from a high value back to ~0.  That works *most* of the time but
        // fails on song-level loop wraps because pit can already be small
        // when setters fire (e.g. with sub-tick timing on, the host's
        // MasterInfo snapshot may not reflect a clean 0).  Tracking the
        // song's PlayPosition is far more reliable: it advances by 1 per
        // tick and snaps to LoopStart on loop wrap — every row boundary
        // including the loop-wrap one is a change in this value.
        int _prevSongPos = int.MinValue;

        // ── Transport state edge detection ───────────────────────────────────
        // Tracks the previous IBuzz.Playing state so we can react to Play→Stop
        // transitions: when the user hits the Stop button, ReBuzz stops
        // delivering setter calls but keeps invoking Work().  Without
        // detection the voices would just ring out indefinitely.
        bool _wasPlaying;

        // ── Master mute fade state ───────────────────────────────────────────
        // Smooth fade between unmuted (1.0) and muted (0.0) over MuteInertia
        // milliseconds.  Default starts unmuted, no ramp in flight.
        float _muteGain   = 1f;     // current
        float _muteTarget = 1f;     // ramp toward this; 1=unmuted, 0=muted

        // ── Per-track mute fade state ────────────────────────────────────────
        // 16 independent track mutes that affect just their own track's
        // contribution to BOTH the master mix (out 0) and that track's
        // dedicated multi-out channel (out N+1).  These are deliberately
        // independent of the main IMachine.IsMuted / MuteParam — they just
        // gate the render audio for their track, no zero-CPU bypass.
        // Same fade time constant (MuteInertia) as the master mute.
        readonly float[] _trackMuteGain   = AllocOnes();
        readonly float[] _trackMuteTarget = AllocOnes();
        readonly bool[]  _trackMuteParam  = new bool[MAX_VOICES];

        static float[] AllocOnes()
        {
            var a = new float[MAX_VOICES];
            for (int i = 0; i < MAX_VOICES; i++) a[i] = 1f;
            return a;
        }

        // Push a value to one of our own global parameters via ReBuzz's
        // parameter machinery (parameter.SetValue), so the change actually
        // persists in the song.  Writing directly to our auto-property
        // updates the C# field but does NOT update ReBuzz's internal
        // ParameterCore.values dictionary, which is what
        // BMXMLFile.cs:858 reads at save time — so direct writes get
        // dropped on the next save/load cycle.  Used by the right-click
        // menu and any other UI we expose for our own globals.
        void WritebackParameter(string name, int value)
        {
            try
            {
                var groups = host?.Machine?.ParameterGroups;
                if (groups == null || groups.Count < 2) return;
                foreach (var p in groups[1].Parameters)
                {
                    if (p?.Name == name) { p.SetValue(0, value); return; }
                }
            }
            catch { }
        }

        // ── Diagnostics ──────────────────────────────────────────────────────
        long _workCalls;
        long _activeRenders;
        long _setNoteCalls;
        long _setWaveCalls;
        long _triggerSuccess;
        long _triggerFailNoSlot;
        long _triggerFailNoSnap;
        string _lastSnapshotResult = "(none yet)";
        string _lastTriggerResult  = "(none yet)";

        // ── Pitch-effect trace ring (for 01/02 debug only) ──────────────────
        // Lock-free single-writer-per-event-source ring buffer.  Writers are
        // on the audio thread (RunPerTickEffects / Voice.Render / TriggerNote);
        // ShowDiagnostics is the only reader and runs on UI thread.  Memory
        // ordering is loose — strings can race during dump — but the ring
        // never throws, never allocates per write (strings ARE allocated
        // but that's the trade-off for keeping the call sites simple), and
        // gives us a recent-history view of what happened in any voice's
        // pitch state.  Set _pitchTraceEnabled = true (e.g. via menu) to
        // start collecting.
        const int PITCH_TRACE_LEN = 256;
        readonly string[] _pitchTrace = new string[PITCH_TRACE_LEN];
        long _pitchTraceHead;             // monotonic; modulo PITCH_TRACE_LEN gives index
        volatile bool _pitchTraceEnabled;
        void PitchTrace(string s)
        {
            if (!_pitchTraceEnabled) return;
            long h = System.Threading.Interlocked.Increment(ref _pitchTraceHead) - 1;
            _pitchTrace[(int)(h % PITCH_TRACE_LEN)] = s;
        }

        // ── Audio scratch buffers (allocated once) ──────────────────────────
        // One stereo pair per track for multi-out rendering.  Each voice
        // renders into its own scratch pair; the publishing step then sums
        // them into the master output (out 0) and copies each track buffer
        // into its own output (outs 1..N) when that output is connected.
        // 2048 samples is the upper bound for a Work block (ReBuzz clamps
        // samplesToProcess to 256, but we leave headroom in case that
        // changes in a future ReBuzz revision).
        const int SCRATCH_LEN = 2048;
        readonly float[][] _trackL = AllocTrackScratch();
        readonly float[][] _trackR = AllocTrackScratch();

        static float[][] AllocTrackScratch()
        {
            var arr = new float[MAX_VOICES][];
            for (int i = 0; i < MAX_VOICES; i++) arr[i] = new float[SCRATCH_LEN];
            return arr;
        }

        // ── Engine sample-rate snapshot (re-read in Work()) ─────────────────
        double _engineRate = 44100.0;

        // ── Constructor ──────────────────────────────────────────────────────
        public PedalTrackerMachine(IBuzzMachineHost host)
        {
            this.host = host;
            _voices = new Voice[MAX_VOICES];
            for (int i = 0; i < MAX_VOICES; i++) _voices[i] = new Voice();

            // Precompute sinc tables eagerly on the construction thread.
            // This avoids any chance of the audio thread waiting on table
            // construction during the very first Work() call.
            Interpolation.EnsureSinc16();
            Interpolation.EnsureSinc32();

            // Wire the pitch-trace hook so VoiceRender's deferred-trigger
            // fire can log into our ring buffer.  Static hook is the
            // simplest cross-class signal; null-safe and free when
            // _pitchTraceEnabled is false.
            VoiceRender.Trace = PitchTrace;
        }

        // ── Finalizer ────────────────────────────────────────────────────────
        // Unhook every PropertyChanged subscription when the machine is
        // collected.  Without this, deleting and recreating the machine
        // repeatedly leaves dangling delegates pinning IWave instances.
        ~PedalTrackerMachine()
        {
            try { ClearWaveCache(); } catch { }
        }

        // =====================================================================
        // Global parameters
        // =====================================================================

        // Master output gain.  Stored 0..0xFFFE; 0x8000 == unity.
        // Named "Master" rather than "Volume" so it doesn't collide with
        // the per-track Volume parameter in pattern-editor headers.
        [ParameterDecl(
            Name        = "Master",
            Description = "Master output volume (0x8000 = unity)",
            MinValue    = 0,
            MaxValue    = 0xFFFE,
            DefValue    = 0x8000)]
        public int MasterParam { get; set; } = 0x8000;

        // Master mute switch — pattern-automatable.  Mirrors to (and from)
        // the standard ReBuzz per-machine IsMuted flag, so toggling this
        // also toggles the UI mute button and vice-versa.  When mute is
        // active AND the user has 'process muted machines' off in
        // ReBuzz audio settings, ReBuzz skips Work() entirely (zero CPU).
        // The fade is governed by MuteInertia.
        [ParameterDecl(
            Name        = "Mute",
            Description = "Master mute (smooth-fades by MuteInertia)",
            DefValue    = false)]
        public bool MuteParam
        {
            get => _muteParam;
            set
            {
                if (_muteParam == value) return;
                _muteParam = value;
                _muteTarget = value ? 0f : 1f;
                // On song load (before any Work() has run), snap the gain
                // to the target so we don't fade in/out from the previous
                // state.  After audio has started, normal fade behaviour.
                if (_workCalls == 0) _muteGain = _muteTarget;
                // We deliberately do NOT touch host.Machine.IsMuted here.
                // Path 1 design (always render): the machine produces audio
                // continuously, fading the output gain in/out via _muteGain.
                // ReBuzz's per-machine zero-CPU bypass is never engaged from
                // our side — that would freeze voice state during the muted
                // period and break the smooth swap behaviour we need for
                // peer-controlled alternating-mute workflows.
            }
        }
        bool _muteParam = false;

        // Mute fade time, in milliseconds.  0 = instant (hard mute, may
        // click); typical values 5-100ms for clean toggles.  Stored as
        // an integer count of ms so it round-trips cleanly through saves.
        // Shared by both master mute and all per-track mutes.
        [ParameterDecl(
            Name            = "Mute Inertia",
            Description     = "Mute fade time in ms (0 = instant)",
            MinValue        = 0,
            MaxValue        = 500,
            DefValue        = 50,
            ValueDescriptor = Descriptors.Milliseconds)]
        public int MuteInertiaParam { get; set; } = 50;

        // ── Per-track mute switches (16 of them, one per possible track) ─────
        // Always 16 declared regardless of TrackCount.  Switches for tracks
        // that don't currently exist still act as state holders but have no
        // audible effect (no voice activates on a non-existent track).
        // Each switch is independent of IMachine.IsMuted and the master Mute
        // parameter — they just gate that track's audio in the render path.
        // Shared MuteInertia governs all fade times.

        // Centralised setter used by all 16 individual property setters.
        void SetTrackMute(int track, bool value)
        {
            if ((uint)track >= MAX_VOICES) return;
            if (_trackMuteParam[track] == value) return;
            _trackMuteParam[track]  = value;
            _trackMuteTarget[track] = value ? 0f : 1f;
            // On song load (before any Work() has run), snap to target so
            // we don't fade in/out from a stale prior state.
            if (_workCalls == 0) _trackMuteGain[track] = _trackMuteTarget[track];
        }

        [ParameterDecl(Name = "Mute Track 1",  Description = "Mute track 1 (fades by Mute Inertia)",  DefValue = false)]
        public bool MuteT1  { get => _trackMuteParam[0];  set => SetTrackMute(0,  value); }

        [ParameterDecl(Name = "Mute Track 2",  Description = "Mute track 2",  DefValue = false)]
        public bool MuteT2  { get => _trackMuteParam[1];  set => SetTrackMute(1,  value); }

        [ParameterDecl(Name = "Mute Track 3",  Description = "Mute track 3",  DefValue = false)]
        public bool MuteT3  { get => _trackMuteParam[2];  set => SetTrackMute(2,  value); }

        [ParameterDecl(Name = "Mute Track 4",  Description = "Mute track 4",  DefValue = false)]
        public bool MuteT4  { get => _trackMuteParam[3];  set => SetTrackMute(3,  value); }

        [ParameterDecl(Name = "Mute Track 5",  Description = "Mute track 5",  DefValue = false)]
        public bool MuteT5  { get => _trackMuteParam[4];  set => SetTrackMute(4,  value); }

        [ParameterDecl(Name = "Mute Track 6",  Description = "Mute track 6",  DefValue = false)]
        public bool MuteT6  { get => _trackMuteParam[5];  set => SetTrackMute(5,  value); }

        [ParameterDecl(Name = "Mute Track 7",  Description = "Mute track 7",  DefValue = false)]
        public bool MuteT7  { get => _trackMuteParam[6];  set => SetTrackMute(6,  value); }

        [ParameterDecl(Name = "Mute Track 8",  Description = "Mute track 8",  DefValue = false)]
        public bool MuteT8  { get => _trackMuteParam[7];  set => SetTrackMute(7,  value); }

        [ParameterDecl(Name = "Mute Track 9",  Description = "Mute track 9",  DefValue = false)]
        public bool MuteT9  { get => _trackMuteParam[8];  set => SetTrackMute(8,  value); }

        [ParameterDecl(Name = "Mute Track 10", Description = "Mute track 10", DefValue = false)]
        public bool MuteT10 { get => _trackMuteParam[9];  set => SetTrackMute(9,  value); }

        [ParameterDecl(Name = "Mute Track 11", Description = "Mute track 11", DefValue = false)]
        public bool MuteT11 { get => _trackMuteParam[10]; set => SetTrackMute(10, value); }

        [ParameterDecl(Name = "Mute Track 12", Description = "Mute track 12", DefValue = false)]
        public bool MuteT12 { get => _trackMuteParam[11]; set => SetTrackMute(11, value); }

        [ParameterDecl(Name = "Mute Track 13", Description = "Mute track 13", DefValue = false)]
        public bool MuteT13 { get => _trackMuteParam[12]; set => SetTrackMute(12, value); }

        [ParameterDecl(Name = "Mute Track 14", Description = "Mute track 14", DefValue = false)]
        public bool MuteT14 { get => _trackMuteParam[13]; set => SetTrackMute(13, value); }

        [ParameterDecl(Name = "Mute Track 15", Description = "Mute track 15", DefValue = false)]
        public bool MuteT15 { get => _trackMuteParam[14]; set => SetTrackMute(14, value); }

        [ParameterDecl(Name = "Mute Track 16", Description = "Mute track 16", DefValue = false)]
        public bool MuteT16 { get => _trackMuteParam[15]; set => SetTrackMute(15, value); }

        // Interpolation method as a global parameter so it persists with the
        // song.  Also exposed via the right-click ▸ Interpolation Quality menu.
        [ParameterDecl(
            Name              = "Interpolation",
            Description       = "Sample interpolation algorithm",
            ValueDescriptions = new[] { "None", "Linear", "Cubic", "B-Spline", "Sinc 16", "Sinc 32" },
            DefValue          = (int)Interp.Cubic)]
        public int InterpParam { get; set; } = (int)Interp.Cubic;

        // =====================================================================
        // Track parameters (one set per voice / pattern column)
        // =====================================================================
        //
        // ReBuzz infers each parameter's type from the C# parameter type of
        // the *setter*:
        //   Note  → ParameterType.Note   (piano keyboard input)
        //   int   → Byte / Word          (with IsWaveNumber for wave selectors)
        //   bool  → Switch
        //
        // The order in which setters appear here is the order in which
        // ReBuzz lists them in the pattern editor (left to right).

        // 1) Note column
        [ParameterDecl(
            Name        = "Note",
            Description = "Note to play (NOTE-OFF stops the voice)")]
        public void SetNote(Note value, int track)
        {
            if ((uint)track >= MAX_VOICES) return;
            _setNoteCalls++;
            var v = _voices[track];
            v.PendingNote = value.Value;
            v.HasNewNote  = true;

            // Multi-track simultaneous-note workaround AND dropped-setter
            // recovery: poll the raw pvalues dictionary for every track
            // parameter so that any pattern data that didn't come through
            // a setter call is still recovered.  See "Multi-track
            // simultaneous-note + dropped-setter workaround" comment above.
            PollAllPValues(track);
        }

        // 2) Wave selector (byte; IsWaveNumber gives the W## ↑↓ behaviour)
        [ParameterDecl(
            Name         = "Wave",
            Description  = "Wavetable slot to play",
            MinValue     = 0,
            MaxValue     = MAX_WAVE,
            DefValue     = 0,
            IsWaveNumber = true)]
        public void SetWave(int value, int track)
        {
            if ((uint)track >= MAX_VOICES) return;
            // 0 = no change, otherwise wave slot number (1-based)
            if (value == 0 || value == 255) return;
            _setWaveCalls++;
            var v = _voices[track];
            v.PendingWave = (byte)value;
            v.HasNewWave  = true;

            // Pre-snapshot the wave on the calling thread (still safe – the
            // pattern-engine setter call runs before audio Work).  Subsequent
            // audio-thread usage is just a reference read.
            EnsureWaveSnapshots(value);
        }

        // 3) Volume (0..0xFE; 0xFF = no change)
        [ParameterDecl(
            Name        = "Volume",
            Description = "Track volume (0x80 = unity)",
            MinValue    = 0,
            MaxValue    = 0xFE,
            DefValue    = 0x80)]
        public void SetTrackVolume(int value, int track)
        {
            if ((uint)track >= MAX_VOICES) return;
            if (value == 0xFF) return;       // no change
            var v = _voices[track];
            v.PendingVolume = (byte)value;
            v.HasNewVolume  = true;
        }

        // 4) Effect command — first column
        [ParameterDecl(
            Name        = "Cmd",
            Description = "Effect command, first column (Matilde-style)",
            MinValue    = 0,
            MaxValue    = 0xFE,
            DefValue    = 0)]
        public void SetCmd(int value, int track) => StoreCmd(track, 0, value);

        // 5) Effect argument — first column
        [ParameterDecl(
            Name        = "Arg",
            Description = "Argument for first effect command",
            MinValue    = 0,
            MaxValue    = 0xFE,
            DefValue    = 0)]
        public void SetArg(int value, int track) => StoreArg(track, 0, value);

        // 6) Effect command — second column
        [ParameterDecl(
            Name        = "Cmd2",
            Description = "Effect command, second column (runs alongside the first)",
            MinValue    = 0,
            MaxValue    = 0xFE,
            DefValue    = 0)]
        public void SetCmd2(int value, int track) => StoreCmd(track, 1, value);

        // 7) Effect argument — second column
        [ParameterDecl(
            Name        = "Arg2",
            Description = "Argument for second effect command",
            MinValue    = 0,
            MaxValue    = 0xFE,
            DefValue    = 0)]
        public void SetArg2(int value, int track) => StoreArg(track, 1, value);

        void StoreCmd(int track, int slot, int value)
        {
            if ((uint)track >= MAX_VOICES) return;
            if (value == 0xFF) return;
            var v = _voices[track];
            v.PendingCmd[slot] = (byte)value;
            v.HasNewCmd[slot]  = true;
        }

        void StoreArg(int track, int slot, int value)
        {
            if ((uint)track >= MAX_VOICES) return;
            if (value == 0xFF) return;
            var v = _voices[track];
            v.PendingArg[slot] = (byte)value;
            v.HasNewCmd[slot]  = true;
        }

        // =====================================================================
        // DescribeValue — populates the second status-bar panel when a
        // parameter cell is selected in the pattern editor.
        // =====================================================================
        // Channel naming for the connection menu
        // =====================================================================
        // Discovered by ReBuzz via reflection (ManagedMachineHost.cs line 247):
        //   GetChannelNameFunction = GetMethod("GetChannelName");
        // Signature must be exactly: string GetChannelName(bool input, int index)
        // Returning null falls back to a generic "out N" / "in N" label.
        // index 0 → "Master", 1..N → "Track 1".."Track N" so the connect
        // popup is self-explanatory.
        public string GetChannelName(bool input, int index)
        {
            if (input) return null;            // we have no inputs
            if (index == 0) return "Master";
            return "Track " + index;           // 1-based for the user
        }

        // =====================================================================
        // DescribeValue — dynamic per-parameter value text in the status bar
        // =====================================================================
        // Discovered by ReBuzz via reflection (ManagedMachineHost.cs line 255):
        //   DescribeValueFunction = GetMethod(typeof(DescribeValueDelegate),
        //                                     "DescribeValue");
        // Signature must be exactly:  string DescribeValue(IParameter, int)
        // Return null to fall back to the default ParameterDecl ValueDescriptions.
        public string DescribeValue(IParameter parameter, int value)
        {
            if (parameter == null) return null;

            // Match by parameter name — robust against parameter-order changes.
            switch (parameter.Name)
            {
                case "Cmd":
                case "Cmd2":
                    return DescribeCmd(value);

                case "Arg":
                case "Arg2":
                    // Two presentations: hex byte AND, where the upper / lower
                    // nibbles have meaning (vibrato, volume slide), the broken-
                    // out nibbles too.
                    int hi = (value >> 4) & 0xF;
                    int lo =  value       & 0xF;
                    return string.Format("{0:X2}  (hi {1:X} / lo {2:X})", value, hi, lo);

                case "Wave":
                    if (value == 0) return "(no change)";
                    // Try to look up the wave name in the cache for a friendlier
                    // display.  This runs on UI thread (parameter window or
                    // pattern editor selection) so it's safe to do a dictionary
                    // probe.
                    WaveSnapshot[] arr;
                    if (_waveCache.TryGetValue(value, out arr) && arr != null && arr.Length > 0)
                        return string.Format("{0:X2}  {1}", value, arr[0].Name);
                    return string.Format("{0:X2}", value);

                case "Volume":
                    // Track volume — 0x80 = unity, 0x00 = silent.
                    // Matilde-compatible logarithmic scale: amplitude = (v/128)².
                    if (value == 0xFF) return "(no change)";
                    if (value == 0x00) return "00  (silent)";
                    if (value == 0x80) return "80  (unity, 0.0 dB)";
                    double db = 40.0 * System.Math.Log10(value / 128.0);
                    return string.Format("{0:X2}  ({1:+0.0;-0.0;0.0} dB)", value, db);

                case "Master":
                    // Global Master volume — 0x8000 = unity.
                    if (value == 0x8000) return "8000  (unity, 0.0 dB)";
                    if (value == 0)      return "0000  (silent)";
                    double mdb = 20.0 * System.Math.Log10(value / 32768.0);
                    return string.Format("{0:X4}  ({1:+0.0;-0.0;0.0} dB)", value, mdb);

                case "Interpolation":
                    if (value >= 0 && value < Interpolation.InterpNames.Length)
                        return Interpolation.InterpNames[value];
                    return null;
            }

            // Note column and anything else we haven't customized — fall back to
            // ReBuzz's default formatting.
            return null;
        }

        // Map a tracker effect command number to a human-readable label.
        // Pattern: "code  Name" so the hex stays visible alongside the meaning.
        static string DescribeCmd(int cmd)
        {
            switch (cmd)
            {
                case 0x00: return "00  (none)";
                case 0x01: return "01  Slide pitch up";
                case 0x02: return "02  Slide pitch down";
                case 0x03: return "03  Tone portamento";
                case 0x04: return "04  Vibrato";
                case 0x09: return "09  Sample offset";
                case 0x0A: return "0A  Volume slide";
                case 0x0C: return "0C  Set volume";
                case 0x0E: return "0E  Extended (1x/2x fine pitch, Ax/Bx fine vol, " +
                                  "9x retrig, Cx cut, Dx delay)";
                default:
                    return string.Format("{0:X2}  (unassigned)", cmd);
            }
        }

        // =====================================================================
        // Audio – Work() — multi-out generator
        // =====================================================================
        // Multi-out generator signature: bool Work(IList<Sample[]> output, int n, WorkModes mode)
        // ReBuzz auto-detects this overload and sets MULTI_IO on our machine.
        //
        // Output layout:
        //   output[0]      — summed master mix (with master gain applied)
        //   output[1..N]   — per-track stereo (track 0 → out[1], etc.)
        //                    rendered dry; master gain does NOT affect these
        //                    so external chains can use them as clean signal
        //
        // Stereo-vs-mono: each output is always a stereo Sample[] buffer.
        // For mono waves, snap.R is a duplicate of snap.L (set up in
        // WavetableAccess.SnapAll), so a mono voice writes L=R on its
        // track output — effectively centre-panned mono.  Stereo waves
        // write actual L/R content.  No special-casing in this loop.
        //
        // ReBuzz only allocates buffers for connected outputs; output[i] is
        // null otherwise.  We always render every active voice into its own
        // scratch L/R pair, then publish only to the connected outputs at
        // the end of the function.  This keeps render cost independent of
        // routing topology and avoids per-voice connection bookkeeping.
        public bool Work(IList<Sample[]> output, int n, WorkModes mode)
        {
            if (output == null || n <= 0) return false;
            _workCalls++;

            // Sample rate may change at runtime if the user changes audio driver
            int sr = host?.MasterInfo?.SamplesPerSec ?? 0;
            if (sr > 0) _engineRate = sr;

            // Detect a new pattern row.  Song.PlayPosition advances by 1 per
            // tick and snaps to LoopStart on song-loop wrap; any change in
            // its value is a new row.  Far more reliable than watching
            // MasterInfo.PosInTick (whose snapshotted value can vary with
            // sub-tick timing and audio-block sizing).
            int songPos = -1;
            try { songPos = Buzz?.Song?.PlayPosition ?? -1; } catch { }
            bool newRow = songPos != _prevSongPos;

            // Consume any pending parameter changes from setters.  The HasNew*
            // flags are one-shot (set in SetXxx, cleared on consume), so
            // calling this every Work() is harmless — and avoids missing a
            // note if the very first Work() of a play cycle happens not to
            // align with a fresh tick boundary.  When `newRow` is true we
            // also clear stale Active effect state for slots that didn't get
            // a fresh setter call this row.
            ApplyPendingParameters(newRow);

            // Detect transitions of the master Play / Stop control.  When
            // ReBuzz stops the transport we fade out every active voice; the
            // pattern engine has stopped delivering setter calls but Work()
            // keeps being called, so without this, voices ring out forever.
            CheckTransport();

            // Pattern-tick edge → run continuous effect-command processors
            // exactly once per row (slides, volume slide, etc).
            if (newRow) RunPerTickEffects();
            _prevSongPos = songPos;

            // ── 0E sub-tick scheduling ────────────────────────────────────────
            // Every Work() we decrement each voice's per-sample countdowns and
            // fire the corresponding event when one hits zero.  Sample-accurate
            // even when the audio buffer crosses a tick boundary mid-block.
            ProcessSubTickSchedules(n);

            // ── Per-track render ─────────────────────────────────────────────
            // Each voice renders into its own dedicated stereo scratch pair.
            // Render() applies the voice's Volume and the wave's intrinsic
            // volume (snap.Volume) inside `amp`, but we pass master=1.0 so
            // master gain is applied later only to the master mix (out 0)
            // and NOT to the per-track outputs.
            Interp interp = ClampInterp(InterpParam);
            float  master = MasterParam / 32768f;     // 0x8000 → 1.0

            // Reuse persistent scratch when block fits; allocate fresh on
            // the rare oversized block.  Either way every track buffer
            // starts cleared so an inactive voice contributes nothing.
            float[][] tL = _trackL;
            float[][] tR = _trackR;
            if (n > SCRATCH_LEN)
            {
                tL = new float[MAX_VOICES][];
                tR = new float[MAX_VOICES][];
                for (int t = 0; t < MAX_VOICES; t++)
                {
                    tL[t] = new float[n];
                    tR[t] = new float[n];
                }
            }
            else
            {
                for (int t = 0; t < MAX_VOICES; t++)
                {
                    Array.Clear(tL[t], 0, n);
                    Array.Clear(tR[t], 0, n);
                }
            }

            bool anyActive = false;
            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];
                if (!v.Active) continue;
                anyActive = true;
                // Render with master=1.0; we apply MasterParam later to
                // out 0 only.  Track-level Volume column already in v.Volume.
                VoiceRender.Render(v, tL[t], tR[t], n, interp, _engineRate, 1f);
            }

            // ── Master mute fade trajectory for this block ───────────────────
            // Compute the gain at the start of this block and the gain at
            // the end; both publish loops then linearly interpolate per
            // sample.  At MuteInertia = 0 the fade is "instant" (one block,
            // about 5ms — still avoids the worst clicks).  At higher values
            // the fade spans several blocks for a smooth transition.
            int   inertiaMs      = MuteInertiaParam;
            int   inertiaSamples = (inertiaMs <= 0) ? 0
                                 : System.Math.Max(1, (int)((inertiaMs / 1000f) * _engineRate));

            float muteStart = _muteGain;
            float muteEnd   = AdvanceFade(_muteGain, _muteTarget, n, inertiaSamples);
            _muteGain = muteEnd;

            // ── Per-track mute fade trajectories ─────────────────────────────
            // Same logic as master, applied independently to each of the 16
            // tracks.  Stored in stack-allocated spans so the inner sample
            // loop can read them directly.  Trajectories are computed even
            // for inactive tracks so a track that becomes active mid-fade
            // won't snap audibly.
            System.Span<float> tmStart = stackalloc float[MAX_VOICES];
            System.Span<float> tmEnd   = stackalloc float[MAX_VOICES];
            for (int t = 0; t < MAX_VOICES; t++)
            {
                tmStart[t] = _trackMuteGain[t];
                tmEnd[t]   = AdvanceFade(_trackMuteGain[t], _trackMuteTarget[t], n, inertiaSamples);
                _trackMuteGain[t] = tmEnd[t];
            }

            // ── Publish to outputs ───────────────────────────────────────────
            // ReBuzz Sample is ±32768 floats — we render in normalised ±1.0
            // and scale at publish time.  Each track's contribution is gated
            // by its own per-track mute fade; the whole machine is then
            // gated by the master mute fade (out 0 also picks up MasterParam).
            const float OUT_SCALE = 32768f;
            int outCount = output.Count;
            float invN  = (n > 1) ? 1f / (n - 1) : 0f;
            float gainDelta = muteEnd - muteStart;

            // Out 0 — summed master with master gain * master mute fade.
            // Each track inside the sum is gated by its own per-track fade.
            if (outCount > 0 && output[0] != null)
            {
                var dst = output[0];
                int copy = System.Math.Min(n, dst.Length);
                for (int i = 0; i < copy; i++)
                {
                    float u  = i * invN;
                    float mg = muteStart + gainDelta * u;
                    float l = 0f, r = 0f;
                    for (int t = 0; t < MAX_VOICES; t++)
                    {
                        float tg = tmStart[t] + (tmEnd[t] - tmStart[t]) * u;
                        l += tL[t][i] * tg;
                        r += tR[t][i] * tg;
                    }
                    dst[i].L = l * master * mg * OUT_SCALE;
                    dst[i].R = r * master * mg * OUT_SCALE;
                }
            }

            // Outs 1..N — per-track dry (no master gain), gated by both the
            // track's own mute and the master mute.
            int maxTrackOut = System.Math.Min(outCount - 1, MAX_VOICES);
            for (int t = 0; t < maxTrackOut; t++)
            {
                var dst = output[t + 1];
                if (dst == null) continue;     // unconnected
                int copy = System.Math.Min(n, dst.Length);
                float[] sL = tL[t], sR = tR[t];
                float tStart = tmStart[t];
                float tEnd   = tmEnd[t];
                float tDelta = tEnd - tStart;
                for (int i = 0; i < copy; i++)
                {
                    float u  = i * invN;
                    float mg = muteStart + gainDelta * u;
                    float tg = tStart + tDelta * u;
                    dst[i].L = sL[i] * tg * mg * OUT_SCALE;
                    dst[i].R = sR[i] * tg * mg * OUT_SCALE;
                }
            }

            // (Zero-CPU bypass is engaged from the top-of-Work assertion
            // above — once the fade reaches zero, IsMuted goes true and
            // ReBuzz's MachineWorkInstance.cs:118 short-circuits Work() on
            // the next call when 'process muted machines' is false.)

            if (anyActive) _activeRenders++;

            // Returning false when silent lets ReBuzz skip downstream effects.
            // Also report inactive when fully muted, so downstream effects
            // can skip their own work.
            return anyActive && _muteGain > 1e-4f;
        }

        // Advance a fade gain toward its target by one Work block's worth.
        // Returns the gain at the end of the block; the caller stores this
        // back to seed the next block.  When inertiaSamples == 0 the fade
        // snaps to target this block (the publish loop's per-sample lerp
        // still makes it a one-block ramp, killing zero-crossing clicks).
        static float AdvanceFade(float current, float target, int n, int inertiaSamples)
        {
            if (current == target) return current;
            if (inertiaSamples <= 0) return target;
            float step = n / (float)inertiaSamples;
            return target > current
                ? System.Math.Min(target, current + step)
                : System.Math.Max(target, current - step);
        }

        // =====================================================================
        // Pending-parameter application
        // =====================================================================
        //
        // Setters store values into voice.Pending*; this routine consumes
        // them.  Each HasNew* flag is one-shot — set by the setter, cleared
        // here — so calling this every Work() is correct and the natural
        // place to also handle deferred startup work like the wave snapshot.
        // ApplyPendingParameters consumes setter-delivered values into voice
        // state.  When `newRow` is true, the audio cursor has just crossed
        // into a fresh pattern row — at that moment, any effect-column slot
        // that did NOT receive a setter call this row must be cleared.  That
        // matches Matilde semantics: an empty effect cell means "no effect
        // this row", not "carry the previous effect over".
        void ApplyPendingParameters(bool newRow)
        {
            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];

                // ── Wave change (without triggering) ─────────────────────────
                // Matilde semantics: changing the Wave column resets the
                // track volume to unity.  A subsequent Volume column on the
                // same row is then applied on top of that reset.
                if (v.HasNewWave)
                {
                    v.HasNewWave = false;
                    v.ActiveWaveSlot = v.PendingWave;
                    v.Volume = 1f;
                    EnsureWaveSnapshots(v.PendingWave);
                }

                // ── Volume change (immediate) ────────────────────────────────
                // Matilde semantics: pattern volume is squared.  0x80 → 1.0,
                // 0x40 → 0.25, 0xFE → ~3.94.  This makes the column much
                // more useful as a quick volume scaler — small values are
                // audibly quieter than a linear mapping would give.
                if (v.HasNewVolume)
                {
                    v.HasNewVolume = false;
                    float davol    = v.PendingVolume / 128f;
                    v.Volume       = davol * davol;
                }

                // ── Effect commands latched for both columns ─────────────────
                // On a row boundary, slots that didn't receive a fresh setter
                // call get cleared (Matilde behaviour).  Within a row, this
                // branch is skipped so we don't keep re-clearing values that
                // legitimately persist for the duration of the row.
                for (int s = 0; s < Voice.CMD_SLOTS; s++)
                {
                    if (v.HasNewCmd[s])
                    {
                        v.HasNewCmd[s]  = false;
                        v.ActiveCmd[s]  = v.PendingCmd[s];
                        v.ActiveArg[s]  = v.PendingArg[s];
                    }
                    else if (newRow)
                    {
                        // No fresh effect on this row → clear any leftover
                        // continuous effect from the previous row.
                        v.ActiveCmd[s] = 0;
                        v.ActiveArg[s] = 0;
                    }
                }

                // ── Note ─────────────────────────────────────────────────────
                if (v.HasNewNote)
                {
                    v.HasNewNote = false;
                    byte nb = v.PendingNote;
                    if (nb == 0)        { /* no note */ }
                    else if (nb == 0xFF)
                    {
                        // Note-off: trigger envelope fade-out.  The voice
                        // deactivates inside Render() once CurrentGain == 0.
                        v.TargetGain        = 0f;
                        v.HasPendingTrigger = false;
                        v.PendingSnap       = null;
                    }
                    else
                    {
                        TriggerNote(v, nb);
                    }
                }
            }
        }

        // =====================================================================
        // Note triggering
        // =====================================================================
        void TriggerNote(Voice v, byte buzzNote)
        {
            // Use whatever wave slot was last selected on this track (or
            // the last triggered one if the new pattern row didn't change it).
            int slot = v.ActiveWaveSlot;
            if (slot <= 0)
            {
                _triggerFailNoSlot++;
                _lastTriggerResult = string.Format(
                    "FAIL: note {0:X2} on a track with no Wave assigned " +
                    "(put a wave number in the W column)", buzzNote);
                return;
            }

            WaveSnapshot[] layers;
            if (!_waveCache.TryGetValue(slot, out layers) || layers == null || layers.Length == 0)
            {
                layers = EnsureWaveSnapshots(slot);
                if (layers == null || layers.Length == 0)
                {
                    _triggerFailNoSnap++;
                    _lastTriggerResult = string.Format(
                        "FAIL: note {0:X2} for wave slot {1}, snapshot unavailable. " +
                        "Last snapshot result: {2}", buzzNote, slot, _lastSnapshotResult);
                    return;
                }
            }

            int midi = MidiFromBuzzNote(buzzNote);

            // ── 03 tone portamento intercept ─────────────────────────────────
            // When 03 is the active command in either column AND the voice is
            // already playing, the note becomes the slide TARGET — no retrigger,
            // no envelope restart.  03 with no active voice falls through to
            // normal trigger.
            int portaSlot = FindCmdSlot(v, 0x03);
            if (portaSlot >= 0 && v.Active && v.CurrentGain > 1e-3f && v.Snap != null)
            {
                byte portaArg = v.ActiveArg[portaSlot];
                v.PortaTargetMidi = midi;
                if (portaArg > 0)
                    v.PortaSpeedSemis = portaArg / 16.0;
                else if (v.PortaSpeedSemis <= 0)
                    v.PortaSpeedSemis = 1.0 / 16.0;     // sane default
                v.PortaActive = true;
                _triggerSuccess++;
                _lastTriggerResult = string.Format(
                    "OK (portamento): target MIDI {0} speed {1:F3} semi/row",
                    midi, v.PortaSpeedSemis);
                return;
            }

            var snap = WavetableAccess.PickLayerForNote(layers, midi);
            if (snap == null)
            {
                _triggerFailNoSnap++;
                _lastTriggerResult = string.Format(
                    "FAIL: note {0:X2} – no playable layer for slot {1}",
                    buzzNote, slot);
                return;
            }

            // Effect 09xx — sample offset, Matilde semantics: PROPORTIONAL,
            // not absolute.  arg / 0x100 = fraction into the sample (so 0x80
            // is the midpoint, 0x40 is 25 %, etc).  No argument (00) means
            // start at the very end — useful with future E8/01 reverse trigger.
            double startPos = 0;
            int sampleOffsetSlot = FindCmdSlot(v, 0x09);
            if (sampleOffsetSlot >= 0)
            {
                int arg = v.ActiveArg[sampleOffsetSlot];
                if (arg == 0)
                    startPos = snap.Length - 1;
                else
                    startPos = (snap.Length * arg) / 256.0;
                if (startPos >= snap.Length) startPos = snap.Length - 1;
                if (startPos < 0)            startPos = 0;
                v.ActiveCmd[sampleOffsetSlot] = 0;
                v.ActiveArg[sampleOffsetSlot] = 0;
            }

            double newIncrement = VoiceRender.IncrementFor(midi, snap, _engineRate);

            // ── 0EDx note-delay intercept ─────────────────────────────────────
            // Defer the trigger by x ticks.  We schedule via NoteDelaySamples,
            // which Work() decrements every buffer; when it reaches zero the
            // pending-trigger machinery fires the note.
            int noteDelaySlot = FindExtendedSubCmd(v, 0xD0);
            if (noteDelaySlot >= 0)
            {
                int delayTicks = v.ActiveArg[noteDelaySlot] & 0x0F;
                int spt = host?.MasterInfo?.SamplesPerTick ?? 0;
                v.ActiveCmd[noteDelaySlot] = 0;
                v.ActiveArg[noteDelaySlot] = 0;

                if (delayTicks > 0 && spt > 0)
                {
                    v.HasPendingTrigger  = true;
                    v.PendingSnap        = snap;
                    v.PendingMidi        = midi;
                    v.PendingPos         = (int)startPos;
                    v.NoteDelaySamples   = delayTicks * spt;
                    v.TargetGain         = 0f;       // silence any current voice while we wait
                    _triggerSuccess++;
                    _lastTriggerResult = string.Format(
                        "OK (delay {0}t): note {1:X2} wave {2}", delayTicks, buzzNote, slot);
                    return;
                }
                // delayTicks == 0 falls through to immediate trigger.
            }

            // ── 0E9x retrigger setup ──────────────────────────────────────────
            // Latch retrigger period; the actual first trigger still happens
            // immediately via the normal path below, then RetrigCountdown
            // re-fires every x ticks.
            int retrigSlot = FindExtendedSubCmd(v, 0x90);
            if (retrigSlot >= 0)
            {
                int periodTicks = v.ActiveArg[retrigSlot] & 0x0F;
                int spt = host?.MasterInfo?.SamplesPerTick ?? 0;
                if (periodTicks > 0 && spt > 0)
                {
                    v.RetrigPeriodSamples = periodTicks * spt;
                    v.RetrigCountdown     = v.RetrigPeriodSamples;
                    v.RetrigActive        = true;
                }
                v.ActiveCmd[retrigSlot] = 0;
                v.ActiveArg[retrigSlot] = 0;
            }
            else if (FindCmdSlot(v, 0x0E) < 0)
            {
                // No 0E command in either column on this fresh trigger → cancel retrig.
                v.RetrigActive = false;
            }

            // ── Deferred retrigger (anti-click) ───────────────────────────────
            // If the voice is currently sounding, hand the new trigger to the
            // render loop: it will fire once the fade-out reaches zero.  This
            // hides the click that would otherwise come from interrupting an
            // ongoing voice mid-sample.
            if (v.Active && v.CurrentGain > 1e-3f)
            {
                v.HasPendingTrigger = true;
                v.PendingSnap       = snap;
                v.PendingMidi       = midi;
                v.PendingPos        = (int)startPos;
                v.TargetGain        = 0f;       // start fading out
                _triggerSuccess++;
                _lastTriggerResult = string.Format(
                    "OK (deferred): note {0:X2} (MIDI {1}) wave {2} '{3}'",
                    buzzNote, midi, slot, snap.Name);
                PitchTrace($"TRIGdef t={Array.IndexOf(_voices, v)} pendMidi={midi} (existing midi={v.CurrentMidi:F2})");
                return;
            }

            // ── Immediate trigger (voice was inactive or already silent) ──────
            v.Snap          = snap;
            v.LastNoteMidi  = midi;
            v.CurrentMidi   = midi;     // track for portamento / vibrato
            v.Pos           = startPos;
            v.PingPongDir   = 1;
            v.InLoop        = false;
            v.Increment     = newIncrement;
            v.BaseIncrement = newIncrement;
            v.Active        = true;
            v.CurrentGain   = 0f;
            v.TargetGain    = 1f;
            v.HasPendingTrigger = false;
            v.PendingSnap   = null;
            PitchTrace($"TRIGimm t={Array.IndexOf(_voices, v)} midi={midi} inc={newIncrement:F4}");

            // Reset any in-flight portamento on a fresh trigger (sliding to a
            // new note when one was already in progress is uncommon enough
            // that the natural behaviour is to abandon the old slide).
            v.PortaActive = false;
            v.VibratoActive = false;     // 04 must be re-armed on new note

            _triggerSuccess++;
            _lastTriggerResult = string.Format(
                "OK: note {0:X2} (MIDI {1}) wave {2} '{3}' inc={4:F4}{5}",
                buzzNote, midi, slot, snap.Name, v.Increment,
                layers.Length > 1 ? $" (layer root MIDI {snap.RootNote} of {layers.Length})" : "");
        }

        // =====================================================================
        // Transport edge detection — react to ReBuzz Play / Stop transitions
        // =====================================================================
        // ReBuzz stops the pattern engine when the user hits Stop, but our
        // Work() continues being called (the audio graph still runs in case
        // something downstream needs the silence).  Without explicit handling
        // any active voice would keep playing wherever it was.  We watch the
        // master IBuzz.Playing flag and trigger a clean envelope fade-out on
        // every voice when it falls.
        void CheckTransport()
        {
            bool nowPlaying;
            try
            {
                nowPlaying = Buzz?.Playing ?? false;
            }
            catch
            {
                // Don't let a transport-check failure break audio.
                nowPlaying = _wasPlaying;
            }

            if (_wasPlaying && !nowPlaying)
            {
                // Falling edge — the user just hit Stop.  Fade every active
                // voice out via the existing anti-click envelope and clear
                // any scheduled triggers so they don't fire after we've
                // stopped.  Render() deactivates each voice once its gain
                // reaches zero.
                StopAllVoices();
            }
            _wasPlaying = nowPlaying;
        }

        // Used by both the transport-stop handler and the right-click menu's
        // "Reset all voices" command.  Avoids a sample-accurate cut (which
        // would click) — instead it asks the envelope to fade everything out.
        void StopAllVoices()
        {
            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];
                v.TargetGain        = 0f;
                v.HasPendingTrigger = false;
                v.PendingSnap       = null;
                v.NoteDelaySamples  = 0;
                v.NoteCutSamples    = 0;
                v.RetrigActive      = false;
                v.PortaActive       = false;
                v.VibratoActive     = false;
            }
        }

        static int MidiFromBuzzNote(byte b)
        {
            // Buzz: high nibble = octave, low nibble = semitone (1..12).
            int octave = (b >> 4);
            int semi   = (b & 0xF) - 1;
            return octave * 12 + semi;
        }

        /// <summary>
        /// Find the first effect-column slot whose ActiveCmd equals `cmd`.
        /// Returns -1 if no slot matches.  Use this when looking for one-shot
        /// or trigger-aware effects (03, 09, etc).
        /// </summary>
        static int FindCmdSlot(Voice v, byte cmd)
        {
            for (int s = 0; s < Voice.CMD_SLOTS; s++)
                if (v.ActiveCmd[s] == cmd) return s;
            return -1;
        }

        /// <summary>
        /// Find the first effect-column slot holding an `0E` command whose
        /// argument's high nibble matches `subHighNibble` (e.g. 0xD0 for 0EDx).
        /// </summary>
        static int FindExtendedSubCmd(Voice v, byte subHighNibble)
        {
            for (int s = 0; s < Voice.CMD_SLOTS; s++)
                if (v.ActiveCmd[s] == 0x0E &&
                    (v.ActiveArg[s] & 0xF0) == subHighNibble)
                    return s;
            return -1;
        }

        // =====================================================================
        // Per-tick effect-command processor
        // =====================================================================
        // Runs once per pattern row.  Continuous effects (pitch slides, volume
        // slide, vibrato, portamento) apply their delta here.  One-shot effects
        // (09 sample offset, 0C set volume) are handled at trigger time and
        // not seen in continuous form.
        //
        // Convention: ActiveCmd persists between rows until explicitly changed
        // — entering "0000" in the effect column stops the effect.
        //
        //   01xx  Slide pitch up   — xx in 1/16 semitone units (16 = 1 semi/row)
        //   02xx  Slide pitch down — same scale
        //   03xx  Tone portamento  — slide toward target note (set by note column)
        //   04xy  Vibrato          — x speed, y depth (sine LFO around base pitch)
        //   0Axy  Volume slide     — x up rate, y down rate per row (units/64)
        //   0Cxx  Set volume       — one-shot, handled in ApplyPendingParameters
        //
        // The model: CurrentMidi tracks the audible pitch (continuous slides
        // update it).  Each tick we recompute Increment from CurrentMidi using
        // the current snapshot; vibrato then modulates Increment around that
        // base.  When all pitch effects stop, Increment naturally settles back
        // to BaseIncrement.
        //
        // Two-column behaviour: each effect column is dispatched independently
        // through ApplyEffectSlot().  If two columns set the same continuous-
        // state effect (e.g. both 04 vibrato) the second-column write wins
        // because both update the same fields; if they set different effects
        // (e.g. 04 vibrato + 0A volume slide), the effects compose naturally.
        // If two columns both apply 01/02 pitch slides on the same row, the
        // CurrentMidi delta from each accumulates additively.
        void RunPerTickEffects()
        {
            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];
                if (!v.Active || v.Snap == null) continue;

                // Latch effect-presence flags up-front so per-slot processing
                // can use them without re-scanning the slot array each time.
                bool any03 = FindCmdSlot(v, 0x03) >= 0;
                bool any04 = FindCmdSlot(v, 0x04) >= 0;

                bool pitchChanged = false;
                for (int s = 0; s < Voice.CMD_SLOTS; s++)
                    pitchChanged |= ApplyEffectSlot(v, s, any03);

                // ── Vibrato auto-cancel when 04 missing in BOTH columns ───────
                if (!any04) v.VibratoActive = false;
                // ── Portamento auto-cancel when 03 missing in BOTH columns ────
                if (!any03) v.PortaActive = false;

                // ── Recompute base Increment if pitch changed ─────────────────
                if (pitchChanged)
                {
                    v.BaseIncrement = VoiceRender.IncrementFor(v.CurrentMidi, v.Snap, _engineRate);
                    v.Increment     = v.BaseIncrement;        // vibrato re-applied below
                }

                // ── Vibrato modulation (tick-rate LFO) ────────────────────────
                if (v.VibratoActive && v.VibratoDepthCents > 0)
                {
                    v.VibratoPhase += v.VibratoSpeed;
                    if (v.VibratoPhase >= 1.0) v.VibratoPhase -= 1.0;

                    // Sine LFO swung over ±VibratoDepthCents.
                    double cents = v.VibratoDepthCents *
                                   System.Math.Sin(v.VibratoPhase * 2.0 * System.Math.PI);
                    double mul = System.Math.Pow(2.0, cents / 1200.0);
                    v.Increment = v.BaseIncrement * mul;
                }
                else if (!pitchChanged && !v.PortaActive && v.BaseIncrement > 0)
                {
                    // Vibrato just stopped — snap Increment back to base.
                    v.Increment = v.BaseIncrement;
                }
            }
        }

        /// <summary>
        /// Apply one slot's effect command for the current row.
        /// Returns true if CurrentMidi was modified (caller recomputes the
        /// base increment after both slots have run).
        /// `anyPortaActive` tells us whether *any* column has a 03 — used to
        /// avoid the portamento branch firing twice if both columns set 03,
        /// since portamento only needs to advance once per row.
        /// </summary>
        bool ApplyEffectSlot(Voice v, int slot, bool anyPortaActive)
        {
            byte cmd = v.ActiveCmd[slot];
            byte arg = v.ActiveArg[slot];
            if (cmd == 0) return false;

            bool pitchChanged = false;

            switch (cmd)
            {
                case 0x01:   // pitch slide up (continuous)
                    if (arg > 0)
                    {
                        double before = v.CurrentMidi;
                        v.CurrentMidi += arg / 16.0;
                        pitchChanged = true;
                        PitchTrace($"01 t={Array.IndexOf(_voices, v)} arg={arg:X2} midi {before:F2}->{v.CurrentMidi:F2} active={v.Active} defer={v.HasPendingTrigger}");
                    }
                    break;

                case 0x02:   // pitch slide down (continuous)
                    if (arg > 0)
                    {
                        double before = v.CurrentMidi;
                        v.CurrentMidi -= arg / 16.0;
                        pitchChanged = true;
                        PitchTrace($"02 t={Array.IndexOf(_voices, v)} arg={arg:X2} midi {before:F2}->{v.CurrentMidi:F2} active={v.Active} defer={v.HasPendingTrigger}");
                    }
                    break;

                case 0x03:   // tone portamento — only advance once per row
                    if (slot == 0 || !anyPortaActive)   // avoid double-advance if 03 in both
                    {
                        if (v.PortaActive)
                        {
                            if (arg > 0) v.PortaSpeedSemis = arg / 16.0;
                            double diff = v.PortaTargetMidi - v.CurrentMidi;
                            if (System.Math.Abs(diff) <= v.PortaSpeedSemis)
                            {
                                v.CurrentMidi = v.PortaTargetMidi;
                                v.PortaActive = false;
                            }
                            else
                            {
                                v.CurrentMidi += System.Math.Sign(diff) * v.PortaSpeedSemis;
                            }
                            pitchChanged = true;
                        }
                    }
                    break;

                case 0x04:   // vibrato setup
                    {
                        int speedNib = (arg >> 4) & 0xF;
                        int depthNib =  arg       & 0xF;
                        if (speedNib > 0) v.VibratoSpeed      = speedNib / 64.0;
                        if (depthNib > 0) v.VibratoDepthCents = depthNib * 12.5;
                        v.VibratoActive = true;
                    }
                    break;

                case 0x0A:   // volume slide
                    if (arg != 0)
                    {
                        int up   = (arg >> 4) & 0xF;
                        int down =  arg       & 0xF;
                        if (up > 0)        v.Volume += up   / 64f;
                        else if (down > 0) v.Volume -= down / 64f;
                        if (v.Volume < 0f) v.Volume = 0f;
                        if (v.Volume > 2f) v.Volume = 2f;
                    }
                    break;

                case 0x0C:   // set volume (one-shot, consume)
                    {
                        // Match the same logarithmic curve as the Volume column.
                        float davol = arg / 128f;
                        v.Volume = davol * davol;
                    }
                    v.ActiveCmd[slot] = 0;
                    v.ActiveArg[slot] = 0;
                    break;

                case 0x0E:   // extended commands
                    {
                        int hi = (arg >> 4) & 0xF;
                        int lo =  arg       & 0xF;
                        bool consume = false;

                        switch (hi)
                        {
                            case 0x1:   // fine pitch slide up
                                if (lo > 0) { v.CurrentMidi += lo / 256.0; pitchChanged = true; }
                                consume = true;
                                break;
                            case 0x2:   // fine pitch slide down
                                if (lo > 0) { v.CurrentMidi -= lo / 256.0; pitchChanged = true; }
                                consume = true;
                                break;
                            case 0xA:   // fine volume slide up
                                v.Volume += lo / 64f;
                                if (v.Volume > 2f) v.Volume = 2f;
                                consume = true;
                                break;
                            case 0xB:   // fine volume slide down
                                v.Volume -= lo / 64f;
                                if (v.Volume < 0f) v.Volume = 0f;
                                consume = true;
                                break;
                            case 0xC:   // note cut after x ticks
                                {
                                    int spt = host?.MasterInfo?.SamplesPerTick ?? 0;
                                    if (lo > 0 && spt > 0)
                                        v.NoteCutSamples = lo * spt;
                                    else
                                        v.TargetGain = 0f;
                                }
                                consume = true;
                                break;
                        }
                        if (consume) { v.ActiveCmd[slot] = 0; v.ActiveArg[slot] = 0; }
                    }
                    break;
            }

            return pitchChanged;
        }

        // =====================================================================
        // Per-block sub-tick scheduler (0E delay / cut / retrig)
        // =====================================================================
        // Runs once per Work() call.  Each voice has up to three independent
        // sample countdowns; we subtract `n` from each and act when they hit
        // zero.  The granularity is one buffer (typically 256–1024 samples),
        // which is finer than per-tick (often 1500–6000 samples) and therefore
        // sample-accurate enough that all 0E timing feels right.
        //
        // We don't try to handle the "fire mid-buffer" case — for retrig,
        // delay and cut the audible effect of being early/late by one buffer
        // (≈5 ms) is below the perception threshold for tracker-style timing.
        void ProcessSubTickSchedules(int n)
        {
            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];

                // ── 0EDx note delay → fire the pending trigger ────────────────
                if (v.NoteDelaySamples > 0)
                {
                    v.NoteDelaySamples -= n;
                    if (v.NoteDelaySamples <= 0)
                    {
                        v.NoteDelaySamples = 0;
                        // Trip the existing pending-trigger machinery.  If
                        // CurrentGain is already at 0, Render() fires inline
                        // on the very next sample.  If a previous voice was
                        // still ramping down, that fade completes first.
                        if (v.HasPendingTrigger && v.PendingSnap != null)
                        {
                            // Force fade-out completion to fire the trigger now.
                            v.CurrentGain = 0f;
                            v.TargetGain  = 0f;
                            v.Active      = true;     // ensure Render visits us
                        }
                    }
                }

                // ── 0ECx note cut → fade voice out ────────────────────────────
                if (v.NoteCutSamples > 0)
                {
                    v.NoteCutSamples -= n;
                    if (v.NoteCutSamples <= 0)
                    {
                        v.NoteCutSamples = 0;
                        v.TargetGain     = 0f;        // envelope handles the click-free fade
                    }
                }

                // ── 0E9x retrigger → re-fire periodically ─────────────────────
                if (v.RetrigActive && v.RetrigPeriodSamples > 0 && v.Active)
                {
                    v.RetrigCountdown -= n;
                    if (v.RetrigCountdown <= 0)
                    {
                        v.RetrigCountdown = v.RetrigPeriodSamples;
                        // Stage a retrigger of the currently-playing snapshot
                        // back at frame 0.  The existing pending-trigger plus
                        // anti-click envelope fade does the rest.
                        if (v.Snap != null)
                        {
                            v.HasPendingTrigger = true;
                            v.PendingSnap       = v.Snap;
                            v.PendingMidi       = v.LastNoteMidi;
                            v.PendingPos        = 0;
                            v.TargetGain        = 0f;
                        }
                    }
                }
            }
        }

        static Interp ClampInterp(int i)
        {
            if (i < 0) i = 0;
            if (i > (int)Interp.Sinc32) i = (int)Interp.Sinc32;
            return (Interp)i;
        }

        // =====================================================================
        // Wave snapshot management
        // =====================================================================
        //
        // ReBuzz waves can be edited or replaced from the wavetable UI at
        // any time.  We don't currently invalidate – the user gets the
        // snapshot that was current at first use.  A future improvement
        // is to subscribe to Wavetable change events and re-snap on edit.

        WaveSnapshot[] EnsureWaveSnapshots(int slot)
        {
            if (slot <= 0) return null;
            WaveSnapshot[] s;
            if (_waveCache.TryGetValue(slot, out s) && s != null) return s;

            s = WavetableAccess.SnapAll(Buzz, slot);
            if (s != null && s.Length > 0)
            {
                _waveCache[slot] = s;

                // Build a friendly multi-layer description for diagnostics.
                if (s.Length == 1)
                {
                    var l = s[0];
                    _lastSnapshotResult = string.Format(
                        "OK slot {0}: '{1}' {2}ch {3}Hz, {4} frames, root MIDI {5}",
                        slot, l.Name, l.ChannelCount, l.SampleRate, l.Length, l.RootNote);
                }
                else
                {
                    var roots = new string[s.Length];
                    for (int i = 0; i < s.Length; i++) roots[i] = s[i].RootNote.ToString();
                    _lastSnapshotResult = string.Format(
                        "OK slot {0}: '{1}' {2} layers, roots [{3}]",
                        slot, s[0].Name, s.Length, string.Join(", ", roots));
                }

                // Hook PropertyChanged for live invalidation (one-time per slot).
                SubscribeForInvalidation(slot);
            }
            else
            {
                _lastSnapshotResult = string.Format(
                    "FAIL slot {0}: SnapAll returned no layers - " +
                    "wavetable empty or accessor names don't match this ReBuzz build",
                    slot);
            }
            return s;
        }

        /// <summary>
        /// Subscribe to IWave.PropertyChanged so edits to the wave (loop
        /// points, volume, etc.) automatically invalidate the cached
        /// snapshot.  Idempotent — safe to call repeatedly.
        /// </summary>
        void SubscribeForInvalidation(int slot)
        {
            lock (_subLock)
            {
                if (_waveSubs.ContainsKey(slot)) return;     // already subscribed

                var wave = WavetableAccess.ResolveWave(Buzz, slot);
                if (wave == null) return;

                PropertyChangedEventHandler handler = (s, e) =>
                {
                    // Any property change triggers a re-snapshot on next access.
                    _waveCache.TryRemove(slot, out _);
                };

                wave.PropertyChanged += handler;
                _waveSubs[slot] = (wave, handler);
            }
        }

        /// <summary>Unsubscribe a single slot (e.g. on Reload command).</summary>
        void UnsubscribeSlot(int slot)
        {
            lock (_subLock)
            {
                if (!_waveSubs.TryGetValue(slot, out var sub)) return;
                try { sub.wave.PropertyChanged -= sub.handler; } catch { }
                _waveSubs.Remove(slot);
            }
        }

        /// <summary>Clear all cached snapshots and unhook every subscription.</summary>
        void ClearWaveCache()
        {
            lock (_subLock)
            {
                foreach (var kv in _waveSubs)
                {
                    try { kv.Value.wave.PropertyChanged -= kv.Value.handler; } catch { }
                }
                _waveSubs.Clear();
            }
            _waveCache.Clear();
        }

        // =====================================================================
        // Multi-track simultaneous-note workaround
        // =====================================================================
        // Re-reads the raw pvalues for our own Note parameter on every
        // SetNote call.  When two or more pattern tracks fire a note on
        // the same row the engine only delivers SetNote() for one of them;
        // the others are still visible in pvalues (the post-tick pvalue
        // reset hasn't run yet).  Recover them and mark them pending.
        // See ReBuzz_ManagedMachine_Notes §14.

        // Poll the raw pvalues dictionaries for every track parameter and
        // recover any pattern-row data whose setter wasn't delivered.  This
        // is called from inside SetNote — at that moment pvalues still holds
        // each parameter's just-written value (the post-tick NoValue reset
        // hasn't run yet).  Every pattern-row cell that's non-empty leaves
        // a non-NoValue residue in pvalues even if no setter call fired.
        void PollAllPValues(int firedTrack)
        {
            if (!EnsurePollFields()) return;

            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];

                // Note (also for the originally-fired track, since the
                // pattern delivers Note via setter directly anyway and this
                // is a no-op — only the *other* tracks may need recovery).
                int pv;
                if (t != firedTrack && _ownNotePValues != null &&
                    (pv = _ownNotePValues(t)) != _ownNoteParam.NoValue && pv != 0)
                {
                    v.PendingNote = (byte)pv;
                    v.HasNewNote  = true;
                }

                // Wave
                if (_ownWavePValues != null &&
                    (pv = _ownWavePValues(t)) != _ownWaveParam.NoValue && pv != 0 && !v.HasNewWave)
                {
                    v.PendingWave = (byte)pv;
                    v.HasNewWave  = true;
                    EnsureWaveSnapshots(pv);
                }

                // Volume
                if (_ownVolumePValues != null &&
                    (pv = _ownVolumePValues(t)) != _ownVolumeParam.NoValue && pv != 0xFF && !v.HasNewVolume)
                {
                    v.PendingVolume = (byte)pv;
                    v.HasNewVolume  = true;
                }

                // Cmd / Arg slot 0
                if (_ownCmd0PValues != null &&
                    (pv = _ownCmd0PValues(t)) != _ownCmd0Param.NoValue && pv != 0xFF && !v.HasNewCmd[0])
                {
                    v.PendingCmd[0] = (byte)pv;
                    v.HasNewCmd[0]  = true;
                }
                if (_ownArg0PValues != null &&
                    (pv = _ownArg0PValues(t)) != _ownArg0Param.NoValue && pv != 0xFF)
                {
                    v.PendingArg[0] = (byte)pv;
                    v.HasNewCmd[0]  = true;     // arg implies a cmd row
                }

                // Cmd / Arg slot 1
                if (_ownCmd1PValues != null &&
                    (pv = _ownCmd1PValues(t)) != _ownCmd1Param.NoValue && pv != 0xFF && !v.HasNewCmd[1])
                {
                    v.PendingCmd[1] = (byte)pv;
                    v.HasNewCmd[1]  = true;
                }
                if (_ownArg1PValues != null &&
                    (pv = _ownArg1PValues(t)) != _ownArg1Param.NoValue && pv != 0xFF)
                {
                    v.PendingArg[1] = (byte)pv;
                    v.HasNewCmd[1]  = true;
                }
            }
        }

        bool EnsurePollFields()
        {
            try
            {
                if (_ownNoteParam == null)
                {
                    var pg = host?.Machine?.ParameterGroups;
                    if (pg == null || pg.Count < 3) return false;
                    var pars = pg[2].Parameters;
                    _ownNoteParam   = FindParam(pars, "Note");
                    _ownWaveParam   = FindParam(pars, "Wave");
                    _ownVolumeParam = FindParam(pars, "Volume");
                    _ownCmd0Param   = FindParam(pars, "Cmd");
                    _ownArg0Param   = FindParam(pars, "Arg");
                    _ownCmd1Param   = FindParam(pars, "Cmd2");
                    _ownArg1Param   = FindParam(pars, "Arg2");
                    if (_ownNoteParam == null) return false;
                }

                _ownNotePValues   = _ownNotePValues   ?? GetPValuesField(_ownNoteParam);
                _ownWavePValues   = _ownWavePValues   ?? GetPValuesField(_ownWaveParam);
                _ownVolumePValues = _ownVolumePValues ?? GetPValuesField(_ownVolumeParam);
                _ownCmd0PValues   = _ownCmd0PValues   ?? GetPValuesField(_ownCmd0Param);
                _ownArg0PValues   = _ownArg0PValues   ?? GetPValuesField(_ownArg0Param);
                _ownCmd1PValues   = _ownCmd1PValues   ?? GetPValuesField(_ownCmd1Param);
                _ownArg1PValues   = _ownArg1PValues   ?? GetPValuesField(_ownArg1Param);
                return _ownNotePValues != null;
            }
            catch { return false; }
        }

        static IParameter FindParam(System.Collections.Generic.IEnumerable<IParameter> pars, string name)
        {
            foreach (var p in pars) if (p?.Name == name) return p;
            return null;
        }

        // Build a reader (track -> raw pvalue) for a parameter's private
        // `pvalues` field, tolerating either backing representation:
        //   • int[]                         (ReBuzz 1827+)
        //   • ConcurrentDictionary<int,int> (ReBuzz up to ~1818)
        // Returns null if the field can't be found at all.  The reader
        // returns the parameter's NoValue for out-of-range / absent tracks
        // so callers can treat "no data" uniformly.
        static System.Func<int, int> GetPValuesField(IParameter p)
        {
            if (p == null) return null;
            try
            {
                var fi = p.GetType().GetField("pvalues",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi == null) return null;
                object raw = fi.GetValue(p);
                if (raw == null) return null;

                int noValue = p.NoValue;

                if (raw is int[] arr)
                {
                    return track =>
                        ((uint)track < (uint)arr.Length) ? arr[track] : noValue;
                }

                if (raw is ConcurrentDictionary<int, int> dict)
                {
                    return track =>
                        dict.TryGetValue(track, out int v) ? v : noValue;
                }

                // Some other dictionary shape — try the generic IDictionary
                // path as a last resort (covers Dictionary<int,int> too).
                if (raw is System.Collections.IDictionary idict)
                {
                    return track =>
                        idict.Contains(track) ? (int)idict[track] : noValue;
                }

                return null;
            }
            catch { return null; }
        }

        // =====================================================================
        // Right-click context menu
        // =====================================================================
        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                var items = new List<IMenuItem>();

                items.Add(new MenuEntry(0, "Diagnostics...",         ShowDiagnostics));
                items.Add(new MenuEntry(1, "Inspect wavetable API",  ShowApiInspection));
                items.Add(new MenuEntry(2,
                    _pitchTraceEnabled
                        ? "Pitch trace: ON (click to disable + dump)"
                        : "Pitch trace: OFF (click to enable)",
                    TogglePitchTrace));
                items.Add(new MenuEntry(2, "Test trigger voice 0",   TestTrigger));
                items.Add(new MenuEntry(3, "Force snapshot wave 1",  () => ForceSnapshot(1)));
                items.Add(new MenuEntry(4, "Reset all voices",
                    () => { foreach (var v in _voices) v.Reset(); }));
                items.Add(new MenuEntry(5, "Reload wavetable cache",
                    () => { ClearWaveCache();
                            Buzz?.DCWriteLine("[PedalTracker] wave cache cleared"); }));

                // ── Import-from-Matilde entries ──────────────────────────────
                // One entry per Matilde Tracker instance currently in the song.
                // Selecting one wipes this Pedal Tracker's patterns and replaces
                // them with translated copies of the chosen Matilde's patterns.
                int matildeId = 50;
                try
                {
                    var matildes = Buzz?.Song?.Machines
                        ?.Where(m => m != null
                                  && m.DLL?.Name != null
                                  && m.DLL.Name.IndexOf("Matilde",
                                        StringComparison.OrdinalIgnoreCase) >= 0)
                        ?.ToList();

                    if (matildes != null && matildes.Count > 0)
                    {
                        foreach (var src in matildes)
                        {
                            var captured = src;     // capture for closure
                            items.Add(new MenuEntry(matildeId++,
                                $"Import patterns from \"{src.Name}\"  (REPLACES existing)",
                                () => RunMatildeImport(captured)));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Buzz?.DCWriteLine($"[PedalTracker] import-menu build error: {ex.Message}");
                }

                // Interpolation submenu (flat for now; can be nested later).
                // Writes go through ReBuzz's parameter machinery so the
                // change persists in the saved song.  See WritebackParameter.
                for (int i = 0; i < Interpolation.InterpNames.Length; i++)
                {
                    int captured = i;
                    items.Add(new MenuEntry(100 + i,
                        $"Interpolation: {Interpolation.InterpNames[i]}",
                        () => WritebackParameter("Interpolation", captured))
                    {
                        IsCheckable = true,
                        IsChecked   = (InterpParam == captured),
                    });
                }
                return items;
            }
        }

        // Dump everything we know about the current state to the debug
        // console.  Open it in ReBuzz via the Debug Console menu / command.
        void ShowDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Pedal Tracker diagnostics ===");
            sb.AppendFormat("Engine sample rate     : {0} Hz\n", (int)_engineRate);
            sb.AppendFormat("Work() calls           : {0}\n",   _workCalls);
            sb.AppendFormat("Active-render Work()s  : {0}\n",   _activeRenders);
            sb.AppendFormat("SetNote calls received : {0}\n",   _setNoteCalls);
            sb.AppendFormat("SetWave calls received : {0}\n",   _setWaveCalls);
            sb.AppendFormat("Triggers OK            : {0}\n",   _triggerSuccess);
            sb.AppendFormat("Triggers fail (no slot): {0}\n",   _triggerFailNoSlot);
            sb.AppendFormat("Triggers fail (no snap): {0}\n",   _triggerFailNoSnap);
            sb.AppendFormat("Last snapshot result   : {0}\n",   _lastSnapshotResult);
            sb.AppendFormat("WavetableAccess error  : {0}\n",   WavetableAccess.LastError);
            sb.AppendFormat("Last trigger result    : {0}\n",   _lastTriggerResult);
            sb.AppendFormat("Wave cache slots       : {0}\n",   _waveCache.Count);
            foreach (var kv in _waveCache)
            {
                var arr = kv.Value;
                if (arr == null || arr.Length == 0) continue;
                sb.AppendFormat("  slot {0,3}: '{1}' {2} layer(s)\n",
                                kv.Key, arr[0].Name, arr.Length);
                for (int i = 0; i < arr.Length; i++)
                {
                    var s = arr[i];
                    sb.AppendFormat("           [{0}] {1}ch {2} Hz, {3} frames, " +
                                    "root MIDI {4}, loop [{5}..{6}] {7}\n",
                                    i, s.ChannelCount, s.SampleRate,
                                    s.Length, s.RootNote, s.LoopStart, s.LoopEnd,
                                    s.Looped ? (s.PingPong ? "(ping-pong)" : "(forward)") : "");
                }
            }
            sb.AppendLine("Voice state:");
            for (int t = 0; t < MAX_VOICES; t++)
            {
                var v = _voices[t];
                if (!v.Active && v.ActiveWaveSlot == 0 && !v.HasNewNote && !v.HasNewWave) continue;
                sb.AppendFormat("  v{0,2}: active={1} slot={2} pos={3:F1} inc={4:F4} " +
                                "vol={5:F2} gain={6:F2}->{7:F2}{8} pendNote={9:X2}/{10} pendWave={11}/{12}\n",
                                t, v.Active, v.ActiveWaveSlot, v.Pos, v.Increment, v.Volume,
                                v.CurrentGain, v.TargetGain,
                                v.HasPendingTrigger ? " (defer)" : "",
                                v.PendingNote, v.HasNewNote, v.PendingWave, v.HasNewWave);
            }

            string text = sb.ToString();
            try
            {
                var b = Buzz;
                if (b != null)
                    foreach (var line in text.Split('\n'))
                        b.DCWriteLine(line.TrimEnd('\r'));
            }
            catch { }
        }

        // Toggle the 01/02 pitch-effect trace.  When enabled, every
        // ApplyEffectSlot 01/02, every TriggerNote (immediate + deferred),
        // and every deferred-trigger fire writes a line to a ring buffer.
        // When disabled (after collecting), the ring is dumped to the
        // debug console so the user can see the exact sequence that
        // produced their test note's pitch.
        void TogglePitchTrace()
        {
            if (!_pitchTraceEnabled)
            {
                // Enable: clear ring and start recording.
                for (int i = 0; i < PITCH_TRACE_LEN; i++) _pitchTrace[i] = null;
                _pitchTraceHead = 0;
                _pitchTraceEnabled = true;
                try { Buzz?.DCWriteLine("[PedalTracker] pitch trace ON — play test pattern then click menu again to dump"); }
                catch { }
                return;
            }

            // Disable: dump.
            _pitchTraceEnabled = false;
            try
            {
                Buzz?.DCWriteLine("[PedalTracker] pitch trace OFF — dumping:");
                long head = System.Threading.Interlocked.Read(ref _pitchTraceHead);
                long start = System.Math.Max(0, head - PITCH_TRACE_LEN);
                int count = 0;
                for (long i = start; i < head; i++)
                {
                    var s = _pitchTrace[(int)(i % PITCH_TRACE_LEN)];
                    if (s == null) continue;
                    Buzz?.DCWriteLine($"  [{i,6}] {s}");
                    count++;
                }
                Buzz?.DCWriteLine($"[PedalTracker] {count} trace lines ({head} total events; ring holds last {PITCH_TRACE_LEN})");
            }
            catch { }
        }

        // Force a snapshot of a wave slot, bypassing the pattern engine —
        // useful to confirm WavetableAccess.SnapAll() works at all on this build.
        void ForceSnapshot(int slot)
        {
            UnsubscribeSlot(slot);
            _waveCache.TryRemove(slot, out _);
            var s = EnsureWaveSnapshots(slot);
            try
            {
                Buzz?.DCWriteLine(s != null && s.Length > 0
                    ? string.Format("[PedalTracker] forced snapshot OK: {0}", _lastSnapshotResult)
                    : string.Format("[PedalTracker] forced snapshot FAILED: {0} | LastError: {1}",
                        _lastSnapshotResult, WavetableAccess.LastError));
            }
            catch { }
        }

        // Run the Matilde-to-Pedal-Tracker pattern importer.  Source is a
        // Matilde Tracker instance from the song; we are the destination.
        // Output is logged to Buzz's debug console.
        void RunMatildeImport(IMachine source)
        {
            if (source == null)
            {
                Buzz?.DCWriteLine("[PedalTracker] import: source machine is null");
                return;
            }
            string summary;
            try
            {
                summary = MatildeImport.Run(Buzz, source, host?.Machine);
            }
            catch (Exception ex)
            {
                Buzz?.DCWriteLine(
                    $"[PedalTracker] import FAILED: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // DCWriteLine truncates multi-line strings — split.
            foreach (var line in summary.Split('\n'))
            {
                var t = line.TrimEnd('\r');
                if (t.Length > 0) Buzz?.DCWriteLine(t);
            }

            // After import, our wave cache should still be valid (slots
            // didn't change), but force a clear just in case sample-offset
            // effects reference slots we haven't pre-snapshotted.
            ClearWaveCache();
        }

        // Dumps the actual properties / values of the runtime IWavetable /
        // IWave / IWaveLayer instances to the debug console.  Now that we
        // know the API, this is mainly useful as a quick sanity check.
        void ShowApiInspection()
        {
            string text;
            try { text = WavetableAccess.InspectApi(Buzz); }
            catch (Exception ex) { text = "InspectApi threw " + ex.GetType().Name + ": " + ex.Message; }
            try
            {
                var b = Buzz;
                if (b != null)
                    foreach (var line in text.Split('\n'))
                        b.DCWriteLine(line.TrimEnd('\r'));
            }
            catch { }
        }

        // Bypass the pattern engine entirely.  Plays the first available
        // wave on voice 0 at MIDI 60 (C-4).  If this produces sound, the
        // render path is fine and the pattern-data path is the issue.
        // If this is silent, the issue is in wave loading or rendering.
        void TestTrigger()
        {
            // Find a usable wave slot — first one already cached, else slot 1
            int slot = 0;
            foreach (var kv in _waveCache)
            {
                if (kv.Value != null && kv.Value.Length > 0) { slot = kv.Key; break; }
            }
            if (slot == 0)
            {
                EnsureWaveSnapshots(1);
                slot = 1;
            }

            var v = _voices[0];
            v.ActiveWaveSlot = slot;
            v.PendingNote    = 0x40;     // C-4
            v.HasNewNote     = true;
            v.Volume         = 1f;

            try
            {
                Buzz?.DCWriteLine(string.Format(
                    "[PedalTracker] TestTrigger: requested wave slot {0}, note C-4 on voice 0", slot));
            }
            catch { }
        }

        // =====================================================================
        // INotifyPropertyChanged
        // =====================================================================
        public event PropertyChangedEventHandler PropertyChanged;
        void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Lightweight IMenuItem implementation (matches the pattern used by the
    // PeerCtrl machine).  Kept private so we don't pollute the assembly's
    // public surface.
    //
    // IMenuItem extends INotifyPropertyChanged; the event is required even
    // though we don't fire it (WPF binds checked/enabled via DPs instead).
    // ─────────────────────────────────────────────────────────────────────────
#if WINDOWS
    internal sealed class MenuEntry : System.Windows.DependencyObject, IMenuItem
#else
    internal sealed class MenuEntry : IMenuItem     // fork (studio-mvp.md S2): no WPF off Windows
#endif
    {
#if WINDOWS
        public static readonly System.Windows.DependencyProperty IsCheckedProperty =
            System.Windows.DependencyProperty.Register("IsChecked", typeof(bool),
                typeof(MenuEntry), new System.Windows.PropertyMetadata(false));

        public static readonly System.Windows.DependencyProperty IsEnabledProperty =
            System.Windows.DependencyProperty.Register("IsEnabled", typeof(bool),
                typeof(MenuEntry), new System.Windows.PropertyMetadata(true));
#endif

        readonly Action _action;

        public MenuEntry(int id, string text, Action action)
        {
            ID = id; Text = text; _action = action;
        }

        public int    ID              { get; }
        public string Text            { get; }
        public string GestureText     => null;
        public object CommandParameter => null;
        public bool   IsCheckable     { get; set; }
        public bool   IsDefault       { get; set; }
        public bool   IsSeparator     { get; set; }
        public bool   IsLabel         { get; set; }
        public bool   StaysOpenOnClick { get; set; }
        public IEnumerable<IMenuItem> Children => null;

#if WINDOWS
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
#else
        public bool IsChecked { get; set; }
        public bool IsEnabled { get; set; } = true;
#endif

        public System.Windows.Input.ICommand Command => new RelayCmd(_action);

        // INotifyPropertyChanged — required because IMenuItem extends it.
        // Empty add/remove because WPF binds via the DependencyProperties
        // above; we never raise change notifications ourselves.
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged
        { add { } remove { } }

        sealed class RelayCmd : System.Windows.Input.ICommand
        {
            readonly Action _a;
            public RelayCmd(Action a) { _a = a; }
            public bool CanExecute(object _) => true;
            public void Execute(object _)    => _a?.Invoke();
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
