// Fork (docs/specs/daw-modes.md AR4, §4.3): knob moves recorded into automation lanes. The spec names no
// write, touch or latch modes, so there is one simple one: a lane armed for recording ("write") takes every
// move of its parameter while the song plays, as a point at the play position in the automation clip under
// it (a clip is drawn from the bar the move is in when there is none), a ramp to the next. A move replaces
// what the pass sweeps over; points less than a quarter tick apart are one point. One pass, from the first
// move until the transport stops (or the lane is disarmed), is one step of the song's history.
// UI thread only (the same moves the Session View records: ReBuzzCore.RecordControlChange); nothing here
// runs on the audio thread.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core
{
    internal sealed class AutomationRecorder
    {
        sealed class Pass
        {
            public int Last = int.MinValue;     // pattern time of the last point written
            public IPattern Pattern;
        }

        const int Quarter = PatternEvent.TimeBase / 4;

        readonly ReBuzzCore buzz;
        readonly HashSet<(string Machine, ColumnKey Key)> armed = new();
        readonly Dictionary<(string Machine, ColumnKey Key), Pass> passes = new();
        bool grouped;

        public AutomationRecorder(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.PropertyChanged += OnBuzzChanged;
        }

        /// <summary>An arm changed (headers redraw).</summary>
        public event Action Changed;

        public bool IsArmed(string machine, ColumnKey key) => armed.Contains((machine, key));

        public bool Any => armed.Count > 0;

        public void SetArmed(string machine, ColumnKey key, bool on)
        {
            if (on ? armed.Add((machine, key)) : armed.Remove((machine, key)))
            {
                if (!on) EndPass();
                Changed?.Invoke();
            }
        }

        public void Disarm(string machine, ColumnKey key) => SetArmed(machine, key, false);

        public void Clear()
        {
            EndPass();
            if (armed.Count == 0) return;
            armed.Clear();
            Changed?.Invoke();
        }

        void OnBuzzChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Playing" || buzz.Playing || !grouped) return;
            if (buzz.dispatcher.CheckAccess()) EndPass();
            else buzz.dispatcher.BeginInvoke(EndPass);
        }

        void EndPass()
        {
            passes.Clear();
            if (!grouped) return;
            grouped = false;
            buzz.SongCore.ActionStack.EndActionGroup();
        }

        /// <summary>A parameter was moved on the UI thread (or from a native machine's window): armed lanes of it take the value.</summary>
        internal void OnParameterChange(ParameterCore parameter, int track, int value)
        {
            if (armed.Count == 0 || !buzz.Playing || parameter?.Group?.Machine is not IMachine machine) return;
            if (ReBuzzCore.AudioLock.IsHeldByCurrentThread || !(buzz.dispatcher.CheckAccess() || SessionCore.MoveFromEditor) || buzz.SongLoading) return;
            int g = -1;
            var groups = machine.ParameterGroups;
            for (int i = 0; i < groups.Count && g < 0; i++)
                if (ReferenceEquals(groups[i], parameter.Group)) g = i;
            if (g < 0) return;
            var key = new ColumnKey(g, parameter.IndexInGroup, groups[g].Type == ParameterGroupType.Track ? track : 0);
            if (!armed.Contains((machine.Name, key))) return;
            try
            {
                Write(machine, key, value);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NullReferenceException || ex is KeyNotFoundException)
            {
                buzz.DCWriteLine("Automation recording: " + ex.Message, DCLogLevel.Warning);
            }
        }

        /// <summary>The play position in song time (1/240 tick).</summary>
        int Now()
        {
            var info = ReBuzzCore.masterInfo;
            int fraction = info != null && info.SamplesPerTick > 0 ? info.PosInTick * PatternEvent.TimeBase / info.SamplesPerTick : 0;
            return buzz.Song.PlayPosition * PatternEvent.TimeBase + Math.Clamp(fraction, 0, PatternEvent.TimeBase - 1);
        }

        void Write(IMachine machine, ColumnKey key, int value)
        {
            int now = Now();
            var stack = buzz.SongCore.ActionStack;
            if (!grouped)
            {
                stack.BeginActionGroup();
                stack.Label("Record automation");
                grouped = true;
            }
            var editor = new AutomationEditor(buzz, new ClipEditor(buzz));
            var seg = Under(machine, key, now);
            if (seg == null)
            {
                int start = BarStart(now / PatternEvent.TimeBase);
                int length = Math.Max(BarLength(start), buzz.Song.SongEnd - start);
                editor.DrawClip(machine, key, start, length, value, value, out _, withPoints: false);
                seg = Under(machine, key, now);
                if (seg == null) return;
            }
            var id = (machine.Name, key);
            if (!passes.TryGetValue(id, out var pass) || !ReferenceEquals(pass.Pattern, seg.Pattern))
                passes[id] = pass = new Pass { Pattern = seg.Pattern };
            int t = Math.Clamp(seg.ToPattern(now), 0, seg.Pattern.Length * PatternEvent.TimeBase - 1);
            int last = pass.Last;
            editor.Edit(seg, key, points =>
            {
                // Replace what the pass sweeps over; a move within a quarter tick of the last takes that point's place.
                int from = last == int.MinValue ? t : (t - last < Quarter ? last : last + 1);
                points.RemoveAll(p => p.Time >= from && p.Time <= t);
                points.Add(new AutoPoint(t, value, true));
                return points;
            });
            pass.Last = t;
        }

        AutoSegment Under(IMachine machine, ColumnKey key, int now) =>
            AutomationReader.Read(buzz, machine, key).Segments.FirstOrDefault(s => s.Source == AutoSource.AutomationClip && s.Editable && s.Covers(now));

        int BarStart(int tick)
        {
            int start = 0;
            foreach (var (s, l) in buzz.SongMeter.GetBars(tick + 2048))
            {
                if (s > tick) break;
                start = s;
            }
            return start;
        }

        int BarLength(int start)
        {
            foreach (var (s, l) in buzz.SongMeter.GetBars(start + 2048))
                if (s == start) return Math.Max(1, l);
            return 16;
        }
    }
}
