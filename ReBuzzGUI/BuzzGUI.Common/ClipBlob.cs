#nullable disable
// Fork addition (neo-buzz, docs/specs/clips.md §7, wave CU2): the pattern editor's data blob as bytes, read and written with no
// editor and no machines: PatternXP's format (versions 1 to 3, rows) and the Modern Pattern Editor's own (0xFF, version 1, exact
// times and the rows of each beat). The layouts are those of PatternEditorUtils (ProcessEditorData and the two Create...Data
// writers); this is the same code without the editor's objects, so a converter can read what a song holds and a test can compare.
//
// A blob may carry a stamp after its patterns (clips.md §7.2): the editors stop reading at the end of the last pattern.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BuzzGUI.Interfaces;

namespace BuzzGUI.Common
{
    public enum BlobFormat { None, Pxp1, Pxp2, Pxp3, Mpe1 }

    /// <summary>A column of the blob: a machine's parameter (by its index, negative for the editor's own), a track, and events in pattern time.</summary>
    public sealed class BlobColumn
    {
        public string Machine;
        public int ParamIndex;                  // globals first, then the track parameters; negative: the editor's own (MIDI lanes, PatternXP's)
        public int Track;
        public bool Graphical;
        public List<PatternEvent> Events = new List<PatternEvent>();   // Time in 1/240 tick (PXP rows are already converted), Duration 0
        public List<int> BeatRows = new List<int>();                    // MPE only: rows in each beat
    }

    public sealed class BlobPattern
    {
        public string Name;
        public int Beats;                       // MPE only
        public int RowsPerBeat = 4;
        public List<BlobColumn> Columns = new List<BlobColumn>();
    }

    public sealed class BlobData
    {
        public BlobFormat Format;
        public List<BlobPattern> Patterns = new List<BlobPattern>();
        /// <summary>The stamp after the patterns (clips.md §7.2): 32 bytes of SHA-256 of the store content, or null.</summary>
        public byte[] Stamp;
        /// <summary>Why the blob could not be read whole (nothing after that point is in <see cref="Patterns"/>), or null.</summary>
        public string Problem;
        /// <summary>The number of events in every column of every pattern.</summary>
        public int EventCount => Patterns.Sum(p => p.Columns.Sum(c => c.Events.Count));
    }

    public static class ClipBlob
    {
        public const byte PxpVersion = 3;
        const byte NotPxp = 255;
        const byte MpeVersion = 1;
        static readonly byte[] StampMagic = { (byte)'N', (byte)'B', (byte)'S', (byte)'T', 1 };
        public const int PxpTime = PatternEvent.TimeBase * 4;       // the pattern time of a beat: a PXP row is this over the rows per beat

        sealed class Reader
        {
            public readonly byte[] Data;
            public int Pos;
            public string Problem;
            public Reader(byte[] data, int pos) { Data = data; Pos = pos; }
            public bool Ok => Problem == null;

            public int Int()
            {
                if (Pos + 4 > Data.Length) { Problem ??= "ends inside a number"; Pos = Data.Length; return 0; }
                int v = BitConverter.ToInt32(Data, Pos);
                Pos += 4;
                return v;
            }

            public bool Bool()
            {
                if (Pos + 1 > Data.Length) { Problem ??= "ends inside a flag"; return false; }
                return Data[Pos++] != 0;
            }

            public string String()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    if (Pos >= Data.Length) { Problem ??= "ends inside a name"; return sb.ToString(); }
                    byte b = Data[Pos++];
                    if (b == 0) return sb.ToString();
                    sb.Append((char)b);
                }
            }
        }

        /// <summary>Reads a blob; never throws. An empty or unknown blob has no patterns (and says why in <see cref="BlobData.Problem"/> if unknown).</summary>
        public static BlobData Parse(byte[] data)
        {
            var result = new BlobData();
            if (data == null || data.Length == 0) return result;
            byte version = data[0];
            var r = new Reader(data, 1);
            if (data.Length < 2)
                return result;
            if (version >= 1 && version <= PxpVersion)
            {
                result.Format = (BlobFormat)((int)BlobFormat.Pxp1 + version - 1);
                int count = r.Int();
                if (count < 0 || count > 1 << 20) r.Problem ??= "a pattern count out of range";
                for (int i = 0; i < count && r.Ok; i++)
                    result.Patterns.Add(ReadPxpPattern(r, version));
            }
            else if (version == NotPxp)
            {
                int mpe = data[1];
                r.Pos = 2;
                r.Int();                                            // the data size
                if (mpe != MpeVersion)
                {
                    result.Problem = $"editor data version {mpe} is not one we read";
                    return result;
                }
                result.Format = BlobFormat.Mpe1;
                int count = r.Int();
                if (count < 0 || count > 1 << 20) r.Problem ??= "a pattern count out of range";
                for (int i = 0; i < count && r.Ok; i++)
                    result.Patterns.Add(ReadMpePattern(r));
            }
            else
            {
                result.Problem = $"editor data starting with {version} is not in a format we read";
                return result;
            }
            result.Problem = r.Problem;
            if (r.Ok && r.Pos + StampMagic.Length + 32 <= data.Length && data.Skip(r.Pos).Take(StampMagic.Length).SequenceEqual(StampMagic))
                result.Stamp = data.Skip(r.Pos + StampMagic.Length).Take(32).ToArray();
            return result;
        }

        static BlobPattern ReadPxpPattern(Reader r, byte version)
        {
            var p = new BlobPattern { Name = r.String() };
            p.RowsPerBeat = version > 1 ? r.Int() : 4;
            int count = r.Int();
            if (p.RowsPerBeat < 1) { r.Problem ??= $"{p.Name}: rows per beat {p.RowsPerBeat}"; return p; }
            if (count < 0 || count > 1 << 20) { r.Problem ??= "a column count out of range"; return p; }
            for (int i = 0; i < count && r.Ok; i++)
            {
                var c = new BlobColumn { Machine = r.String(), ParamIndex = r.Int(), Track = r.Int() };
                if (version >= 3) c.Graphical = r.Bool();
                int n = r.Int();
                if (n < 0 || n > (r.Data.Length - r.Pos) / 8 + 1) { r.Problem ??= "an event count out of range"; return p; }
                for (int k = 0; k < n && r.Ok; k++)
                {
                    int row = r.Int(), value = r.Int();
                    c.Events.Add(new PatternEvent(row * PatternEvent.TimeBase * 4 / p.RowsPerBeat, value));
                }
                p.Columns.Add(c);
            }
            return p;
        }

        static BlobPattern ReadMpePattern(Reader r)
        {
            var p = new BlobPattern { Name = r.String() };
            p.Beats = r.Int();
            p.RowsPerBeat = r.Int();
            int count = r.Int();
            if (p.Beats < 0 || p.Beats > 1 << 20) { r.Problem ??= "a beat count out of range"; return p; }
            if (count < 0 || count > 1 << 20) { r.Problem ??= "a column count out of range"; return p; }
            for (int i = 0; i < count && r.Ok; i++)
            {
                var c = new BlobColumn { Machine = r.String(), ParamIndex = r.Int(), Track = r.Int(), Graphical = r.Bool() };
                int n = r.Int();
                if (n < 0 || n > (r.Data.Length - r.Pos) / 8 + 1) { r.Problem ??= "an event count out of range"; return p; }
                for (int k = 0; k < n && r.Ok; k++)
                {
                    int time = r.Int(), value = r.Int();
                    c.Events.Add(new PatternEvent(time, value));
                }
                for (int b = 0; b < p.Beats && r.Ok; b++)
                    c.BeatRows.Add(r.Int());
                p.Columns.Add(c);
            }
            return p;
        }

        static void Int(List<byte> d, int v) => d.AddRange(BitConverter.GetBytes(v));

        static void Str(List<byte> d, string s)
        {
            foreach (char ch in s ?? "") d.Add(ch < 256 ? (byte)ch : (byte)'?');
            d.Add(0);
        }

        /// <summary>The Modern Pattern Editor's data (PatternEditorUtils.CreateModernPatternEditorData): exact times, the rows of each beat.</summary>
        public static byte[] WriteMpe(IEnumerable<BlobPattern> patterns, byte[] stamp = null)
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
            if (stamp != null) { data.AddRange(StampMagic); data.AddRange(stamp); }
            return data.ToArray();
        }

        /// <summary>
        /// PatternXP's data, version 3 (PatternEditorUtils.CreatePatternXPPatternData): events on rows. Each event goes to the start of its row,
        /// as the editor's own save does (GetEventsQuantized, then a division by a row's length); <paramref name="moved"/> counts the events
        /// whose time that changed.
        /// </summary>
        public static byte[] WritePxp(IEnumerable<BlobPattern> patterns, out int moved, byte[] stamp = null)
        {
            moved = 0;
            var data = new List<byte> { PxpVersion };
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
                        int q = Quantized(e.Time, rpb);
                        if (q != e.Time) moved++;
                        int rowLength = PxpTime / rpb;
                        Int(data, q / Math.Max(1, rowLength));
                        Int(data, e.Value);
                    }
                }
            }
            if (stamp != null) { data.AddRange(StampMagic); data.AddRange(stamp); }
            return data.ToArray();
        }

        /// <summary>MPEPatternColumn.GetTimeQuantized(time, rpb): the start of the row a time is in, a row being an integer number of pattern time.</summary>
        public static int Quantized(int time, int rpb)
        {
            int beat = time / PxpTime;
            double rowTime = PxpTime / rpb;
            int row = (int)((time - beat * PxpTime) / rowTime);
            return (int)(beat * PxpTime + row * rowTime);
        }

        /// <summary>The stamp of a blob (clips.md §7.2), or null if it has none.</summary>
        public static byte[] StampOf(byte[] blob) => Parse(blob).Stamp;
    }
}
