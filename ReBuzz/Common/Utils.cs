using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using Microsoft.Win32;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ReBuzz.Common
{
    public static class Utils
    {
        public static IntPtr GetObjectHandle(object obj)
        {
            GCHandle handle = GCHandle.Alloc(obj, GCHandleType.Pinned);
            IntPtr ptr = GCHandle.ToIntPtr(handle);
            handle.Free();
            return ptr;
        }

        public static object GetObjectFromHandle(IntPtr ptr)
        {
            return GCHandle.FromIntPtr(ptr).Target;
        }

        internal static List<string> GetThemes(string buzzPath)
        {
            List<string> themes = new List<string>();
            themes.Add("<default>");

            // Fork (mac-engine.md M1): portable path; a host without themes (headless) has none.
            string folder = Path.Combine(buzzPath, "Themes");
            if (!Directory.Exists(folder)) return themes;
            foreach (var file in Directory.GetFiles(folder, "*.col"))
            {
                themes.Add(Path.GetFileNameWithoutExtension(file));
            }

            return themes;
        }

        internal static void SaveProfile(string buzzPath, XElement profileElement)
        {
            try
            {
                RegistryEx.Write<string>(profileElement.Name.LocalName, profileElement.ToString(), "Profiles");
            }
            catch { }
        }

        internal static Dictionary<string, XElement> GetProfiles(string buzzPath)
        {
            Dictionary<string, XElement> ret = new Dictionary<string, XElement>();

            var keys = RegistryEx.ReadValues("Profiles");
            if (keys != null)
            {
                foreach (var k in keys)
                {
                    try
                    {
                        var elementXml = RegistryEx.Read<string>(k, "", "Profiles");
                        if (!string.IsNullOrEmpty(elementXml))
                        {
                            var doc = XDocument.Parse(elementXml);
                            ret.Add(doc.Root.Name.LocalName, doc.Root);
                        }
                    }
                    catch { }
                }
            }

            return ret;
        }

        internal static void SetProcessorAffinityMask(IRegistryEx registryEx, IUserMessages userMessages, bool ideal)
        {
            var process = Process.GetCurrentProcess();
            long processorAffinityMask = registryEx.Read("ProcessorAffinity", 0xFFFFFFFF, "Settings");
            try
            {
                if (processorAffinityMask < process.ProcessorAffinity)
                    process.ProcessorAffinity = (IntPtr)processorAffinityMask;
            }
            catch (Exception ex)
            {
                userMessages.Error(ex.Message, null, ex);
            }

            /*
            if (ideal)
            {
                processorAffinityMask = (long)process.ProcessorAffinity;
                int processorCount = Environment.ProcessorCount;// >= 32 ? 31 : Environment.ProcessorCount;
                var threads = process.Threads;
                for (int i = 0; i < threads.Count; i++)
                {
                    var pt = threads[i];
                    int targetCpu = i;
                    // Find next cpu
                    for (int j = 0; j < processorCount; j++)
                    {
                        int target = (j + targetCpu) % processorCount;
                        bool cpuEnabled = (processorAffinityMask & (1L << target)) != 0;
                        if (cpuEnabled)
                        {
                            try
                            {
                                threads[i].IdealProcessor = target;
                            }
                            catch { }
                            break;
                        }
                    }
                }
            }
            */
        }

        public static unsafe byte[] SerializeValueType<T>(in T value) where T : unmanaged
        {
            byte[] result = new byte[sizeof(T)];
            fixed (byte* dst = result)
                *(T*)dst = value;
            return result;
        }

        public static unsafe byte[] SerializeValueTypeChangePointer<T>(in T value, ref byte[] result) where T : unmanaged
        {
            fixed (byte* dst = result)
                *(T*)dst = value;
            return result;
        }

        //static float k_DENORMAL_DC = 1e-18f; // Flush to Zero
        static float k_DENORMAL_DC = 1e-15f; // DC
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FlushDenormalToZero(Sample[] samples)
        {
            /*
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i].L += k_DENORMAL_DC;
                //samples[i].L -= k_DENORMAL_DC;

                samples[i].R += k_DENORMAL_DC;
                //samples[i].R -= k_DENORMAL_DC;
            }
            */
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float FlushDenormalToZero(float f)
        {
            f += k_DENORMAL_DC;
            //f -= k_DENORMAL_DC;
            return f;
        }

        internal static void FlipDenormalDC()
        {
            k_DENORMAL_DC = -k_DENORMAL_DC;
        }

        public static Dictionary<string, int> KeyValuesToStringInt(this Dictionary<string, object> d)
        {
            Dictionary<string, int> ret = new Dictionary<string, int>();
            foreach (var key in d.Keys)
            {
                if (d[key] is int)
                {
                    ret[key] = (int)d[key];
                }
            }
            return ret;
        }

        public static string RemoveInvalidXmlChars(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            StringBuilder sb = new StringBuilder(text.Length);

            foreach (char ch in text)
            {
                // Keep only valid XML characters
                if (XmlConvert.IsXmlChar(ch))
                {
                    sb.Append(ch);
                }
                // else skip invalid char
            }

            return sb.ToString();
        }
    }
}
