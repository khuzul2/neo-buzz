// Fork (docs/specs/ux-overhaul.md P6c, §9 row 1): a native machine's own window inside a studio window.
//
// A native machine runs out of process (NativeMachineHost). Its double click is answered by the machine itself: it shows a window of
// its own, in the host process, owned by the studio's main window. The engine tells the UI host (IMachineUiHost.NativeGuiShown); this
// is what the studio does then, in pieces that need no Windows to test:
//
//  - NativeGuiPolicy picks the machine's window among the host process's top-level windows (the frontmost visible one that is not the
//    host's own technical window, not a window already taken, preferring a title that names the machine) and decides what to do;
//  - NativeGuiController runs it: embed the window in a studio window (Win32NativeGui, NativeControlHost as H0's spike did), try
//    twice, and when embedding fails leave the window floating as the machine put it; look again a few times when the machine has not
//    shown its window yet; raise the studio window when the machine's window is already embedded.
//
// Windows only: everywhere else the engine runs no native machines out of process, NativeGuiShown is never called, and a double click
// opens the manifest editor or the device page as before.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Studio.Platform
{
    /// <summary>A top-level window of a machine host process, as the platform lists it.</summary>
    internal readonly record struct ForeignWindow(IntPtr Handle, int ProcessId, string Title, string ClassName, bool Visible, int Width, int Height);

    /// <summary>What the controller needs of the platform: the windows of a process, whether one still exists, and the embedding.</summary>
    internal interface INativeGuiPlatform
    {
        /// <summary>The process's top-level windows, frontmost first.</summary>
        IReadOnlyList<ForeignWindow> TopLevelWindows(int processId);

        /// <summary>Whether the window exists (a machine may destroy it).</summary>
        bool Exists(IntPtr handle);

        /// <summary>
        /// Takes the window into a studio window of the machine and shows it; false when it did not work (the window is then as it was).
        /// </summary>
        bool Embed(string machineName, ForeignWindow window, object machine);

        /// <summary>Brings the studio window that holds this foreign window to the front.</summary>
        void Raise(IntPtr handle);

        /// <summary>Runs <paramref name="again"/> after <paramref name="delay"/>, on the UI thread.</summary>
        void Later(TimeSpan delay, Action again);
    }

    internal enum NativeGuiOutcome
    {
        /// <summary>No window of the machine's was found (yet).</summary>
        None,
        /// <summary>The machine's window is in a studio window.</summary>
        Embedded,
        /// <summary>Embedding did not work twice: the window stays as the machine put it, a window of its own.</summary>
        Floating,
    }

    internal static class NativeGuiPolicy
    {
        /// <summary>The window classes of the hosts' own technical windows (ReBuzzEngine.cpp), never a machine's.</summary>
        public static bool IsHostWindow(ForeignWindow w) => w.ClassName != null && w.ClassName.StartsWith("ReBuzzEngine", StringComparison.Ordinal);

        /// <summary>A window big enough to be a machine's GUI and not a tool tip or a hidden helper.</summary>
        public const int MinSize = 40;

        /// <summary>
        /// The machine's window among those of its host process: visible, not the host's own, not one already taken (embedded, or left
        /// floating for another machine), big enough. The windows come frontmost first, and the one a machine has just shown is the
        /// frontmost; a title that names the machine wins over the order (hosts share a process between machines).
        /// </summary>
        public static ForeignWindow? Pick(IEnumerable<ForeignWindow> windows, int processId, string machineName, string dllName, ISet<IntPtr> taken)
        {
            var candidates = windows
                .Where(w => w.ProcessId == processId && w.Visible && !IsHostWindow(w) && w.Width >= MinSize && w.Height >= MinSize && !taken.Contains(w.Handle))
                .ToList();
            if (candidates.Count == 0) return null;
            bool Names(ForeignWindow w, string name) => !string.IsNullOrEmpty(name) && w.Title != null && w.Title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
            return candidates.FirstMatch(w => Names(w, machineName))
                   ?? candidates.FirstMatch(w => Names(w, dllName))
                   ?? candidates[0];
        }

        // FirstOrDefault on a struct list gives a default struct, not null: the helper above wants a nullable result.
        static ForeignWindow? FirstMatch(this List<ForeignWindow> list, Func<ForeignWindow, bool> match)
        {
            foreach (var w in list) if (match(w)) return w;
            return null;
        }

        /// <summary>
        /// What to do with a window found: embed it when embedding is on and has not failed twice for it; otherwise it floats.
        /// </summary>
        public static NativeGuiOutcome Decide(bool embeddingOn, int failures) => embeddingOn && failures < MaxAttempts ? NativeGuiOutcome.Embedded : NativeGuiOutcome.Floating;

        /// <summary>Embedding is tried this often for a window before it is left floating (the task's "two real attempts").</summary>
        public const int MaxAttempts = 2;
    }

    /// <summary>One studio's native windows: which machine's window is in which studio window, and what failed.</summary>
    internal sealed class NativeGuiController
    {
        /// <summary>Where the machine's window is looked for again when it has not shown it yet.</summary>
        public static readonly TimeSpan[] Retries = { TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(800) };

        readonly INativeGuiPlatform platform;
        readonly Dictionary<object, IntPtr> embedded = new();
        readonly Dictionary<IntPtr, int> failures = new();
        readonly HashSet<IntPtr> floating = new();
        readonly HashSet<IntPtr> taken = new();

        public NativeGuiController(INativeGuiPlatform platform)
        {
            this.platform = platform;
        }

        /// <summary>Whether windows are embedded at all (the platform can, and nothing has switched it off).</summary>
        public bool EmbeddingOn { get; set; } = true;

        /// <summary>The window of this machine that is in a studio window now (for tests), or zero.</summary>
        public IntPtr EmbeddedWindowOf(object machine) => embedded.TryGetValue(machine, out var h) ? h : IntPtr.Zero;

        public bool IsFloating(IntPtr handle) => floating.Contains(handle);

        /// <summary>A studio window with a machine's window closed or the window gone: the machine's claim ends.</summary>
        public void Released(object machine)
        {
            if (embedded.TryGetValue(machine, out var h))
            {
                embedded.Remove(machine);
                taken.Remove(h);
            }
        }

        /// <summary>The machine answered its double click with a window of its own in <paramref name="processId"/>.</summary>
        public NativeGuiOutcome Shown(object machine, string machineName, string dllName, int processId, int retry = 0)
        {
            // Its window is in a studio window already, and still there: that one comes to the front.
            if (embedded.TryGetValue(machine, out var held))
            {
                if (platform.Exists(held))
                {
                    platform.Raise(held);
                    return NativeGuiOutcome.Embedded;
                }
                Released(machine);
            }

            var found = NativeGuiPolicy.Pick(platform.TopLevelWindows(processId), processId, machineName, dllName, taken);
            if (found == null)
            {
                // The machine may show its window a moment after it answered.
                if (retry < Retries.Length)
                    platform.Later(Retries[retry], () => Shown(machine, machineName, dllName, processId, retry + 1));
                return NativeGuiOutcome.None;
            }

            var window = found.Value;
            failures.TryGetValue(window.Handle, out int failed);
            if (NativeGuiPolicy.Decide(EmbeddingOn, failed) == NativeGuiOutcome.Embedded)
            {
                // Two real attempts: the first may meet a window that is still being built.
                while (failed < NativeGuiPolicy.MaxAttempts)
                {
                    bool ok;
                    try
                    {
                        ok = platform.Embed(machineName, window, machine);
                    }
                    catch (Exception)
                    {
                        ok = false;
                    }
                    if (ok)
                    {
                        embedded[machine] = window.Handle;
                        taken.Add(window.Handle);
                        failures.Remove(window.Handle);
                        return NativeGuiOutcome.Embedded;
                    }
                    failures[window.Handle] = ++failed;
                }
            }

            // Left as the machine put it: a floating window, which no other machine's lookup takes either.
            floating.Add(window.Handle);
            taken.Add(window.Handle);
            return NativeGuiOutcome.Floating;
        }
    }
}
