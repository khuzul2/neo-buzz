// Agent server (docs/specs/mcp-server.md §3.1): headless mode. `ReBuzz.exe --agent [song]` starts
// the engine with no window and no audio device and serves MCP on stdin/stdout until stdin
// closes. Every message box is captured (stderr and the call's error); nothing else may write to
// stdout, which carries the protocol.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using BuzzGUI.Common;
using ReBuzz.AppViews;
using ReBuzz.Core;
using ReBuzz.FileOps;
using ReBuzz.MachineManagement;

namespace ReBuzz.Agent
{
    internal static class HeadlessAgent
    {
        public const string Switch = "--agent";

        public static bool Requested(string[] args) => args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

        sealed class NoFileChoice : IFileNameChoice
        {
            public ChosenValue<string> SelectFileName() => ChosenValue<string>.Nothing;
        }

        sealed class NoKeyboard : IKeyboard
        {
            public bool HasModifierKeyPressed(Enum modifierKey) => false;
        }

        sealed class Observer : IInitializationObserver
        {
            public void NotifyMachineManagerCreated(MachineManager machineManager) { }
        }

        /// <summary>Starts the engine and the stdio server; the app shuts down when stdin closes.</summary>
        public static void Start(Application app, string[] args)
        {
            var stdout = Console.OpenStandardOutput();
            var stdin = Console.OpenStandardInput();
            Console.SetOut(Console.Error);   // stdout carries the protocol only
            NonModal.CaptureAlways(m => Console.Error.WriteLine("[ReBuzz] " + m.Replace('\n', ' ')));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Step(string what) => Console.Error.WriteLine($"[ReBuzz] {clock.Elapsed.TotalSeconds:F1} s: {what}");
            Step("starting headless");

            var engineSettings = Global.EngineSettings;
            var dispatcher = new WindowsGuiDispatcher();
            var registry = new WindowsRegistry();
            var keyboard = new NoKeyboard();
            string buzzPath = Global.BuzzPath;

            BuzzGUIStartup.PreInit();
            var core = new ReBuzzCore(
                Global.GeneralSettings,
                engineSettings,
                buzzPath,
                Global.RegistryRoot,
                new MachineDLLScanner(dispatcher, engineSettings),
                dispatcher,
                registry,
                new NoFileChoice(),
                new NoFileChoice(),
                new UserMessagesViaMessageBox(),
                keyboard);
            var init = new ReBuzzCoreInitialization(core, buzzPath, dispatcher, registry, keyboard, engineSettings);
            Step("engine step 1");
            init.StartReBuzzEngineStep1((s, e) => { });
            // Native machines want a parent window: a message-only window, never shown.
            var hidden = new HwndSource(new HwndSourceParameters("ReBuzz agent") { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
            Step("engine step 2");
            init.StartReBuzzEngineStep2(hidden.Handle);
            Step("engine step 3 (gear scan)");
            init.StartReBuzzEngineStep3(engineSettings, new Observer());
            var machineDb = new MachineDatabase(core, buzzPath, dispatcher, engineSettings);
            Step("engine step 4 (machine database)");
            init.StartReBuzzEngineStep4(machineDb, s => { }, () => { }, () => { }, s => { }, c => { }, b => { }, t => { });
            Step("engine step 5");
            init.StartReBuzzEngineStep5(f => { });
            Step("engine step 6");
            init.StartReBuzzEngineStep6(openAudioDevice: false);
            core.SongCore.SongEnd = 16;
            core.SongCore.LoopEnd = 16;
            core.Modified = false;

            string song = args.SkipWhile(a => !string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault(a => !a.StartsWith("-"));
            if (song != null)
            {
                if (File.Exists(song)) core.OpenSongForAgent(Path.GetFullPath(song));
                else Console.Error.WriteLine($"[ReBuzz] No song at {song}; starting with an empty song.");
            }

            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReBuzz", "agent-logs");
            var host = new AgentHost(core, dispatcher, "headless", logs);
            Console.Error.WriteLine($"[ReBuzz] Headless agent server ready (MCP on stdio). Log: {host.Log.FilePath}");

            Task.Run(async () =>
            {
                try
                {
                    await host.RunStdio(stdin, stdout);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("[ReBuzz] stdio ended: " + e.Message);
                }
                app.Dispatcher.BeginInvoke(() =>
                {
                    try { init.ShutDownReBuzzEngine(); } catch (Exception) { }
                    hidden.Dispose();
                    app.Shutdown();
                });
            });
        }
    }
}
