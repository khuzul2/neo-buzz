// Agent server (docs/specs/mcp-server.md §3): ReBuzz's side. Builds the MCP server over a running
// ReBuzzCore: the engine queue on ReBuzz's dispatcher, one undo group per changing call, no
// dialogs, the agent log; then serves it on stdio (headless) or Streamable HTTP (studio).
// Tool implementations talk to the engine only (never to the UI), so they move with the engine
// when it is split out for macOS (docs/specs/cross-platform.md P1).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuzzGUI.Common;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Guides;
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
        public const string Version = "0.7 (A7)";

        readonly List<HttpTransport> http = new();

        public AgentHost(ReBuzzCore core, IUiDispatcher dispatcher, string mode, string logFolder, TimeSpan? timeout = null)
        {
            Core = core;
            Dispatcher = dispatcher;
            Mode = mode;
            Queue = new EngineQueue(new DispatcherEngineThread(dispatcher), timeout ?? TimeSpan.FromSeconds(120));
            Log = new AgentLog(logFolder, mode);
            var tools = new ToolRegistry();
            StudioTools.Register(tools, this);
            NotesTools.Register(tools, this);       // fork (machine-safety.md §3.5): Song Notes
            GuideLibrary.Register(tools, gear => Manual(core, gear), () => Described(core).Select(x => x.Dll.Name).ToList());
            MachineCatalog.Register(tools, () => Described(core).Select(x => x.Manifest).ToList());   // K0 (agent-knowledge.md): machine_find
            MachineTools.Register(tools, this);
            ReplaceTools.Register(tools, this);     // U5 (mcp-server.md §12.5): machine_replace
            ParamTools.Register(tools, this);
            PatternTools.Register(tools, this);
            RenderTools.Register(tools, this);
            AnalysisTools.Register(tools, this);
            MasteringTools.Register(tools, this);   // mastering-tools.md §6: target_build, eq_fit
            StateTools.Register(tools, this);
            CompositionTools.Register(tools, this);
            WaveTools.Register(tools, this);
            LiveTools.Register(tools, this);
            AppControl = new AppBackend(this, () => tools.All.Count);   // K3 (agent-knowledge.md §5): settings, MIDI, audio, view, export, app
            AppTools.Register(tools, AppControl);
            // Named after the app (Product: neobuzz-live, "NeoBuzz Live"), with the instructions an agent reads first.
            Server = new McpServer(GuideLibrary.AppServer(Version), tools, GuideLibrary.Prompts(), GuideLibrary.Resources().Concat(Manuals(core))
                .Concat(new[] { MachineCatalog.Resource(() => Described(core).Select(x => x.Manifest).ToList()), ToolReference.Resource(tools) }));
            Server.ToolCalled += Log.Write;
        }

        /// <summary>The installed gear with a manifest, by name.</summary>
        static IEnumerable<(BuzzGUI.Interfaces.IMachineDLL Dll, ReBuzz.Agent.Model.MachineManifest Manifest)> Described(ReBuzzCore core) =>
            core.MachineDLLs.Values.Select(d => (Dll: d, Manifest: SongModel.Manifest(d))).Where(x => x.Manifest != null);

        /// <summary>One manual resource per gear with a manifest (A5): rebuzz://machines/&lt;slug&gt;/manual.</summary>
        static IEnumerable<ResourceDef> Manuals(ReBuzzCore core) =>
            Described(core).Select(x => MachineManual.Resource(x.Manifest, () => StateTools.PresetNames(x.Dll))).ToList();

        /// <summary>guide(machine=…): the gear's full manual, or null for gear without a manifest.</summary>
        static string Manual(ReBuzzCore core, string gear)
        {
            var dll = core.MachineDLLs.Values.FirstOrDefault(d => string.Equals(d.Name, gear, StringComparison.OrdinalIgnoreCase))
                      ?? core.MachineDLLs.Values.FirstOrDefault(d => MachineManual.Slug(d.Name) == MachineManual.Slug(gear));
            var manifest = dll == null ? null : SongModel.Manifest(dll);
            return manifest == null ? null : MachineManual.Build(manifest, StateTools.PresetNames(dll));
        }

        public ReBuzzCore Core { get; }
        /// <summary>ReBuzz's dispatcher; work queued here runs after the engine's own deferred updates.</summary>
        public IUiDispatcher Dispatcher { get; }
        /// <summary>
        /// "studio" (ReBuzz open), "live" (rebuzz-headless playing through an audio device:
        /// mac-engine.md M5) or "headless" (renders only).
        /// </summary>
        public string Mode { get; }

        /// <summary>Whether an audio device plays, so the live tools work: studio or live mode.</summary>
        public bool Live => Mode is "studio" or "live";
        public EngineQueue Queue { get; }
        public AgentLog Log { get; }
        public McpServer Server { get; }

        /// <summary>The app-control tools' side of the host (K3): the studio adds its settings and its view control here.</summary>
        internal AppBackend AppControl { get; }

        /// <summary>The last changing agent call ("Agent: song_set") and the undo stack's size right after it.</summary>
        internal string LastAgentUndo { get; private set; }

        /// <summary>
        /// A failed call's steps were taken back and sit next on the redo stack (the stack can't
        /// drop them): redo refuses them until something else is done or undone.
        /// </summary>
        internal bool FailedCallOnRedo { get; set; }
        internal int UndosSinceFailedCall { get; set; }

        /// <summary>
        /// An automatic full snapshot before a big operation (UI thread, inside an Engine call), labelled auto
        /// (history-snapshots.md HS2). Never throws: a failed snapshot is logged and the operation goes on.
        /// </summary>
        internal void AutoSnapshot(string reason) => Core.Snapshots?.Auto(reason);

        /// <summary>This session's snapshots by name.</summary>
        internal ConcurrentDictionary<string, Snapshot> Snapshots { get; } = new();
        /// <summary>Phrases midi_play plays (A7).</summary>
        internal LivePlayer Player => player ??= new LivePlayer(Core);
        LivePlayer player;
        /// <summary>Tracks session_record armed to record a take, disarmed when it stops (machine names).</summary>
        internal HashSet<string> ArmedByAgent { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>This session's renders by id ("r1"…).</summary>
        internal ConcurrentDictionary<string, RenderInfo> Renders { get; } = new();
        /// <summary>The style targets target_build made in this session, by name (each also saved as a file).</summary>
        internal ConcurrentDictionary<string, ReBuzz.Agent.Audio.TargetHandle> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
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
                int steps = 0;
                void Count(IEnumerable<BuzzGUI.Interfaces.IAction> actions, bool done) { if (done) steps++; }
                bool failed = false;
                IDisposable source = null;
                if (changes)
                {
                    // history-snapshots.md HS1: the step is the agent's, named for the call.
                    source = stack.BeginSource(BuzzGUI.Common.HistorySource.Agent, "Agent: " + what);
                    stack.BeginActionGroup();
                    stack.PostDoOrUndoGroup += Count;
                }
                try
                {
                    return work(messages.Messages);
                }
                catch (ToolError e) when (messages.Messages.Count > 0 && !e.Message.Contains(Product.Name + " said:"))
                {
                    failed = true;
                    throw new ToolError(e.Message + " " + Product.Name + " said: " + string.Join(" | ", messages.Messages));
                }
                catch (Exception)
                {
                    failed = true;
                    throw;
                }
                finally
                {
                    if (changes)
                    {
                        stack.PostDoOrUndoGroup -= Count;
                        stack.EndActionGroup();
                        source?.Dispose();
                        // A call that fails changes nothing: take back the steps it made before failing.
                        if (failed && steps > 0)
                        {
                            try { stack.Undo(); } catch (Exception) { }
                            FailedCallOnRedo = true;
                            UndosSinceFailedCall = 0;
                        }
                        else if (!failed && steps > 0)
                        {
                            LastAgentUndo = "Agent: " + what;
                            ActionsAfterLastAgentCall = stack.Actions.Count();
                            FailedCallOnRedo = false;
                            // Not every engine change marks the song (parameter sets, connection gains, notes).
                            Core.SetModifiedFlag();
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

        public void Dispose()
        {
            player?.Dispose();
            StopHttp();
        }
    }
}
