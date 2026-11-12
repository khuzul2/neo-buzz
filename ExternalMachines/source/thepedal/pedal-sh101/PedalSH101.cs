// PedalSH101.cs — Pedal SH101 main machine
//
// Faithful, efficient emulation of the Roland SH-101 voice (no GUI, no
// internal sequencer/arp). Receives notes via track 0 — drive it from a
// pattern column or from a Pedal Chord (or other) control machine.
//
// Architecture:
//
//   Note → MIDI → portamento → ┐
//                              ├→ +Range +Tune +VCO-mod ┐
//                LFO ──────────┘                        ├→ VCO ─→ VCF ─→ VCA ─→ out
//                                                       │   ↑      ↑     ↑
//   PWM:    Manual / LFO / Env ───────────────→ ───────→┘   │      │     │
//   ENV: ───────────────────────────────────────────────────┘──────┘─────┘
//   LFO: ──────────────────────────────────────────────────────────┘
//   Kbd-follow: pitch ─────────────────────────────────────────────┘
//
// All DSP runs at the host sample rate, no oversampling. The ZDF Moog
// ladder is stable to self-oscillation without it.
//
// IMPORTANT — preset stability (Build §3.3):
// Parameter declaration order in this file is the public preset format.
// Never reorder; always APPEND new parameters at the end of the global
// list, marking the version they were added in.

using System;
using System.Collections.Generic;   // IEnumerable<T> for Commands
using System.Runtime.CompilerServices;   // fork (neo-buzz): MethodImplOptions.AggressiveOptimization
using System.Windows;               // MessageBox (About window)
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using BuzzGUI.Common;               // MenuItemVM, SimpleCommand

namespace PedalSH101
{
    [MachineDecl(
        Name      = "Pedal SH101",
        ShortName = "SH101",
        Author    = "WDE",
        MaxTracks = 1)]
    public class PedalSH101Machine : IBuzzMachine
    {
        // Single source of truth for the machine version — used by the About
        // window (AboutWindow §1.3). Bump here on release.
        internal const string Version = "1.4.0";

        // ── Right-click "About..." menu entry (AboutWindow §1.4) ──────────────
        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                yield return new MenuItemVM()
                {
                    Text = "About...",
                    Command = new SimpleCommand()
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate    = p => MessageBox.Show(
                            $"Pedal SH101   v{Version}\n\n" +
                            "Monophonic Roland SH-101 emulation: single VCO (pulse /\n" +
                            "saw / sub / noise) with PWM, ZDF Moog-ladder VCF,\n" +
                            "ADSR, LFO and glide.\n\n" +
                            "Author: Pedal\n" +
                            "github.com/thepedal/pedal-sh101\n" +
                            "GPL-3.0 License\n\n" +
                            "Roland and SH-101 are trademarks of Roland\n" +
                            "Corporation. This project is independent and is not\n" +
                            "affiliated with or endorsed by Roland.",
                            "About Pedal SH101")
                    }
                };
            }
        }

        readonly IBuzzMachineHost host;

        // ── DSP blocks ─────────────────────────────────────────────────
        readonly VCO        _vco     = new VCO();
        readonly MoogLadder _filter  = new MoogLadder();
        readonly ADSR       _env     = new ADSR();
        readonly LFO        _lfo     = new LFO();
        readonly DCBlocker  _dcBlock = new DCBlocker();

        // ── Voice state ────────────────────────────────────────────────
        float _currentPitchSemis = 60f;
        float _targetPitchSemis  = 60f;
        bool  _gateActive        = false;

        // Pending events from setters; consumed at the top of Work()
        bool _hasNoteOn  = false;
        bool _hasNoteOff = false;
        byte _pendingBuzzNote = 0;

        // ── Fork (neo-buzz, docs/specs/mcp-server.md §12.15): MIDI notes and controllers (MidiIn.cs) ───────
        // Monophonic, last-note priority: a new key retriggers, as a track's note does; letting go of it goes back
        // to the key still held, without a retrigger (gliding there with Glide on). The velocity is a level on the
        // VCA (velocity / 127; a track's note is 1, exactly as before), moving over 2 ms between overlapping notes.
        // Pitch bend ±2 semitones, the mod wheel brings in the LFO's vibrato up to ±1 semitone (the SH-101's
        // modulation lever), the sustain pedal holds MIDI notes. The transport stopping releases a MIDI note, and a
        // track's note too.
        readonly MidiIn _midi = new MidiIn();
        readonly int[]  _keys = new int[16];   // MIDI keys down, oldest first: the last one sounds
        int   _keyCount;
        bool  _midiSounding;                   // the note sounding is a MIDI note
        bool  _midiSustained;                  // its keys are up; the pedal holds it
        bool  _wasPlaying;
        const float VelocityGlideMs = 2f;

        // ── Fork (neo-buzz, §12.15): Volume, the mixer's levels, PWM, Cutoff (as its 0…1 position) and
        // Resonance glide to a new setting over 20 ms, the velocity level over 2 ms (ParamGlide, below); a setting that
        // doesn't move is the same number as before. The first block, and a new sample rate, start at the setting.
        ParamGlide _gVolume, _gPulse, _gSaw, _gSub, _gNoise, _gPwm, _gCutoff, _gReso, _gVel;
        bool  _glidesSet;

        public PedalSH101Machine(IBuzzMachineHost host)
        {
            this.host = host;
            _gVel.Snap(1f);   // fork (neo-buzz, §12.15): a track's note is at full level
        }

        // ─────────────────────────────────────────────────────────────
        //  Track parameter — Note (must be method-style to use Note type;
        //  see Core §3)
        // ─────────────────────────────────────────────────────────────

        [ParameterDecl(Name = "Note",
            IsStateless = true,
            Description = "Note (z=C-4, s=C#-4 …). Note-off = 1.")]
        public void SetNote(Note value, int track)
        {
            byte v = value.Value;
            if (v == 0) return;          // NoValue — shouldn't reach here
            if (v == Note.Off)           // 255 — release
            {
                _hasNoteOff = true;
                return;
            }
            _pendingBuzzNote = v;
            _hasNoteOn       = true;
        }

        // ─────────────────────────────────────────────────────────────
        //  Global parameters (declaration order is the preset format)
        // ─────────────────────────────────────────────────────────────

        // ── VCO ──
        [ParameterDecl(Name = "Range",
            MinValue = 0, MaxValue = 2, DefValue = 1,
            Description = "VCO octave",
            ValueDescriptions = new[] { "16'", "8'", "4'" })]
        public int Range { get; set; } = 1;

        [ParameterDecl(Name = "VCO Mod",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO → VCO pitch (vibrato)")]
        public int VcoMod { get; set; } = 0;

        [ParameterDecl(Name = "PWM Source",
            MinValue = 0, MaxValue = 2, DefValue = 0,
            Description = "Pulse-width modulation source",
            ValueDescriptions = new[] { "Manual", "LFO", "Env" })]
        public int PwmSource { get; set; } = 0;

        [ParameterDecl(Name = "PWM",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Manual: 0=square..127=narrow. LFO/Env: mod depth.")]
        public int PwmAmount { get; set; } = 0;

        // ── Mixer ──
        [ParameterDecl(Name = "Pulse Lvl",
            MinValue = 0, MaxValue = 127, DefValue = 100)]
        public int PulseLevel { get; set; } = 100;

        [ParameterDecl(Name = "Saw Lvl",
            MinValue = 0, MaxValue = 127, DefValue = 0)]
        public int SawLevel { get; set; } = 0;

        [ParameterDecl(Name = "Sub Lvl",
            MinValue = 0, MaxValue = 127, DefValue = 0)]
        public int SubLevel { get; set; } = 0;

        [ParameterDecl(Name = "Noise Lvl",
            MinValue = 0, MaxValue = 127, DefValue = 0)]
        public int NoiseLevel { get; set; } = 0;

        [ParameterDecl(Name = "Sub Type",
            MinValue = 0, MaxValue = 2, DefValue = 0,
            Description = "Sub-oscillator waveform",
            ValueDescriptions = new[] { "Sq -1oct", "Sq -2oct", "Pulse -2oct" })]
        public int SubType { get; set; } = 0;

        // ── VCF ──
        [ParameterDecl(Name = "Cutoff",
            MinValue = 0, MaxValue = 127, DefValue = 90,
            Description = "Filter cutoff (~20 Hz to ~20 kHz, log)")]
        public int Cutoff { get; set; } = 90;

        [ParameterDecl(Name = "Resonance",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Filter resonance (self-oscillates near max)")]
        public int Resonance { get; set; } = 0;

        // Bipolar Env Amount stored as offset (PedalComp §2):
        // 0..128, with 64 = 0; lower = inverted, higher = positive.
        [ParameterDecl(Name = "Env Amt",
            MinValue = 0, MaxValue = 128, DefValue = 64,
            Description = "Filter envelope depth (64 = 0; <64 inverted)")]
        public int EnvAmount { get; set; } = 64;

        [ParameterDecl(Name = "VCF Mod",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO → VCF cutoff depth")]
        public int VcfMod { get; set; } = 0;

        [ParameterDecl(Name = "Kbd Follow",
            MinValue = 0, MaxValue = 2, DefValue = 0,
            Description = "Cutoff tracks pitch",
            ValueDescriptions = new[] { "Off", "1/2", "Full" })]
        public int KbdFollow { get; set; } = 0;

        // ── VCA ──
        [ParameterDecl(Name = "VCA Mode",
            MinValue = 0, MaxValue = 1, DefValue = 1,
            Description = "VCA control source",
            ValueDescriptions = new[] { "Gate", "Env" })]
        public int VcaMode { get; set; } = 1;

        // ── Envelope (ADSR) ──
        [ParameterDecl(Name = "Attack",  MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Envelope attack time (~2 ms .. 8 s)")]
        public int Attack { get; set; } = 0;

        [ParameterDecl(Name = "Decay",   MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "Envelope decay time (~2 ms .. 15 s)")]
        public int Decay { get; set; } = 64;

        [ParameterDecl(Name = "Sustain", MinValue = 0, MaxValue = 127, DefValue = 100,
            Description = "Envelope sustain level")]
        public int Sustain { get; set; } = 100;

        [ParameterDecl(Name = "Release", MinValue = 0, MaxValue = 127, DefValue = 50,
            Description = "Envelope release time (~2 ms .. 15 s)")]
        public int Release { get; set; } = 50;

        // ── LFO ──
        [ParameterDecl(Name = "LFO Rate",
            MinValue = 0, MaxValue = 127, DefValue = 64,
            Description = "LFO rate (~0.1 Hz .. ~30 Hz, log)")]
        public int LfoRate { get; set; } = 64;

        [ParameterDecl(Name = "LFO Wave",
            MinValue = 0, MaxValue = 3, DefValue = 0,
            Description = "LFO waveform",
            ValueDescriptions = new[] { "Triangle", "Square", "S&H", "Noise" })]
        public int LfoWave { get; set; } = 0;

        [ParameterDecl(Name = "LFO Delay",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "LFO onset delay after note-on (0 .. 2 s)")]
        public int LfoDelay { get; set; } = 0;

        // ── Pitch ──
        [ParameterDecl(Name = "Glide",
            MinValue = 0, MaxValue = 127, DefValue = 0,
            Description = "Portamento time (0 = off / instant)")]
        public int Glide { get; set; } = 0;

        // Bipolar Tune stored as offset (50 = center, ±50 cents).
        [ParameterDecl(Name = "Tune",
            MinValue = 0, MaxValue = 100, DefValue = 50,
            Description = "Master fine tune (50 = 0¢, 0 = -50¢, 100 = +50¢)")]
        public int Tune { get; set; } = 50;

        // ── Master ──
        [ParameterDecl(Name = "Volume",
            MinValue = 0, MaxValue = 127, DefValue = 96,
            Description = "Output level")]
        public int Volume { get; set; } = 96;

        // ── New in v1.x — APPEND any future parameters BELOW this line so
        // existing presets keep working (Build §3.3). ─────────────────────


        // ─────────────────────────────────────────────────────────────
        //  Coefficient cache for things that depend on (parameter, sr)
        //  but aren't owned by an inner DSP class. (PedalComp §6)
        // ─────────────────────────────────────────────────────────────
        int   _cSr           = 0;

        // DC-blocker corner. 10 Hz clears the VCO's narrow-pulse/sub offset
        // while leaving the lowest musical fundamentals (and all but
        // sub-sonic sub-osc content) untouched. Lower toward ~5 Hz if you
        // run -2 oct sub on very low notes and want to keep its subsonic
        // weight; raise it only if you want a tighter low end.
        const float DC_CORNER_HZ = 10f;

        int   _cGlide        = -1;
        float _glideCoef     = 0f;
        int   _cLfoRate      = -1;
        float _lfoRateHz     = 1f;
        int   _cLfoDelay     = -1;
        int   _lfoDelaySamps = 0;

        void UpdateCoefs(int sr)
        {
            bool srChanged = sr != _cSr;
            if (srChanged) _cSr = sr;

            // Recompute the DC-blocker pole for the new rate (Core §29).
            // State is deliberately not cleared — the held history sample is
            // a valid recent amplitude, so continuing avoids a settle
            // transient on the first buffer at the new rate.
            if (srChanged) _dcBlock.SetCorner(DC_CORNER_HZ, sr);

            if (srChanged || Glide != _cGlide)
            {
                _cGlide = Glide;
                if (Glide == 0)
                {
                    _glideCoef = 0f;       // instant
                }
                else
                {
                    // Log map 1..127 → 1 ms .. 2 s
                    float t = (Glide - 1) / 126f;
                    float ms = MathF.Pow(2000f, t);
                    float n  = ms * 0.001f * sr;
                    if (n < 1f) n = 1f;
                    _glideCoef = MathF.Exp(-1f / n);
                }
            }

            if (LfoRate != _cLfoRate)
            {
                _cLfoRate = LfoRate;
                // 0.1 Hz to 30 Hz, log
                _lfoRateHz = 0.1f * MathF.Pow(300f, LfoRate / 127f);
            }

            if (srChanged || LfoDelay != _cLfoDelay)
            {
                _cLfoDelay = LfoDelay;
                float secs = (LfoDelay / 127f) * 2f;     // 0..2 s
                _lfoDelaySamps = (int)(secs * sr);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Note triggering
        // ─────────────────────────────────────────────────────────────
        void TriggerNote(byte buzzNote)
        {
            // Buzz byte → MIDI (Core §7)
            int oct  = (buzzNote >> 4);
            int semi = (buzzNote & 0xF) - 1;
            int midi = oct * 12 + semi;

            TriggerPitch(midi, 1f);   // fork (neo-buzz, §12.15): a track's note has no velocity
        }

        // Fork (neo-buzz, §12.15): a note by its MIDI number, from a track or MIDI, with its velocity level.
        void TriggerPitch(int midi, float velocity)
        {
            float pitch = midi;

            bool wasIdle = !_env.IsActive;

            _targetPitchSemis = pitch;
            // Glide off, or coming from Idle (first note) → snap immediately.
            if (Glide == 0 || wasIdle)
                _currentPitchSemis = pitch;

            // Fork (neo-buzz, §12.15): the velocity level; from silence it starts there.
            if (!_glidesSet || wasIdle) _gVel.Snap(velocity);
            else _gVel.Set(velocity, (int)(VelocityGlideMs * 0.001f * host.MasterInfo.SamplesPerSec));

            _env.NoteOn();
            _gateActive = true;
            _lfo.NoteOn(_lfoDelaySamps);
        }

        // ─────────────────────────────────────────────────────────────
        //  Fork (neo-buzz, docs/specs/mcp-server.md §12.15): MIDI from
        //  ReBuzz (a keyboard, a pattern's MIDI column), queued and
        //  applied at the start of the next block.
        // ─────────────────────────────────────────────────────────────

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
                        ReleaseMidiNote();
                        break;
                    case MidiIn.AllSoundOff:
                        bool sounding = _midiSounding;
                        ReleaseMidiNote();
                        if (sounding) { _env.HardReset(); _gateActive = false; }
                        break;
                    default:
                        if (_midi.Apply(a, b) && _midiSustained && _keyCount == 0)   // the pedal came up
                            ReleaseMidiNote();
                        break;
                }
            }
        }

        void MidiNoteOn(int key, int velocity)
        {
            RemoveKey(key);
            if (_keyCount == _keys.Length) RemoveAt(0);
            _keys[_keyCount++] = key;
            TriggerPitch(key, velocity / 127f);
            _midiSounding  = true;
            _midiSustained = false;
        }

        void MidiNoteOff(int key)
        {
            bool sounding = _keyCount > 0 && _keys[_keyCount - 1] == key;
            RemoveKey(key);
            if (!_midiSounding || !sounding) return;
            if (_keyCount > 0)
            {
                // Back to the key still held: its pitch, no retrigger.
                _targetPitchSemis = _keys[_keyCount - 1];
                if (Glide == 0) _currentPitchSemis = _targetPitchSemis;
                return;
            }
            if (_midi.Pedal) { _midiSustained = true; return; }
            ReleaseMidiNote();
        }

        void ReleaseMidiNote()
        {
            _keyCount = 0;
            _midiSustained = false;
            if (!_midiSounding) return;
            _midiSounding = false;
            _env.NoteOff();
            _gateActive = false;
        }

        void RemoveKey(int key)
        {
            for (int i = 0; i < _keyCount; i++)
                if (_keys[i] == key) { RemoveAt(i); return; }
        }

        void RemoveAt(int i)
        {
            for (int j = i + 1; j < _keyCount; j++) _keys[j - 1] = _keys[j];
            _keyCount--;
        }

        // The transport stopping releases the note, a MIDI note or (fork, follow-ups: it rang on) a track's, as a
        // track's note off does; a track's note queued in the same tick is stale and dropped, as Juno106 drops them.
        void CheckTransport()
        {
            bool nowPlaying;
            try { nowPlaying = host?.Machine?.Graph?.Buzz?.Playing ?? false; }
            catch { nowPlaying = _wasPlaying; }
            if (_wasPlaying && !nowPlaying)
            {
                ReleaseMidiNote();
                _hasNoteOn = false;
                _hasNoteOff = false;
                if (_gateActive) { _env.NoteOff(); _gateActive = false; }
            }
            _wasPlaying = nowPlaying;
        }

        // ─────────────────────────────────────────────────────────────
        //  Audio process
        // ─────────────────────────────────────────────────────────────
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // fork (neo-buzz): never compiled on the audio thread
        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            int sr = host.MasterInfo.SamplesPerSec;
            if (sr != _cSr) _glidesSet = false;   // fork (neo-buzz, §12.15): a new rate starts at the settings
            UpdateCoefs(sr);
            CheckTransport();                     // fork (neo-buzz, §12.15): Stop releases the note
            bool wasIdle = !_env.IsActive && !_gateActive;   // fork: before this block's notes

            // Drain pending events. Process note-on before note-off so a
            // pattern row containing both retriggers cleanly.
            if (_hasNoteOn)
            {
                TriggerNote(_pendingBuzzNote); _hasNoteOn = false;
                _keyCount = 0; _midiSounding = false; _midiSustained = false;   // fork: the track's note took over
            }
            if (_hasNoteOff)
            {
                _env.NoteOff(); _gateActive = false; _hasNoteOff = false;
                _keyCount = 0; _midiSounding = false; _midiSustained = false;   // fork
            }
            ProcessMidi();                        // fork (neo-buzz, §12.15): after the track's note

            // ── Hoist parameter conversions out of the per-sample loop ──
            float rangeOffsetSemi = (Range == 0) ? -12f : ((Range == 2) ? 12f : 0f);
            float fineSemi        = (Tune - 50) * 0.01f;
            float vcoModSemi      = (VcoMod / 127f) * 12f;            // up to ±1 oct vibrato
            float pulseLvl        = PulseLevel / 127f;
            float sawLvl          = SawLevel   / 127f;
            float subLvl          = SubLevel   / 127f;
            float noiseLvl        = NoiseLevel / 127f;
            int   subType         = SubType;
            int   pwmSrc          = PwmSource;
            float pwmAmt          = PwmAmount  / 127f;
            float cutoffN         = Cutoff / 127f;                    // fork: glides as this
            float baseCutoffHz    = 20f * MathF.Pow(1000f, cutoffN);  // 20..20k Hz log
            float resN            = Resonance  / 127f;                // 0..1
            float volume          = Volume / 127f;

            // Fork (neo-buzz, §12.15): glide to the settings; the first time, at a new rate, and in silence or from it,
            // start there (a note after silence starts at the settings).
            bool idle = !_env.IsActive && !_gateActive;
            if (!_glidesSet || idle || wasIdle)
            {
                _gVolume.Snap(volume);
                _gPulse.Snap(pulseLvl);
                _gSaw.Snap(sawLvl);
                _gSub.Snap(subLvl);
                _gNoise.Snap(noiseLvl);
                _gPwm.Snap(pwmAmt);
                _gCutoff.Snap(cutoffN);
                _gReso.Snap(resN);
                _glidesSet = true;
            }
            else
            {
                int len = (int)(ParamGlide.Ms * 0.001f * sr);
                _gVolume.Set(volume, len);
                _gPulse.Set(pulseLvl, len);
                _gSaw.Set(sawLvl, len);
                _gSub.Set(subLvl, len);
                _gNoise.Set(noiseLvl, len);
                _gPwm.Set(pwmAmt, len);
                _gCutoff.Set(cutoffN, len);
                _gReso.Set(resN, len);
            }

            // Idle fast-path: zero output, return false so ReBuzz can prune.
            if (idle)
            {
                for (int i = 0; i < n; i++) output[i] = new Sample(0f, 0f);
                return false;
            }

            bool  gliding  = _gVolume.Left > 0 || _gPulse.Left > 0 || _gSaw.Left > 0 || _gSub.Left > 0 || _gNoise.Left > 0
                          || _gPwm.Left > 0 || _gCutoff.Left > 0 || _gReso.Left > 0 || _gVel.Left > 0;
            float velGain  = _gVel.Value;
            // Fork (neo-buzz, §12.15): pitch bend, and the mod wheel's vibrato (0 without MIDI: the same numbers).
            float bendSemi = _midi.Bend * MidiIn.BendSemitones;
            if (_midi.Mod > vcoModSemi) vcoModSemi = _midi.Mod;          // up to ±1 semitone
            float envAmtOctaves   = ((EnvAmount - 64) / 64f) * 5f;     // ±5 oct
            float lfoFiltOctaves  = (VcfMod / 127f) * 4f;              // up to 4 oct
            int   kbdFollowMode   = KbdFollow;
            int   vcaMode         = VcaMode;
            float glideCoef       = _glideCoef;
            float currentPitch    = _currentPitchSemis;
            float targetPitch     = _targetPitchSemis;

            // Push live knob values to inner DSP blocks.
            _env.Attack  = Attack;
            _env.Decay   = Decay;
            _env.Sustain = Sustain;
            _env.Release = Release;
            _lfo.Waveform = (LFO.Wave)LfoWave;
            _lfo.Rate     = _lfoRateHz;

            float fcMaxHz = sr * 0.49f;

            for (int i = 0; i < n; i++)
            {
                // Fork (neo-buzz, §12.15): while a setting glides, these follow it sample by sample.
                if (gliding)
                {
                    volume   = _gVolume.Next();
                    pulseLvl = _gPulse.Next();
                    sawLvl   = _gSaw.Next();
                    subLvl   = _gSub.Next();
                    noiseLvl = _gNoise.Next();
                    pwmAmt   = _gPwm.Next();
                    float cn = _gCutoff.Next();
                    if (cn != cutoffN) { cutoffN = cn; baseCutoffHz = 20f * MathF.Pow(1000f, cn); }
                    resN     = _gReso.Next();
                    velGain  = _gVel.Next();
                }

                // 1. Portamento (one-pole approach to target)
                if (glideCoef == 0f)
                    currentPitch = targetPitch;
                else
                    currentPitch = targetPitch + (currentPitch - targetPitch) * glideCoef;

                // 2. LFO and envelope
                float lfo = _lfo.Process(sr);
                float env = _env.Process(sr);

                // 3. Final VCO pitch and frequency
                float pitchSemis = currentPitch
                                 + rangeOffsetSemi
                                 + fineSemi
                                 + lfo * vcoModSemi
                                 + bendSemi;                   // fork (neo-buzz, §12.15)
                float freq = 440f * MathF.Pow(2f, (pitchSemis - 69f) * (1f / 12f));
                if (freq < 1f) freq = 1f;

                // 4. PWM
                float pwm;
                switch (pwmSrc)
                {
                    case 1: // LFO modulates around 0.5
                        pwm = 0.5f + lfo * pwmAmt * 0.4f;
                        break;
                    case 2: // Env modulates around 0.5 (downward, since env >= 0)
                        pwm = 0.5f - env * pwmAmt * 0.4f;
                        break;
                    default: // Manual: 0 = square, 127 = narrow (10%)
                        pwm = 0.5f - pwmAmt * 0.4f;
                        break;
                }
                if      (pwm < 0.05f) pwm = 0.05f;
                else if (pwm > 0.95f) pwm = 0.95f;

                // 5. VCO
                float oscOut = _vco.Process(
                    freq, pwm, subType,
                    pulseLvl, sawLvl, subLvl, noiseLvl, sr);

                // 6. VCF cutoff (in octaves above base)
                float kbdOctaves = 0f;
                if      (kbdFollowMode == 1) kbdOctaves = (currentPitch - 60f) / 24f; // half
                else if (kbdFollowMode == 2) kbdOctaves = (currentPitch - 60f) / 12f; // full

                float fcOctMod = envAmtOctaves * env
                               + lfoFiltOctaves * lfo
                               + kbdOctaves;
                float fc = baseCutoffHz * MathF.Pow(2f, fcOctMod);
                if      (fc < 20f)     fc = 20f;
                else if (fc > fcMaxHz) fc = fcMaxHz;

                // 7. VCF
                float filtered = _filter.Process(oscOut, fc, resN, sr);

                // 7b. DC blocker — strip the VCO's intrinsic offset (narrow
                //     pulse / narrow sub) here, before the VCA, so it never
                //     reaches the output and no enveloped-DC ramp thumps on
                //     note-on. See DCBlocker.cs.
                filtered = _dcBlock.Process(filtered);

                // 8. VCA
                float gain = (vcaMode == 1) ? env : (_gateActive ? 1f : 0f);
                float sample = filtered * gain * volume * velGain;   // fork: × 1 for a track's note

                // 9. Output (mono → both channels). Generators write directly
                //    at ±32768 scale (PedalComp §1).
                float s32 = sample * 32768f;
                output[i] = new Sample(s32, s32);
            }

            // Persist pitch glide state across buffers.
            _currentPitchSemis = currentPitch;

            return true;   // produced audio
        }

        // ─────────────────────────────────────────────────────────────
        //  DescribeValue — populates ReBuzz's status-bar with a
        //  human-readable rendering of the parameter's actual value
        //  (ms / Hz / oct / %), so the user sees "270 ms" rather than
        //  "64". Reflected via ManagedMachineHost.cs:255 — signature
        //  is (IParameter, int), no track.
        //
        //  Return null to fall through to the parameter's
        //  ValueDescriptions (Range, PWM Source, Sub Type, Kbd Follow,
        //  VCA Mode, LFO Wave all have those).
        // ─────────────────────────────────────────────────────────────
        public string DescribeValue(IParameter param, int value)
        {
            switch (param.Name)
            {
                // ── ADSR times (matches ADSR.cs UpdateCoefs ranges) ──
                case "Attack":  return FormatMs(LogTimeMs(value, 2f,  8000f));
                case "Decay":   return FormatMs(LogTimeMs(value, 2f, 15000f));
                case "Release": return FormatMs(LogTimeMs(value, 2f, 15000f));

                case "Sustain": return FormatPercent(value);

                // ── Generic % levels ──
                case "Pulse Lvl":
                case "Saw Lvl":
                case "Sub Lvl":
                case "Noise Lvl":
                case "Resonance":
                case "VCF Mod":
                case "PWM":
                case "Volume":
                    return FormatPercent(value);

                // ── Frequencies ──
                case "Cutoff":
                    return FormatHz(20f * MathF.Pow(1000f, value / 127f));
                case "LFO Rate":
                    return FormatHz(0.1f * MathF.Pow(300f, value / 127f));

                // ── Other times ──
                case "LFO Delay":
                    return FormatMs((value / 127f) * 2000f);
                case "Glide":
                    if (value == 0) return "off";
                    return FormatMs(MathF.Pow(2000f, (value - 1) / 126f));

                // ── Signed / bipolar ──
                case "Env Amt":
                {
                    float oct = ((value - 64) / 64f) * 5f;
                    return oct.ToString("+0.00;-0.00;+0.00", InvCulture) + " oct";
                }
                case "Tune":
                {
                    int cents = value - 50;
                    return cents.ToString("+0;-0;+0", InvCulture) + " ¢";
                }
                case "VCO Mod":
                {
                    float semis = (value / 127f) * 12f;
                    return semis.ToString("0.00", InvCulture) + " semi";
                }

                default:
                    return null;
            }
        }

        // ─── DescribeValue helpers ───
        static readonly System.Globalization.CultureInfo InvCulture
            = System.Globalization.CultureInfo.InvariantCulture;

        static float LogTimeMs(int val, float minMs, float maxMs)
            => minMs * MathF.Pow(maxMs / minMs, val / 127f);

        static string FormatMs(float ms)
        {
            if (ms < 10f)
                return ms.ToString("0.0", InvCulture) + " ms";
            if (ms < 1000f)
                return ((int)MathF.Round(ms)).ToString(InvCulture) + " ms";
            return (ms / 1000f).ToString("0.00", InvCulture) + " s";
        }

        static string FormatHz(float hz)
        {
            if (hz < 100f)
                return hz.ToString("0.0", InvCulture) + " Hz";
            if (hz < 1000f)
                return ((int)MathF.Round(hz)).ToString(InvCulture) + " Hz";
            return (hz / 1000f).ToString("0.00", InvCulture) + " kHz";
        }

        static string FormatPercent(int v)
            => ((int)MathF.Round(v * 100f / 127f)).ToString(InvCulture) + "%";
    }

    // Fork (neo-buzz, docs/specs/mcp-server.md §12.13, §12.15): a setting that glides to a new value in a straight
    // line over Ms milliseconds (or the given samples), sample by sample, and lands on it exactly, so a setting that
    // doesn't move is the same number as before. Automation and pattern events change parameters once a tick or
    // subtick; applied once a block they stepped, which clicks. As in the Pedal effects (U6).
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
