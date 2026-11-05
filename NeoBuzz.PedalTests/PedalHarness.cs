// Fork (docs/specs/mcp-server.md §12.13, U6): a host for the Pedal effects' Work, without ReBuzz. Each effect
// gets a MasterInfo at 48 kHz; Global.Buzz is a stub that answers what they ask of it (the sample rate, the
// song's events) with defaults. Blocks run as ReBuzz runs a managed effect: the input copied in, the
// machine's output taken only when Work returns true (otherwise its input passes, ManagedMachineHost.Work).
global using System;
global using System.IO;

using System.Collections.Generic;
using System.Reflection;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace NeoBuzz.PedalTests
{
    sealed class FakeHost : IBuzzMachineHost
    {
        public const int Rate = 48000;
        public IMachine Machine => null;
        public MasterInfo MasterInfo { get; } = new MasterInfo { BeatsPerMin = 120, TicksPerBeat = 4, SamplesPerSec = Rate, SamplesPerTick = Rate / 8, TicksPerSec = 8 };
        public SubTickInfo SubTickInfo { get; } = new SubTickInfo { SubTicksPerTick = 1, SamplesPerSubTick = Rate / 8 };
        public int MsToSamples(float t) => (int)(t * Rate / 1000f);
        public int OutputChannelCount { get; set; } = 1;
        public int InputChannelCount { get; set; } = 1;
    }

    /// <summary>An interface answered with defaults: 48000 for a sample rate, a stub for an interface, else default.</summary>
    public class Stub : DispatchProxy
    {
        public static T Of<T>() where T : class => Create<T, Stub>();

        /// <summary>Calls answered so far (each allocates a little: the proxy boxes what it returns).</summary>
        public static long Calls;

        protected override object Invoke(MethodInfo method, object[] args)
        {
            System.Threading.Interlocked.Increment(ref Calls);
            var t = method.ReturnType;
            if (t == typeof(void)) return null;
            if (method.Name.Contains("SampleRate") && t == typeof(int)) return FakeHost.Rate;
            if (t.IsInterface)
                return typeof(Stub).GetMethod(nameof(Of))!.MakeGenericMethod(t).Invoke(null, null);
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }
    }

    /// <summary>One Pedal effect under test: how to make it and run a block.</summary>
    public sealed class Effect
    {
        public string Name;
        public Func<IBuzzMachineHost, object> Create;
        /// <summary>output, input, n → Work's result.</summary>
        public Func<object, Sample[], Sample[], int, bool> Work;
        /// <summary>The machine's parameters (properties with ParameterDecl), in order.</summary>
        public IReadOnlyList<PropertyInfo> Parameters(object m)
        {
            var list = new List<PropertyInfo>();
            foreach (var p in m.GetType().GetProperties())
                if (p.GetCustomAttribute<ParameterDecl>() != null) list.Add(p);
            list.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            return list;
        }

        public override string ToString() => Name;

        static Effect Simple<T>(string name, Func<IBuzzMachineHost, T> make, Func<T, Sample[], Sample[], int, WorkModes, bool> work) where T : class =>
            new Effect { Name = name, Create = h => make(h), Work = (m, o, i, n) => work((T)m, o, i, n, WorkModes.WM_READWRITE) };

        public static readonly Effect[] All =
        {
            Simple("Pedal Shaper", h => new PedalShaper.PedalShaperMachine(h), (m, o, i, n, w) => m.Work(o, i, n, w)),
            Simple("Pedal Plate", h => new WDE.PedalPlate.PedalPlateMachine(h), (m, o, i, n, w) => m.Work(o, i, n, w)),
            new Effect
            {
                Name = "Pedal Gate",
                Create = h => new WDE.PedalGate.PedalGateMachine(h),
                Work = (m, o, i, n) =>
                {
                    // A multi-I/O effect: the main input and no sidechain, one output (lists made once).
                    GateOut[0] = o;
                    GateIn[0] = i;
                    return ((WDE.PedalGate.PedalGateMachine)m).Work(GateOut, GateIn, n, WorkModes.WM_READWRITE);
                },
            },
            Simple("Pedal Comp", h => new WDE.PedalComp.PedalCompMachine(h), (m, o, i, n, w) => m.Work(o, i, n, w)),
            Simple("Pedal MComp", h => new PedalMComp.PedalMCompMachine(h), (m, o, i, n, w) => m.Work(o, i, n, w)),
            Simple("Pedal Chorus", h => new WDE.PedalChorus.PedalChorusMachine(h), (m, o, i, n, w) => m.Work(o, i, n, w)),
            Simple("Pedal Filter", h => new WDE.PedalFilter.PedalFilterMachine(h), (m, o, i, n, w) => m.Work(o, i, n, w)),
            Simple("Pedal EQ", h => new WDE.PedalEQ.PedalEQMachine(h), (m, o, i, n, w) => m.Work(o, i, n, w)),
        };

        static readonly List<Sample[]> GateOut = new List<Sample[]> { null }, GateIn = new List<Sample[]> { null, null };

        public static Effect Named(string name) => Array.Find(All, e => e.Name == name);
    }

    /// <summary>Runs an effect over a whole signal in blocks, as ReBuzz does.</summary>
    sealed class Runner
    {
        static Runner() { BuzzGUI.Common.Global.Buzz ??= Stub.Of<IBuzz>(); }

        public readonly Effect Effect;
        public readonly object Machine;
        readonly Sample[] input = new Sample[256], output = new Sample[256];

        public Runner(Effect e, Action<object> setup = null)
        {
            Effect = e;
            Machine = e.Create(new FakeHost());
            // As ReBuzz does when it makes a machine: every parameter at its declared default.
            foreach (var p in e.Parameters(Machine))
            {
                var decl = p.GetCustomAttribute<ParameterDecl>()!;
                if (p.SetMethod == null) continue;
                if (p.PropertyType == typeof(int)) p.SetValue(Machine, decl.DefValue is int d ? d : decl.MinValue);
                else if (p.PropertyType == typeof(bool)) p.SetValue(Machine, decl.DefValue is bool b && b);
            }
            setup?.Invoke(Machine);
        }

        public void Set(string property, int value) => Machine.GetType().GetProperty(property)!.SetValue(Machine, value);

        /// <summary>
        /// Processes <paramref name="left"/>/<paramref name="right"/> (±1) from <paramref name="from"/> for
        /// <paramref name="count"/> frames in blocks of <paramref name="block"/>, into <paramref name="outL"/>/<paramref name="outR"/> (±1).
        /// </summary>
        public void Run(float[] left, float[] right, int from, int count, int block, float[] outL, float[] outR)
        {
            for (int at = from; at < from + count; at += block)
            {
                int n = Math.Min(block, from + count - at);
                for (int i = 0; i < n; i++)
                {
                    input[i].L = left[at + i] * 32768f;
                    input[i].R = right[at + i] * 32768f;
                    output[i] = default;
                }
                bool wrote = Effect.Work(Machine, output, input, n);
                var src = wrote ? output : input;
                for (int i = 0; i < n; i++)
                {
                    outL[at + i] = src[i].L / 32768f;
                    outR[at + i] = src[i].R / 32768f;
                }
            }
        }

        /// <summary>The whole signal, from a fresh machine.</summary>
        public static (float[] L, float[] R) Render(Effect e, float[] left, float[] right, int block, Action<object> setup = null, Action<Runner, int> at = null)
        {
            var r = new Runner(e, setup);
            var ol = new float[left.Length];
            var or = new float[left.Length];
            if (at == null) r.Run(left, right, 0, left.Length, block, ol, or);
            else
                for (int pos = 0; pos < left.Length; pos += block)
                {
                    at(r, pos);
                    r.Run(left, right, pos, Math.Min(block, left.Length - pos), block, ol, or);
                }
            return (ol, or);
        }
    }

    static class PedalReport
    {
        /// <summary>A measured figure: the test log, and pedal-measurements.txt in MEASUREMENTS_DIR (CI shows it).</summary>
        public static void Report(string what)
        {
            TestContext.Out.WriteLine(what);
            var dir = Environment.GetEnvironmentVariable("MEASUREMENTS_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            lock (typeof(PedalReport))
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "pedal-measurements.txt"), what + Environment.NewLine);
            }
        }
    }
}
