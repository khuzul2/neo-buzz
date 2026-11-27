// Fork (docs/specs/machines.md §3.6, PRS1): a user's own presets live in their own folder (the settings folder's Presets), the factory
// ones in Gear are read-only. The store (BuzzGUI.Common's PresetStore) is tested on files in a temp folder; the machine, the Presets
// dialog, the agent's preset tools and the browser on a real machine in the studio's engine.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using NeoBuzz.TestSupport;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Parameters;
using ReBuzz.Studio.Views.Browser;

namespace ReBuzz.Studio.Tests
{
    public class PresetStoreTests
    {
        string oldRoot;
        string root, gear;

        [SetUp]
        public void SetUp()
        {
            oldRoot = PresetStore.RootFolder;
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            root = TestTemp.Path("prs1-" + id, "settings");
            gear = TestTemp.Path("prs1-" + id, "Gear", "Effects");
            Directory.CreateDirectory(gear);
            PresetStore.RootFolder = root;
            PresetStore.GearWritesBlocked = false;
        }

        [TearDown]
        public void TearDown()
        {
            PresetStore.RootFolder = oldRoot;
            PresetStore.GearWritesBlocked = false;
            Unlock(gear);
        }

        // ------------------------------------------------------------------ helpers

        const string Dll = "Neo Test";

        static Preset Make(string comment, int value) => new Preset
        {
            Machine = Dll,
            Comment = comment,
            Parameters = new List<Preset.Parameter> { new Preset.Parameter("Cutoff", 1, 0, 0, value) },
            Attributes = new List<Preset.Attribute>(),
        };

        /// <summary>A Gear folder as the build ships it: the machine's .prs.xml and its factory snapshot, the same bytes.</summary>
        string ShipGear(params string[] names)
        {
            var d = new PresetDictionary();
            int i = 0;
            foreach (var n in names) d[n] = Make("factory " + n, 100 + i++);
            string file = Path.Combine(gear, Dll + ".prs.xml");
            d.Save(file);
            File.Copy(file, PresetStore.SnapshotFile(gear, Dll), true);
            return file;
        }

        /// <summary>An older build saved these into the Gear file (and changed one of the factory presets, when asked).</summary>
        void OldBuildSaved(string file, params string[] names)
        {
            var d = PresetDictionary.LoadXml(Dll, file);
            foreach (var n in names) d[n] = Make("mine " + n, 7);
            d.Save(file);
        }

        internal static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

        /// <summary>Every file under Gear with its hash: nothing may be added, removed or changed.</summary>
        string GearState() => string.Join("\n", Directory.GetFiles(Path.GetDirectoryName(gear), "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).Select(f => f + " " + Hash(f)));

        static void Lock(string dir)
        {
            foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.ReadOnly);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        static void Unlock(string dir)
        {
            if (!Directory.Exists(dir)) return;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, File.GetAttributes(f) & ~FileAttributes.ReadOnly);
        }

        /// <summary>Whether the OS really keeps this process out of the folder (it doesn't for a root user).</summary>
        static bool Enforced(string dir)
        {
            string probe = Path.Combine(dir, "probe-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try { File.WriteAllText(probe, "x"); File.Delete(probe); return false; }
            catch (Exception) { return true; }
        }

        // ------------------------------------------------------------------ gate: a save writes only the user folder

        [Test]
        public void SavingWritesOnlyToTheUserFolderAndGearIsUnchangedByHash()
        {
            ShipGear("Pad — Warm", "Bass");
            string before = GearState();
            var presets = PresetStore.For(gear, Dll);

            presets.Save("My lead", Make("mine", 5));
            presets.Save("Bass", Make("my bass", 6));           // a factory name: a user copy
            Assert.That(presets.Delete("My lead"), Is.True);
            presets.Save("Pad — Mine", Make("mine", 9));
            var import = new PresetDictionary { ["Imported"] = Make("imported", 3) };
            Assert.That(presets.Import(import), Is.EqualTo(1));

            Assert.That(GearState(), Is.EqualTo(before), "Gear's bytes: nothing written, nothing added, no temp file");
            Assert.That(File.Exists(PresetStore.UserFile(Dll)), Is.True);
            Assert.That(PresetStore.UserFile(Dll), Does.StartWith(Path.Combine(root, "Presets")), "in the settings folder's Presets");
            Assert.That(Directory.GetFiles(PresetStore.UserFolder).Select(Path.GetFileName), Has.None.EndsWith(".tmp"), "the atomic write leaves no temp file");
            var user = PresetDictionary.LoadXml(Dll, PresetStore.UserFile(Dll));
            Assert.That(user.Keys, Is.EquivalentTo(new[] { "Bass", "Pad — Mine", "Imported" }), "the same file format, readable as any .prs.xml");
        }

        // ------------------------------------------------------------------ gate: together, shadowing, un-shadowing

        [Test]
        public void FactoryAndUserPresetsListTogetherAndAUserCopyShadowsUntilItIsDeleted()
        {
            ShipGear("Pad — Warm", "Bass");
            var presets = PresetStore.For(gear, Dll);
            Assert.That(presets.Names, Is.EquivalentTo(new[] { "Bass", "Pad — Warm" }));

            presets.Save("My lead", Make("mine", 5));
            presets.Save("Pad — Mine", Make("mine", 6));
            Assert.That(presets.Names, Is.EquivalentTo(new[] { "Bass", "Pad — Warm", "My lead", "Pad — Mine" }), "together");
            Assert.That(presets.IsUser("My lead"), Is.True);
            Assert.That(presets.IsUser("Bass"), Is.False, "factory ones are not marked");
            Assert.That(presets.GroupOf("My lead"), Is.EqualTo("User"), "a user preset without a group is in User");
            Assert.That(presets.GroupOf("Pad — Mine"), Is.EqualTo("Pad"), "one with its own group keeps it");
            Assert.That(presets.GroupOf("Bass"), Is.EqualTo(""));

            // Same name as a factory preset: a user copy that shadows it.
            var factoryBass = presets["Bass"];
            presets.Save("Bass", Make("my bass", 77));
            Assert.That(presets.Names.Count(n => n == "Bass"), Is.EqualTo(1));
            Assert.That(presets["Bass"].Comment, Is.EqualTo("my bass"));
            Assert.That(presets.IsUser("Bass") && presets.IsFactory("Bass") && presets.Shadows("Bass"), Is.True);

            // A reload (a new start) sees the same.
            Assert.That(PresetStore.For(gear, Dll)["Bass"].Comment, Is.EqualTo("my bass"));

            // Deleting the copy brings the factory preset back.
            Assert.That(presets.Delete("Bass"), Is.True);
            Assert.That(presets["Bass"].Comment, Is.EqualTo("factory Bass"));
            Assert.That(presets.IsUser("Bass"), Is.False);
            Assert.That(factoryBass.Comment, Is.EqualTo("factory Bass"));

            // A factory preset alone can't be deleted, and a delete that finds none says so.
            Assert.That(presets.Delete("Bass"), Is.False);
            Assert.That(presets.Delete("Pad — Warm"), Is.False);
            Assert.That(presets.Names, Does.Contain("Bass").And.Contain("Pad — Warm"));
            Assert.That(presets.Delete("nothing"), Is.False);
        }

        // ------------------------------------------------------------------ gate: migration

        [Test]
        public void MigrationMovesExactlyTheTwoPresetsAnOlderBuildAddedAndNothingMovesTwice()
        {
            string file = ShipGear("Pad — Warm", "Bass");
            byte[] factoryBytes = File.ReadAllBytes(file);
            OldBuildSaved(file, "Mine A", "Pad — Mine B");

            var presets = PresetStore.For(gear, Dll);       // the first start of the machine: the migration runs

            var user = PresetDictionary.LoadXml(Dll, PresetStore.UserFile(Dll));
            Assert.That(user.Keys, Is.EquivalentTo(new[] { "Mine A", "Pad — Mine B" }), "exactly the two");
            Assert.That(user["Mine A"].Comment, Is.EqualTo("mine Mine A"));
            Assert.That(presets.Names, Is.EquivalentTo(new[] { "Pad — Warm", "Bass", "Mine A", "Pad — Mine B" }));
            Assert.That(presets.IsUser("Mine A") && presets.IsUser("Pad — Mine B"), Is.True);
            Assert.That(presets.IsFactory("Mine A"), Is.False);
            Assert.That(File.ReadAllBytes(file), Is.EqualTo(factoryBytes), "writable Gear is set back to the factory set");

            // The second start moves nothing: the record says so, and a preset the user deleted meanwhile stays deleted.
            presets.Delete("Mine A");
            OldBuildSaved(file, "Mine A");                  // (as if it came back: a second run would move it again)
            var again = PresetStore.Migrate(gear, Dll);
            Assert.That(again.Moved, Is.Empty);
            Assert.That(again.Skipped, Is.EqualTo("done before"));
            Assert.That(PresetStore.Migrate(gear, Dll).Moved, Is.Empty);
            Assert.That(PresetStore.MigrateAll(Path.GetDirectoryName(gear)), Is.EqualTo(0));
            Assert.That(PresetDictionary.LoadXml(Dll, PresetStore.UserFile(Dll)).Keys, Is.EquivalentTo(new[] { "Pad — Mine B" }));
        }

        [Test]
        public void MigrationAlsoMovesAFactoryPresetTheUserChangedAndKeepsAUserPresetAlreadyInTheUserFile()
        {
            string file = ShipGear("Pad — Warm", "Bass");
            var d = PresetDictionary.LoadXml(Dll, file);
            d["Bass"] = Make("my changed bass", 55);        // a changed factory preset
            d["Mine"] = Make("old mine", 1);
            d.Save(file);
            new PresetDictionary { ["Mine"] = Make("new mine", 2) }.Save(PresetStore.UserFile(Dll));    // already theirs

            var result = PresetStore.Migrate(gear, Dll);

            Assert.That(result.Moved, Is.EquivalentTo(new[] { "Bass" }), "the changed one moved; Mine was theirs already");
            var presets = PresetStore.For(gear, Dll);
            Assert.That(presets["Mine"].Comment, Is.EqualTo("new mine"), "never replaced");
            Assert.That(presets["Bass"].Comment, Is.EqualTo("my changed bass"));
            Assert.That(presets.Shadows("Bass"), Is.True, "a user copy of the factory preset");
        }

        [Test]
        public void MigrationDoesNothingWithoutAFactorySnapshotAndTriesAgainLater()
        {
            string file = ShipGear("Bass");
            File.Delete(PresetStore.SnapshotFile(gear, Dll));
            OldBuildSaved(file, "Mine");
            Assert.That(PresetStore.Migrate(gear, Dll).Skipped, Is.EqualTo("no snapshot"));
            Assert.That(File.Exists(PresetStore.UserFile(Dll)), Is.False, "nothing can be told from the factory set: nothing moves");
            File.Copy(file, PresetStore.SnapshotFile(gear, Dll));    // (a snapshot with everything: nothing is extra)
            Assert.That(PresetStore.Migrate(gear, Dll).Moved, Is.Empty);
        }

        [Test]
        public void NothingIsLostWhenGearIsReadOnlyTheUserPresetsAreCopiedAndTheGearFileIsLeftAsItIs()
        {
            string file = ShipGear("Bass");
            OldBuildSaved(file, "Mine A", "Mine B");
            string before = GearState();
            Lock(gear);
            if (!Enforced(gear)) PresetStore.GearWritesBlocked = true;      // a root user can write anywhere: stand in for the OS

            var presets = PresetStore.For(gear, Dll);

            Assert.That(GearState(), Is.EqualTo(before), "the Gear file is left as it is");
            Assert.That(PresetDictionary.LoadXml(Dll, PresetStore.UserFile(Dll)).Keys, Is.EquivalentTo(new[] { "Mine A", "Mine B" }), "copied");
            Assert.That(presets.IsUser("Mine A") && presets.IsUser("Mine B"), Is.True);
            Assert.That(presets.IsFactory("Mine A"), Is.False, "what the Gear file still holds is not taken for factory presets");
            Assert.That(presets.Delete("Mine A"), Is.True);
            Assert.That(presets.Names, Is.EquivalentTo(new[] { "Bass", "Mine B" }), "and deleting it removes it");
            Assert.That(PresetStore.Migrate(gear, Dll).Moved, Is.Empty, "not again");
        }

        // ------------------------------------------------------------------ gate: a corrupt user file

        [Test]
        public void ACorruptUserFileKeepsTheFactoryPresetsLoadingIsSetAsideAndANewSaveWorks()
        {
            ShipGear("Pad — Warm", "Bass");
            Directory.CreateDirectory(PresetStore.UserFolder);
            string bad = "<PresetDictionary><Item Key=\"x\"><Pres";
            File.WriteAllText(PresetStore.UserFile(Dll), bad);

            var presets = PresetStore.For(gear, Dll);

            Assert.That(presets.Names, Is.EquivalentTo(new[] { "Pad — Warm", "Bass" }), "the factory presets still load");
            Assert.That(File.ReadAllText(PresetStore.UserFile(Dll) + ".bad"), Is.EqualTo(bad), "the corrupt file is kept aside as .bad");
            Assert.That(File.Exists(PresetStore.UserFile(Dll)), Is.False);
            Assert.That(presets.Problems, Has.Count.EqualTo(1));
            Assert.That(presets.Problems[0], Does.Contain(".bad"));

            presets.Save("After", Make("after", 4));
            Assert.That(PresetDictionary.LoadXml(Dll, PresetStore.UserFile(Dll)).Keys, Is.EquivalentTo(new[] { "After" }), "a new save works");
            Assert.That(PresetStore.For(gear, Dll).Names, Does.Contain("After").And.Contain("Bass"));

            // A second corrupt file doesn't replace the first .bad.
            File.WriteAllText(PresetStore.UserFile(Dll), "not xml at all");
            PresetStore.For(gear, Dll);
            Assert.That(File.ReadAllText(PresetStore.UserFile(Dll) + ".bad"), Is.EqualTo(bad));
            Assert.That(File.ReadAllText(PresetStore.UserFile(Dll) + ".bad2"), Is.EqualTo("not xml at all"));
        }

        [Test]
        public void ACorruptFactoryFileDoesNotStopTheUsersPresets()
        {
            string file = ShipGear("Bass");
            File.Delete(PresetStore.SnapshotFile(gear, Dll));
            File.WriteAllText(file, "garbage");
            new PresetDictionary { ["Mine"] = Make("mine", 2) }.Save(PresetStore.UserFile(Dll));
            var presets = PresetStore.For(gear, Dll);
            Assert.That(presets.Names, Is.EquivalentTo(new[] { "Mine" }));
            Assert.That(presets.Problems, Has.Count.EqualTo(1));
        }

        // ------------------------------------------------------------------ gate: the Mac path

        [Test]
        public void WithTheAppFolderReadOnlySavingStillWorksAndTheAppFolderIsUntouched()
        {
            ShipGear("Pad — Warm", "Bass");
            string before = GearState();
            Lock(gear);

            var presets = PresetStore.For(gear, Dll);
            presets.Save("Mine", Make("mine", 5));
            presets.Save("Bass", Make("my bass", 6));
            Assert.That(presets.Delete("Mine"), Is.True);

            Assert.That(GearState(), Is.EqualTo(before));
            Assert.That(PresetStore.For(gear, Dll)["Bass"].Comment, Is.EqualTo("my bass"));
            if (!Enforced(gear)) Assert.Pass("(a root user can write to a read-only folder: the saves above went to the user folder only, which GearState shows)");
        }
    }

    /// <summary>The same on a real machine, the Presets dialog and the agent's tools (the studio's engine).</summary>
    public class PresetMachineTests
    {
        static string Hash(string file) => PresetStoreTests.Hash(file);

        static ReBuzzCore Core => Studio.Host.Core;

        static MachineCore Add(string machine, string name)
        {
            Core.SongCore.CreateMachine(machine, null, name, null, null, null, -1, 0.3f, 0.3f);
            return Core.SongCore.MachinesList.First(m => m.Name == name);
        }

        static void Remove(MachineCore m)
        {
            Core.UiHost.ReleaseMachine(m);
            Core.SongCore.DeleteMachines(new[] { m });
        }

        static string GearFileOf(IMachine m) => Path.Combine(PresetStore.GearFolderOf(m), m.DLL.Name + ".prs.xml");

        [AvaloniaTest]
        public void TheMachinesSaveWritesTheUserFolderAndNeverItsGearFile()
        {
            var m = Add("Neo Filter", "PRS1 Machine");
            try
            {
                string gearFile = GearFileOf(m);
                Assert.That(File.Exists(gearFile), Is.True, "the build ships the factory file");
                Assert.That(File.Exists(PresetStore.SnapshotFile(PresetStore.GearFolderOf(m), m.DLL.Name)), Is.True, "and its factory snapshot");
                string hash = Hash(gearFile);
                var factory = m.GetPresetNames().ToList();
                Assert.That(factory.Count, Is.GreaterThan(3));
                string some = factory.First(n => n != "<default>");

                m.SetPreset("PRS1 Mine", new Preset(m, false, true) { Comment = "mine" });
                m.SetPreset(some, new Preset(m, false, true) { Comment = "my copy" });

                Assert.That(Hash(gearFile), Is.EqualTo(hash), "Gear's bytes are the same");
                Assert.That(File.Exists(Path.Combine(PresetStore.UserFolder, m.DLL.Name + ".prs.xml")), Is.True);
                Assert.That(m.GetPresetNames(), Is.SupersetOf(factory.Append("PRS1 Mine")), "factory and user together");
                Assert.That(m.IsUserPreset("PRS1 Mine") && m.IsUserPreset(some), Is.True);
                Assert.That(m.GetPresetGroup("PRS1 Mine"), Is.EqualTo("User"));
                Assert.That(m.GetPreset(some).Comment, Is.EqualTo("my copy"), "the user copy shadows");

                m.SetPreset(some, null);
                Assert.That(m.GetPreset(some).Comment, Is.Not.EqualTo("my copy"), "deleting it brings the factory one back");
                Assert.That(m.GetPresetNames(), Does.Contain(some));
                m.SetPreset("PRS1 Mine", null);
                Assert.That(m.GetPresetNames(), Does.Not.Contain("PRS1 Mine"));
                Assert.That(Hash(gearFile), Is.EqualTo(hash));
            }
            finally { Remove(m); }
        }

        [AvaloniaTest]
        public void ASongKeepsItsValuesWhateverHappensToThePresetItWasSetFrom()
        {
            var m = Add("Neo Filter", "PRS1 Song");
            string path = TestTemp.Path("prs1-song-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".nbl");
            try
            {
                string some = m.GetPresetNames().First(n => n != "<default>");
                m.GetPreset(some).Apply(m, true);
                m.SetPreset("PRS1 Gone", new Preset(m, false, true));
                int[] Values() => m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.Where(p => p.Flags.HasFlag(ParameterFlags.State)).Select(p => p.GetValue(0)).ToArray();
                var saved = Values();
                Core.SaveSongCopy(path);

                // The user's presets are another folder now, empty: the song's values come from the song.
                string old = PresetStore.RootFolder;
                PresetStore.RootFolder = TestTemp.Path("prs1-empty-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                try
                {
                    new Preset(m, true, false).Apply(m, false);
                    Core.Modified = false;
                    Assert.That(Core.TryOpenSongFile(path), Is.True);
                    var again = Core.SongCore.MachinesList.First(x => x.Name == "PRS1 Song");
                    Assert.That(again.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.Where(p => p.Flags.HasFlag(ParameterFlags.State)).Select(p => p.GetValue(0)).ToArray(),
                        Is.EqualTo(saved), "the song stores the values, not the preset");
                    Assert.That(again.GetPresetNames(), Does.Not.Contain("PRS1 Gone"));
                    Remove(again);
                }
                finally { PresetStore.RootFolder = old; }
                m.SetPreset("PRS1 Gone", null);
            }
            finally
            {
                var left = Core.SongCore.MachinesList.FirstOrDefault(x => x.Name == "PRS1 Song");
                if (left != null) Remove(left);
            }
        }

        // ------------------------------------------------------------------ the Presets dialog

        [AvaloniaTest]
        public void ThePresetsDialogListsBothMarksTheUsersAndNeverDeletesOrOverwritesAFactoryPreset()
        {
            var m = Add("Neo Filter", "PRS1 Dialog");
            try
            {
                string gearFile = GearFileOf(m);
                string hash = Hash(gearFile);
                string factory = m.GetPresetNames().First(n => n != "<default>");
                int factoryCount = m.GetPresetNames().Count();
                var w = new PresetsWindow(m);

                w.PresetName = "PRS1 Dialog Mine";
                w.Comment = "dialog";
                w.Add();
                Assert.That(m.IsUserPreset("PRS1 Dialog Mine"), Is.True);
                Assert.That(new PresetsWindow(m).Names, Does.Contain("PRS1 Dialog Mine").And.Contain(factory), "both, together");

                // A factory preset can't be deleted: the delete does nothing.
                w = new PresetsWindow(m);
                w.Select(factory);
                w.Delete();
                Assert.That(m.GetPresetNames(), Does.Contain(factory));
                Assert.That(m.GetPresetNames().Count(), Is.EqualTo(factoryCount + 1));

                // Update on a factory preset saves a user copy; the Gear file is untouched.
                w = new PresetsWindow(m);
                w.Select(factory);
                w.Comment = "my copy of it";
                w.Add();
                Assert.That(m.IsUserPreset(factory), Is.True);
                Assert.That(m.GetPreset(factory).Comment, Is.EqualTo("my copy of it"));
                Assert.That(Hash(gearFile), Is.EqualTo(hash));

                // Deleting the copy brings the factory preset back.
                w = new PresetsWindow(m);
                w.Select(factory);
                w.Delete();
                Assert.That(m.IsUserPreset(factory), Is.False);
                Assert.That(m.GetPreset(factory).Comment, Is.Not.EqualTo("my copy of it"));
                w.Select("PRS1 Dialog Mine");
                w.Delete();
                Assert.That(m.GetPresetNames().Count(), Is.EqualTo(factoryCount));
                Assert.That(Hash(gearFile), Is.EqualTo(hash));
            }
            finally { Remove(m); }
        }

        // ------------------------------------------------------------------ the agent's tools

        [AvaloniaTest]
        public void TheAgentsPresetToolsUseTheSameFilesAndListFactoryAndUserTogether()
        {
            var m = Add("Neo Filter", "PRS1 Agent");
            try
            {
                string gearFile = GearFileOf(m);
                string hash = Hash(gearFile);
                var before = UxP4aTests.Call("preset_list", UxP4aTests.Args(("machine", "PRS1 Agent"), ("limit", 500)));
                int factoryCount = before["total"]!.GetValue<int>();
                Assert.That(factoryCount, Is.GreaterThan(3));
                string factory = before["presets"]!.AsArray().First()!["name"]!.GetValue<string>();

                var saved = UxP4aTests.Call("preset_save", UxP4aTests.Args(("machine", "PRS1 Agent"), ("name", "PRS1 Agent Mine"), ("comment", "agent")));
                Assert.That(saved["file"]!.GetValue<string>(), Is.EqualTo(PresetStore.UserFile(m.DLL.Name)), "the user's file");
                Assert.That(Hash(gearFile), Is.EqualTo(hash), "Gear's bytes");
                var shadow = UxP4aTests.Call("preset_save", UxP4aTests.Args(("machine", "PRS1 Agent"), ("name", factory), ("comment", "agent copy"), ("overwrite", true)));
                Assert.That(shadow["shadows"]!.GetValue<bool>(), Is.True);

                var list = UxP4aTests.Call("preset_list", UxP4aTests.Args(("machine", "PRS1 Agent"), ("limit", 500)));
                Assert.That(list["total"]!.GetValue<int>(), Is.EqualTo(factoryCount + 1), "factory and user together; a user copy stands in for its factory preset");
                var rows = list["presets"]!.AsArray().Select(p => p!.AsObject()).ToList();
                var mine = rows.Single(p => p["name"]!.GetValue<string>() == "PRS1 Agent Mine");
                Assert.That(mine["user"]!.GetValue<bool>(), Is.True);
                Assert.That(mine["category"]!.GetValue<string>(), Is.EqualTo("User"));
                Assert.That(rows.Single(p => p["name"]!.GetValue<string>() == factory)["user"]?.GetValue<bool>(), Is.True);
                Assert.That(rows.Count(p => p["user"] != null), Is.EqualTo(2));

                // The same set the dialog and the machine see; preset_load reads the user's copy.
                Assert.That(rows.Select(p => p["name"]!.GetValue<string>()), Is.EquivalentTo(m.GetPresetNames()));
                UxP4aTests.Call("preset_load", UxP4aTests.Args(("machine", "PRS1 Agent"), ("preset", factory)));
                Assert.That(Hash(gearFile), Is.EqualTo(hash));
                m.SetPreset("PRS1 Agent Mine", null);
                m.SetPreset(factory, null);
            }
            finally { Remove(m); }
        }

        [AvaloniaTest]
        public void TheBrowsersPresetItemsListFactoryAndUserPresetsTogetherAndMarkTheUsers()
        {
            var m = Add("Neo Filter", "PRS1 Browser");
            try
            {
                var model = new MachineBrowserModel(Core, Studio.Host.Registry);
                var before = model.PresetItems().Where(p => p.Id == "Neo Filter").ToList();
                Assert.That(before, Is.Not.Empty);
                Assert.That(before.All(p => p.Detail == ""), Is.True, "factory presets are not marked");
                string factory = before[0].Preset;

                m.SetPreset("PRS1 Browser Mine", new Preset(m, false, true));
                m.SetPreset(factory, new Preset(m, false, true) { Comment = "copy" });

                var after = model.PresetItems().Where(p => p.Id == "Neo Filter").ToList();
                Assert.That(after.Count, Is.EqualTo(before.Count + 1), "together; the copy stands in for its factory preset");
                Assert.That(after.Single(p => p.Preset == "PRS1 Browser Mine").Detail, Is.EqualTo("User"));
                Assert.That(after.Single(p => p.Preset == factory).Detail, Is.EqualTo("User"));
                Assert.That(after.Count(p => p.Detail == "User"), Is.EqualTo(2));

                m.SetPreset("PRS1 Browser Mine", null);
                m.SetPreset(factory, null);
                Assert.That(model.PresetItems().Count(p => p.Id == "Neo Filter"), Is.EqualTo(before.Count));
            }
            finally { Remove(m); }
        }
    }
}
