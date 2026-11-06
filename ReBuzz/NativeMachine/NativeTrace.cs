using Buzz.MachineInterface;
using BuzzGUI.Common;
using ReBuzz.Core;
using ReBuzz.MachineManagement;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.NativeMachine
{
    // FU1-DIAG (temporary, fix/followups-1): NEOBUZZ_NATIVE_TRACE=N logs the first N native work and tick calls of a
    // process to stderr, and each render's native machines as it starts.
    internal static class NativeTrace
    {
        static int remaining = int.TryParse(Environment.GetEnvironmentVariable("NEOBUZZ_NATIVE_TRACE"), out int n) ? n : 0;
        static int renders;
        internal static bool On => remaining > 0;

        static void Line(string s)
        {
            if (System.Threading.Interlocked.Decrement(ref remaining) < 0) return;
            Console.Error.WriteLine($"[trace {DateTime.Now:HH:mm:ss.fff} t{Environment.CurrentManagedThreadId}] {s}");
        }

        internal static void Work(MachineCore m, string kind, int channels, BuzzWorkMode mode, bool outputted, Sample[] samples, int n)
        {
            if (remaining <= 0) return;
            float peak = 0;
            for (int i = 0; i < n && samples != null && i < samples.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(samples[i].L), Math.Abs(samples[i].R)));
            Line($"{kind} {m.Name} ch {channels} mode {mode} n {n} -> {outputted} peak {peak:G4} pos {ReBuzzCore.masterInfo.PosInTick}/{ReBuzzCore.masterInfo.SamplesPerTick} playing {Global.Buzz?.Playing}");
        }

        internal static void Tick(MachineCore m, List<byte> globals, List<byte> tracks)
        {
            if (remaining <= 0) return;
            Line($"Tick {m.Name} globals [{string.Join(",", globals.Take(24))}] tracks {tracks.Count} [{string.Join(",", tracks.Take(24))}] spt {ReBuzzCore.masterInfo.SamplesPerTick}");
        }

        internal static void RenderStart(IEnumerable<MachineCore> machines)
        {
            int r = ++renders;
            if (Environment.GetEnvironmentVariable("NEOBUZZ_NATIVE_TRACE") == null) return;
            Console.Error.WriteLine($"[trace] render {r}: " + string.Join("; ", machines.Where(m => m.DLL != null && !m.DLL.IsManaged).Select(m =>
                $"{m.Name} ready {m.Ready} crashed {m.DLL.IsCrashed} loaded {m.MachineDLL?.IsLoaded} muted {m.IsMuted} diverged {m.IsDiverged} latency {m.Latency} stereo {m.HasStereoOutput} tracks {m.TrackCount}")));
        }
    }
}
