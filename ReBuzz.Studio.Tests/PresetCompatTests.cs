// Fork (docs/specs/machines.md §3.2, "User presets"; FX1b): user presets store each global parameter's key, and load by it; a preset
// saved before keys loads by name, with a renamed parameter's former name (Params.def `was`) standing for it. Neo Tune renamed its
// correction strength from "Amount" to "Correction" when FX1's effect strength took the name Amount: a Neo Tune preset saved before
// sets Correction and leaves the new Amount at its default. Every other machine loads a preset without keys by name, as before.

using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;

namespace ReBuzz.Studio.Tests
{
    public class PresetCompatTests
    {
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

        static IParameter Global(IMachine m, string name) =>
            m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.Single(p => p.Name == name);

        static int[] Globals(IMachine m) =>
            m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.Where(p => p.Flags.HasFlag(ParameterFlags.State)).Select(p => p.GetValue(0)).ToArray();

        static void Defaults(IMachine m) => new Preset(m, true, false).Apply(m, false);

        /// <summary>The preset as a build before keys saved it: no ids; Neo Tune as it was before FX1 (its correction strength named "Amount", no effect strength).</summary>
        static Preset OldFormatTune(Preset now)
        {
            var old = Preset.FromXml(now.ToXml());
            old.Parameters.RemoveAll(p => p.Group == 1 && p.Name == "Amount");   // FX1's effect strength didn't exist
            foreach (var p in old.Parameters)
            {
                p.Id = null;
                if (p.Group == 1 && p.Name == "Correction") p.Name = "Amount";
            }
            return old;
        }

        [AvaloniaTest]
        public void ANeoTunePresetSavedBeforeTheRenameSetsCorrectionAndLeavesAmountAtItsDefault()
        {
            var m = Add("Neo Tune", "Compat Tune");
            try
            {
                var correction = Global(m, "Correction");
                var amount = Global(m, "Amount");
                Assert.That(correction.IndexInGroup, Is.EqualTo(6), "the correction strength keeps its index");
                Assert.That(amount.IndexInGroup, Is.EqualTo(25), "FX1's Amount is the last parameter");

                correction.SetValue(0, 420);
                Global(m, "Retune speed").SetValue(0, 300);
                var old = OldFormatTune(new Preset(m, false, true));
                Assert.That(old.Parameters.Any(p => p.Name == "Correction" || p.Id != null), Is.False, "the old format: names only");
                Assert.That(old.Parameters.Single(p => p.Group == 1 && p.Name == "Amount").Value, Is.EqualTo(420));
                var xml = old.ToXml();
                Assert.That(xml, Does.Not.Contain(" Id="), "a preset without ids writes none");

                Defaults(m);
                amount.SetValue(0, 1500);
                correction.SetValue(0, 1000);
                Preset.FromXml(xml).Apply(m, false);

                Assert.That(correction.GetValue(0), Is.EqualTo(420), "the old Amount is the correction strength");
                Assert.That(amount.GetValue(0), Is.EqualTo(amount.DefValue), "the new Amount (the effect strength) takes its default");
                Assert.That(Global(m, "Retune speed").GetValue(0), Is.EqualTo(300), "the other parameters by name, as always");
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void ANeoTunePresetNamingCorrectionAndAmountTakesBothAsTheyAre()
        {
            // A preset without ids saved after the rename (as the factory files are): "Amount" is the effect strength.
            var m = Add("Neo Tune", "Compat Tune Names");
            try
            {
                var correction = Global(m, "Correction");
                var amount = Global(m, "Amount");
                correction.SetValue(0, 600);
                amount.SetValue(0, 1300);
                var named = Preset.FromXml(new Preset(m, false, true).ToXml());
                foreach (var p in named.Parameters) p.Id = null;

                Defaults(m);
                named.Apply(m, false);
                Assert.That(correction.GetValue(0), Is.EqualTo(600));
                Assert.That(amount.GetValue(0), Is.EqualTo(1300));
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void APresetSavedWithIdsSurvivesSaveAndReload()
        {
            var m = Add("Neo Tune", "Compat Tune Ids");
            string dir = Path.Combine(Path.GetTempPath(), $"rebuzz-presets-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            try
            {
                Global(m, "Correction").SetValue(0, 250);
                Global(m, "Amount").SetValue(0, 1700);
                Global(m, "Mix").SetValue(0, 400);
                Global(m, "Transpose").SetValue(0, Global(m, "Transpose").DefValue + 5);
                var want = Globals(m);
                var saved = new Preset(m, false, true);
                Assert.That(saved.Parameters.Where(p => p.Group == 1).All(p => p.Id != null), Is.True, "every global parameter of a Neo machine carries its id");
                Assert.That(saved.Parameters.Single(p => p.Name == "Correction").Id, Is.EqualTo("Amount"));
                Assert.That(saved.Parameters.Single(p => p.Name == "Amount").Id, Is.EqualTo("FxAmount"));

                // Through a user preset file, as the preset bar saves one.
                string file = Path.Combine(dir, "Neo Tune.prs.xml");
                var pd = new PresetDictionary { Filename = file };
                pd["Mine"] = saved;
                pd.Save();
                Assert.That(File.ReadAllText(file), Does.Contain("Id=\"FxAmount\""), "the file stores the ids");
                var back = PresetDictionary.Load(m, file)["Mine"];

                Defaults(m);
                back.Apply(m, false);
                Assert.That(Globals(m), Is.EqualTo(want), "the reloaded preset gives every value back");

                // By id, whatever the names say: a preset whose names were swapped still lands by its ids.
                foreach (var p in back.Parameters.Where(p => p.Group == 1))
                    p.Name = p.Name == "Amount" ? "Correction" : p.Name == "Correction" ? "Amount" : p.Name;
                Defaults(m);
                back.Apply(m, false);
                Assert.That(Globals(m), Is.EqualTo(want), "the ids decide");
            }
            finally
            {
                Remove(m);
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [AvaloniaTest]
        public void AnotherMachinesPresetWithoutIdsLoadsByNameAsBefore()
        {
            var m = Add("Neo Comp", "Compat Comp");
            try
            {
                Global(m, "Ratio").SetValue(0, 800);
                Global(m, "Makeup").SetValue(0, 300);
                Global(m, "Amount").SetValue(0, 1200);
                var want = Globals(m);
                var named = Preset.FromXml(new Preset(m, false, true).ToXml());
                foreach (var p in named.Parameters) { p.Id = null; p.Index = 0; }   // only the names (and groups) say where a value goes
                Defaults(m);
                named.Apply(m, false);
                Assert.That(Globals(m), Is.EqualTo(want));
                foreach (var p in m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters)
                    Assert.That(named.GetValueOrDefault(p, 0), Is.EqualTo(named.Parameters.FirstOrDefault(x => x.Group == 1 && x.Name == p.Name)?.Value ?? p.DefValue), p.Name);
            }
            finally
            {
                Remove(m);
            }
        }
    }
}
