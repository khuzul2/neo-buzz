// Fork (docs/specs/studio-mvp.md S2): which parameters the parameter window hides, per machine
// type, ported from the WPF window's ParameterWindowProperties: the same XML file
// (ParameterWindowProperties.xml), in the studio's settings folder instead of %AppData%\ReBuzz.
// Edit mode shows every parameter with a box; leaving it saves the unticked ones as hidden.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;
using BuzzGUI.Interfaces;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Parameters
{
    [XmlRoot("ParameterWindowProperties")]
    public sealed class ParameterVisibility
    {
        public List<Machine> Machines;

        static ParameterVisibility properties;

        /// <summary>The file (tests may point it elsewhere); default: the settings folder's.</summary>
        internal static string FilePath { get; set; }

        static string PathNow => FilePath ?? Path.Combine(
            StudioHost.Current?.SettingsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReBuzz"),
            "ParameterWindowProperties.xml");

        public static bool IsVisible(IParameter param)
        {
            Load();
            var mac = param.Group.Machine;
            var machine = properties.Machines.FirstOrDefault(m => m.Dll == mac.DLL.Name);
            var group = machine?.ParameterGroups.FirstOrDefault(g => g.Type == param.Group.Type);
            var parameter = group?.Parameters.FirstOrDefault(p => p.Name == param.Name);
            return parameter?.IsVisible ?? true;
        }

        public static void SetVisible(IParameter param, bool visible)
        {
            if (param.Group.Type == ParameterGroupType.Input) return;
            Load();
            var mac = param.Group.Machine;
            var machine = properties.Machines.FirstOrDefault(m => m.Dll == mac.DLL.Name);
            if (machine == null)
                properties.Machines.Add(machine = new Machine { Dll = mac.DLL.Name, ParameterGroups = new List<ParameterGroup>() });
            var group = machine.ParameterGroups.FirstOrDefault(g => g.Type == param.Group.Type);
            if (group == null)
                machine.ParameterGroups.Add(group = new ParameterGroup { Type = param.Group.Type, Parameters = new List<Parameter>() });
            var parameter = group.Parameters.FirstOrDefault(p => p.Name == param.Name);
            if (parameter == null && !visible) group.Parameters.Add(new Parameter { Name = param.Name, IsVisible = false });
            else if (parameter != null && visible) group.Parameters.Remove(parameter);
            else return;
            Save();
        }

        /// <summary>Forgets what was read (tests).</summary>
        internal static void Reset() => properties = null;

        static void Load()
        {
            if (properties != null) return;
            try
            {
                using var fs = File.OpenRead(PathNow);
                properties = (ParameterVisibility)new XmlSerializer(typeof(ParameterVisibility)).Deserialize(XmlReader.Create(fs));
            }
            catch (Exception)
            {
                properties = null;
            }
            properties ??= new ParameterVisibility();
            properties.Machines ??= new List<Machine>();
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathNow));
                using var fs = File.Create(PathNow);
                using var w = XmlWriter.Create(fs, new XmlWriterSettings { NamespaceHandling = NamespaceHandling.OmitDuplicates, Indent = true });
                new XmlSerializer(typeof(ParameterVisibility)).Serialize(w, properties);
            }
            catch (Exception e)
            {
                StudioHost.Current?.Log("Couldn't save the hidden parameters: " + e.Message);
            }
        }

        public sealed class Machine
        {
            [XmlAttribute]
            public string Dll;
            public List<ParameterGroup> ParameterGroups;
        }

        public sealed class ParameterGroup
        {
            [XmlAttribute]
            public ParameterGroupType Type;
            public List<Parameter> Parameters;
        }

        public sealed class Parameter
        {
            [XmlAttribute]
            public string Name;
            [XmlAttribute]
            public bool IsVisible;
        }
    }
}
