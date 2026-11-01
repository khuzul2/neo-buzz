// Experiment helper for docs/specs/engine-split.md. See CensusTool.csproj.
//
// Usage: CensusTool <args.txt> <out.tsv>
//   args.txt lines:  src:<path> | ref:<path> | def:<symbol> | analyzer:<path>
//   out.tsv columns: severity, id, file, line, column, message
// or:    CensusTool refs <out.tsv> <dll>...   (see MachineRefs.cs)
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "refs") return MachineRefs.Run(args);

        var sources = new List<string>();
        var refs = new List<string>();
        var defines = new List<string>();
        var analyzerPaths = new List<string>();
        foreach (var raw in File.ReadAllLines(args[0]))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            int i = line.IndexOf(':');
            var kind = line.Substring(0, i);
            var val = line.Substring(i + 1);
            switch (kind)
            {
                case "src": sources.Add(val); break;
                case "ref": refs.Add(val); break;
                case "def": defines.Add(val); break;
                case "analyzer": analyzerPaths.Add(val); break;
            }
        }

        var parse = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: defines);
        var trees = sources.Distinct().Select(p =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(p), parse, path: p)).ToList();
        var metadata = refs.Distinct().Select(r => MetadataReference.CreateFromFile(r)).ToList();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable);
        var compilation = CSharpCompilation.Create("EngineCensus", trees, metadata, options);

        var diags = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        // CA1416 (platform compatibility), with the SDK's own analyzer.
        var analyzers = new List<DiagnosticAnalyzer>();
        foreach (var path in analyzerPaths)
        {
            var asm = new AnalyzerFileReference(path, new Loader());
            analyzers.AddRange(asm.GetAnalyzers(LanguageNames.CSharp)
                .Where(a => a.SupportedDiagnostics.Any(s => s.Id == "CA1416")));
        }
        if (analyzers.Count > 0)
        {
            var globalOptions = new Dictionary<string, string>
            {
                ["build_property.TargetFramework"] = "net10.0",
                ["build_property.TargetFrameworkIdentifier"] = ".NETCoreApp",
                ["build_property.TargetFrameworkVersion"] = "v10.0",
                ["build_property._SupportedPlatformList"] = "Linux,macOS,Windows",
            };
            var provider = new OptionsProvider(globalOptions);
            var ca = compilation
                .WithOptions(options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["CA1416"] = ReportDiagnostic.Warn }))
                .WithAnalyzers(ImmutableArray.CreateRange(analyzers),
                    new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, provider));
            var analyzerDiags = ca.GetAnalyzerDiagnosticsAsync(CancellationToken.None).Result;
            diags.AddRange(analyzerDiags.Where(d => d.Id == "CA1416"));
        }

        using var w = new StreamWriter(args[1]);
        foreach (var d in diags)
        {
            var span = d.Location.GetLineSpan();
            var msg = d.GetMessage().Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            w.WriteLine($"{d.Severity}\t{d.Id}\t{span.Path}\t{span.StartLinePosition.Line + 1}\t{span.StartLinePosition.Character + 1}\t{msg}");
        }
        Console.WriteLine($"{trees.Count} files, {diags.Count(d => d.Severity == DiagnosticSeverity.Error)} errors, {diags.Count(d => d.Id == "CA1416")} CA1416");
        return 0;
    }

    sealed class Loader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    sealed class OptionsProvider : AnalyzerConfigOptionsProvider
    {
        readonly Opts global;
        public OptionsProvider(Dictionary<string, string> g) { global = new Opts(g); }
        public override AnalyzerConfigOptions GlobalOptions => global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => global;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => global;
    }

    sealed class Opts : AnalyzerConfigOptions
    {
        readonly Dictionary<string, string> values;
        public Opts(Dictionary<string, string> v) { values = v; }
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value);
    }
}
