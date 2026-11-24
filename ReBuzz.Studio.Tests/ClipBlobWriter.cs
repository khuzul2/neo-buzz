// Test support (clips.md §7, clean-break.md CB1): writes the pattern editors' data blobs as the editors did, so the tests can make what a .nbl of
// format 1 or 2 holds and open-time migration (ClipMigration, ClipBlob.Parse) reads. The product has only the reader.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Tests
{
    static class ClipBlobWriter
    {
        const byte NotPxp = 255;
        const byte MpeVersion = 1;

        static void Int(List<byte> d, int v) => d.AddRange(BitConverter.GetBytes(v));

        static void Str(List<byte> d, string s)
        {
            foreach (char ch in s ?? "") d.Add(ch < 256 ? (byte)ch : (byte)'?');
            d.Add(0);
        }

        /// <summary>The Modern Pattern Editor's data (PatternEditorUtils.CreateModernPatternEditorData): exact times, the rows of each beat.</summary>
        public static byte[] WriteMpe(IEnumerable<BlobPattern> patterns)
        {
            var data = new List<byte> { NotPxp, MpeVersion };
            var list = patterns.ToList();
            Int(data, list.Count);
            foreach (var p in list)
            {
                Str(data, p.Name);
                Int(data, p.Beats);
                Int(data, p.RowsPerBeat);
                Int(data, p.Columns.Count);
                foreach (var c in p.Columns)
                {
                    Str(data, c.Machine);
                    Int(data, c.ParamIndex);
                    Int(data, c.Track);
                    data.Add(c.Graphical ? (byte)1 : (byte)0);
                    Int(data, c.Events.Count);
                    foreach (var e in c.Events) { Int(data, e.Time); Int(data, e.Value); }
                    for (int b = 0; b < p.Beats; b++)
                        Int(data, b < c.BeatRows.Count ? c.BeatRows[b] : p.RowsPerBeat);
                }
            }
            int dataSize = data.Count + sizeof(int);
            data.InsertRange(2, BitConverter.GetBytes(dataSize));
            return data.ToArray();
        }

        /// <summary>PatternXP's data, version 3: events on rows, each at the start of its row.</summary>
        public static byte[] WritePxp(IEnumerable<BlobPattern> patterns)
        {
            var data = new List<byte> { ClipBlob.PxpVersion };
            var list = patterns.ToList();
            Int(data, list.Count);
            foreach (var p in list)
            {
                Str(data, p.Name);
                int rpb = Math.Max(1, p.RowsPerBeat);
                Int(data, rpb);
                Int(data, p.Columns.Count);
                foreach (var c in p.Columns)
                {
                    Str(data, c.Machine);
                    Int(data, c.ParamIndex);
                    Int(data, c.Track);
                    data.Add(c.Graphical ? (byte)1 : (byte)0);
                    var events = c.Events.OrderBy(e => e.Time).ToList();
                    Int(data, events.Count);
                    foreach (var e in events)
                    {
                        int rowLength = ClipBlob.PxpTime / rpb;
                        Int(data, Quantized(e.Time, rpb) / Math.Max(1, rowLength));
                        Int(data, e.Value);
                    }
                }
            }
            return data.ToArray();
        }

        /// <summary>The start of the row a time is in, a row being an integer number of pattern time.</summary>
        static int Quantized(int time, int rpb)
        {
            int beat = time / ClipBlob.PxpTime;
            double rowTime = ClipBlob.PxpTime / rpb;
            int row = (int)((time - beat * ClipBlob.PxpTime) / rowTime);
            return (int)(beat * ClipBlob.PxpTime + row * rowTime);
        }
    }
}
