using BuzzGUI.Interfaces;
using System.Diagnostics;

namespace BuzzGUI.BuzzUpdate
{
    public class UpdateService
    {
        /// <summary>
        /// Fork (NeoBuzz Live): off. Upstream's check polls ReBuzz's latest release on GitHub and offers upstream's
        /// installer, which would replace NeoBuzz Live; NeoBuzz Live's builds come from its own downloads. Nothing in
        /// the apps references this project any more (ReBuzz.csproj); off here too, should a merge bring a call back.
        /// To repurpose it, point it at NeoBuzz Live's releases first.
        /// </summary>
        public static bool Enabled => false;

        /// <summary>Where NeoBuzz Live's builds come from (the Mac app; the Windows package is built by the Fork package run).</summary>
        public const string Downloads = "https://github.com/khuzul2/neo-buzz/releases/tag/mac-latest";

        public static void CheckForUpdates(IBuzz buzz)
        {
            if (!Enabled)
            {
                buzz.DCWriteLine("[BuzzUpdate] Off: NeoBuzz Live's updates come from its downloads, " + Downloads);
                return;
            }
            var sw = new Stopwatch();
            sw.Start();
            UpdateWindow.DownloadBuildCount(buzz);
            sw.Stop();
            buzz.DCWriteLine(string.Format("CheckForUpdates {0} ms", sw.ElapsedMilliseconds));
        }


    }
}
