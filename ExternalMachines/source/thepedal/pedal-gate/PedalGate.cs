// Pedal Gate – ReBuzz managed noise gate effect machine
//
// Multi-purpose gate featuring:
//   • Threshold with independent open / close hysteresis
//   • Attack / Hold / Release state machine
//   • Selectable Peak or RMS detection
//   • Configurable closed-gate Range floor (mute → pass-through)
//   • Optional look-ahead (0–25 ms) so transients are never clipped
//   • Bypass
//
// Build (on a Windows machine with ReBuzz and .NET 10 SDK installed):
//   dotnet build PedalGate.csproj -c Release
//
// The output DLL is written directly to:
//   C:\Program Files\ReBuzz\Gear\Effects\Pedal Gate.NET.dll

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace WDE.PedalGate
{
    // =========================================================================
    // Machine declaration
    // =========================================================================

    [MachineDecl(
        Name        = "Pedal Gate",
        ShortName   = "PdlGate",
        Author      = "WDE",
        MaxTracks   = 0,
        InputCount  = 1,
        OutputCount = 1)]
    public class PedalGateMachine : IBuzzMachine
    {
        IBuzzMachineHost host;

        // ReBuzz passes samples as ±32768 floats. Divide to get ±1.0.
        const float SCALE = 1f / 32768f;

        // ── Gate state machine ────────────────────────────────────────────────
        enum GateState { Closed, Attack, Open, Hold, Release }
        GateState _state      = GateState.Closed;
        int       _holdSamps  = 0;   // count-down during Hold phase

        // ── Gate gain ─────────────────────────────────────────────────────────
        // 0.0 = fully gated (or at the Range floor), 1.0 = fully open
        float _gain = 0f;

        // ── Peak envelope follower ────────────────────────────────────────────
        float _envPeak = 0f;

        // ── RMS sliding-window detector ───────────────────────────────────────
        float[] _rmsBuf  = Array.Empty<float>();
        int     _rmsWr   = 0;
        float   _rmsSum  = 0f;
        int     _rmsSize = 0;

        // ── Look-ahead ring buffer ────────────────────────────────────────────
        // Detection runs on the undelayed signal; the output sample is delayed
        // by LookaheadMs so the gate can open *before* the transient arrives.
        Sample[] _laBuf    = new Sample[2];
        int      _laWr     = 0;
        int      _laSamps  = 0;   // currently active lookahead length (samples)

        // ── Cached sample rate ────────────────────────────────────────────────
        int _sr = 0;

        // ── Metering — written by audio thread, read by GUI timer ─────────────
        // Reads of float/int on x64 are atomic; volatile ensures visibility.
        public volatile float MeterLevel = 0f;   // detection level  (0 = silence, 1 = 0 dBFS)
        public volatile float MeterGain  = 0f;   // current gate gain (0 = muted, 1 = fully open)
        public volatile int   MeterState = 0;    // (int)GateState

        // ── Constructor ───────────────────────────────────────────────────────
        // host.Machine is null at construction time — the entire host?.Machine?.…
        // chain returns null and event subscription silently never happens.
        // Use Global.Buzz.Song instead (confirmed pattern from Pedal Patcher).
        public PedalGateMachine(IBuzzMachineHost host)
        {
            this.host = host;
            BuzzGUI.Common.Global.Buzz.Song.MachineAdded   += OnMachineAdded;
            BuzzGUI.Common.Global.Buzz.Song.MachineRemoved += OnMachineRemoved;
        }

        void OnMachineAdded(IMachine m)
        {
            if (m != host?.Machine) return;
            host.InputChannelCount  = 2;   // Main (pin 0) + Sidechain (pin 1)
            host.OutputChannelCount = 1;
            BuzzGUI.Common.Global.Buzz.Song.MachineAdded -= OnMachineAdded;
        }

        void OnMachineRemoved(IMachine m)
        {
            if (m != host?.Machine) return;
            BuzzGUI.Common.Global.Buzz.Song.MachineAdded   -= OnMachineAdded;
            BuzzGUI.Common.Global.Buzz.Song.MachineRemoved -= OnMachineRemoved;
        }

        // =========================================================================
        // Parameters
        // =========================================================================

        // Note: Buzz/ReBuzz integer parameters must have MinValue >= 0.
        // dBFS values are stored as offset integers and decoded in Work().

        /// <summary>
        /// Open threshold. 120 steps × 0.5 dB = −60.0 to 0.0 dBFS.
        /// Stored as 0–120 so MinValue ≥ 0 (Buzz constraint).
        /// Actual dBFS = (ThresholdParam / 2.0) − 60.
        /// Default 60 → −30.0 dBFS.
        /// ValueDescriptions shows the real dBFS label in the ReBuzz parameter UI.
        /// </summary>
        [ParameterDecl(
            Name              = "Threshold",
            Description       = "Gate open threshold in dBFS (−60.0 to 0.0, 0.5 dB steps)",
            MinValue          = 0,
            MaxValue          = 120,
            DefValue          = 60,
            ValueDescriptions = new[]
            {
                "−60.0 dBFS", "−59.5 dBFS", "−59.0 dBFS", "−58.5 dBFS", "−58.0 dBFS",
                "−57.5 dBFS", "−57.0 dBFS", "−56.5 dBFS", "−56.0 dBFS", "−55.5 dBFS",
                "−55.0 dBFS", "−54.5 dBFS", "−54.0 dBFS", "−53.5 dBFS", "−53.0 dBFS",
                "−52.5 dBFS", "−52.0 dBFS", "−51.5 dBFS", "−51.0 dBFS", "−50.5 dBFS",
                "−50.0 dBFS", "−49.5 dBFS", "−49.0 dBFS", "−48.5 dBFS", "−48.0 dBFS",
                "−47.5 dBFS", "−47.0 dBFS", "−46.5 dBFS", "−46.0 dBFS", "−45.5 dBFS",
                "−45.0 dBFS", "−44.5 dBFS", "−44.0 dBFS", "−43.5 dBFS", "−43.0 dBFS",
                "−42.5 dBFS", "−42.0 dBFS", "−41.5 dBFS", "−41.0 dBFS", "−40.5 dBFS",
                "−40.0 dBFS", "−39.5 dBFS", "−39.0 dBFS", "−38.5 dBFS", "−38.0 dBFS",
                "−37.5 dBFS", "−37.0 dBFS", "−36.5 dBFS", "−36.0 dBFS", "−35.5 dBFS",
                "−35.0 dBFS", "−34.5 dBFS", "−34.0 dBFS", "−33.5 dBFS", "−33.0 dBFS",
                "−32.5 dBFS", "−32.0 dBFS", "−31.5 dBFS", "−31.0 dBFS", "−30.5 dBFS",
                "−30.0 dBFS", "−29.5 dBFS", "−29.0 dBFS", "−28.5 dBFS", "−28.0 dBFS",
                "−27.5 dBFS", "−27.0 dBFS", "−26.5 dBFS", "−26.0 dBFS", "−25.5 dBFS",
                "−25.0 dBFS", "−24.5 dBFS", "−24.0 dBFS", "−23.5 dBFS", "−23.0 dBFS",
                "−22.5 dBFS", "−22.0 dBFS", "−21.5 dBFS", "−21.0 dBFS", "−20.5 dBFS",
                "−20.0 dBFS", "−19.5 dBFS", "−19.0 dBFS", "−18.5 dBFS", "−18.0 dBFS",
                "−17.5 dBFS", "−17.0 dBFS", "−16.5 dBFS", "−16.0 dBFS", "−15.5 dBFS",
                "−15.0 dBFS", "−14.5 dBFS", "−14.0 dBFS", "−13.5 dBFS", "−13.0 dBFS",
                "−12.5 dBFS", "−12.0 dBFS", "−11.5 dBFS", "−11.0 dBFS", "−10.5 dBFS",
                "−10.0 dBFS", "− 9.5 dBFS", "− 9.0 dBFS", "− 8.5 dBFS", "− 8.0 dBFS",
                "− 7.5 dBFS", "− 7.0 dBFS", "− 6.5 dBFS", "− 6.0 dBFS", "− 5.5 dBFS",
                "− 5.0 dBFS", "− 4.5 dBFS", "− 4.0 dBFS", "− 3.5 dBFS", "− 3.0 dBFS",
                "− 2.5 dBFS", "− 2.0 dBFS", "− 1.5 dBFS", "− 1.0 dBFS", "− 0.5 dBFS",
                "  0.0 dBFS"
            })]
        public int ThresholdParam { get; set; } = 60;

        /// <summary>
        /// Hysteresis gap (dB).
        /// The gate closes when the signal drops more than this many dB
        /// below the open threshold — prevents rapid chattering.
        /// </summary>
        [ParameterDecl(
            Name        = "Hysteresis",
            Description = "Gap between open and close thresholds (dB). Higher = less chatter.",
            MinValue    = 0,
            MaxValue    = 20,
            DefValue    = 6)]
        public int HysteresisParam { get; set; } = 6;

        /// <summary>Attack time in milliseconds.</summary>
        [ParameterDecl(
            Name        = "Attack ms",
            Description = "Time for gate to open fully (0 = instant, max 500 ms)",
            MinValue    = 0,
            MaxValue    = 500,
            DefValue    = 5)]
        public int AttackMs { get; set; } = 5;

        /// <summary>Hold time in milliseconds.</summary>
        [ParameterDecl(
            Name        = "Hold ms",
            Description = "How long the gate stays open after signal drops below close threshold",
            MinValue    = 0,
            MaxValue    = 2000,
            DefValue    = 100)]
        public int HoldMs { get; set; } = 100;

        /// <summary>Release time in milliseconds.</summary>
        [ParameterDecl(
            Name        = "Release ms",
            Description = "Time for gate to close fully (1–2000 ms)",
            MinValue    = 1,
            MaxValue    = 2000,
            DefValue    = 200)]
        public int ReleaseMs { get; set; } = 200;

        /// <summary>
        /// Closed-gate floor attenuation.
        /// Stored as 0–90: display value = (Range - 90) dBFS.
        /// 0 → −90 dBFS (near-mute), 90 → 0 dBFS (no attenuation at all).
        /// Typical use: leave at 0 for a hard gate, raise slightly for a softer effect.
        /// </summary>
        [ParameterDecl(
            Name        = "Range dB",
            Description = "Closed-gate floor (0 = mute / −90 dBFS … 90 = no attenuation)",
            MinValue    = 0,
            MaxValue    = 90,
            DefValue    = 0)]
        public int RangeParam { get; set; } = 0;

        /// <summary>Signal detection mode: Peak or RMS.</summary>
        [ParameterDecl(
            Name              = "Detection",
            Description       = "Peak: fast transient response. RMS: smoother for sustained sources.",
            MinValue          = 0,
            MaxValue          = 1,
            DefValue          = 0,
            ValueDescriptions = new[] { "Peak", "RMS" })]
        public int DetectionMode { get; set; } = 0;

        /// <summary>Look-ahead delay in milliseconds (0 = off).</summary>
        [ParameterDecl(
            Name        = "Lookahead ms",
            Description = "Pre-delay so gate opens before the transient arrives (0–25 ms)",
            MinValue    = 0,
            MaxValue    = 25,
            DefValue    = 0)]
        public int LookaheadMs { get; set; } = 0;

        /// <summary>Bypass the gate entirely.</summary>
        [ParameterDecl(
            Name              = "Bypass",
            Description       = "Pass audio through unmodified",
            MinValue          = 0,
            MaxValue          = 1,
            DefValue          = 0,
            ValueDescriptions = new[] { "no", "yes" })]
        public int Bypass { get; set; } = 0;

        // =========================================================================
        // Helpers
        // =========================================================================

        static float DbToLin(float db)
            => db <= -100f ? 0f : MathF.Pow(10f, db * 0.05f);

        // =========================================================================
        // Audio processing
        // =========================================================================

        // ── Audio processing — EffectBlockMulti ───────────────────────────────
        // IList<Sample[]> signature is required for multi-IO effects (ReBuzz
        // ≥ 1813-preview).  input[0] = Main, input[1] = Sidechain (null if
        // nothing connected).  output[0] = single stereo output.
        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            Sample[] main = (input  != null && input.Count  > 0) ? input[0]  : null;
            Sample[] sc   = (input  != null && input.Count  > 1) ? input[1]  : null;
            Sample[] out0 = (output != null && output.Count > 0) ? output[0] : null;

            if (mode == WorkModes.WM_NOIO || main == null || out0 == null)
                return false;

            // Detection runs on sidechain when connected, otherwise on main input.
            Sample[] detect = sc ?? main;

            // ── Bypass ────────────────────────────────────────────────────────
            if (Bypass != 0)
            {
                for (int i = 0; i < n; i++) out0[i] = main[i];
                return true;
            }

            // ── Sample-rate initialisation ────────────────────────────────────
            int sr = host.MasterInfo.SamplesPerSec;
            if (sr > 0 && sr != _sr)
            {
                _sr = sr;
                ReinitBuffers();
            }

            // ── Precompute constants (stable over this buffer) ─────────────────
            float sr_f = (float)_sr;

            // Open / close thresholds in linear amplitude (signal is normalised ±1).
            //
            // ThresholdParam 0–120 maps linearly to −60.0 – 0.0 dBFS at 0.5 dB/step.
            // Linear dB spacing feels natural on a gate and matches what the
            // ValueDescriptions label shows in the UI.
            float threshDb  = ThresholdParam * 0.5f - 60f;
            float threshLin = DbToLin(threshDb);
            float closeLin  = threshLin * DbToLin(-HysteresisParam);

            // Range floor: gain applied when gate is fully closed
            float rangeLin = DbToLin(RangeParam - 90);

            // Per-sample gain step sizes
            float attackRate  = (AttackMs  > 0) ? 1f / (AttackMs  * 0.001f * sr_f) : 1f;
            float releaseRate = (ReleaseMs > 0) ? 1f / (ReleaseMs * 0.001f * sr_f) : 1f;

            // Peak envelope smoother coefficients (~1 ms attack, ~10 ms release)
            float peakAtk = MathF.Exp(-1f / MathF.Max(1f, 0.001f * sr_f));
            float peakRel = MathF.Exp(-1f / MathF.Max(1f, 0.010f * sr_f));

            // Determine / update lookahead length
            int newLaSamps = Math.Min((int)(LookaheadMs * 0.001f * sr_f), _laBuf.Length - 1);
            if (newLaSamps != _laSamps)
            {
                // Clear ring on size change to avoid stale samples
                Array.Clear(_laBuf, 0, _laBuf.Length);
                _laWr    = 0;
                _laSamps = newLaSamps;
            }

            // Determine / update RMS window size (10 ms)
            int newRmsSize = Math.Max(1, (int)(0.010f * sr_f));
            if (newRmsSize != _rmsSize)
            {
                _rmsSize = newRmsSize;
                _rmsBuf  = new float[_rmsSize];
                _rmsWr   = 0;
                _rmsSum  = 0f;
            }

            bool useRMS = DetectionMode == 1;
            int  ringLen = _laSamps + 1;   // ring size = delay + 1

            // ── Per-sample loop ───────────────────────────────────────────────
            for (int i = 0; i < n; i++)
            {
                // Audio to be gated — always from main input (pin 0)
                float L = main[i].L * SCALE;
                float R = main[i].R * SCALE;

                // Detection signal — sidechain (pin 1) when connected, else main input
                float dL = detect[i].L * SCALE;
                float dR = detect[i].R * SCALE;

                // ── 1. Detect signal level on the *undelayed* detection sample ───
                float level;
                if (useRMS)
                {
                    // Sliding-window RMS (stereo power summed)
                    float sq = dL * dL + dR * dR;
                    _rmsSum -= _rmsBuf[_rmsWr];
                    _rmsBuf[_rmsWr] = sq;
                    _rmsSum  = MathF.Max(0f, _rmsSum + sq);
                    _rmsWr   = (_rmsWr + 1) % _rmsSize;
                    level    = MathF.Sqrt(_rmsSum / _rmsSize);
                }
                else
                {
                    // Peak envelope follower (max of |dL|, |dR|)
                    float peak = MathF.Max(MathF.Abs(dL), MathF.Abs(dR));
                    _envPeak = (peak > _envPeak)
                        ? peakAtk * _envPeak + (1f - peakAtk) * peak
                        : peakRel * _envPeak + (1f - peakRel) * peak;
                    level = _envPeak;
                }

                // ── 2. Gate state machine ──────────────────────────────────────
                switch (_state)
                {
                    case GateState.Closed:
                        // Wait for signal to cross the open threshold
                        if (level >= threshLin)
                            _state = GateState.Attack;
                        break;

                    case GateState.Attack:
                        _gain += attackRate;
                        if (_gain >= 1f)
                        {
                            _gain  = 1f;
                            _state = GateState.Open;
                            // Re-check close threshold now that gate is fully open —
                            // if signal already dropped, go to Hold rather than
                            // leaving the state as Open for the next sample.
                            if (level < closeLin)
                            {
                                _holdSamps = Math.Max(0, (int)(HoldMs * 0.001f * sr_f));
                                _state = GateState.Hold;
                            }
                        }
                        else if (level < closeLin)
                        {
                            // Signal dropped before gate fully opened → close directly
                            _state = GateState.Release;
                        }
                        break;

                    case GateState.Open:
                        // Gate stays open while signal exceeds close threshold
                        if (level < closeLin)
                        {
                            _holdSamps = Math.Max(0, (int)(HoldMs * 0.001f * sr_f));
                            _state     = GateState.Hold;
                        }
                        break;

                    case GateState.Hold:
                        // Keep gate open for the hold period even if signal is low
                        if (level >= threshLin)
                        {
                            _state = GateState.Open;   // re-triggered
                        }
                        else if (--_holdSamps <= 0)
                        {
                            _state = GateState.Release;
                        }
                        break;

                    case GateState.Release:
                        _gain -= releaseRate;
                        if (_gain <= rangeLin)
                        {
                            _gain  = rangeLin;
                            _state = GateState.Closed;
                        }
                        // Signal came back up during release → re-open
                        if (level >= threshLin)
                            _state = GateState.Attack;
                        break;
                }

                // ── 3. Clamp gain to [rangeLin, 1] ─────────────────────────────
                float g = Math.Clamp(_gain, rangeLin, 1f);

                // ── 4. Apply gain to output sample (with optional lookahead) ────
                if (_laSamps > 0)
                {
                    // Write current sample into ring, read the delayed sample
                    int readPos = (_laWr + 1) % ringLen;
                    Sample delayed = _laBuf[readPos];
                    _laBuf[_laWr] = main[i];
                    _laWr = (_laWr + 1) % ringLen;

                    out0[i].L = delayed.L * g;
                    out0[i].R = delayed.R * g;
                }
                else
                {
                    out0[i].L = main[i].L * g;
                    out0[i].R = main[i].R * g;
                }
            }

            // ── Publish metering for GUI ───────────────────────────────────────
            // Publish whichever signal the gate is actually detecting on so the
            // IN meter moves correctly in both Peak and RMS modes.
            MeterLevel = useRMS
                ? MathF.Sqrt(MathF.Max(0f, _rmsSum) / _rmsSize)  // end-of-buffer RMS level
                : _envPeak;                                        // peak envelope
            MeterGain  = _gain;
            MeterState = (int)_state;

            return true;
        }

        // ── Buffer / state reset on sample-rate change ────────────────────────

        void ReinitBuffers()
        {
            // Lookahead ring: sized for max 25 ms
            int maxLa = (int)(0.025f * _sr) + 2;
            _laBuf   = new Sample[maxLa];
            _laWr    = 0;
            _laSamps = 0;

            // RMS window: ~10 ms
            _rmsSize = Math.Max(1, (int)(0.010f * _sr));
            _rmsBuf  = new float[_rmsSize];
            _rmsWr   = 0;
            _rmsSum  = 0f;

            // Reset dynamic state
            _gain    = 0f;
            _envPeak = 0f;
            _state   = GateState.Closed;
        }

        // =========================================================================
        // MachineState — versioned binary blob persisted with the song
        //
        // All parameters are already serialised automatically by ReBuzz via its
        // parameter value store (ParameterCore.values[]).  MachineState is
        // implemented here as belt-and-suspenders: a single versioned blob that
        // survives future parameter additions or reorderings cleanly.
        //
        // Version history:
        //   1 — initial: Threshold, Hysteresis, Attack, Hold, Release,
        //                Range, Detection, Lookahead, Bypass  (9 × int32)
        // =========================================================================

        public byte[] MachineState
        {
            get
            {
                using var ms = new System.IO.MemoryStream();
                using var w  = new System.IO.BinaryWriter(ms);
                w.Write((byte)1);           // format version
                w.Write(ThresholdParam);    // int32
                w.Write(HysteresisParam);   // int32
                w.Write(AttackMs);          // int32
                w.Write(HoldMs);            // int32
                w.Write(ReleaseMs);         // int32
                w.Write(RangeParam);        // int32
                w.Write(DetectionMode);     // int32
                w.Write(LookaheadMs);       // int32
                w.Write(Bypass);            // int32
                return ms.ToArray();
            }
            set
            {
                if (value == null || value.Length == 0) return;
                try
                {
                    using var ms = new System.IO.MemoryStream(value);
                    using var r  = new System.IO.BinaryReader(ms);
                    byte ver = r.ReadByte();
                    if (ver >= 1)
                    {
                        // Route every write through SetValue so that
                        // ParameterCore.values[] is updated alongside the
                        // C# property — the only safe path per Core §13.1.
                        SetParam("Threshold",    r.ReadInt32());
                        SetParam("Hysteresis",   r.ReadInt32());
                        SetParam("Attack ms",    r.ReadInt32());
                        SetParam("Hold ms",      r.ReadInt32());
                        SetParam("Release ms",   r.ReadInt32());
                        SetParam("Range dB",     r.ReadInt32());
                        SetParam("Detection",    r.ReadInt32());
                        SetParam("Lookahead ms", r.ReadInt32());
                        SetParam("Bypass",       r.ReadInt32());
                    }
                    // ver >= 2 reads would go here when new parameters are added
                }
                catch { /* corrupt or truncated blob — leave parameters at defaults */ }
            }
        }

        // Routes a value through ParameterCore.SetValue so both the C# property
        // backing field and ReBuzz's internal values[] stay in sync.
        void SetParam(string name, int value)
        {
            var groups = host?.Machine?.ParameterGroups;
            if (groups == null || groups.Count < 2) return;
            foreach (var p in groups[1].Parameters)
            {
                if (p?.Name == name)
                {
                    p.SetValue(0, value);
                    return;
                }
            }
        }

        // ── Input pin names ───────────────────────────────────────────────────
        // Called by ReBuzz via reflection (ManagedMachineHost) to label the
        // connection circles in the graph view.
        public string GetChannelName(bool input, int index)
        {
            if (!input) return null;                      // single stereo output, no label needed
            return index == 0 ? "Main" : "Sidechain";
        }
    } // end PedalGateMachine

    // =========================================================================
    // GUI factory — discovered by ReBuzz's assembly scan for IMachineGUIFactory
    // =========================================================================

    [MachineGUIFactoryDecl(PreferWindowedGUI = false, IsGUIResizable = false)]
    public class PedalGateGuiFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalGateGui();
    }

    // =========================================================================
    // GUI — embedded above the parameter sliders in the parameter window.
    //
    // Displays:
    //   Row 1  IN  — input level bar (green → red), yellow threshold marker,
    //                dBFS readout
    //   Row 2  GR  — gain reduction bar (blue, shrinks as gate closes),
    //                dB tick marks
    //   Right  LED + label — gate state (CLOSED / ATTACK / OPEN / HOLD / RELEASE)
    //
    // Built entirely in code — no XAML, no BAML, no resource dictionaries.
    // =========================================================================

    public class PedalGateGui : System.Windows.Controls.UserControl, IMachineGUI
    {
        // ── IMachineGUI ───────────────────────────────────────────────────────
        IMachine         _iMachine;
        PedalGateMachine _machine;

        public IMachine Machine
        {
            get => _iMachine;
            set
            {
                _iMachine = value;
                _machine  = value?.ManagedMachine as PedalGateMachine;
            }
        }

        // ── Visual elements ───────────────────────────────────────────────────
        System.Windows.Shapes.Rectangle   _levelBar;
        System.Windows.Shapes.Rectangle   _threshMarker;
        System.Windows.Shapes.Rectangle   _grBar;
        System.Windows.Shapes.Ellipse     _stateLed;
        System.Windows.Controls.TextBlock _stateLabel;
        System.Windows.Controls.TextBlock _levelDbLabel;

        // ── Layout constants ──────────────────────────────────────────────────
        const double PanelW = 310;
        const double PanelH = 84;
        const double BarX   = 26;    // left edge of meter tracks
        const double BarW   = 172;   // track width — shortened to leave room for dB label
        const double BarH   = 14;
        const double Row1Y  = 10;    // input level row top
        const double Row2Y  = 46;    // gain reduction row top
        const double LedX   = 270;   // pushed right — clear of the dB text (ends ~262)
        const double LedY   = 8;
        const double LedSz  = 14;

        // ── Colours ───────────────────────────────────────────────────────────
        static System.Windows.Media.SolidColorBrush Rgb(byte r, byte g, byte b)
            => new System.Windows.Media.SolidColorBrush(
                   System.Windows.Media.Color.FromRgb(r, g, b));

        static readonly System.Windows.Media.SolidColorBrush
            ColTrack   = Rgb( 30,  30,  30),
            ColLevelLo = Rgb( 55, 175,  55),
            ColLevelHi = Rgb(220,  70,  40),
            ColGR      = Rgb( 55, 130, 220),
            ColThresh  = Rgb(255, 200,   0),
            ColLabel   = Rgb(150, 150, 150);

        static readonly System.Windows.Media.SolidColorBrush[] LedColors =
        {
            Rgb( 45,  45,  45),   // 0 Closed  — dark
            Rgb(255, 195,   0),   // 1 Attack  — amber
            Rgb( 50, 200,  50),   // 2 Open    — green
            Rgb( 50, 200,  50),   // 3 Hold    — green (still open)
            Rgb(200,  90,   0),   // 4 Release — orange
        };

        static readonly string[] StateNames = { "CLOSED", "ATTACK", "OPEN", "HOLD", "RELEASE" };

        System.Windows.Threading.DispatcherTimer _timer;

        // ── Constructor ───────────────────────────────────────────────────────
        public PedalGateGui()
        {
            Width      = PanelW;
            Height     = PanelH;
            Background = Rgb(18, 18, 18);

            var cv = new System.Windows.Controls.Canvas { Width = PanelW, Height = PanelH };
            Content = cv;

            // ── Row 1: Input level ────────────────────────────────────────────
            AddLabel(cv, "IN", 4, Row1Y);
            AddRect (cv, BarX, Row1Y, BarW, BarH, ColTrack);          // track
            _levelBar = AddRect(cv, BarX, Row1Y, 0, BarH, ColLevelLo);// fill (width set each tick)

            // Threshold marker — a narrow vertical bar, repositioned each tick
            _threshMarker = new System.Windows.Shapes.Rectangle
                { Width = 2, Height = BarH + 4, Fill = ColThresh, Opacity = 0.85 };
            Place(cv, _threshMarker, BarX, Row1Y - 2);

            _levelDbLabel = new System.Windows.Controls.TextBlock
                { Foreground = ColLabel, FontSize = 10, Text = "−∞" };
            Place(cv, _levelDbLabel, BarX + BarW + 4, Row1Y);

            // ── Row 2: Gain reduction ─────────────────────────────────────────
            // Convention: full bar = maximum reduction (gate closed),
            //             empty bar = no reduction (gate fully open).
            // Matches hardware VU convention (dbx, BSS etc.).
            AddLabel(cv, "GR", 4, Row2Y);
            AddRect (cv, BarX, Row2Y, BarW, BarH, ColTrack);           // track
            _grBar = AddRect(cv, BarX, Row2Y, 0, BarH, ColGR);         // fill (starts empty = 0 reduction)

            // "0" label at left edge (no reduction), tick marks grow rightward
            AddLabel(cv, "0", BarX - 6, Row2Y, 8);

            foreach (var pair in new (float redDb, string lbl)[]
                { (6f,"6"), (12f,"12"), (20f,"20"), (40f,"40") })
            {
                double tx = BarX + GrReductionToX(pair.redDb);
                var tick = new System.Windows.Shapes.Rectangle
                    { Width = 1, Height = 4, Fill = ColLabel, Opacity = 0.45 };
                Place(cv, tick, tx, Row2Y + BarH);
                var t = new System.Windows.Controls.TextBlock
                    { Text = pair.lbl, Foreground = ColLabel, FontSize = 8, Opacity = 0.55 };
                Place(cv, t, tx - 4, Row2Y + BarH + 2);
            }

            // ── State LED + label ─────────────────────────────────────────────
            _stateLed = new System.Windows.Shapes.Ellipse
                { Width = LedSz, Height = LedSz, Fill = LedColors[0] };
            Place(cv, _stateLed, LedX, LedY);

            _stateLabel = new System.Windows.Controls.TextBlock
            {
                Text          = "CLOSED",
                Foreground    = ColLabel,
                FontSize      = 9,
                TextAlignment = System.Windows.TextAlignment.Center,
                Width         = 60
            };
            Place(cv, _stateLabel, LedX - 20, LedY + 18);

            // ── Refresh timer ~30 fps ─────────────────────────────────────────
            _timer = new System.Windows.Threading.DispatcherTimer
                { Interval = System.TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, __) => Refresh();
            _timer.Start();
            Unloaded    += (_, __) => _timer.Stop();
        }

        // ── Refresh — polls volatile machine fields, updates visuals ──────────
        void Refresh()
        {
            if (_machine == null) return;

            float level = _machine.MeterLevel;
            float gain  = _machine.MeterGain;
            int   state = System.Math.Clamp(_machine.MeterState, 0, LedColors.Length - 1);

            // Input level bar
            float  levelDb = level > 1e-7f ? 20f * MathF.Log10(level) : -100f;
            double levelPx = LevelToX(levelDb);
            _levelBar.Fill  = levelDb >= -6f ? ColLevelHi : ColLevelLo;
            _levelBar.Width = levelPx;
            _levelDbLabel.Text = levelDb <= -60f ? "−∞"
                : levelDb.ToString(levelDb < -10f ? "F0" : "F1") + " dB";

            // Threshold marker — reads int param directly (atomic on x64, safe from UI thread)
            float threshDb = _machine.ThresholdParam * 0.5f - 60f;
            System.Windows.Controls.Canvas.SetLeft(_threshMarker, BarX + LevelToX(threshDb));

            // Gain reduction bar — empty = gate open (0 dB reduction), full = gate closed
            float  grDb = gain > 1e-7f ? 20f * MathF.Log10(gain) : -100f;
            double grPx = System.Math.Max(0.0, GrReductionToX(-grDb));   // −grDb = reduction amount ≥ 0
            _grBar.Width = grPx;

            // State LED + label
            _stateLed.Fill   = LedColors[state];
            _stateLabel.Text = StateNames[state];
        }

        // ── Coordinate helpers ────────────────────────────────────────────────

        // dBFS −60..0 → pixel 0..BarW  (level meter scale)
        static double LevelToX(float db)
            => System.Math.Clamp((db + 60.0) / 60.0, 0.0, 1.0) * BarW;

        // GR amount 0..60 dB → pixel 0..BarW  (0 dB reduction = empty, 60 dB = full)
        // Matches hardware VU convention: bar grows as reduction increases.
        static double GrReductionToX(float reductionDb)
            => System.Math.Clamp(reductionDb / 60.0, 0.0, 1.0) * BarW;

        // ── WPF builder helpers ───────────────────────────────────────────────

        static System.Windows.Shapes.Rectangle AddRect(
            System.Windows.Controls.Canvas cv,
            double x, double y, double w, double h,
            System.Windows.Media.Brush fill)
        {
            var r = new System.Windows.Shapes.Rectangle
                { Width = w, Height = h, Fill = fill };
            Place(cv, r, x, y);
            return r;
        }

        static void AddLabel(System.Windows.Controls.Canvas cv,
                             string text, double x, double y, double fs = 9)
        {
            var tb = new System.Windows.Controls.TextBlock
                { Text = text, Foreground = ColLabel, FontSize = fs };
            Place(cv, tb, x, y);
        }

        static void Place(System.Windows.Controls.Canvas cv,
                          System.Windows.UIElement el, double x, double y)
        {
            System.Windows.Controls.Canvas.SetLeft(el, x);
            System.Windows.Controls.Canvas.SetTop (el, y);
            cv.Children.Add(el);
        }
    }

} // namespace WDE.PedalGate
