#nullable disable
// Neo FM6 (docs/specs/machines.md §5): DX7 voice data. Reads 32-voice banks ("bulk dumps", 4104
// bytes with the SysEx header, or the bare 4096 bytes) and single voices (163 / 155 bytes), maps a
// voice onto Neo FM6's parameters and back, and writes banks. Voice data keeps the DX7's own order
// (operator 6 first); the parameter values are the DX7's own, so nothing is rescaled.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NeoBuzz.Fm6
{
    internal static class Sysex
    {
        public const int VoiceSize = 155;       // unpacked voice
        public const int PackedSize = 128;      // a voice in a 32-voice bank
        public const int BankDataSize = 32 * PackedSize;

        /// <summary>One voice: its name and 155 bytes of DX7 voice data.</summary>
        internal sealed class Voice
        {
            public Voice(string name, byte[] data)
            {
                Name = name;
                Data = data;
            }

            public string Name { get; }
            public byte[] Data { get; }
        }

        /// <summary>Reads a .syx file: a 32-voice bank or one voice. Throws with a readable message.</summary>
        public static List<Voice> Read(byte[] file)
        {
            if (file == null || file.Length == 0) throw new InvalidDataException("the file is empty");
            // A file may hold several SysEx messages; take the first DX7 voice data in it.
            for (int at = 0; at < file.Length; at++)
            {
                if (file[at] != 0xF0) continue;
                if (at + 6 > file.Length || file[at + 1] != 0x43) continue;
                int format = file[at + 3];
                int count = (file[at + 4] << 7) | file[at + 5];
                int start = at + 6;
                if (format == 9 && count == BankDataSize && start + BankDataSize <= file.Length)
                {
                    Check(file, start, BankDataSize);
                    return Bank(file, start);
                }
                if (format == 0 && count == VoiceSize && start + VoiceSize <= file.Length)
                {
                    Check(file, start, VoiceSize);
                    var data = new byte[VoiceSize];
                    Array.Copy(file, start, data, 0, VoiceSize);
                    return new List<Voice> { new Voice(NameOf(data), Clean(data)) };
                }
            }
            if (file.Length == BankDataSize) return Bank(file, 0);
            if (file.Length == VoiceSize) return new List<Voice> { new Voice(NameOf(file), Clean((byte[])file.Clone())) };
            throw new InvalidDataException("no DX7 voice data found (expected a 32-voice bank or a single voice)");
        }

        static void Check(byte[] file, int start, int count)
        {
            if (start + count >= file.Length) return;   // no checksum byte: accept
            int sum = 0;
            for (int i = 0; i < count; i++) sum += file[start + i];
            int expected = (-sum) & 0x7F;
            if (file[start + count] != expected)
                throw new InvalidDataException($"checksum mismatch (the file says {file[start + count]}, the data adds up to {expected}): the file is damaged");
        }

        static List<Voice> Bank(byte[] file, int start)
        {
            var voices = new List<Voice>(32);
            for (int v = 0; v < 32; v++)
            {
                var data = Unpack(file, start + v * PackedSize);
                voices.Add(new Voice(NameOf(data), data));
            }
            return voices;
        }

        static string NameOf(byte[] voice)
        {
            var sb = new StringBuilder(10);
            for (int i = 145; i < 155; i++)
            {
                int c = voice[i] & 0x7F;
                sb.Append(c >= 32 && c < 127 ? (char)c : ' ');
            }
            return sb.ToString().TrimEnd();
        }

        // Keeps every value in its DX7 range (damaged or odd files stay playable).
        static readonly byte[] OpMax = { 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 3, 3, 7, 3, 7, 99, 1, 31, 99, 14 };
        static readonly byte[] GlobalMax = { 99, 99, 99, 99, 99, 99, 99, 99, 31, 7, 1, 99, 99, 99, 99, 1, 5, 7, 48 };

        static byte[] Clean(byte[] v)
        {
            for (int op = 0; op < 6; op++)
                for (int i = 0; i < 21; i++)
                    v[op * 21 + i] = (byte)Math.Min(v[op * 21 + i] & 0x7F, OpMax[i]);
            for (int i = 0; i < GlobalMax.Length; i++) v[126 + i] = (byte)Math.Min(v[126 + i] & 0x7F, GlobalMax[i]);
            return v;
        }

        /// <summary>A bank's 128-byte packed voice → 155 bytes.</summary>
        public static byte[] Unpack(byte[] packed, int at)
        {
            var v = new byte[VoiceSize];
            for (int op = 0; op < 6; op++)
            {
                int p = at + op * 17, u = op * 21;
                for (int i = 0; i < 11; i++) v[u + i] = packed[p + i];     // rates, levels, break, depths
                v[u + 11] = (byte)(packed[p + 11] & 3);                   // left curve
                v[u + 12] = (byte)((packed[p + 11] >> 2) & 3);            // right curve
                v[u + 13] = (byte)(packed[p + 12] & 7);                   // rate scaling
                v[u + 20] = (byte)((packed[p + 12] >> 3) & 15);           // detune
                v[u + 14] = (byte)(packed[p + 13] & 3);                   // amp mod sens
                v[u + 15] = (byte)((packed[p + 13] >> 2) & 7);            // velocity sens
                v[u + 16] = packed[p + 14];                               // output level
                v[u + 17] = (byte)(packed[p + 15] & 1);                   // mode
                v[u + 18] = (byte)((packed[p + 15] >> 1) & 31);           // coarse
                v[u + 19] = packed[p + 16];                               // fine
            }
            for (int i = 0; i < 8; i++) v[126 + i] = packed[at + 102 + i];   // pitch envelope
            v[134] = (byte)(packed[at + 110] & 31);                         // algorithm
            v[135] = (byte)(packed[at + 111] & 7);                          // feedback
            v[136] = (byte)((packed[at + 111] >> 3) & 1);                   // osc key sync
            for (int i = 0; i < 4; i++) v[137 + i] = packed[at + 112 + i];  // LFO speed, delay, PMD, AMD
            v[141] = (byte)(packed[at + 116] & 1);                          // LFO key sync
            v[142] = (byte)((packed[at + 116] >> 1) & 7);                   // LFO wave
            v[143] = (byte)((packed[at + 116] >> 4) & 7);                   // pitch mod sens
            v[144] = packed[at + 117];                                      // transpose
            for (int i = 0; i < 10; i++) v[145 + i] = packed[at + 118 + i];
            return Clean(v);
        }

        /// <summary>155 bytes → a bank's 128-byte packed voice.</summary>
        public static byte[] Pack(byte[] v)
        {
            var p = new byte[PackedSize];
            for (int op = 0; op < 6; op++)
            {
                int o = op * 17, u = op * 21;
                for (int i = 0; i < 11; i++) p[o + i] = v[u + i];
                p[o + 11] = (byte)((v[u + 11] & 3) | ((v[u + 12] & 3) << 2));
                p[o + 12] = (byte)((v[u + 13] & 7) | ((v[u + 20] & 15) << 3));
                p[o + 13] = (byte)((v[u + 14] & 3) | ((v[u + 15] & 7) << 2));
                p[o + 14] = v[u + 16];
                p[o + 15] = (byte)((v[u + 17] & 1) | ((v[u + 18] & 31) << 1));
                p[o + 16] = v[u + 19];
            }
            for (int i = 0; i < 8; i++) p[102 + i] = v[126 + i];
            p[110] = (byte)(v[134] & 31);
            p[111] = (byte)((v[135] & 7) | ((v[136] & 1) << 3));
            for (int i = 0; i < 4; i++) p[112 + i] = v[137 + i];
            p[116] = (byte)((v[141] & 1) | ((v[142] & 7) << 1) | ((v[143] & 7) << 4));
            p[117] = v[144];
            for (int i = 0; i < 10; i++) p[118 + i] = v[145 + i];
            return p;
        }

        /// <summary>A 32-voice bank as a SysEx message (4104 bytes); missing voices are INIT VOICE.</summary>
        public static byte[] WriteBank(IList<Voice> voices)
        {
            var file = new byte[6 + BankDataSize + 2];
            file[0] = 0xF0; file[1] = 0x43; file[2] = 0x00; file[3] = 0x09; file[4] = 0x20; file[5] = 0x00;
            int sum = 0;
            for (int v = 0; v < 32; v++)
            {
                var data = v < voices.Count ? voices[v].Data : InitVoice();
                var packed = Pack(data);
                Array.Copy(packed, 0, file, 6 + v * PackedSize, PackedSize);
                foreach (var b in packed) sum += b;
            }
            file[6 + BankDataSize] = (byte)((-sum) & 0x7F);
            file[7 + BankDataSize] = 0xF7;
            return file;
        }

        /// <summary>One voice as a SysEx message (163 bytes), as the DX7 sends its edit buffer.</summary>
        public static byte[] WriteVoice(byte[] voice)
        {
            var file = new byte[6 + VoiceSize + 2];
            file[0] = 0xF0; file[1] = 0x43; file[2] = 0x00; file[3] = 0x00; file[4] = 0x01; file[5] = 0x1B;
            int sum = 0;
            for (int i = 0; i < VoiceSize; i++)
            {
                file[6 + i] = (byte)(voice[i] & 0x7F);
                sum += file[6 + i];
            }
            file[6 + VoiceSize] = (byte)((-sum) & 0x7F);
            file[7 + VoiceSize] = 0xF7;
            return file;
        }

        /// <summary>The DX7's INIT VOICE: algorithm 1, operator 1 alone at full level, a plain sine.</summary>
        public static byte[] InitVoice()
        {
            var v = new byte[VoiceSize];
            for (int op = 0; op < 6; op++)
            {
                int u = op * 21;
                for (int i = 0; i < 4; i++) v[u + i] = 99;
                v[u + 4] = 99; v[u + 5] = 99; v[u + 6] = 99; v[u + 7] = 0;
                v[u + 8] = 39;
                v[u + 16] = (byte)(op == 5 ? 99 : 0);   // op 1 is stored last
                v[u + 18] = 1;
                v[u + 20] = 7;
            }
            for (int i = 0; i < 4; i++) { v[126 + i] = 99; v[130 + i] = 50; }
            v[136] = 1;
            v[137] = 35;
            v[141] = 1;
            v[143] = 3;
            v[144] = 24;
            var name = Encoding.ASCII.GetBytes("INIT VOICE");
            Array.Copy(name, 0, v, 145, 10);
            return v;
        }

        // Voice data offsets of an operator's fields (within its 21 bytes) for each parameter offset.
        static readonly (int Param, int Data)[] OpFields =
        {
            (P.O1Level, 16), (P.O1Mode, 17), (P.O1Coarse, 18), (P.O1Fine, 19), (P.O1Detune, 20),
            (P.O1R1, 0), (P.O1R2, 1), (P.O1R3, 2), (P.O1R4, 3), (P.O1L1, 4), (P.O1L2, 5), (P.O1L3, 6), (P.O1L4, 7),
            (P.O1Break, 8), (P.O1LDepth, 9), (P.O1RDepth, 10), (P.O1LCurve, 11), (P.O1RCurve, 12),
            (P.O1RateSc, 13), (P.O1AmpSens, 14), (P.O1VelSens, 15),
        };

        static readonly (int Param, int Data)[] GlobalFields =
        {
            (P.PegR1, 126), (P.PegR2, 127), (P.PegR3, 128), (P.PegR4, 129),
            (P.PegL1, 130), (P.PegL2, 131), (P.PegL3, 132), (P.PegL4, 133),
            (P.Algorithm, 134), (P.Feedback, 135), (P.OscSync, 136),
            (P.LfoSpeed, 137), (P.LfoDelay, 138), (P.LfoPmd, 139), (P.LfoAmd, 140), (P.LfoSync, 141), (P.LfoWave, 142),
            (P.PitchSens, 143), (P.Transpose, 144),
        };

        /// <summary>
        /// The parameter values (raw) a voice sets: every operator and voice parameter of the DX7.
        /// Operators come on with a sine wave; performance settings (voice mode, glide, …) stay.
        /// </summary>
        public static List<(int Param, int Value)> ToParams(byte[] voice)
        {
            var list = new List<(int, int)>();
            for (int op = 0; op < 6; op++)
            {
                int u = (5 - op) * 21;   // operator 6 is stored first
                list.Add((FmEngine.OpParam(op, 0), 1));
                list.Add((FmEngine.OpParam(op, P.O1Wave - P.O1On), 0));
                foreach (var (param, data) in OpFields)
                    list.Add((param - P.O1On + FmEngine.OpParam(op, 0), voice[u + data]));
            }
            foreach (var (param, data) in GlobalFields) list.Add((param, voice[data]));
            return list;
        }

        /// <summary>A voice from parameter values (raw); the name is cut or padded to 10 characters.</summary>
        public static byte[] FromParams(Func<int, int> value, string name)
        {
            var v = new byte[VoiceSize];
            for (int op = 0; op < 6; op++)
            {
                int u = (5 - op) * 21;
                foreach (var (param, data) in OpFields)
                    v[u + data] = (byte)Math.Clamp(value(param - P.O1On + FmEngine.OpParam(op, 0)), 0, 127);
                // An operator switched off is silent on the DX7 too: level 0.
                if (value(FmEngine.OpParam(op, 0)) == 0) v[u + 16] = 0;
            }
            foreach (var (param, data) in GlobalFields) v[data] = (byte)Math.Clamp(value(param), 0, 127);
            var bytes = Encoding.ASCII.GetBytes((name ?? "").PadRight(10).Substring(0, 10));
            for (int i = 0; i < 10; i++) v[145 + i] = (byte)(bytes[i] & 0x7F);
            return Clean(v);
        }
    }
}
