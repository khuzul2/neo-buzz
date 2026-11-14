// Fork (docs/specs/history-snapshots.md HS2): full-song snapshots. A snapshot is the song's own file sections
// (every machine's saved state, patterns, sequences, wires, parameters, waves, the Session View layout) cut
// into content-defined chunks in <song>.history/ plus a small JSON manifest.
//
// Live safety: capture builds the sections on the UI thread (the work a save does before it touches the
// disk, no file or audio-thread access) and hands them to a background queue that hashes, deflates and
// writes. Nothing here runs on the audio thread, takes AudioLock, or allocates there. Restore replaces the
// song as opening a file does, so it is allowed only while the transport is stopped (HS3: live restore).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core.SongHistory
{
    internal sealed class SnapshotManifest
    {
        public sealed class ChunkRef
        {
            public string Hash { get; set; }
            public int Length { get; set; }
        }

        public sealed class SectionRef
        {
            public uint Magic { get; set; }
            public List<ChunkRef> Chunks { get; set; } = new();
        }

        public int Version { get; set; } = 1;
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime Time { get; set; }
        /// <summary>"manual" or "auto".</summary>
        public string Trigger { get; set; } = "manual";
        /// <summary>For an auto snapshot, why (save, timer, before replace…).</summary>
        public string Reason { get; set; }
        public string Song { get; set; }
        public long Bytes { get; set; }
        public int Machines { get; set; }
        public List<SectionRef> Sections { get; set; } = new();
        public SongSummary Summary { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool Pending { get; set; }
        /// <summary>The live-restore data while the snapshot is still being stored (afterwards it is read back from the store).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal LiveSong LiveMemory { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsAuto => Trigger == "auto";
    }

    internal sealed class SongSnapshots
    {
        /// <summary>Auto snapshots kept; the oldest drop first. Manual ones are never dropped.</summary>
        public const int MaxAuto = 40;
        /// <summary>An agent history_jump across more steps than this snapshots first.</summary>
        public const int BigJumpSteps = 20;

        static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        /// <summary>Where an untitled song's snapshots go until it is saved (tests point it elsewhere).</summary>
        internal static string UntitledRoot { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "NeoBuzz", "History");

        readonly ReBuzzCore core;
        readonly object gate = new();
        readonly List<SnapshotManifest> items = new();
        Task queue = Task.CompletedTask;
        ChunkStore store;
        string folder;
        bool untitled = true;
        volatile bool changed;
        DateTime lastAuto = DateTime.UtcNow;
        int counter;
        byte[] pendingEmbedded;
        public string LastError { get; private set; }

        /// <summary>The list changed (a capture queued or stored, a delete, a rename, another song). Any thread.</summary>
        public event Action Changed;

        internal SongSnapshots(ReBuzzCore core)
        {
            this.core = core;
        }

        public string Folder => folder;
        public bool IsUntitled => untitled;

        /// <summary>Whether a save writes the history into the song file too (engine setting).</summary>
        public bool Embed
        {
            get => core.EngineSettings?.EmbedHistoryInSong ?? false;
            set { if (core.EngineSettings != null) core.EngineSettings.EmbedHistoryInSong = value; }
        }

        public int IntervalMinutes => core.EngineSettings?.SnapshotIntervalMinutes ?? 5;

        /// <summary>An edit marked the song modified: the next timer snapshot has something to keep.</summary>
        public void NoteChanged() => changed = true;

        // ---- Folder ----

        static string FolderFor(string songName) => songName + ".history";

        /// <summary>The song changed (opened, new, first saved): look at its folder. songName null: an untitled song.</summary>
        public void Bind(string songName)
        {
            Flush();
            core.ActiveRestore = null;                  // HS3: a restore waiting for the old song has nothing to apply to
            active = null; deferred = null; deferredReason = null;
            finisher?.Stop();
            lock (gate) pinned.Clear();
            lock (gate)
            {
                items.Clear();
                untitled = string.IsNullOrEmpty(songName);
                folder = untitled ? Path.Combine(UntitledRoot, "untitled-" + Guid.NewGuid().ToString("N")) : FolderFor(songName);
                store = new ChunkStore(folder);
                changed = false;
                lastAuto = DateTime.UtcNow;
            }
            if (!untitled) Load();
            if (!untitled && pendingEmbedded != null)
            {
                bool empty;
                lock (gate) empty = items.Count == 0;
                if (empty) ReadEmbedded(pendingEmbedded);
            }
            pendingEmbedded = null;
            Changed?.Invoke();
        }

        void Load()
        {
            string dir = Path.Combine(folder, "manifests");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(f));
                    if (m?.Id != null) lock (gate) items.Add(m);
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { LastError = "Couldn't read " + Path.GetFileName(f) + ": " + e.Message; }
            }
        }

        /// <summary>The song got a file name (first save, or Save As): an untitled song's folder moves there; a named one's is copied.</summary>
        public void Relocate(string newSongName)
        {
            if (string.IsNullOrEmpty(newSongName)) return;
            string target = FolderFor(newSongName);
            if (string.Equals(Path.GetFullPath(target), folder == null ? null : Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) return;
            Flush();
            string old = folder;
            try
            {
                if (old != null && Directory.Exists(old) && !Directory.Exists(target))
                {
                    if (untitled)
                    {
                        try { Directory.Move(old, target); }
                        catch (IOException) { CopyFolder(old, target); TryDelete(old); }
                    }
                    else CopyFolder(old, target);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LastError = "Couldn't move the history folder: " + e.Message;
            }
            Bind(newSongName);
        }

        static void CopyFolder(string from, string to)
        {
            foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
        }

        static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        // ---- Queue ----

        void Enqueue(Action work)
        {
            lock (gate)
                queue = queue.ContinueWith(_ =>
                {
                    try { work(); }
                    catch (Exception e) { LastError = e.Message; }
                }, TaskScheduler.Default);
        }

        /// <summary>Waits until everything queued is stored.</summary>
        public void Flush()
        {
            while (true)
            {
                Task t;
                lock (gate) t = queue;
                t.Wait();
                lock (gate) if (ReferenceEquals(t, queue)) return;
            }
        }

        // ---- List ----

        public IReadOnlyList<SnapshotManifest> List()
        {
            lock (gate) return items.OrderBy(m => m.Time).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        }

        public SnapshotManifest Find(string idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return null;
            lock (gate)
            {
                var m = items.FirstOrDefault(x => x.Id == idOrName);
                m ??= items.Where(x => x.Name == idOrName).OrderByDescending(x => x.Time).FirstOrDefault();
                m ??= items.Where(x => string.Equals(x.Name, idOrName, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Time).FirstOrDefault();
                return m;
            }
        }

        // ---- Capture ----

        /// <summary>
        /// UI thread. Copies the song (its file sections and a summary) and queues the storing. Returns the
        /// snapshot (pending until stored; <see cref="Flush"/> waits) or null when the song is loading.
        /// </summary>
        public SnapshotManifest Capture(string name, string trigger = "manual", string reason = null)
        {
            if (core.SongLoading) return null;
            if (store == null) Bind(core.SongCore.SongName);
            var sections = core.CaptureSongSections();
            var summary = SongSummary.Take(core);
            var live = LiveSong.Take(core);                      // HS3: what a live restore needs (machine data, events, wires, tracks)
            sections.Add((LiveSong.Magic, live.Write()));
            var now = DateTime.UtcNow;
            var m = new SnapshotManifest
            {
                Id = now.ToString("yyyyMMddHHmmssfff") + "-" + Interlocked.Increment(ref counter).ToString("x2"),
                Name = string.IsNullOrWhiteSpace(name) ? "Snapshot " + now.ToLocalTime().ToString("HH:mm:ss") : name.Trim(),
                Time = now,
                Trigger = trigger,
                Reason = reason,
                Song = core.SongCore.SongName,
                Machines = summary.Machines.Count,
                Summary = summary,
                Bytes = sections.Sum(s => (long)s.Data.Length),
                Pending = true,
                LiveMemory = live,
            };
            var st = store;
            lock (gate) items.Add(m);
            changed = false;
            Enqueue(() => Write(st, m, sections));
            Changed?.Invoke();
            if (m.IsAuto) PruneAuto();
            return m;
        }

        void Write(ChunkStore st, SnapshotManifest m, List<(uint Magic, byte[] Data)> sections)
        {
            try
            {
                var refs = new List<SnapshotManifest.SectionRef>();
                foreach (var (magic, data) in sections)
                {
                    var sr = new SnapshotManifest.SectionRef { Magic = magic };
                    foreach (var (off, len) in ChunkStore.Split(data))
                    {
                        string hash = ChunkStore.HashOf(new ReadOnlySpan<byte>(data, off, len));
                        st.Put(hash, data, off, len);
                        sr.Chunks.Add(new SnapshotManifest.ChunkRef { Hash = hash, Length = len });
                    }
                    refs.Add(sr);
                }
                m.Sections = refs;
                WriteManifest(st, m);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LastError = "Couldn't store the snapshot \"" + m.Name + "\": " + e.Message;
                lock (gate) items.Remove(m);
            }
            finally
            {
                m.Pending = false;
                m.LiveMemory = null;
                if (m.IsAuto) PruneAuto();
                Changed?.Invoke();
            }
        }

        static void WriteManifest(ChunkStore st, SnapshotManifest m)
        {
            string dir = Path.Combine(st.Root, "manifests");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, m.Id + ".json");
            File.WriteAllText(path + ".part", JsonSerializer.Serialize(m, Json));
            File.Move(path + ".part", path, overwrite: true);
        }

        /// <summary>UI thread: an automatic snapshot before something big, labelled auto.</summary>
        public SnapshotManifest Auto(string reason)
        {
            try { return Capture("Auto: " + reason, "auto", reason); }
            catch (Exception e)
            {
                LastError = "Auto snapshot (" + reason + ") failed: " + e.Message;
                return null;
            }
        }

        /// <summary>
        /// UI thread, every minute or so with the autosave: a snapshot every <see cref="IntervalMinutes"/> minutes of editing, when
        /// the song changed since the last one. Not while the transport runs (a capture takes UI time). Returns whether it took one.
        /// </summary>
        public bool Tick(DateTime utcNow)
        {
            int minutes = IntervalMinutes;
            if (minutes <= 0 || !changed || core.PlayRequested || core.SongLoading) return false;
            if (utcNow - lastAuto < TimeSpan.FromMinutes(minutes)) return false;
            lastAuto = utcNow;
            return Auto("every " + minutes + " min") != null;
        }

        void PruneAuto()
        {
            List<SnapshotManifest> drop;
            lock (gate) drop = items.Where(x => x.IsAuto && !pinned.Contains(x.Id)).OrderByDescending(x => x.Time).ThenByDescending(x => x.Id, StringComparer.Ordinal).Skip(MaxAuto).Where(x => !x.Pending).ToList();
            foreach (var d in drop) Delete(d);
        }

        // ---- Delete, rename ----

        public bool Delete(string idOrName)
        {
            var m = Find(idOrName);
            return m != null && Delete(m);
        }

        bool Delete(SnapshotManifest m)
        {
            lock (gate) if (!items.Remove(m)) return false;
            var st = store;
            Enqueue(() =>
            {
                try { File.Delete(Path.Combine(st.Root, "manifests", m.Id + ".json")); } catch (IOException) { }
                CollectGarbage(st);
            });
            Changed?.Invoke();
            return true;
        }

        /// <summary>Chunks no snapshot holds any more are removed (on the queue).</summary>
        void CollectGarbage(ChunkStore st)
        {
            HashSet<string> keep;
            lock (gate) keep = items.SelectMany(i => i.Sections).SelectMany(s => s.Chunks).Select(c => c.Hash).ToHashSet();
            foreach (var h in st.All().ToList())
                if (!keep.Contains(h)) st.Delete(h);
        }

        public bool Rename(string idOrName, string newName)
        {
            var m = Find(idOrName);
            if (m == null || string.IsNullOrWhiteSpace(newName)) return false;
            m.Name = newName.Trim();
            var st = store;
            Enqueue(() => { if (!m.Pending && m.Sections.Count > 0) WriteManifest(st, m); });
            Changed?.Invoke();
            return true;
        }

        // ---- Diff ----

        /// <summary>What changed from <paramref name="a"/> to <paramref name="b"/> (null: the song now; UI thread then).</summary>
        public List<SongSummary.Change> Diff(SnapshotManifest a, SnapshotManifest b)
        {
            var sb = b?.Summary ?? SongSummary.Take(core);
            return SongSummary.Diff(a.Summary, sb, n => core.SongCore.MachinesList.FirstOrDefault(m => m.Name == n));
        }

        // ---- Restore ----

        /// <summary>The song file's bytes a snapshot holds.</summary>
        public byte[] Assemble(SnapshotManifest m)
        {
            Flush();
            var st = store;
            var parts = new List<(uint, byte[])>();
            foreach (var s in m.Sections)
            {
                if (s.Magic == LiveSong.Magic) continue;       // the live-restore section isn't part of a song file
                var ms = new MemoryStream();
                foreach (var c in s.Chunks)
                {
                    var bytes = st.Get(c.Hash);
                    ms.Write(bytes, 0, bytes.Length);
                }
                parts.Add((s.Magic, ms.ToArray()));
            }
            return ReBuzz.FileOps.BMXFile.Assemble(parts);
        }

        /// <summary>
        /// UI thread. Puts the song back as a snapshot had it, as one step of the song's history (undo returns to the song as it was).
        /// Transport stopped: the song is replaced as opening a file does (its history stays). Playing: applied live at the next
        /// <paramref name="quantize"/> boundary (default a bar) without a click (<see cref="LiveRestorePlan"/>), or, when a part can't be
        /// prepared safely while playing, deferred until the transport stops. Throws <see cref="InvalidOperationException"/> with the reason when it can't.
        /// </summary>
        public RestoreResult Restore(SnapshotManifest m, RestoreQuantize quantize = RestoreQuantize.Auto) => RestoreCore(m, quantize, null);

        /// <summary>A restore the history asks for (undo or redo of a restore step): no new step, no new auto snapshot.</summary>
        RestoreResult RestoreFromHistory(SnapshotManifest m) => RestoreCore(m, RestoreQuantize.Auto, new RestoreOrigin());

        sealed class RestoreOrigin { }

        public const string Unsupported = "This snapshot was taken before live restore existed, so it can only be restored while the transport is stopped.";

        RestoreResult RestoreCore(SnapshotManifest m, RestoreQuantize quantize, RestoreOrigin history)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            if (m.Pending) Flush();
            CancelPending();
            if (!core.PlayRequested)
                return RestoreStopped(m, history, null);
            return RestoreLive(m, quantize, history);
        }

        string RestoreName(SnapshotManifest m) => "Restore \"" + m.Name + "\"";

        RestoreResult RestoreStopped(SnapshotManifest m, RestoreOrigin history, string waited)
        {
            byte[] bytes;
            try { bytes = Assemble(m); }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                throw new InvalidOperationException("Snapshot \"" + m.Name + "\" can't be read: " + e.Message);
            }
            var before = history == null ? Auto("before restoring " + m.Name) : null;
            // The song's history is kept across the load (a load starts a new one): the restore is one step on it. The load's own edits
            // (it centres the master first) go on a stack of their own, which is dropped.
            var stack = core.SongCore.ActionStack;
            bool ok;
            try
            {
                core.SongCore.ActionStack = HistorySettings.NewStack();
                ok = core.RestoreSongBytes(bytes, core.SongCore.SongName);
            }
            finally { core.SongCore.ActionStack = stack; }
            if (!ok)
                throw new InvalidOperationException("The song couldn't be restored from \"" + m.Name + "\". It is unchanged or partly loaded; the auto snapshot \"before restoring\" holds it.");
            changed = true;
            if (history == null) RecordStep(stack, m, before, waited);
            var done = new RestoreResult { Mode = "applied", Snapshot = m };
            if (waited != null) done.Notes.Add("It waited for the transport to stop: " + waited);
            return done;
        }

        void RecordStep(ManagedActionStack stack, SnapshotManifest target, SnapshotManifest before, string waited)
        {
            if (before == null) return;                      // no way back was kept: the auto snapshot failed
            stack.Record(new RestoreAction(this, target.Id, before.Id, RestoreName(target) + (waited != null ? " (waited for stop)" : "")));
            lock (gate) { pinned.Add(target.Id); pinned.Add(before.Id); }
        }

        /// <summary>Snapshots a restore step of the history needs kept (an auto one is never pruned while a step holds it).</summary>
        readonly HashSet<string> pinned = new();

        // ---- Live restore ----

        LiveRestorePlan active;
        IUiTimer finisher;
        SnapshotManifest deferred;

        /// <summary>The restore waiting for the transport to stop, if any (name and reason), for the UI line.</summary>
        public (string Name, string Reason)? Deferred { get { var d = deferred; return d == null ? null : (d.Name, deferredReason); } }
        string deferredReason;

        /// <summary>Whether a live restore is waiting for its boundary or finishing.</summary>
        public bool LiveActive => active != null;

        /// <summary>Raised (any thread) when a live restore was applied and finished, or a deferred one ran.</summary>
        public event Action RestoreFinished;

        LiveSong LoadLive(SnapshotManifest m)
        {
            if (m.LiveMemory != null) return m.LiveMemory;
            var sec = m.Sections.FirstOrDefault(s => s.Magic == LiveSong.Magic);
            if (sec == null) return null;
            try
            {
                var ms = new MemoryStream();
                foreach (var c in sec.Chunks)
                {
                    var bytes = store.Get(c.Hash);
                    ms.Write(bytes, 0, bytes.Length);
                }
                return LiveSong.Read(ms.ToArray());
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { return null; }
        }

        RestoreResult RestoreLive(SnapshotManifest m, RestoreQuantize quantize, RestoreOrigin history)
        {
            var target = LoadLive(m);
            if (target == null)
                return Defer(m, Unsupported, history);
            var before = history == null ? Auto("before restoring " + m.Name) : null;
            var outcome = LiveRestorePlanner.Build(core, m, target, m.Summary, before?.LiveMemory);
            if (outcome.DeferReason != null)
                return Defer(m, outcome.DeferReason, history, before);
            var plan = outcome.Plan;
            var q = quantize switch
            {
                RestoreQuantize.Now => BuzzGUI.Interfaces.LaunchQuantization.None,
                RestoreQuantize.Beat => BuzzGUI.Interfaces.LaunchQuantization.Beat,
                _ => BuzzGUI.Interfaces.LaunchQuantization.Bar,
            };
            string qName = quantize switch { RestoreQuantize.Now => "tick", RestoreQuantize.Beat => "beat", _ => "bar" };
            if (plan.IsEmpty)
            {
                core.ActiveRestore = null;
                if (history == null && before != null) RecordStep(core.SongCore.ActionStack, m, before, null);
                return new RestoreResult { Mode = "live", Waiting = false, Quantize = qName, Snapshot = m, Notes = { "Nothing differs from the song as it is." } };
            }
            plan.At = core.SessionCore.PlanRestoreBoundary(q);
            if (plan.At < 0)
            {
                LiveRestorePlanner.Discard(core, plan, plan.AllNewMachines());
                return Defer(m, "the transport hasn't started its first tick", history, before);
            }
            active = plan;
            core.ActiveRestore = plan;
            finisher ??= core.dispatcher.CreateTimer(TimeSpan.FromMilliseconds(25), () => Pump());
            finisher.Start();
            if (history == null) RecordStep(core.SongCore.ActionStack, m, before, null);
            var result = new RestoreResult { Mode = "live", Waiting = true, Quantize = qName, Snapshot = m, WaitMs = WaitMs(plan.At) };
            result.Notes.Add(plan.Summary);
            result.Notes.Add("The Session View's clip grid and settings are not part of a live restore.");
            Changed?.Invoke();
            return result;
        }

        double WaitMs(long at)
        {
            long clock = core.SessionCore.Clock;
            int rate = ReBuzzCore.masterInfo.SamplesPerSec;
            if (clock < 0 || rate <= 0) return 0;
            return Math.Max(0, (at - clock) * (double)Math.Max(1, ReBuzzCore.masterInfo.SamplesPerTick) * 1000.0 / rate);
        }

        RestoreResult Defer(SnapshotManifest m, string reason, RestoreOrigin history, SnapshotManifest before = null)
        {
            deferred = m;
            deferredReason = reason;
            finisher ??= core.dispatcher.CreateTimer(TimeSpan.FromMilliseconds(25), () => Pump());
            finisher.Start();
            Changed?.Invoke();
            return new RestoreResult { Mode = "deferred", Reason = reason, Snapshot = m };
        }

        /// <summary>
        /// A live restore or a deferred one waits no more (another restore replaced it, the song changed, the user cancelled). A live restore
        /// that hasn't reached its boundary is taken back; one that has is finished at once.
        /// </summary>
        public void CancelPending()
        {
            var plan = active;
            if (plan != null && !TakeBack(plan))
                FinishNow(plan);
            deferred = null;
            deferredReason = null;
            if (active == null) finisher?.Stop();
            Changed?.Invoke();
        }

        /// <summary>Takes a plan that hasn't applied back (under AudioLock, so the audio thread isn't in the middle of it). False: it had applied.</summary>
        bool TakeBack(LiveRestorePlan plan)
        {
            lock (ReBuzzCore.AudioLock)
            {
                core.ActiveRestore = null;
                if (plan.State != LiveRestorePlan.Waiting) return false;
                plan.State = LiveRestorePlan.Cancelled;
            }
            LiveRestorePlanner.Discard(core, plan, plan.AllNewMachines());
            active = null;
            return true;
        }

        /// <summary>UI thread: finishes an applied plan now (the old machines go, the new ones are shown), without waiting for the glide or the fades.</summary>
        void FinishNow(LiveRestorePlan plan)
        {
            lock (ReBuzzCore.AudioLock) core.ActiveRestore = null;
            LiveRestorePlanner.Finish(core, plan);
            plan.State = LiveRestorePlan.Finished;
            active = null;
            changed = true;
            finisher?.Stop();
            Changed?.Invoke();
            RestoreFinished?.Invoke();
        }

        /// <summary>
        /// UI thread, every 25 ms while something waits (a timer; tests call it too): finishes an applied live restore once its glide is done,
        /// and runs a deferred restore when the transport has stopped.
        /// </summary>
        public void Pump()
        {
            var plan = active;
            if (plan != null)
            {
                bool playing = core.PlayRequested;
                if (plan.State == LiveRestorePlan.Waiting && !playing)
                {
                    // The transport stopped before the boundary: what was prepared is taken back and the restore waits for the stopped path.
                    if (TakeBack(plan)) RestoreStoppedNow(plan.Target);
                }
                else if (plan.State == LiveRestorePlan.GlideDone || (plan.State == LiveRestorePlan.Applied && !playing))
                {
                    if (plan.AppliedAt == 0) plan.AppliedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    // The fades take a few blocks; give them time before the old machines go.
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - plan.AppliedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms >= 100 || !playing) FinishNow(plan);
                }
                else if (plan.State == LiveRestorePlan.Applied && plan.AppliedAt == 0)
                    plan.AppliedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                return;
            }
            var d = deferred;
            if (d != null && !core.PlayRequested)
            {
                string reason = deferredReason;
                deferred = null;
                deferredReason = null;
                finisher?.Stop();
                RestoreStoppedNow(d, reason);
            }
            else if (d == null) finisher?.Stop();
        }

        void RestoreStoppedNow(SnapshotManifest m, string reason = null)
        {
            try { RestoreStopped(m, null, reason ?? "the transport stopped before the boundary"); }
            catch (InvalidOperationException e) { LastError = e.Message; }
            Changed?.Invoke();
            RestoreFinished?.Invoke();
        }

        sealed class RestoreAction : BuzzGUI.Interfaces.IAction, BuzzGUI.Common.IHistoryAction
        {
            readonly SongSnapshots owner;
            readonly string target, before, name;

            public RestoreAction(SongSnapshots owner, string target, string before, string name)
            {
                this.owner = owner; this.target = target; this.before = before; this.name = name;
            }

            public string HistoryName => name;
            public long HistoryBytes => 0;
            public IEnumerable<KeyValuePair<object, long>> HistoryBlobs => null;

            public void Do() => Run(target, "redo");
            public void Undo() => Run(before, "undo");

            void Run(string id, string what)
            {
                var m = owner.Find(id);
                if (m == null)
                {
                    owner.LastError = "Can't " + what + " the restore: its snapshot was deleted.";
                    return;
                }
                try { owner.RestoreFromHistory(m); }
                catch (InvalidOperationException e) { owner.LastError = e.Message; }
            }
        }

        // ---- Embedding ----

        /// <summary>The section a save adds when "embed history" is on: the manifests and the chunks they use. Null: nothing to embed.</summary>
        public byte[] BuildEmbedded()
        {
            if (!Embed) return null;
            Flush();
            List<SnapshotManifest> list;
            lock (gate) list = items.Where(i => !i.Pending && i.Sections.Count > 0).ToList();
            if (list.Count == 0 || store == null) return null;
            var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            w.Write((byte)1);
            w.Write(list.Count);
            foreach (var m in list)
            {
                var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m));
                w.Write(json.Length);
                w.Write(json);
            }
            var hashes = list.SelectMany(i => i.Sections).SelectMany(s => s.Chunks).Select(c => c.Hash).Distinct().ToList();
            w.Write(hashes.Count);
            foreach (var h in hashes)
            {
                var stored = store.GetStored(h);
                w.Write(Convert.FromHexString(h));
                w.Write(stored.Length);
                w.Write(stored);
            }
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>Loader: the song file carries history. It is read once the song's name is known (<see cref="Bind"/>), and only if the folder has none.</summary>
        public void ImportEmbedded(byte[] data) => pendingEmbedded = data;

        void ReadEmbedded(byte[] data)
        {
            try
            {
                using var r = new BinaryReader(new MemoryStream(data));
                if (r.ReadByte() != 1) return;
                int n = r.ReadInt32();
                var manifests = new List<SnapshotManifest>();
                for (int i = 0; i < n; i++)
                {
                    var json = r.ReadBytes(r.ReadInt32());
                    var m = JsonSerializer.Deserialize<SnapshotManifest>(json);
                    if (m?.Id != null) manifests.Add(m);
                }
                int chunks = r.ReadInt32();
                for (int i = 0; i < chunks; i++)
                {
                    string hash = Convert.ToHexString(r.ReadBytes(32)).ToLowerInvariant();
                    store.PutStored(hash, r.ReadBytes(r.ReadInt32()));
                }
                foreach (var m in manifests)
                {
                    WriteManifest(store, m);
                    lock (gate) items.Add(m);
                }
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
            {
                LastError = "The history embedded in the song couldn't be read: " + e.Message;
            }
        }
    }
}
