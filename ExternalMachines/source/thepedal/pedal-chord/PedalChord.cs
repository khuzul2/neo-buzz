// Pedal Chord v1.5 — ReBuzz Managed Controller Machine
// Single-voice chord/arpeggio trigger for one target generator.
// Use multiple instances to control multiple generators.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using Buzz.MachineInterface;

namespace WDE.PedalChord
{
    // =========================================================================
    // Chord library
    // =========================================================================

    static class ChordLib
    {
        public static readonly string[] Names = {
            "Major","Minor","Dom 7","Min 7","Maj 7","Dim","Aug","Sus4","Sus2",
            "Min Maj 7","Add 9","Min Add 9","Maj 9","Min 9","Dom 9","Maj 6",
            "Half Dim 7","Aug Maj 7","Aug 7","Dim Maj 7","7Sus4","7Sus2",
            "6/9","Min 6/9","7b9","7#9","9Sus4","7b5","7b5b9","7b5#9",
            "b5 Triad","Dom 11","Maj 11","Min 11","7#11","Maj7#11","Add 11",
            "Dom 13","Maj 13","Min 13","Dom 13 Full",
            "Dom Shell","Maj7 Shell","Min7 Shell",
            "Quartal","Quintal","Whole Tone","Cluster",
            "Power","Dim 7","Oct 2"
        };

        public static readonly int[][] Intervals = {
            new[]{0,4,7},          // Major
            new[]{0,3,7},          // Minor
            new[]{0,4,7,10},       // Dom 7
            new[]{0,3,7,10},       // Min 7
            new[]{0,4,7,11},       // Maj 7
            new[]{0,3,6},          // Dim
            new[]{0,4,8},          // Aug
            new[]{0,5,7},          // Sus4
            new[]{0,2,7},          // Sus2
            new[]{0,3,7,11},       // Min Maj 7
            new[]{0,4,7,14},       // Add 9
            new[]{0,3,7,14},       // Min Add 9
            new[]{0,4,7,11,14},    // Maj 9
            new[]{0,3,7,10,14},    // Min 9
            new[]{0,4,7,10,14},    // Dom 9
            new[]{0,4,7,9},        // Maj 6
            new[]{0,3,6,10},       // Half Dim 7
            new[]{0,4,8,11},       // Aug Maj 7
            new[]{0,4,8,10},       // Aug 7
            new[]{0,3,6,11},       // Dim Maj 7
            new[]{0,5,7,10},       // 7Sus4
            new[]{0,2,7,10},       // 7Sus2
            new[]{0,4,7,9,14},     // 6/9
            new[]{0,3,7,9,14},     // Min 6/9
            new[]{0,4,7,10,13},    // 7b9
            new[]{0,4,7,10,15},    // 7#9
            new[]{0,5,7,10,14},    // 9Sus4
            new[]{0,4,6,10},       // 7b5
            new[]{0,4,6,10,13},    // 7b5b9
            new[]{0,4,6,10,15},    // 7b5#9
            new[]{0,4,6},          // b5 Triad
            new[]{0,4,7,10,17},    // Dom 11
            new[]{0,4,7,11,17},    // Maj 11
            new[]{0,3,7,10,17},    // Min 11
            new[]{0,4,7,10,18},    // 7#11
            new[]{0,4,7,11,18},    // Maj7#11
            new[]{0,4,7,17},       // Add 11
            new[]{0,4,7,10,14,21}, // Dom 13
            new[]{0,4,7,11,14,21}, // Maj 13
            new[]{0,3,7,10,14,21}, // Min 13
            new[]{0,4,7,10,14,17,21}, // Dom 13 Full
            new[]{0,4,10},         // Dom Shell
            new[]{0,4,11},         // Maj7 Shell
            new[]{0,3,10},         // Min7 Shell
            new[]{0,5,10,15},      // Quartal
            new[]{0,7,14,21},      // Quintal
            new[]{0,2,4,6,8,10},   // Whole Tone
            new[]{0,1,2},          // Cluster
            new[]{0,7},            // Power
            new[]{0,3,6,9},        // Dim 7
            new[]{0,12},           // Oct 2
        };
    }

    // =========================================================================
    // Buzz note helpers
    // =========================================================================

    static class BN
    {
        public const int Off     = 255;
        public const int NoValue = 0;

        public static int ToMidi(int buzzNote)
        {
            if (buzzNote <= 0 || buzzNote == Off) return -1;
            int oct = (buzzNote >> 4) & 0xF;
            int sem = (buzzNote & 0xF) - 1;
            return oct * 12 + sem;
        }

        public static int FromMidi(int midi)
        {
            if (midi < 0 || midi > 119) return -1;
            int oct = midi / 12;
            int sem = midi % 12 + 1;
            return (oct << 4) | sem;
        }
    }

    // =========================================================================
    // Voice state
    // =========================================================================

    class VoiceState
    {
        public int  ChordType    = 0;
        public int  Mode         = 0;
        public int  Speed        = 2;
        public int  Length       = 0;
        public int  OctaveSpread = 1;
        public int  Step         = 1;
        public int  OctWalk      = 0;
        public int  OctOffset    = 0;
        public int  OctDir       = 1;
        public int  Swing        = 0;
        public int  SwingPhaseVal= 0;
        public int  Velocity     = 100;
        public int  Humanize     = 0;
        public int  HumanizeVel  = 0;

        public int  PendingNote  = 0;
        public bool HasNewNote   = false;
        public bool PendingReset = false;

        public bool  Active  = false;
        public int[] Notes   = Array.Empty<int>();
        public int   ArpIdx  = 0;
        public int   ArpDir  = 1;
        public int   ArpTicks       = 0;   // legacy/unused since v1.6.0 (fraction clock)
        public int   ArpStepParity  = 0;

        public const int MaxSlots = 16;
        public int[] SlotOff   = new int[MaxSlots];
        public int[] SlotTrack = new int[MaxSlots];
    }

    // =========================================================================
    // Serialisable machine state — single target
    // =========================================================================

    [Serializable]
    public class PedalChordState
    {
        public string TargetMachine { get; set; } = "";
        public int    BaseTrack     { get; set; } = 0;
    }

    // =========================================================================
    // Machine declaration
    // =========================================================================

    [MachineDecl(
        Name      = "Pedal Chord",
        ShortName = "PdlChrd",
        Author    = "WDE",
        MaxTracks = 1)]
    public class PedalChordMachine : IBuzzMachine
    {
        IBuzzMachineHost host;
        IBuzz            Buzz => host?.Machine?.Graph?.Buzz;

        PedalChordState _state = new PedalChordState();
        VoiceState      _vs    = new VoiceState();
        Random          _rng   = new Random();

        // Cached target resolution — only set on the UI thread (never from Work).
        IMachine   _tgt = null;
        IParameter _np  = null;
        IParameter _vp  = null;

        int    _prevPit = int.MinValue;  // last MasterInfo.PosInTick; sentinel = no prior call
        double _stepAcc = 0.0;           // fractional ticks accrued toward the next arp step
        double _stepLen = 0.0;           // length of the current arp step, in fractional ticks

        readonly int[] _buildBuf = new int[128]; // per-instance, never shared

        // ── Construction ──────────────────────────────────────────────────────
        public PedalChordMachine(IBuzzMachineHost host) { this.host = host; }

        public IBuzzMachineHost Host { set { host = value; } }

        // ── Cache — always resolved on the UI thread, never from Work() ─────────
        void ResolveCache()
        {
            _tgt = null; _np = null; _vp = null;
            if (string.IsNullOrEmpty(_state.TargetMachine)) { return; }
            try
            {
                _tgt = Buzz?.Song?.Machines?.FirstOrDefault(
                    m => m.Name == _state.TargetMachine);
_np = _tgt != null ? FindNoteParam(_tgt) : null;
_vp = _np != null ? FindVelocityParam(_tgt, _np) : null;
                if (_tgt != null) EnsureTrackCount(_tgt, VoiceState.MaxSlots);
            }
            catch { }
        }

        // Schedule ResolveCache with retries — on song load Song.Machines may
        // not yet contain all machines when MachineState is first set.
        void ScheduleResolve(int attempt = 0)
        {
            int delayMs = attempt == 0 ? 0 : Math.Min(500, 100 * attempt);
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(delayMs)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                ResolveCache();
                // If target still not found and we have a name to look for,
                // retry up to 10 times (covers up to ~4 seconds of load time).
                if (_tgt == null && !string.IsNullOrEmpty(_state.TargetMachine)
                    && attempt < 10)
                    ScheduleResolve(attempt + 1);
            };
            timer.Start();
        }

        // ── Parameters ────────────────────────────────────────────────────────

        [ParameterDecl(IsStateless = true,
            Name = "Note",
            Description = "Root note — triggers chord or arp on the target machine")]
        public void SetNote(Note value, int track)
        {
            _vs.PendingNote = value.Value;
            _vs.HasNewNote  = true;
        }

        [ParameterDecl(Name = "Velocity", MinValue = 1, MaxValue = 127, DefValue = 100,
            Description = "Note velocity sent to target (1-127)")]
        public void SetVelocity(int value, int track) =>
            _vs.Velocity = Math.Max(1, Math.Min(127, value));

        [ParameterDecl(Name = "Chord", MinValue = 0,
            MaxValue = 50, DefValue = 0,
            ValueDescriptions = new[]{
                "Major","Minor","Dom 7","Min 7","Maj 7","Dim","Aug","Sus4","Sus2",
                "Min Maj 7","Add 9","Min Add 9","Maj 9","Min 9","Dom 9","Maj 6",
                "Half Dim 7","Aug Maj 7","Aug 7","Dim Maj 7","7Sus4","7Sus2",
                "6/9","Min 6/9","7b9","7#9","9Sus4","7b5","7b5b9","7b5#9",
                "b5 Triad","Dom 11","Maj 11","Min 11","7#11","Maj7#11","Add 11",
                "Dom 13","Maj 13","Min 13","Dom 13 Full",
                "Dom Shell","Maj7 Shell","Min7 Shell",
                "Quartal","Quintal","Whole Tone","Cluster","Power","Dim 7","Oct 2"},
            Description = "Chord voicing")]
        public void SetChord(int value, int track) =>
            _vs.ChordType = Math.Max(0, Math.Min(ChordLib.Intervals.Length - 1, value));

        [ParameterDecl(Name = "Mode", MinValue = 0, MaxValue = 5, DefValue = 0,
            ValueDescriptions = new[]{"Chord","Arp Up","Arp Down","Arp Up+Down","Arp Down+Up","Arp Random"},
            Description = "Playback mode")]
        public void SetMode(int value, int track) =>
            _vs.Mode = Math.Max(0, Math.Min(5, value));

        [ParameterDecl(Name = "Speed", MinValue = 1, MaxValue = 1024, DefValue = 2,
            Description = "Pattern ticks between arp steps")]
        public void SetSpeed(int value, int track) =>
            _vs.Speed = Math.Max(1, value);

        [ParameterDecl(Name = "Length", MinValue = 0, MaxValue = 16384, DefValue = 0,
            Description = "Note duration in ticks (0 = no auto note-off)")]
        public void SetLength(int value, int track) =>
            _vs.Length = Math.Max(0, value);

        [ParameterDecl(Name = "Octaves", MinValue = 1, MaxValue = 4, DefValue = 1,
            Description = "Octave range")]
        public void SetOctaves(int value, int track) =>
            _vs.OctaveSpread = Math.Max(1, Math.Min(4, value));

        [ParameterDecl(Name = "Step", MinValue = 1, MaxValue = 8, DefValue = 1,
            Description = "Chord tones advanced per arp step")]
        public void SetStep(int value, int track) =>
            _vs.Step = Math.Max(1, Math.Min(8, value));

        [ParameterDecl(Name = "Oct Walk", MinValue = 0, MaxValue = 2, DefValue = 0,
            ValueDescriptions = new[]{"Off","Up","Ping-pong"},
            Description = "Octave cycling after each chord cycle")]
        public void SetOctWalk(int value, int track) =>
            _vs.OctWalk = Math.Max(0, Math.Min(2, value));

        [ParameterDecl(Name = "Swing", MinValue = 0, MaxValue = 100, DefValue = 0,
            Description = "0=straight  50=medium shuffle  100=2:1 triplet")]
        public void SetSwing(int value, int track) =>
            _vs.Swing = Math.Max(0, Math.Min(100, value));

        [ParameterDecl(Name = "Swing On", MinValue = 0, MaxValue = 1, DefValue = 0,
            Description = "0=1st beat  1=2nd beat")]
        public void SetSwingPhase(int value, int track) =>
            _vs.SwingPhaseVal = value & 1;

        [ParameterDecl(Name = "Humanize", MinValue = 0, MaxValue = 100, DefValue = 0,
            Description = "Random ±timing drift per arp step")]
        public void SetHumanize(int value, int track) =>
            _vs.Humanize = Math.Max(0, Math.Min(100, value));

        [ParameterDecl(Name = "Hum. Vel", MinValue = 0, MaxValue = 100, DefValue = 0,
            Description = "Random ±velocity variation per arp step")]
        public void SetHumanizeVel(int value, int track) =>
            _vs.HumanizeVel = Math.Max(0, Math.Min(100, value));

        [ParameterDecl(Name = "Arp Reset", MinValue = 0, MaxValue = 1, DefValue = 0,
            IsStateless = true,
            Description = "1 = restart arp from first note on this step")]
        public void SetArpReset(int value, int track)
        {
            if (value != 0) _vs.PendingReset = true;
        }

        // ── Note helpers ──────────────────────────────────────────────────────

        IParameter FindNoteParam(IMachine m)
        {
            if (m?.ParameterGroups == null) return null;
            foreach (var pg in m.ParameterGroups)
            {
                if (pg?.Parameters == null) continue;
                foreach (var p in pg.Parameters)
                    if (p?.Type == ParameterType.Note) return p;
            }
            int tgi = m.ParameterGroups.Count > 2 ? 2 : m.ParameterGroups.Count - 1;
            if (tgi >= 0)
            {
                var pg = m.ParameterGroups[tgi];
                if (pg?.Parameters != null)
                    foreach (var p in pg.Parameters)
                        if (p != null && p.MinValue == 0 && p.MaxValue >= 100) return p;
            }
            return null;
        }

        IParameter FindVelocityParam(IMachine m, IParameter noteParam)
        {
            if (m?.ParameterGroups == null) return null;
            var velNames = new[] { "volume", "velocity", "vol", "vel" };
            foreach (var pg in m.ParameterGroups)
            {
                if (pg?.Parameters == null) continue;
                foreach (var p in pg.Parameters)
                    if (p != null && p != noteParam &&
                        velNames.Contains(p.Name?.ToLowerInvariant() ?? ""))
                        return p;
            }
            return null;
        }

        void EnsureTrackCount(IMachine m, int needed)
        {
            if (m == null || m.TrackCount >= needed) return;
            try { m.TrackCount = needed; return; } catch { }
            try
            {
                var prop = m.GetType().GetProperty("TrackCount");
                if (prop?.CanWrite == true) { prop.SetValue(m, needed); }
            }
            catch { }
        }

        void FireNote(int track, int midiNote, int velocity)
        {
            if (_np == null || _tgt == null || track < 0) return;
            if (_vp != null)
                try { _vp.SetValue(track, Math.Max(_vp.MinValue,
                                             Math.Min(_vp.MaxValue, velocity))); } catch { }
            try { _np.SetValue(track, BN.FromMidi(midiNote)); } catch { return; }
            try { _tgt.SendControlChanges(); } catch { }
        }

        void FireOff(int track)
        {
            if (_np == null || _tgt == null || track < 0) return;
            try { _np.SetValue(track, BN.Off); } catch { return; }
            try { _tgt.SendControlChanges(); } catch { }
        }

        // ── Chord / arp engine ────────────────────────────────────────────────

        int[] BuildNotes(int buzzRoot, int chordType, int octaves)
        {
            int root = BN.ToMidi(buzzRoot);
            if (root < 0) return Array.Empty<int>();
            int[] ivals = ChordLib.Intervals[
                Math.Max(0, Math.Min(ChordLib.Intervals.Length - 1, chordType))];
            int count = 0;
            for (int o = 0; o < octaves; o++)
                foreach (int s in ivals)
                {
                    int n = Math.Max(0, Math.Min(119, root + s + o * 12));
                    bool dup = false;
                    for (int i = 0; i < count; i++) if (_buildBuf[i] == n) { dup = true; break; }
                    if (!dup && count < _buildBuf.Length) _buildBuf[count++] = n;
                }
            int[] result = new int[count];
            Array.Copy(_buildBuf, result, count);
            return result;
        }

        void Kill()
        {
            for (int s = 0; s < VoiceState.MaxSlots; s++)
                if (_vs.SlotOff[s] > 0) { FireOff(_vs.SlotTrack[s]); _vs.SlotOff[s] = 0; }
            _vs.Active = false;
        }

        void Start(int baseTrack)
        {
            Kill();
            if (_vs.Notes.Length == 0) return;
            _vs.Active         = true;
            _vs.ArpIdx         = (_vs.Mode == 2 || _vs.Mode == 4) ? _vs.Notes.Length - 1 : 0;
            _vs.ArpDir         = (_vs.Mode == 4) ? -1 : 1;
            _stepAcc           = 0.0;
            _vs.ArpStepParity  = 0;
            _vs.OctOffset      = 0;
            _vs.OctDir         = 1;

            if (_vs.Mode == 0)
            {
                for (int i = 0; i < _vs.Notes.Length && i < VoiceState.MaxSlots; i++)
                {
                    int t = baseTrack + i;
                    FireNote(t, _vs.Notes[i], _vs.Velocity);
                    _vs.SlotTrack[i] = t;
                    _vs.SlotOff[i]   = _vs.Length > 0 ? _vs.Length : 0;
                }
            }
            else
            {
                StepArp(baseTrack);
            }
        }

        void StepArp(int baseTrack)
        {
            if (_vs.Notes.Length == 0) return;
            int idx = (_vs.Mode == 5) ? _rng.Next(_vs.Notes.Length) : _vs.ArpIdx;
            int midi = _vs.Notes[idx];
            if (_vs.OctWalk != 0)
                midi = Math.Min(119, midi + _vs.OctOffset * 12);

            int velDrift = _vs.HumanizeVel > 0
                ? (int)Math.Round(_vs.Velocity * _vs.HumanizeVel / 200.0) : 0;
            int velJitter = velDrift > 0 ? _rng.Next(-velDrift, velDrift + 1) : 0;
            int firedVel  = Math.Max(1, Math.Min(127, _vs.Velocity + velJitter));

            FireNote(baseTrack, midi, firedVel);
            _vs.SlotTrack[0] = baseTrack;
            _vs.SlotOff[0]   = _vs.Length > 0 ? _vs.Length : 0;
            if (_vs.Mode != 5) AdvArp();

            // Step length is measured directly in fractional ticks, so timing is
            // tempo-locked regardless of BPM, TPB, or the Sub-Tick Timing setting.
            // Swing splits a 2-step span (2 * Speed ticks) into long + short whose
            // sum is exactly the span; placement resolution is the audio chunk
            // (sub-tick), giving smooth swing even at low Speed without depending
            // on the sub-tick edge stream. Humanize jitters the length in ticks.
            double period = 2.0 * _vs.Speed;                      // long + short, ticks
            double ratio  = 1.0 + _vs.Swing / 100.0;
            double longL  = period * ratio / (ratio + 1.0);
            double shortL = period - longL;
            bool   isLong = ((_vs.ArpStepParity + _vs.SwingPhaseVal) % 2 == 0);
            double len    = isLong ? longL : shortL;
            _vs.ArpStepParity = 1 - _vs.ArpStepParity;

            if (_vs.Humanize > 0)
            {
                double drift = _vs.Speed * _vs.Humanize / 200.0;  // ticks
                len += (_rng.NextDouble() * 2.0 - 1.0) * drift;
            }
            _stepLen = Math.Max(1.0 / 256.0, len);                // floor: never 0/negative
        }

        void AdvArp()
        {
            int len = _vs.Notes.Length;
            if (len <= 1) { AdvOct(); return; }
            int s = _vs.Step;
            switch (_vs.Mode)
            {
                case 1:
                    if (_vs.ArpIdx + s >= len) AdvOct();
                    _vs.ArpIdx = (_vs.ArpIdx + s) % len;
                    break;
                case 2:
                    if (_vs.ArpIdx - s < 0) AdvOct();
                    _vs.ArpIdx = ((_vs.ArpIdx - s) % len + len) % len;
                    break;
                case 3: case 4:
                    _vs.ArpIdx += _vs.ArpDir * s;
                    while (_vs.ArpIdx >= len || _vs.ArpIdx < 0)
                    {
                        if (_vs.ArpIdx >= len)
                        { _vs.ArpIdx = 2*(len-1) - _vs.ArpIdx; _vs.ArpDir = -1; AdvOct(); }
                        if (_vs.ArpIdx < 0)
                        { _vs.ArpIdx = -_vs.ArpIdx; _vs.ArpDir = +1; }
                    }
                    break;
            }
        }

        void AdvOct()
        {
            if (_vs.OctWalk == 0 || _vs.OctaveSpread <= 1) return;
            switch (_vs.OctWalk)
            {
                case 1:
                    _vs.OctOffset = (_vs.OctOffset + 1) % _vs.OctaveSpread;
                    break;
                case 2:
                    _vs.OctOffset += _vs.OctDir;
                    if (_vs.OctOffset >= _vs.OctaveSpread)
                    { _vs.OctOffset = Math.Max(0, _vs.OctaveSpread - 2); _vs.OctDir = -1; }
                    else if (_vs.OctOffset < 0)
                    { _vs.OctOffset = Math.Min(1, _vs.OctaveSpread - 1); _vs.OctDir = +1; }
                    break;
            }
        }

        // ── Work ──────────────────────────────────────────────────────────────

        public void Work()
        {
            if (Buzz == null) return;
            int pit = host?.MasterInfo?.PosInTick    ?? 0;
            int spt = host?.MasterInfo?.SamplesPerTick ?? 0;

            // Step clock driven off the musical position, not sub-tick edges. The
            // arp advances by the fraction of a tick elapsed since the last call
            // (PosInTick / SamplesPerTick), accumulated in fractional ticks. This
            // is tempo-locked by construction (a tick is a tick at any BPM) and
            // independent of the host's Sub-Tick Timing setting — unlike counting
            // CurrentSubTick edges, which the engine only delivers R-per-tick when
            // Sub-Tick Timing is on, yet still reports SubTicksPerTick > 1 when it
            // is off (so edge counts then mismatch and the arp drifts). PosInTick
            // resets only at the tick boundary, so a backwards step is one tick
            // wrap; chunks never cross a tick boundary, so at most one wrap.
            bool   first   = _prevPit == int.MinValue;
            bool   newTick = !first && pit < _prevPit;
            double dTicks  = 0.0;
            if (!first && spt > 0)
            {
                int d = pit - _prevPit;
                if (d < 0) d += spt;                 // single tick wrap
                dTicks = d / (double)spt;
            }
            _prevPit = pit;

            // _tgt/_np/_vp are resolved on the UI thread only — never touched here.
            int baseTrk = _state.BaseTrack;

            if (_vs.HasNewNote)
            {
                _vs.HasNewNote = false;
                if (_vs.PendingNote == BN.Off)
                    Kill();
                else if (_vs.PendingNote > 0)
                {
                    int octs = _vs.OctWalk != 0 ? 1 : _vs.OctaveSpread;
                    _vs.Notes = BuildNotes(_vs.PendingNote, _vs.ChordType, octs);
                    if (_tgt != null && _np != null)
                        Start(baseTrk);
                }
                return;
            }

            if (_vs.PendingReset && newTick && _vs.Active)
            {
                _vs.PendingReset  = false;
                _vs.ArpIdx        = (_vs.Mode == 2 || _vs.Mode == 4) ? _vs.Notes.Length - 1 : 0;
                _vs.ArpDir        = (_vs.Mode == 4) ? -1 : 1;
                _stepAcc          = _stepLen;   // fire promptly on reset
            }
            else _vs.PendingReset = false;

            if (!_vs.Active || _tgt == null || _np == null) return;

            // Note-offs run on the TICK clock — Length is specified in ticks.
            if (newTick)
                for (int s = 0; s < VoiceState.MaxSlots; s++)
                {
                    if (_vs.SlotOff[s] <= 0) continue;
                    if (--_vs.SlotOff[s] == 0) FireOff(_vs.SlotTrack[s]);
                }

            // Arp steps fire when the accumulated musical time reaches the current
            // step length. Normally fires 0 or 1 times per call; the loop and the
            // step-length floor in StepArp bound it. A re-seed (Start) resets the
            // accumulator and returns above, so a seed and a step can't collide.
            if (_vs.Mode != 0)
            {
                _stepAcc += dTicks;
                int guard = 0;
                while (_stepLen > 0.0 && _stepAcc >= _stepLen && guard++ < 64)
                {
                    _stepAcc -= _stepLen;
                    StepArp(baseTrk);
                }
            }
        }

        // ── Stop ──────────────────────────────────────────────────────────────

        public void Stop()
        {
            if (_vs.Active && _np != null && _tgt != null)
            {
                for (int s = 0; s < VoiceState.MaxSlots; s++)
                    if (_vs.SlotOff[s] > 0)
                        try { _np.SetValue(_vs.SlotTrack[s], BN.Off); } catch { }
                try { _tgt.SendControlChanges(); } catch { }
            }
            _vs.Active        = false;
            _vs.HasNewNote    = false;
            _vs.PendingNote   = 0;
            _vs.PendingReset  = false;
            _stepAcc          = 0.0;
            _stepLen          = 0.0;
            _prevPit          = int.MinValue;   // clean delta on next play
            _vs.ArpStepParity = 0;
            _vs.OctOffset     = 0;
            _vs.OctDir        = 1;
            for (int s = 0; s < VoiceState.MaxSlots; s++) _vs.SlotOff[s] = 0;
        }

        // ── Machine state persistence ─────────────────────────────────────────

        public PedalChordState MachineState
        {
            get => _state;
            set
            {
                if (value == null) return;
                _state = value;
                // Song.Machines may not be fully populated when MachineState is
                // set on load — schedule retries until the target resolves.
                ScheduleResolve();
            }
        }

        // ── Right-click commands ──────────────────────────────────────────────

        public IEnumerable<IMenuItem> Commands
        {
            get => new IMenuItem[]
            {
                new MenuEntry(0, "Target Settings\u2026", OpenSettings),
                new MenuEntry(1, "About\u2026",           ShowAbout),
                new MenuEntry(2, "Chord Reference\u2026", ShowChordRef),
                new MenuEntry(3, "Diagnostics\u2026",     ShowDiagnostics),
            };
        }

        public void Command(int index)
        {
            switch (index)
            {
                case 0: OpenSettings();    break;
                case 1: ShowAbout();       break;
                case 2: ShowChordRef();    break;
                case 3: ShowDiagnostics(); break;
            }
        }

        void OpenSettings()
        {
            // Snapshot machine names on the calling thread (safe — still on UI thread
            // since Command() is called by ReBuzz from the UI thread).
            List<string> names;
            try
            {
                names = Buzz?.Song?.Machines?
                    .Where(m => m.Name != host?.Machine?.Name)
                    .Select(m => m.Name)
                    .OrderBy(n => n)
                    .ToList() ?? new List<string>();
            }
            catch { names = new List<string>(); }

            PedalChordState stateSnap = _state;

            // Open on a dedicated STA thread — completely independent of the
            // ReBuzz dispatcher so ShowDialog() cannot affect the host pump.
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    var win = new TargetSettingsWindow(stateSnap, names);
                    bool ok = win.ShowDialog() == true;
                    if (ok)
                    {
                        _state = win.Result;
                        Application.Current?.Dispatcher?.BeginInvoke(
                            (Action)ResolveCache);
                    }
                }
                catch (Exception ex)
                {
                    try { MessageBox.Show(ex.Message, "Pedal Chord – Settings Error",
                        MessageBoxButton.OK, MessageBoxImage.Error); } catch { }
                }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        void ShowAbout()
        {
            Application.Current?.Dispatcher?.BeginInvoke((Action)(() =>
                MessageBox.Show(
                    "Pedal Chord v1.5\n\nChord and arpeggio trigger for ReBuzz.\n\n" +
                    "Use multiple instances to control multiple generators simultaneously.",
                    "About Pedal Chord",
                    MessageBoxButton.OK, MessageBoxImage.Information)));
        }

        void ShowChordRef() =>
            Application.Current?.Dispatcher?.BeginInvoke(
                (Action)(() => new ChordReferenceWindow().Show()));

        void ShowDiagnostics()
        {
            // Use cached values — no Song.Machines access here
            string msg = string.Format(
                "Target=[{0}]  resolved={1}  noteParam={2} (hash={3})  tracks={4}  active={5}",
                _state.TargetMachine,
                _tgt != null ? "YES" : "NO - not in Song.Machines",
                _np  != null ? "YES" : "NO - not found",
                _np?.GetHashCode().ToString() ?? "n/a",
                _tgt?.TrackCount ?? -1,
                _vs.Active);
            try
            {
                foreach (string line in msg.Split('\n'))
                    if (line.Trim().Length > 0)
                        Buzz?.DCWriteLine("[PedalChord] " + line.Trim());
                Application.Current?.Dispatcher?.BeginInvoke((Action)(() =>
                { try { Buzz?.ExecuteCommand(BuzzCommand.DebugConsole); } catch { } }));
            }
            catch { }
        }
    }

    // =========================================================================
    // Target settings dialog — single target
    // =========================================================================

    class TargetSettingsWindow : Window
    {
        public PedalChordState Result { get; private set; }

        readonly ComboBox _machBox = new ComboBox { Width = 220, Margin = new Thickness(4,0,0,0) };
        readonly ComboBox _baseTrk = new ComboBox { Width = 220, Margin = new Thickness(4,0,0,0) };
        readonly PedalChordState _work;
        bool _loading = false;

        public TargetSettingsWindow(PedalChordState state, List<string> machineNames)
        {
            _work  = new PedalChordState
            {
                TargetMachine = state?.TargetMachine ?? "",
                BaseTrack     = state?.BaseTrack     ?? 0
            };
            Result = _work;

            Title                 = "Pedal Chord \u2013 Target Settings";
            Width                 = 400;
            SizeToContent         = SizeToContent.Height;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Content               = BuildUI(machineNames);
            Loaded               += (s, e) => Populate(machineNames);
        }

        UIElement BuildUI(List<string> machineNames)
        {
            var outer = new StackPanel { Margin = new Thickness(12) };
            outer.Children.Add(MakeRow("Target machine:",   _machBox));
            outer.Children.Add(MakeRow("Base track index:", _baseTrk));
            outer.Children.Add(new TextBlock
            {
                Text         = "Chord mode fires consecutive target tracks from Base.\n" +
                               "Arpeggio modes use the Base Track only.",
                Foreground   = Brushes.Gray,
                Margin       = new Thickness(0, 8, 0, 12),
                TextWrapping = TextWrapping.Wrap,
                FontSize     = 11
            });

            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var btnOK  = new Button { Content = "OK",     Width = 80, Margin = new Thickness(0,0,6,0), IsDefault = true };
            var btnCan = new Button { Content = "Cancel", Width = 80, IsCancel = true };
            btnOK.Click  += (s, e) => { Save(); DialogResult = true; };
            btnCan.Click += (s, e) => { DialogResult = false; };
            btnRow.Children.Add(btnOK);
            btnRow.Children.Add(btnCan);
            outer.Children.Add(btnRow);
            _machBox.SelectionChanged += (s, e) => { if (!_loading) Save(); };
            _baseTrk.SelectionChanged += (s, e) => { if (!_loading) Save(); };
            return outer;
        }

        static StackPanel MakeRow(string label, UIElement ctrl)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,6) };
            row.Children.Add(new TextBlock
            {
                Text              = label,
                Width             = 120,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment     = TextAlignment.Right,
                Margin            = new Thickness(0,0,8,0)
            });
            row.Children.Add(ctrl);
            return row;
        }

        void Populate(List<string> machineNames)
        {
            _loading = true;
            _machBox.Items.Clear();
            _machBox.Items.Add("(none)");
            foreach (string n in machineNames) _machBox.Items.Add(n);
            int mi = _machBox.Items.IndexOf(_work.TargetMachine);
            _machBox.SelectedIndex = mi > 0 ? mi : 0;

            _baseTrk.Items.Clear();
            for (int i = 0; i < 64; i++) _baseTrk.Items.Add(string.Format("Track {0}", i));
            _baseTrk.SelectedIndex = Math.Max(0, Math.Min(63, _work.BaseTrack));
            _loading = false;
        }

        void Save()
        {
            _work.TargetMachine = _machBox.SelectedIndex <= 0
                ? "" : _machBox.SelectedItem?.ToString() ?? "";
            _work.BaseTrack     = Math.Max(0, Math.Min(63, _baseTrk.SelectedIndex));
        }
    }

    // =========================================================================
    // Menu helpers
    // =========================================================================

    sealed class MenuEntry : IMenuItem, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        readonly int _id; readonly string _text; readonly Action _action;
        public MenuEntry(int id, string text, Action action)
        { _id = id; _text = text; _action = action; }
        public int    ID               => _id;
        public string Text             => _text;
        public string GestureText      => null;
        public object CommandParameter => null;
        public ICommand Command        => new RelayCmd(() => _action?.Invoke());
        public bool   IsEnabled        { get; set; } = true;
        public bool   IsChecked        { get; set; } = false;
        public bool   IsSeparator      { get; set; } = false;
        public bool   IsLabel          { get; set; } = false;
        public bool   IsCheckable      { get; set; } = false;
        public bool   IsDefault        { get; set; } = false;
        public bool   StaysOpenOnClick { get; set; } = false;
        public IEnumerable<IMenuItem> Children => null;
        public void   Invoke() => _action?.Invoke();
    }

    sealed class RelayCmd : ICommand
    {
        readonly Action _exec;
        public RelayCmd(Action exec) { _exec = exec; }
        public event EventHandler CanExecuteChanged;
        public bool CanExecute(object p) => true;
        public void Execute(object p)    => _exec();
    }

    // =========================================================================
    // Chord Reference window
    // =========================================================================

    sealed class ChordReferenceWindow : Window
    {
        static readonly string[] NoteNames =
            { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        static string IntervalsToNotes(int[] semitones)
        {
            return string.Join(" ", semitones.Select(s =>
            {
                string n = NoteNames[s % 12];
                int oct  = s / 12;
                return oct > 0 ? n + "+" + oct : n;
            }));
        }

        static string IntervalsLabel(int[] ivals) =>
            string.Join(" ", ivals.Select(i => i.ToString()));

        static TextBlock TB(string text, HorizontalAlignment ha,
                            Brush fg, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text                = text,
                HorizontalAlignment = ha,
                Foreground          = fg,
                Margin              = new Thickness(4, 1, 4, 1),
            };
            if (bold) tb.FontWeight = FontWeights.Bold;
            return tb;
        }

        public ChordReferenceWindow()
        {
            Title                 = "Pedal Chord \u2013 Chord Reference";
            Width                 = 560;
            Height                = 600;
            ResizeMode            = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var sections = new[] {
                ("Triads",    new[]{0,1,5,6,7,30}),
                ("Seventh",   new[]{2,3,4,16,18,19,9}),
                ("Sixth",     new[]{15}),
                ("Ninth",     new[]{10,11,12,13,14}),
                ("Altered",   new[]{24,25,27,28,29}),
                ("Eleventh",  new[]{31,32,33,34,35,36}),
                ("Thirteenth",new[]{37,38,39,40}),
                ("Shell",     new[]{41,42,43}),
                ("Suspended", new[]{7,8,20,21,26}),
                ("Exotic",    new[]{44,45,46,47,48,49,50}),
            };

            var sp = new StackPanel { Margin = new Thickness(4) };
            sp.Children.Add(new TextBlock
            {
                Text       = "Dec and Hex columns show the value to type in the pattern editor. Intervals relative to root (C shown as example).",
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(4, 4, 4, 8),
                Foreground = SystemColors.GrayTextBrush,
                FontSize   = 11
            });

            var seenIdx = new HashSet<int>();
            foreach (var (sectionName, indices) in sections)
            {
                sp.Children.Add(new TextBlock
                {
                    Text       = sectionName,
                    FontWeight = FontWeights.Bold,
                    Margin     = new Thickness(4, 8, 4, 2),
                    Foreground = SystemColors.ControlTextBrush,
                });

                foreach (int ci in indices)
                {
                    if (ci < 0 || ci >= ChordLib.Names.Length) continue;
                    if (seenIdx.Contains(ci)) continue;
                    seenIdx.Add(ci);

                    int[]  ivals = ChordLib.Intervals[ci];
                    string name  = ChordLib.Names[ci];

                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    void Add(int col, string text, HorizontalAlignment ha, Brush fg, bool bold = false)
                    {
                        var tb = TB(text, ha, fg, bold);
                        Grid.SetColumn(tb, col);
                        row.Children.Add(tb);
                    }

                    Add(0, ci.ToString(),          HorizontalAlignment.Right,  SystemColors.GrayTextBrush);
                    Add(1, ci.ToString("X2"),       HorizontalAlignment.Center, Brushes.DarkOrange);
                    Add(2, name,                    HorizontalAlignment.Left,   SystemColors.ControlTextBrush, true);
                    Add(3, IntervalsToNotes(ivals), HorizontalAlignment.Left,   SystemColors.ControlTextBrush);
                    Add(4, IntervalsLabel(ivals),   HorizontalAlignment.Left,   SystemColors.GrayTextBrush);

                    sp.Children.Add(row);
                }
            }

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = sp
            };
        }
    }
}
