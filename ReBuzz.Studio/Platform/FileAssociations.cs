// Fork (docs/specs/ux-overhaul.md P6c, §9 row 16): the portable package has no installer, so the studio offers to be the default
// program for songs itself (Preferences ▸ General ▸ File types, Windows only). Per user, under HKCU\Software\Classes: no
// administrator rights, nothing outside the user's own hive, and Remove puts back what Make found.
//
//   .nbl  ->  ProgID NeoBuzzLive.Song  ->  shell\open\command = "<exe>" "%1"
//
// The extension's earlier default (another Buzz, say) is kept in the extension key's value "NeoBuzzLive.Previous" and restored by Remove.
// The registry sits behind IAssocStore so the logic is tested on any platform with a dictionary.

using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReBuzz.Studio.Platform
{
    /// <summary>The slice of the registry the associations use: HKCU\Software\Classes, keys by relative path, string values ("" = the default value).</summary>
    internal interface IAssocStore
    {
        string Get(string key, string name);
        void Set(string key, string name, string value);
        void DeleteValue(string key, string name);
        void DeleteTree(string key);
    }

    internal sealed class FileAssociations
    {
        public const string ProgId = "NeoBuzzLive.Song";
        public const string PreviousValue = "NeoBuzzLive.Previous";
        public static readonly string[] Extensions = { ".nbl" };
        /// <summary>Extensions an earlier build associated: Restore still frees them if they point at this program, so no stale entry is left.</summary>
        static readonly string[] Retired = { ".bmx", ".bmw", ".bmxml" };

        readonly IAssocStore store;
        readonly string exePath;
        readonly Action changed;

        public FileAssociations(IAssocStore store, string exePath, Action changed = null)
        {
            this.store = store;
            this.exePath = exePath;
            this.changed = changed;
        }

        /// <summary>The real thing: Windows only (null elsewhere, and for a studio run from a place with no exe, as under dotnet).</summary>
        public static FileAssociations ForThisApp()
        {
            if (!OperatingSystem.IsWindows()) return null;
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(System.IO.Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase)) return null;
            return new FileAssociations(new RegistryStore(), exe, RegistryStore.NotifyShell);
        }

        string Command => "\"" + exePath + "\" \"%1\"";

        /// <summary>True when every extension opens with this exe.</summary>
        public bool IsDefault
        {
            get
            {
                foreach (var ext in Extensions)
                    if (store.Get(ext, "") != ProgId) return false;
                return string.Equals(store.Get(ProgId + @"\shell\open\command", ""), Command, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>The program (ProgID) a song file opens with now, for the page to name when it is not this one; null = none.</summary>
        public string CurrentFor(string ext) => store.Get(ext, "") is { Length: > 0 } p ? p : null;

        public void Make()
        {
            store.Set(ProgId, "", "NeoBuzz Live song");
            store.Set(ProgId + @"\DefaultIcon", "", "\"" + exePath + "\",0");
            store.Set(ProgId + @"\shell\open\command", "", Command);
            foreach (var ext in Extensions)
            {
                string old = store.Get(ext, "");
                if (old != ProgId)
                {
                    if (!string.IsNullOrEmpty(old)) store.Set(ext, PreviousValue, old);
                    else store.DeleteValue(ext, PreviousValue);
                }
                store.Set(ext, "", ProgId);
            }
            changed?.Invoke();
        }

        public void Remove()
        {
            foreach (var ext in Extensions.Concat(Retired))
            {
                if (store.Get(ext, "") == ProgId)
                {
                    string prev = store.Get(ext, PreviousValue);
                    if (!string.IsNullOrEmpty(prev)) store.Set(ext, "", prev);
                    else store.DeleteValue(ext, "");
                }
                store.DeleteValue(ext, PreviousValue);
            }
            store.DeleteTree(ProgId);
            changed?.Invoke();
        }
    }

    [SupportedOSPlatform("windows")]
    internal sealed class RegistryStore : IAssocStore
    {
        const string Root = @"Software\Classes\";

        public string Get(string key, string name)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Root + key);
            return k?.GetValue(name) as string;
        }

        public void Set(string key, string name, string value)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Root + key);
            k.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
        }

        public void DeleteValue(string key, string name)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Root + key, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }

        public void DeleteTree(string key) => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Root + key, throwOnMissingSubKey: false);

        /// <summary>SHChangeNotify(SHCNE_ASSOCCHANGED): Explorer re-reads the associations.</summary>
        public static void NotifyShell()
        {
            try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch (Exception) { }
        }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }

    /// <summary>A store in memory, for tests.</summary>
    internal sealed class MemoryAssocStore : IAssocStore
    {
        public readonly Dictionary<(string Key, string Name), string> Values = new();

        public string Get(string key, string name) => Values.TryGetValue((key.ToLowerInvariant(), name), out var v) ? v : null;
        public void Set(string key, string name, string value) => Values[(key.ToLowerInvariant(), name)] = value;
        public void DeleteValue(string key, string name) => Values.Remove((key.ToLowerInvariant(), name));
        public void DeleteTree(string key)
        {
            string k = key.ToLowerInvariant();
            foreach (var c in new List<(string, string)>(Values.Keys))
                if (c.Item1 == k || c.Item1.StartsWith(k + "\\")) Values.Remove(c);
        }
    }
}
