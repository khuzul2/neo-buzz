// Fork (audio hygiene, docs/specs/daw-modes.md §8.1): the audio path compiled before it first runs.
// .NET compiles a method the first time it is called, on the calling thread. The first buffers the
// device asked for compiled about 110 methods on the audio thread (about 45 ms), the transport's
// first play about 25 more (5-8 ms, more than a 256-frame buffer leaves at 44.1 kHz), its first stop
// about 30, and a machine type's first tick its parameter setters (95 for Neo Poly): each a likely
// dropout on a low-latency device.
//
// Running the audio path ahead of time can't be done without changing the song and render state
// (the transport, positions, the session clock, machines' own state). Instead the methods are
// compiled without running them (RuntimeHelpers.PrepareMethod), on a thread of their own: every
// method of the engine's types in the namespaces the audio and render threads run, of the
// machine interface's assemblies (BuzzGUI.Interfaces, BuzzGUI.Common; views left out), and of the generic types
// their fields use, closed as the fields close them (with their nested types: enumerators); then
// each managed machine's assembly, and those beside it that it uses, when the machine is first
// made (its own class at once, on the thread making it, before it can tick). Compiling runs no
// code of the methods (P/Invoke stubs, which would load native libraries, are left out), so
// nothing the engine or a song holds changes. Recompiling hot methods at a higher tier happens on
// the runtime's own background thread, as before.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ReBuzz.Audio
{
    internal static class AudioWarmUp
    {
        /// <summary>The engine's namespaces whose code the audio and render threads run (and their sub-namespaces).</summary>
        static readonly string[] Namespaces =
        {
            "ReBuzz.Audio", "ReBuzz.Core", "ReBuzz.MachineManagement", "ReBuzz.ManagedMachine", "ReBuzz.NativeMachine",
            "ReBuzz.Midi", "ReBuzz.Platform", "ReBuzz.Common", "Helios.Concurrency",
        };

        /// <summary>Parts of those that never run on the audio path (edits).</summary>
        static readonly string[] Excluded = { "ReBuzz.Core.Actions" };

        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        static readonly BlockingCollection<Action> work = new BlockingCollection<Action>();
        static readonly HashSet<Assembly> assemblies = new HashSet<Assembly>();
        static readonly HashSet<Type> preparedTypes = new HashSet<Type>();
        static readonly object gate = new object();
        static Thread thread;
        static int pending;
        static readonly ManualResetEventSlim idle = new ManualResetEventSlim(true);

        /// <summary>Methods compiled so far, and the time it took (diagnostics, tests).</summary>
        internal static int Methods;
        internal static long Ticks;

        /// <summary>Starts compiling the engine's audio path in the background (once per process).</summary>
        internal static void Begin()
        {
            var engine = typeof(AudioWarmUp).Assembly;
            lock (gate)
            {
                if (!assemblies.Add(engine)) return;
            }
            Queue(() =>
            {
                var generics = new HashSet<Type>();
                foreach (var t in TypesOf(engine))
                    if (InNamespaces(t.Namespace ?? ""))
                        PrepareType(t, generics);
                foreach (var a in new[] { typeof(BuzzGUI.Interfaces.IMachine).Assembly, typeof(BuzzGUI.Common.Global).Assembly })
                    foreach (var t in TypesOf(a))
                        PrepareType(t, generics);
                PrepareGenerics(generics);
                // A wait that blocks, once: its runtime path (a P/Invoke stub, the blocking-info
                // bookkeeping) is compiled here, not the first time the audio thread waits for a
                // render thread or a lock.
                var o = new object();
                lock (o) Monitor.Wait(o, 1);
            });
        }

        /// <summary>Waits until everything queued so far is compiled (tests; at most <paramref name="timeoutMs"/>).</summary>
        internal static bool Wait(int timeoutMs = 60000) => idle.Wait(timeoutMs);

        /// <summary>
        /// A managed machine is about to be made: its assembly is compiled now, on this thread
        /// (before the machine can tick: its first buffers would compile its Work path on the audio
        /// thread), the assemblies beside it that it uses in the background. Once per assembly; a
        /// machine made again only has its own class looked at (compiled: a lookup a method).
        /// </summary>
        internal static void PrepareMachine(Type machineType)
        {
            if (machineType == null) return;
            var assembly = machineType.Assembly;
            bool first;
            lock (gate)
            {
                first = assemblies.Add(assembly);
            }
            long start = Stopwatch.GetTimestamp();
            if (!first)
            {
                PrepareMethods(machineType, null);
                Interlocked.Add(ref Ticks, Stopwatch.GetTimestamp() - start);
                return;
            }
            var generics = new HashSet<Type>();
            foreach (var t in TypesOf(assembly))
                PrepareType(t, generics);
            PrepareGenerics(generics);
            Interlocked.Add(ref Ticks, Stopwatch.GetTimestamp() - start);
            var neighbours = WithNeighbours(assembly);
            neighbours.Remove(assembly);
            if (neighbours.Count == 0) return;
            Queue(() =>
            {
                var more = new HashSet<Type>();
                foreach (var a in neighbours)
                    foreach (var t in TypesOf(a))
                        PrepareType(t, more);
                PrepareGenerics(more);
            });
        }

        /// <summary>A type the audio thread calls that isn't in the sets above (the UI dispatcher's), in the background.</summary>
        internal static void PrepareLater(Type type)
        {
            if (type == null) return;
            Queue(() => PrepareType(type, null));
        }

        /// <summary>The assembly and those it references from its own folder (a machine's libraries).</summary>
        static List<Assembly> WithNeighbours(Assembly assembly)
        {
            var list = new List<Assembly> { assembly };
            string folder = null;
            try { folder = Path.GetDirectoryName(assembly.Location); } catch (Exception) { }
            if (string.IsNullOrEmpty(folder)) return list;
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                try
                {
                    var a = Assembly.Load(name);
                    if (string.Equals(Path.GetDirectoryName(a.Location), folder, StringComparison.Ordinal))
                    {
                        lock (gate)
                        {
                            if (!assemblies.Add(a)) continue;
                        }
                        list.Add(a);
                    }
                }
                catch (Exception)
                {
                }
            }
            return list;
        }

        static void Queue(Action action)
        {
            lock (gate)
            {
                pending++;
                idle.Reset();
                if (thread == null)
                {
                    thread = new Thread(Run) { IsBackground = true, Name = "Audio warm-up", Priority = ThreadPriority.BelowNormal };
                    thread.Start();
                }
            }
            work.Add(action);
        }

        static void Run()
        {
            foreach (var action in work.GetConsumingEnumerable())
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    action();
                }
                catch (Exception)
                {
                    // Warm-up is best effort: whatever it didn't compile compiles when first called.
                }
                Interlocked.Add(ref Ticks, Stopwatch.GetTimestamp() - start);
                lock (gate)
                {
                    if (--pending == 0)
                        idle.Set();
                }
            }
        }

        static bool InNamespaces(string ns)
        {
            foreach (var x in Excluded)
                if (ns == x || ns.StartsWith(x + ".", StringComparison.Ordinal)) return false;
            foreach (var n in Namespaces)
                if (ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal)) return true;
            return false;
        }

        static Type[] TypesOf(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return Array.FindAll(e.Types, t => t != null);
            }
            catch (Exception)
            {
                return Type.EmptyTypes;
            }
        }

        /// <summary>Compiles the type's own methods (once); collects the closed generic types its fields use.</summary>
        static void PrepareType(Type t, HashSet<Type> generics)
        {
            if (t.IsInterface || t.ContainsGenericParameters || IsView(t)) return;
            lock (gate)
            {
                if (!preparedTypes.Add(t)) return;
            }
            PrepareMethods(t, null);
            try
            {
                foreach (var f in t.GetFields(All))
                    CollectGeneric(f.FieldType, generics);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>A WPF or Avalonia control, window or the like: UI code, never on the audio path.</summary>
        static bool IsView(Type t)
        {
            try
            {
                for (var b = t.BaseType; b != null; b = b.BaseType)
                {
                    string ns = b.Namespace ?? "";
                    if (ns.StartsWith("System.Windows", StringComparison.Ordinal) || ns.StartsWith("Avalonia", StringComparison.Ordinal))
                        return true;
                }
            }
            catch (Exception)
            {
                return true;                            // its base can't load: nothing to compile either
            }
            return false;
        }

        static void PrepareMethods(Type t, RuntimeTypeHandle[] instantiation)
        {
            MethodBase[] methods;
            try
            {
                var m = t.GetMethods(All);
                var c = t.GetConstructors(All);
                methods = new MethodBase[m.Length + c.Length];
                Array.Copy(m, methods, m.Length);
                Array.Copy(c, 0, methods, m.Length, c.Length);
            }
            catch (Exception)
            {
                return;
            }
            foreach (var m in methods)
                Prepare(m, instantiation);
        }

        static void CollectGeneric(Type t, HashSet<Type> generics)
        {
            while (t.IsArray || t.IsByRef || t.IsPointer) t = t.GetElementType();
            if (t.IsConstructedGenericType && !t.ContainsGenericParameters && generics.Count < 2000)
                generics.Add(t);
        }

        /// <summary>
        /// The closed generic types the fields use, their nested types closed alike (List's
        /// enumerator), and the generic types their own fields use, once more.
        /// </summary>
        static void PrepareGenerics(HashSet<Type> generics)
        {
            var next = new HashSet<Type>();
            for (int depth = 0; depth < 2 && generics.Count > 0; depth++)
            {
                foreach (var g in generics)
                {
                    PrepareGeneric(g, next);
                    try
                    {
                        var args = g.GetGenericArguments();
                        foreach (var nested in g.GetGenericTypeDefinition().GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                            if (nested.IsGenericTypeDefinition && nested.GetGenericArguments().Length == args.Length)
                                PrepareGeneric(nested.MakeGenericType(args), next);
                    }
                    catch (Exception)
                    {
                    }
                }
                generics = next;
                next = new HashSet<Type>();
            }
        }

        static void PrepareGeneric(Type g, HashSet<Type> next)
        {
            lock (gate)
            {
                if (!preparedTypes.Add(g)) return;
            }
            RuntimeTypeHandle[] instantiation;
            try
            {
                instantiation = Array.ConvertAll(g.GetGenericArguments(), a => a.TypeHandle);
            }
            catch (Exception)
            {
                return;
            }
            PrepareMethods(g, instantiation);
            try
            {
                foreach (var f in g.GetFields(All))
                    CollectGeneric(f.FieldType, next);
            }
            catch (Exception)
            {
            }
        }

        static void Prepare(MethodBase m, RuntimeTypeHandle[] instantiation)
        {
            if (m.IsAbstract || m.ContainsGenericParameters) return;
            if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) return;      // would load its native library
            if ((m.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0) return;
            try
            {
                var handle = m.MethodHandle;
                // The method's entry point first: without one (made lazily since .NET 9, as for the
                // interface implementations most of the audio path calls), PrepareMethod does nothing.
                try { handle.GetFunctionPointer(); } catch (Exception) { }
                if (instantiation == null)
                    RuntimeHelpers.PrepareMethod(handle);
                else
                    RuntimeHelpers.PrepareMethod(handle, instantiation);
                Interlocked.Increment(ref Methods);
            }
            catch (Exception)
            {
                // A method that can't be compiled ahead (a missing dependency, an unmanaged entry
                // point) compiles when first called, as before.
            }
        }
    }
}
