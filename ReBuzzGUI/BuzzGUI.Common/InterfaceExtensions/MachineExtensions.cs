using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace BuzzGUI.Common.InterfaceExtensions
{
    public static class MachineExtensions
    {
        public static IEnumerable<IParameter> AllParameters(this IMachine machine)
        {
            foreach (var pg in machine.ParameterGroups)
                foreach (var p in pg.Parameters)
                    yield return p;
        }

        public static IEnumerable<IParameter> AllNonInputParameters(this IMachine machine)
        {
            return machine.AllParameters().Where(p => p.Group.Type != ParameterGroupType.Input);
        }

        public static IEnumerable<IParameter> AllNonInputStateParameters(this IMachine machine)
        {
            return machine.AllParameters().Where(p => p.Group.Type != ParameterGroupType.Input && p.Flags.HasFlag(ParameterFlags.State));
        }

        public static IEnumerable<Tuple<IParameter, int>> AllParametersAndTracks(this IMachine machine)
        {
            foreach (var pg in machine.ParameterGroups)
            {
                if (pg.Type == ParameterGroupType.Input)
                {
                    for (int t = 0; t < pg.TrackCount; t++)
                    {
                        yield return Tuple.Create(pg.Parameters[0], t);
                        if (machine.HasStereoInput) yield return Tuple.Create(pg.Parameters[1], t);
                    }

                }
                else
                {
                    for (int t = 0; t < pg.TrackCount; t++)
                    {
                        foreach (var p in pg.Parameters)
                            yield return Tuple.Create(p, t);
                    }
                }

            }
        }

        public static IEnumerable<Tuple<IParameter, int>> AllNonInputStateParametersAndTracks(this IMachine machine)
        {
            return machine.AllParametersAndTracks().Where(p => p.Item1.Group.Type != ParameterGroupType.Input && p.Item1.Flags.HasFlag(ParameterFlags.State));
        }

        public static bool AreParameterNamesUnique(this IMachine machine)
        {
            return AllParameters(machine).Select(p => p.Name).Distinct().Count() == AllParameters(machine).Count();
        }

        public static string GetDirectory(this IMachine machine)
        {
            var path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Gear");

            if (machine.DLL.Info.Type == MachineType.Generator)
                path = Path.Combine(path, "Generators");
            else if (machine.DLL.Info.Type == MachineType.Effect)
                path = Path.Combine(path, "Effects");

            return path;
        }

        /// <summary>
        /// The machine's presets (docs/specs/machines.md §3.6): the factory ones in Gear (read-only) and the user's in the settings
        /// folder's Presets, together. Problems reading either are shown once, when the files change.
        /// </summary>
        public static MachinePresets GetPresets(this IMachine machine)
        {
            var presets = PresetStore.For(machine);
            ShowProblems(presets);
            return presets;
        }

        static readonly HashSet<string> shown = new HashSet<string>();

        static void ShowProblems(MachinePresets presets)
        {
            foreach (var p in presets.Problems)
            {
                lock (shown)
                    if (!shown.Add(p)) continue;
                PortableMessageBox.Show(p);
            }
        }

        public static IEnumerable<string> GetPresetNames(this IMachine machine)
        {
            return GetPresets(machine).Names;
        }

        /// <summary>Whether the preset is the user's own (a user copy of a factory preset counts).</summary>
        public static bool IsUserPreset(this IMachine machine, string name) => GetPresets(machine).IsUser(name);

        /// <summary>The group the preset is listed in: the part of its name before " — ", else "User" for the user's own, else "".</summary>
        public static string GetPresetGroup(this IMachine machine, string name) => GetPresets(machine).GroupOf(name);

        public static Preset GetPreset(this IMachine machine, string name)
        {
            var presets = GetPresets(machine);
            if (name == "<default>" || !presets.Contains(name))
                return new Preset(machine, true, true);
            else
                return presets[name];
        }

        /// <summary>
        /// Saves a user preset (null deletes the user's). Only the user's folder is written. A name that is a factory preset's saves a
        /// user copy that shadows it; deleting that copy brings the factory preset back; a factory preset alone can't be deleted.
        /// </summary>
        public static void SetPreset(this IMachine machine, string name, Preset preset)
        {
            if (name == "<default>") return;

            var presets = GetPresets(machine);
            try
            {
                if (preset != null) presets.Save(name, preset);
                else presets.Delete(name);
            }
            catch (Exception e)
            {
                PortableMessageBox.Show(string.Format("Couldn't save preset file '{0}' ({1})", presets.UserFile, e.Message));
            }
        }

        /// <summary>Deletes the user's preset of that name: false when there is none (a factory preset can't be deleted).</summary>
        public static bool DeleteUserPreset(this IMachine machine, string name)
        {
            try { return GetPresets(machine).Delete(name); }
            catch (Exception e)
            {
                PortableMessageBox.Show(string.Format("Couldn't save preset file '{0}' ({1})", GetPresets(machine).UserFile, e.Message));
                return false;
            }
        }

        /// <summary>Imports a preset file into the user's presets: the presets the machine doesn't have yet.</summary>
        public static void ImportPresets(this IMachine machine, string filename)
        {
            var presets = GetPresets(machine);

            if (File.Exists(filename))
            {
                try
                {
                    var imported = PresetDictionary.Load(machine, filename);
                    if (imported != null) presets.Import(imported);
                }
                catch (Exception e)
                {
                    PortableMessageBox.Show(string.Format("Couldn't import preset file '{0}' ({1})", filename, e.Message));
                }
            }
        }

        public static IParameter GetParameter(this IMachine machine, string name)
        {
            return machine.AllParameters().Where(p => p.Name == name).First();
        }

        public static IAttribute GetAttribute(this IMachine machine, string name)
        {
            return machine.Attributes.Where(p => p.Name == name).First();
        }


        public static string GetNewClipName(this IMachine machine)
        {
            return Enumerable.Range(0, 1000).Select(n => n.ToString("D2")).Where(n => !machine.Clips.Select(p => p.Name).Contains(n)).FirstOrDefault();
        }

        public static R GUIMessageExchange<R>(this IMachine machine, params object[] p) where R : struct
        {
            var request = new MemoryStream();
            var bw = new BinaryWriter(request);

            foreach (var x in p)
            {
                if (x is string)
                    bw.WriteASCIIZString(x as string);
                else
                    bw.WriteRaw(x);
            }

            var response = machine.SendGUIMessage(request.ToArray());

            if (response == null) throw new Exception("no response");

            var br = new BinaryReader(new MemoryStream(response));

            if (typeof(R) == typeof(string))
                return (R)(br.ReadASCIIZString() as object);
            else
                return br.ReadRaw<R>();
        }

        public static void GUIMessage(this IMachine machine, params object[] p)
        {
            var request = new MemoryStream();
            var bw = new BinaryWriter(request);

            foreach (var x in p)
                bw.WriteRaw(x);

            machine.SendGUIMessage(request.ToArray());
        }


    }
}
