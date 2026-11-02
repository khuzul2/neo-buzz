using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.ManagedMachine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cryptography = System.Security.Cryptography;
using System.Xml;
using System.Xml.Serialization;

namespace ReBuzz.FileOps
{
    internal interface IMachineDLLScanner
    {
        Dictionary<string, MachineDLL> GetMachineDLLs(ReBuzzCore buzz, string buzzPath);
        void AddMachineDllsToDictionary(ReBuzzCore buzz, XMLMachineDLL[] xMLMachineDLLs, Dictionary<string, MachineDLL> md);
        XMLMachineDLL ValidateDll(ReBuzzCore buzz, string libName, string path, string buzzPath);
    }

    internal class MachineDLLScanner(IUiDispatcher dispatcher, EngineSettings engineSettings) : IMachineDLLScanner
    {
        static readonly string dllDataFileName = "MachineDLLCache.xml";

        public Dictionary<string, MachineDLL> GetMachineDLLs(ReBuzzCore buzz, string buzzPath)
        {
            XMLMachineDLLs xmlMachines = null;
            FileStream f = null;
            try
            {
                string xmlFilePath = GetFullFileName();
                if (File.Exists(xmlFilePath))
                {
                    f = File.OpenRead(xmlFilePath);
                    xmlMachines = LoadMachineDLLs(f, buzz);
                    // Fork (mac-engine.md M1): closed before a rescan rewrites the file (File.Create
                    // failed while it was open: "used by another process").
                    f.Dispose();
                    f = null;

                    if (xmlMachines != null)
                    {
                        // Check dll counts
                        string filepath = Path.Combine(buzzPath, "Gear", "Effects");
                        var effectDllFiles = GetDllList(filepath);
                        filepath = Path.Combine(buzzPath, "Gear", "Generators");
                        var generatorDllFiles = GetDllList(filepath);
                        if (Global.GeneralSettings.PluginRescanStrategy == PluginRescanStrategyType.Always ||
                            (Global.GeneralSettings.PluginRescanStrategy == PluginRescanStrategyType.WhenChanged && (xmlMachines.NumDLLsInEffectsFolder != effectDllFiles.Count || xmlMachines.NumDLLsGeneratorsFolder != generatorDllFiles.Count)))
                        {
                            xmlMachines = RescanMachineDLLs(buzz, buzzPath, dispatcher, engineSettings);
                        }
                    }
                    else
                    {
                        //No XML machines available - force scan
                        xmlMachines = RescanMachineDLLs(buzz, buzzPath, dispatcher, engineSettings);
                    }
                }
            }
            catch (Exception e)
            {
                buzz.UserMessages.Error("Error loading Machine DLL Cache Data:\n\n" + e.ToString(), "Machine DLL Cache Load Error", e);
                if (f != null)
                    f.Close();
            }

            try
            {
                if (xmlMachines == null)
                {
                    xmlMachines = RescanMachineDLLs(buzz, buzzPath, dispatcher, engineSettings);
                }
            }
            catch (Exception e)
            {
                buzz.UserMessages.Error("RescanMachineDLLs error:\n\n" + e.ToString(), "Machine DLL Cache Load Error", e);
            }


            Dictionary<string, MachineDLL> md = new Dictionary<string, MachineDLL>();
            try
            {
                AddMachineDllsToDictionary(buzz, xmlMachines.EffectMachineDLLs, md);
                AddMachineDllsToDictionary(buzz, xmlMachines.GeneratorMachineDLLs, md);
            }
            catch (Exception)
            {
                //MessageBox.Show("AddMachineDllsToDictionary error:\n\n" + e.ToString(), "Machine DLL Cache Load Error", MessageBoxButton.OK);
                xmlMachines = RescanMachineDLLs(buzz, buzzPath, dispatcher, engineSettings);
                md.Clear();
                AddMachineDllsToDictionary(buzz, xmlMachines.EffectMachineDLLs, md);
                AddMachineDllsToDictionary(buzz, xmlMachines.GeneratorMachineDLLs, md);
            }
            return md;
        }

        public void AddMachineDllsToDictionary(ReBuzzCore buzz, XMLMachineDLL[] xMLMachineDLLs, Dictionary<string, MachineDLL> md)
        {
            var sha256 = Cryptography.SHA1.Create();

            foreach (var xmac in xMLMachineDLLs)
            {
                if (!buzz.Gear.IsBlacklisted(xmac))
                {
                    MachineDLL mDll = new MachineDLL();

                    mDll.Name = xmac.Name;
                    mDll.Path = xmac.Path;
                    mDll.IsManaged = xmac.IsManaged;
                    mDll.Is64Bit = xmac.Is64Bit;
                    mDll.IsOutOfProcess = (IntPtr.Size == 4 && xmac.Is64Bit) || (IntPtr.Size == 8 && !xmac.Is64Bit);

                    using (var fs = File.OpenRead(mDll.Path))
                    {
                        mDll.SHA1Hash = string.Join("", sha256.ComputeHash(fs).Select(b => b.ToString("X2")));
                    }

                    var mi = new MachineInfo();
                    var xmi = xmac.MachineInfo;

                    mi.Type = xmi.Type;
                    mi.Version = xmi.Version;
                    mi.InternalVersion = xmi.InternalVersion;
                    mi.Flags = xmi.Flags;
                    mi.MinTracks = xmi.MinTracks;
                    mi.MaxTracks = xmi.MaxTracks;
                    mi.Name = xmi.Name;
                    mi.ShortName = xmi.ShortName;
                    mi.Author = xmi.Author;
                    mDll.MachineInfo = mi;

                    // Ignore native editors
                    if (!(mi.Flags.HasFlag(MachineInfoFlags.PATTERN_EDITOR) && !mDll.IsManaged))
                    {
                        md[mDll.Name] = mDll;
                    }
                }
            }

            sha256.Dispose();
        }

        public static XMLMachineDLLs LoadMachineDLLs(Stream input, IBuzz buzz)
        {
            if (input == null)
            {
                return null;
            }
            var s = new XmlSerializer(typeof(XMLMachineDLLs));

            var r = XmlReader.Create(input);
            object o = null;
            try
            {
                o = s.Deserialize(r);
            }
            catch (Exception e)
            {
                //MessageBox.Show("Error loading Machine DLL Cache Data:\n\n" + e.ToString(), "Machine DLL Cache Load Error", MessageBoxButton.OK);
                buzz.DCWriteLine("Error loading Machine DLL Cache Data. Rebuilding cache...");
            }
            r.Close();
            input.Close();
            var t = o as XMLMachineDLLs;
            return t;
        }

        /// <summary>
        /// Fork (mac-engine.md M5): where the cache lives when not in the per-user application
        /// data folder (rebuzz-headless keeps it in its settings folder).
        /// </summary>
        internal static string CacheFolder { get; set; }

        /// <summary>
        /// The per-user application data folder, created if missing: off Windows it can be missing
        /// (~/.config on a fresh Mac), and GetFolderPath then gives "", which would put the cache in
        /// the current folder.
        /// </summary>
        static string Folder => CacheFolder ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), ReBuzzCore.AppDataPath);

        public static string GetFullFileName()
        {
            var dir = Folder;

            // Check if the directory exists
            if (!Directory.Exists(dir))
            {
                // Create the directory
                Directory.CreateDirectory(dir);
                Console.WriteLine("Directory created successfully.");
            }

            return Path.Combine(dir, dllDataFileName);
        }

        public static string GetFilePath()
        {
            return Folder;
        }

        public static XMLMachineDLLs RescanMachineDLLs(
            ReBuzzCore buzz,
            string buzzPath,
            IUiDispatcher dispatcher,
            EngineSettings engineSettings)
        {
            buzz.DCWriteLine("RescanMachineDLLs");
            var xFileName = GetFullFileName();

            // Fork (mac-engine.md M2): native machines run in Windows host processes; elsewhere
            // only managed machines are scanned (native ones load as placeholders from songs).
            NativeMachine.NativeMachineHost nativeMachineHost = null, nativeMachineHost64 = null;
            if (NativeMachine.NativeMachineHost.Supported)
            {
                nativeMachineHost = new NativeMachine.NativeMachineHost("ScanDLLs", buzzPath, dispatcher, engineSettings);
                nativeMachineHost.InitHost(buzz, false);

                nativeMachineHost64 = new NativeMachine.NativeMachineHost("ScanDLLs64", buzzPath, dispatcher, engineSettings);
                nativeMachineHost64.InitHost(buzz, true);
            }

            string filepath = Path.Combine(buzzPath, "Gear", "Effects");
            var effectDllFiles = GetDllList(filepath);
            filepath = Path.Combine(buzzPath, "Gear", "Generators");
            var generatorDllFiles = GetDllList(filepath);

            XMLMachineDLLs machineDLLs = new XMLMachineDLLs();
            machineDLLs.NumDLLsInEffectsFolder = effectDllFiles.Count;
            machineDLLs.NumDLLsGeneratorsFolder = generatorDllFiles.Count;

            List<XMLMachineDLL> effectsList = ValidateDlls(buzz, nativeMachineHost, nativeMachineHost64, effectDllFiles, buzzPath, dispatcher, engineSettings);
            List<XMLMachineDLL> generatorsList = ValidateDlls(buzz, nativeMachineHost, nativeMachineHost64, generatorDllFiles, buzzPath, dispatcher, engineSettings);

            machineDLLs.EffectMachineDLLs = effectsList.ToArray();
            machineDLLs.GeneratorMachineDLLs = generatorsList.ToArray();

            // Fork: created only now, so a scan that fails doesn't leave the file open (a rescan
            // then failed with "used by another process").
            Save(File.Create(xFileName), machineDLLs, buzz.UserMessages);

            nativeMachineHost?.Dispose();
            nativeMachineHost64?.Dispose();

            return machineDLLs;
        }

        public static void Save(Stream output, object obj, IUserMessages userMessages)
        {
            var ws = new XmlWriterSettings() { NamespaceHandling = System.Xml.NamespaceHandling.OmitDuplicates, NewLineOnAttributes = false, Indent = true };
            var w = XmlWriter.Create(output, ws);
            try
            {
                var s = new XmlSerializer(obj.GetType());
                s.Serialize(w, obj);
            }
            catch (Exception e)
            {
                userMessages.Error(e.ToString(), "Save Error", e);
            }

            w.Close();
            output.Close();
        }

        private static List<XMLMachineDLL> ValidateDlls(
            ReBuzzCore buzz,
            NativeMachine.NativeMachineHost nativeMachineHost,
            NativeMachine.NativeMachineHost nativeMachineHost64,
            List<FileInfo> machineDllFiles,
            string buzzPath,
            IUiDispatcher dispatcher, 
            EngineSettings engineSettings)
        {
            List<XMLMachineDLL> machineDLLs = new List<XMLMachineDLL>();

            var uiMessage = nativeMachineHost?.UIMessage;
            var uiMessage64 = nativeMachineHost64?.UIMessage;

            foreach (FileInfo file in machineDllFiles)
            {
                XMLMachineDLL mDll = null;
                try
                {
                    mDll = ValidateDll(
                        buzz,
                        file.Name,
                        file.FullName,
                        nativeMachineHost,
                        nativeMachineHost64,
                        buzzPath,
                        dispatcher,
                        engineSettings);
                    if (mDll != null)
                    {
                        machineDLLs.Add(mDll);
                    }
                }
                catch (Exception e)
                {
                    Global.Buzz.DCWriteLine(e.ToString());
                }
            }
            return machineDLLs;
        }

        public static XMLMachineDLL ValidateDll(ReBuzzCore buzz,
            string libName,
            string path,
            NativeMachine.NativeMachineHost nativeMachineHost,
            NativeMachine.NativeMachineHost nativeMachineHost64,
            string buzzPath,
            IUiDispatcher dispatcher,
            EngineSettings engineSettings)
        {
            var uiMessage = nativeMachineHost?.UIMessage;
            var uiMessage64 = nativeMachineHost64?.UIMessage;

            if (libName.EndsWith("GUI.dll"))
                return null;

            if (buzz.Gear.IsBlacklisted(libName))
                return null;

            // Managed machines
            if (libName.EndsWith("NET.dll"))
            {
                libName = libName.Remove(libName.Length - 8);

                ManagedMachineDLL managedMachineDLL = new ManagedMachineDLL(dispatcher);
                managedMachineDLL.LoadManagedMachine(path);

                XMLMachineDLL mDll = new XMLMachineDLL();
                mDll.Name = libName;
                mDll.Path = path;
                mDll.Is64Bit = true;

                var module = managedMachineDLL.Assembly.Modules.FirstOrDefault();
                if (module != null)
                {
                    managedMachineDLL.Assembly.Modules.First().GetPEKind(out var peKind, out var peType);
                    mDll.Is64Bit = !(peKind == PortableExecutableKinds.Required32Bit);
                }
                mDll.IsManaged = true;

                XMLMachineInfo machineInfo = new XMLMachineInfo();
                var mi = managedMachineDLL.MachineInfo;

                machineInfo.Type = mi.Type;
                machineInfo.Version = mi.Version;
                machineInfo.InternalVersion = mi.InternalVersion;
                machineInfo.Flags = mi.Flags;
                machineInfo.MinTracks = mi.MinTracks;
                machineInfo.MaxTracks = mi.MaxTracks;
                machineInfo.Name = mi.Name;
                machineInfo.ShortName = mi.ShortName;
                machineInfo.Author = mi.Author;
                mDll.MachineInfo = machineInfo;

                return mDll;
            }
            // 64 bit native machines
            else if (nativeMachineHost == null || nativeMachineHost64 == null)
            {
                return null;        // fork (mac-engine.md M2): a native machine, and no native host here
            }
            else if (libName.EndsWith("x64.dll"))
            {
                libName = libName.Remove(libName.Length - 8);
                var machine = new MachineCore(buzz.SongCore, buzzPath, dispatcher, engineSettings);
                machine.MachineDLL.Is64Bit = true;
                if (uiMessage64.UILoadLibrarySync(buzz, machine, libName, path))
                {
                    XMLMachineDLL mDll = new XMLMachineDLL();
                    mDll.Name = libName;
                    mDll.Path = path;
                    mDll.Is64Bit = true;

                    XMLMachineInfo machineInfo = new XMLMachineInfo();
                    var mi = machine.DLL.Info;

                    machineInfo.Type = mi.Type;
                    machineInfo.Version = mi.Version;
                    machineInfo.InternalVersion = mi.InternalVersion;
                    machineInfo.Flags = mi.Flags;
                    machineInfo.MinTracks = mi.MinTracks;
                    machineInfo.MaxTracks = mi.MaxTracks;
                    machineInfo.Name = mi.Name;
                    machineInfo.ShortName = mi.ShortName;
                    machineInfo.Author = mi.Author;
                    mDll.MachineInfo = machineInfo;

                    return mDll;
                }
            }
            // 32 bit
            else
            {
                libName = libName.Remove(libName.Length - 4);
                var machine = new MachineCore(buzz.SongCore, buzzPath, dispatcher, engineSettings);

                if (uiMessage.UILoadLibrarySync(buzz, machine, libName, path))
                {
                    XMLMachineDLL mDll = new XMLMachineDLL();
                    mDll.Name = libName;
                    mDll.Path = path;
                    mDll.IsOutOfProcess = true;

                    XMLMachineInfo machineInfo = new XMLMachineInfo();
                    var mi = machine.DLL.Info;

                    machineInfo.Type = mi.Type;
                    machineInfo.Version = mi.Version;
                    machineInfo.InternalVersion = mi.InternalVersion;
                    machineInfo.Flags = mi.Flags;
                    machineInfo.MinTracks = mi.MinTracks;
                    machineInfo.MaxTracks = mi.MaxTracks;
                    machineInfo.Name = mi.Name;
                    machineInfo.ShortName = mi.ShortName;
                    machineInfo.Author = mi.Author;
                    mDll.MachineInfo = machineInfo;

                    return mDll;
                }
            }
            return null;
        }

        public XMLMachineDLL ValidateDll(ReBuzzCore buzz, string libName, string path, string buzzPath)
        {
            NativeMachine.NativeMachineHost nativeMachineHost = null, nativeMachineHost64 = null;
            if (NativeMachine.NativeMachineHost.Supported)
            {
                nativeMachineHost = new NativeMachine.NativeMachineHost("ScanDLLs", buzzPath, dispatcher, engineSettings);
                nativeMachineHost.InitHost(buzz, false);

                nativeMachineHost64 = new NativeMachine.NativeMachineHost("ScanDLLs64", buzzPath, dispatcher, engineSettings);
                nativeMachineHost64.InitHost(buzz, true);
            }

            var ret = ValidateDll(buzz, libName, path, nativeMachineHost, nativeMachineHost64, buzzPath, dispatcher, engineSettings);
            nativeMachineHost?.Dispose();
            nativeMachineHost64?.Dispose();

            return ret;
        }

        public static List<FileInfo> GetDllList(string filepath)
        {
            List<string> list = new List<string>();
            DirectoryInfo d = new DirectoryInfo(filepath);
            if (!d.Exists) return new List<FileInfo>();      // fork (mac-engine.md M2): a host without that gear folder

            return d.GetFiles().Where(f => f.Name.EndsWith(".dll")).ToList();
        }
    }

    // XML import/export structures
    [XmlType(TypeName = "MachineDLLs")]
    public class XMLMachineDLLs
    {
        public int NumDLLsInEffectsFolder { get; set; }
        public int NumDLLsGeneratorsFolder { get; set; }

        public XMLMachineDLL[] EffectMachineDLLs { get; set; }
        public XMLMachineDLL[] GeneratorMachineDLLs { get; set; }

    }

    [XmlType(TypeName = "MachineDLL")]
    public class XMLMachineDLL
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public XMLMachineInfo MachineInfo { get; set; }
        public bool IsOutOfProcess { get; set; }
        public bool IsManaged { get; set; }
        public bool Is64Bit { get; set; }
    }

    [XmlType(TypeName = "MachineInfo")]
    public class XMLMachineInfo
    {
        [XmlAttribute]
        public MachineType Type { get; set; }
        [XmlAttribute]
        public int Version { get; set; }
        [XmlAttribute]
        public int InternalVersion { get; set; }
        [XmlAttribute]
        public MachineInfoFlags Flags { get; set; }
        [XmlAttribute]
        public int MinTracks { get; set; }
        [XmlAttribute]
        public int MaxTracks { get; set; }
        [XmlAttribute]
        public string Name { get; set; }
        [XmlAttribute]
        public string ShortName { get; set; }
        [XmlAttribute]
        public string Author { get; set; }
    }

}
