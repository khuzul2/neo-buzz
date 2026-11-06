// Fork (docs/specs/mcp-server.md §12.15): a host for the Pedal synths' Work, without ReBuzz, as PedalHarness.cs is for
// the effects. Each synth gets a MasterInfo at 48 kHz (120 BPM, 4 ticks a beat: 6000 samples a tick); M1 and Tracker,
// which play the song's wavetable, get a song (a machine whose graph's IBuzz has a song with a wavetable, the song's
// play position moving a tick at a time). Notes come as ReBuzz gives them: a track's Note (and Wave, Volume or
// Velocity) through the machine's setters before the block they start, MIDI through MidiNote and MidiControlChange.
// Blocks run as ReBuzz runs a managed generator: the output cleared, then taken only when Work returns true
// (ManagedMachineHost.Work); a block never crosses an event (ReBuzz splits blocks at ticks).
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace NeoBuzz.PedalTests
{
    /// <summary>An interface whose members answer what <see cref="Answers"/> holds (pre-boxed), else a default.</summary>
    public class Fake : DispatchProxy
    {
        public static T Of<T>(Dictionary<string, object> answers) where T : class
        {
            var t = Create<T, Fake>();
            ((Fake)(object)t).Answers = answers;
            return t;
        }

        public Dictionary<string, object> Answers;

        /// <summary>Calls answered so far (each allocates its argument array: the allocation checks take them off).</summary>
        public static long Calls;

        static readonly Dictionary<Type, object> Defaults = new Dictionary<Type, object>();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<MethodInfo, string> Names = new();

        protected override object Invoke(MethodInfo method, object[] args)
        {
            System.Threading.Interlocked.Increment(ref Calls);
            string name = Names.GetOrAdd(method, static m => m.Name.StartsWith("get_") ? m.Name.Substring(4) : m.Name);
            if (Answers != null && Answers.TryGetValue(name, out var v)) return v;
            var t = method.ReturnType;
            if (t == typeof(void) || !t.IsValueType) return null;
            lock (Defaults)
            {
                if (!Defaults.TryGetValue(t, out var d)) Defaults[t] = d = Activator.CreateInstance(t);
                return d;
            }
        }
    }

    /// <summary>A mono wave in the song's wavetable (what M1 and Tracker read of it).</summary>
    sealed class FakeLayer : IWaveLayer
    {
        public float[] Data;
        public WaveFormat Format => WaveFormat.Float32;
        public int SampleCount => Data.Length;
        public int RootNote { get; set; }
        public int SampleRate { get; set; } = FakeHost.Rate;
        public int LoopStart { get; set; }
        public int LoopEnd { get; set; }
        public int ChannelCount => 1;
        public string Path => "";
        public IntPtr RawSamples => IntPtr.Zero;
        public void GetDataAsFloat(float[] output, int outoffset, int outstride, int channel, int offset, int count)
        {
            for (int i = 0; i < count; i++) output[outoffset + i * outstride] = Data[offset + i];
        }
        public void SetDataAsFloat(float[] input, int inoffset, int instride, int channel, int offset, int count) { }
        public void InvalidateData() { }
        public event PropertyChangedEventHandler PropertyChanged { add { } remove { } }
    }

    sealed class FakeWave : IWave
    {
        public int Index { get; set; }
        public string Name { get; set; } = "test";
        public WaveFlags Flags { get; set; }
        public float Volume { get; set; } = 1f;
        public ReadOnlyCollection<IWaveLayer> Layers { get; set; }
        public IEnvelope GetEnvelope(int index, IMachine m) => null;
        public void Play(IMachine m, int start = 0, int end = -1, LoopType looptype = LoopType.None, int layer = -1) { }
        public void Stop(IMachine m) { }
        public event PropertyChangedEventHandler PropertyChanged { add { } remove { } }
    }

    sealed class FakeWavetable : IWavetable
    {
        public ISong Song => null;
        public ReadOnlyCollection<IWave> Waves { get; set; }
        public float Volume { get; set; }
        public void AllocateWave(int index, string path, string name, int size, WaveFormat format, bool stereo, int root, bool add, bool wavechangedevent) { }
        public void LoadWave(int index, string path, string name, bool add) { }
        public void PlayWave(string path) { }
        public event Action<int> WaveChanged { add { } remove { } }
        public event PropertyChangedEventHandler PropertyChanged { add { } remove { } }
    }

    /// <summary>A song for the synths that play the wavetable: its play position and wave 1.</summary>
    sealed class FakeSong
    {
        public readonly Dictionary<string, object> Song = new Dictionary<string, object>();
        public readonly Dictionary<string, object> Buzz = new Dictionary<string, object>();
        public readonly IMachine Machine;
        static readonly object True = true, False = false;

        /// <summary>The transport (IBuzz.Playing), pre-boxed.</summary>
        public bool Playing { set => Buzz["Playing"] = value ? True : False; }
        static readonly object[] Positions = MakePositions();

        static object[] MakePositions()
        {
            var a = new object[4096];
            for (int i = 0; i < a.Length; i++) a[i] = i;
            return a;
        }

        public FakeSong(float[] wave1, bool loop, int rootMidi)
        {
            // A Buzz note byte for the root (its layer's RootNote is one; A4 = 69 is A-5, 0x5A).
            var layer = new FakeLayer { Data = wave1, RootNote = ((rootMidi / 12) << 4) | (rootMidi % 12 + 1), LoopStart = 0, LoopEnd = wave1.Length };
            var wave = new FakeWave { Index = 0, Flags = loop ? WaveFlags.Loop : 0, Layers = new ReadOnlyCollection<IWaveLayer>(new IWaveLayer[] { layer }) };
            var table = new FakeWavetable { Waves = new ReadOnlyCollection<IWave>(new IWave[] { wave }) };
            Song["Wavetable"] = table;
            Song["PlayPosition"] = Positions[0];
            var song = Fake.Of<ISong>(Song);
            Buzz["Song"] = song;
            Buzz["Playing"] = False;
            var buzz = Fake.Of<IBuzz>(Buzz);
            Song["Buzz"] = buzz;
            var graph = Fake.Of<IMachineGraph>(new Dictionary<string, object> { ["Buzz"] = buzz });
            Machine = Fake.Of<IMachine>(new Dictionary<string, object> { ["Graph"] = graph });
        }

        /// <summary>The song's position, in ticks (pre-boxed: setting it allocates nothing).</summary>
        public int Position { set => Song["PlayPosition"] = Positions[value & (Positions.Length - 1)]; }
    }

    sealed class SynthHost : IBuzzMachineHost
    {
        public SynthHost(IMachine machine) { Machine = machine; }
        public IMachine Machine { get; }
        public MasterInfo MasterInfo { get; } = new MasterInfo { BeatsPerMin = 120, TicksPerBeat = 4, SamplesPerSec = FakeHost.Rate, SamplesPerTick = FakeHost.Rate / 8, TicksPerSec = 8 };
        public SubTickInfo SubTickInfo { get; } = new SubTickInfo { SubTicksPerTick = 1, SamplesPerSubTick = FakeHost.Rate / 8 };
        public int MsToSamples(float t) => (int)(t * FakeHost.Rate / 1000f);
        public int OutputChannelCount { get; set; } = 1;
        public int InputChannelCount { get; set; } = 0;
    }

    /// <summary>One Pedal synth under test: how to make it, run a block and give it notes.</summary>
    public sealed class Synth
    {
        public string Name;
        public bool Polyphonic;
        /// <summary>Plays the song's wavetable (M1, Tracker): it gets a song with a wave in slot 1.</summary>
        public bool NeedsWave;
        /// <summary>A multi-output machine (Tracker): ReBuzz takes what it wrote whatever Work returns (WorkMachineManaged).</summary>
        public bool MultiIO;
        public Func<IBuzzMachineHost, object> Create;
        /// <summary>output (±32768), n → Work's result.</summary>
        public Func<object, Sample[], int, bool> Work;
        /// <summary>A track's note (a Buzz note byte; 255 is off), with what else the track needs for it (a wave).</summary>
        public Action<object, int, byte> TrackNote;

        public override string ToString() => Name;

        public IReadOnlyList<PropertyInfo> Parameters(object m)
        {
            var list = new List<PropertyInfo>();
            foreach (var p in m.GetType().GetProperties())
                if (p.GetCustomAttribute<ParameterDecl>() != null) list.Add(p);
            list.Sort((a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            return list;
        }

        static readonly List<Sample[]> TrackerOut = new List<Sample[]> { null };

        public static readonly Synth[] All =
        {
            new Synth
            {
                Name = "Pedal Juno106", Polyphonic = true, Create = h => new PedalJuno106.PedalJuno106Machine(h),
                Work = (m, o, n) => ((PedalJuno106.PedalJuno106Machine)m).Work(o, n, WorkModes.WM_WRITE),
                TrackNote = (m, t, b) => ((PedalJuno106.PedalJuno106Machine)m).SetNote(new Note(b), t),
            },
            new Synth
            {
                Name = "Pedal SH101", Polyphonic = false, Create = h => new PedalSH101.PedalSH101Machine(h),
                Work = (m, o, n) => ((PedalSH101.PedalSH101Machine)m).Work(o, n, WorkModes.WM_WRITE),
                TrackNote = (m, t, b) => ((PedalSH101.PedalSH101Machine)m).SetNote(new Note(b), t),
            },
            new Synth
            {
                Name = "Pedal FM", Polyphonic = true, Create = h => new PedalFM.PedalFMMachine(h),
                Work = (m, o, n) => ((PedalFM.PedalFMMachine)m).Work(o, n, WorkModes.WM_WRITE),
                TrackNote = (m, t, b) => ((PedalFM.PedalFMMachine)m).SetNote(new Note(b), t),
            },
            new Synth
            {
                Name = "Pedal M1", Polyphonic = true, NeedsWave = true, Create = h => new WDE.PedalM1.PedalM1Machine(h),
                Work = (m, o, n) => ((WDE.PedalM1.PedalM1Machine)m).Work(o, n, WorkModes.WM_WRITE),
                TrackNote = (m, t, b) => ((WDE.PedalM1.PedalM1Machine)m).SetNote(new Note(b), t),
            },
            new Synth
            {
                Name = "Pedal Tracker", Polyphonic = true, NeedsWave = true, MultiIO = true, Create = h => new WDE.PedalTracker.PedalTrackerMachine(h),
                Work = (m, o, n) =>
                {
                    TrackerOut[0] = o;   // out 0, the master mix (a list made once)
                    return ((WDE.PedalTracker.PedalTrackerMachine)m).Work(TrackerOut, n, WorkModes.WM_WRITE);
                },
                TrackNote = (m, t, b) =>
                {
                    var tr = (WDE.PedalTracker.PedalTrackerMachine)m;
                    if (b != 255) tr.SetWave(1, t);   // a tracker's note needs its track's wave
                    tr.SetNote(new Note(b), t);
                },
            },
        };

        public static Synth Named(string name) => Array.Find(All, s => s.Name == name);
    }

    /// <summary>Something that happens at a sample position: a track note, MIDI, a parameter set.</summary>
    public readonly struct SynthEvent
    {
        public readonly int At;
        public readonly Action<SynthRunner> Do;
        public SynthEvent(int at, Action<SynthRunner> what) { At = at; Do = what; }
    }

    /// <summary>Runs a synth over a span of time in blocks, as ReBuzz does.</summary>
    public sealed class SynthRunner
    {
        static SynthRunner() { BuzzGUI.Common.Global.Buzz ??= Stub.Of<IBuzz>(); }

        public const int Rate = FakeHost.Rate;
        public const int SamplesPerTick = Rate / 8;

        public readonly Synth Synth;
        public readonly object Machine;
        readonly FakeSong song;
        readonly Sample[] buffer = new Sample[256];
        public int Position;
        /// <summary>Where given, the first frame of each block whose Work returned false (ReBuzz then plays silence).</summary>
        public List<int> Reported;
        /// <summary>The loudest sample the machine wrote in a block whose Work returned false (±1): what saying so cut.</summary>
        public double ReportedPeak;

        /// <summary>The transport: playing or stopped (the song's IBuzz.Playing; needs a song).</summary>
        public bool Playing { set => (song ?? throw new InvalidOperationException("no song")).Playing = value; }

        /// <summary>A saw over 2 s on A4 (looped, so held notes sustain), as the profiler's test wave.</summary>
        public static float[] TestWave()
        {
            int n = 2 * Rate;
            var w = new float[n];
            double hz = 440, ph = 0;
            for (int i = 0; i < n; i++) { w[i] = (float)(0.5 * (2 * ph - 1)); ph += hz / Rate; if (ph >= 1) ph -= 1; }
            return w;
        }

        /// <param name="withSong">A song (and so a transport) for a synth that doesn't need its wavetable too.</param>
        public SynthRunner(Synth s, Action<object> setup = null, float[] wave = null, bool loop = true, bool withSong = false)
        {
            Synth = s;
            if (s.NeedsWave || withSong) song = new FakeSong(wave ?? TestWave(), loop, 69);
            Machine = s.Create(new SynthHost(song?.Machine));
            // As ReBuzz does when it makes a machine: every parameter at its declared default.
            foreach (var p in s.Parameters(Machine))
            {
                var decl = p.GetCustomAttribute<ParameterDecl>()!;
                if (p.SetMethod == null) continue;
                if (p.PropertyType == typeof(int)) p.SetValue(Machine, decl.DefValue is int d ? d : decl.MinValue);
                else if (p.PropertyType == typeof(bool)) p.SetValue(Machine, decl.DefValue is bool b && b);
            }
            setup?.Invoke(Machine);
        }

        // Typed setters, made once per parameter, so setting one allocates nothing (as ReBuzz's own calls).
        readonly Dictionary<string, Action<int>> setters = new Dictionary<string, Action<int>>();

        public void Set(string property, int value)
        {
            if (!setters.TryGetValue(property, out var set))
            {
                var p = Machine.GetType().GetProperty(property) ?? throw new ArgumentException(property);
                if (p.PropertyType == typeof(bool))
                {
                    var b = (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), Machine, p.SetMethod!);
                    set = v => b(v != 0);
                }
                else set = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), Machine, p.SetMethod!);
                setters[property] = set;
            }
            set(value);
        }

        public void Note(int track, int midi) => Synth.TrackNote(Machine, track, (byte)Buzz.MachineInterface.Note.FromMIDINote(midi).Value);
        public void Off(int track) => Synth.TrackNote(Machine, track, 255);

        // The machine's MidiNote and MidiControlChange, found as ReBuzz finds them (ManagedMachineHost: by name).
        Action<int, int, int> midiNote, midiControl;

        Action<int, int, int> Bind(string name) =>
            (Action<int, int, int>)Delegate.CreateDelegate(typeof(Action<int, int, int>), Machine, name, false, false)
            ?? throw new InvalidOperationException($"{Synth.Name} has no {name}");

        /// <summary>A MIDI note (velocity 0 is off), as ReBuzz sends one: MidiNote(channel, note, velocity).</summary>
        public void Midi(int key, int velocity) => (midiNote ??= Bind("MidiNote"))(0, key, velocity);

        /// <summary>A MIDI controller, as ReBuzz sends one: MidiControlChange(ctrl, channel, value) (0xE0: pitch bend, 14-bit).</summary>
        public void Control(int ctrl, int value) => (midiControl ??= Bind("MidiControlChange"))(ctrl, 0, value);

        /// <summary>Whether the machine takes MIDI (the server's gear_info.takes_midi test).</summary>
        public bool TakesMidi => Machine.GetType().GetMethod("MidiNote", new[] { typeof(int), typeof(int), typeof(int) }) != null;

        /// <summary>One block of <paramref name="n"/> frames into <paramref name="outL"/>/<paramref name="outR"/> (±1) at <paramref name="at"/>.</summary>
        public bool Block(int n, float[] outL, float[] outR, int at)
        {
            if (song != null) song.Position = Position / SamplesPerTick;
            for (int i = 0; i < n; i++) buffer[i] = default;
            bool wrote = Synth.Work(Machine, buffer, n);
            if (!wrote)
            {
                Reported?.Add(Position);
                for (int i = 0; i < n; i++)
                    ReportedPeak = Math.Max(ReportedPeak, Math.Max(Math.Abs(buffer[i].L), Math.Abs(buffer[i].R)) / 32768.0);
            }
            bool take = wrote || Synth.MultiIO;
            for (int i = 0; i < n; i++)
            {
                outL[at + i] = take ? buffer[i].L / 32768f : 0f;
                outR[at + i] = take ? buffer[i].R / 32768f : 0f;
            }
            Position += n;
            return wrote;
        }

        /// <summary>
        /// <paramref name="frames"/> from here, in blocks of <paramref name="block"/> split at each event (applied before
        /// the block starting where it is), into new arrays (±1).
        /// </summary>
        public (float[] L, float[] R) Run(int frames, int block, IReadOnlyList<SynthEvent> events = null)
        {
            var l = new float[frames];
            var r = new float[frames];
            Run(frames, block, events, l, r);
            return (l, r);
        }

        public void Run(int frames, int block, IReadOnlyList<SynthEvent> events, float[] l, float[] r)
        {
            int start = Position, e = 0;
            var list = events ?? Array.Empty<SynthEvent>();
            while (e < list.Count && list[e].At < start) e++;
            for (int at = 0; at < frames;)
            {
                while (e < list.Count && list[e].At <= start + at) list[e++].Do(this);
                int n = Math.Min(block, frames - at);
                if (e < list.Count) n = Math.Min(n, list[e].At - (start + at));
                Block(n, l, r, at);
                at += n;
            }
        }

        public static (float[] L, float[] R) Render(Synth s, int frames, int block, IReadOnlyList<SynthEvent> events, Action<object> setup = null)
            => new SynthRunner(s, setup).Run(frames, block, events);
    }
}
