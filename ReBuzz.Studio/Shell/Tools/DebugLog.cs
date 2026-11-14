// Fork (docs/specs/ux-overhaul.md P6b): the engine's log for the debug console. DCWriteLine (and everything that logs through
// Serilog.Log) already goes to the host's writer for warnings and errors; this sink also keeps the last few thousand lines of
// information and above in memory, so the console can show them when it opens, not only from then on. Installed once at start,
// in front of the host's logger (events still reach it, at its own level).

using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class DebugLog : ILogEventSink
    {
        internal sealed record Line(DateTime Time, LogEventLevel Level, string Text);

        public const int Capacity = 5000;

        public static DebugLog Instance { get; private set; }

        readonly object gate = new object();
        readonly Queue<Line> lines = new Queue<Line>();
        long version;

        /// <summary>Put in front of the current logger, once.</summary>
        public static DebugLog Install()
        {
            if (Instance != null) return Instance;
            var sink = new DebugLog();
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).WriteTo.Logger(Log.Logger).CreateLogger();
            return Instance = sink;
        }

        /// <summary>A log with no logger behind it (tests).</summary>
        internal static DebugLog Detached() => new DebugLog();

        public void Emit(LogEvent e) => Add(e.Level, e.RenderMessage());

        public void Add(LogEventLevel level, string text)
        {
            lock (gate)
            {
                lines.Enqueue(new Line(DateTime.Now, level, text));
                while (lines.Count > Capacity) lines.Dequeue();
                version++;
            }
        }

        /// <summary>Changes with every line, so a view knows when to redraw.</summary>
        public long Version { get { lock (gate) return version; } }

        public List<Line> Snapshot(LogEventLevel minimum = LogEventLevel.Information)
        {
            lock (gate) return lines.Where(l => l.Level >= minimum).ToList();
        }

        public void Clear()
        {
            lock (gate)
            {
                lines.Clear();
                version++;
            }
        }

        public static string Format(Line l) => $"{l.Time:HH:mm:ss.fff} {Abbreviation(l.Level)} {l.Text}";

        static string Abbreviation(LogEventLevel l) => l switch
        {
            LogEventLevel.Verbose => "VRB", LogEventLevel.Debug => "DBG", LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN", LogEventLevel.Error => "ERR", _ => "FTL",
        };
    }
}
