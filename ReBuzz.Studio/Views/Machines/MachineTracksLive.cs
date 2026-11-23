// Fork (docs/specs/ux-overhaul.md §11, MT1): the song's track model, kept up to date for the views. One per engine, like StudioSelection.
// The Machines view reads it today; the Mixer and the Arrangement headers may use the same one later. It follows the song's machine, wire
// and rename events and works the model out again once per burst of them, on the UI thread, so loading a song or pasting twenty machines
// costs one pass, not twenty. While nothing holds it (Retain) it is not listening: the model is worked out when asked.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Tracks;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Machines
{
    internal sealed class MachineTracksLive
    {
        static readonly ConditionalWeakTable<ReBuzzCore, MachineTracksLive> all = new();

        /// <summary>The track model of a song's window (one per engine).</summary>
        public static MachineTracksLive For(ReBuzzCore core) => all.GetValue(core, c => new MachineTracksLive(c));

        readonly ReBuzzCore core;
        readonly HashSet<IMachine> watched = new();
        SongCore song;
        TrackModel model;
        // The signature of the model now, and of the one the listeners were last told of (a read that works the model out first must not
        // hide the change from them).
        string signature = "", announced;
        bool dirty = true, posted;
        int users;

        MachineTracksLive(ReBuzzCore core) => this.core = core;

        /// <summary>The model of the song now (worked out at once when an event has made the last one stale).</summary>
        public TrackModel Model
        {
            get
            {
                if (model == null || dirty || users == 0) Recompute();
                return model;
            }
        }

        /// <summary>How many times the model was worked out (tests: a burst of events is one pass).</summary>
        public int Passes { get; private set; }

        /// <summary>The model changed its lists, its members or a name (debounced: once per burst, on the UI thread).</summary>
        public event Action Changed;

        public IMachine MachineOf(TrackNode node) => node?.Tag as IMachine;

        /// <summary>A view that shows the model holds it: the events are listened to until every holder has released it.</summary>
        public void Retain()
        {
            if (users++ > 0) return;
            announced = null;
            song = core.SongCore;
            if (song == null) return;
            song.MachineAdded += OnMachine;
            song.MachineRemoved += OnMachine;
            song.ConnectionAdded += OnWire;
            song.ConnectionRemoved += OnWire;
            foreach (var m in song.Machines) Watch(m);
            Touch();
        }

        public void Release()
        {
            if (users == 0 || --users > 0) return;
            if (song != null)
            {
                song.MachineAdded -= OnMachine;
                song.MachineRemoved -= OnMachine;
                song.ConnectionAdded -= OnWire;
                song.ConnectionRemoved -= OnWire;
            }
            foreach (var m in watched) m.PropertyChanged -= OnMachineChanged;
            watched.Clear();
            song = null;
        }

        void Watch(IMachine m)
        {
            if (watched.Add(m)) m.PropertyChanged += OnMachineChanged;
        }

        void OnMachine(IMachine m)
        {
            if (m != null) Watch(m);
            Touch();
        }

        void OnWire(IMachineConnection c) => Touch();

        void OnMachineChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Name") Touch();
        }

        /// <summary>Something that can change the tracks happened (on any thread): work them out once the burst is over.</summary>
        void Touch()
        {
            dirty = true;
            if (posted) return;
            posted = true;
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Loaded);
        }

        void Flush()
        {
            posted = false;
            if (users == 0) return;
            Recompute();
            if (signature == announced) return;
            announced = signature;
            Changed?.Invoke();
        }

        void Recompute()
        {
            dirty = false;
            try
            {
                model = SongTracks.Of(core.SongCore);
            }
            catch (InvalidOperationException)
            {
                // A machine added on the engine's thread while the list was read: the next pass has it.
                dirty = true;
                if (model == null) model = TrackModel.Compute(new TrackGraph());
                return;
            }
            Passes++;
            signature = Signature(model);
        }

        /// <summary>Everything the chips and the paint depend on, as text: equal text, nothing to redraw for.</summary>
        internal static string Signature(TrackModel m)
        {
            var sb = new StringBuilder();
            foreach (var e in m.Entries)
            {
                sb.Append(e.Id).Append('|').Append(string.Join(",", e.Machines.Select(x => x.Name))).Append('|')
                  .Append(string.Join(",", e.Members.Select(x => x.Id))).Append('|').Append(string.Join(",", e.Senders.Select(x => x.Id))).Append(';');
            }
            sb.Append('#');
            foreach (var u in m.Unrouted) sb.Append(u.Node.Name).Append(':').Append((int)u.Why).Append(';');
            return sb.ToString();
        }
    }

    /// <summary>The Tracks toggle of the Machines view, kept in the settings registry (Studio/MachineTracks); off until the user turns it on.</summary>
    internal static class MachineTracksPreference
    {
        public const string Key = "MachineTracks", Section = "Studio";

        public static bool On
        {
            get
            {
                try { return StudioHost.Current?.Registry?.Read(Key, false, Section) ?? false; }
                catch (Exception) { return false; }
            }
            set
            {
                if (On == value) return;
                try { StudioHost.Current?.Registry?.Write(Key, value, Section); }
                catch (Exception) { /* a registry that can't be written: the choice lasts this session */ }
                Changed?.Invoke(value);
            }
        }

        /// <summary>The choice changed (any open Machines view follows).</summary>
        public static event Action<bool> Changed;
    }
}
