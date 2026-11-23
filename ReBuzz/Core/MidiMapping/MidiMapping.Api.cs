// Fork (docs/specs/midi-mapping.md, MM1): what the views and the agent edit and ask through. Every edit is validated here and is one step of
// the song's history; the agent's tools and the Studio's popover, map mode and Inspector page call the same methods.

using BuzzGUI.Interfaces;
using ReBuzz.Core.Tracks;
using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace ReBuzz.Core
{
    internal enum MapScope { Global, Track }

    /// <summary>What a mapping edit did.</summary>
    internal sealed class MapResult
    {
        public string Error;
        public bool Ok => Error == null;
        public MapScope Scope;
        public string TrackId;
        public int Page;
        /// <summary>The knob's number from 0 when the control filled a slot of a page, else -1.</summary>
        public int Slot = -1;
        public MapSource Source;
        public MapTarget Target;
        /// <summary>"K3 → Neo Delay · Feedback", for the status line.</summary>
        public string Text;
        public bool Changed;
    }

    /// <summary>A mapping as a list shows it.</summary>
    internal sealed class MapInfo
    {
        public string Scope = "global";
        public string TrackId;
        public int Page;
        public int Slot = -1;
        public string Control = "";
        public string Short = "";
        public MapSource Source;
        public MapTarget Target;
        public string Range = "full";
        public bool Invert;
        public Takeover Takeover;
        public KnobEncoding Encoding;
        public bool Fallback;
        public bool Legacy;
    }

    /// <summary>What is being listened for: a control to map to a target, or the knobs of a device to teach.</summary>
    internal sealed class Listening
    {
        public MapTarget Target;
        public MapScope Scope;
        public MapDrive Drive;
        public string Label = "";

        public bool Teach;
        public string TeachDevice;       // null: the first device that moves a knob
        public int TeachCount = 8;
        public readonly List<MapSource> Taught = new List<MapSource>();
        public readonly List<KnobEncoding> TaughtEncodings = new List<KnobEncoding>();
    }

    internal sealed partial class MidiMapping
    {
        volatile Listening capture;
        volatile Refine refine;
        readonly object listenLock = new object();

        sealed class Refine
        {
            public MapSource Source;
            public long Until;
            public readonly EncoderDetector Detector = new EncoderDetector();
        }

        /// <summary>A control was mapped (by listening); UI thread.</summary>
        public event Action<MapResult> Mapped;

        /// <summary>A device's knobs were taught; UI thread.</summary>
        public event Action<string, int> Taught;

        /// <summary>One more knob of a device being taught was heard: the device and how many so far (UI thread).</summary>
        public event Action<string, int> TeachProgress;

        /// <summary>What is being listened for now, or null.</summary>
        public Listening Listening => capture;

        public bool IsListening => capture != null;

        // ------------------------------------------------------------------ validation

        /// <summary>The names the song's machine and parameter give, or why not (null: fine). <paramref name="trackId"/>: the page's track, when the target must lie on its chain.</summary>
        public string Validate(MapTarget target, string trackId = null)
        {
            if (target == null) return "Nothing to map to.";
            if (target.IsCommand)
                return target.Cmd == MapCommand.SelectTrack && target.Index < 1 ? "Select track needs a track number from 1." : null;
            var core = buzz.SongCore;
            var machine = core?.MachinesList.FirstOrDefault(m => m.Name == target.Machine);
            if (machine == null) return $"The song has no machine named \"{target.Machine}\".";
            var p = ResolveParam(target, core.MachinesList.ToDictionary(m => m.Name, m => m, StringComparer.Ordinal));
            if (p == null)
                return target.IsTrackParam && target.Track >= machine.ParameterGroupsList[2].TrackCount
                    ? $"{machine.Name} has {machine.ParameterGroupsList[2].TrackCount} track(s): there is no track {target.Track}."
                    : $"{machine.Name} has no parameter \"{target.ParamName}\"" + (target.IsTrackParam ? " (a track parameter)." : " (a global one; track parameters are written \"T:Name\").");
            if (p.Type == ParameterType.Note) return $"\"{p.Name}\" is a note parameter: notes play the machine, they are not mapped.";
            if (trackId != null)
            {
                var model = Model;
                var entry = EntryById(trackId, model);
                if (entry == null) return $"The song has no track \"{trackId}\".";
                if (!ChainOf(entry, model).Contains(machine)) return $"{machine.Name} is not on the chain of {entry.Name} (" + string.Join(", ", ChainOf(entry, model).Select(m => m.Name)) + ").";
            }
            return null;
        }

        static bool SameControl(MapSource a, MapSource b) =>
            a.Kind == b.Kind && a.Channel == b.Channel && a.Number == b.Number &&
            (a.AnyDevice || b.AnyDevice || string.Equals(a.Device, b.Device, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ mapping a control

        /// <summary>
        /// Maps a control to a target in a scope. In the track scope a knob of a device's group fills the slot of that number on the track's current
        /// page; any other control makes an entry of the page. In the global scope the control gets an entry that holds on every track. A control
        /// drives one target and a target is driven by one control in a scope.
        /// </summary>
        public MapResult MapControl(MapSource source, KnobEncoding encoding, MapTarget target, MapScope scope, MapDrive drive = null, string trackId = null)
        {
            var result = new MapResult { Scope = scope, Source = source, Target = target };
            if (scope == MapScope.Track) trackId ??= SelectedTrackId;
            if (scope == MapScope.Track && trackId == null) { result.Error = "No track is selected: select a track first, or map in the global scope."; return result; }
            result.Error = Validate(target, scope == MapScope.Track && !target.IsCommand ? trackId : null);
            if (result.Error != null) return result;
            if (drive != null && (drive.Min < 0 || drive.Max > 1 || drive.Min > drive.Max)) { result.Error = "The range runs from 0 to 1 of the parameter's span, low end first."; return result; }
            result.TrackId = trackId;
            string text = null;
            result.Changed = Edit((c, s) =>
            {
                var d = new MapDrive { Target = target, Min = drive?.Min ?? 0, Max = drive?.Max ?? 1, Invert = drive?.Invert ?? false, Takeover = drive?.Takeover ?? Takeover.Pickup };
                if (scope == MapScope.Global)
                {
                    var list = s.Global ?? c.Global;
                    list.RemoveAll(e => SameControl(e.Source, source) || (!target.IsCommand && e.Target.Equals(target)));
                    list.Add(new MapEntry { Source = source, Encoding = encoding, Target = d.Target, Min = d.Min, Max = d.Max, Invert = d.Invert, Takeover = d.Takeover });
                    text = source.Short() + " → " + target.Text;
                    return;
                }
                var pages = s.Ensure(trackId);
                var page = pages.CurrentPage;
                result.Page = pages.Current;
                int knob = !target.IsCommand && c.GroupOf(source.Device) is { } g ? g.IndexOf(source) : -1;
                foreach (var gone in page.Slots.Where(kv => kv.Value.Target != null && kv.Value.Target.Equals(target)).Select(kv => kv.Key).ToList()) page.Slots.Remove(gone);
                page.Direct.RemoveAll(e => SameControl(e.Source, source) || e.Target.Equals(target));
                if (knob >= 0)
                {
                    page.Slots[knob] = d;
                    result.Slot = knob;
                    text = "K" + (knob + 1) + " → " + target.Text + " on " + trackId.Substring(trackId.IndexOf(':') + 1);
                }
                else
                {
                    page.Direct.Add(new MapEntry { Source = source, Encoding = encoding, Target = d.Target, Min = d.Min, Max = d.Max, Invert = d.Invert, Takeover = d.Takeover });
                    text = source.Short() + " → " + target.Text + " on " + trackId.Substring(trackId.IndexOf(':') + 1);
                }
            });
            result.Text = text;
            return result;
        }

        /// <summary>Sets what a page slot holds (null target: the slot is free again, and falls back to the generator's pin).</summary>
        public MapResult SetSlot(string trackId, int page, int slot, MapTarget target, MapDrive drive = null)
        {
            var result = new MapResult { Scope = MapScope.Track, TrackId = trackId, Page = page, Slot = slot, Target = target };
            if (slot < 0 || slot > 63) { result.Error = "A slot is a knob number from 1 to 64."; return result; }
            if (EntryById(trackId) == null) { result.Error = $"The song has no track \"{trackId}\"."; return result; }
            if (target != null)
            {
                result.Error = Validate(target, target.IsCommand ? null : trackId);
                if (result.Error == null && target.IsCommand) result.Error = "A slot holds a parameter; commands are mapped to a control.";
                if (result.Error != null) return result;
            }
            result.Changed = Edit((c, s) =>
            {
                var pages = s.Ensure(trackId);
                if (page < 0 || page >= pages.Pages.Count) { result.Error = $"{trackId} has {pages.Pages.Count} page(s): there is no page {page + 1}."; return; }
                var p = pages.Pages[page];
                if (target == null) { p.Slots.Remove(slot); return; }
                foreach (var gone in p.Slots.Where(kv => kv.Key != slot && kv.Value.Target != null && kv.Value.Target.Equals(target)).Select(kv => kv.Key).ToList()) p.Slots.Remove(gone);
                p.Slots[slot] = new MapDrive { Target = target, Min = drive?.Min ?? 0, Max = drive?.Max ?? 1, Invert = drive?.Invert ?? false, Takeover = drive?.Takeover ?? Takeover.Pickup };
            });
            result.Text = target == null ? "K" + (slot + 1) + " is free" : "K" + (slot + 1) + " → " + target.Text;
            return result;
        }

        /// <summary>Removes what a control drives, in every scope that is in use now (global, and the selected track's page).</summary>
        public bool Unmap(MapSource source)
        {
            string track = SelectedTrackId;
            return Edit((c, s) =>
            {
                (s.Global ?? c.Global).RemoveAll(e => SameControl(e.Source, source));
                if (track == null || s.Find(track) is not { } pages) return;
                var page = pages.CurrentPage;
                page.Direct.RemoveAll(e => SameControl(e.Source, source));
                if (c.GroupOf(source.Device) is { } g && g.IndexOf(source) is >= 0 and int knob) page.Slots.Remove(knob);
            });
        }

        /// <summary>Removes the mappings of a target: in the global maps, and on the selected track's page (and with <paramref name="everywhere"/> on every page of every track).</summary>
        public bool UnmapTarget(MapTarget target, bool everywhere = false)
        {
            if (target == null) return false;
            string track = SelectedTrackId;
            bool changed = Edit((c, s) =>
            {
                (s.Global ?? c.Global).RemoveAll(e => e.Target.Equals(target));
                foreach (var pages in s.Tracks.Where(t => everywhere || t.TrackId == track))
                    foreach (var page in pages.Pages)
                    {
                        page.Direct.RemoveAll(e => e.Target.Equals(target));
                        foreach (var gone in page.Slots.Where(kv => kv.Value.Target != null && kv.Value.Target.Equals(target)).Select(kv => kv.Key).ToList()) page.Slots.Remove(gone);
                    }
            });
            // An old Buzz binding of the parameter goes too.
            changed |= RemoveLegacy(target);
            return changed;
        }

        /// <summary>Changes the range, invert and takeover of what a target is mapped to (everywhere it is).</summary>
        public bool SetDrive(MapTarget target, double min, double max, bool invert, Takeover takeover)
        {
            if (target == null || min < 0 || max > 1 || min > max) return false;
            return Edit((c, s) =>
            {
                void Apply(MapDrive d)
                {
                    if (d.Target == null || !d.Target.Equals(target)) return;
                    d.Min = min; d.Max = max; d.Invert = invert; d.Takeover = takeover;
                }
                foreach (var e in s.Global ?? c.Global) Apply(e);
                foreach (var t in s.Tracks)
                    foreach (var p in t.Pages)
                    {
                        foreach (var d in p.Slots.Values) Apply(d);
                        foreach (var e in p.Direct) Apply(e);
                    }
            });
        }

        /// <summary>The song keeps its own copy of the global entries (starting from the computer's), or goes back to the computer's.</summary>
        public bool SetSongGlobal(bool own) => Edit((c, s) =>
        {
            if (own) s.Global ??= c.Global.Select(e => e.Clone()).ToList();
            else s.Global = null;
        });

        // ------------------------------------------------------------------ pages

        public TrackPages PagesOf(string trackId) => song.Find(trackId)?.Clone();

        public bool AddPage(string trackId)
        {
            if (EntryById(trackId) == null) return false;
            return Edit((c, s) =>
            {
                var t = s.Ensure(trackId);
                if (t.Pages.Count >= TrackPages.MaxPages) return;
                t.Pages.Add(new TrackPage { Name = "Page " + (t.Pages.Count + 1) });
                t.Current = t.Pages.Count - 1;
            });
        }

        public bool SelectPage(string trackId, int page) => Edit((c, s) =>
        {
            var t = s.Find(trackId);
            if (t != null && page >= 0 && page < t.Pages.Count) t.Current = page;
        });

        public bool RemovePage(string trackId, int page) => Edit((c, s) =>
        {
            var t = s.Find(trackId);
            if (t == null || t.Pages.Count <= 1 || page < 0 || page >= t.Pages.Count) return;
            t.Pages.RemoveAt(page);
            t.Current = Math.Clamp(t.Current, 0, t.Pages.Count - 1);
        });

        // ------------------------------------------------------------------ controllers

        /// <summary>The controllers: the inputs open now, and every device a map, a group or a role names.</summary>
        public IReadOnlyList<(string Id, bool Connected, int Knobs, bool Keys)> Controllers()
        {
            var ids = new List<string>();
            var open = buzz.MidiInOutEngine?.OpenInputKeys() ?? Array.Empty<string>();
            ids.AddRange(open);
            var c = computer;
            foreach (var d in c.Groups.Select(g => g.Device).Concat(c.NoKeys).Concat((song.Global ?? c.Global).Select(e => e.Source.Device)).Where(d => !string.IsNullOrEmpty(d)))
                if (!ids.Contains(d, StringComparer.OrdinalIgnoreCase)) ids.Add(d);
            return ids.Select(id => (id, open.Contains(id, StringComparer.OrdinalIgnoreCase), c.GroupOf(id)?.Count ?? 0, !c.NoKeys.Contains(id))).ToList();
        }

        /// <summary>Whether a device's notes follow the selected track (the default; a control surface or a pad-only device can be left out).</summary>
        public bool SetKeysRole(string device, bool keys)
        {
            if (string.IsNullOrEmpty(device)) return false;
            return Edit((c, s) => { if (keys) c.NoKeys.Remove(device); else c.NoKeys.Add(device); });
        }

        /// <summary>Sets a device's knob group by hand (the agent; teaching does it from the knobs).</summary>
        public bool SetKnobGroup(string device, IEnumerable<MapSource> knobs, IEnumerable<KnobEncoding> encodings = null)
        {
            if (string.IsNullOrEmpty(device)) return false;
            var list = knobs.Take(64).Select(k => k with { Device = device }).ToList();
            var enc = (encodings ?? Array.Empty<KnobEncoding>()).ToList();
            return Edit((c, s) =>
            {
                c.Groups.RemoveAll(g => string.Equals(g.Device, device, StringComparison.OrdinalIgnoreCase));
                if (list.Count == 0) return;
                var g = new KnobGroup { Device = device };
                for (int i = 0; i < list.Count; i++) { g.Knobs.Add(list[i]); g.Encodings.Add(i < enc.Count ? enc[i] : KnobEncoding.Absolute); }
                c.Groups.Add(g);
            });
        }

        // ------------------------------------------------------------------ listening (map mode, the MIDI button, teaching)

        /// <summary>Starts listening: the next control moved on any controller is mapped to the target in the scope.</summary>
        public MapResult BeginMap(MapTarget target, MapScope scope, MapDrive drive = null, string label = null)
        {
            var result = new MapResult { Scope = scope, Target = target };
            if (scope == MapScope.Track && SelectedTrackId == null) { result.Error = "No track is selected: select a track first, or map in the global scope."; return result; }
            result.Error = Validate(target, scope == MapScope.Track && !target.IsCommand ? SelectedTrackId : null);
            if (result.Error != null) return result;
            capture = new Listening { Target = target, Scope = scope, Drive = drive, Label = label ?? target.Text };
            Changed?.Invoke();
            return result;
        }

        /// <summary>Starts listening for a device's knobs, in order: "turn knob 1 ... knob N". A null device is the first one that moves a CC.</summary>
        public void BeginTeach(string device = null, int count = 8)
        {
            capture = new Listening { Teach = true, TeachDevice = string.IsNullOrEmpty(device) ? null : device, TeachCount = Math.Clamp(count, 1, 64), Label = "knobs" };
            Changed?.Invoke();
        }

        /// <summary>Ends teaching with the knobs heard so far (at least one).</summary>
        public void FinishTeach()
        {
            if (capture is { Teach: true } cap) FinishTeach(cap);
        }

        public void CancelListening()
        {
            if (System.Threading.Interlocked.Exchange(ref capture, null) != null) Changed?.Invoke();
        }

        /// <summary>MIDI thread: lets a control through to the listener. True when the message was taken (it must not play).</summary>
        bool TryCapture(Listening cap, MidiDevice device, MapKind kind, int channel, int number, int value, int max)
        {
            if (kind == MapKind.Note && value == 0) return true;                         // a key's release while listening
            if (kind == MapKind.PitchBend && Math.Abs(value - 8192) < 2048) return true;  // a wheel's drift
            string id = device.Id;
            if (cap.Teach)
            {
                if (kind != MapKind.ControlChange) return false;
                buzz.dispatcher.BeginInvoke(() => OnTaught(cap, id, channel, number, value));
                return true;
            }
            if (System.Threading.Interlocked.CompareExchange(ref capture, null, cap) != cap) return false;
            buzz.dispatcher.BeginInvoke(() => OnCaptured(cap, new MapSource(id, kind, channel, number), value));
            return true;
        }

        void OnCaptured(Listening cap, MapSource source, int value)
        {
            var enc = source.Kind == MapKind.ControlChange ? MapMath.Guess(value) : KnobEncoding.Absolute;
            var result = MapControl(source, enc, cap.Target, cap.Scope, cap.Drive);
            if (result.Ok && source.Kind == MapKind.ControlChange)
            {
                var r = new Refine { Source = source, Until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3 / 2 };
                r.Detector.Observe(value);
                refine = r;
            }
            Changed?.Invoke();
            Mapped?.Invoke(result);
        }

        void OnTaught(Listening cap, string device, int channel, int number, int value)
        {
            if (capture != cap) return;
            cap.TeachDevice ??= device;
            if (!string.Equals(cap.TeachDevice, device, StringComparison.OrdinalIgnoreCase)) return;
            var src = new MapSource(device, MapKind.ControlChange, channel, number);
            if (cap.Taught.Any(k => k.Channel == channel && k.Number == number)) return;
            cap.Taught.Add(src);
            cap.TaughtEncodings.Add(MapMath.Guess(value));
            TeachProgress?.Invoke(device, cap.Taught.Count);
            Changed?.Invoke();
            if (cap.Taught.Count >= cap.TeachCount) FinishTeach(cap);
        }

        void FinishTeach(Listening cap)
        {
            if (System.Threading.Interlocked.CompareExchange(ref capture, null, cap) != cap) return;
            if (cap.Taught.Count > 0 && cap.TeachDevice != null)
            {
                SetKnobGroup(cap.TeachDevice, cap.Taught, cap.TaughtEncodings);
                Taught?.Invoke(cap.TeachDevice, cap.Taught.Count);
            }
            Changed?.Invoke();
        }

        /// <summary>MIDI thread: a control just mapped is watched for a moment, to tell a knob from an encoder by what it sends.</summary>
        void RefineWatch(MidiDevice device, MapKind kind, int channel, int number, int value)
        {
            var r = refine;
            if (r == null || kind != MapKind.ControlChange || r.Source.Channel != channel || r.Source.Number != number) return;
            if (!r.Source.AnyDevice && !string.Equals(r.Source.Device, device.Id, StringComparison.OrdinalIgnoreCase)) return;
            if (Stopwatch.GetTimestamp() > r.Until) { refine = null; return; }
            PostRefine(r, value);
        }

        // Its own method: a lambda that captures the parameters would allocate its closure on entry of the caller, for every message.
        void PostRefine(Refine r, int value) => buzz.dispatcher.BeginInvoke(() => ObserveRefine(r, value));

        void ObserveRefine(Refine r, int value)
        {
            r.Detector.Observe(value);
            var enc = r.Detector.Encoding;
            bool changed = false;
            void Fix(MapEntry e) { if (SameControl(e.Source, r.Source) && e.Encoding != enc) { e.Encoding = enc; changed = true; } }
            foreach (var e in song.Global ?? computer.Global) Fix(e);
            foreach (var t in song.Tracks) foreach (var p in t.Pages) foreach (var e in p.Direct) Fix(e);
            foreach (var g in computer.Groups.Where(g => string.Equals(g.Device, r.Source.Device, StringComparison.OrdinalIgnoreCase)))
            {
                int i = g.IndexOf(r.Source);
                if (i >= 0 && i < g.Encodings.Count && g.Encodings[i] != enc) { g.Encodings[i] = enc; changed = true; }
            }
            if (!changed) return;
            Write(ComputerKey, computer.ToXml().ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
            buzz.SetModifiedFlag();
            Rebuild();
        }

        // ------------------------------------------------------------------ what is mapped (badges and lists)

        /// <summary>Every mapping that is in force now: the selected track's current page, then the global entries, then an old Buzz binding.</summary>
        public IReadOnlyList<MapInfo> Active()
        {
            var list = new List<MapInfo>();
            string track = SelectedTrackId;
            if (track != null)
                list.AddRange(ListTrack(track, onlyCurrent: true));
            list.AddRange(ListGlobal());
            list.AddRange(LegacyInfos());
            return list;
        }

        IEnumerable<MapInfo> ListGlobal()
        {
            foreach (var e in song.Global ?? computer.Global)
                yield return Info(e, "global", null, 0, -1);
        }

        IEnumerable<MapInfo> ListTrack(string trackId, bool onlyCurrent)
        {
            var pages = song.Find(trackId);
            if (pages == null) yield break;
            var entry = EntryById(trackId);
            var generator = GeneratorOf(entry);
            for (int pi = 0; pi < pages.Pages.Count; pi++)
            {
                if (onlyCurrent && pi != pages.Current) continue;
                var page = pages.Pages[pi];
                foreach (var (slot, d) in page.Slots)
                    yield return new MapInfo
                    {
                        Scope = "track", TrackId = trackId, Page = pi, Slot = slot, Control = "K" + (slot + 1), Short = "K" + (slot + 1),
                        Target = d.Target, Range = d.RangeText, Invert = d.Invert, Takeover = d.Takeover,
                    };
                foreach (var e in page.Direct) yield return Info(e, "track", trackId, pi, -1);
            }
        }

        static MapInfo Info(MapEntry e, string scope, string trackId, int page, int slot) => new MapInfo
        {
            Scope = scope, TrackId = trackId, Page = page, Slot = slot, Source = e.Source, Control = e.Source.Short(), Short = e.Source.Short(),
            Target = e.Target, Range = e.RangeText, Invert = e.Invert, Takeover = e.Takeover, Encoding = e.Encoding,
        };

        /// <summary>Every mapping in the song and the computer, for the agent: pages of every track, then the global entries, then old bindings.</summary>
        public IReadOnlyList<MapInfo> All(string scope = "all", string trackId = null)
        {
            var list = new List<MapInfo>();
            if (scope != "global")
                foreach (var t in song.Tracks.Select(t => t.TrackId).Where(id => trackId == null || id == trackId))
                    list.AddRange(ListTrack(t, onlyCurrent: false));
            if (scope != "track")
            {
                list.AddRange(ListGlobal());
                list.AddRange(LegacyInfos());
            }
            return list;
        }

        /// <summary>The badge a control with this target shows, in its short form that fits a small control: "K3" (a slot), "CC74", "N36"; null when nothing maps to it.</summary>
        public string BadgeOf(MapTarget target)
        {
            if (target == null) return null;
            foreach (var i in Active())
                if (i.Target != null && i.Target.Equals(target))
                    return i.Slot >= 0 ? i.Short : i.Source.Compact;
            return null;
        }

        /// <summary>What the badge stands for in full, for its tooltip and the popover: "Note 36 ch10 · Pads", or "K3: knob 3 of Lead's page"; null when nothing maps to it.</summary>
        public string BadgeFullOf(MapTarget target)
        {
            if (target == null) return null;
            foreach (var i in Active())
                if (i.Target != null && i.Target.Equals(target))
                    return i.Slot >= 0 ? i.Short + ": knob " + (i.Slot + 1) + " of " + (SelectedTrack?.Name ?? "the track") + "'s page" : i.Source.Short();
            return null;
        }

        /// <summary>The mapping a target has in force (to edit its range), or null.</summary>
        public MapInfo InfoOf(MapTarget target) => target == null ? null : Active().FirstOrDefault(i => i.Target != null && i.Target.Equals(target));

        /// <summary>The page the selected track shows: each knob's slot (null: free), with the generator's pin as a fallback (marked).</summary>
        public IReadOnlyList<MapInfo> PageSlots(int count, string trackId = null)
        {
            trackId ??= SelectedTrackId;
            var result = new List<MapInfo>();
            var entry = EntryById(trackId ?? "");
            var generator = GeneratorOf(entry);
            var pages = trackId == null ? null : song.Find(trackId);
            var page = pages?.CurrentPage;
            var pins = generator != null && buzz.PinStore != null ? buzz.PinStore.Get(generator.Name) : Array.Empty<string>();
            for (int i = 0; i < count; i++)
            {
                if (page != null && page.Slots.TryGetValue(i, out var d))
                    result.Add(new MapInfo { Scope = "track", TrackId = trackId, Page = pages.Current, Slot = i, Control = "K" + (i + 1), Short = "K" + (i + 1), Target = d.Target, Range = d.RangeText, Invert = d.Invert, Takeover = d.Takeover });
                else if (generator != null && i < pins.Count)
                    result.Add(new MapInfo { Scope = "track", TrackId = trackId, Slot = i, Control = "K" + (i + 1), Short = "K" + (i + 1), Target = MapTarget.Parameter(generator.Name, pins[i]), Fallback = true });
                else
                    result.Add(new MapInfo { Scope = "track", TrackId = trackId, Slot = i, Control = "K" + (i + 1), Short = "K" + (i + 1) });
            }
            return result;
        }

        /// <summary>How many knobs the page shows: the largest knob group, at least 8.</summary>
        public int KnobCount => Math.Max(8, computer.Groups.Select(g => g.Count).DefaultIfEmpty(0).Max());

        // ------------------------------------------------------------------ old Buzz bindings (MM1 section 7)

        /// <summary>
        /// Per-parameter Buzz bindings (a channel and a controller number, saved in .bmx and .bmxml) are global entries on "any device": they keep
        /// working as they did (MidiControllerAssignments), and show here as such. Editing one in the popover replaces it with an entry of the new kind.
        /// </summary>
        IEnumerable<MapInfo> LegacyInfos()
        {
            var assignments = buzz.MidiControllerAssignments;
            if (assignments == null) yield break;
            foreach (var b in assignments.ContollerBindings.ToList())
            {
                var machine = b.Parameter.Group?.Machine;
                if (machine == null) continue;
                var groups = machine.ParameterGroups;
                bool track = b.Parameter.Group.Type == ParameterGroupType.Track;
                var source = new MapSource("", MapKind.ControlChange, b.MidiChannel, b.MidiController);
                yield return new MapInfo
                {
                    Scope = "global", Legacy = true, Source = source, Control = source.Short(), Short = source.Short(),
                    Target = MapTarget.Parameter(machine.Name, (track ? MapTarget.TrackPrefix : "") + b.Parameter.Name, track ? b.Track : 0),
                    Takeover = Global_SoftTakeover ? Takeover.Pickup : Takeover.Jump,
                };
            }
        }

        static bool Global_SoftTakeover => BuzzGUI.Common.Global.MIDISettings?.ParameterSoftTakeover ?? true;

        bool RemoveLegacy(MapTarget target)
        {
            var assignments = buzz.MidiControllerAssignments;
            if (assignments == null || target.IsCommand) return false;
            bool removed = false;
            foreach (var b in assignments.ContollerBindings.ToList())
            {
                var m = b.Parameter.Group?.Machine;
                if (m == null || m.Name != target.Machine || b.Parameter.Name != target.ParamName) continue;
                bool track = b.Parameter.Group.Type == ParameterGroupType.Track;
                if (track != target.IsTrackParam || (track && b.Track != target.Track)) continue;
                b.Parameter.BindToMIDIController(b.Track, -1, -1);
                removed = true;
            }
            return removed;
        }

        /// <summary>A parameter of the song for a target (the views' badges and the agent's reads), or null.</summary>
        public IParameter ParameterOf(MapTarget target)
        {
            var core = buzz.SongCore;
            return core == null ? null : ResolveParam(target, core.MachinesList.ToDictionary(m => m.Name, m => m, StringComparer.Ordinal));
        }

        /// <summary>The target a Device panel control maps to: the machine's name, the parameter's key (a track parameter's starts with "T:") and its track.</summary>
        public static MapTarget TargetOf(IParameter parameter, int track)
        {
            var machine = parameter?.Group?.Machine;
            if (machine == null || parameter.Group.Type == ParameterGroupType.Input) return null;
            bool isTrack = parameter.Group.Type == ParameterGroupType.Track;
            return MapTarget.Parameter(machine.Name, (isTrack ? MapTarget.TrackPrefix : "") + parameter.Name, isTrack ? track : 0);
        }
    }
}
