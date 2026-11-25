using BuzzGUI.Interfaces;

namespace BuzzGUI.Common
{
    public static class Global
    {
        public static IBuzz Buzz { get; set; }
        public static string RegistryRoot { get { return "Software\\ReBuzz\\"; } }

        public static string BuzzPath
        {
            get
            {
                // Fork (package layout): the studio's program files sit in App\ beside the exe's Gear\, bin32\ and bin64\ (build.ps1,
                // the switch); BuzzPath is then the exe's folder, the parent of App\.
                string folder = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string parent = folder == null ? null : System.IO.Path.GetDirectoryName(folder);
                if (parent != null && System.IO.Path.GetFileName(folder) == "App" && System.IO.Directory.Exists(System.IO.Path.Combine(parent, "Gear")))
                    return parent;
                return folder;
            }
        }

        public static Settings.EngineSettings EngineSettings = new Settings.EngineSettings();
        public static Settings.GeneralSettings GeneralSettings = new Settings.GeneralSettings();
        public static Settings.MIDISettings MIDISettings = new Settings.MIDISettings();

    }
}
