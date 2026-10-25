// =====================================================================
// MatildeImport.cs — bulk-clone patterns from a Matilde Tracker (or any
// IMachine using Modern Pattern Editor with the standard Note/Wave/
// Volume/Effect+Argument track-parameter layout) into this Pedal Tracker
// instance.
//
// Approach
// --------
// Pattern data for managed machines lives in the Modern Pattern Editor
// ("MPE")'s own MPEPattern / MPEPatternColumn structures, NOT in the
// public IPatternColumn that ReBuzz core exposes.  MPE caches its own
// event lists; writing through IPatternColumn.SetEvents updates the
// core but not MPE's display.
//
// To make a real copy we therefore have to drive MPE directly:
//   1. Find the ModernPatternEditorMachine instance via Buzz.Song.Machines.
//   2. Reach its private 'ModernPatternEditor' field (the PatternEditor
//      WPF UserControl) and from there the internal 'MPEPatternsDB'.
//   3. Use the public IMachine.CreatePattern / DeletePattern to add /
//      remove patterns.  MPE observes IMachine.PatternAdded /
//      PatternRemoved and creates an empty MPEPattern automatically.
//   4. After each CreatePattern, fetch the new MPEPattern via
//      MPEPatternsDB.GetMPEPattern(IPattern), then drive its
//      CreateNewColumn(parameter, track) and column.SetEvents(events,
//      true, false) to populate it.  The 'play=false' overload avoids
//      any audio-thread side effects.
//
// All MPE access is via reflection so we don't take a compile-time
// dependency on the editor assembly.  Failures throw with descriptive
// messages so the caller can show them in the debug console.
//
// Name mapping
// ------------
// Matilde declares its track parameters as Note / Wave / Volume /
// Effect1 / Argument1 / Effect2 / Argument2 — confirmed against
// Buzztrax/buzzmachines/Matilde/Tracker/Tracker.cpp.  Pedal Tracker
// uses Note / Wave / Volume / Cmd / Arg / Cmd2 / Arg2.  Mapping is by
// name: identity first, then the Matilde alias table.
// =====================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BuzzGUI.Interfaces;
using Buzz.MachineInterface;

namespace WDE.PedalTracker
{
    internal static class MatildeImport
    {
        // ── Param-name alias: source-side name → Pedal-Tracker-side name ─────
        // Identity entries listed for clarity; missing entries fall back to
        // identity at lookup time.
        static readonly Dictionary<string, string> ParamAlias = new()
        {
            { "Note",       "Note"   },
            { "Wave",       "Wave"   },
            { "Volume",     "Volume" },
            { "Effect1",    "Cmd"    },
            { "Argument1",  "Arg"    },
            { "Effect2",    "Cmd2"   },
            { "Argument2",  "Arg2"   },
            // Identity passthroughs for Pedal-Tracker → Pedal-Tracker imports
            // and any third-party tracker that already uses our names.
            { "Cmd",        "Cmd"    },
            { "Arg",        "Arg"    },
            { "Cmd2",       "Cmd2"   },
            { "Arg2",       "Arg2"   },
        };

        // ── Public entry point ───────────────────────────────────────────────
        /// <summary>
        /// Run the import.  Returns a multi-line summary suitable for
        /// DCWriteLine (one Buzz console line per call though, so callers
        /// should split on '\n').  Throws on fatal errors so the caller
        /// can surface them.  All operations expected to run on the UI
        /// thread (the right-click menu handler runs there).
        /// </summary>
        public static string Run(IBuzz buzz, IMachine source, IMachine destination)
        {
            if (buzz == null)        throw new ArgumentNullException(nameof(buzz));
            if (source == null)      throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (ReferenceEquals(source, destination))
                throw new InvalidOperationException(
                    "Source and destination are the same machine — refusing to copy onto self.");

            var sb = new StringBuilder();
            sb.AppendFormat("[Import] {0}  →  {1}\n", source.Name, destination.Name);
            sb.AppendFormat("[Import] source: {0} pattern(s), {1} track(s)\n",
                source.Patterns?.Count ?? 0, source.TrackCount);

            // ── 1. Locate MPE editors for source and destination ─────────────
            var mpe = ResolveMpe(buzz, source, destination);
            sb.Append($"[Import] MPE editors: {mpe.SummaryLine}\n");

            // ── 2. Wipe destination patterns ─────────────────────────────────
            int wiped = 0;
            // Snapshot the list — DeletePattern mutates Patterns.
            foreach (var p in destination.Patterns.ToArray())
            {
                try { destination.DeletePattern(p); wiped++; }
                catch (Exception ex)
                {
                    sb.AppendFormat("[Import] WARN: failed to delete '{0}': {1}\n",
                        p.Name, ex.Message);
                }
            }
            sb.AppendFormat("[Import] removed {0} existing pattern(s)\n", wiped);

            // ── 3. Match destination track count to source ───────────────────
            int srcTracks = source.TrackCount;
            int dstTracks = destination.TrackCount;
            if (dstTracks < srcTracks)
            {
                try
                {
                    destination.TrackCount = srcTracks;
                    sb.AppendFormat("[Import] grew destination tracks {0} → {1}\n",
                        dstTracks, srcTracks);
                }
                catch (Exception ex)
                {
                    sb.AppendFormat("[Import] WARN: TrackCount = {0} failed ({1}); " +
                        "import will continue but tracks beyond {2} won't be copied.\n",
                        srcTracks, ex.Message, dstTracks);
                }
            }

            // ── 4. Per-pattern import ────────────────────────────────────────
            int patternsImported = 0;
            int columnsCopied    = 0;
            int eventsCopied     = 0;
            int colsSkippedNoMatch = 0;
            var unknownCmds = new SortedSet<byte>();

            // Build dest parameter index by Pedal-Tracker-side name.
            var dstParams = destination.ParameterGroups[2].Parameters
                .Where(p => p?.Name != null)
                .ToDictionary(p => p.Name, p => p);

            foreach (var srcPattern in source.Patterns.ToArray())
            {
                int origLen = srcPattern.Length;
                string name = srcPattern.Name ?? "untitled";

                // Create destination pattern; MPE's PatternAdded handler
                // will register an empty MPEPattern for it.
                try { destination.CreatePattern(name, origLen); }
                catch (Exception ex)
                {
                    sb.AppendFormat("[Import] WARN: CreatePattern('{0}', {1}) failed: {2}\n",
                        name, origLen, ex.Message);
                    continue;
                }

                // The newly-created pattern is in destination.Patterns.
                // Find by name (last-wins if duplicates somehow exist).
                var dstPattern = destination.Patterns.LastOrDefault(p => p.Name == name);
                if (dstPattern == null)
                {
                    sb.AppendFormat("[Import] WARN: created pattern '{0}' but couldn't locate it back\n",
                        name);
                    continue;
                }

                // Fetch MPE-side wrappers for both
                object srcMpe = mpe.GetMpePattern(srcPattern);
                object dstMpe = mpe.GetMpePattern(dstPattern);
                if (srcMpe == null || dstMpe == null)
                {
                    sb.AppendFormat("[Import] WARN: MPE pattern lookup failed " +
                        "(src={0}, dst={1}) for '{2}'\n",
                        srcMpe != null, dstMpe != null, name);
                    continue;
                }

                // Copy RowsPerBeat (best effort — the property is internal)
                mpe.TryCopyRowsPerBeat(srcMpe, dstMpe);

                // Iterate source MPE columns
                IList srcCols = mpe.GetMpeColumns(srcMpe);
                if (srcCols == null)
                {
                    sb.AppendFormat("[Import] WARN: no columns found on source pattern '{0}'\n", name);
                    continue;
                }

                foreach (object srcCol in srcCols)
                {
                    var (srcParam, srcTrack) = mpe.ReadColumnIdentity(srcCol);
                    if (srcParam == null) continue;
                    if (srcTrack >= destination.TrackCount) { colsSkippedNoMatch++; continue; }

                    // Map source param name → destination param name
                    string srcName = srcParam.Name;
                    if (srcName == null) continue;
                    if (!ParamAlias.TryGetValue(srcName, out string dstName))
                        dstName = srcName;       // fall back to identity

                    if (!dstParams.TryGetValue(dstName, out IParameter dstParam))
                    {
                        // Source has a column we don't have (e.g. global-only
                        // or future Matilde extension).  Skip with telemetry.
                        colsSkippedNoMatch++;
                        continue;
                    }

                    // Read source events
                    var events = mpe.ReadColumnEvents(srcCol);
                    if (events == null || events.Length == 0) continue;

                    // For Cmd/Cmd2 columns flag any value Pedal Tracker
                    // doesn't currently implement.  We still copy the byte
                    // — the user might set up the same effects later, and
                    // the data is preserved verbatim.
                    if (dstName == "Cmd" || dstName == "Cmd2")
                    {
                        for (int i = 0; i < events.Length; i++)
                        {
                            byte cmd = (byte)(events[i].Value & 0xFF);
                            if (cmd != 0 && !PedalTrackerKnowsCmd(cmd))
                                unknownCmds.Add(cmd);
                        }
                    }

                    // Note column: pass through (both machines use Buzz pt_note byte form)
                    // Volume / Wave / Arg: identity byte values

                    // Create or find the destination MPEPatternColumn for
                    // (dstParam, srcTrack)
                    object dstCol = mpe.GetOrCreateColumn(dstMpe, dstParam, srcTrack);
                    if (dstCol == null)
                    {
                        sb.AppendFormat("[Import] WARN: could not create dest column " +
                            "{0} track {1} in '{2}'\n", dstName, srcTrack, name);
                        continue;
                    }

                    if (mpe.WriteColumnEvents(dstCol, events))
                    {
                        columnsCopied++;
                        eventsCopied += events.Length;
                    }
                    else
                    {
                        sb.AppendFormat("[Import] WARN: SetEvents failed for {0} t{1} in '{2}'\n",
                            dstName, srcTrack, name);
                    }
                }

                // Refresh UI
                try { dstPattern.NotifyPatternChanged(); } catch { /* not critical */ }
                patternsImported++;
            }

            sb.AppendFormat("[Import] imported {0} pattern(s), {1} column(s), {2} event(s)\n",
                patternsImported, columnsCopied, eventsCopied);
            if (colsSkippedNoMatch > 0)
                sb.AppendFormat("[Import] skipped {0} unmappable column(s) " +
                    "(track out of range or no destination parameter)\n", colsSkippedNoMatch);
            if (unknownCmds.Count > 0)
            {
                sb.Append("[Import] copied effect commands Pedal Tracker doesn't yet implement: ");
                sb.AppendLine(string.Join(" ", unknownCmds.Select(b => b.ToString("X2"))));
                sb.Append("[Import]   (the bytes are preserved; the engine just ignores them)\n");
            }
            sb.Append("[Import] done.\n");
            return sb.ToString();
        }

        // Effect commands Pedal Tracker v1.2's engine actively interprets.
        // Anything else is copied verbatim but ignored at playback time.
        static bool PedalTrackerKnowsCmd(byte cmd) => cmd switch
        {
            0x01 or 0x02 or 0x03 or 0x04 or 0x09 or 0x0A or 0x0C or 0x0E => true,
            _ => false
        };

        // ─────────────────────────────────────────────────────────────────────
        // MpeBridge — wraps reflection into MPE's internals.  Each machine
        // gets its OWN dedicated PatternEditor instance (with its own
        // MPEPatternsDB), so the bridge holds two database references — one
        // for the source machine, one for the destination — and routes
        // GetMpePattern() calls to the correct DB based on which side the
        // pattern's owner is.
        // ─────────────────────────────────────────────────────────────────────
        sealed class MpeBridge
        {
            public string SummaryLine { get; }

            readonly object _srcDb;               // source's MPEPatternDatabase
            readonly object _dstDb;               // destination's MPEPatternDatabase
            readonly IMachine _srcMachine;        // for routing pattern lookups
            readonly IMachine _dstMachine;
            readonly MethodInfo _getMpePattern;   // MPEPatternDatabase.GetMPEPattern(IPattern)
            readonly MethodInfo _createNewColumn; // MPEPattern.CreateNewColumn(IParameter, int)
            readonly MethodInfo _columnSetEvents; // MPEPatternColumn.SetEvents(PatternEvent[], bool, bool)
            readonly MethodInfo _columnGetEvents; // MPEPatternColumn.GetEvents(int, int)

            public MpeBridge(object srcDb, object dstDb,
                             IMachine srcMachine, IMachine dstMachine,
                             string summary,
                             MethodInfo getMpePattern, MethodInfo createNewColumn,
                             MethodInfo columnSetEvents, MethodInfo columnGetEvents)
            {
                _srcDb            = srcDb;
                _dstDb            = dstDb;
                _srcMachine       = srcMachine;
                _dstMachine       = dstMachine;
                _getMpePattern    = getMpePattern;
                _createNewColumn  = createNewColumn;
                _columnSetEvents  = columnSetEvents;
                _columnGetEvents  = columnGetEvents;
                SummaryLine       = summary;
            }

            public object GetMpePattern(IPattern p)
            {
                if (p == null) return null;
                // Route to the DB that owns the pattern's machine.
                object db = ReferenceEquals(p.Machine, _srcMachine) ? _srcDb
                          : ReferenceEquals(p.Machine, _dstMachine) ? _dstDb
                          : null;
                if (db == null) return null;
                try { return _getMpePattern.Invoke(db, new object[] { p }); }
                catch (TargetInvocationException) { return null; }
                catch { return null; }
            }

            public IList GetMpeColumns(object mpePattern)
            {
                // 'MPEPatternColumns' is a public List<MPEPatternColumn> on MPEPattern.
                var fi = mpePattern.GetType().GetField("MPEPatternColumns",
                    BindingFlags.Public | BindingFlags.Instance);
                return fi?.GetValue(mpePattern) as IList;
            }

            public (IParameter param, int track) ReadColumnIdentity(object col)
            {
                var par = ReadProperty(col, "Parameter") as IParameter
                       ?? ReadField(col, "Parameter") as IParameter;
                int track = 0;
                object t = ReadProperty(col, "ParamTrack")
                         ?? ReadField(col, "ParamTrack");
                if (t is int i) track = i;
                return (par, track);
            }

            public PatternEvent[] ReadColumnEvents(object col)
            {
                try
                {
                    var result = _columnGetEvents.Invoke(col,
                        new object[] { 0, int.MaxValue });
                    if (result is IEnumerable<PatternEvent> e) return e.ToArray();
                }
                catch { }
                // Fallback: read the raw 'eventList' field directly so we
                // capture every event regardless of GetEvents() filtering.
                var fi = col.GetType().GetField("eventList",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi?.GetValue(col) is IList<PatternEvent> raw)
                    return raw.ToArray();
                return null;
            }

            public bool WriteColumnEvents(object col, PatternEvent[] events)
            {
                try
                {
                    _columnSetEvents.Invoke(col, new object[] { events, true, false });
                    return true;
                }
                catch (TargetInvocationException) { return false; }
                catch { return false; }
            }

            public object GetOrCreateColumn(object mpePattern, IParameter dstParam, int track)
            {
                // First look for an existing column with matching (parameter, track)
                IList existing = GetMpeColumns(mpePattern);
                if (existing != null)
                {
                    foreach (object c in existing)
                    {
                        var (par, trk) = ReadColumnIdentity(c);
                        if (par == dstParam && trk == track) return c;
                    }
                }
                // Otherwise create one via MPEPattern.CreateNewColumn(IParameter, int)
                try
                {
                    return _createNewColumn.Invoke(mpePattern,
                        new object[] { dstParam, track });
                }
                catch (TargetInvocationException) { return null; }
                catch { return null; }
            }

            public void TryCopyRowsPerBeat(object srcMpe, object dstMpe)
            {
                try
                {
                    var pi = srcMpe.GetType().GetProperty("RowsPerBeat",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pi == null) return;
                    object rpb = pi.GetValue(srcMpe);
                    if (rpb is int)
                        pi.SetValue(dstMpe, rpb);
                }
                catch { /* not critical */ }
            }

            static object ReadField(object obj, string name)
            {
                var fi = obj?.GetType().GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return fi?.GetValue(obj);
            }
            static object ReadProperty(object obj, string name)
            {
                var pi = obj?.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return pi?.GetValue(obj);
            }
        }

        // Each managed machine has its OWN dedicated pattern editor instance
        // (ReBuzz creates them per-machine, and they're hidden from
        // Song.Machines by name "\u0001pe1", "\u0001pe2"...).  The editor
        // reference lives on MachineCore.EditorMachine, which is internal
        // to ReBuzz core but reachable via reflection.  We extract source's
        // and destination's editors separately, then resolve method handles
        // from one of them (they're all the same Type).
        static MpeBridge ResolveMpe(IBuzz buzz, IMachine source, IMachine destination)
        {
            (object srcDb, object srcEditorMM, object srcEditorPE, string srcLabel) =
                ResolveOneSide(source, "source");
            (object dstDb, object dstEditorMM, object dstEditorPE, string dstLabel) =
                ResolveOneSide(destination, "destination");

            // Resolve the MethodInfos once — same Type on both sides.
            var getMpePattern = srcDb.GetType().GetMethod("GetMPEPattern",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(IPattern) }, null);
            if (getMpePattern == null)
                throw new InvalidOperationException(
                    "MPEPatternDatabase.GetMPEPattern(IPattern) not found.");

            Type mpePatternType = getMpePattern.ReturnType;
            var createNewColumn = mpePatternType.GetMethod("CreateNewColumn",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(IParameter), typeof(int) }, null);
            if (createNewColumn == null)
                throw new InvalidOperationException(
                    "MPEPattern.CreateNewColumn(IParameter, int) not found.");

            // MPEPatternColumn type: pulled from the MPEPatternColumns list field.
            var columnsField = mpePatternType.GetField("MPEPatternColumns",
                BindingFlags.Public | BindingFlags.Instance);
            Type mpeColType = null;
            if (columnsField != null)
            {
                var ft = columnsField.FieldType;
                if (ft.IsGenericType)
                {
                    var args = ft.GetGenericArguments();
                    if (args.Length == 1) mpeColType = args[0];
                }
            }
            if (mpeColType == null)
                throw new InvalidOperationException(
                    "Could not determine MPEPatternColumn element type.");

            var setEvents = mpeColType.GetMethod("SetEvents",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(PatternEvent[]), typeof(bool), typeof(bool) }, null);
            if (setEvents == null)
                throw new InvalidOperationException(
                    "MPEPatternColumn.SetEvents(PatternEvent[], bool, bool) not found.");

            var getEvents = mpeColType.GetMethod("GetEvents",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(int), typeof(int) }, null);
            if (getEvents == null)
                throw new InvalidOperationException(
                    "MPEPatternColumn.GetEvents(int, int) not found.");

            string summary = $"src={srcLabel}, dst={dstLabel}";
            return new MpeBridge(srcDb, dstDb, source, destination, summary,
                getMpePattern, createNewColumn, setEvents, getEvents);
        }

        // Find one machine's editor + its MPEPatternsDB.  Throws with a
        // descriptive message if any step fails.
        static (object db, object editorMM, object editorPE, string label)
            ResolveOneSide(IMachine machine, string sideName)
        {
            // Step A: read MachineCore.EditorMachine via reflection.  This
            // is a public-but-internal-typed property/field on MachineCore.
            object editorIm = ReadAny(machine, "EditorMachine");
            if (editorIm == null)
                throw new InvalidOperationException(
                    $"{sideName} machine '{machine.Name}' has no EditorMachine — " +
                    "it has no pattern editor assigned.  Both source and destination " +
                    "must use Modern Pattern Editor.");

            // Step B: editorIm is itself an IMachine (MachineCore : IMachine).
            // Read its ManagedMachine — the IBuzzMachine implementation, which
            // for MPE is a ModernPatternEditorMachine.
            IBuzzMachine editorMM = null;
            if (editorIm is IMachine eimAsM) editorMM = eimAsM.ManagedMachine;
            if (editorMM == null)
                editorMM = ReadAny(editorIm, "ManagedMachine") as IBuzzMachine;
            if (editorMM == null)
                throw new InvalidOperationException(
                    $"{sideName}'s editor has no ManagedMachine — its pattern editor " +
                    "is probably not Modern Pattern Editor.");

            // Step C: confirm class identity.  If this isn't ModernPatternEditorMachine
            // we throw now rather than fail later in a confusing way.
            string mmTypeName = editorMM.GetType().FullName ?? "";
            if (mmTypeName.IndexOf("ModernPatternEditorMachine",
                                    StringComparison.Ordinal) < 0)
                throw new InvalidOperationException(
                    $"{sideName}'s editor is '{mmTypeName}', not Modern Pattern Editor.  " +
                    "This importer requires both machines to use Modern Pattern Editor.");

            // Step D: reach the private 'ModernPatternEditor' field/prop on
            // ModernPatternEditorMachine.  It's typed as PatternEditor (a
            // WPF UserControl).
            object patternEditor = null;
            foreach (var name in new[] { "ModernPatternEditor", "PatternEditor", "Editor" })
            {
                patternEditor = ReadAny(editorMM, name);
                if (patternEditor != null) break;
            }
            if (patternEditor == null)
                throw new InvalidOperationException(
                    $"Could not find PatternEditor field on {sideName}'s " +
                    "ModernPatternEditorMachine instance.");

            // Step E: reach the internal MPEPatternsDB on that PatternEditor.
            object db = null;
            foreach (var name in new[] { "MPEPatternsDB", "MPEPatternsDb", "PatternsDB" })
            {
                db = ReadAny(patternEditor, name);
                if (db != null) break;
            }
            if (db == null)
                throw new InvalidOperationException(
                    $"Could not find MPEPatternsDB on {sideName}'s PatternEditor.");

            string label = $"'{machine.Name}' → {editorMM.GetType().Name}";
            return (db, editorMM, patternEditor, label);
        }

        // One-stop reflection lookup: try field then property, public AND
        // non-public, instance scope.  Used for the editor-internal pokes.
        static object ReadAny(object obj, string memberName)
        {
            if (obj == null) return null;
            const BindingFlags bf =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var fi = obj.GetType().GetField(memberName, bf);
                if (fi != null) return fi.GetValue(obj);
                var pi = obj.GetType().GetProperty(memberName, bf);
                if (pi != null) return pi.GetValue(obj);
            }
            catch { }
            return null;
        }
    }
}
