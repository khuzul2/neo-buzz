// Evaluation (docs/specs/mcp-server.md §10, agent tasks): a task is a brief (the exact prompt a
// real agent gets, with where to save the song), an optional setup (tool calls that prepare the
// starting song: planted problems, a reference render), a scorer (named criteria, each 0–1, from
// our meters on rendered files and from tool reads) and a scripted reference solution (the tool
// sequence a competent agent would make). In CI the reference solutions must pass and the
// starting songs must not: that validates the tools and the scorers end to end.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace ReBuzz.AgentEval
{
    /// <summary>Where a task's files live.</summary>
    internal sealed class TaskContext
    {
        public TaskContext(string task, string folder)
        {
            Task = task;
            Folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(Folder);
        }

        public string Task { get; }
        public string Folder { get; }
        /// <summary>The song the setup made (what the agent starts from).</summary>
        public string StartSong => Path.Combine(Folder, "start.bmxml");
        /// <summary>Where the brief tells the agent to save.</summary>
        public string ResultSong => Path.Combine(Folder, "result.bmxml");
        public string File(string name) => Path.Combine(Folder, name);
    }

    /// <summary>What a scorer measured: numbers and texts by name (kept in the results for diagnosis).</summary>
    internal sealed class Facts
    {
        readonly Dictionary<string, double> numbers = new();
        readonly Dictionary<string, string> texts = new();

        public double this[string key]
        {
            get => numbers.TryGetValue(key, out double v) ? v : double.NaN;
            set => numbers[key] = value;
        }

        public string Text(string key) => texts.TryGetValue(key, out var v) ? v : null;
        public void Text(string key, string value) => texts[key] = value;
        public bool Has(string key) => numbers.ContainsKey(key);

        public JsonObject ToJson()
        {
            var o = new JsonObject();
            foreach (var kv in numbers.OrderBy(k => k.Key)) o[kv.Key] = J.Finite(kv.Value);
            foreach (var kv in texts.OrderBy(k => k.Key)) o[kv.Key] = kv.Value;
            return o;
        }
    }

    internal abstract class EvalTask
    {
        public abstract string Name { get; }
        public abstract string Title { get; }

        /// <summary>The prompt a real agent gets.</summary>
        public abstract string Brief(TaskContext ctx);

        /// <summary>Builds the starting song in a new, empty song (and any reference files). Default: nothing (an empty song).</summary>
        public virtual void Setup(Studio s, TaskContext ctx) { }

        /// <summary>The scripted reference solution, run on the starting song (the runner saves the result).</summary>
        public abstract void Solve(Studio s, TaskContext ctx);

        /// <summary>Measures the song that is open.</summary>
        public abstract Facts Measure(Studio s, TaskContext ctx);

        /// <summary>Criteria from what the result measured, against the starting song's measures.</summary>
        public abstract List<Criterion> Score(Facts start, Facts result, TaskContext ctx);

        public static readonly EvalTask[] All = { new BeatTask(), new SoundMatchTask(), new FixTheMixTask(), new MasterTask(), new SongTask(), new MasterMatchTask(), new MasterLoudTask(), new FixHarshnessTask() };

        public static EvalTask Find(string name) =>
            All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No task \"{name}\". Tasks: {string.Join(", ", All.Select(t => t.Name))}.");

        // ---- scoring helpers ----------------------------------------------------------------

        /// <summary>1 at <paramref name="good"/> (and beyond), 0 at <paramref name="bad"/> (and beyond), linear between; either direction.</summary>
        public static double Linear(double x, double good, double bad)
        {
            if (double.IsNaN(x)) return 0;
            if (double.IsPositiveInfinity(x)) return good > bad ? 1 : 0;
            if (double.IsNegativeInfinity(x)) return good < bad ? 1 : 0;
            return Math.Clamp((x - bad) / (good - bad), 0, 1);
        }

        /// <summary>1 inside [lo, hi], falling to 0 over <paramref name="fall"/> outside.</summary>
        public static double Band(double x, double lo, double hi, double fall) =>
            double.IsNaN(x) ? 0 : x < lo ? Linear(x, lo, lo - fall) : x > hi ? Linear(x, hi, hi + fall) : 1;

        /// <summary>The share of note time in the best-fitting major scale (any mode), and that scale's tonic name.</summary>
        public static (double Share, string Scale) ScaleFit(IEnumerable<(int Pitch, double Length)> notes)
        {
            var list = notes.ToList();
            double total = list.Sum(n => n.Length);
            if (total <= 0) return (0, "none");
            int[] major = { 0, 2, 4, 5, 7, 9, 11 };
            string[] names = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };
            double best = -1;
            int bestTonic = 0;
            for (int t = 0; t < 12; t++)
            {
                double share = list.Where(n => major.Contains(((n.Pitch - t) % 12 + 12) % 12)).Sum(n => n.Length) / total;
                if (share > best + 1e-9) { best = share; bestTonic = t; }
            }
            return (best, $"{names[bestTonic]} major / {names[(bestTonic + 9) % 12]} minor");
        }
    }
}
