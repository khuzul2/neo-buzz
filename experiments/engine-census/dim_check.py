#!/usr/bin/env python3
"""Binary-compatibility check for docs/specs/engine-split.md §4.3 (option E2).

Question: can the engine be compiled ONCE against a portable BuzzGUI.Interfaces (without the
WPF-typed members) and still run on Windows, where the app ships the full Windows build and
existing machine DLLs call those members?

Builds six tiny net10.0 projects in out/dim-check/ (no NuGet) and runs two hosts:
  host      ships the proposed Windows build: the WPF-typed member keeps its name and signature
            but gets a default body (C# default interface member) that asks a hook the UI sets.
            Expected: OK.
  host-neg  ships today's Windows build (abstract member). Expected: TypeLoadException, because
            the portable-compiled engine type doesn't implement the member.
`object` stands in for System.Windows.Window; the CLR's dispatch rules are the same.

    python3 experiments/engine-census/dim_check.py
"""
import os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, 'out', 'dim-check')

PROJ = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>{name}</AssemblyName>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
    <OutputType>{kind}</OutputType>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
  <ItemGroup>
{refs}
  </ItemGroup>
</Project>
'''
REF = '    <Reference Include="{n}"><HintPath>{p}</HintPath><Private>{priv}</Private></Reference>'


def dll(proj, name):
    return os.path.join(ROOT, proj, 'bin', 'Debug', 'net10.0', name + '.dll')


def project(proj, name, kind, src, refs=()):
    d = os.path.join(ROOT, proj)
    os.makedirs(d, exist_ok=True)
    r = '\n'.join(REF.format(n=n, p=p, priv=priv) for n, p, priv in refs)
    with open(os.path.join(d, proj + '.csproj'), 'w') as f:
        f.write(PROJ.format(name=name, kind=kind, refs=r))
    with open(os.path.join(d, 'Src.cs'), 'w') as f:
        f.write(src)
    b = subprocess.run(['dotnet', 'build', d, '-nologo', '-v:q'], capture_output=True, text=True)
    if b.returncode != 0:
        print(b.stdout)
        sys.exit(1)


def main():
    # Today's Windows build, as machine DLLs were compiled against it: the member is abstract.
    project('api-today', 'Api', 'Library', '''namespace BuzzGUI.Interfaces {
  public interface IMachine { string Name { get; } object ParameterWindow { get; } }
}''')
    # The portable build: no WPF-typed member.
    project('api-portable', 'Api', 'Library', '''namespace BuzzGUI.Interfaces {
  public interface IMachine { string Name { get; } }
}''')
    # The proposed Windows build: same member, now with a default body that asks a UI hook.
    project('api-windows', 'Api', 'Library', '''namespace BuzzGUI.Interfaces {
  public static class WpfMachineApi { public static System.Func<IMachine, object> ParameterWindow; }
  public interface IMachine { string Name { get; } object ParameterWindow => WpfMachineApi.ParameterWindow?.Invoke(this); }
}''')
    # A machine DLL compiled against today's build that reads the WPF-typed member.
    project('machine', 'Machine', 'Library', '''using BuzzGUI.Interfaces;
public static class Probe { public static string Describe(IMachine m) => m.Name + " / " + (m.ParameterWindow ?? "null"); }''',
            [('Api', dll('api-today', 'Api'), 'false')])
    # The engine, compiled once against the portable build.
    project('engine', 'Engine', 'Library', '''using BuzzGUI.Interfaces;
public class MachineCore : IMachine { public string Name => "MachineCore"; }
public static class Factory { public static IMachine Create() => new MachineCore(); }''',
            [('Api', dll('api-portable', 'Api'), 'false')])
    common = [('Engine', dll('engine', 'Engine'), 'true'), ('Machine', dll('machine', 'Machine'), 'true')]
    project('host', 'Host', 'Exe', '''using BuzzGUI.Interfaces;
public static class Program {
  public static int Main() {
    try { WpfMachineApi.ParameterWindow = m => "window of " + m.Name; System.Console.WriteLine("OK: " + Probe.Describe(Factory.Create())); return 0; }
    catch (System.Exception e) { System.Console.WriteLine("FAIL: " + e.GetType().Name + ": " + e.Message); return 1; }
  }
}''', [('Api', dll('api-windows', 'Api'), 'true')] + common)
    project('host-neg', 'HostNeg', 'Exe', '''public static class Program {
  public static int Main() {
    try { System.Console.WriteLine("OK: " + Probe.Describe(Factory.Create())); return 0; }
    catch (System.Exception e) { System.Console.WriteLine("FAIL: " + e.GetType().Name + ": " + e.Message); return 1; }
  }
}''', [('Api', dll('api-today', 'Api'), 'true')] + common)
    ok = subprocess.run(['dotnet', dll('host', 'Host')], capture_output=True, text=True)
    neg = subprocess.run(['dotnet', dll('host-neg', 'HostNeg')], capture_output=True, text=True)
    print('host (default body):     ', ok.stdout.strip())
    print('host-neg (abstract):     ', neg.stdout.strip())
    sys.exit(0 if ok.returncode == 0 and neg.returncode != 0 else 1)


if __name__ == '__main__':
    main()
