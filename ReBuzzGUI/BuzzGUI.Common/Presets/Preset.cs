using Buzz.MachineInterface;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using SevenZip.Compression.LZMA;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace BuzzGUI.Common.Presets
{
    public class Preset
    {
        public class Parameter
        {
            [XmlAttribute]
            public string Name;

            [XmlAttribute]
            public byte Group;

            [XmlAttribute]
            public int Index;

            [XmlAttribute]
            public int Track;

            [XmlAttribute]
            public int Value;

            /// <summary>
            /// Fork: the parameter's stable key (IVersionedMachine.ParameterKeys) for a global parameter of a versioned machine, so a
            /// rename can't send the value elsewhere; null in presets saved before ids, and for other machines. Only the XML form
            /// stores it (the copy-and-paste string keeps version 2's layout).
            /// </summary>
            [XmlAttribute]
            public string Id;

            public Parameter() { }

            public Parameter(string name, byte group, int track, int index, int value)
            {
                Name = name;
                this.Group = group;
                this.Track = track;
                this.Index = index;
                this.Value = value;
            }

            public Parameter(BinaryReader br)
            {
                Name = br.ReadString();
                Group = br.ReadByte();
                Index = br.ReadInt16();
                Track = br.ReadInt16();
                Value = br.ReadInt32();
            }

            public void Write(BinaryWriter bw)
            {
                bw.Write(Name);
                bw.Write(Group);
                bw.Write((short)Index);
                bw.Write((short)Track);
                bw.Write(Value);
            }

        }

        public class Attribute
        {
            [XmlAttribute]
            public string Name;

            [XmlAttribute]
            public int Value;

            public Attribute() { }

            public Attribute(IAttribute attribute, bool defvalue)
            {
                Name = attribute.Name;
                Value = defvalue ? attribute.DefValue : attribute.Value;
            }

            public Attribute(BinaryReader br)
            {
                Name = br.ReadString();
                Value = br.ReadInt32();
            }

            public void Write(BinaryWriter bw)
            {
                bw.Write(Name);
                bw.Write(Value);
            }

        }

        public List<Parameter> Parameters { get; set; }
        public List<Attribute> Attributes { get; set; }

        [XmlAttribute]
        public string Machine;

        public string Comment { get; set; }

        public byte[] Data { get; set; }

        public Preset() { }

        public Preset(IMachine machine, bool defvalue, bool includedata)
        {
            Machine = machine.DLL.Name;

            Parameters = new List<Parameter>();
            var keys = KeysOf(machine);

            foreach (IParameterGroup pg in machine.ParameterGroups)
            {
                if (pg.Type == ParameterGroupType.Input) continue;

                for (int t = 0; t < pg.TrackCount; t++)
                {
                    foreach (IParameter p in pg.Parameters)
                    {
                        if ((p.Flags & ParameterFlags.State) == ParameterFlags.State)
                        {
                            var saved = new Parameter(p.Name, (byte)pg.Type, t, p.IndexInGroup, defvalue ? p.DefValue : p.GetValue(t));
                            if (keys != null && pg.Type == ParameterGroupType.Global && p.IndexInGroup >= 0 && p.IndexInGroup < keys.Count)
                                saved.Id = keys[p.IndexInGroup];
                            Parameters.Add(saved);
                        }

                    }
                }
            }

            Attributes = new List<Attribute>();
            foreach (IAttribute a in machine.Attributes) Attributes.Add(new Attribute(a, defvalue));

            if (!defvalue && includedata && (machine.DLL.Info.Flags & MachineInfoFlags.LOAD_DATA_RUNTIME) == MachineInfoFlags.LOAD_DATA_RUNTIME)
            {
                try
                {
                    Data = machine.Data;
                }
                catch (Exception e)
                {
                    PortableMessageBox.Show(e.Message, "Get IMachine.Data");
                }

            }
        }

        // import old preset
        public Preset(IMachine machine, BinaryReader br)
        {
            Machine = machine.DLL.Name;

            int trackcount = br.ReadInt32();
            int valcount = br.ReadInt32();

            if (valcount < 0 || valcount >= 65536) throw new Exception("invalid preset");

            int[] values = new int[valcount];
            for (int i = 0; i < valcount; i++) values[i] = br.ReadInt32();

            Parameters = new List<Parameter>();

            foreach (IParameterGroup pg in machine.ParameterGroups)
            {
                if (pg.Type == ParameterGroupType.Input) continue;

                for (int t = 0; t < (pg.Type == ParameterGroupType.Track ? trackcount : pg.TrackCount); t++)
                {
                    foreach (IParameter p in pg.Parameters)
                    {
                        if ((p.Flags & ParameterFlags.State) == ParameterFlags.State)
                        {
                            Parameters.Add(new Parameter(p.Name, (byte)pg.Type, t, p.IndexInGroup, values[Parameters.Count]));

                            if (Parameters.Count == valcount)
                                goto done;
                        }

                    }
                }
            }
        done:

            Comment = br.ReadASCIIStringWithInt32Length();
        }

        public Preset(IMachine machine, string s)
        {
            s = s.Trim();

            if (s.Length < 10) throw new ArgumentException("invalid preset");
            if (s[0] != '[') throw new ArgumentException("invalid preset");

            s = s.Replace("\r", "");
            s = s.Replace("\n", "");

            int endheader = s.IndexOf(']');
            if (endheader < 0) throw new ArgumentException("invalid preset");

            string header = s.Substring(1, endheader - 1);
            var hw = header.Split('/');
            if (hw.Length != 3 || hw[0] != "Buzz") throw new ArgumentException("invalid preset");
            uint readver = uint.Parse(hw[1]);
            if (readver > version) throw new ArgumentException("invalid preset");
            Machine = hw[2];

            if (machine != null && Machine != machine.DLL.Name) throw new ArgumentException("invalid preset (wrong machine)");

            var compressed = new Ascii85().Decode(s.Substring(endheader + 1).Trim().Replace(" ", ""));
            var presetdata = SevenZipHelper.Decompress(compressed);
            var ms = new MemoryStream(presetdata);
            var br = new BinaryReader(ms);

            int pcount = br.ReadInt32();
            if (pcount < 0 || pcount > 1000) throw new ArgumentException("invalid preset");

            Parameters = new List<Parameter>();
            for (int i = 0; i < pcount; i++) Parameters.Add(new Parameter(br));

            int acount = br.ReadInt32();
            if (acount < 0 || acount > 1000) throw new ArgumentException("invalid preset");

            Attributes = new List<Attribute>();
            for (int i = 0; i < acount; i++) Attributes.Add(new Attribute(br));

            if (readver >= 2)
            {
                int datasize = br.ReadInt32();
                if (datasize < 0) throw new ArgumentException("invalid preset");
                Data = new byte[datasize];
                br.Read(Data, 0, Data.Length);
            }
        }

        const int version = 2;

        public override string ToString()
        {
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);

            bw.Write(Parameters.Count);
            foreach (var p in Parameters) p.Write(bw);

            bw.Write(Attributes.Count);
            foreach (var a in Attributes) a.Write(bw);

            bw.Write(Data != null ? Data.Length : 0);
            if (Data != null) bw.Write(Data, 0, Data.Length);

            var compressed = SevenZipHelper.Compress(ms.ToArray());

            string s = "[Buzz/" + version.ToString() + "/" + Machine + "]" + "\r\n";
            s += new Ascii85().Encode(compressed) + "\r\n";
            return s;
        }

        public string ToXml()
        {
            try
            {
                var sb = new StringBuilder();
                var ws = new XmlWriterSettings() { NamespaceHandling = System.Xml.NamespaceHandling.OmitDuplicates, NewLineOnAttributes = false, Indent = true };
                var w = XmlWriter.Create(sb, ws);

                var s = new XmlSerializer(GetType());
                var ns = new XmlSerializerNamespaces();
                ns.Add("", "");
                s.Serialize(w, this, ns);

                w.Close();

                return sb.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"");
            }
            catch (Exception e)
            {
                PortableMessageBox.Show(string.Format("xml serialization failed: {0}", e.Message));
                return "";
            }
        }

        public static Preset FromXml(string text)
        {
            try
            {
                var s = new XmlSerializer(typeof(Preset));

                var r = XmlReader.Create(new StringReader(text));

                object o = s.Deserialize(r);
                r.Close();

                var preset = o as Preset;
                return preset;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static Preset FromString(string s)
        {
            if (s.Trim().StartsWith("[Buzz/"))
                return new Preset(null, s);
            else
                return FromXml(s);
        }

        bool AreParameterNamesUnique { get { return Parameters.Select(p => p.Name).Distinct().Count() == Parameters.Count(); } }

        public void Apply(IMachine machine, bool setdata)
        {
            if (AreParameterNamesUnique && machine.AreParameterNamesUnique())
            {
                foreach (var p in machine.AllNonInputStateParametersAndTracks())
                    p.Item1.SetValue(p.Item2, GetValueOrDefault(p.Item1, p.Item2));
            }
            else
            {
                try
                {
                    // try to paste as long as names match

                    int index = 0;
                    byte lastgroup = 255;
                    int track = 0;

                    foreach (var p in Parameters)
                    {
                        if (p.Group != lastgroup || track != p.Track)
                        {
                            index = 0;
                            lastgroup = p.Group;
                            track = p.Track;
                        }

                        var pg = machine.ParameterGroups[p.Group];
                        while (index < pg.Parameters.Count && ((pg.Parameters[index].Flags & ParameterFlags.State) == 0)) index++;
                        if (index >= pg.Parameters.Count || p.Name != pg.Parameters[index].Name) break;

                        pg.Parameters[index].SetValue(p.Track, p.Value);

                        index++;
                    }
                }
                catch (Exception) { }
            }

            foreach (var ma in machine.Attributes)
            {
                var pa = Attributes != null ? Attributes.Where(x => x.Name == ma.Name).SingleOrDefault() : null;
                var val = pa != null ? pa.Value : ma.DefValue;
                if (ma.HasUserDefValue && ma.UserDefValueOverridesPreset) val = ma.UserDefValue;
                if (val != ma.Value) ma.Value = val;
            }

            if (setdata && (machine.DLL.Info.Flags & MachineInfoFlags.LOAD_DATA_RUNTIME) == MachineInfoFlags.LOAD_DATA_RUNTIME)
            {
                try
                {
                    machine.Data = Data;
                }
                catch (Exception e)
                {
                    PortableMessageBox.Show(e.Message, "Set IMachine.Data");
                }
            }

        }

        public int GetValueOrDefault(IParameter parameter, int track)
        {
            // TODO: support non-unique parameter names
            var mp = Find(parameter, track);
            return mp != null ? mp.Value : parameter.DefValue;
        }

        static IReadOnlyList<string> KeysOf(IMachine machine) => (machine?.ManagedMachine as IVersionedMachine)?.ParameterKeys;

        /// <summary>
        /// Fork (docs/specs/machines.md §3.2, "Renamed parameters and user presets"): the saved value for <paramref name="parameter"/>.
        /// A global parameter of a versioned machine is found by its id when the preset stored ids. A preset saved before ids is
        /// found by name, as Buzz always did; only on a machine that renamed parameters (IRenamedParameters), a former name stands
        /// for its parameter while the preset doesn't name that parameter by its current name, and a name a former name took over
        /// (Neo Tune's "Amount") is not read as the parameter that has it now. Every other parameter and machine: by name as before.
        /// </summary>
        Parameter Find(IParameter parameter, int track)
        {
            byte group = (byte)parameter.Group.Type;
            Parameter ByName(string name) => Parameters.FirstOrDefault(p => p.Name == name && p.Group == group && p.Track == track);

            var machine = parameter.Group.Machine;
            var keys = parameter.Group.Type == ParameterGroupType.Global ? KeysOf(machine) : null;
            int index = parameter.IndexInGroup;
            if (keys == null || index < 0 || index >= keys.Count) return ByName(parameter.Name);
            string key = keys[index];

            if (Parameters.Any(p => p.Id != null && p.Group == group))
                return Parameters.FirstOrDefault(p => p.Id == key && p.Group == group && p.Track == track);

            var former = (machine.ManagedMachine as IRenamedParameters)?.FormerParameterNames;
            if (former == null || former.Count == 0) return ByName(parameter.Name);

            var globals = parameter.Group.Parameters;
            string CurrentName(string k)
            {
                for (int i = 0; i < keys.Count && i < globals.Count; i++)
                    if (keys[i] == k) return globals[i].Name;
                return null;
            }
            // A saved name is a former one when it is listed and the preset doesn't name its parameter by the current name.
            string FormerKey(string name) =>
                name != null && former.TryGetValue(name, out var k) && CurrentName(k) is { } current && ByName(current) == null ? k : null;

            var mine = ByName(parameter.Name);
            if (mine != null)
            {
                string meant = FormerKey(mine.Name);
                if (meant == null || meant == key) return mine;
            }
            foreach (var p in Parameters)
                if (p.Group == group && p.Track == track && FormerKey(p.Name) == key) return p;
            return null;
        }

        static string cachedValidPresetString;
        static string cachedInvalidPresetString;

        public static bool IsValidPresetString(string s)
        {
            if (s == cachedValidPresetString)
                return true;
            else if (s == cachedInvalidPresetString)
                return false;

            if (s.Trim().StartsWith("[Buzz/"))
            {
                cachedValidPresetString = s;
                return true;
            }

            try
            {
                using (var r = XmlReader.Create(new StringReader(s)))
                {
                    r.MoveToContent();
                    if (r.Name == "Preset")
                    {
                        cachedValidPresetString = s;
                        return true;
                    }
                }

            }
            catch { }

            cachedInvalidPresetString = s;
            return false;
        }

    }
}
