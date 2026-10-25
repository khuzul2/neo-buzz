// Wavetable.cs — snapshot cache for ReBuzz wavetable entries.
//
// Lifted from Pedal Tracker. Each Snapshot holds a copy of the wave's
// float data, root note (in linear MIDI), loop info, length, and a small
// MAX_PAD head/tail padding so interpolators can read pos±N without
// bounds checks.
//
// Snapshots are built lazily on first access from the audio thread
// (cheap try-get, blocking allocation only on cold miss — acceptable
// since wave-change is a deliberate user action). Cache holds for the
// lifetime of the WavetableAccess: if the user replaces a wave in the
// wavetable, the machine must be reloaded to pick up the new data.
// (Earlier versions subscribed to IWave.PropertyChanged and invalidated
// the cache on any property change — that turned out to force snapshot
// rebuilds on the audio thread mid-playback, producing clicks. Keeping
// the cache stable is the cheaper trade.)

using System;
using System.Collections.Generic;
using BuzzGUI.Interfaces;

namespace WDE.PedalM1
{
    public sealed class WaveSnapshot
    {
        public int       WaveIndex;          // 1-based; 0 = empty
        public float[]   DataL;              // mono or left channel; pad-offset
        public float[]   DataR;              // null if mono
        public bool      Stereo;
        public int       Length;             // sample count (per channel), excluding pad
        public int       LoopStart, LoopEnd;
        public bool      Loop;
        public bool      BidiLoop;
        public int       RootMidi;           // linear MIDI (0..127)
        public int       SampleRate;         // ReBuzz wave native rate
    }

    public sealed class WavetableAccess
    {
        readonly IBuzz _buzz;
        readonly Dictionary<int, WaveSnapshot> _cache = new Dictionary<int, WaveSnapshot>();
        readonly object _lock = new object();

        public WavetableAccess(IBuzz buzz) { _buzz = buzz; }

        // Returns null if waveIndex is 0 or the wave/layer is empty.
        public WaveSnapshot Get(int waveIndex)
        {
            if (waveIndex <= 0) return null;
            lock (_lock)
            {
                if (_cache.TryGetValue(waveIndex, out var snap)) return snap;
                snap = Build(waveIndex);
                if (snap != null) _cache[waveIndex] = snap;
                return snap;
            }
        }

        public void Invalidate(int waveIndex)
        {
            lock (_lock)
            {
                _cache.Remove(waveIndex);
            }
        }

        public void InvalidateAll()
        {
            lock (_lock) _cache.Clear();
        }

        WaveSnapshot Build(int waveIndex)
        {
            try
            {
                var wt = _buzz?.Song?.Wavetable;
                if (wt == null) return null;
                if (waveIndex < 1 || waveIndex > wt.Waves.Count) return null;

                var w = wt.Waves[waveIndex - 1];
                if (w == null || w.Layers == null || w.Layers.Count == 0) return null;

                var layer = w.Layers[0];          // pick first layer for now
                int len = layer.SampleCount;
                if (len <= 0) return null;

                bool stereo  = (w.Flags & WaveFlags.Stereo) != 0;
                bool loop    = (w.Flags & WaveFlags.Loop) != 0;
                bool bidi    = (w.Flags & WaveFlags.BidirectionalLoop) != 0;

                int pad = Interpolation.MAX_PAD;
                var L = new float[len + 2 * pad];
                float[] R = stereo ? new float[len + 2 * pad] : null;

                layer.GetDataAsFloat(L, pad, 1, 0, 0, len);
                if (stereo) layer.GetDataAsFloat(R, pad, 1, 1, 0, len);

                // Edge padding: extend first/last sample into the pad zones for
                // interpolators that read pos±N near the very start/end.
                for (int p = 0; p < pad; p++)
                {
                    L[p]               = L[pad];
                    L[pad + len + p]   = L[pad + len - 1];
                    if (stereo)
                    {
                        R[p]             = R[pad];
                        R[pad + len + p] = R[pad + len - 1];
                    }
                }

                int loopStart = layer.LoopStart;
                int loopEnd   = layer.LoopEnd;
                if (loopEnd <= loopStart || loopEnd > len)
                {
                    loopStart = 0;
                    loopEnd   = len;
                }

                int rootMidi = BuzzByteToMidi(layer.RootNote);

                var snap = new WaveSnapshot
                {
                    WaveIndex  = waveIndex,
                    DataL      = L,
                    DataR      = R,
                    Stereo     = stereo,
                    Length     = len,
                    LoopStart  = loopStart,
                    LoopEnd    = loopEnd,
                    Loop       = loop,
                    BidiLoop   = bidi,
                    RootMidi   = rootMidi,
                    SampleRate = layer.SampleRate
                };

                return snap;
            }
            catch
            {
                return null;
            }
        }

        // Buzz-byte note: high nibble = octave, low nibble = (semi+1).
        // c-4 = 0x41 → octave 4, semi 0 → standard MIDI 60 (middle C).
        // The +12 offset matches the convention shown in the ReBuzz pattern
        // editor: the note byte's "octave 4" corresponds to MIDI octave 5
        // in the standard MIDI naming scheme. Without the +12, splits and
        // labels would be one octave off from what the user sees in the
        // pattern editor (Pedal M1 v1.1 fix).
        // IWaveLayer.RootNote is declared as int but holds a Buzz-byte value.
        static int BuzzByteToMidi(int b)
        {
            int oct  = (b >> 4);
            int semi = (b & 0xF) - 1;
            if (semi < 0) semi = 0;
            int m = (oct + 1) * 12 + semi;
            if (m < 0)   m = 0;
            if (m > 127) m = 127;
            return m;
        }
    }
}
