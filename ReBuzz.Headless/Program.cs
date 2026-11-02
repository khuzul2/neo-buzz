// Fork (docs/specs/mac-engine.md M1): rebuzz-headless. Starts the engine as ReBuzz.exe's
// `--agent` does (ReBuzz/Agent/HeadlessAgent.cs), with the seams for hosts without Windows or a UI
// toolkit (ReBuzz/Platform): the JSON registry, the loop dispatcher on the main thread, messages
// on stderr. Then serves MCP on stdio until stdin closes. stdout carries the protocol only.
//
//   rebuzz-headless --agent [song.bmxml] [--settings <folder>]

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.AppViews;
using ReBuzz.Core;
using ReBuzz.FileOps;
using ReBuzz.MachineManagement;
using ReBuzz.Platform;

namespace ReBuzz.Headless
{
    internal static class Program
    {
        const string Usage =
            "rebuzz-headless: ReBuzz's engine with its MCP server on stdio (docs/specs/mcp-server.md).\n" +
            "  rebuzz-headless --agent [song.bmxml] [--settings <folder>]\n" +
            "Point an MCP client at it, e.g. claude mcp add rebuzz -- rebuzz-headless --agent";

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

        static int Main(string[] args)
        {
            if (!args.Any(a => string.Equals(a, "--agent", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine(Usage);
                return args.Any(a => a is "-h" or "--help") ? 0 : 2;
            }
            string settings = Option(args, "--settings") ?? JsonRegistry.SettingsFolder;
            // The song: the first argument that is neither an option nor an option's value.
            string song = null;
            for (int i = 0; i < args.Length && song == null; i++)
            {
                if (string.Equals(args[i], "--settings", StringComparison.OrdinalIgnoreCase)) i++;
                else if (!args[i].StartsWith("-")) song = args[i];
            }

            // Before anything touches Global: its settings load from the JSON registry, not the Windows one.
            Directory.CreateDirectory(settings);
            var registry = new JsonRegistry(Path.Combine(settings, "registry.json"), @"Software\ReBuzz");
            RegistryEx.Store = registry;

            var stdout = Console.OpenStandardOutput();
            var stdin = Console.OpenStandardInput();
            Console.SetOut(Console.Error);                  // stdout carries the protocol only
            NonModal.CaptureAlways(m => Console.Error.WriteLine("[ReBuzz] " + m.Replace('\n', ' ')));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Step(string what) => Console.Error.WriteLine($"[ReBuzz] {clock.Elapsed.TotalSeconds:F1} s: {what}");

            using var dispatcher = new LoopDispatcher();
            int exit = 0;
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    Start(dispatcher, registry, settings, song, stdin, stdout, Step);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[ReBuzz] start-up failed: {e}");
                    exit = 1;
                    dispatcher.Stop();
                }
            });
            dispatcher.Run();
            return exit;
        }

        static string Option(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>
        /// What BuzzGUIStartup.PreInit does for ReBuzz.exe, minus its settings window: unobserved
        /// task exceptions are logged, and machines' dependencies resolve from the gear folders
        /// (with this OS's path separators).
        /// </summary>
        static void PreInit(string buzzPath)
        {
            TaskScheduler.UnobservedTaskException += (sender, e) =>
            {
                Console.Error.WriteLine("[ReBuzz] unobserved task exception: " + e.Exception.Message);
                e.SetObserved();
            };
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                if (e.Name.Contains(".XmlSerializers")) return null;
                string name = new System.Reflection.AssemblyName(e.Name).Name;
                foreach (var folder in new[] { Path.Combine("Gear", "Generators"), Path.Combine("Gear", "Effects") })
                {
                    string path = Path.Combine(buzzPath, folder, name + ".dll");
                    if (File.Exists(path)) return System.Reflection.Assembly.LoadFile(path);
                }
                return null;
            };
        }

        static void Start(LoopDispatcher dispatcher, JsonRegistry registry, string settings, string song, Stream stdin, Stream stdout, Action<string> step)
        {
            step($"starting headless on {Environment.OSVersion.Platform} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}); settings in {settings}");
            var engineSettings = Global.EngineSettings;
            var keyboard = new NoKeyboard();
            string buzzPath = Global.BuzzPath;

            PreInit(buzzPath);
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
                new ConsoleUserMessages(),
                keyboard);
            var init = new ReBuzzCoreInitialization(core, buzzPath, dispatcher, registry, keyboard, engineSettings);
            step("engine step 1");
            init.StartReBuzzEngineStep1((s, e) => { });
            step("engine step 2");
            init.StartReBuzzEngineStep2(IntPtr.Zero);       // no window: native machines have no parent for their GUIs
            step("engine step 3 (gear scan)");
            init.StartReBuzzEngineStep3(engineSettings, new Observer());
            var machineDb = new MachineDatabase(core, buzzPath, dispatcher, engineSettings);
            step("engine step 4 (machine database)");
            init.StartReBuzzEngineStep4(machineDb, s => { }, () => { }, () => { }, s => { }, c => { }, b => { }, t => { });
            step("engine step 5");
            init.StartReBuzzEngineStep5(f => { });
            step("engine step 6");
            init.StartReBuzzEngineStep6(openAudioDevice: false);
            core.SongCore.SongEnd = 16;
            core.SongCore.LoopEnd = 16;
            core.Modified = false;

            if (song != null)
            {
                if (File.Exists(song)) core.OpenSongForAgent(Path.GetFullPath(song));
                else Console.Error.WriteLine($"[ReBuzz] No song at {song}; starting with an empty song.");
            }

            string logs = Path.Combine(settings, "agent-logs");
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
                dispatcher.BeginInvoke(() =>
                {
                    try { init.ShutDownReBuzzEngine(); } catch (Exception) { }
                    host.Dispose();
                    dispatcher.Stop();
                });
            });
        }
    }
}
