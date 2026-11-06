// Fork (docs/specs/ui-gallery.md, the real Mac menu bar): what the macOS app menu's own items do that Avalonia's API doesn't
// offer (its commands for them are internal): Hide, Hide Others and Show All through NSApplication, as Avalonia's own items
// call them.

using System;
using System.Runtime.InteropServices;

namespace ReBuzz.Studio.Shell
{
    internal static class MacApp
    {
        const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC)]
        static extern IntPtr objc_getClass(string name);

        [DllImport(ObjC)]
        static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);

        static IntPtr Application => Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));

        static void Tell(string selector)
        {
            if (!OperatingSystem.IsMacOS()) return;
            try
            {
                Send(Application, sel_registerName(selector), IntPtr.Zero);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Hide NeoBuzz Live (⌘H).</summary>
        public static void Hide() => Tell("hide:");

        /// <summary>Hide Others (⌥⌘H).</summary>
        public static void HideOthers() => Tell("hideOtherApplications:");

        /// <summary>Show All.</summary>
        public static void ShowAll() => Tell("unhideAllApplications:");
    }
}
