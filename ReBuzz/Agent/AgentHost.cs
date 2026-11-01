// Agent server (docs/specs/mcp-server.md §3): ReBuzz's side. Builds the MCP server over a running
// ReBuzzCore: the engine queue on ReBuzz's dispatcher, one undo group per changing call, no
// dialogs, the agent log; then serves it on stdio (headless) or Streamable HTTP (studio).
// Tool implementations talk to the engine only (never to the UI), so they move with the engine
// when it is split out for macOS (docs/specs/cross-platform.md P1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuzzGUI.Common;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;

namespace ReBuzz.Agent
{
    /// <summary>The engine thread is ReBuzz's own dispatcher (UI thread in studio mode, main thread headless).</summary>
    internal sealed class DispatcherEngineThread : IEngineThread
    {
        readonly IUiDispatcher dispatcher;

        public DispatcherEngineThread(IUiDispatcher dispatcher) => this.dispatcher = dispatcher;

        public Task<T> InvokeAsync<T>(Func<T> work)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.BeginInvoke(() =>
            {
                try { tcs.SetResult(work()); }
                catch (Exception e) { tcs.SetException(e); }
            });
            return tcs.Task;
        }
    }

    internal sealed class AgentHost : IDisposable
    {
        public const string Version = "0.1 (A0)";

        readonly List<HttpTransport> http = new();

        public AgentHost(ReBuzzCore core, IUiDispatcher dispatcher, string mode, string logFolder, TimeSpan? timeout = null)
        {
            Core = core;
            Mode = mode;
            Queue = new EngineQueue(new DispatcherEngineThread(dispatcher), timeout ?? TimeSpan.FromSeconds(120));
            Log = new AgentLog(logFolder, mode);
            var tools = new ToolRegistry();
            StudioTools.Register(tools, this);
            Server = new McpServer(new ServerInfo
            {
                Name = "rebuzz",
                Title = "ReBuzz",
                Version = Version,
                Instructions =
                    "ReBuzz is a modular music studio (Buzz): generators and effects connected in a graph, patterns placed in an arrangement. " +
                    "Start every session with studio_status. Times are in beats from the song start; pitches are MIDI numbers (60 = C4); levels in dB. " +
                    "Every changing call is one undo step (undo reverts it). Errors say what to do next.",
            }, tools);
            Server.ToolCalled += Log.Write;
        }

        public ReBuzzCore Core { get; }
        /// <summary>"studio" or "headless".</summary>
        public string Mode { get; }
        public EngineQueue Queue { get; }
        public AgentLog Log { get; }
        public McpServer Server { get; }

        /// <summary>The last changing agent call ("Agent: song_set") and the undo stack's size right after it.</summary>
        internal string LastAgentUndo { get; private set; }
        internal int ActionsAfterLastAgentCall { get; private set; } = -1;

        /// <summary>
        /// Runs a call on the engine thread with dialogs captured: <paramref name="work"/> gets the
        /// messages ReBuzz raised so far (and can turn them into its error). Changing calls
        /// (<paramref name="changes"/>) are one undo group; a ToolError it throws carries the messages.
        /// </summary>
        public Task<T> Engine<T>(string what, bool changes, Func<IReadOnlyList<string>, T> work, CancellationToken ct) =>
            Queue.Run(what, () =>
            {
                using var messages = NonModal.Capture();
                var stack = Core.SongCore.ActionStack;
                int before = stack.Actions.Count();
                if (changes) stack.BeginActionGroup();
                try
                {
                    return work(messages.Messages);
                }
                catch (ToolError e) when (messages.Messages.Count > 0 && !e.Message.Contains("ReBuzz said:"))
                {
                    throw new ToolError(e.Message + " ReBuzz said: " + string.Join(" | ", messages.Messages));
                }
                finally
                {
                    if (changes)
                    {
                        stack.EndActionGroup();
                        int after = stack.Actions.Count();
                        if (after != before)
                        {
                            LastAgentUndo = "Agent: " + what;
                            ActionsAfterLastAgentCall = after;
                        }
                    }
                }
            }, ct);

        /// <summary>Serves stdio until it closes.</summary>
        public Task RunStdio(Stream input, Stream output, CancellationToken ct = default) =>
            new StdioTransport(Server, input, output).RunAsync(ct);

        /// <summary>Serves Streamable HTTP on 127.0.0.1.</summary>
        public HttpTransport StartHttp(int port, string token)
        {
            var t = new HttpTransport(Server, port, token);
            t.Start();
            http.Add(t);
            return t;
        }

        public void StopHttp()
        {
            foreach (var t in http) t.Dispose();
            http.Clear();
        }

        public void Dispose() => StopHttp();
    }
}
