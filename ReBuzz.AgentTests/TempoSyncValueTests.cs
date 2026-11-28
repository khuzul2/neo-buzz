// SYN1 (docs/sdk/tempo-sync.md): the agent says a timing the way a person does: "1/8 dotted", "1/16 quintuplet", "1 bar triplet".

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using V = ReBuzz.Agent.Model.ValueResolver;

namespace ReBuzz.AgentTests
{
    public class TempoSyncValueTests
    {
        static ParamInfo Enum(string name, int index, params string[] names) => new()
        {
            Name = name, Index = index, Min = 0, Max = names.Length - 1,
            Mapping = new ParamMapping { Kind = MappingKind.Enum, Names = names },
            Describe = r => names[r],
        };

        // Neo LFO's shape (Free in the list), Neo Arp's (1/1 for a bar), and Neo Filter's old list with inline T and D.
        static readonly ParamInfo LfoSync = Enum("Sync", 2, "Free", "8 bars", "4 bars", "2 bars", "1 bar", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64");
        static readonly ParamInfo LfoFeel = Enum("Feel", 3, "Straight", "Dotted", "Triplet", "Quintuplet");
        static readonly ParamInfo ArpRate = Enum("Rate", 4, "1/1", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64", "2 bars", "4 bars", "8 bars");
        static readonly ParamInfo ArpFeel = Enum("Feel", 5, "Straight", "Dotted", "Triplet", "Quintuplet");
        static readonly ParamInfo FilterSync = Enum("LFO sync", 0, "Free", "1/32", "1/16T", "1/16", "1/16D", "1/8T", "1/8", "1/8D", "1/4", "1 bar", "2 bars", "1/64");
        static readonly ParamInfo FilterFeel = Enum("LFO feel", 1, "Straight", "Dotted", "Triplet", "Quintuplet");

        static (int division, int? feel) Say(ParamInfo d, ParamInfo f, string text)
        {
            int raw = V.ResolveTiming(d, f, JsonValue.Create(text), 0, out int? feel);
            return (raw, feel);
        }

        [Test]
        public void ADivisionAndAFeelInOnePhrase()
        {
            Assert.That(Say(LfoSync, LfoFeel, "1/8 dotted"), Is.EqualTo((7, (int?)1)));
            Assert.That(Say(LfoSync, LfoFeel, "1/16 quintuplet"), Is.EqualTo((8, (int?)3)));
            Assert.That(Say(LfoSync, LfoFeel, "1 bar triplet"), Is.EqualTo((4, (int?)2)));
            Assert.That(Say(LfoSync, LfoFeel, "dotted 1/8"), Is.EqualTo((7, (int?)1)));
            Assert.That(Say(LfoSync, LfoFeel, "1/8 Dotted"), Is.EqualTo((7, (int?)1)));
            Assert.That(Say(LfoSync, LfoFeel, "1/8t"), Is.EqualTo((7, (int?)2)));
            Assert.That(Say(LfoSync, LfoFeel, "1/8d"), Is.EqualTo((7, (int?)1)));
            Assert.That(Say(LfoSync, LfoFeel, "8 bars straight"), Is.EqualTo((1, (int?)0)));
            Assert.That(Say(LfoSync, LfoFeel, "1/64 quintuplet"), Is.EqualTo((10, (int?)3)));
        }

        [Test]
        public void TheOldWordsStillWork()
        {
            Assert.That(Say(LfoSync, LfoFeel, "1/8"), Is.EqualTo((7, (int?)null)));
            Assert.That(Say(LfoSync, LfoFeel, "1_bar"), Is.EqualTo((4, (int?)null)));
            Assert.That(Say(LfoSync, LfoFeel, "Free"), Is.EqualTo((0, (int?)null)));
            Assert.That(Say(FilterSync, FilterFeel, "1/16T"), Is.EqualTo((2, (int?)null)), "an older list's own entry is taken as it is");
            Assert.That(Say(FilterSync, FilterFeel, "1/8D"), Is.EqualTo((7, (int?)null)));
            Assert.That(V.ResolveTiming(LfoSync, LfoFeel, JsonValue.Create("raw:3"), 0, out int? f), Is.EqualTo(3));
            Assert.That(f, Is.Null);
            Assert.That(V.ResolveTiming(LfoSync, LfoFeel, JsonValue.Create(6), 0, out f), Is.EqualTo(6), "a number is the raw value");
        }

        [Test]
        public void ABarIsFoundWhateverTheListCallsIt()
        {
            Assert.That(Say(ArpRate, ArpFeel, "1 bar"), Is.EqualTo((0, (int?)null)));
            Assert.That(Say(ArpRate, ArpFeel, "1/1 dotted"), Is.EqualTo((0, (int?)1)));
            Assert.That(Say(ArpRate, ArpFeel, "2 bars quintuplet"), Is.EqualTo((7, (int?)3)));
            Assert.That(Say(ArpRate, ArpFeel, "1/16 dotted"), Is.EqualTo((4, (int?)1)));
        }

        [Test]
        public void AFeelNeedsAFeelParameter()
        {
            var ex = Assert.Throws<ToolError>(() => V.ResolveTiming(LfoSync, null, JsonValue.Create("1/8 dotted"), 0, out _));
            Assert.That(ex.Message, Does.Contain("no feel"));
            Assert.Throws<ToolError>(() => V.ResolveTiming(LfoSync, LfoFeel, JsonValue.Create("1/7 dotted"), 0, out _), "no such division: the usual error");
            Assert.Throws<ToolError>(() => V.ResolveTiming(LfoSync, LfoFeel, JsonValue.Create("banana"), 0, out _));
        }

        [Test]
        public void TheManifestsNameEachTimingsFeelSwitchAndKnob()
        {
            string root = AppContext.BaseDirectory;
            while (root != null && !Directory.Exists(Path.Combine(root, "Machines", "NeoLfo"))) root = Path.GetDirectoryName(root);
            Assert.That(root, Is.Not.Null, "the repository");
            var want = new Dictionary<string, int>
            {
                ["NeoDelay"] = 1, ["NeoLfo"] = 1, ["NeoRandom"] = 1, ["NeoArp"] = 1, ["NeoPulse"] = 1, ["NeoNotes"] = 2,
                ["NeoPoly"] = 2, ["NeoWave"] = 2, ["NeoEnsemble"] = 1, ["NeoFilter"] = 1,
            };
            foreach (var (folder, count) in want)
            {
                var manifest = MachineManifest.Load(Directory.GetFiles(Path.Combine(root!, "Machines", folder), "*.machine.json").Single())!;
                Assert.That(manifest.Timings, Has.Count.EqualTo(count), folder);
                var names = manifest.Parameters.Select(p => p.Name).ToHashSet();
                foreach (var t in manifest.Timings)
                {
                    Assert.That(names, Does.Contain(t.Division), folder);
                    Assert.That(names, Does.Contain(t.Feel), folder + ": " + t.Division + " has a Feel");
                    Assert.That(names, Does.Contain(t.Rate), folder + ": " + t.Division + " has a free knob");
                    if (t.Mode.Length > 0) Assert.That(names, Does.Contain(t.Mode), folder);
                    var feel = manifest.Parameters.Single(p => p.Name == t.Feel);
                    Assert.That(feel.Mapping.Names, Is.EqualTo(new[] { "Straight", "Dotted", "Triplet", "Quintuplet" }), folder + ": the Feel's words");
                }
            }
        }
    }
}
