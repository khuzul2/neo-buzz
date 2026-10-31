#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 0.8): Ableton Link through the helper
// process (LinkHelper/, GPL-2.0-or-later, as Link is). ReBuzz starts ReBuzzLink.exe and talks
// to it in lines of text over its standard input and output; nothing of Link is linked in.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ReBuzz.Core.Sync
{
    /// <summary>
    /// The Link helper process: starts it, sends it commands, and reads its state lines on a
    /// thread of its own. As a <see cref="ISyncSource"/>, Link's session is followed while it has
    /// peers; alone, ReBuzz leads (it tells Link its tempo and beat).
    /// </summary>
    internal sealed class LinkClient : ISyncSource, IDisposable
    {
        /// <summary>State lines older than this mean the helper stopped talking: don't follow it.</summary>
        public const double StaleMicros = 1_000_000;

        readonly string path;
        readonly object sendLock = new object();
        readonly TimelineCell timeline = new TimelineCell();
        Process process;
        Thread reader;
        volatile int peers;
        volatile bool playing, enabled, exited;
        int version = -1;
        double lastLine = double.NaN;               // host µs of the last state line (read on the audio thread)
        double tempo;

        /// <summary>A state line changed the peers, the tempo (by more than 0.001 BPM) or the play state; on the reader's thread.</summary>
        public event Action<LinkState> StateChanged;

        /// <summary>The helper ended (or couldn't start); on the reader's thread.</summary>
        public event Action Exited;

        public LinkClient(string helperPath)
        {
            path = helperPath;
        }

        /// <summary>ReBuzzLink.exe beside ReBuzz.exe.</summary>
        public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "ReBuzzLink.exe");

        public bool IsRunning => process != null && !exited;
        public int Peers => peers;
        public bool IsPlaying => playing;
        public bool IsEnabled => enabled;
        public int ProtocolVersion => Volatile.Read(ref version);

        /// <summary>Link's tempo, as last reported (0 before the first state line).</summary>
        public double Tempo => Volatile.Read(ref tempo);

        /// <summary>Link's beat line, as last reported.</summary>
        public bool TryGetState(out Timeline line) => timeline.TryRead(out line);

        /// <summary>Starts the helper; null when it runs, else why not.</summary>
        public string Start()
        {
            if (IsRunning) return null;
            if (!File.Exists(path)) return $"The Link helper ({Path.GetFileName(path)}) isn't installed next to ReBuzz.";
            try
            {
                var info = new ProcessStartInfo(path)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? ""
                };
                process = Process.Start(info);
                if (process == null) return "The Link helper didn't start.";
                exited = false;
                reader = new Thread(Read) { IsBackground = true, Name = "Link helper" };
                reader.Start();
                return null;
            }
            catch (Exception e)
            {
                process = null;
                return "The Link helper didn't start: " + e.Message;
            }
        }

        /// <summary>Sends a command line (any thread but the audio thread).</summary>
        public void Send(string line)
        {
            lock (sendLock)
            {
                if (!IsRunning) return;
                try
                {
                    process.StandardInput.WriteLine(line);
                    process.StandardInput.Flush();
                }
                catch (Exception)
                {
                    exited = true;
                }
            }
        }

        void Read()
        {
            var p = process;
            try
            {
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    int v = LinkProtocol.ParseHello(line);
                    if (v >= 0)
                    {
                        Volatile.Write(ref version, v);
                        continue;
                    }
                    if (!LinkProtocol.TryParseState(line, out var state)) continue;
                    timeline.Write(state.Timeline);
                    Volatile.Write(ref lastLine, HostClock.Now());
                    bool changed = state.Peers != peers || state.Playing != playing || state.Enabled != enabled
                                   || Math.Abs(state.Timeline.Tempo - tempo) > 0.001;
                    peers = state.Peers;
                    playing = state.Playing;
                    enabled = state.Enabled;
                    Volatile.Write(ref tempo, state.Timeline.Tempo);
                    if (changed) StateChanged?.Invoke(state);
                }
            }
            catch (Exception)
            {
                // The pipe broke: the helper is gone.
            }
            exited = true;
            peers = 0;
            Exited?.Invoke();
        }

        // ------------------------------------------------------------------ ISyncSource

        /// <summary>Audio thread: Link's beat line while it has peers and talks.</summary>
        public bool TryGetTimeline(out Timeline line)
        {
            line = default;
            if (exited || !enabled || peers <= 0) return false;
            double last = Volatile.Read(ref lastLine);
            if (double.IsNaN(last) || HostClock.Now() - last > StaleMicros) return false;
            return timeline.TryRead(out line);
        }

        /// <summary>Phases match over the session's bar (Link's quantum).</summary>
        public double QuantumFor(int beatsPerBar) => beatsPerBar;

        /// <summary>A start may be heard at once; it waits for Link's phase (Live's: the next bar line from a bar's start).</summary>
        public double EarliestStart(in Timeline line, double heardMicros) => heardMicros;

        public void Dispose()
        {
            var p = process;
            if (p == null) return;
            Send(LinkProtocol.Quit);
            try
            {
                lock (sendLock) p.StandardInput.Close();
            }
            catch (Exception) { }
            try
            {
                if (!p.WaitForExit(1000)) p.Kill();
            }
            catch (Exception) { }
            p.Dispose();
            process = null;
            exited = true;
        }
    }
}
