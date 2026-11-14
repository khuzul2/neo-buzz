// Fork (test hygiene): where the tests put their files. Everything lives under one folder per test process, deleted when the process ends
// (also after a failed test whose TearDown Avalonia.Headless skipped, and after the test host's normal exit), and a test that wants its
// folder gone sooner takes a TempDir and disposes it. Linked into ReBuzz.Studio.Tests, ReBuzz.AgentTests and ReBuzzTests.

using System;
using System.IO;
using System.Threading;

namespace ReBuzzTests.Automation
{
    internal static class TestTemp
    {
        static readonly object Gate = new();
        static string root;

        /// <summary>This process's folder: rebuzz-t-&lt;pid&gt;-&lt;random&gt; in the temp folder.</summary>
        public static string Root
        {
            get
            {
                lock (Gate)
                {
                    if (root == null)
                    {
                        root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rebuzz-t-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                        Directory.CreateDirectory(root);
                        SweepDead();
                        AppDomain.CurrentDomain.ProcessExit += (_, _) => Sweep();
                    }
                    return root;
                }
            }
        }

        /// <summary>A path under <see cref="Root"/> (nothing is created); a drop-in for Path.Combine(Path.GetTempPath(), …).</summary>
        public static string Path(params string[] parts)
        {
            var all = new string[parts.Length + 1];
            all[0] = Root;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return System.IO.Path.Combine(all);
        }

        /// <summary>A new, empty folder of its own under <see cref="Root"/>, deleted by Dispose (or at process end).</summary>
        public static TempDir Dir(string what) => new(Path(what + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)));

        /// <summary>Deletes a file or a folder tree; a file another process holds for a moment is retried, then left to the sweep at process end.</summary>
        public static void Delete(string path)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    else if (File.Exists(path)) File.Delete(path);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(25 * (attempt + 1)); }
            }
        }

        /// <summary>Folders of earlier test processes that were killed before they could clean up (their process is gone).</summary>
        static void SweepDead()
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(System.IO.Path.GetTempPath(), "rebuzz-t-*"))
                {
                    var parts = System.IO.Path.GetFileName(dir).Split('-');
                    if (parts.Length != 4 || !int.TryParse(parts[2], out int pid) || pid == Environment.ProcessId) continue;
                    bool alive;
                    try { using var p = System.Diagnostics.Process.GetProcessById(pid); alive = !p.HasExited; }
                    catch (ArgumentException) { alive = false; }
                    catch (Exception) { alive = true; }
                    if (!alive) Delete(dir);
                }
            }
            catch (Exception) { }
        }

        static void Sweep()
        {
            string r;
            lock (Gate) { r = root; root = null; }
           
            if (r == null) return;
            // A song still saving its history or a window closing can put files back a moment after the first pass: until it stays gone.
            for (int pass = 0; pass < 10 && Directory.Exists(r); pass++)
            {
                Delete(r);
                if (Directory.Exists(r)) Thread.Sleep(100);
            }
        }
    }

    /// <summary>A folder of one test; <c>using var dir = TestTemp.Dir("x");</c>, then dir.Path.</summary>
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public override string ToString() => Path;

        public static implicit operator string(TempDir d) => d.Path;

        public void Dispose() => TestTemp.Delete(Path);
    }
}
