using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace ReBuzzTests
{
    /// <summary>
    /// docs/specs/engine-split.md §4: BuzzGUI.Interfaces and BuzzGUI.Common build twice (Windows and
    /// portable). The Windows build must keep every type and member that the prebuilt managed
    /// machines in RunTime/Gear use. Each machine's references into the two assemblies are resolved
    /// against the builds these tests run with, as the runtime does when the machine runs.
    /// </summary>
    public class PrebuiltMachineBindingTests
    {
        static readonly string[] Api = { "BuzzGUI.Interfaces", "BuzzGUI.Common" };

        /// <summary>
        /// Broken before the engine split: upstream changed ISong.SequenceAdded, SequenceRemoved and
        /// SequenceChanged from Action&lt;int&gt; to Action&lt;int, ISequence&gt;, so these machines
        /// can't subscribe to them. Listed so that the test catches new breaks.
        /// </summary>
        static readonly HashSet<string> KnownBreaks = new[] { "Jeskola Live.NET.dll", "Yitten Mongoose.NET.dll" }
            .SelectMany(dll => new[] { "add_", "remove_" }.SelectMany(a => new[] { "SequenceAdded", "SequenceRemoved", "SequenceChanged" }
                .Select(e => $"{dll}: BuzzGUI.Interfaces.ISong.{a}{e}")))
            .ToHashSet();

        [Test]
        public void EveryPrebuiltManagedMachineBindsToTheMachineApi()
        {
            var gear = FindGear();
            var machines = Directory.GetFiles(gear, "*.dll", SearchOption.AllDirectories)
                .Where(ReferencesTheApi).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var context = new MachineContext(gear);
            var failures = new List<string>();
            int resolved = 0;

            foreach (var path in machines)
            {
                string dll = Path.GetFileName(path);
                Module module;
                try { module = context.LoadFile(path).ManifestModule; }
                catch (Exception e) { failures.Add($"{dll}: {e.GetType().Name}: {e.Message}"); continue; }

                using var pe = new PEReader(File.OpenRead(path));
                var md = pe.GetMetadataReader();
                foreach (var h in md.TypeReferences)
                {
                    if (!InApi(md, h)) continue;
                    try { module.ResolveType(MetadataTokens.GetToken(h)); resolved++; }
                    catch (Exception e) { failures.Add($"{dll}: type {Name(md, h)}: {e.GetType().Name}: {e.Message}"); }
                }
                foreach (var h in md.MemberReferences)
                {
                    var mr = md.GetMemberReference(h);
                    if (mr.Parent.Kind != HandleKind.TypeReference || !InApi(md, (TypeReferenceHandle)mr.Parent)) continue;
                    string member = $"{dll}: {Name(md, (TypeReferenceHandle)mr.Parent)}.{md.GetString(mr.Name)}";
                    try { module.ResolveMember(MetadataTokens.GetToken(h)); resolved++; }
                    catch (Exception e)
                    {
                        if (KnownBreaks.Contains(member))
                            TestContext.Out.WriteLine("known break: " + member);
                        else
                            failures.Add($"{member}: {e.GetType().Name}: {e.Message}");
                    }
                }
            }

            TestContext.Out.WriteLine($"{machines.Count} machines, {resolved} references into the machine API resolved");
            Assert.That(machines, Has.Count.GreaterThanOrEqualTo(19), "prebuilt managed machines in " + gear);
            Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));
        }

        static string FindGear()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var gear = Path.Combine(dir.FullName, "RunTime", "Gear");
                if (Directory.Exists(Path.Combine(gear, "Generators"))) return gear;
            }
            throw new DirectoryNotFoundException("RunTime/Gear above " + TestContext.CurrentContext.TestDirectory);
        }

        static bool ReferencesTheApi(string path)
        {
            try
            {
                using var pe = new PEReader(File.OpenRead(path));
                if (!pe.HasMetadata) return false;
                var md = pe.GetMetadataReader();
                return md.IsAssembly && md.AssemblyReferences.Any(a => Api.Contains(md.GetString(md.GetAssemblyReference(a).Name)));
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }

        static bool InApi(MetadataReader md, TypeReferenceHandle h)
        {
            var scope = md.GetTypeReference(h).ResolutionScope;
            if (scope.Kind == HandleKind.TypeReference) return InApi(md, (TypeReferenceHandle)scope);
            return scope.Kind == HandleKind.AssemblyReference
                && Api.Contains(md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name));
        }

        static string Name(MetadataReader md, TypeReferenceHandle h)
        {
            var tr = md.GetTypeReference(h);
            return md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
        }

        /// <summary>
        /// The machines and the libraries beside them; BuzzGUI.Interfaces, BuzzGUI.Common and the
        /// framework come from the default context, as they do in ReBuzz.
        /// </summary>
        sealed class MachineContext(string gear) : AssemblyLoadContext("prebuilt machines")
        {
            readonly Dictionary<string, Assembly> loaded = new(StringComparer.OrdinalIgnoreCase);

            public Assembly LoadFile(string path)
            {
                var name = AssemblyName.GetAssemblyName(path).Name ?? path;
                if (!loaded.TryGetValue(name, out var asm))
                    loaded[name] = asm = LoadFromAssemblyPath(path);
                return asm;
            }

            protected override Assembly? Load(AssemblyName name)
            {
                if (name.Name == null || Api.Contains(name.Name)) return null;
                if (loaded.TryGetValue(name.Name, out var asm)) return asm;
                var file = Directory.GetFiles(gear, name.Name + ".dll", SearchOption.AllDirectories).FirstOrDefault();
                return file != null ? LoadFile(file) : null;
            }
        }
    }
}
