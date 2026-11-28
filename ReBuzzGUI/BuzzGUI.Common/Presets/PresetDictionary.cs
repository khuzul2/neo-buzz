using BuzzGUI.Interfaces;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace BuzzGUI.Common.Presets
{
    public class PresetDictionary : SerializableDictionary<Preset>
    {
        [XmlIgnore]
        public string Filename { get; set; }

        public PresetDictionary()
        {
        }

        public static void Init()
        {
            Task.Factory.StartNew(() =>
            {
                XmlSerializer.FromTypes(new[] { typeof(PresetDictionary), typeof(Preset), typeof(Preset.Parameter), typeof(Preset.Attribute) });
            });
        }

        public static PresetDictionary Load(IMachine machine, string filename)
        {
            if (Path.GetExtension(filename) == ".prs")
                return new PresetDictionary(machine, filename);
            else if (Path.GetExtension(filename) == ".xml")
                return LoadXml(machine.DLL.Name, filename);

            return null;
        }

        PresetDictionary(IMachine machine, string filename)
        {
            var br = new BinaryReader(File.OpenRead(filename));

            int ver = br.ReadInt32();
            if (ver != 1) throw new Exception("wrong preset file version");

            string libname = br.ReadASCIIStringWithInt32Length();
            //if (libname.Replace("(Debug build)", "").Trim() != machine.DLL.Name)
            if (libname.Replace("(Debug build)", "").Trim() != machine.DLL.Info.Name.Replace("(Debug build)", "").Trim())
                throw new Exception("preset file is for a different machine");

            int count = br.ReadInt32();
            if (count < 0 || count > 65536) throw new Exception("invalid preset count");

            for (int i = 0; i < count; i++)
            {
                string name = br.ReadASCIIStringWithInt32Length();
                this[name] = new Preset(machine, br);
            }

        }

        /// <summary>A .prs.xml file, read without a machine (its DLL's name fills a preset's Machine when the file leaves it out).</summary>
        public static PresetDictionary LoadXml(string dllName, string filename)
        {
            var s = new XmlSerializer(typeof(PresetDictionary));

            object o;
            using (var r = XmlReader.Create(filename))
                o = s.Deserialize(r);

            var pd = o as PresetDictionary;
            if (pd != null)
            {
                pd.Filename = filename;

                foreach (var x in pd)
                {
                    if (x.Value.Machine == null)
                        x.Value.Machine = dllName;
                }

            }
            return pd;
        }



        public void Save()
        {
            if (Filename == null) return;
            Save(Filename);
        }

        /// <summary>
        /// Writes the file atomically (docs/specs/machines.md §3.6): a temp file beside it, then a rename over it, so a crash or a full
        /// disk leaves the old file whole. Makes the folder when it is missing.
        /// </summary>
        public void Save(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                var ws = new XmlWriterSettings() { NamespaceHandling = System.Xml.NamespaceHandling.OmitDuplicates, NewLineOnAttributes = false, Indent = true };
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = XmlWriter.Create(fs, ws))
                    new XmlSerializer(GetType()).Serialize(w, this);
                File.Move(tmp, path, true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception) { /* the temp file is only litter */ }
                throw;
            }
        }

        public void Merge(PresetDictionary a)
        {
            foreach (var p in a)
            {
                if (!this.ContainsKey(p.Key))
                    this[p.Key] = p.Value;
            }
        }
    }
}
