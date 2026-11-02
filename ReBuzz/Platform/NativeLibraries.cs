#nullable disable
// Fork (docs/specs/mac-engine.md M4/M5): where the engine finds its native libraries off Windows.
// .NET's default probing finds neither Homebrew's /opt/homebrew/lib on macOS nor a versioned
// libportaudio.so.2 on Linux, so the engine's assembly gets one resolver (an assembly can have
// only one) that tries, in order: the app's folder, its runtimes/<rid>/native folder, then the
// usual install places. Libraries we ship (P5) are found first.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ReBuzz.Platform
{
    public static class NativeLibraries
    {
        static readonly object gate = new object();
        static readonly HashSet<Assembly> registered = new HashSet<Assembly>();

        /// <summary>The file names a library goes by on this OS, most specific first.</summary>
        public static IEnumerable<string> FileNames(string name)
        {
            if (OperatingSystem.IsMacOS())
            {
                yield return "lib" + name + ".dylib";
                yield return "lib" + name + ".2.dylib";
            }
            else if (OperatingSystem.IsLinux())
            {
                yield return "lib" + name + ".so";
                yield return "lib" + name + ".so.2";
                yield return "lib" + name + ".so.1";
            }
            else
            {
                yield return name + ".dll";
                yield return "lib" + name + ".dll";
            }
        }

        /// <summary>The folders searched, in order.</summary>
        public static IEnumerable<string> Folders()
        {
            string app = AppContext.BaseDirectory;
            yield return app;
            yield return Path.Combine(app, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
            if (OperatingSystem.IsMacOS())
            {
                yield return "/opt/homebrew/lib";
                yield return "/usr/local/lib";
            }
            else if (OperatingSystem.IsLinux())
            {
                string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64-linux-gnu" : "x86_64-linux-gnu";
                yield return "/usr/lib/" + arch;
                yield return "/usr/local/lib";
                yield return "/usr/lib";
            }
        }

        /// <summary>The first existing path for a library, or null.</summary>
        public static string Find(string name)
        {
            foreach (var folder in Folders())
                foreach (var file in FileNames(name))
                {
                    string path = Path.Combine(folder, file);
                    if (File.Exists(path)) return path;
                }
            return null;
        }

        /// <summary>Installs the resolver for the engine's assembly (PortAudio).</summary>
        public static void Register() => Register(typeof(NativeLibraries).Assembly);

        /// <summary>
        /// Installs the resolver for an assembly that imports native libraries by their Windows
        /// names (BuzzGUI.Common's libsndfile binding imports "libsndfile-1"); once per assembly,
        /// later calls do nothing. This file stays free of other references: the seam tests link it.
        /// </summary>
        public static void Register(Assembly assembly)
        {
            lock (gate)
            {
                if (!registered.Add(assembly)) return;
                try
                {
                    NativeLibrary.SetDllImportResolver(assembly, Resolve);
                }
                catch (InvalidOperationException)
                {
                    // The assembly has a resolver already (a host set one): it decides.
                }
            }
        }

        static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            // Windows libraries ("kernel32.dll", "libsndfile-1") go the default way there.
            if (OperatingSystem.IsWindows()) return IntPtr.Zero;
            string bare = Path.GetFileNameWithoutExtension(name);
            if (bare.StartsWith("lib", StringComparison.Ordinal)) bare = bare.Substring(3);
            int dash = bare.IndexOf('-');
            if (dash > 0) bare = bare.Substring(0, dash);            // "libsndfile-1" → "sndfile"
            string path = Find(bare);
            return path != null && NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
        }
    }
}
