// PedalLFO.cs — managed control machine.
//
// One LFO per instance; drives one global parameter on one foreign machine.
// User picks the target via the embedded GUI's "Configure target…" button.
//
// References: Core §N, PedalComp §N, PedalMuter §N, PedalTracker §N
// from ReBuzz_ManagedMachine_Notes_*.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace WDE.PedalLFO
{
    [MachineDecl(
        Name      = "Pedal LFO",
        ShortName = "PLFO",
        Author    = "WDE",
        MaxTracks = 0)]
    public class PedalLFOMachine : IBuzzMachine
    {
        // ── Period range (free-running mode, in ticks) ────────────────────
        // 16 ticks = 1 bar at 4 TPB.  Min 2 because below that the shape
        // is meaningless (Nyquist for the per-tick update rate).
        const int PERIOD_MIN = 2;
        const int PERIOD_MAX = 16384;     // ~1024 bars at 4 TPB — plenty
        const int PERIOD_DEF = 16;        // 1 bar at 4 TPB

        // ── Host & Buzz access ───────────────────────────────────────────
        readonly IBuzzMachineHost host;
        IBuzz Buzz => host?.Machine?.Graph?.Buzz;

        // ── Persistent state (saved with song via MachineState) ───────────
        string _targetMachineName   = "";
        string _targetParameterName = "";

        // Display-side accessors (UI thread; reference reads atomic).
        public string TargetMachineName   => _targetMachineName;
        public string TargetParameterName => _targetParameterName;

        // ── Cached target (atomically replaced; never mutated in place) ───
        //
        // Audio thread reads `_target` once per Work(); reference reads are
        // atomic on x64 so the read always returns either a coherent old or
        // new TargetRef, never a partial view (Core §24).
        sealed class TargetRef
        {
            public IMachine   Machine;
            public IParameter Param;
            public int Min;
            public int Max;
        }
        TargetRef _target = null;

        // ── LFO state (audio thread) ─────────────────────────────────────
        int    _prevSongPos       = -1;
        int    _resetSongPos      = 0;
        double _freePhaseAccum    = 0;       // free-running mode, per-tick accumulator
        int    _lastWrittenValue  = int.MinValue;
        int    _lastSHCycle       = int.MinValue;
        double _lastSHValue       = 0;
        readonly Random _rng      = new Random();

        // Audio→UI throttle for re-resolve attempts when target name is set
        // but reference is null (target not yet in song, or just deleted).
        int _lastResolveAttemptMs = 0;

        // ── Live display state (audio→UI; volatile is sufficient on x64,
        //                       see PedalComp §7) ─────────────────────────
        volatile float _displayPhase    = 0f;   // 0..1
        volatile float _displayValue    = 0f;   // 0..1 (normalised within target range)
        volatile bool  _isStopped       = false; // true: Start switch is off → holding Center
        volatile string _lastError      = null;  // populated by Work() try/catch — surfaced in GUI
        public  float  DisplayPhase    => _displayPhase;
        public  float  DisplayValue    => _displayValue;
        public  bool   IsStopped       => _isStopped;
        public  bool   HasTarget       => _target != null;
        public  string LastError       => _lastError;
        public  void   ClearLastError()  => _lastError = null;

        // ── Switch-mode Start state ───────────────────────────────────────
        // _prevStartState: snapshot from the previous Work() call, used to
        //   detect 0→1 edges (which fire a retrigger).  Detection is done
        //   in Work() rather than a custom setter because custom property
        //   setters on managed-machine bool parameters appear to interact
        //   poorly with ReBuzz's parameter-delegate construction (causes
        //   a hang at machine-load time — Bypass works because it's an
        //   auto-property).
        // _retriggerRequested: also set by ResetPhase() (the GUI button).
        bool _prevStartState               = true;
        volatile bool _retriggerRequested  = false;

        // ── Construction ─────────────────────────────────────────────────
        public PedalLFOMachine(IBuzzMachineHost host) { this.host = host; }

        // ── Rack parameters ──────────────────────────────────────────────
        // Declaration order is preset-stable (Build §3.3) — do NOT reorder
        // or insert; only append at the end.

        [ParameterDecl(
            Name              = "Shape",
            Description       = "LFO waveform shape",
            MinValue          = 0,
            MaxValue          = 5,
            DefValue          = 0,
            ValueDescriptions = new[] { "Sine", "Triangle", "Square", "Saw Up", "Saw Down", "S&H" })]
        public int Shape { get; set; } = 0;

        [ParameterDecl(
            Name        = "Period",
            Description = "LFO cycle length in ticks (free-running mode). 16 ticks = 1 bar at 4 TPB. Ignored when Sync is set.",
            MinValue    = PERIOD_MIN,
            MaxValue    = PERIOD_MAX,
            DefValue    = PERIOD_DEF)]
        public int Period { get; set; } = PERIOD_DEF;

        [ParameterDecl(
            Name        = "Depth",
            Description = "Modulation amount, in % of target parameter range (peak-to-peak swing = Depth × range).",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 50)]
        public int Depth { get; set; } = 50;

        [ParameterDecl(
            Name        = "Center",
            Description = "Center value, in % of target parameter range. LFO oscillates around this point.",
            MinValue    = 0,
            MaxValue    = 100,
            DefValue    = 50)]
        public int Center { get; set; } = 50;

        [ParameterDecl(
            Name        = "Phase",
            Description = "Phase offset (0–127 maps to 0°–360°)",
            MinValue    = 0,
            MaxValue    = 127,
            DefValue    = 0)]
        public int Phase { get; set; } = 0;

        [ParameterDecl(
            Name              = "Sync",
            Description       = "Time base. Free uses Period; others lock one cycle to a musical division.",
            MinValue          = 0,
            MaxValue          = 7,
            DefValue          = 0,
            ValueDescriptions = new[] { "Free", "1/1", "1/2", "1/4", "1/8", "1/16", "1/4T", "1/8T" })]
        public int Sync { get; set; } = 0;

        [ParameterDecl(
            Name        = "Bypass",
            Description = "Disable LFO output (target value frozen at last write)")]
        public bool Bypass { get; set; } = false;

        // ── Appended in v1.1 — keep at the end so existing preset/pattern
        //    indices for parameters 0–6 stay valid (Build §3.3).
        // ── v1.2: switch semantics (was tick-threshold int in v1.1).
        //    Pattern row "Start=1" runs the LFO; "Start=0" stops it and
        //    holds the target at Center.  The 0→1 transition retriggers
        //    phase to 0; detection happens in WorkInternal() rather than
        //    in a custom setter (see _prevStartState).
        [ParameterDecl(
            Name        = "Start",
            Description = "Switch: 1 = LFO running (0→1 transition retriggers from phase 0). 0 = stopped, target held at Center.")]
        public bool Start { get; set; } = true;

        // ── DescribeValue: per-parameter status-bar formatting ────────────
        // (Hooked by ReBuzz via reflection — PedalTracker §7.4.)
        public string DescribeValue(IParameter p, int value)
        {
            if (p == null) return null;
            switch (p.Name)
            {
                case "Period":
                {
                    double bars = value / 16.0;             // 16 ticks = 1 bar at 4 TPB
                    return bars >= 1.0
                        ? $"{value} ticks ({bars:0.##} bars @4TPB)"
                        : $"{value} ticks ({bars:0.###} bars @4TPB)";
                }
                case "Depth":  return $"{value}%";
                case "Center": return $"{value}%";
                case "Phase":  return $"{(int)Math.Round(value * 360.0 / 128.0)}°";
            }
            return null;
        }

        // ── Persistence ──────────────────────────────────────────────────
        // ReBuzz auto-detects this property and serialises/deserialises it
        // with the song.
        public PedalLFOState MachineState
        {
            get => new PedalLFOState
            {
                TargetMachineName   = _targetMachineName,
                TargetParameterName = _targetParameterName,
            };
            set
            {
                if (value == null) return;
                _targetMachineName   = value.TargetMachineName   ?? "";
                _targetParameterName = value.TargetParameterName ?? "";
                ScheduleResolveOnUiThread();
            }
        }

        // ── Public configuration entrypoints (called from GUI) ────────────
        public void SetTarget(string machineName, string parameterName)
        {
            _targetMachineName   = machineName   ?? "";
            _targetParameterName = parameterName ?? "";
            ScheduleResolveOnUiThread();
        }

        public void ClearTarget() => SetTarget(null, null);

        public void ResetPhase()
        {
            // Same path as a Start=0→1 transition: free-mode accumulator
            // gets zeroed and sync mode rebases to current song position
            // on the next Work() tick.
            _retriggerRequested = true;
            _freePhaseAccum     = 0;
            _prevSongPos        = -1;        // re-detect first tick after reset
        }

        /// <summary>UI thread only: returns true if cached target machine has
        /// been removed from Song.Machines (e.g. user deleted it).</summary>
        public bool IsTargetStale()
        {
            var t = _target;
            if (t == null) return false;
            var song = Buzz?.Song;
            if (song?.Machines == null) return true;
            return !song.Machines.Contains(t.Machine);
        }

        /// <summary>UI thread only: clear stale cache and try again.</summary>
        public void RefreshTargetIfStale()
        {
            if (IsTargetStale())
            {
                _target = null;
                _lastWrittenValue = int.MinValue;
                ResolveTarget();
            }
        }

        // ── Resolution (UI thread only) ──────────────────────────────────
#if WINDOWS
        void ScheduleResolveOnUiThread()
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess())
                ResolveTarget();
            else
                d.BeginInvoke((Action)ResolveTarget);
        }
#else
        // Fork (studio-mvp.md S2): no WPF dispatcher off Windows: the UI thread's context, as the
        // machine was made on it.
        readonly System.Threading.SynchronizationContext uiContext = System.Threading.SynchronizationContext.Current;

        void ScheduleResolveOnUiThread()
        {
            if (uiContext == null || System.Threading.SynchronizationContext.Current == uiContext)
                ResolveTarget();
            else
                uiContext.Post(_ => ResolveTarget(), null);
        }
#endif

        void ResolveTarget()
        {
            // ALWAYS called on UI thread.  Do not call from Work().
            _target           = null;
            _lastWrittenValue = int.MinValue;

            if (string.IsNullOrEmpty(_targetMachineName)) return;

            var song = Buzz?.Song;
            if (song?.Machines == null) return;

            // Case-sensitive name compare — matches the rest of the project.
            var m = song.Machines.FirstOrDefault(x => x?.Name == _targetMachineName);
            if (m == null) return;

            // Refuse to target self (PedalMuter §1.3).
            if (ReferenceEquals(m, host?.Machine)) return;

            IParameter found = null;
            if (m.ParameterGroups != null)
            {
                foreach (var pg in m.ParameterGroups)
                {
                    if (pg?.Parameters == null) continue;
                    foreach (var p in pg.Parameters)
                    {
                        if (p == null) continue;
                        if (p.Name == _targetParameterName) { found = p; break; }
                    }
                    if (found != null) break;
                }
            }
            if (found == null) return;

            _target = new TargetRef
            {
                Machine = m,
                Param   = found,
                Min     = found.MinValue,
                Max     = found.MaxValue,
            };
        }

        // ── The audio-thread Work() ──────────────────────────────────────
        // Control machines have signature `void Work()` (Core §2).
        //
        // The body is wrapped in a try/catch because an unhandled exception
        // from a control machine's Work() can leave the ReBuzz audio engine
        // in a poisoned state (apparent hang on Play).  We capture the first
        // such fault into _lastError so the GUI can surface it; the cached
        // target is also cleared so we stop hammering the offending write
        // path until the user re-Configures.
        public void Work()
        {
            try { WorkInternal(); }
            catch (Exception ex)
            {
                if (_lastError == null)
                    _lastError = ex.GetType().Name + ": " + ex.Message;
                _target = null;
            }
        }

        void WorkInternal()
        {
            // Read the cached snapshot once.  Reference reads are atomic.
            var t = _target;

            // Lazy/auto re-resolve when name is configured but reference
            // is missing.  Throttled to ~1/sec so we don't pound the
            // dispatcher when target isn't in the song (Core §24).
            if (t == null && !string.IsNullOrEmpty(_targetMachineName))
            {
                int now = Environment.TickCount;
                if (unchecked(now - _lastResolveAttemptMs) > 1000)
                {
                    _lastResolveAttemptMs = now;
                    ScheduleResolveOnUiThread();
                }
            }

            if (Bypass || t == null) return;

            // PlayPosition only advances during playback (PedalTracker §2.3).
            // When stopped, songPos stays put → no advance, LFO frozen.
            int songPos = ReadSongPosition();
            if (songPos < 0) return;

            // Per-tick gate: only update when PlayPosition has changed.
            // (Work() is called ~28×/tick at typical buffer sizes — Core §13.)
            if (songPos == _prevSongPos) return;

            int ticksAdvanced;
            if (_prevSongPos < 0)              ticksAdvanced = 1;   // first tick
            else if (songPos < _prevSongPos)   ticksAdvanced = 1;   // loop wrap
            else                               ticksAdvanced = songPos - _prevSongPos;
            _prevSongPos = songPos;

            // Snapshot rack values for this update (atomic int reads on x64).
            int    sync   = Sync;
            int    shape  = Shape;
            int    period = Period;
            bool   startOn = Start;
            double phaseOffset = Phase / 128.0;

            // Edge-detect Start to fire a retrigger on every 0→1 transition.
            // (For global parameters ReBuzz typically suppresses consecutive
            //  identical writes, so a pattern column "1, --, --, 0, 1" only
            //  fires the setter at rows 0, 3, 4 — exactly what we want.)
            if (startOn && !_prevStartState)
                _retriggerRequested = true;
            _prevStartState = startOn;

            // Consume a pending retrigger request.  We only honour it when
            // the switch is actually on, otherwise it'd retrigger silently
            // while we're holding Center and surprise the user when they
            // next turn Start on.
            bool retrigger = false;
            if (_retriggerRequested && startOn)
            {
                retrigger = true;
                _retriggerRequested = false;
            }

            int range = t.Max - t.Min;
            if (range <= 0) return;

            double centerNorm = Center / 100.0;             // 0..1
            int    centerInt  = (int)Math.Round(t.Min + centerNorm * range);
            if (centerInt < t.Min) centerInt = t.Min;
            else if (centerInt > t.Max) centerInt = t.Max;

            // ── Stopped state (Start switch off) ──────────────────────────
            // Hold the target at Center.  The wave restarts from phase 0
            // on the next 0→1 Start transition.
            if (!startOn)
            {
                _isStopped = true;
                _displayPhase = 0f;
                _displayValue = (float)centerNorm;

                // Idempotent SetValue — see PedalMuter §1.1.  Once we've
                // written Center the first time, subsequent ticks no-op.
                if (centerInt != _lastWrittenValue)
                {
                    _lastWrittenValue = centerInt;
                    try { t.Param.SetValue(0, centerInt); } catch { return; }
                    try { t.Machine.SendControlChanges();   } catch { }
                }
                // Reset free-mode accumulator so the wave begins cleanly
                // when Start flips back on.
                _freePhaseAccum = 0;
                return;
            }
            _isStopped = false;

            // ── Phase computation ─────────────────────────────────────────
            double rawCycle;
            if (sync == 0)
            {
                // Free running: phase accumulator advanced per tick.
                if (period < PERIOD_MIN) period = PERIOD_MIN;
                if (retrigger) _freePhaseAccum = 0;
                _freePhaseAccum += ticksAdvanced / (double)period;
                rawCycle = _freePhaseAccum + phaseOffset;
            }
            else
            {
                // Sync: phase derived directly from song position so it locks
                // to bar/beat boundaries.  A retrigger rebases the cycle to
                // the current song position; otherwise _resetSongPos stays at
                // its last value (defaults to 0 = song-start aligned).
                int    tpb           = host?.MasterInfo?.TicksPerBeat ?? 4;
                if (tpb < 1) tpb = 4;
                double ticksPerCycle = ComputeTicksPerCycle(sync, tpb);
                if (ticksPerCycle <= 0) ticksPerCycle = 1;
                if (retrigger) _resetSongPos = songPos;
                rawCycle = ((songPos - _resetSongPos) / ticksPerCycle) + phaseOffset;
            }

            double phase    = rawCycle - Math.Floor(rawCycle);   // [0,1)
            int    cycleIdx = (int)Math.Floor(rawCycle);

            double lfo = ComputeShape(shape, phase, cycleIdx);   // -1..+1

            // Map to target's integer range using Center / Depth.
            // (range, centerInt already computed above the pre-Start gate.)
            double swing    = (Depth / 100.0) * range * 0.5;
            int outputValue = (int)Math.Round(centerInt + lfo * swing);
            if (outputValue < t.Min) outputValue = t.Min;
            else if (outputValue > t.Max) outputValue = t.Max;

            // Update display state (cheap, lock-free).
            _displayPhase = (float)phase;
            _displayValue = (float)((outputValue - t.Min) / (double)range);

            // Idempotent short-circuit — nothing to do if value didn't change
            // (PedalMuter §1.1).  This is the common case for slow LFOs.
            if (outputValue == _lastWrittenValue) return;
            _lastWrittenValue = outputValue;

            // Write to target.  SendControlChanges schedules an extra Tick
            // on this audio buffer so the value is consumed within the same
            // buffer rather than next tick (Core §4).
            try { t.Param.SetValue(0, outputValue); } catch { return; }
            try { t.Machine.SendControlChanges();   } catch { /* swallow */ }
        }

        // ── Helpers ──────────────────────────────────────────────────────
        int ReadSongPosition()
        {
            try { return Buzz?.Song?.PlayPosition ?? -1; }
            catch { return -1; }
        }

        static double ComputeTicksPerCycle(int sync, int tpb)
        {
            switch (sync)
            {
                case 1: return tpb * 4.0;       // 1/1  (assumes 4 beats/measure)
                case 2: return tpb * 2.0;       // 1/2
                case 3: return tpb;              // 1/4
                case 4: return tpb / 2.0;       // 1/8
                case 5: return tpb / 4.0;       // 1/16
                case 6: return tpb * 2.0 / 3.0; // 1/4 triplet
                case 7: return tpb / 3.0;       // 1/8 triplet
                default: return 0;
            }
        }

        double ComputeShape(int shape, double phase, int cycleIdx)
        {
            switch (shape)
            {
                case 0: // Sine
                    return Math.Sin(phase * 2 * Math.PI);

                case 1: // Triangle: -1 at 0, +1 at 0.5, -1 at 1.0
                    return phase < 0.5 ? -1.0 + 4.0 * phase : 3.0 - 4.0 * phase;

                case 2: // Square: +1 first half, -1 second
                    return phase < 0.5 ? 1.0 : -1.0;

                case 3: // Saw up: -1 at 0, +1 at 1
                    return 2.0 * phase - 1.0;

                case 4: // Saw down: +1 at 0, -1 at 1
                    return 1.0 - 2.0 * phase;

                case 5: // Sample & Hold: new random per cycle
                    if (cycleIdx != _lastSHCycle)
                    {
                        _lastSHValue = _rng.NextDouble() * 2 - 1;
                        _lastSHCycle = cycleIdx;
                    }
                    return _lastSHValue;

                default:
                    return 0;
            }
        }

        // ── Picker support (called by settings dialog on UI thread) ───────
        // Globals only — modulating per-track parameters from one LFO doesn't
        // map cleanly to a single track index.  Switch params (bool 0..1) and
        // wave-number params are intentionally allowed; the math degrades
        // gracefully for both.
        internal static List<string> GetGlobalParameterNames(IMachine m)
        {
            var result = new List<string>();
            if (m?.ParameterGroups == null) return result;
            foreach (var pg in m.ParameterGroups)
            {
                if (pg?.Parameters == null) continue;
                if (pg.Type != ParameterGroupType.Global) continue;
                foreach (var p in pg.Parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                    if (p.MaxValue <= p.MinValue) continue;
                    result.Add(p.Name);
                }
            }
            return result;
        }
    }

    // ── Persistent state (XmlSerializable) ──────────────────────────────
    public class PedalLFOState
    {
        public string TargetMachineName   { get; set; } = "";
        public string TargetParameterName { get; set; } = "";
    }
}
