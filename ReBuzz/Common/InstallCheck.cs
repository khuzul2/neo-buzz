using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReBuzz.Common
{
    /// <summary>
    /// Fork: is the install whole? A package that is still being unzipped (or was unzipped in part, or half-synced by OneDrive)
    /// has the folders that extract first but not Themes, and the first window then dies with a DirectoryNotFoundException.
    /// The windowed app checks the files its start-up loads (themes, the native machine hosts, the gear list) before it opens
    /// anything, and says so in one message. The headless server (--agent) loads no theme and is not checked; the studio
    /// (Avalonia) has its own parameter windows and reads no theme, so it has no such dependency.
    /// </summary>
    internal static class InstallCheck
    {
        public const string Message = "NeoBuzz Live's files are incomplete. Unzip the whole package again, outside OneDrive if possible, and wait until it finishes.";

        /// <summary>What start-up loads, relative to the app's folder: Themes\Default (MainWindow.xaml, ToolBar.xaml, MachineView\, WavetableView\,
        /// the parameter windows and theme.xml: ReBuzzCore and MainWindow), what the window code reads at start. The native machine hosts (bin32, bin64) are not listed: a build output
        /// folder (the UI gallery's, CI's) has none, and a machine that needs one reports it by itself.</summary>
        public static readonly string[] Required =
        {
            @"Themes\Default\theme.xml",
            @"Themes\Default\MainWindow.xaml",
            @"Themes\Default\ToolBar.xaml",
            @"Themes\Default\ParameterWindow.xaml",
            @"Themes\Default\ParameterWindowShell.xaml",
            @"Themes\Default\MachineView\MachineView.xaml",
            @"Themes\Default\MachineView\MVResources.xaml",
            @"Themes\Default\WavetableView\WavetableView.xaml",
        };

        /// <summary>The required files that are not under <paramref name="root"/> (empty: the install is whole).</summary>
        public static List<string> Missing(string root, IEnumerable<string> required = null)
        {
            return (required ?? Required).Where(r => !File.Exists(Path.Combine(root, r.Replace('\\', Path.DirectorySeparatorChar)))).ToList();
        }
    }
}
