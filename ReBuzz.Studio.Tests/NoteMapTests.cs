// Fork (docs/specs/daw-modes.md §8.9, NM1): note maps, headless. A machine says which keys make a sound and their names, the piano roll
// shows the names on its rows and folds to the playable keys: Neo Sampler (a kit of zones; the keys inside the zones' ranges), Neo Drums
// (its voices), a manifest's declared map, a provider that raises its own change (what Neo Plug does with a hosted plug-in's note names),
// and no map at all (the roll behaves as it always did). Fold: playable keeps every key that holds notes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Tests
{
    public class NoteMapTests
    {
        const int TB = PatternEvent.TimeBase;

        static void Set(IMachine m, string name, int value) =>
            m.ParameterGroups[1].Parameters.First(p => p.Name == name).SetValue(0, value);

        /// <summary>Neo Sampler's Kit mode with the given waves on zones 1, 2, … (the other zones off).</summary>
        static void Kit(IMachine m, params int[] waves)
        {
            Set(m, "Mode", 1);
            Set(m, "Kit base", 36);
            for (int z = 0; z < 16; z++) Set(m, $"Z{z + 1} wave", z < waves.Length ? waves[z] : 0);
        }

        // The built-in samples, by their wave numbers (201 on): Kick, Kick 808, Snare, Clap, Rim, Closed hat, Open hat, …
        const int Kick = 201, Kick808 = 202, Snare = 203, Clap = 204, Rim = 205, ClosedHat = 206, OpenHat = 207;

        // ------------------------------------------------------------------ the model

        [Test]
        public void ASnapshotKnowsWhichKeysPlayAndTheirNamesAndIsImmutable()
        {
            var map = NoteMap.Create(new[]
            {
                new NoteMapEntry(36, "Kick", "Drums", 0xFFE0457B), new NoteMapEntry(38, " Snare "), new NoteMapEntry(40), new NoteMapEntry(200, "outside"), new NoteMapEntry(-1, "outside"),
            }, 7, "machine");
            Assert.That(map.Version, Is.EqualTo(7));
            Assert.That(map.PlayableKeys, Is.EqualTo(new[] { 36, 38, 40 }), "keys outside 0 to 127 are dropped");
            Assert.That(map.NameOf(36), Is.EqualTo("Kick"));
            Assert.That(map.NameOf(38), Is.EqualTo("Snare"), "names are trimmed");
            Assert.That(map.NameOf(40), Is.Null, "a playable key may have no name");
            Assert.That(map.IsPlayable(40) && !map.IsPlayable(39), Is.True);
            Assert.That(map.GroupOf(36), Is.EqualTo("Drums"));
            Assert.That(map.ColorOf(36), Is.EqualTo(0xFFE0457Bu));
            Assert.That(map.NamedCount, Is.EqualTo(2));
            Assert.That(map.HasNames, Is.True);
            Assert.That(map.Ranges(), Is.EqualTo("36, 38, 40"));
            Assert.That(NoteMap.Create(new[] { new NoteMapEntry(60), new NoteMapEntry(61), new NoteMapEntry(62), new NoteMapEntry(70) }, 1, "x").Ranges(), Is.EqualTo("60-62, 70"));
            Assert.That(NoteMap.Create(Array.Empty<NoteMapEntry>(), 3, "x"), Is.SameAs(NoteMap.None), "no keys: no map");
            Assert.That(NoteMap.None.IsEmpty && NoteMap.None.NameOf(60) == null && !NoteMap.None.IsPlayable(60), Is.True);
            Assert.That(map.SameKeysAs(NoteMap.Create(map.Entries(), 99, "other")), Is.True, "the same keys and names, whatever the version");
            Assert.That(map.SameKeysAs(NoteMap.Create(new[] { new NoteMapEntry(36, "Kik") }, 7, "machine")), Is.False);
        }

        [Test]
        public void FoldPlayableKeepsThePlayableKeysAndEveryKeyThatHoldsNotes()
        {
            var map = NoteMap.Create(new[] { new NoteMapEntry(36, "Kick"), new NoteMapEntry(38, "Snare"), new NoteMapEntry(42, "Hat") }, 1, "machine");
            Assert.That(NoteScale.Rows(FoldMode.Playable, Array.Empty<int>(), null, map), Is.EqualTo(new[] { 42, 38, 36 }), "highest first");
            Assert.That(NoteScale.Rows(FoldMode.Playable, new[] { 60, 36, 20 }, null, map), Is.EqualTo(new[] { 60, 42, 38, 36, 20 }), "a note never disappears, though its key makes no sound");
            Assert.That(NoteScale.Rows(FoldMode.Playable, new[] { 60 }, null, NoteMap.None), Is.Null, "no map: all the rows");
            Assert.That(NoteScale.Rows(FoldMode.Playable, new[] { 60 }, null, null), Is.Null);
            Assert.That(NoteScale.Rows(FoldMode.Used, new[] { 60 }, null, map), Is.EqualTo(new[] { 60 }), "the other folds ignore the map");
        }

        // ------------------------------------------------------------------ a provider, as a machine gives it

        /// <summary>A machine that is nothing but its managed part: what <see cref="NoteMaps"/> asks.</summary>
        public class FakeMachine : DispatchProxy
        {
            public IBuzzMachine Managed;

            protected override object Invoke(MethodInfo target, object[] args)
            {
                if (target.Name == "get_ManagedMachine") return Managed;
                return target.ReturnType.IsValueType && target.ReturnType != typeof(void) ? Activator.CreateInstance(target.ReturnType) : null;
            }
        }

        sealed class FakeProvider : IBuzzMachine, INoteMapProvider
        {
            public IReadOnlyList<NoteMapEntry> Entries;
            int version;
            public int NoteMapVersion => version;
            public IReadOnlyList<NoteMapEntry> ReadNoteMap(int channel) => channel == 9 ? new[] { new NoteMapEntry(36, "Channel ten") } : Entries;
            public event Action NoteMapChanged;
            public void Change(params NoteMapEntry[] entries)
            {
                Entries = entries;
                version++;
                NoteMapChanged?.Invoke();
            }
        }

        static (IMachine Machine, FakeProvider Provider) Fake(bool withProvider = true)
        {
            var provider = withProvider ? new FakeProvider() : null;
            var machine = DispatchProxy.Create<IMachine, FakeMachine>();
            ((FakeMachine)(object)machine).Managed = provider;
            return (machine, provider);
        }

        [Test]
        public void AProviderThatChangesIsFollowedAndTheChangeIsRaisedOnce()
        {
            var (machine, provider) = Fake();
            var raised = new List<IMachine>();
            void OnChanged(IMachine m) => raised.Add(m);
            NoteMaps.Changed += OnChanged;
            try
            {
                Assert.That(NoteMaps.For(machine).IsEmpty, Is.True, "no entries: no map");
                Assert.That(NoteMaps.For(machine), Is.SameAs(NoteMap.None));
                provider.Change(new NoteMapEntry(36, "Kick"), new NoteMapEntry(38, "Snare"));
                Assert.That(raised, Has.Count.EqualTo(1).And.All.SameAs(machine), "the provider said so: the change is raised");
                var map = NoteMaps.For(machine);
                Assert.That(map.Source, Is.EqualTo("machine"));
                Assert.That(map.NameOf(38), Is.EqualTo("Snare"));
                int version = map.Version;
                Assert.That(NoteMaps.For(machine), Is.SameAs(map), "nothing changed: the same snapshot");
                NoteMaps.Poll(machine);
                Assert.That(raised, Has.Count.EqualTo(1), "and nothing is raised");
                provider.Change(new NoteMapEntry(36, "Bass drum"));
                var next = NoteMaps.For(machine);
                Assert.That(next.Version, Is.GreaterThan(version));
                Assert.That(next.NameOf(36), Is.EqualTo("Bass drum"));
                Assert.That(next.IsPlayable(38), Is.False, "the old snapshot is untouched, the new one has its own keys");
                Assert.That(map.NameOf(36), Is.EqualTo("Kick"), "a snapshot never changes");
                Assert.That(NoteMaps.For(machine, 9).NameOf(36), Is.EqualTo("Channel ten"), "a provider may answer per MIDI channel");
                Assert.That(NoteMaps.For(machine, 3).NameOf(36), Is.EqualTo("Bass drum"));
            }
            finally { NoteMaps.Changed -= OnChanged; }
        }

        [Test]
        public void TheManifestsMapIsUsedWhenTheMachineHasNoneOfItsOwn()
        {
            var was = NoteMaps.Manifest;
            try
            {
                NoteMaps.Manifest = _ => new[] { new NoteMapEntry(48, "Low"), new NoteMapEntry(49, "High", "Pads") };
                var (plain, _) = Fake(withProvider: false);
                var map = NoteMaps.For(plain);
                Assert.That(map.Source, Is.EqualTo("manifest"));
                Assert.That(map.PlayableKeys, Is.EqualTo(new[] { 48, 49 }));
                Assert.That(map.GroupOf(49), Is.EqualTo("Pads"));

                var (withProvider, provider) = Fake();
                Assert.That(NoteMaps.For(withProvider).Source, Is.EqualTo("manifest"), "a provider with no map (null or empty) leaves it to the manifest");
                provider.Change(new NoteMapEntry(60, "Mine"));
                var own = NoteMaps.For(withProvider);
                Assert.That(own.Source, Is.EqualTo("machine"), "its own map comes first");
                Assert.That(own.PlayableKeys, Is.EqualTo(new[] { 60 }));

                NoteMaps.Manifest = _ => throw new InvalidOperationException("a broken manifest");
                var (broken, _) = Fake(withProvider: false);
                Assert.That(NoteMaps.For(broken).IsEmpty, Is.True, "a manifest that can't be read is no map");
            }
            finally { NoteMaps.Manifest = was; }
        }

        // ------------------------------------------------------------------ Neo Sampler

        [AvaloniaTest]
        public void ANeoSamplerKitOfSixZonesHasSixPlayableNamedKeysAndTheRowsFollowEveryChange()
        {
            using var r = new UxP3bTests.Rig(16, "Neo Sampler");
            var sampler = r.Lead;
            Kit(sampler, Kick, Snare, Clap, Rim, ClosedHat, OpenHat);
            var map = NoteMaps.For(sampler);
            Assert.That(map.Source, Is.EqualTo("machine"));
            Assert.That(map.PlayableKeys, Is.EqualTo(new[] { 36, 37, 38, 39, 40, 41 }), "six zones, six keys from Kit base");
            Assert.That(Enumerable.Range(36, 6).Select(map.NameOf), Is.EqualTo(new[] { "Kick", "Snare", "Clap", "Rim", "Closed hat", "Open hat" }));
            Assert.That(map.IsPlayable(60) || map.IsPlayable(35) || map.IsPlayable(42), Is.False, "keys outside every zone make no sound");

            r.ShowRoll();
            var roll = r.Notes;
            Assert.That(roll.Map.PlayableCount, Is.EqualTo(6));
            Assert.That(roll.KeyColumn, Is.GreaterThan(PianoRoll.KeysWidth), "the key column is wide enough for the names");
            Assert.That(r.Roll.CanFoldPlayable, Is.True);
            r.Roll.FoldBox.SelectedIndex = 3;
            UxP3bTests.Pump();
            Assert.That(roll.Rows, Is.EqualTo(new[] { 41, 40, 39, 38, 37, 36 }), "Fold: playable shows the six keys and nothing else");
            Assert.That(roll.KeyTip(36), Does.StartWith("Kick · C2"));
            Assert.That(roll.KeyTip(60), Is.EqualTo("C4 · no sound"), "keys that make no sound say so");

            // Renaming a zone (another wave on it): the version moves and the same roll follows, with no reopening.
            int version = map.Version;
            Set(sampler, "Z2 wave", Kick808);
            Assert.That(roll.SyncMap(), Is.True);
            Assert.That(roll.Map.Version, Is.GreaterThan(version));
            Assert.That(roll.Map.NameOf(37), Is.EqualTo("Kick 808"));
            Assert.That(roll.Rows, Is.EqualTo(new[] { 41, 40, 39, 38, 37, 36 }));
            Assert.That(roll.SyncMap(), Is.False, "nothing changed since");

            // Adding a zone: a seventh key. Removing one: its key goes.
            Set(sampler, "Z7 wave", ClosedHat + 2);
            Assert.That(roll.SyncMap(), Is.True);
            Assert.That(roll.Rows, Is.EqualTo(new[] { 42, 41, 40, 39, 38, 37, 36 }), "the rows follow an added zone");
            Assert.That(roll.Map.NameOf(42), Is.EqualTo("Low tom"));
            Set(sampler, "Z1 wave", 0);
            Assert.That(roll.SyncMap(), Is.True);
            Assert.That(roll.Rows, Is.EqualTo(new[] { 42, 41, 40, 39, 38, 37 }), "and a removed one");
            Assert.That(roll.Map.IsPlayable(36), Is.False);

            // A note on a key that makes no sound stays on screen: Fold: playable keeps the rows that hold notes.
            roll.AddNote(0, 60);
            UxP3bTests.Pump();
            Assert.That(roll.Rows, Is.EqualTo(new[] { 60, 42, 41, 40, 39, 38, 37 }), "a note never disappears");
            Assert.That(roll.Notes.Any(n => n.Note == 60), Is.True);

            // Fold off: all the keys again, the names still on their rows.
            r.Roll.FoldBox.SelectedIndex = 0;
            UxP3bTests.Pump();
            Assert.That(roll.Rows, Is.Null);
            Assert.That(roll.Map.NameOf(38), Is.EqualTo("Clap"));
        }

        [AvaloniaTest]
        public void ANeoSamplersInstrumentModeSaysWhichKeysItsZonesCover()
        {
            using var r = new UxP3bTests.Rig(16, "Neo Sampler");
            var sampler = r.Lead;
            Set(sampler, "Mode", 0);
            for (int z = 0; z < 16; z++) Set(sampler, $"Z{z + 1} wave", 0);
            Set(sampler, "Z1 wave", Kick);
            Set(sampler, "Z1 key low", 48);
            Set(sampler, "Z1 key high", 59);
            Set(sampler, "Z2 wave", Snare);
            Set(sampler, "Z2 key low", 60);
            Set(sampler, "Z2 key high", 72);
            var map = NoteMaps.For(sampler);
            Assert.That(map.PlayableKeys, Is.EqualTo(Enumerable.Range(48, 25).ToArray()));
            Assert.That(map.HasNames, Is.False, "melodic zones give no key names");
            Assert.That(map.GroupOf(50), Is.EqualTo("Kick"));
            Assert.That(map.GroupOf(65), Is.EqualTo("Snare"));
            Assert.That(map.IsPlayable(47) || map.IsPlayable(73), Is.False);
            r.ShowRoll();
            Assert.That(r.Notes.KeyColumn, Is.EqualTo(PianoRoll.KeysWidth), "no names: the key column is as it was");
            r.Roll.FoldBox.SelectedIndex = 3;
            UxP3bTests.Pump();
            Assert.That(r.Notes.Rows, Is.EqualTo(Enumerable.Range(48, 25).Reverse().ToArray()));
        }

        // ------------------------------------------------------------------ Neo Drums

        [AvaloniaTest]
        public void NeoDrumsNamesItsVoicesOnTheirKeys()
        {
            using var r = new UxP3bTests.Rig(16, "Neo Drums");
            var map = NoteMaps.For(r.Lead);
            Assert.That(map.Source, Is.EqualTo("machine"));
            Assert.That(new[] { 36, 38, 39, 37, 42, 46, 43, 47, 50, 56, 49, 51 }.Select(map.NameOf),
                Is.EqualTo(new[] { "Kick", "Snare", "Clap", "Rim", "Closed hat", "Open hat", "Low tom", "Mid tom", "High tom", "Cowbell", "Crash", "Ride" }));
            Assert.That(map.NameOf(35), Is.EqualTo("Kick"), "the second key of a pair");
            Assert.That(map.IsPlayable(60) || map.IsPlayable(20), Is.False);
            Assert.That(map.GroupOf(42), Is.EqualTo("Hats"));
            Assert.That(map.ColorOf(36), Is.Not.EqualTo(0u));
            Assert.That(NoteMaps.For(r.Lead, 9), Is.SameAs(NoteMaps.For(r.Lead, 9)), "snapshots are cached per channel");
            r.ShowRoll();
            Assert.That(r.Notes.Map.PlayableCount, Is.EqualTo(map.PlayableCount));
            r.Roll.FoldBox.SelectedIndex = 3;
            UxP3bTests.Pump();
            Assert.That(r.Notes.Rows, Is.EqualTo(map.PlayableKeys.Reverse().ToArray()));
            Assert.That(r.Notes.KeyColumn, Is.GreaterThan(PianoRoll.KeysWidth));
        }

        // ------------------------------------------------------------------ no map

        [AvaloniaTest]
        public void AMachineWithNoMapLeavesTheRollAsItWasAndFoldPlayableDisabled()
        {
            using var r = new UxP3bTests.Rig(16);       // Neo Poly: it plays every key
            Assert.That(NoteMaps.For(r.Lead).IsEmpty, Is.True);
            r.ShowRoll();
            Assert.That(r.Notes.Map.IsEmpty, Is.True);
            Assert.That(r.Notes.KeyColumn, Is.EqualTo(PianoRoll.KeysWidth));
            Assert.That(r.Roll.CanFoldPlayable, Is.False);
            var item = (ComboBoxItem)r.Roll.FoldBox.Items[3]!;
            Assert.That(item.IsEnabled, Is.False);
            Assert.That(ToolTip.GetTip(item), Is.EqualTo("This machine reports no playable keys"));
            Assert.That(ToolTip.GetTip(r.Roll.FoldBox), Is.EqualTo("This machine reports no playable keys"));
            Assert.That(r.Roll.FoldBox.Items.Cast<object>().Select(i => i is ComboBoxItem c ? c.Content : i), Is.EqualTo(new object[] { "Fold: off", "Fold: used", "Fold: scale", "Fold: playable" }));
            r.Notes.AddNote(0, 55);
            r.Roll.FoldBox.SelectedIndex = 1;
            UxP3bTests.Pump();
            Assert.That(r.Notes.Rows, Is.EqualTo(new[] { 55 }), "the other folds work as they did");
            r.Roll.FoldBox.SelectedIndex = 3;       // (if it were chosen anyway: every row)
            UxP3bTests.Pump();
            Assert.That(r.Notes.Rows, Is.Null);
            Assert.That(r.Notes.KeyTip(60), Is.EqualTo("C4"), "a plain key's tip is its note");
        }
    }
}
