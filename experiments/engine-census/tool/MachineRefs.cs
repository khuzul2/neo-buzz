// Experiment helper for docs/specs/engine-split.md §4: which members of BuzzGUI.Interfaces and
// BuzzGUI.Common do prebuilt managed machine DLLs reference? Reads metadata only (no loading).
//
// Usage: CensusTool refs <out.tsv> <dll>...
//   out.tsv columns: dll, target framework, kind (asmref|typeref|memberref|impl), assembly, type, member
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class MachineRefs
{
    static readonly string[] Watched = { "BuzzGUI.Interfaces", "BuzzGUI.Common", "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms" };

    public static int Run(string[] args)
    {
        using var w = new StreamWriter(args[1]);
        foreach (var path in args.Skip(2))
        {
            try { Scan(path, w); }
            catch (Exception e) { w.WriteLine($"{Path.GetFileName(path)}\t?\terror\t\t\t{e.Message}"); }
        }
        return 0;
    }

    static void Scan(string path, StreamWriter w)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) return;
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly) return;
        string name = Path.GetFileName(path);

        string tfm = "";
        foreach (var h in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(h);
            if (TypeNameOf(md, ca.Constructor) == "System.Runtime.Versioning.TargetFrameworkAttribute")
            {
                var blob = md.GetBlobReader(ca.Value);
                blob.ReadUInt16();
                tfm = blob.ReadSerializedString();
            }
        }
        if (tfm == "")
        {
            // .NET Framework 4.0-era assemblies have no TargetFrameworkAttribute; report mscorlib's version.
            foreach (var ah in md.AssemblyReferences)
            {
                var ar = md.GetAssemblyReference(ah);
                if (md.GetString(ar.Name) == "mscorlib") tfm = "mscorlib " + ar.Version;
            }
        }

        foreach (var ah in md.AssemblyReferences)
        {
            var ar = md.GetAssemblyReference(ah);
            var an = md.GetString(ar.Name);
            if (Watched.Contains(an)) w.WriteLine($"{name}\t{tfm}\tasmref\t{an}\t{ar.Version}\t");
        }

        foreach (var th in md.TypeReferences)
        {
            var (asm, type) = Resolve(md, th);
            if (Watched.Contains(asm)) w.WriteLine($"{name}\t{tfm}\ttyperef\t{asm}\t{type}\t");
        }

        foreach (var mh in md.MemberReferences)
        {
            var mr = md.GetMemberReference(mh);
            if (mr.Parent.Kind != HandleKind.TypeReference) continue;
            var (asm, type) = Resolve(md, (TypeReferenceHandle)mr.Parent);
            if (Watched.Contains(asm)) w.WriteLine($"{name}\t{tfm}\tmemberref\t{asm}\t{type}\t{md.GetString(mr.Name)}");
        }

        foreach (var tdh in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdh);
            var self = md.GetString(td.Namespace) + "." + md.GetString(td.Name);
            foreach (var ih in td.GetInterfaceImplementations())
            {
                var ii = md.GetInterfaceImplementation(ih);
                if (ii.Interface.Kind != HandleKind.TypeReference) continue;
                var (asm, type) = Resolve(md, (TypeReferenceHandle)ii.Interface);
                if (Watched.Contains(asm)) w.WriteLine($"{name}\t{tfm}\timpl\t{asm}\t{type}\t{self}");
            }
            if (td.BaseType.Kind == HandleKind.TypeReference)
            {
                var (asm, type) = Resolve(md, (TypeReferenceHandle)td.BaseType);
                if (Watched.Contains(asm)) w.WriteLine($"{name}\t{tfm}\tbase\t{asm}\t{type}\t{self}");
            }
        }
    }

    static (string asm, string type) Resolve(MetadataReader md, TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        var type = md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
        var scope = tr.ResolutionScope;
        while (scope.Kind == HandleKind.TypeReference)
        {
            var outer = md.GetTypeReference((TypeReferenceHandle)scope);
            type = md.GetString(outer.Namespace) + "." + md.GetString(outer.Name) + "+" + md.GetString(tr.Name);
            tr = outer;
            scope = outer.ResolutionScope;
        }
        string asm = scope.Kind == HandleKind.AssemblyReference
            ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name) : "";
        return (asm, type);
    }

    static string TypeNameOf(MetadataReader md, EntityHandle ctor)
    {
        if (ctor.Kind == HandleKind.MemberReference)
        {
            var mr = md.GetMemberReference((MemberReferenceHandle)ctor);
            if (mr.Parent.Kind == HandleKind.TypeReference)
            {
                var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                return md.GetString(tr.Namespace) + "." + md.GetString(tr.Name);
            }
        }
        return "";
    }
}
