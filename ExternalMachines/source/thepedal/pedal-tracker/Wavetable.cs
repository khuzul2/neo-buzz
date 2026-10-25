// Wavetable / IWave / IWaveLayer access shim.
//
// Verified against ReBuzz 1818-preview source.  The public interface
// (BuzzGUI.Interfaces) gives us:
//
//   public enum WaveFormat { Int16, Float32, Int32, Int24 };
//   [Flags] enum WaveFlags { Loop = 1, Not16Bit = 4, Stereo = 8, BidirectionalLoop = 16 };
//
//   interface IWaveformBase {
//     WaveFormat Format     { get; }
//     int  SampleCount      { get; }
//     int  RootNote         { get; set; }
//     int  SampleRate       { get; set; }
//     int  LoopStart, LoopEnd, ChannelCount;
//     void GetDataAsFloat(float[] output, int outoffset, int outstride,
//                         int channel,    int offset,    int count);
//     void SetDataAsFloat(...);
//     void InvalidateData();
//   }
//
//   interface IWaveLayer : IWaveformBase { string Path; IntPtr RawSamples; }
//
//   interface IWave {
//     int Index; string Name; WaveFlags Flags; float Volume;
//     ReadOnlyCollection<IWaveLayer> Layers; ...
//   }
//
// We bake one snapshot per slot into a fixed-format float buffer so the
// audio thread reads pre-converted samples with no branching on bit depth.

using System;
using System.Text;
using BuzzGUI.Interfaces;

namespace WDE.PedalTracker
{
    /// <summary>
    /// Immutable, audio-thread-safe view of one wavetable slot.
    /// Channels are split into two parallel float[] buffers, padded
    /// at both ends so the interpolator can read PadBefore/PadAfter
    /// taps without bounds-checks.
    /// </summary>
    internal sealed class WaveSnapshot
    {
        public int    SlotIndex;
        public string Name;

        public int    ChannelCount;
        public int    SampleRate;
        public int    RootNote;       // LINEAR MIDI (already converted from
                                       // the Buzz-byte form on IWaveLayer)

        public int    Length;
        public int    LoopStart;
        public int    LoopEnd;
        public bool   Looped;
        public bool   PingPong;

        public float  Volume;

        public float[] L;
        public float[] R;
        public int     PadHead;
        public int     PadTail;
    }

    internal static class WavetableAccess
    {
        public const int MAX_PAD = 16;

        // Last-error breadcrumb – the diagnostics command reads this.
        public static string LastError = "(no attempt yet)";

        /// <summary>
        /// Snapshot every non-null layer of a wavetable slot.  Returns an
        /// array of WaveSnapshots sorted by RootNote (ascending) so the
        /// layer picker can scan in O(N).  Returns null on failure.
        /// </summary>
        public static WaveSnapshot[] SnapAll(IBuzz buzz, int slot)
        {
            if (buzz == null) { LastError = "buzz is null";        return null; }
            if (slot < 1)     { LastError = "slot < 1";            return null; }

            try
            {
                var song = buzz.Song;
                if (song == null) { LastError = "buzz.Song is null"; return null; }

                var wt = song.Wavetable;
                if (wt == null)   { LastError = "song.Wavetable is null"; return null; }

                IWave wave = null;
                try
                {
                    var waves = wt.Waves;
                    if (waves != null && (slot - 1) < waves.Count)
                        wave = waves[slot - 1];
                }
                catch (Exception ex)
                {
                    LastError = $"wt.Waves[{slot - 1}] threw {ex.GetType().Name}: {ex.Message}";
                    return null;
                }

                if (wave == null)
                {
                    LastError = $"slot {slot}: empty";
                    return null;
                }

                var layers = wave.Layers;
                if (layers == null || layers.Count == 0)
                {
                    LastError = $"slot {slot}: wave has no layers";
                    return null;
                }

                // Build a snapshot per non-null layer.
                var list = new System.Collections.Generic.List<WaveSnapshot>(layers.Count);
                int idx = 0;
                foreach (var layer in layers)
                {
                    if (layer == null) { idx++; continue; }
                    var s = SnapOneLayer(wave, layer, slot);
                    if (s != null) list.Add(s);
                    idx++;
                }

                if (list.Count == 0)
                {
                    if (LastError == "(ok)" || LastError.StartsWith("(no"))
                        LastError = $"slot {slot}: every layer failed to snapshot";
                    return null;
                }

                // Sort ascending by RootNote so the picker can binary/linear scan.
                list.Sort((a, b) => a.RootNote.CompareTo(b.RootNote));
                LastError = "(ok)";
                return list.ToArray();
            }
            catch (Exception ex)
            {
                LastError = $"slot {slot}: exception {ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// Pick the most-appropriate layer for a played MIDI note.  Standard
        /// sampler convention: the layer whose RootNote is the largest value
        /// ≤ midiNote, so we pitch-shift up rather than down (preserves
        /// fidelity).  Falls back to the lowest layer if midiNote is below
        /// every RootNote.
        /// </summary>
        public static WaveSnapshot PickLayerForNote(WaveSnapshot[] layers, int midiNote)
        {
            if (layers == null || layers.Length == 0) return null;
            if (layers.Length == 1) return layers[0];

            // Layers sorted ascending by RootNote.
            var best = layers[0];
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].RootNote <= midiNote) best = layers[i];
                else break;
            }
            return best;
        }

        // ────────────────────────────────────────────────────────────────────
        // Single-layer snapshot worker.  Caller has already null-checked the
        // wave and the layer.
        // ────────────────────────────────────────────────────────────────────
        static WaveSnapshot SnapOneLayer(IWave wave, IWaveLayer layer, int slot)
        {
            try
            {
                int channels   = layer.ChannelCount;
                int sampleRate = layer.SampleRate;
                int sampleCnt  = layer.SampleCount;
                int loopStart  = layer.LoopStart;
                int loopEnd    = layer.LoopEnd;

                // RootNote on IWaveformBase is stored as a Buzz note BYTE
                // (octave-nibble | (semitone+1)), NOT a linear MIDI integer.
                // ReBuzz's WavetableCore loads it via
                //   BuzzNote.FromMIDINote(basenote - 12)
                // where basenote is the WAV file's standard MIDI root.
                int rootByte = layer.RootNote;
                int oct  = (rootByte >> 4) & 0xF;
                int semi = (rootByte & 0xF) - 1;
                if (semi < 0) semi = 0;
                if (semi > 11) semi = 11;
                int rootNoteMidi = oct * 12 + semi;

                if (sampleCnt <= 0) return null;
                if (channels < 1) channels = 1;
                if (channels > 2) channels = 2;
                if (loopEnd > sampleCnt) loopEnd = sampleCnt;
                if (loopStart < 0)        loopStart = 0;

                var flags    = wave.Flags;
                bool looped  = (flags & WaveFlags.Loop)              != 0;
                bool pingPng = (flags & WaveFlags.BidirectionalLoop) != 0;
                if (loopStart >= loopEnd) { looped = false; pingPng = false; }

                int padded = sampleCnt + 2 * MAX_PAD;
                var L = new float[padded];
                var R = new float[padded];

                // void GetDataAsFloat(float[] output,
                //                     int outoffset, int outstride,
                //                     int channel,   int offset,    int count);
                layer.GetDataAsFloat(L, MAX_PAD, 1, 0, 0, sampleCnt);
                if (channels == 2)
                    layer.GetDataAsFloat(R, MAX_PAD, 1, 1, 0, sampleCnt);
                else
                    Array.Copy(L, MAX_PAD, R, MAX_PAD, sampleCnt);

                return new WaveSnapshot
                {
                    SlotIndex    = slot,
                    Name         = wave.Name ?? $"#{slot}",
                    ChannelCount = channels,
                    SampleRate   = sampleRate,
                    RootNote     = rootNoteMidi,
                    Length       = sampleCnt,
                    LoopStart    = loopStart,
                    LoopEnd      = loopEnd,
                    Looped       = looped,
                    PingPong     = pingPng,
                    Volume       = wave.Volume,
                    L            = L,
                    R            = R,
                    PadHead      = MAX_PAD,
                    PadTail      = MAX_PAD,
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolve the IWave instance for a slot (1-based) without
        /// snapshotting it.  Used by the live-invalidation subscription.
        /// Returns null on any failure.
        /// </summary>
        public static IWave ResolveWave(IBuzz buzz, int slot)
        {
            if (buzz == null || slot < 1) return null;
            try
            {
                var waves = buzz.Song?.Wavetable?.Waves;
                if (waves == null || (slot - 1) >= waves.Count) return null;
                return waves[slot - 1];
            }
            catch { return null; }
        }

        // ────────────────────────────────────────────────────────────────────
        // API inspection — keeps working as a diagnostic aid.  Dumps
        // line-by-line via the supplied writer so each line gets its own
        // log entry (avoiding any single-message length truncation).
        // ────────────────────────────────────────────────────────────────────
        public static string InspectApi(IBuzz buzz)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Wavetable API inspection ===");
            try
            {
                if (buzz == null) { sb.AppendLine("buzz is null"); return sb.ToString(); }
                var song = buzz.Song; if (song == null) { sb.AppendLine("buzz.Song null"); return sb.ToString(); }
                var wt   = song.Wavetable; if (wt == null) { sb.AppendLine("song.Wavetable null"); return sb.ToString(); }

                sb.AppendLine("Wavetable: " + wt.GetType().FullName);
                var waves = wt.Waves;
                sb.AppendFormat("  Waves count: {0}\n", waves?.Count ?? -1);

                IWave first = null;
                if (waves != null)
                {
                    for (int i = 0; i < waves.Count && i < 8; i++)
                    {
                        var w = waves[i];
                        sb.AppendFormat("  [{0}] {1}\n", i,
                            w == null ? "null" : (w.Name ?? w.GetType().Name));
                        if (first == null && w != null) first = w;
                    }
                }

                if (first != null)
                {
                    sb.AppendFormat("First wave runtime type: {0}\n", first.GetType().FullName);
                    sb.AppendFormat("  Name:   {0}\n", first.Name);
                    sb.AppendFormat("  Flags:  {0}\n", first.Flags);
                    sb.AppendFormat("  Volume: {0}\n", first.Volume);

                    IWaveLayer layer = null;
                    var ls = first.Layers;
                    if (ls != null)
                        foreach (var l in ls) { if (l != null) { layer = l; break; } }

                    if (layer != null)
                    {
                        sb.AppendFormat("First layer runtime type: {0}\n", layer.GetType().FullName);
                        sb.AppendFormat("  Path:         {0}\n", layer.Path);
                        sb.AppendFormat("  Format:       {0}\n", layer.Format);
                        sb.AppendFormat("  SampleCount:  {0}\n", layer.SampleCount);
                        sb.AppendFormat("  SampleRate:   {0}\n", layer.SampleRate);
                        sb.AppendFormat("  RootNote:     {0} (Buzz byte 0x{0:X2}; linear MIDI {1})\n",
                            layer.RootNote,
                            ((layer.RootNote >> 4) & 0xF) * 12 +
                            System.Math.Max(0, (layer.RootNote & 0xF) - 1));
                        sb.AppendFormat("  ChannelCount: {0}\n", layer.ChannelCount);
                        sb.AppendFormat("  LoopStart:    {0}\n", layer.LoopStart);
                        sb.AppendFormat("  LoopEnd:      {0}\n", layer.LoopEnd);
                    }
                    else sb.AppendLine("(no non-null layer)");
                }
                else sb.AppendLine("(no non-null waves – load a sample into slot 1 first)");
            }
            catch (Exception ex)
            {
                sb.AppendFormat("Inspection threw {0}: {1}\n", ex.GetType().Name, ex.Message);
            }
            return sb.ToString();
        }
    }
}
