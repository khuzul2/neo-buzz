using ReBuzz.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace ReBuzz.NativeMachine
{
    /// <summary>
    /// Fork: what a native machine's host process was doing when it stopped answering (a message it didn't answer
    /// within its time). Logged once per hang with the crash line: the host process (alive or its exit code), its
    /// threads (state, wait reason, CPU time), each channel's state in the shared memory (whose turn it is), and what
    /// this side's listeners were handling and for how long. NEOBUZZ_HANG_STACKS=1 also attaches the Windows
    /// debugger (cdb, from the Windows SDK, when it's there) to the host without stopping it and logs every thread's
    /// stack, and NEOBUZZ_HANG_DIR names a folder that gets each report as a file.
    /// </summary>
    internal static class NativeHangReport
    {
        static int reports;

        internal static string Describe(NativeMachineHost host, MachineCore machine, ChannelType channel, string message)
        {
            var s = new StringBuilder();
            s.Append($"{machine?.Name} ({machine?.DLL?.Name}): the host didn't answer {message} on the {channel}.");
            try
            {
                var p = host.childProcess;
                if (p == null) s.Append(" No host process.");
                else
                {
                    p.Refresh();
                    s.Append(p.HasExited ? $" Host {p.Id} exited (0x{p.ExitCode:X8})." : $" Host {p.Id} ({(host.Host64 ? "64" : "32")}-bit) is running, {p.Threads.Count} threads:");
                    if (!p.HasExited)
                        foreach (ProcessThread t in p.Threads)
                        {
                            string state = t.ThreadState == System.Diagnostics.ThreadState.Wait ? $"waiting ({t.WaitReason})" : t.ThreadState.ToString();
                            s.Append($" [{t.Id}: {state}, priority {t.CurrentPriority}, CPU {t.TotalProcessorTime.TotalMilliseconds:0} ms]");
                        }
                }
            }
            catch (Exception e)
            {
                s.Append($" (the host process: {e.GetType().Name}: {e.Message})");
            }
            s.Append(" Channels:");
            foreach (ChannelType c in Enum.GetValues(typeof(ChannelType)))
                s.Append($" {c} {host.DescribeChannel(c)};");
            return s.ToString();
        }

        /// <summary>The report, logged and written where NEOBUZZ_HANG_DIR says, with the host's stacks if asked for.</summary>
        internal static void Report(ReBuzzCore buzz, NativeMachineHost host, MachineCore machine, ChannelType channel, string message)
        {
            string text;
            try
            {
                text = Describe(host, machine, channel, message);
            }
            catch (Exception e)
            {
                text = $"{machine?.Name}: no hang report ({e.GetType().Name}: {e.Message})";
            }
            buzz?.DCWriteLine("Native host hang: " + text, BuzzGUI.Interfaces.DCLogLevel.Error);
            if (Environment.GetEnvironmentVariable("NEOBUZZ_HANG_STACKS") == "1")
                text += "\n" + Stacks(host);
            Console.Error.WriteLine("[NeoBuzz Live] native host hang: " + text);
            string dir = Environment.GetEnvironmentVariable("NEOBUZZ_HANG_DIR");
            if (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    int n = System.Threading.Interlocked.Increment(ref reports);
                    File.WriteAllText(Path.Combine(dir, $"native-hang-{Environment.ProcessId}-{n}.txt"), $"{DateTime.Now:O}\n{text}\n");
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>Every thread's stack in the host, from cdb attached without stopping it (-pv), or why there are none.</summary>
        static string Stacks(NativeMachineHost host)
        {
            try
            {
                var p = host.childProcess;
                if (p == null || p.HasExited) return "(no host process for stacks)";
                string kits = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Debuggers");
                string cdb = new[] { host.Host64 ? "x64" : "x86", "x64" }.Select(a => Path.Combine(kits, a, "cdb.exe")).FirstOrDefault(File.Exists);
                if (cdb == null) return $"(no cdb.exe under {kits})";
                var psi = new ProcessStartInfo(cdb, $"-pv -p {p.Id} -c \".symfix+ {Path.Combine(Path.GetTempPath(), "neobuzz-symbols")};.reload;~*kn 40;q\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var d = Process.Start(psi);
                var output = d.StandardOutput.ReadToEndAsync();
                if (!d.WaitForExit(90000))
                {
                    d.Kill();
                    return "(cdb didn't finish in 90 s)\n" + (output.IsCompleted ? output.Result : "");
                }
                return $"cdb ({cdb}):\n" + output.Result;
            }
            catch (Exception e)
            {
                return $"(no stacks: {e.GetType().Name}: {e.Message})";
            }
        }
    }
}
