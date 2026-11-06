// PedalM1.cs — main machine class.
//
// 8-voice polyphonic generator. Each pattern track maps to one voice
// slot (track index = voice index). Voice signal flow per Voice.cs:
//   OSC1+OSC2 → mix → VDF (lowpass) → VDA → output.
// Three modulation sources: amp env, filter env, two LFOs.
//
// Setters store pending events; Work() drains them, updates coefficients
// at control rate (every 16 samples), runs DSP, writes to ±32768 stereo.
//
// Multi-track simultaneous note delivery is handled by the §14 polling
// workaround — without this, simultaneous chord notes get dropped to
// last-track-only.
//
// Transport stop is handled via the falling-edge fade pattern (Core §27).

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;   // fork (neo-buzz): MethodImplOptions.AggressiveOptimization
using BuzzGUI.Interfaces;
using Buzz.MachineInterface;

namespace WDE.PedalM1
{
    [MachineDecl(
        Name        = "Pedal M1",
        ShortName   = "M1",
        Author      = "WDE",
        InputCount  = 0,
        OutputCount = 2,
        MaxTracks   = MAX_VOICES_CONST)]
    public class PedalM1Machine : IBuzzMachine
    {
        public const int MAX_VOICES_CONST = 8;
        const int   FILTER_UPDATE_MASK = 16 - 1;     // SH101 §2 — control-rate
        const float SAMPLE_SCALE = 32768f;            // PedalComp §1

        readonly IBuzzMachineHost host;
        IBuzz                     _buzz;
        WavetableAccess           _wt;
        readonly Voice[]          _voices;
        bool                      _wasPlaying;

        // ── Lazy parameter polling (Core §14, §15) ──────────────────────
        // pvalues readers tolerate both backing shapes — ConcurrentDictionary
        // <int,int> (ReBuzz ≤1818) and int[] (ReBuzz ≥1827). See Tracker §16:
        // a hard `as ConcurrentDictionary` cast returns null on the int[] form
        // and silently kills all multi-track playback.
        IParameter _ownNoteParam;
        IParameter _ownVolumeParam;
        Func<int,int> _ownNotePValues;
        Func<int,int> _ownVolumePValues;
        bool _pollReady;

        // ── Fork (neo-buzz, docs/specs/mcp-server.md §12.15): MIDI notes and controllers (MidiIn.cs) ───────
        // A MIDI note takes a free voice (the highest first: tracks take the first ones), else the one released
        // longest ago, else the oldest, and plays at its own velocity (velocity / 127, as the Volume column's).
        // Pitch bend ±2 semitones, the mod wheel brings in LFO1's vibrato up to ±1 semitone (LFO1 to Pitch's full
        // depth, the M1's joystick), the sustain pedal holds MIDI notes. A track's note plays exactly as before.
        readonly MidiIn     _midi          = new MidiIn();
        readonly MidiVoices _midiVoices    = new MidiVoices(MAX_VOICES_CONST);
        readonly bool[]     _voiceFree     = new bool[MAX_VOICES_CONST];
        readonly bool[]     _voiceReleased = new bool[MAX_VOICES_CONST];
        readonly bool[]     _voiceSkip     = new bool[MAX_VOICES_CONST];   // idle or finished for this whole block (Voice.Finished)

        // ── Fork (neo-buzz, §12.15): Volume, the oscillators' levels, Cutoff (as its 0…1 position) and Resonance
        // glide to a new setting over 20 ms (ParamGlide, below); a setting that doesn't move is the same number as
        // before. The first block, and a new sample rate, start at the setting.
        ParamGlide _gVolume, _gLevel1, _gLevel2, _gCutoff, _gReso;
        bool _glidesSet;
        int  _glideSr;

        public PedalM1Machine(IBuzzMachineHost host)
        {
            this.host = host;
            _voices = new Voice[MAX_VOICES_CONST];
            for (int i = 0; i < MAX_VOICES_CONST; i++)
                _voices[i] = new Voice();
        }

        IBuzz Buzz
        {
            get
            {
                if (_buzz != null) return _buzz;
                try { _buzz = host?.Machine?.Graph?.Buzz; } catch { }
                return _buzz;
            }
        }

        WavetableAccess WT
        {
            get
            {
                if (_wt != null) return _wt;
                var b = Buzz;
                if (b != null) _wt = new WavetableAccess(b);
                return _wt;
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Global parameters — declaration order matters for preset bundles.
        // (Build §3.3 — append-only after release.)
        // ════════════════════════════════════════════════════════════════

        // ── OSC1 ────────────────────────────────────────────────────────
        [ParameterDecl(Name = "OSC1 Wave",
            MinValue = 0, MaxValue = 200, DefValue = 1, IsWaveNumber = true,
            Description = "OSC1 wavetable index (0 = off)")]
        public int Osc1Wave { get; set; } = 1;

        [ParameterDecl(Name = "OSC1 Octave",
            MinValue = 0, MaxValue = 4, DefValue = 2,
            ValueDescriptions = new[] { "-2", "-1", "0", "+1", "+2" },
            Description = "OSC1 octave shift")]
        public int Osc1Octave { get; set; } = 2;

        [ParameterDecl(Name = "OSC1 Transpose",
            MinValue = 0, MaxValue = 24, DefValue = 12,
            Description = "OSC1 semitone transpose; 12 is unity")]
        public int Osc1Transpose { get; set; } = 12;

        [ParameterDecl(Name = "OSC1 Detune",
            MinValue = 0, MaxValue = 100, DefValue = 50,
            Description = "OSC1 fine detune; 50 is unity, range -50..+50 cents")]
        public int Osc1Detune { get; set; } = 50;

        [ParameterDecl(Name = "OSC1 Level",
            MinValue = 0, MaxValue = 127, DefValue = 100,
            Description = "OSC1 mix level")]
        public int Osc1Level { get; set; } = 100;

        // ── OSC2 ────────────────────────────────────────────────────────
        [ParameterDecl(Name = "OSC2 Wave",
            MinValue = 0, MaxValue = 200, DefValue = 0, IsWaveNumber = true,
            Description = "OSC2 wavetable index (0 = off)")]
        public int Osc2Wave { get; set; } = 0;

        [ParameterDecl(Name = "OSC2 Octave",
            MinValue = 0, MaxValue = 4, DefValue = 2,
            ValueDescriptions = new[] { "-2", "-1", "0", "+1", "+2" },
            Description = "OSC2 octave shift")]
        public int Osc2Octave { get; set; } = 2;

        [ParameterDecl(Name = "OSC2 Transpose",
            MinValue = 0, MaxValue = 24, DefValue = 12,
            Description = "OSC2 semitone transpose; 12 is unity")]
        public int Osc2Transpose { get; set; } = 12;

        [ParameterDecl(Name = "OSC2 Detune",
            MinValue = 0, MaxValue = 100, DefValue = 53,
            Description = "OSC2 fine detune; 50 is unity, range -50..+50 cents")]
        public int Osc2Detune { get; set; } = 53;       // slight default detune

        [ParameterDecl(Name = "OSC2 Level",
            MinValue = 0, MaxValue = 127, DefValue = 100,
            Description = "OSC2 mix level")]
        public int Osc2Level { get; set; } = 100;

        // ── Mix ─────────────────────────────────────────────────────────
        [ParameterDecl(Name = "OSC Mode",
            MinValue = 0, MaxValue = 2, DefValue = 0,
            ValueDescriptions = new[] { "OSC1 only", "OSC2 only", "Both" },
            Description = "Oscillator routing mode")]
        public int OscMode { get; set; } = 0;

        // ── Pitch ───────────────────────────────────────────────────────
        [ParameterDecl(Name = "Portamento",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Portamento time; 0 is off")]
        public int Portamento { get; set; } = 0;

        // ── VDF ─────────────────────────────────────────────────────────
        [ParameterDecl(Name = "Cutoff",
            MinValue = 0, MaxValue = 127, DefValue = 127,
            Description = "Filter cutoff; 0 closed, 127 fully open")]
        public int Cutoff { get; set; } = 127;

        [ParameterDecl(Name = "Resonance",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Filter resonance; gentle range, never self-oscillates")]
        public int Resonance { get; set; } = 0;

        [ParameterDecl(Name = "Key Track",
            MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "Filter cutoff key tracking; 64 is none, 127 full, 0 inverse")]
        public int KeyTrack { get; set; } = 64;

        [ParameterDecl(Name = "Filter EG Int",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Filter envelope intensity")]
        public int FilterEgInt { get; set; } = 0;

        [ParameterDecl(Name = "Filter Vel",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Velocity to filter cutoff")]
        public int FilterVel { get; set; } = 0;

        // ── Filter envelope ─────────────────────────────────────────────
        [ParameterDecl(Name = "Filter Attack",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Filter envelope attack time")]
        public int FilterAttack { get; set; } = 0;

        [ParameterDecl(Name = "Filter Decay",
            MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "Filter envelope decay time")]
        public int FilterDecay { get; set; } = 64;

        [ParameterDecl(Name = "Filter Sustain",
            MinValue = 0, MaxValue = 127, DefValue = 100,
            Description = "Filter envelope sustain level")]
        public int FilterSustain { get; set; } = 100;

        [ParameterDecl(Name = "Filter Release",
            MinValue = 0, MaxValue = 127, DefValue = 40,
            Description = "Filter envelope release time")]
        public int FilterRelease { get; set; } = 40;

        // ── Amp envelope ────────────────────────────────────────────────
        [ParameterDecl(Name = "Amp Attack",
            MinValue = 0, MaxValue = 127, DefValue = 10,
            Description = "Amp envelope attack time")]
        public int AmpAttack { get; set; } = 10;

        [ParameterDecl(Name = "Amp Decay",
            MinValue = 0, MaxValue = 127, DefValue = 80,
            Description = "Amp envelope decay time")]
        public int AmpDecay { get; set; } = 80;

        [ParameterDecl(Name = "Amp Sustain",
            MinValue = 0, MaxValue = 127, DefValue = 127,
            Description = "Amp envelope sustain level")]
        public int AmpSustain { get; set; } = 127;

        [ParameterDecl(Name = "Amp Release",
            MinValue = 0, MaxValue = 127, DefValue = 40,
            Description = "Amp envelope release time")]
        public int AmpRelease { get; set; } = 40;

        [ParameterDecl(Name = "Amp Vel",
            MinValue = 0, MaxValue = 127, DefValue = 80,
            Description = "Velocity to amp level; 0 ignores velocity")]
        public int AmpVel { get; set; } = 80;

        // ── LFO 1 ───────────────────────────────────────────────────────
        [ParameterDecl(Name = "LFO1 Wave",
            MinValue = 0, MaxValue = 4, DefValue = 0,
            ValueDescriptions = new[] { "Triangle", "Sawtooth", "Square", "S+H", "Smooth" },
            Description = "LFO1 waveform")]
        public int Lfo1Wave { get; set; } = 0;

        [ParameterDecl(Name = "LFO1 Speed",
            MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "LFO1 speed; 0 slow, 127 fast")]
        public int Lfo1Speed { get; set; } = 64;

        [ParameterDecl(Name = "LFO1 Delay",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO1 delay time after note-on")]
        public int Lfo1Delay { get; set; } = 0;

        [ParameterDecl(Name = "LFO1 Fade",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO1 fade-in time")]
        public int Lfo1Fade { get; set; } = 0;

        [ParameterDecl(Name = "LFO1 to Pitch",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO1 pitch modulation depth (0 to ~1 semitone)")]
        public int Lfo1ToPitch { get; set; } = 0;

        [ParameterDecl(Name = "LFO1 to VDF",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO1 filter modulation depth")]
        public int Lfo1ToVdf { get; set; } = 0;

        [ParameterDecl(Name = "LFO1 to VDA",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO1 amp modulation depth (tremolo)")]
        public int Lfo1ToVda { get; set; } = 0;

        // ── LFO 2 ───────────────────────────────────────────────────────
        [ParameterDecl(Name = "LFO2 Wave",
            MinValue = 0, MaxValue = 4, DefValue = 2,
            ValueDescriptions = new[] { "Triangle", "Sawtooth", "Square", "S+H", "Smooth" },
            Description = "LFO2 waveform")]
        public int Lfo2Wave { get; set; } = 2;

        [ParameterDecl(Name = "LFO2 Speed",
            MinValue = 0, MaxValue = 127, DefValue = 32,
            Description = "LFO2 speed")]
        public int Lfo2Speed { get; set; } = 32;

        [ParameterDecl(Name = "LFO2 Delay",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO2 delay time after note-on")]
        public int Lfo2Delay { get; set; } = 0;

        [ParameterDecl(Name = "LFO2 Fade",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO2 fade-in time")]
        public int Lfo2Fade { get; set; } = 0;

        [ParameterDecl(Name = "LFO2 to Pitch",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO2 pitch modulation depth")]
        public int Lfo2ToPitch { get; set; } = 0;

        [ParameterDecl(Name = "LFO2 to VDF",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO2 filter modulation depth")]
        public int Lfo2ToVdf { get; set; } = 0;

        [ParameterDecl(Name = "LFO2 to VDA",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO2 amp modulation depth")]
        public int Lfo2ToVda { get; set; } = 0;

        // ── Master ──────────────────────────────────────────────────────
        [ParameterDecl(Name = "Volume",
            MinValue = 0, MaxValue = 127, DefValue = 100,
            Description = "Master output volume")]
        public int Volume { get; set; } = 100;

        // ── Multisample zones (v1.1) ────────────────────────────────────
        // Each OSC has up to 4 zones. The existing OSCx Wave (declared
        // above) is the primary/fallback zone. Zones 2/3/4 take over for
        // notes >= their Split value, with higher zones overriding lower.
        // Zone is disabled when its Wave is 0. Per-OSC Octave/Transpose/
        // Detune/Level apply globally (M1 convention — multisample-level).
        // Zone selection is fixed at note-on: a portamento glide does not
        // change the zone mid-note.

        [ParameterDecl(Name = "OSC1 Wave 2",
            MinValue = 0, MaxValue = 200, DefValue = 0, IsWaveNumber = true,
            Description = "OSC1 zone 2 wave; 0 = off")]
        public int Osc1Wave2 { get; set; } = 0;

        [ParameterDecl(Name = "OSC1 Split 2",
            MinValue = 0, MaxValue = 127, DefValue = 60,
            Description = "MIDI note where OSC1 zone 2 takes over (default C4 = 60)")]
        public int Osc1Split2 { get; set; } = 60;

        [ParameterDecl(Name = "OSC1 Wave 3",
            MinValue = 0, MaxValue = 200, DefValue = 0, IsWaveNumber = true,
            Description = "OSC1 zone 3 wave; 0 = off")]
        public int Osc1Wave3 { get; set; } = 0;

        [ParameterDecl(Name = "OSC1 Split 3",
            MinValue = 0, MaxValue = 127, DefValue = 72,
            Description = "MIDI note where OSC1 zone 3 takes over (default C5 = 72)")]
        public int Osc1Split3 { get; set; } = 72;

        [ParameterDecl(Name = "OSC1 Wave 4",
            MinValue = 0, MaxValue = 200, DefValue = 0, IsWaveNumber = true,
            Description = "OSC1 zone 4 wave; 0 = off")]
        public int Osc1Wave4 { get; set; } = 0;

        [ParameterDecl(Name = "OSC1 Split 4",
            MinValue = 0, MaxValue = 127, DefValue = 84,
            Description = "MIDI note where OSC1 zone 4 takes over (default C6 = 84)")]
        public int Osc1Split4 { get; set; } = 84;

        [ParameterDecl(Name = "OSC2 Wave 2",
            MinValue = 0, MaxValue = 200, DefValue = 0, IsWaveNumber = true,
            Description = "OSC2 zone 2 wave; 0 = off")]
        public int Osc2Wave2 { get; set; } = 0;

        [ParameterDecl(Name = "OSC2 Split 2",
            MinValue = 0, MaxValue = 127, DefValue = 60,
            Description = "MIDI note where OSC2 zone 2 takes over (default C4 = 60)")]
        public int Osc2Split2 { get; set; } = 60;

        [ParameterDecl(Name = "OSC2 Wave 3",
            MinValue = 0, MaxValue = 200, DefValue = 0, IsWaveNumber = true,
            Description = "OSC2 zone 3 wave; 0 = off")]
        public int Osc2Wave3 { get; set; } = 0;

        [ParameterDecl(Name = "OSC2 Split 3",
            MinValue = 0, MaxValue = 127, DefValue = 72,
            Description = "MIDI note where OSC2 zone 3 takes over (default C5 = 72)")]
        public int Osc2Split3 { get; set; } = 72;

        [ParameterDecl(Name = "OSC2 Wave 4",
            MinValue = 0, MaxValue = 200, DefValue = 0, IsWaveNumber = true,
            Description = "OSC2 zone 4 wave; 0 = off")]
        public int Osc2Wave4 { get; set; } = 0;

        [ParameterDecl(Name = "OSC2 Split 4",
            MinValue = 0, MaxValue = 127, DefValue = 84,
            Description = "MIDI note where OSC2 zone 4 takes over (default C6 = 84)")]
        public int Osc2Split4 { get; set; } = 84;

        // ════════════════════════════════════════════════════════════════
        // Track parameters — Note + Volume (velocity).
        // ════════════════════════════════════════════════════════════════

        [ParameterDecl(Name = "Note", IsStateless = true,
            Description = "Trigger note; z=C-4, s=C#-4 etc")]
        public void SetNote(Note value, int track)
        {
            if ((uint)track >= MAX_VOICES_CONST) return;
            byte raw = value.Value;

            if (raw == 0) return;                               // NoValue
            if (raw == Note.Off)
            {
                _voices[track].HasNoteOff = true;
                PollSiblingTracks(track);
                return;
            }

            _voices[track].HasNoteOn = true;
            _voices[track].PendingBuzzNote = raw;
            PollSiblingTracks(track);
        }

        [ParameterDecl(Name = "Volume",
            MinValue = 0, MaxValue = 127, DefValue = 127,
            Description = "Note velocity; 0..127, 127 is full")]
        public void SetTrackVolume(int value, int track)
        {
            if ((uint)track >= MAX_VOICES_CONST) return;
            _voices[track].HasNewVelocity = true;
            _voices[track].PendingVolumeRaw = (byte)value;
            PollSiblingTracks(track);
        }

        // ── §14 / §15 polling workaround ────────────────────────────────
        void EnsurePollFields()
        {
            if (_pollReady) return;
            try
            {
                var pg = host?.Machine?.ParameterGroups;
                if (pg == null || pg.Count < 3) return;   // groups not ready — retry next call
                var trackGroup = pg[2];
                if (trackGroup?.Parameters == null) return;

                if (_ownNoteParam == null)
                    _ownNoteParam = trackGroup.Parameters
                        .FirstOrDefault(p => p?.Type == ParameterType.Note);
                if (_ownVolumeParam == null)
                    _ownVolumeParam = trackGroup.Parameters
                        .FirstOrDefault(p => p != null && p.Type != ParameterType.Note);

                if (_ownNotePValues == null && _ownNoteParam != null)
                    _ownNotePValues = GetPValuesReader(_ownNoteParam);
                if (_ownVolumePValues == null && _ownVolumeParam != null)
                    _ownVolumePValues = GetPValuesReader(_ownVolumeParam);

                // Note reader is the essential one. Once it's live, stop
                // retrying the reflection on every setter call.
                if (_ownNotePValues != null) _pollReady = true;
            }
            catch { }
        }

        // Returns a reader: track → raw pvalue, or the parameter's NoValue if
        // the track has no entry / is out of range. Tolerates both pvalues
        // backing shapes (Tracker §16):
        //   ReBuzz ≤1818 : ConcurrentDictionary<int,int>
        //   ReBuzz ≥1827 : int[]   (the `as Dictionary` cast used to null here)
        static Func<int,int> GetPValuesReader(IParameter p)
        {
            try
            {
                var fi = p.GetType().GetField("pvalues",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi == null) return null;
                object raw = fi.GetValue(p);
                if (raw == null) return null;
                int noValue = p.NoValue;

                if (raw is int[] arr)
                    return track => ((uint)track < (uint)arr.Length) ? arr[track] : noValue;

                if (raw is ConcurrentDictionary<int,int> dict)
                    return track => dict.TryGetValue(track, out int v) ? v : noValue;

                return null;   // unknown shape — degrade to monophonic-per-track
            }
            catch { return null; }
        }

        // Poll all sibling tracks' Note + Volume pvalues to recover
        // simultaneous-track writes that parametersChanged dropped.
        // Guarded by HasNewXxx flags — a real setter call earlier in this
        // tick takes precedence over a poll.
        void PollSiblingTracks(int firedTrack)
        {
            EnsurePollFields();
            if (_ownNotePValues == null && _ownVolumePValues == null) return;

            int noteNoVal = _ownNoteParam?.NoValue ?? 0;
            int volNoVal  = _ownVolumeParam?.NoValue ?? 255;

            for (int t = 0; t < MAX_VOICES_CONST; t++)
            {
                var v = _voices[t];

                if (t != firedTrack && _ownNotePValues != null && !v.HasNoteOn && !v.HasNoteOff)
                {
                    int pv = _ownNotePValues(t);
                    if (pv != noteNoVal && pv != 0)
                    {
                        if (pv == 255) v.HasNoteOff = true;
                        else
                        {
                            v.HasNoteOn = true;
                            v.PendingBuzzNote = (byte)pv;
                        }
                    }
                }

                if (_ownVolumePValues != null && !v.HasNewVelocity)
                {
                    int pv = _ownVolumePValues(t);
                    if (pv != volNoVal)
                    {
                        v.HasNewVelocity = true;
                        v.PendingVolumeRaw = (byte)pv;
                    }
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        // Fork (neo-buzz, docs/specs/mcp-server.md §12.15): MIDI from ReBuzz
        // (a keyboard, a pattern's MIDI column), queued and applied at the
        // start of the next block.
        // ════════════════════════════════════════════════════════════════

        /// <summary>ReBuzz: a MIDI note for this machine (velocity 0 releases).</summary>
        public void MidiNote(int channel, int value, int velocity) => _midi.Note(value, velocity);

        /// <summary>ReBuzz: a MIDI controller, pitch bend (0xE0 / 0xFF) or channel pressure (0xD0 / 0xFC).</summary>
        public void MidiControlChange(int ctrl, int channel, int value) => _midi.Controller(ctrl, value);

        void ProcessMidi()
        {
            while (_midi.Next(out int kind, out int a, out int b))
            {
                if (kind == MidiIn.NoteOn) MidiNoteOn(a, b);
                else if (kind == MidiIn.NoteOff) MidiNoteOff(a);
                else switch (a)
                {
                    case MidiIn.AllNotesOff:
                        ReleaseMidiVoices();
                        break;
                    case MidiIn.AllSoundOff:
                        ReleaseMidiVoices();
                        ForceFadeAllVoices();
                        break;
                    default:
                        if (_midi.Apply(a, b))   // the sustain pedal came up
                            for (int v = 0; v < MAX_VOICES_CONST; v++)
                                if (_midiVoices.Sustained[v]) ReleaseMidiVoice(v);
                        break;
                }
            }
        }

        void MidiNoteOn(int key, int velocity)
        {
            int t = _midiVoices.Playing(key, MAX_VOICES_CONST);   // the same key again: its voice
            if (t < 0)
            {
                for (int i = 0; i < MAX_VOICES_CONST; i++)
                {
                    var voice = _voices[i];
                    _voiceFree[i]     = !voice.IsActive || voice.Finished;
                    _voiceReleased[i] = !voice.HasPendingTrigger
                                     && (!voice.AmpEnv.IsActive || voice.AmpEnv.CurrentStage == Envelope.Stage.Release);
                }
                t = _midiVoices.Pick(MAX_VOICES_CONST, _voiceFree, _voiceReleased);
            }
            // A Buzz note byte for the key (A4 = 69 is A-5, 0x5A), as a track's note gives it.
            byte buzz = (byte)(((key / 12) << 4) | (key % 12 + 1));
            TriggerVoice(t, buzz, true, velocity / 127f);
            _midiVoices.Start(t, key);
        }

        void MidiNoteOff(int key)
        {
            int t = _midiVoices.Playing(key, MAX_VOICES_CONST);
            if (t < 0 || !_midiVoices.Held[t]) return;
            if (_midi.Pedal)
            {
                _midiVoices.Held[t]      = false;
                _midiVoices.Sustained[t] = true;
                return;
            }
            ReleaseMidiVoice(t);
        }

        void ReleaseMidiVoice(int t)
        {
            var v = _voices[t];
            // A note still waiting for its voice's fade-out doesn't start after its own note off.
            if (v.HasPendingTrigger && v.PendingFromMidi) v.HasPendingTrigger = false;
            v.AmpEnv.NoteOff();
            v.FilterEnv.NoteOff();
            _midiVoices.Clear(t);
        }

        void ReleaseMidiVoices()
        {
            for (int t = 0; t < MAX_VOICES_CONST; t++)
                if (_midiVoices.Key[t] >= 0) ReleaseMidiVoice(t);
        }

        // ════════════════════════════════════════════════════════════════
        // Work — generator. Writes ±32768 directly (PedalComp §1).
        // ════════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // fork (neo-buzz): never compiled on the audio thread
        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            int sr = host?.MasterInfo?.SamplesPerSec ?? 48000;

            // Transport stop falling-edge (Core §27).
            bool nowPlaying = _wasPlaying;
            try { nowPlaying = Buzz?.Playing ?? false; } catch { }
            if (_wasPlaying && !nowPlaying)
            {
                ForceFadeAllVoices();
                for (int t = 0; t < MAX_VOICES_CONST; t++) _midiVoices.Clear(t);   // fork: MIDI notes too
            }
            _wasPlaying = nowPlaying;

            // Fork (neo-buzz, §12.15): with no voice sounding, a changed setting needs no glide: the next note starts at it.
            bool wasIdle = true;
            for (int t = 0; t < MAX_VOICES_CONST; t++)
                if (!_voices[t].Finished) { wasIdle = false; break; }

            // Drain pending pattern events.
            DrainPendingEvents(sr);
            ProcessMidi();              // fork (neo-buzz, §12.15): after the tracks' notes

            // Resolve cached coefficients that are constant across this buffer.
            float ampA  = TimeMs(AmpAttack);
            float ampD  = TimeMs(AmpDecay);
            float ampS  = AmpSustain / 127f;
            float ampR  = TimeMs(AmpRelease);
            float fA    = TimeMs(FilterAttack);
            float fD    = TimeMs(FilterDecay);
            float fS    = FilterSustain / 127f;
            float fR    = TimeMs(FilterRelease);
            float ampVelN  = AmpVel / 127f;
            float filtVelN = FilterVel / 127f;
            float keyTrkN  = (KeyTrack - 64) / 64f;       // -1..+1 around 64
            float fegIntN  = FilterEgInt / 127f;          // 0..1 → octaves of cutoff sweep
            float cutoffN    = Cutoff / 127f;             // fork: glides as this
            float cutoffBase = CutoffHzAt(cutoffN);       // Hz
            float resN      = Resonance / 127f;
            float volN      = Volume / 127f;

            int   oscMode = OscMode;
            float lvl1    = (oscMode == 1) ? 0f : Osc1Level / 127f;
            float lvl2    = (oscMode == 0) ? 0f : Osc2Level / 127f;

            // Fork (neo-buzz, §12.15): glide to the settings; the first time, at a new rate, and from silence, start there.
            if (!_glidesSet || sr != _glideSr || wasIdle)
            {
                _gVolume.Snap(volN);
                _gLevel1.Snap(lvl1);
                _gLevel2.Snap(lvl2);
                _gCutoff.Snap(cutoffN);
                _gReso.Snap(resN);
                _glidesSet = true;
                _glideSr   = sr;
            }
            else
            {
                int len = (int)(ParamGlide.Ms * 0.001f * sr);
                _gVolume.Set(volN, len);
                _gLevel1.Set(lvl1, len);
                _gLevel2.Set(lvl2, len);
                _gCutoff.Set(cutoffN, len);
                _gReso.Set(resN, len);
            }
            bool gliding = _gVolume.Left > 0 || _gLevel1.Left > 0 || _gLevel2.Left > 0 || _gCutoff.Left > 0 || _gReso.Left > 0;

            // LFO update params (per voice — same coefs for all voices).
            float lfo1Hz   = LfoSpeedHz(Lfo1Speed);
            float lfo1DlMs = TimeMs(Lfo1Delay);
            float lfo1FdMs = TimeMs(Lfo1Fade);
            LfoWave lfo1Wv = (LfoWave)Lfo1Wave;
            float lfo1ToP  = Lfo1ToPitch / 127f;          // semitones (×1)
            if (_midi.Mod > lfo1ToP) lfo1ToP = _midi.Mod; // fork (neo-buzz, §12.15): the mod wheel's vibrato
            float bendSemi = _midi.Bend * MidiIn.BendSemitones;   // fork: pitch bend (0 without MIDI)
            float lfo1ToF  = Lfo1ToVdf  / 127f;           // octaves (×3)
            float lfo1ToA  = Lfo1ToVda  / 127f;

            float lfo2Hz   = LfoSpeedHz(Lfo2Speed);
            float lfo2DlMs = TimeMs(Lfo2Delay);
            float lfo2FdMs = TimeMs(Lfo2Fade);
            LfoWave lfo2Wv = (LfoWave)Lfo2Wave;
            float lfo2ToP  = Lfo2ToPitch / 127f;
            float lfo2ToF  = Lfo2ToVdf  / 127f;
            float lfo2ToA  = Lfo2ToVda  / 127f;

            // Update per-voice envelope + LFO coefficients (cheap).
            for (int vi = 0; vi < MAX_VOICES_CONST; vi++)
            {
                var v = _voices[vi];
                v.AmpEnv   .UpdateCoefs(ampA, ampD, ampS, ampR, sr);
                v.FilterEnv.UpdateCoefs(fA,   fD,   fS,   fR,   sr);
                v.Lfo1     .UpdateCoefs(lfo1Hz, lfo1DlMs, lfo1FdMs, lfo1Wv, sr);
                v.Lfo2     .UpdateCoefs(lfo2Hz, lfo2DlMs, lfo2FdMs, lfo2Wv, sr);
            }

            // Portamento coefficient (one-pole approach to TargetMidi).
            float portaCoef = 0f;
            if (Portamento > 0)
            {
                float ms = TimeMs(Portamento);
                portaCoef = MathF.Exp(-1f / (ms * 0.001f * sr));
            }

            bool any = false;

            // Fork (neo-buzz, §12.15): a voice whose note has ended plays exact silence until its next note (which
            // only comes at a block's start): left out of the loop, only its LFOs keep time (Lfo.Skip, in a few runs
            // of equal steps). A voice idle at the block's start stays idle through it (only a note starts one), so
            // it is left out by the same flag, one load a sample, not IsActive's four tests (follow-ups: that
            // and Skip's per-sample adds made silence after notes cost 1.5× silence before them).
            for (int vi = 0; vi < MAX_VOICES_CONST; vi++)
            {
                var v = _voices[vi];
                bool active = v.IsActive, finished = active && v.Finished;
                _voiceSkip[vi] = !active || finished;
                if (finished) Lfo.Skip(v.Lfo1, v.Lfo2, n);
            }

            for (int i = 0; i < n; i++)
            {
                float sumL = 0f, sumR = 0f;

                // Fork (neo-buzz, §12.15): while a setting glides, these follow it sample by sample.
                if (gliding)
                {
                    volN = _gVolume.Next();
                    lvl1 = _gLevel1.Next();
                    lvl2 = _gLevel2.Next();
                    float cn = _gCutoff.Next();
                    if (cn != cutoffN) { cutoffN = cn; cutoffBase = CutoffHzAt(cn); }
                    resN = _gReso.Next();
                }

                for (int vi = 0; vi < MAX_VOICES_CONST; vi++)
                {
                    var v = _voices[vi];
                    if (_voiceSkip[vi] || !v.IsActive) continue;
                    any = true;

                    // ── Per-voice modulation sources ────────────────────
                    float lfo1 = v.Lfo1.Tick();
                    float lfo2 = v.Lfo2.Tick();
                    float fenv = v.FilterEnv.Tick();
                    float aenv = v.AmpEnv.Tick();

                    // Anti-click ramp.
                    if (v.CurrentGain < v.TargetGain)
                    {
                        v.CurrentGain += Voice.FADE_STEP;
                        if (v.CurrentGain > v.TargetGain) v.CurrentGain = v.TargetGain;
                    }
                    else if (v.CurrentGain > v.TargetGain)
                    {
                        v.CurrentGain -= Voice.FADE_STEP;
                        if (v.CurrentGain < v.TargetGain) v.CurrentGain = v.TargetGain;
                    }

                    // Inline-fire pending trigger when fade-out completes
                    // (Pedal Tracker §4.2). One sample of silence covers the
                    // swap; next sample reads post-trigger env/LFO values.
                    if (v.HasPendingTrigger
                        && v.CurrentGain <= Voice.NEAR_ZERO
                        && v.TargetGain == 0f)
                    {
                        v.HasPendingTrigger = false;   // fork: before the trigger, which reads the pending note's
                        FireTriggerNow(vi, v.PendingTriggerNote, v.PendingFromMidi, v.PendingMidiVelocity);
                    }

                    // Portamento — approach TargetMidi.
                    if (portaCoef > 0f)
                        v.CurrentMidi = v.TargetMidi
                                      + (v.CurrentMidi - v.TargetMidi) * portaCoef;
                    else
                        v.CurrentMidi = v.TargetMidi;

                    // Pitch with LFO modulation (semitones).
                    double pitchMidi = v.CurrentMidi
                                     + lfo1 * lfo1ToP
                                     + lfo2 * lfo2ToP
                                     + bendSemi;          // fork (neo-buzz, §12.15)

                    // Update both osc increments based on pitch.
                    UpdateOscIncrement(v.Osc1, pitchMidi,
                        Osc1Octave, Osc1Transpose, Osc1Detune, sr);
                    UpdateOscIncrement(v.Osc2, pitchMidi,
                        Osc2Octave, Osc2Transpose, Osc2Detune, sr);

                    // Sample read.
                    float s1 = v.TickOsc(v.Osc1);
                    float s2 = v.TickOsc(v.Osc2);
                    float oscMix = s1 * lvl1 + s2 * lvl2;

                    // Filter cutoff modulation. Gate on sample index so each
                    // voice updates on the same control-rate boundary.
                    if ((i & FILTER_UPDATE_MASK) == 0)
                    {
                        float kbdOct  = (float)((pitchMidi - 60.0) / 12.0) * keyTrkN;
                        float velOct  = (v.NoteVelocity - 0.5f) * 2f * filtVelN; // -1..+1 × N (fork: a MIDI note's own)
                        float fegOct  = fenv * fegIntN * 6f;                     // up to 6 octaves
                        float lfoFOct = (lfo1 * lfo1ToF + lfo2 * lfo2ToF) * 4f;
                        float fc      = cutoffBase * Pow2(kbdOct + velOct + fegOct + lfoFOct);
                        if (fc < 20f)     fc = 20f;
                        if (fc > 0.49f * sr) fc = 0.49f * sr;
                        v.Filter.UpdateCoefs(fc, resN, sr);
                    }
                    float filtered = v.Filter.Process(oscMix);

                    // Amp stage: env × velocity × LFO tremolo × anti-click ramp.
                    float velAmp = 1f - ampVelN + ampVelN * v.NoteVelocity;   // fork: a MIDI note's own velocity
                    float trem   = 1f + (lfo1 * lfo1ToA + lfo2 * lfo2ToA) * 0.5f;
                    if (trem < 0f) trem = 0f;
                    float amp    = aenv * velAmp * v.CurrentGain * trem;

                    float outV   = filtered * amp;

                    sumL += outV;
                    sumR += outV;
                }

                // Mixer headroom: mild attenuation to leave room for two
                // oscs at unity. The M1 itself is loud, but downstream
                // ReBuzz processing tends to clip easily, so apply 0.5×.
                float scale = volN * 0.5f * SAMPLE_SCALE;
                output[i] = new Sample(sumL * scale, sumR * scale);
            }

            return any;
        }

        // ── Helpers ─────────────────────────────────────────────────────

        void DrainPendingEvents(int sr)
        {
            for (int t = 0; t < MAX_VOICES_CONST; t++)
            {
                var v = _voices[t];

                if (v.HasNewVelocity)
                {
                    v.Velocity = v.PendingVolumeRaw / 127f;
                    v.HasNewVelocity = false;
                }

                if (v.HasNoteOn)
                {
                    TriggerVoice(t, v.PendingBuzzNote);
                    v.HasNoteOn = false;
                    _midiVoices.Track(t);     // fork (neo-buzz, §12.15): the track's note took the voice
                }
                if (v.HasNoteOff)
                {
                    v.AmpEnv.NoteOff();
                    v.FilterEnv.NoteOff();
                    v.HasNoteOff = false;
                    _midiVoices.Clear(t);     // fork (neo-buzz, §12.15): no longer a MIDI key's
                }
            }
        }

        // TriggerVoice — defers if voice is still sounding, fires immediately
        // if the voice is silent (Pedal Tracker §4 pattern). Avoids click on
        // arpeggio retriggers caused by sample-data discontinuity.
        // Fork (neo-buzz, §12.15): fromMidi and midiVelocity for a MIDI note (a track's note plays at its Volume).
        void TriggerVoice(int track, byte buzzNote, bool fromMidi = false, float midiVelocity = 1f)
        {
            var v = _voices[track];

            if (v.CurrentGain > Voice.NEAR_ZERO)
            {
                // Voice still sounding — fade out, fire when gain reaches zero.
                v.HasPendingTrigger   = true;
                v.PendingTriggerNote  = buzzNote;
                v.PendingFromMidi     = fromMidi;
                v.PendingMidiVelocity = midiVelocity;
                v.TargetGain          = 0f;
                return;
            }

            FireTriggerNow(track, buzzNote, fromMidi, midiVelocity);
        }

        // Actual trigger logic — only called when CurrentGain is at zero
        // (either a fresh voice or a deferred retrigger that has finished
        // fading out). Filter.Reset() is safe here because audio is silent
        // at this moment (amp = aenv × CurrentGain × ... and CurrentGain=0).
        // The reset clears stale integrator state left from a previous note
        // that went inactive — without it, the frozen filter state feeds
        // back into the first samples of the new note as a click.
        void FireTriggerNow(int track, byte buzzNote, bool fromMidi, float midiVelocity)
        {
            var v = _voices[track];
            v.FromMidi     = fromMidi;      // fork (neo-buzz, §12.15)
            v.MidiVelocity = midiVelocity;

            int oct  = (buzzNote >> 4);
            int semi = (buzzNote & 0xF) - 1;
            // +12 maps Buzz "c-4" (byte 0x41) to standard MIDI 60 (middle C),
            // so split-point parameters line up with what the pattern editor
            // displays. Same conversion as Wavetable.BuzzByteToMidi; pitch
            // math is unaffected because the offset cancels in (played-root).
            int midi = (oct + 1) * 12 + semi;

            bool wasIdle = !v.AmpEnv.IsActive;
            v.TargetMidi = midi;
            if (Portamento == 0 || wasIdle) v.CurrentMidi = midi;

            // Resolve wave snapshots — pick from multisample zones (§v1.1).
            // Zone selection uses the played note (TargetMidi); a portamento
            // glide does not change zone mid-note.
            int o1Wave = PickZoneWave(midi,
                Osc1Wave, Osc1Wave2, Osc1Wave3, Osc1Wave4,
                Osc1Split2, Osc1Split3, Osc1Split4);
            int o2Wave = PickZoneWave(midi,
                Osc2Wave, Osc2Wave2, Osc2Wave3, Osc2Wave4,
                Osc2Split2, Osc2Split3, Osc2Split4);

            var wt = WT;
            v.Osc1.Snap = wt?.Get(o1Wave);
            v.Osc2.Snap = wt?.Get(o2Wave);

            // Reset osc state on note-on.
            v.Osc1.Pos = (v.Osc1.Snap != null) ? Interpolation.MAX_PAD : 0;
            v.Osc2.Pos = (v.Osc2.Snap != null) ? Interpolation.MAX_PAD : 0;
            v.Osc1.BackwardsBidi = false;
            v.Osc2.BackwardsBidi = false;
            v.Osc1.Active = (v.Osc1.Snap != null && Osc1Level > 0 && OscMode != 1);
            v.Osc2.Active = (v.Osc2.Snap != null && Osc2Level > 0 && OscMode != 0);

            v.Filter.Reset();
            v.AmpEnv.NoteOn();
            v.FilterEnv.NoteOn();
            v.Lfo1.NoteOn();
            v.Lfo2.NoteOn();

            v.TargetGain = 1f;
            // CurrentGain at 0 — anti-click ramp will fade up smoothly.
        }

        // Per-osc playback rate. Called per-sample per voice per osc to
        // honour LFO pitch modulation, so use FastPow2 not Math.Pow.
        void UpdateOscIncrement(Voice.Osc o, double playMidi,
                                int octParam, int transposeParam, int detuneParam, int sr)
        {
            if (!o.Active || o.Snap == null) return;
            double octShift = octParam - 2;                   // -2..+2
            double semiShift = (transposeParam - 12);         // ±12
            double centsShift = (detuneParam - 50) * 0.01;    // ±0.5 semitones
            double effectiveMidi = playMidi + octShift * 12.0 + semiShift + centsShift;

            // Pitch ratio against snapshot's RootMidi at its native rate.
            float semiDelta = (float)(effectiveMidi - o.Snap.RootMidi);
            float rate      = Pow2(semiDelta / 12f);

            // Sample-rate ratio to handle wave's native rate vs playback rate.
            double srRatio = (double)o.Snap.SampleRate / sr;
            o.Increment = rate * srRatio;
        }

        void ForceFadeAllVoices()
        {
            for (int i = 0; i < MAX_VOICES_CONST; i++)
            {
                var v = _voices[i];
                v.AmpEnv.ForcedRelease();
                v.FilterEnv.ForcedRelease();
                v.TargetGain = 0f;
            }
        }

        // 0..127 → exponential ms range, 1ms..8000ms.
        static float TimeMs(int v)
        {
            const float minMs = 1f, maxMs = 8000f;
            float t = v / 127f;
            return minMs * MathF.Pow(maxMs / minMs, t);
        }

        // 0..127 → 20 Hz .. 18 kHz exponential.
        // (fork, neo-buzz §12.15: at a position 0..1, Cutoff / 127, for its glide)
        static float CutoffHzAt(float t)
        {
            const float minHz = 20f, maxHz = 18000f;
            return minHz * MathF.Pow(maxHz / minHz, t);
        }

        // 0..127 → 0.05 Hz .. 30 Hz exponential.
        static float LfoSpeedHz(int v)
        {
            const float minHz = 0.05f, maxHz = 30f;
            float t = v / 127f;
            return minHz * MathF.Pow(maxHz / minHz, t);
        }

        // FastPow2 from SH101 §1.
        static float Pow2(float x)
        {
            float xi = MathF.Floor(x);
            float xf = x - xi;
            float p  = 1f + xf * (0.69315f + xf * (0.24023f + xf * 0.05550f));
            int   e  = Math.Clamp((int)xi + 127, 1, 254);
            return BitConverter.Int32BitsToSingle(e << 23) * p;
        }

        // Multisample zone selection (v1.1). Higher-numbered zones override
        // lower for notes >= their split. A zone is disabled when its wave
        // is 0. Falls through to w1 (the primary wave, which itself may be
        // 0 → null snapshot → silent voice).
        static int PickZoneWave(int midi,
            int w1, int w2, int w3, int w4,
            int s2, int s3, int s4)
        {
            if (w4 != 0 && midi >= s4) return w4;
            if (w3 != 0 && midi >= s3) return w3;
            if (w2 != 0 && midi >= s2) return w2;
            return w1;
        }
    }

    // Fork (neo-buzz, docs/specs/mcp-server.md §12.13, §12.15): a setting that glides to a new value in a straight line
    // over Ms milliseconds, sample by sample, and lands on it exactly, so a setting that doesn't move is the same
    // number as before. Automation and pattern events change parameters once a tick or subtick; applied once a block
    // they stepped, which clicks. As in the Pedal effects (U6).
    struct ParamGlide
    {
        public const float Ms = 20f;
        public float Value, Target, Step;
        public int Left;

        public void Snap(float v) { Value = Target = v; Left = 0; }

        public void Set(float v, int samples)
        {
            if (v == Target) return;
            Target = v;
            if (samples <= 1) { Snap(v); return; }
            Left = samples;
            Step = (v - Value) / samples;
        }

        public float Next()
        {
            if (Left > 0) Value = --Left == 0 ? Target : Value + Step;
            return Value;
        }
    }
}
