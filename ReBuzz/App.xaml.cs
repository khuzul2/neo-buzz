using System;
using System.Windows;
using System.Windows.Threading;

namespace ReBuzz
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // Fork (docs/specs/engine-split.md §4.3): before the first machine loads.
            ReBuzz.AppViews.WpfMachineApiHooks.Install();
            Dispatcher.CurrentDispatcher.UnhandledException += ExHandler;
            AppDomain.CurrentDomain.UnhandledException += ADExHandler;
            base.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        /// <summary>
        /// Fork (docs/specs/mcp-server.md §3.1): `--agent` runs headless (no window, MCP on stdio);
        /// otherwise the main window opens as usual.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            if (ReBuzz.Agent.HeadlessAgent.Requested(e.Args))
            {
                base.OnStartup(e);
                ReBuzz.Agent.HeadlessAgent.Start(this, e.Args);
                return;
            }
            // Fork (docs/specs/ui-gallery.md): --ui-shots draws the windows for the UI gallery; nobody answers
            // a message box there, so they go to stderr (the test that runs it reads it).
            if (ReBuzz.MainWindow.UiShotsRequested)
                ReBuzz.Agent.NonModal.CaptureAlways(m => Console.Error.WriteLine("[ReBuzz] " + m.Replace('\n', ' ')));
            // Fork: the main window is chosen here rather than in App.xaml, so --agent opens none
            // (StartupUri can't be cleared once set: WPF throws on null).
            StartupUri = new Uri("AppViews/MainWindow.xaml", UriKind.Relative);
            base.OnStartup(e);
        }

        public static void ExHandler(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ExitOrIgnoreDependingOnUserChoice(e.Exception);
            e.Handled = true;
        }

        public static void ADExHandler(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                ExitOrIgnoreDependingOnUserChoice(ex);
            }
        }

        private static void ExitOrIgnoreDependingOnUserChoice(Exception ex)
        {
            // Fork (agent server): headless, or inside an agent call, nobody can answer a dialog.
            if (ReBuzz.Agent.NonModal.TryCapture(GetFullExceptionMessage(ex), "Unhandled exception")) return;
            if (ReBuzz.Agent.HeadlessAgent.Requested(Environment.GetCommandLineArgs()))
            {
                // Headless and not serving yet: report on stderr and exit; a dialog would hang the client.
                Console.Error.WriteLine(Product.LogPrefix + "Unhandled exception: " + GetFullExceptionMessage(ex));
                Environment.Exit(1);
            }

            var result = MessageBox.Show(
                GetFullExceptionMessage(ex)
                + "Do you want the application to terminate? " +
                "Not terminating the application might leave an unresponsive process running in the background.",
                Product.Name + ": unhandled exception" + (ex.Source != null ? " (" + ex.Source + ")" : ""),
                MessageBoxButton.YesNo,
                MessageBoxImage.Error,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                Environment.Exit(1);
            }
        }

        private static string GetFullExceptionMessage(Exception ex)
        {
            var message = "";
            while (ex != null)
            {
                message += ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace + "\n\n";
                ex = ex.InnerException;
            }
            return message;
        }
    }
}
