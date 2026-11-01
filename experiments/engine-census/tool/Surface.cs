// Experiment helper for docs/specs/engine-split.md §4.3: the public surface of an assembly, one
// sorted line per visible type and member, read from metadata (no loading). "census.py surface"
// compares the portable build of BuzzGUI.Interfaces and BuzzGUI.Common with the Windows build:
// every line of the portable build must appear in the Windows build, so #if WINDOWS only ever
// adds types and members and never changes a signature, base type or interface list.
//
// Usage: CensusTool surface <out.txt> <dll>
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class Surface
{
    public static int Run(string[] args)
    {
        using var fs = File.OpenRead(args[2]);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var names = new Names();
        var lines = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            if (!Visible(md, td)) continue;
            string type = Names.FullName(md, th);

            string kind = (td.Attributes & TypeAttributes.Interface) != 0 ? "interface" : "class";
            string baseName = td.BaseType.IsNil ? "" : names.Of(md, td.BaseType);
            if (baseName == "System.Enum") kind = "enum";
            else if (baseName == "System.ValueType") kind = "struct";
            else if (baseName == "System.MulticastDelegate") kind = "delegate";
            var flags = new List<string>();
            if ((td.Attributes & TypeAttributes.Abstract) != 0 && kind == "class") flags.Add("abstract");
            if ((td.Attributes & TypeAttributes.Sealed) != 0 && kind == "class") flags.Add("sealed");
            var ifaces = td.GetInterfaceImplementations()
                .Select(i => names.Of(md, md.GetInterfaceImplementation(i).Interface)).OrderBy(x => x, StringComparer.Ordinal);
            int gen = td.GetGenericParameters().Count;
            lines.Add($"type {type} {kind}{(gen > 0 ? $" generic{gen}" : "")}{Join(flags)} : {baseName} [{string.Join(", ", ifaces)}]");

            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (!Visible(m.Attributes & MethodAttributes.MemberAccessMask)) continue;
                var sig = m.DecodeSignature(names, null);
                var mf = new List<string>();
                if ((m.Attributes & MethodAttributes.Static) != 0) mf.Add("static");
                if ((m.Attributes & MethodAttributes.Abstract) != 0) mf.Add("abstract");
                else if ((m.Attributes & MethodAttributes.Virtual) != 0) mf.Add("virtual");
                if ((m.Attributes & MethodAttributes.Final) != 0) mf.Add("final");
                int mgen = m.GetGenericParameters().Count;
                lines.Add($"method {type}::{md.GetString(m.Name)}{(mgen > 0 ? $"`{mgen}" : "")}({string.Join(", ", sig.ParameterTypes)}) : {sig.ReturnType}{Join(mf)}");
            }

            foreach (var fh in td.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if (!Visible(f.Attributes & FieldAttributes.FieldAccessMask)) continue;
                var ff = new List<string>();
                if ((f.Attributes & FieldAttributes.Static) != 0) ff.Add("static");
                if ((f.Attributes & FieldAttributes.InitOnly) != 0) ff.Add("readonly");
                if ((f.Attributes & FieldAttributes.Literal) != 0)
                {
                    var c = f.GetDefaultValue();
                    ff.Add("const=" + (c.IsNil ? "?" : Convert.ToHexString(md.GetBlobBytes(md.GetConstant(c).Value))));
                }
                lines.Add($"field {type}::{md.GetString(f.Name)} : {f.DecodeSignature(names, null)}{Join(ff)}");
            }

            foreach (var ph in td.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                var acc = p.GetAccessors();
                if (!Accessible(md, acc.Getter) && !Accessible(md, acc.Setter)) continue;
                var sig = p.DecodeSignature(names, null);
                string idx = sig.ParameterTypes.Length > 0 ? $"[{string.Join(", ", sig.ParameterTypes)}]" : "";
                lines.Add($"property {type}::{md.GetString(p.Name)}{idx} : {sig.ReturnType}");
            }

            foreach (var eh in td.GetEvents())
            {
                var e = md.GetEventDefinition(eh);
                var acc = e.GetAccessors();
                if (!Accessible(md, acc.Adder) && !Accessible(md, acc.Remover)) continue;
                lines.Add($"event {type}::{md.GetString(e.Name)} : {names.Of(md, e.Type)}");
            }
        }

        File.WriteAllLines(args[1], lines);
        Console.WriteLine($"{Path.GetFileName(args[2])}: {lines.Count} surface lines");
        return 0;
    }

    static string Join(List<string> flags) => flags.Count == 0 ? "" : " " + string.Join(" ", flags);

    static bool Visible(MethodAttributes a) =>
        a == MethodAttributes.Public || a == MethodAttributes.Family || a == MethodAttributes.FamORAssem;

    static bool Visible(FieldAttributes a) =>
        a == FieldAttributes.Public || a == FieldAttributes.Family || a == FieldAttributes.FamORAssem;

    static bool Accessible(MetadataReader md, MethodDefinitionHandle h) =>
        !h.IsNil && Visible(md.GetMethodDefinition(h).Attributes & MethodAttributes.MemberAccessMask);

    static bool Visible(MetadataReader md, TypeDefinition td)
    {
        var v = td.Attributes & TypeAttributes.VisibilityMask;
        if (v == TypeAttributes.Public) return true;
        if (v != TypeAttributes.NestedPublic && v != TypeAttributes.NestedFamily && v != TypeAttributes.NestedFamORAssem)
            return false;
        return Visible(md, md.GetTypeDefinition(td.GetDeclaringType()));
    }

    sealed class Names : ISignatureTypeProvider<string, object>
    {
        public string Of(MetadataReader md, EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => FullName(md, (TypeDefinitionHandle)h),
            HandleKind.TypeReference => FullName(md, (TypeReferenceHandle)h),
            HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, null),
            _ => "?",
        };

        public static string FullName(MetadataReader md, TypeDefinitionHandle h)
        {
            var td = md.GetTypeDefinition(h);
            var name = md.GetString(td.Name);
            if (!td.GetDeclaringType().IsNil) return FullName(md, td.GetDeclaringType()) + "/" + name;
            var ns = md.GetString(td.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        public static string FullName(MetadataReader md, TypeReferenceHandle h)
        {
            var tr = md.GetTypeReference(h);
            var name = md.GetString(tr.Name);
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                return FullName(md, (TypeReferenceHandle)tr.ResolutionScope) + "/" + name;
            var ns = md.GetString(tr.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetFunctionPointerType(MethodSignature<string> s) => $"fnptr({string.Join(", ", s.ParameterTypes)}) : {s.ReturnType}";
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
        public string GetGenericMethodParameter(object c, int i) => "!!" + i;
        public string GetGenericTypeParameter(object c, int i) => "!" + i;
        public string GetModifiedType(string m, string u, bool req) => u + (req ? " modreq(" : " modopt(") + m + ")";
        public string GetPinnedType(string e) => e + " pinned";
        public string GetPointerType(string e) => e + "*";
        public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
        public string GetSZArrayType(string e) => e + "[]";
        public string GetTypeFromDefinition(MetadataReader md, TypeDefinitionHandle h, byte k) => FullName(md, h);
        public string GetTypeFromReference(MetadataReader md, TypeReferenceHandle h, byte k) => FullName(md, h);
        public string GetTypeFromSpecification(MetadataReader md, object c, TypeSpecificationHandle h, byte k) =>
            md.GetTypeSpecification(h).DecodeSignature(this, c);
    }
}
