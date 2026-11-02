using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace BuzzGUI.Common
{
    public static class RegistryEx
    {
        static readonly string regpath = Global.RegistryRoot;

        /// <summary>
        /// Fork (docs/specs/mac-engine.md M1): the store used instead of the Windows registry. Set it
        /// before Global is first used. Off Windows with none set, reads give the defaults and
        /// writes are dropped.
        /// </summary>
        public static IRegistryStore Store { get; set; }

        /// <summary>Whether values go to <see cref="Store"/> rather than the Windows registry.</summary>
        public static bool Portable => Store != null || !OperatingSystem.IsWindows();

        static T Convert<T>(string text, T def)
        {
            if (text == null) return def;
            try { return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFromString(text); }
            catch (Exception) { return def; }
        }

        public static void Write<T>(string key, T x, string path = "BuzzGUI")
        {
            if (Portable)
            {
                Store?.Set(path, key, x?.ToString());
                return;
            }
            Microsoft.Win32.RegistryKey? regkey = null;
            try
            {
                regkey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(regpath + "\\" + path);
                if (regkey == null) return;
                regkey.SetValue(key, x.ToString());
            }
            catch (Exception) 
            { }
            finally
            {
                if (regkey != null)
                {
                    regkey.Close();
                    regkey.Dispose();
                }
            }
        }

        public static T Read<T>(string key, T def, string path = "BuzzGUI")
        {
            if (Portable) return Store == null ? def : Convert(Store.Get(path, key), def);
            Microsoft.Win32.RegistryKey? regkey = null;
            try
            {
                regkey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regpath + "\\" + path);
                if (regkey == null) return def;
                
                object o = regkey.GetValue(key);
                if (o == null) return def;
                return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFromString(o.ToString());
            }
            catch (Exception)
            {
                return def;
            }
            finally
            {
                if (regkey != null)
                {
                    regkey.Close();
                    regkey.Dispose();
                }
            }
        }

        public static IEnumerable<T> ReadNumberedList<T>(string key, string path = "BuzzGUI")
        {
            if (Portable)
            {
                for (int n = 1; Store != null && Store.Get(path, key + n.ToString()) is string text; n++)
                    yield return Convert<T>(text, default);
                yield break;
            }
            Microsoft.Win32.RegistryKey? regkey = null;
            try
            {
                regkey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regpath + "\\" + path);
                if (regkey == null) yield break;

                int i = 0;
                while (i++ < 0x7fffffff)
                {
                    object o = regkey.GetValue(key + i.ToString());
                    if (o == null) yield break;
                    yield return (T)TypeDescriptor.GetConverter(typeof(T)).ConvertFromString(o.ToString());
                }
            }
            finally
            {
                if (regkey != null)
                {
                    regkey.Close();
                    regkey.Dispose();
                }
            }
        }

        public static SortedSet<string> ReadValues(string path = "BuzzGUI")
        {
            SortedSet<string> ret = new SortedSet<string>();
            if (Portable)
            {
                if (Store != null) foreach (var name in Store.Names(path)) ret.Add(name);
                return ret;
            }
            Microsoft.Win32.RegistryKey? regkey = null;
            try
            {
                regkey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regpath + "\\" + path);
                if (regkey == null)
                    return ret;

                var keys = regkey.GetValueNames();
                foreach(var k in keys)
                {
                    ret.Add(k);
                }
            }
            finally
            {
                if (regkey != null)
                {
                    regkey.Close();
                    regkey.Dispose();
                }
            }

            return ret;
        }

        public static Dictionary<string, object> ReadDictionary(string path = "BuzzGUI")
        {
            Dictionary<string, object> ret = new Dictionary<string, object>();
            if (Portable)
            {
                if (Store != null) foreach (var kv in Store.Values(path)) ret[kv.Key] = kv.Value;
                return ret;
            }
            Microsoft.Win32.RegistryKey? regkey = null;
            try
            {
                regkey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regpath + "\\" + path);
                if (regkey == null)
                    return ret;

                var keys = regkey.GetValueNames();
                foreach (var k in keys)
                {
                    ret.Add(k, regkey.GetValue(k));
                }
            }
            finally
            {
                if (regkey != null)
                {
                    regkey.Close();
                    regkey.Dispose();
                }
            }

            return ret;
        }

        public static RegistryMonitor CreateMonitor(string path = "BuzzGUI")
        {
            return new RegistryMonitor(Microsoft.Win32.RegistryHive.CurrentUser, regpath + path);
        }

    }
}
